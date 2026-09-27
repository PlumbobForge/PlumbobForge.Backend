using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlumbobForge.Backend.Configuration;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;

namespace PlumbobForge.Backend.Services;

public record MigrationResult(int PackagesMigrated, int SetsMigrated, int ConfigurationsMigrated, int SkippedMissingFiles = 0, int CollectionsMigrated = 0);

public class MigrationService
{
    private readonly AppDbContext _db;
    private readonly PlumbobForgeOptions _options;
    private readonly CacheBuilderService _cacheBuilderService;
    private readonly Sims3CollectionService _collectionService;
    private readonly LocalizationService _localizer;

    public MigrationService(
        AppDbContext db,
        IOptions<PlumbobForgeOptions> options,
        CacheBuilderService cacheBuilderService,
        Sims3CollectionService collectionService,
        LocalizationService localizer)
    {
        _db = db;
        _options = options.Value ?? new PlumbobForgeOptions();
        _cacheBuilderService = cacheBuilderService;
        _collectionService = collectionService;
        _localizer = localizer;
    }

    public async Task<MigrationResult> MigrateCcMagicAsync(
        bool isFullMigration = true,
        string? customPath = null,
        Action<string>? onProgress = null,
        ITaskProgressReporter? progress = null)
    {
        string ccMagicPath = !string.IsNullOrWhiteSpace(customPath)
            ? customPath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Electronic Arts", "CC Magic");

        if (!Directory.Exists(ccMagicPath))
        {
            throw new DirectoryNotFoundException($"Legacy CC Magic directory not found at: {ccMagicPath}");
        }

        string docBase = !string.IsNullOrWhiteSpace(_options.DocumentBaseDir)
            ? _options.DocumentBaseDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PlumbobForge");
        string libraryFolderName = !string.IsNullOrWhiteSpace(_options.ManagedPackageFolderName) ? _options.ManagedPackageFolderName : "Library";
        string libraryPath = Path.Combine(docBase, libraryFolderName);
        Directory.CreateDirectory(libraryPath);

        await _db.Database.EnsureCreatedAsync();

        if (isFullMigration)
        {
            return await MigrateCcMagicFullAsync(ccMagicPath, libraryPath, onProgress, progress);
        }
        else
        {
            return await MigrateCcMagicSimpleAsync(ccMagicPath, libraryPath, onProgress, progress);
        }
    }

