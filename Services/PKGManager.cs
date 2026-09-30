using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using PlumbobForge.Backend.Database;
using PlumbobForge.Backend.Configuration;
using S3ForgeTools.GameFiles.Package;
using S3ForgeTools.GameFiles.TS3Pack;
using S3ForgeTools.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace PlumbobForge.Backend.Services;

public class PKGManager
{
    private readonly AppDbContext _db;
    private readonly IOptionsMonitor<PlumbobForgeOptions> _optionsMonitor;
    private PlumbobForgeOptions _options => _optionsMonitor.CurrentValue;
    private readonly LocalizationService _localizer;
    private readonly PackageTypeService _packageTypeService;
    private readonly ArchiveService _archiveService;
    private readonly ThumbnailService _thumbnailService;
    private readonly CacheBuilderService _cacheBuilderService;
    private readonly MigrationService _migrationService;

    public PKGManager(
        AppDbContext db,
        IOptionsMonitor<PlumbobForgeOptions> options,
        LocalizationService localizer,
        PackageTypeService packageTypeService,
        ArchiveService archiveService,
        ThumbnailService thumbnailService,
        CacheBuilderService cacheBuilderService,
        MigrationService migrationService)
    {
        _db = db;
        _optionsMonitor = options;
        _localizer = localizer;
        _packageTypeService = packageTypeService;
        _archiveService = archiveService;
        _thumbnailService = thumbnailService;
        _cacheBuilderService = cacheBuilderService;
        _migrationService = migrationService;
    }