    private async Task<MigrationResult> MigrateCcMagicSimpleAsync(
        string ccMagicPath,
        string libraryPath,
        Action<string>? onProgress,
        ITaskProgressReporter? progress)
    {
        var files = Directory.GetFiles(ccMagicPath, "*.*", SearchOption.AllDirectories)
            .Where(s => s.EndsWith(".package", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (files.Count == 0) return new MigrationResult(0, 0, 0, 0, 0);

        string stepFiles = "import_ccmagic_simple";
        progress?.StartStep(stepFiles, _localizer.GetString("progress.importing_ccmagic_packages"), badge: $"0/{files.Count}", progress: 0.0);

        int count = 0;
        int updateInterval = Math.Max(1, files.Count / 100);

        foreach (var file in files)
        {
            count++;
            if (count % updateInterval == 0 || count == files.Count)
            {
                double pct = (double)count / Math.Max(1, files.Count);
                progress?.UpdateStep(stepFiles, progress: pct, badge: $"{count}/{files.Count}");
            }

            var dest = Path.Combine(libraryPath, Path.GetFileName(file));
            if (!string.Equals(file, dest, StringComparison.OrdinalIgnoreCase) && !File.Exists(dest))
            {
                File.Copy(file, dest, overwrite: true);
            }
        }

        progress?.CompleteStep(stepFiles, finalBadge: _localizer.GetString("progress.packages_imported_badge", count));
        return new MigrationResult(count, 0, 0, 0, 0);
    }

    private async Task<MigrationResult> MigrateCcMagicFullAsync(
        string ccMagicPath,
        string libraryPath,
        Action<string>? onProgress,
        ITaskProgressReporter? progress)
    {
        string dbPath = Path.Combine(ccMagicPath, "Settings", "Launcher2.db");
        if (!File.Exists(dbPath))
        {
            return await MigrateCcMagicSimpleAsync(ccMagicPath, libraryPath, onProgress, progress);
        }

        int packagesMigrated = 0;
        int setsMigrated = 0;
        int configsMigrated = 0;

        var legacySetIdToPlumbobSet = new Dictionary<long, SetsEntity>();
        var legacyConfigIdToPlumbobConfig = new Dictionary<long, ConfigEntity>();
        var legacySetIdToCollectionId = new Dictionary<long, ulong>();
        var fileToLegacySetMap = new Dictionary<string, (long? LegacySetId, string? Description, bool Enabled)>(StringComparer.OrdinalIgnoreCase);

        // 1. Read Launcher2.db
        string stepDb = "read_ccmagic_db";
        progress?.StartStep(stepDb, _localizer.GetString("progress.reading_ccmagic_db"), progress: 0.0);

        using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
        {
            await conn.OpenAsync();

            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            string? setsTable = FindTable(tables, "SetsEntities", "SetsEntity", "Sets", "Set");
            string? configsTable = FindTable(tables, "ConfigEntities", "ConfigEntity", "Configurations", "Configs", "Configuration");
            string? configSetsTable = FindTable(tables, "ConfigSetsEntities", "ConfigSetsEntity", "ConfigSets", "ConfigurationSets", "Config_Sets", "Configurations_Sets");
            string? packagesTable = FindTable(tables, "MetaEntities", "MetaEntity", "Packages", "Package", "Files", "File", "Items", "Contents");

            var existingSets = await _db.SetsEntities.ToListAsync();
            var setsByName = new Dictionary<string, SetsEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in existingSets)
            {
                setsByName[s.Name] = s;
            }
            var legacyParentMap = new Dictionary<long, long>();

            // 1a. Import Sets
            if (!string.IsNullOrEmpty(setsTable))
            {
                var setColumns = await GetTableColumnsAsync(conn, setsTable);
                string colId = FindColumn(setColumns, "Id", "SetId", "RowId") ?? setColumns.FirstOrDefault() ?? "Id";
                string colName = FindColumn(setColumns, "Name", "SetName", "Title") ?? "Name";
                string? colDesc = FindColumn(setColumns, "Description", "Desc", "Details");
                string? colParent = FindColumn(setColumns, "ParentSetsEntityId", "ParentId", "ParentSetId", "Parent");
                string? colFolder = FindColumn(setColumns, "FolderName", "Folder");
                string? colIsDef = FindColumn(setColumns, "Default", "IsDefault");
                string? colCollection = FindColumn(setColumns, "Collection", "CollectionId");

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT * FROM \"{setsTable}\"";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    long legacyId = reader.GetInt64(reader.GetOrdinal(colId));
                    string name = reader.GetString(reader.GetOrdinal(colName));

                    if (string.Equals(name, "Legacy", StringComparison.OrdinalIgnoreCase)) continue;

                    string? desc = colDesc != null && !reader.IsDBNull(reader.GetOrdinal(colDesc)) ? reader.GetString(reader.GetOrdinal(colDesc)) : null;
                    string? folder = colFolder != null && !reader.IsDBNull(reader.GetOrdinal(colFolder)) ? reader.GetString(reader.GetOrdinal(colFolder)) : name;
                    bool isDef = colIsDef != null && !reader.IsDBNull(reader.GetOrdinal(colIsDef)) && Convert.ToBoolean(reader.GetValue(reader.GetOrdinal(colIsDef)));

                    if (colParent != null && !reader.IsDBNull(reader.GetOrdinal(colParent)))
                    {
                        long pId = reader.GetInt64(reader.GetOrdinal(colParent));
                        if (pId > 0 && pId != legacyId) legacyParentMap[legacyId] = pId;
                    }

                    if (colCollection != null && !reader.IsDBNull(reader.GetOrdinal(colCollection)))
                    {
                        string collVal = reader.GetString(reader.GetOrdinal(colCollection));
                        if (ulong.TryParse(collVal, out ulong cId) && cId != 0)
                        {
                            legacySetIdToCollectionId[legacyId] = cId;
                        }
                    }

                    if (!setsByName.TryGetValue(name, out var setEntity))
                    {
                        setEntity = new SetsEntity
                        {
                            Name = name,
                            FolderName = !string.IsNullOrWhiteSpace(folder) ? folder : name,
                            LongName = name,
                            Description = desc,
                            Dirty = true,
                            IsExpanded = true,
                            IsDefault = isDef || string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase)
                        };
                        _db.SetsEntities.Add(setEntity);
                        setsByName[name] = setEntity;
                        setsMigrated++;
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(desc) && string.IsNullOrWhiteSpace(setEntity.Description))
                            setEntity.Description = desc;
                        setEntity.Dirty = true;
                    }
                    legacySetIdToPlumbobSet[legacyId] = setEntity;
                }
                await _db.SaveChangesAsync();

                foreach (var (childLegId, parentLegId) in legacyParentMap)
                {
                    if (legacySetIdToPlumbobSet.TryGetValue(childLegId, out var childSet) &&
                        legacySetIdToPlumbobSet.TryGetValue(parentLegId, out var parentSet))
                    {
                        childSet.ParentSetsEntityId = parentSet.Id;
                    }
                }
                await _db.SaveChangesAsync();
            }

            // 1b. Import Configurations
            if (!string.IsNullOrEmpty(configsTable))
            {
                var configColumns = await GetTableColumnsAsync(conn, configsTable);
                string colId = FindColumn(configColumns, "Id", "ConfigId", "RowId") ?? configColumns.FirstOrDefault() ?? "Id";
                string colName = FindColumn(configColumns, "Name", "ConfigName", "Title") ?? "Name";
                string? colDesc = FindColumn(configColumns, "Description", "Desc");
                string? colActive = FindColumn(configColumns, "Active", "IsActive");
                string? colDefault = FindColumn(configColumns, "Default", "IsDefault");

                var existingConfigs = await _db.ConfigEntities.Include(c => c.ConfigSetsEntities).ToListAsync();
                var configsByName = new Dictionary<string, ConfigEntity>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in existingConfigs) configsByName[c.Name] = c;

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT * FROM \"{configsTable}\"";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    long legacyId = reader.GetInt64(reader.GetOrdinal(colId));
                    string name = reader.GetString(reader.GetOrdinal(colName));
                    string? desc = colDesc != null && !reader.IsDBNull(reader.GetOrdinal(colDesc)) ? reader.GetString(reader.GetOrdinal(colDesc)) : null;
                    bool active = colActive != null && !reader.IsDBNull(reader.GetOrdinal(colActive)) && Convert.ToBoolean(reader.GetValue(reader.GetOrdinal(colActive)));
                    bool isDef = colDefault != null && !reader.IsDBNull(reader.GetOrdinal(colDefault)) && Convert.ToBoolean(reader.GetValue(reader.GetOrdinal(colDefault)));

                    if (!configsByName.TryGetValue(name, out var configEntity))
                    {
                        configEntity = new ConfigEntity
                        {
                            Name = name,
                            Description = desc ?? "Migrated from CC Magic",
                            Active = active,
                            Default = isDef
                        };
                        _db.ConfigEntities.Add(configEntity);
                        configsByName[name] = configEntity;
                        configsMigrated++;
                    }
                    else if (!string.IsNullOrWhiteSpace(desc) && string.IsNullOrWhiteSpace(configEntity.Description))
                        configEntity.Description = desc;