    public async Task ScanLibraryDiskAsync(Action<string>? onProgress = null)
    {
        try
        {
            onProgress?.Invoke(_localizer.GetString("checking_orphan_packages"));
            await CheckOrphanPackagesAsync();
            await CheckSetParentingAsync();
            await CheckConfigurationsAsync();
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"Error scanning library disk: {ex.Message}");
        }
    }

    public async Task RunAsync(bool isRefresh, Action<string>? onProgress = null, ITaskProgressReporter? progress = null, bool forceRebuild = false)
    {
        if (!isRefresh)
        {
            CreateFolders();
        }
        else
        {
            try
            {
                var skippedFiles = new List<(string FileName, string Reason)>();

                // 1. Initial Checks Step (aggregated)
                progress?.StartStep("checks", _localizer.GetString("progress.doing_checks"), badge: null, progress: 0.0);
                onProgress?.Invoke(_localizer.GetString("checking_orphan_packages"));
                await CheckOrphanPackagesAsync();
                progress?.UpdateStep("checks", progress: 0.33);

                onProgress?.Invoke(_localizer.GetString("validating_set_hierarchy"));
                await CheckSetParentingAsync();
                progress?.UpdateStep("checks", progress: 0.66);

                onProgress?.Invoke(_localizer.GetString("initializing_config_profile"));
                await CheckConfigurationsAsync();
                progress?.UpdateStep("checks", progress: 0.90);

                onProgress?.Invoke(_localizer.GetString("scanning_cache_requirements"));
                progress?.UpdateStep("checks", progress: 1.0);
                progress?.CompleteStep("checks");

                // 2. Rebuild Cache for Sets
                bool isStatic = string.Equals(_options.CacheMethod, "Static", StringComparison.OrdinalIgnoreCase);
                await RebuildCacheAsync(forceRebuild || isStatic, onProgress, skippedFiles, progress);

                // 3. Skipped Files Warning Step (if any)
                if (skippedFiles.Count > 0)
                {
                    var details = skippedFiles.Select(f => $"{f.FileName}: {f.Reason}").ToList();
                    progress?.WarningStep("skipped", _localizer.GetString("progress.skipped_packages_title"), _localizer.GetString("progress.skipped_badge", skippedFiles.Count), details);
                    onProgress?.Invoke(_localizer.GetString("rebuild_partially_completed", skippedFiles.Count));
                    foreach (var (fileName, reason) in skippedFiles)
                    {
                        onProgress?.Invoke($"  - {fileName}: {reason}");
                    }
                }

                // 4. Syncing Step
                progress?.StartStep("sync", _localizer.GetString("progress.syncing_cache_to_sims3"), badge: null, progress: 0.0);
                await SyncToSims3Async(onProgress, forceRebuildStatic: forceRebuild);
                progress?.CompleteStep("sync");

                onProgress?.Invoke(_localizer.GetString("scan_complete"));
            }
            catch (Exception ex)
            {
                progress?.ErrorStep("error", _localizer.GetString("progress.cache_rebuild_error"), ex.Message);
                onProgress?.Invoke(_localizer.GetString("rebuild_error", ex.Message));
                throw;
            }
        }
    }

    public static bool IsArchiveExtension(string filePath) => ArchiveService.IsArchiveExtension(filePath);

    public static List<string> GetArchivePackageFileNames(string archivePath) => ArchiveService.GetArchivePackageFileNames(archivePath);

    public static List<string> GetArchivePackageFileNames(Stream stream) => ArchiveService.GetArchivePackageFileNames(stream);

    public List<string> ExtractPackagesFromArchive(string archivePath, string destinationFolder, string duplicateAction = "rename", Action<string>? onProgress = null)
        => _archiveService.ExtractPackagesFromArchive(archivePath, destinationFolder, duplicateAction, onProgress, MarkExistingFileDirty);

    public List<string> GetDuplicateFiles(IEnumerable<string> fileNames) => _archiveService.GetDuplicateFiles(fileNames);

    public async Task<List<MetaEntity>> ImportFilesAsync(string[] files, Action<string>? onProgress = null, string duplicateAction = "rename", long? targetSetId = null)
    {
        CreateFolders();
        var importedPaths = new List<string>();

        foreach (var filePath in files)
        {
            if (!File.Exists(filePath)) continue;

            string fileName = Path.GetFileName(filePath);
            if (IsArchiveExtension(fileName))
            {
                var extractedPaths = ExtractPackagesFromArchive(filePath, _options.ManagedPackageFolderPath, duplicateAction, onProgress);
                importedPaths.AddRange(extractedPaths);
            }
            else
            {
                string ext = Path.GetExtension(fileName).ToLowerInvariant();
                if (ext != ".package" && ext != ".sims3pack" && ext != ".world" && ext != ".sim") continue;

                string destPath = Path.Combine(_options.ManagedPackageFolderPath, fileName);

                if (File.Exists(destPath))
                {
                    if (string.Equals(duplicateAction, "skip", StringComparison.OrdinalIgnoreCase))
                    {
                        onProgress?.Invoke($"Skipped duplicate file: {fileName}");
                        continue;
                    }
                    else if (string.Equals(duplicateAction, "replace", StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(destPath); } catch { }
                        MarkExistingFileDirty(fileName);
                    }
                    else
                    {
                        destPath = Path.Combine(_options.ManagedPackageFolderPath, Guid.NewGuid().ToString().Substring(0, 8) + "_" + fileName);
                    }
                }

                File.Copy(filePath, destPath, overwrite: true);
                importedPaths.Add(destPath);
                onProgress?.Invoke($"Imported file: {fileName}");
            }
        }

        if (importedPaths.Count > 0)
        {
            onProgress?.Invoke("Registering imported files...");
            var registered = await RegisterSpecificFilesAsync(importedPaths, targetSetId);
            onProgress?.Invoke("Import finished.");
            return registered;
        }

        return new List<MetaEntity>();
    }

    public async Task<List<MetaEntity>> RegisterSpecificFilesAsync(IEnumerable<string> filePaths, long? targetSetId = null)
    {
        var result = new List<MetaEntity>();
        var validFilePaths = filePaths.Where(File.Exists).ToList();
        if (validFilePaths.Count == 0) return result;

        var setEntities = await _db.SetsEntities.ToListAsync();
        var setEntitiesById = setEntities.ToDictionary(s => s.Id);
        SetsEntity assignSet;
        if (targetSetId.HasValue && setEntitiesById.TryGetValue(targetSetId.Value, out var requestedSet))
        {
            assignSet = requestedSet;
        }
        else
        {
            assignSet = setEntities.FirstOrDefault(s => s.Name == "Default") ?? setEntities.First();
        }

        var fileNames = validFilePaths.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existingMetas = await _db.MetaEntities
            .Include(m => m.SetsEntity)
            .Where(m => fileNames.Contains(m.FileName))
            .ToDictionaryAsync(m => m.FileName, StringComparer.OrdinalIgnoreCase);

        var tombstones = await _db.Tombstones
            .Where(t => fileNames.Contains(t.FileName))
            .ToDictionaryAsync(t => t.FileName, StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in validFilePaths)
        {
            string fileName = Path.GetFileName(filePath);
            existingMetas.TryGetValue(fileName, out var existingMeta);
            var fileInfo = new FileInfo(filePath);
            double currentSizeKb = fileInfo.Length / 1024.0;
            bool isSims3Pack = Path.GetExtension(fileName).Equals(".sims3pack", StringComparison.OrdinalIgnoreCase);

            if (existingMeta == null)
            {
                if (!isSims3Pack && _options.CompressionLevel > 0)
                {
                    try
                    {
                        if (DBPFPackage.OptimizePackage(filePath, _options.CompressionLevel))
                        {
                            fileInfo.Refresh();
                            currentSizeKb = fileInfo.Length / 1024.0;
                        }
                    }
                    catch { }
                }

                (string PackageType, string CASCategories, string CASAge, string CASGender, string CASOutfitCategory) typeInfo = ("Other", "", "", "", "");
                try
                {
                    typeInfo = DetectPackageType(filePath, isSims3Pack);
                }
                catch { }

                tombstones.TryGetValue(fileName, out var tombstone);
                var targetSet = (tombstone != null && tombstone.SetsEntityId.HasValue && setEntitiesById.TryGetValue(tombstone.SetsEntityId.Value, out var setFromTombstone))
                    ? setFromTombstone
                    : assignSet;

                var meta = new MetaEntity
                {
                    FileName = fileName,
                    FileType = isSims3Pack ? "TS3PACK" : fileName.EndsWith(".world", StringComparison.OrdinalIgnoreCase) ? "WORLD" : fileName.EndsWith(".sim", StringComparison.OrdinalIgnoreCase) ? "SIM" : "DBPF",
                    PackageType = tombstone != null ? tombstone.PackageType : typeInfo.PackageType,
                    CASCategories = tombstone != null ? tombstone.CASCategories : typeInfo.CASCategories,
                    CASAge = tombstone != null ? tombstone.CASAge : typeInfo.CASAge,
                    CASGender = tombstone != null ? tombstone.CASGender : typeInfo.CASGender,
                    CASOutfitCategory = tombstone != null ? tombstone.CASOutfitCategory : typeInfo.CASOutfitCategory,
                    IsUserTagged = tombstone != null ? tombstone.IsUserTagged : false,
                    UserTags = tombstone != null ? tombstone.UserTags : null,
                    Description = tombstone?.Description ?? string.Empty,
                    IsFavorite = tombstone != null && tombstone.IsFavorite,
                    CompleteFileName = filePath,
                    SetsEntity = targetSet,
                    InstallDate = DateTime.Now.ToString(),
                    Manifest = string.Empty,
                    Enabled = true,
                    FileSize = currentSizeKb
                };

                if (tombstone != null)
                {
                    _db.Tombstones.Remove(tombstone);
                    tombstones.Remove(fileName);
                }

                targetSet.Dirty = true;
                _db.MetaEntities.Add(meta);
                existingMetas[fileName] = meta;
                result.Add(meta);
            }
            else
            {
                existingMeta.FileSize = currentSizeKb;
                existingMeta.CompleteFileName = filePath;
                if (existingMeta.SetsEntity != null) existingMeta.SetsEntity.Dirty = true;
                result.Add(existingMeta);
            }
        }

        await _db.SaveChangesAsync();
        return result;
    }

    public string GetSims3UserFolderPath()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string eaDir = Path.Combine(docs, "Electronic Arts");
        if (Directory.Exists(eaDir))
        {
            var candidates = Directory.GetDirectories(eaDir)
                .Where(d => Path.GetFileName(d).Contains("Sims 3", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count > 0)
            {
                var exactMatch = candidates.FirstOrDefault(d => Path.GetFileName(d).Equals("The Sims 3", StringComparison.OrdinalIgnoreCase));
                return exactMatch ?? candidates[0];
            }
        }

        return Path.Combine(eaDir, "The Sims 3");
    }

    public string GetDownloadsFolderPath()
    {
        string sims3UserDir = GetSims3UserFolderPath();
        return Path.Combine(sims3UserDir, "Downloads");
    }

    public List<string> GetObservedFolders()
    {
        var result = new List<string>();

        if (_options.ObservedFolders != null && _options.ObservedFolders.Count > 0)
        {
            foreach (var folder in _options.ObservedFolders)
            {
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) && !result.Contains(folder))
                {
                    result.Add(folder);
                }
            }
        }
        else
        {
            string downloads = GetDownloadsFolderPath();
            if (Directory.Exists(downloads)) result.Add(downloads);
        }

        return result;
    }

    public List<string> CheckDownloadsDuplicates()
    {
        var folders = GetObservedFolders();
        if (folders.Count == 0) return new List<string>();

        var validExts = new[] { ".package", ".sims3pack", ".world", ".sim", ".zip", ".rar", ".7z" };
        var candidateNames = new List<string>();

        foreach (var dir in folders)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.GetFiles(dir))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (!validExts.Contains(ext)) continue;

                string fileName = Path.GetFileName(file);
                if (IsArchiveExtension(fileName))
                {
                    try
                    {
                        candidateNames.AddRange(GetArchivePackageFileNames(file));
                    }
                    catch { }
                }
                else
                {
                    candidateNames.Add(fileName);
                }
            }
        }

        return GetDuplicateFiles(candidateNames);
    }

    public async Task<int> ImportFromDownloadsAsync(Action<string>? onProgress = null, string duplicateAction = "rename", ITaskProgressReporter? progress = null)
    {
        CreateFolders();
        var folders = new List<string>();

        if (_options.ObservedFolders != null && _options.ObservedFolders.Count > 0)
        {
            foreach (var folder in _options.ObservedFolders)
            {
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) && !folders.Contains(folder))
                {
                    folders.Add(folder);
                }
            }
        }
        else
        {
            string defaultDownloads = GetDownloadsFolderPath();
            if (Directory.Exists(defaultDownloads)) folders.Add(defaultDownloads);
        }

        var validExts = new[] { ".package", ".sims3pack", ".world", ".sim", ".zip", ".rar", ".7z" };
        var filesToImport = new List<string>();

        foreach (var dir in folders)
        {
            if (!Directory.Exists(dir)) continue;
            filesToImport.AddRange(Directory.GetFiles(dir)
                .Where(f => validExts.Contains(Path.GetExtension(f).ToLowerInvariant())));
        }

        if (filesToImport.Count == 0) return 0;

        string stepId = "import_downloads";
        progress?.StartStep(stepId, _localizer.GetString("progress.importing_downloads"), badge: $"0/{filesToImport.Count}", progress: 0.0);

        int importedCount = 0;
        int current = 0;

        foreach (var file in filesToImport)
        {
            current++;
            double pct = (double)current / filesToImport.Count;
            progress?.UpdateStep(stepId, progress: pct, badge: $"{current}/{filesToImport.Count}");

            string fileName = Path.GetFileName(file);

            if (IsArchiveExtension(fileName))
            {
                var extracted = ExtractPackagesFromArchive(file, _options.ManagedPackageFolderPath, duplicateAction, onProgress);
                if (extracted.Count > 0)
                {
                    try { File.Delete(file); } catch { }
                    importedCount += extracted.Count;
                }
            }
            else
            {
                string destPath = Path.Combine(_options.ManagedPackageFolderPath, fileName);

                if (File.Exists(destPath))
                {
                    if (string.Equals(duplicateAction, "skip", StringComparison.OrdinalIgnoreCase))
                    {
                        onProgress?.Invoke($"Skipped duplicate file: {fileName}");
                        continue;
                    }
                    else if (string.Equals(duplicateAction, "replace", StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(destPath); } catch { }
                        MarkExistingFileDirty(fileName);
                    }
                    else
                    {
                        destPath = Path.Combine(_options.ManagedPackageFolderPath, Guid.NewGuid().ToString().Substring(0, 8) + "_" + fileName);
                    }
                }

                try
                {
                    File.Move(file, destPath, overwrite: true);
                    importedCount++;
                    onProgress?.Invoke($"Moved to Library: {fileName}");
                }
                catch (Exception ex)
                {
                    onProgress?.Invoke($"Failed to move {fileName}: {ex.Message}");
                }
            }
        }

        if (importedCount > 0)
        {
            progress?.UpdateStep(stepId, title: _localizer.GetString("progress.registering_imported_files"));
            onProgress?.Invoke("Registering imported files...");
            await CheckOrphanPackagesAsync();
            onProgress?.Invoke("Import finished.");
        }

        progress?.CompleteStep(stepId, finalBadge: _localizer.GetString("progress.imported_badge", importedCount));
        return importedCount;
    }

    public async Task AutoFixAsync(Action<string>? onProgress = null, ITaskProgressReporter? progress = null)
    {
        string prepStep = "autofix_prep";
        progress?.StartStep(prepStep, _localizer.GetString("progress.repairing_sets_db"), progress: 0.0);
        onProgress?.Invoke("Starting Auto-Fix System...");

        var defaultSet = await _db.SetsEntities.FirstOrDefaultAsync(s => s.Name == "Default");
        if (defaultSet == null)
        {
            defaultSet = new SetsEntity { Name = "Default", FolderName = "Default" };
            _db.SetsEntities.Add(defaultSet);
            await _db.SaveChangesAsync();
            onProgress?.Invoke("Created missing 'Default' set.");
        }

        var unassignedItems = await _db.MetaEntities.Where(m => m.SetsEntityId == null).ToListAsync();
        if (unassignedItems.Count > 0)
        {
            foreach (var item in unassignedItems)
            {
                item.SetsEntityId = defaultSet.Id;
            }
            defaultSet.Dirty = true;
            await _db.SaveChangesAsync();
            onProgress?.Invoke($"Assigned {unassignedItems.Count} unassigned items to Default set.");
        }

        var allSets = await _db.SetsEntities.ToListAsync();
        foreach (var set in allSets)
        {
            set.Dirty = true;
        }
        await _db.SaveChangesAsync();
        progress?.CompleteStep(prepStep, finalBadge: _localizer.GetString("progress.badge_repairs_applied"));

        onProgress?.Invoke("Rebuilding all sets cache...");
        await RunAsync(isRefresh: true, onProgress, progress, forceRebuild: true);
    }

    public async Task<MigrationResult> MigrateCcMagicAsync(bool isFullMigration = true, string? customPath = null, Action<string>? onProgress = null, ITaskProgressReporter? progress = null)
    {
        var result = await _migrationService.MigrateCcMagicAsync(isFullMigration, customPath, onProgress, progress);
        if (result.PackagesMigrated > 0 || result.SetsMigrated > 0)
        {
            progress?.StartStep("register_migrated", "Registering imported items in library...", progress: 0.0);
            await CheckOrphanPackagesAsync();
            await CheckSetParentingAsync();
            await CheckConfigurationsAsync();
            progress?.CompleteStep("register_migrated", finalBadge: $"{result.PackagesMigrated} items indexed");
        }
        return result;
    }

    public async Task<MigrationResult> MigrateS3moAsync(string s3moPath, bool isFullMigration = true, Action<string>? onProgress = null, ITaskProgressReporter? progress = null)
    {
        var result = await _migrationService.MigrateS3moAsync(s3moPath, isFullMigration, onProgress, progress);
        if (result.PackagesMigrated > 0 || result.SetsMigrated > 0)
        {
            progress?.StartStep("register_migrated", "Registering imported items in library...", progress: 0.0);
            await CheckOrphanPackagesAsync();
            await CheckSetParentingAsync();
            await CheckConfigurationsAsync();
            progress?.CompleteStep("register_migrated", finalBadge: $"{result.PackagesMigrated} items indexed");
        }
        return result;
    }

    public async Task SyncToSims3Async(Action<string>? onProgress = null, bool forceRebuildStatic = false)
        => await _cacheBuilderService.SyncToSims3Async(onProgress, forceRebuildStatic);

    private void CreateFolders()
    {
        Directory.CreateDirectory(_options.ManagedPackageFolderPath);
        Directory.CreateDirectory(_options.SetCacheFolderPath);
        Directory.CreateDirectory(Path.Combine(_options.DocumentBaseDir, "Thumbnails"));
    }

    private void MarkExistingFileDirty(string fileName)
    {
        var existing = _db.MetaEntities.FirstOrDefault(m => m.FileName == fileName);
        if (existing != null && existing.SetsEntityId.HasValue)
        {
            var set = _db.SetsEntities.Find(existing.SetsEntityId.Value);
            if (set != null) set.Dirty = true;
        }
    }

    private async Task CheckOrphanPackagesAsync(long? targetSetId = null)
    {
        if (!Directory.Exists(_options.ManagedPackageFolderPath)) return;

        var setEntities = await _db.SetsEntities.ToListAsync();

        SetsEntity assignSet;
        if (targetSetId.HasValue)
        {
            var requestedSet = setEntities.FirstOrDefault(s => s.Id == targetSetId.Value);
            assignSet = requestedSet ?? setEntities.FirstOrDefault(s => s.Name == "Default") ?? setEntities.First();
        }
        else
        {
            assignSet = setEntities.FirstOrDefault(s => s.Name == "Default") ?? setEntities.First();
        }

        var metaEntities = await _db.MetaEntities.ToListAsync();
        var metaMap = new Dictionary<string, MetaEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in metaEntities)
        {
            if (!metaMap.ContainsKey(m.FileName)) metaMap[m.FileName] = m;
        }

        var setMap = setEntities.ToDictionary(s => s.Id);
        var tombstoneEntities = await _db.Tombstones.ToListAsync();
        var tombstoneMap = new Dictionary<string, TombstoneEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tombstoneEntities)
        {
            if (!tombstoneMap.ContainsKey(t.FileName)) tombstoneMap[t.FileName] = t;
        }

        var currentFilesOnDisk = Directory.GetFiles(_options.ManagedPackageFolderPath, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".package", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".world", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".sim", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var diskFileMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in currentFilesOnDisk)
        {
            string fn = Path.GetFileName(file);
            if (!diskFileMap.ContainsKey(fn))
            {
                diskFileMap[fn] = file;
            }
        }

        var toRemoveFromDb = metaEntities.Where(m => !diskFileMap.ContainsKey(m.FileName)).ToList();
        if (toRemoveFromDb.Count > 0)
        {
            var setIdsToDirty = toRemoveFromDb.Where(m => m.SetsEntityId.HasValue).Select(m => m.SetsEntityId!.Value).Distinct().ToList();
            var setsToDirty = setEntities.Where(s => setIdsToDirty.Contains(s.Id)).ToList();
            foreach (var s in setsToDirty) s.Dirty = true;

            foreach (var missing in toRemoveFromDb)
            {
                _thumbnailService.DeleteThumbnail(missing.Id);
            }

            _db.MetaEntities.RemoveRange(toRemoveFromDb);
        }

        bool metaAddedOrUpdated = false;

        foreach (var kvp in diskFileMap)
        {
            string fileName = kvp.Key;
            string filePath = kvp.Value;
            var fileInfo = new FileInfo(filePath);
            double currentSizeKb = fileInfo.Length / 1024.0;

            if (!metaMap.TryGetValue(fileName, out var existingMeta))
            {
                bool isSims3Pack = Path.GetExtension(fileName).Equals(".sims3pack", StringComparison.OrdinalIgnoreCase);

                if (!isSims3Pack && _options.CompressionLevel > 0)
                {
                    try
                    {
                        if (DBPFPackage.OptimizePackage(filePath, _options.CompressionLevel))
                        {
                            fileInfo.Refresh();
                            currentSizeKb = fileInfo.Length / 1024.0;
                        }
                    }
                    catch { }
                }

                (string PackageType, string CASCategories, string CASAge, string CASGender, string CASOutfitCategory) typeInfo = ("Other", "", "", "", "");
                try
                {
                    typeInfo = DetectPackageType(filePath, isSims3Pack);
                }
                catch { /* Ignore lock/read error on raw scan; tombstone or recheck will handle type */ }

                tombstoneMap.TryGetValue(fileName, out var tombstone);

                SetsEntity targetSet = assignSet;
                if (tombstone != null && tombstone.SetsEntityId.HasValue && setMap.TryGetValue(tombstone.SetsEntityId.Value, out var foundSet))
                {
                    targetSet = foundSet;
                }

                var meta = new MetaEntity
                {
                    FileName = fileName,
                    FileType = isSims3Pack ? "TS3PACK" : fileName.EndsWith(".world", StringComparison.OrdinalIgnoreCase) ? "WORLD" : fileName.EndsWith(".sim", StringComparison.OrdinalIgnoreCase) ? "SIM" : "DBPF",
                    PackageType = tombstone != null ? tombstone.PackageType : typeInfo.PackageType,
                    CASCategories = tombstone != null ? tombstone.CASCategories : typeInfo.CASCategories,
                    CASAge = tombstone != null ? tombstone.CASAge : typeInfo.CASAge,
                    CASGender = tombstone != null ? tombstone.CASGender : typeInfo.CASGender,
                    CASOutfitCategory = tombstone != null ? tombstone.CASOutfitCategory : typeInfo.CASOutfitCategory,
                    IsUserTagged = tombstone != null ? tombstone.IsUserTagged : false,
                    UserTags = tombstone != null ? tombstone.UserTags : null,
                    Description = tombstone?.Description ?? string.Empty,
                    IsFavorite = tombstone != null && tombstone.IsFavorite,
                    CompleteFileName = filePath,
                    SetsEntity = targetSet,
                    InstallDate = DateTime.Now.ToString(),
                    Manifest = string.Empty,
                    Enabled = true,
                    FileSize = currentSizeKb
                };

                if (tombstone != null)
                {
                    _db.Tombstones.Remove(tombstone);
                }

                targetSet.Dirty = true;
                _db.MetaEntities.Add(meta);
                metaAddedOrUpdated = true;
            }
            else
            {
                bool isSims3Pack = Path.GetExtension(fileName).Equals(".sims3pack", StringComparison.OrdinalIgnoreCase);
                bool sizeChanged = Math.Abs(existingMeta.FileSize - currentSizeKb) > 0.01;

                if (sizeChanged || existingMeta.CompleteFileName != filePath)
                {
                    (string PackageType, string CASCategories, string CASAge, string CASGender, string CASOutfitCategory) typeInfo = ("Other", "", "", "", "");
                    try
                    {
                        typeInfo = DetectPackageType(filePath, isSims3Pack);
                    }
                    catch { /* Ignore lock/read error on raw scan */ }

                    existingMeta.FileType = isSims3Pack ? "TS3PACK" : "DBPF";
                    if (!existingMeta.IsUserTagged)
                    {
                        existingMeta.PackageType = typeInfo.PackageType;
                        existingMeta.CASCategories = typeInfo.CASCategories;
                        existingMeta.CASAge = typeInfo.CASAge;
                        existingMeta.CASGender = typeInfo.CASGender;
                        existingMeta.CASOutfitCategory = typeInfo.CASOutfitCategory;
                    }
                    existingMeta.CompleteFileName = filePath;
                    existingMeta.FileSize = currentSizeKb;
                    existingMeta.InstallDate = DateTime.Now.ToString();

                    if (existingMeta.SetsEntity != null)
                    {
                        existingMeta.SetsEntity.Dirty = true;
                    }
                    else
                    {
                        existingMeta.SetsEntity = assignSet;
                        assignSet.Dirty = true;
                    }
                    metaAddedOrUpdated = true;
                }
            }
        }

        if (toRemoveFromDb.Count > 0 || metaAddedOrUpdated)
        {
            await _db.SaveChangesAsync();
        }
    }

    private async Task CheckSetParentingAsync()
    {
        var allSets = await _db.SetsEntities.ToListAsync();
        bool modified = false;

        foreach (var set in allSets)
        {
            if (set.ParentSetsEntityId.HasValue)
            {
                var visited = new HashSet<long> { set.Id };
                var current = allSets.FirstOrDefault(s => s.Id == set.ParentSetsEntityId.Value);
                bool isCycleOrMissing = false;

                while (current != null)
                {
                    if (visited.Contains(current.Id))
                    {
                        isCycleOrMissing = true;
                        break;
                    }
                    visited.Add(current.Id);
                    current = current.ParentSetsEntityId.HasValue
                        ? allSets.FirstOrDefault(s => s.Id == current.ParentSetsEntityId.Value)
                        : null;
                }

                if (isCycleOrMissing || !allSets.Any(s => s.Id == set.ParentSetsEntityId.Value))
                {
                    set.ParentSetsEntityId = null;
                    modified = true;
                }
            }
        }

        if (modified)
        {
            await _db.SaveChangesAsync();
        }
    }

    private async Task CheckConfigurationsAsync()
    {
        var configs = await _db.ConfigEntities.Include(c => c.ConfigSetsEntities).ToListAsync();
        if (configs.Count == 0)
        {
            var defaultConfig = new ConfigEntity { Name = "Default", Description = "Default Configuration", Active = true };
            _db.ConfigEntities.Add(defaultConfig);
            await _db.SaveChangesAsync();

            var allSets = await _db.SetsEntities.ToListAsync();
            foreach (var set in allSets)
            {
                _db.ConfigSetsEntities.Add(new ConfigSetsEntity { ConfigEntityId = defaultConfig.Id, SetsEntityId = set.Id });
            }
            await _db.SaveChangesAsync();
        }
        else if (!configs.Any(c => c.Active))
        {
            configs[0].Active = true;
            await _db.SaveChangesAsync();
        }
    }

    public async Task RebuildCacheAsync(bool forceRebuild = false, Action<string>? onProgress = null, List<(string FileName, string Reason)>? skippedFiles = null, ITaskProgressReporter? progress = null)
        => await _cacheBuilderService.RebuildCacheAsync(forceRebuild, onProgress, skippedFiles, progress);

    public async Task RebuildStaticCacheAsync(Action<string>? onProgress = null, List<(string FileName, string Reason)>? skippedFiles = null, ITaskProgressReporter? progress = null)
        => await _cacheBuilderService.RebuildStaticCacheAsync(onProgress, skippedFiles, progress);

    public string GetSetFolderName(SetsEntity activeSet) => _cacheBuilderService.GetSetFolderName(activeSet);
    public string GetSetPath(SetsEntity activeSet) => _cacheBuilderService.GetSetPath(activeSet);
    public string GetSetCachePath(string folderName) => _cacheBuilderService.GetSetCachePath(folderName);
    public void RebuildSet(SetsEntity activeSet, Action<string>? onProgress = null, List<(string FileName, string Reason)>? skippedFiles = null, ITaskProgressReporter? progress = null, string? stepId = null, int totalItems = 0)
        => _cacheBuilderService.RebuildSet(activeSet, onProgress, skippedFiles, progress, stepId, totalItems);

    public async Task<string?> GetThumbnailPathAsync(long itemId) => await _thumbnailService.GetThumbnailPathAsync(itemId);

    public (string PackageType, string CASCategories, string CASAge, string CASGender, string CASOutfitCategory) DetectPackageType(string filePath, bool isSims3Pack)
        => _packageTypeService.DetectPackageType(filePath, isSims3Pack);

    public async Task<(int TotalScanned, int UpdatedCount)> RecheckPackageTypesAsync(Action<string>? onProgress = null, bool skipUserTagged = true, ITaskProgressReporter? progress = null)
        => await _packageTypeService.RecheckPackageTypesAsync(onProgress, skipUserTagged, progress);

    public async Task<(int optimizedCount, long bytesSaved)> OptimizeLibraryAsync(Action<string>? onProgress = null, ITaskProgressReporter? progress = null)
    {
        string stepId = "optimize_library";
        progress?.StartStep(stepId, "Optimizing library packages...", progress: 0.0);
        onProgress?.Invoke("Scanning library for uncompressed packages...");

        if (!Directory.Exists(_options.ManagedPackageFolderPath))
        {
            progress?.CompleteStep(stepId, finalBadge: "Library empty");
            return (0, 0);
        }

        var files = Directory.GetFiles(_options.ManagedPackageFolderPath, "*.package", SearchOption.AllDirectories);
        int total = files.Length;
        int current = 0;
        int optimizedCount = 0;
        long totalBytesSaved = 0;
        int reportInterval = Math.Max(1, total / 50);

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };

        await Task.Run(() =>
        {
            Parallel.ForEach(files, parallelOptions, filePath =>
            {
                int index = Interlocked.Increment(ref current);
                if (index % reportInterval == 0 || index == total)
                {
                    double pct = total > 0 ? (double)index / total : 1.0;
                    string badge = $"{index}/{total}";
                    progress?.UpdateStep(stepId, progress: pct, badge: badge);
                    onProgress?.Invoke($"Optimizing packages ({index}/{total})...");
                }

                try
                {
                    long beforeSize = new FileInfo(filePath).Length;
                    if (DBPFPackage.OptimizePackage(filePath, _options.CompressionLevel > 0 ? _options.CompressionLevel : 1))
                    {
                        long afterSize = new FileInfo(filePath).Length;
                        long saved = beforeSize - afterSize;
                        if (saved > 0)
                        {
                            Interlocked.Add(ref totalBytesSaved, saved);
                        }
                        Interlocked.Increment(ref optimizedCount);
                    }
                }
                catch { }
            });
        });

        if (optimizedCount > 0)
        {
            onProgress?.Invoke("Updating database file sizes...");
            var allMetas = await _db.MetaEntities.Where(m => m.FileType == "DBPF").ToListAsync();
            foreach (var meta in allMetas)
            {
                if (File.Exists(meta.CompleteFileName))
                {
                    meta.FileSize = new FileInfo(meta.CompleteFileName).Length / 1024.0;
                }
            }
            await _db.SaveChangesAsync();
        }

        double mbSaved = totalBytesSaved / (1024.0 * 1024.0);
        string resultMsg = $"Optimized {optimizedCount} packages, saving {mbSaved:F2} MB of disk space.";
        onProgress?.Invoke(resultMsg);
        progress?.CompleteStep(stepId, finalBadge: $"{optimizedCount} optimized ({mbSaved:F1} MB saved)");

        return (optimizedCount, totalBytesSaved);
    }
}