                    legacyConfigIdToPlumbobConfig[legacyId] = configEntity;
                }
                await _db.SaveChangesAsync();
            }

            // 1c. Import ConfigSets Associations
            if (!string.IsNullOrEmpty(configSetsTable))
            {
                var csColumns = await GetTableColumnsAsync(conn, configSetsTable);
                string? colConfigId = FindColumn(csColumns, "ConfigEntity_Id", "ConfigEntityId", "ConfigId", "ConfigurationId");
                string? colSetId = FindColumn(csColumns, "SetsEntity_Id", "SetsEntityId", "SetId");

                if (colConfigId != null && colSetId != null)
                {
                    var existingConfigSets = await _db.ConfigSetsEntities.ToListAsync();
                    var linkedPairs = new HashSet<(long ConfigId, long SetId)>(
                        existingConfigSets.Select(cs => (cs.ConfigEntityId, cs.SetsEntityId)));

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT \"{colConfigId}\", \"{colSetId}\" FROM \"{configSetsTable}\"";
                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                        {
                            long legCfgId = reader.GetInt64(0);
                            long legSetId = reader.GetInt64(1);

                            if (legacyConfigIdToPlumbobConfig.TryGetValue(legCfgId, out var pbConfig) &&
                                legacySetIdToPlumbobSet.TryGetValue(legSetId, out var pbSet))
                            {
                                if (linkedPairs.Add((pbConfig.Id, pbSet.Id)))
                                {
                                    _db.ConfigSetsEntities.Add(new ConfigSetsEntity
                                    {
                                        ConfigEntityId = pbConfig.Id,
                                        SetsEntityId = pbSet.Id
                                    });
                                }
                            }
                        }
                    }
                    await _db.SaveChangesAsync();
                }
            }

            // 1d. Map Package Records
            if (!string.IsNullOrEmpty(packagesTable))
            {
                var pkgColumns = await GetTableColumnsAsync(conn, packagesTable);
                string? colFileName = FindColumn(pkgColumns, "FileName", "Filename", "Name", "Path");
                string? colSetId = FindColumn(pkgColumns, "SetsEntity_Id", "SetsEntityId", "SetId");
                string? colDesc = FindColumn(pkgColumns, "Description", "Desc", "Notes", "Note", "Comment", "Comments");
                string? colEnabled = FindColumn(pkgColumns, "Enabled", "Active");

                if (colFileName != null)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT * FROM \"{packagesTable}\"";
                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        string rawFile = reader.GetString(reader.GetOrdinal(colFileName));
                        string fileName = Path.GetFileName(rawFile);
                        string fileNameNoExt = Path.GetFileNameWithoutExtension(rawFile);
                        long? legSetId = colSetId != null && !reader.IsDBNull(reader.GetOrdinal(colSetId)) ? reader.GetInt64(reader.GetOrdinal(colSetId)) : null;
                        string? desc = colDesc != null && !reader.IsDBNull(reader.GetOrdinal(colDesc)) ? reader.GetString(reader.GetOrdinal(colDesc)) : null;
                        if (!string.IsNullOrWhiteSpace(desc)) desc = desc.Trim();
                        bool enabled = colEnabled == null || reader.IsDBNull(reader.GetOrdinal(colEnabled)) || Convert.ToBoolean(reader.GetValue(reader.GetOrdinal(colEnabled)));

                        if (!string.IsNullOrWhiteSpace(fileName))
                        {
                            fileToLegacySetMap[fileName] = (legSetId, desc, enabled);
                            if (!string.IsNullOrWhiteSpace(fileNameNoExt) && !fileToLegacySetMap.ContainsKey(fileNameNoExt))
                                fileToLegacySetMap[fileNameNoExt] = (legSetId, desc, enabled);
                        }
                    }
                }
            }
        }

        progress?.CompleteStep(stepDb, finalBadge: _localizer.GetString("progress.sets_configs_migrated_badge", setsMigrated, configsMigrated));

        var files = Directory.GetFiles(ccMagicPath, "*.*", SearchOption.AllDirectories)
            .Where(s => s.EndsWith(".package", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var physicalFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files) physicalFileNames.Add(Path.GetFileName(f));

        string stepFiles = "import_ccmagic_files";
        progress?.StartStep(stepFiles, _localizer.GetString("progress.importing_ccmagic_sets"), badge: $"0/{files.Count}", progress: 0.0);

        var defaultSet = await _db.SetsEntities.FirstOrDefaultAsync(s => s.Name == "Default") ?? await _db.SetsEntities.FirstOrDefaultAsync();
        var allExistingMetas = await _db.MetaEntities.ToListAsync();
        var metaMap = new Dictionary<string, MetaEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in allExistingMetas) metaMap[m.FileName] = m;

        int current = 0;
        int updateInterval = Math.Max(1, files.Count / 100);

        foreach (var file in files)
        {
            current++;
            if (current % updateInterval == 0 || current == files.Count)
            {
                double pct = (double)current / Math.Max(1, files.Count);
                progress?.UpdateStep(stepFiles, progress: pct, badge: $"{current}/{files.Count}");
            }

            string fileName = Path.GetFileName(file);
            string fileNameNoExt = Path.GetFileNameWithoutExtension(fileName);
            var dest = Path.Combine(libraryPath, fileName);
            if (!string.Equals(file, dest, StringComparison.OrdinalIgnoreCase) && !File.Exists(dest))
            {
                File.Copy(file, dest, overwrite: true);
                packagesMigrated++;
            }

            long targetSetId = defaultSet?.Id ?? 1;
            string? itemDesc = null;
            bool isEnabled = true;

            if (fileToLegacySetMap.TryGetValue(fileName, out var mapInfo) || fileToLegacySetMap.TryGetValue(fileNameNoExt, out mapInfo))
            {
                if (mapInfo.LegacySetId.HasValue && legacySetIdToPlumbobSet.TryGetValue(mapInfo.LegacySetId.Value, out var matchedSet))
                {
                    targetSetId = matchedSet.Id;
                    matchedSet.Dirty = true;
                }
                itemDesc = mapInfo.Description;
                isEnabled = mapInfo.Enabled;
            }

            if (!metaMap.TryGetValue(fileName, out var meta))
            {
                meta = new MetaEntity
                {
                    FileName = fileName,
                    CompleteFileName = dest,
                    FileType = Path.GetExtension(fileName),
                    Filehash = string.Empty,
                    PackageType = "Unknown",
                    SetsEntityId = targetSetId,
                    Description = itemDesc,
                    Enabled = isEnabled
                };
                _db.MetaEntities.Add(meta);
                metaMap[fileName] = meta;
            }
            else
            {
                meta.SetsEntityId = targetSetId;
                meta.Enabled = isEnabled;
                if (!string.IsNullOrWhiteSpace(itemDesc)) meta.Description = itemDesc;
            }
        }

        foreach (var (key, info) in fileToLegacySetMap)
        {
            if (string.IsNullOrWhiteSpace(info.Description)) continue;
            if (metaMap.TryGetValue(key, out var existingMeta) ||
                metaMap.TryGetValue($"{key}.package", out existingMeta) ||
                metaMap.TryGetValue($"{key}.sims3pack", out existingMeta))
            {
                existingMeta.Description = info.Description;
            }
        }

        await _db.SaveChangesAsync();
        progress?.CompleteStep(stepFiles, finalBadge: _localizer.GetString("progress.items_imported_badge", packagesMigrated));

        // 1d. Migrate Collections linked in CC Magic
        int collectionsMigrated = 0;
        if (legacySetIdToCollectionId.Count > 0)
        {
            string stepCollections = "import_ccmagic_collections";
            progress?.StartStep(stepCollections, _localizer.GetString("progress.migrating_collections_ccmagic"), badge: _localizer.GetString("progress.badge_initializing"), progress: 0.0);

            try
            {
                var existingUserCollections = await _collectionService.LoadUserCollectionsAsync();
                var existingCollMap = existingUserCollections.ToDictionary(c => c.Id, c => c);

                var collGroups = legacySetIdToCollectionId.GroupBy(kvp => kvp.Value).ToList();
                int currentColl = 0;

                foreach (var group in collGroups)
                {
                    currentColl++;
                    ulong collectionId = group.Key;
                    var legSetIds = group.Select(g => g.Key).ToList();
                    var pbSetIds = new List<long>();
                    foreach (var legSetId in legSetIds)
                    {
                        if (legacySetIdToPlumbobSet.TryGetValue(legSetId, out var pbSet))
                        {
                            pbSetIds.Add(pbSet.Id);
                        }
                    }

                    if (pbSetIds.Count == 0) continue;

                    string collName = "Collection";
                    TGI_Key iconKey = new TGI_Key(0x2F7D0004, 0, 0);

                    if (existingCollMap.TryGetValue(collectionId, out var existingColl))
                    {
                        collName = existingColl.Name;
                        iconKey = existingColl.IconKey;
                    }
                    else
                    {
                        var firstSet = legacySetIdToPlumbobSet.GetValueOrDefault(legSetIds.First());
                        if (firstSet != null) collName = firstSet.Name;
                    }

                    var saveRes = await _collectionService.SaveCollectionAsync(collectionId, collName, iconKey, pbSetIds);
                    if (saveRes.Success)
                    {
                        collectionsMigrated++;
                    }

                    double pct = (double)currentColl / Math.Max(1, collGroups.Count);
                    progress?.UpdateStep(stepCollections, progress: pct, badge: $"{collectionsMigrated}/{collGroups.Count}");
                }

                progress?.CompleteStep(stepCollections, finalBadge: _localizer.GetString("progress.collections_migrated_badge", collectionsMigrated));
            }
            catch (Exception ex)
            {
                progress?.WarningStep("collections_error", "Failed to migrate some collections", ex.Message, new List<string> { ex.Message });
            }
        }

        var missingFiles = fileToLegacySetMap.Keys.Where(k => !physicalFileNames.Contains(k)).ToList();
        if (missingFiles.Count > 0)
        {
            var previewList = missingFiles.Take(50).Select(f => $"{f} (not found in CC Magic folder)").ToList();
            progress?.WarningStep("skipped_missing_files", "Items found in CC Magic database but missing on disk", $"{missingFiles.Count} files missing", previewList);
        }

        return new MigrationResult(packagesMigrated, setsMigrated, configsMigrated, missingFiles.Count, collectionsMigrated);
    }

    private static string? FindTable(HashSet<string> tables, params string[] candidates)
    {
        foreach (var c in candidates)
        {
            var match = tables.FirstOrDefault(t => t.Equals(c, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return null;
    }

    private static async Task<List<string>> GetTableColumnsAsync(SqliteConnection conn, string tableName)
    {
        var columns = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{tableName}\")";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        return columns;
    }

    private static string? FindColumn(List<string> columns, params string[] candidates)
    {
        foreach (var c in candidates)
        {
            var match = columns.FirstOrDefault(col => col.Equals(c, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return null;
    }

    private async Task<MigrationResult> MigrateS3moSimpleAsync(
        string s3moPath,
        string libraryPath,
        Action<string>? onProgress,
        ITaskProgressReporter? progress)
    {
        string modsDir = Path.Combine(s3moPath, "Mods");
        string searchDir = Directory.Exists(modsDir) ? modsDir : s3moPath;

        var files = Directory.GetFiles(searchDir, "*.*", SearchOption.AllDirectories)
            .Where(s => s.EndsWith(".package", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (files.Count == 0) return new MigrationResult(0, 0, 0, 0, 0);

        string stepFiles = "import_s3mo_simple";
        progress?.StartStep(stepFiles, _localizer.GetString("progress.importing_s3mo_packages"), badge: $"0/{files.Count}", progress: 0.0);

        int count = 0;
        int updateInterval = Math.Max(1, files.Count / 100);

        foreach (var file in files)
        {
            count++;
            if (count % updateInterval == 0 || count == files.Count)
            {
                double pct = (double)count / Math.Max(1, files.Count);
                progress?.UpdateStep(stepFiles, progress: pct, badge: $"{count}/{files.Count}");
            }

            var dest = Path.Combine(libraryPath, Path.GetFileName(file));
            if (!string.Equals(file, dest, StringComparison.OrdinalIgnoreCase) && !File.Exists(dest))
            {
                File.Copy(file, dest, overwrite: true);
            }
        }

        progress?.CompleteStep(stepFiles, finalBadge: _localizer.GetString("progress.packages_imported_badge", count));
        return new MigrationResult(count, 0, 0, 0, 0);
    }

    public async Task<MigrationResult> MigrateS3moAsync(
        string s3moPath,
        bool isFullMigration = true,
        Action<string>? onProgress = null,
        ITaskProgressReporter? progress = null)
    {
        if (string.IsNullOrWhiteSpace(s3moPath) || !Directory.Exists(s3moPath))
        {
            throw new DirectoryNotFoundException($"Sims 3 Mod Organizer folder not found at: {s3moPath}");
        }

        string modsDir = Path.Combine(s3moPath, "Mods");
        string profilesDir = Path.Combine(s3moPath, "Profiles");
        string iniPath = Path.Combine(s3moPath, "s3mo.ini");

        if (!Directory.Exists(modsDir) && !Directory.Exists(profilesDir))
        {
            throw new InvalidOperationException("The selected folder does not contain 'Mods' or 'Profiles' subdirectories.");
        }

        string docBase = !string.IsNullOrWhiteSpace(_options.DocumentBaseDir)
            ? _options.DocumentBaseDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PlumbobForge");
        string libraryFolderName = !string.IsNullOrWhiteSpace(_options.ManagedPackageFolderName) ? _options.ManagedPackageFolderName : "Library";
        string libraryPath = Path.Combine(docBase, libraryFolderName);
        Directory.CreateDirectory(libraryPath);

        await _db.Database.EnsureCreatedAsync();

        if (!isFullMigration)
        {
            return await MigrateS3moSimpleAsync(s3moPath, libraryPath, onProgress, progress);
        }

        string? activeProfile = null;
        if (File.Exists(iniPath))
        {
            var iniLines = await File.ReadAllLinesAsync(iniPath);
            foreach (var line in iniLines)
            {
                if (line.StartsWith("Profile=", StringComparison.OrdinalIgnoreCase))
                {
                    activeProfile = line.Substring("Profile=".Length).Trim();
                    break;
                }
            }
        }

        int totalPackagesMigrated = 0;
        int totalSetsMigrated = 0;
        int totalConfigsMigrated = 0;

        var existingSets = await _db.SetsEntities.ToListAsync();
        var setsByName = new Dictionary<string, SetsEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in existingSets) setsByName[s.Name] = s;

        var existingMetas = await _db.MetaEntities.ToListAsync();
        var metasByFileName = new Dictionary<string, MetaEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in existingMetas) metasByFileName[m.FileName] = m;

        if (Directory.Exists(modsDir))
        {
            var modDirs = Directory.GetDirectories(modsDir);
            string stepMods = "migrate_s3mo_mods";
            progress?.StartStep(stepMods, _localizer.GetString("progress.importing_s3mo_mods"), badge: $"0/{modDirs.Length}", progress: 0.0);

            int modIndex = 0;
            foreach (var modPath in modDirs)
            {
                modIndex++;
                string modName = Path.GetFileName(modPath);
                progress?.UpdateStep(stepMods, progress: (double)modIndex / Math.Max(1, modDirs.Length), badge: $"{modIndex}/{modDirs.Length}");

                if (!setsByName.TryGetValue(modName, out var setEntity))
                {
                    setEntity = new SetsEntity
                    {
                        Name = modName,
                        FolderName = modName,
                        LongName = modName,
                        Dirty = true,
                        IsExpanded = true,
                        IsDefault = false
                    };
                    _db.SetsEntities.Add(setEntity);
                    setsByName[modName] = setEntity;
                    totalSetsMigrated++;
                }

                var packageFiles = Directory.GetFiles(modPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".package", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var pkgFile in packageFiles)
                {
                    string fileName = Path.GetFileName(pkgFile);
                    string destPath = Path.Combine(libraryPath, fileName);
                    if (!string.Equals(pkgFile, destPath, StringComparison.OrdinalIgnoreCase) && !File.Exists(destPath))
                    {
                        File.Copy(pkgFile, destPath, overwrite: true);
                        totalPackagesMigrated++;
                    }

                    if (!metasByFileName.TryGetValue(fileName, out var meta))
                    {
                        meta = new MetaEntity
                        {
                            FileName = fileName,
                            CompleteFileName = destPath,
                            FileType = Path.GetExtension(fileName),
                            Filehash = string.Empty,
                            PackageType = "Unknown",
                            SetsEntityId = setEntity.Id,
                            Enabled = true
                        };
                        _db.MetaEntities.Add(meta);
                        metasByFileName[fileName] = meta;
                    }
                    else
                    {
                        meta.CompleteFileName = destPath;
                        meta.SetsEntityId = setEntity.Id;
                    }
                }
            }

            await _db.SaveChangesAsync();
            progress?.CompleteStep(stepMods, finalBadge: _localizer.GetString("progress.total_sets_packages_badge", totalSetsMigrated, totalPackagesMigrated));
        }

        if (Directory.Exists(profilesDir))
        {
            var profileDirs = Directory.GetDirectories(profilesDir);
            string stepProfiles = "migrate_s3mo_profiles";
            progress?.StartStep(stepProfiles, _localizer.GetString("progress.importing_s3mo_profiles"), badge: $"0/{profileDirs.Length}", progress: 0.0);

            var existingConfigs = await _db.ConfigEntities.Include(c => c.ConfigSetsEntities).ToListAsync();
            var configsByName = new Dictionary<string, ConfigEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in existingConfigs) configsByName[c.Name] = c;

            var existingConfigSets = await _db.ConfigSetsEntities.ToListAsync();
            var linkedPairs = new HashSet<(long ConfigId, long SetId)>(
                existingConfigSets.Select(cs => (cs.ConfigEntityId, cs.SetsEntityId)));

            int profileIndex = 0;
            foreach (var profPath in profileDirs)
            {
                profileIndex++;
                string profileName = Path.GetFileName(profPath);
                progress?.UpdateStep(stepProfiles, progress: (double)profileIndex / Math.Max(1, profileDirs.Length), badge: $"{profileIndex}/{profileDirs.Length}");

                if (!configsByName.TryGetValue(profileName, out var configEntity))
                {
                    configEntity = new ConfigEntity
                    {
                        Name = profileName,
                        Description = "Migrated from Sims 3 Mod Organizer",
                        Active = string.Equals(profileName, activeProfile, StringComparison.OrdinalIgnoreCase),
                        Default = string.Equals(profileName, "Default", StringComparison.OrdinalIgnoreCase)
                    };
                    _db.ConfigEntities.Add(configEntity);
                    configsByName[profileName] = configEntity;
                    totalConfigsMigrated++;
                }

                await _db.SaveChangesAsync();

                string modlistPath = Path.Combine(profPath, "modlist.txt");
                if (File.Exists(modlistPath))
                {
                    var lines = await File.ReadAllLinesAsync(modlistPath);
                    foreach (var rawLine in lines)
                    {
                        var line = rawLine.Trim();
                        if (line.StartsWith("+"))
                        {
                            string targetModName = line.Substring(1).Trim();
                            if (setsByName.TryGetValue(targetModName, out var targetSet))
                            {
                                if (linkedPairs.Add((configEntity.Id, targetSet.Id)))
                                {
                                    _db.ConfigSetsEntities.Add(new ConfigSetsEntity
                                    {
                                        ConfigEntityId = configEntity.Id,
                                        SetsEntityId = targetSet.Id
                                    });
                                }
                            }
                        }
                    }
                }
            }

            await _db.SaveChangesAsync();
            progress?.CompleteStep(stepProfiles, finalBadge: _localizer.GetString("progress.total_configs_migrated_badge", totalConfigsMigrated));
        }

        await _db.SaveChangesAsync();
        return new MigrationResult(totalPackagesMigrated, totalSetsMigrated, totalConfigsMigrated, 0);
    }
}
