using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlumbobForge.Backend.Configuration;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;
using S3ForgeTools.GameFiles.TS3Pack;
using S3ForgeTools.Utils.Logging;
using SkiaSharp;

namespace PlumbobForge.Backend.Services;

public class Sims3CollectionService
{
    private static readonly ILog log = LogManager.GetLogger(nameof(Sims3CollectionService));

    private readonly AppDbContext _db;
    private readonly CacheBuilderService _cacheBuilderService;
    private readonly IOptionsMonitor<PlumbobForgeOptions> _optionsMonitor;
    private readonly Sims3HiderService _hiderService;

    private static readonly List<CollectionIconDef> _standardIcons = new();
    private static readonly List<CollectionIconDef> _customIcons = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<TGI_Key, byte[]> _iconBytesCache = new();
    private static bool _hasPreloadedIcons = false;
    private static readonly object _iconPreloadLock = new();

    public Sims3CollectionService(
        AppDbContext db,
        CacheBuilderService cacheBuilderService,
        IOptionsMonitor<PlumbobForgeOptions> optionsMonitor,
        Sims3HiderService hiderService)
    {
        _db = db;
        _cacheBuilderService = cacheBuilderService;
        _optionsMonitor = optionsMonitor;
        _hiderService = hiderService;
    }

    public string GetCollectionsFolderPath()
    {
        string s3Dir = _cacheBuilderService?.GetSims3FolderPath() ?? _optionsMonitor.CurrentValue.DocumentBaseDir;
        string userCollDir = Path.Combine(s3Dir, "Collections", "User");
        if (!Directory.Exists(userCollDir))
        {
            try
            {
                Directory.CreateDirectory(userCollDir);
            }
            catch (Exception ex)
            {
                log.Warn($"Could not create Sims 3 collections directory '{userCollDir}': {ex.Message}");
            }
        }
        return userCollDir;
    }

    public Task PreloadGameIconsAsync() => Task.Run(PreloadGameIcons);

    public void PreloadGameIcons()
    {
        if (_hasPreloadedIcons) return;

        lock (_iconPreloadLock)
        {
            if (_hasPreloadedIcons) return;
            _hasPreloadedIcons = true;

            EnsureStandardIconsInitialized();

            string gameDir = _optionsMonitor.CurrentValue.GameFilesDir;
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            {
                gameDir = GamePathValidator.AutodetectGameFilesPath();
            }

            if (!string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir))
            {
                try
                {
                    var standardKeys = _standardIcons.Select(i => i.Key).ToHashSet();
                    var packagesToScan = new List<string>();

                    string primaryFullBuild = Path.Combine(gameDir, "GameData", "Shared", "Packages", "FullBuild0.package");
                    if (File.Exists(primaryFullBuild))
                    {
                        packagesToScan.Add(primaryFullBuild);
                    }

                    // Also search for FullBuild packages in gameDir and expansion/stuff pack folders
                    try
                    {
                        string searchRoot = gameDir;
                        var parent = Directory.GetParent(gameDir);
                        if (parent != null && parent.Exists && Directory.GetDirectories(parent.FullName, "The Sims 3*").Length > 0)
                        {
                            searchRoot = parent.FullName;
                        }

                        var discovered = Directory.GetFiles(searchRoot, "*Build*.package", SearchOption.AllDirectories);
                        foreach (var fb in discovered)
                        {
                            string fileName = Path.GetFileName(fb);
                            if (fileName.StartsWith("FullBuild", StringComparison.OrdinalIgnoreCase) ||
                                fileName.StartsWith("DeltaBuild", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!packagesToScan.Contains(fb, StringComparer.OrdinalIgnoreCase))
                                {
                                    packagesToScan.Add(fb);
                                }
                            }
                        }

                        string uiPkg = Path.Combine(gameDir, "Game", "Bin", "UI", "UI.package");
                        if (File.Exists(uiPkg) && !packagesToScan.Contains(uiPkg, StringComparer.OrdinalIgnoreCase))
                        {
                            packagesToScan.Add(uiPkg);
                        }
                    }
                    catch { }

                    var standardInstances = _standardIcons.Select(i => i.Key.Instance).ToHashSet();

                    foreach (var pkgPath in packagesToScan)
                    {
                        try
                        {
                            using var pkg = new DBPFPackage(pkgPath);
                            var iconResources = pkg.Resources.Where(r => r.Key.Type == 0x2F7D0004).ToList();
                            foreach (var res in iconResources)
                            {
                                if ((standardKeys.Count == 0 || standardKeys.Contains(res.Key) || standardInstances.Contains(res.Key.Instance)) && !_iconBytesCache.ContainsKey(res.Key))
                                {
                                    var data = res.Read();
                                    if (data != null && data.Length > 16)
                                    {
                                        _iconBytesCache[res.Key] = data;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    log.Warn($"Error preloading Sims 3 collection icons from '{gameDir}': {ex.Message}");
                }
            }

            // Load custom icons from user directory
            LoadCustomIcons();

            // Populate IconBytes on standard icons
            foreach (var def in _standardIcons)
            {
                if (def.IconBytes == null)
                {
                    if (_iconBytesCache.TryGetValue(def.Key, out var bytes))
                    {
                        def.IconBytes = bytes;
                    }
                    else
                    {
                        var match = _iconBytesCache.FirstOrDefault(kvp => kvp.Key.Instance == def.Key.Instance);
                        if (match.Value != null)
                        {
                            def.IconBytes = match.Value;
                        }
                    }
                }
            }
        }
    }

    public byte[]? GetIconImageBytes(TGI_Key key)
    {
        if (_iconBytesCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // Also match by Instance as fallback
        var matchByInstance = _iconBytesCache.FirstOrDefault(kvp => kvp.Key.Instance == key.Instance);
        if (matchByInstance.Value != null)
        {
            return matchByInstance.Value;
        }

        if (!_hasPreloadedIcons)
        {
            PreloadGameIcons();
            if (_iconBytesCache.TryGetValue(key, out var loaded))
            {
                return loaded;
            }
            matchByInstance = _iconBytesCache.FirstOrDefault(kvp => kvp.Key.Instance == key.Instance);
            if (matchByInstance.Value != null)
            {
                return matchByInstance.Value;
            }
        }

        return null;
    }

    public async Task<List<CollectionModel>> LoadUserCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var dbCollMap = new Dictionary<ulong, CollectionEntity>();
        try
        {
            await EnsureDatabaseTablesAsync(cancellationToken);
            var dbCollections = await _db.Collections
                .Include(c => c.CollectionSets)
                .ThenInclude(cs => cs.Set)
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            dbCollMap = dbCollections.ToDictionary(c => c.Id, c => c);
        }
        catch (Exception ex)
        {
            log.Warn($"Could not query Collections from database: {ex.Message}");
        }

        return await Task.Run(() =>
        {
            var collections = new List<CollectionModel>();
            string folder = GetCollectionsFolderPath();
            if (!Directory.Exists(folder)) return collections;

            PreloadGameIcons();

            var iconDefs = GetStandardIcons();
            var iconMap = iconDefs.ToDictionary(i => i.Key, i => i);

            var files = Directory.GetFiles(folder, "coll_0x*.package");
            foreach (var filePath in files)
            {
                if (cancellationToken.IsCancellationRequested) break;

                // Ignore signature files if any
                if (File.Exists(filePath + ".sig")) continue;

                string fileName = Path.GetFileName(filePath);
                ulong collectionId = ParseCollectionIdFromFileName(fileName);
                if (collectionId == 0) continue;

                try
                {
                    using var pkg = new DBPFPackage(filePath);
                    var collRes = pkg.Resources.FirstOrDefault(r => r.Key.Type == 0x0C07456D || r.Key.Type == 0x0C074C4D);
                    if (collRes == null) continue;

                    byte[] data = collRes.Read();
                    using var reader = new BinaryReader(new MemoryStream(data, writable: false));

                    uint version = reader.ReadUInt32();
                    if (version != 1) continue;

                    int nameChars = reader.ReadInt32();
                    byte[] nameBytes = reader.ReadBytes(nameChars * 2);
                    string name = Encoding.Unicode.GetString(nameBytes);

                    uint flags = reader.ReadUInt32();
                    uint iconType = reader.ReadUInt32();
                    uint iconGroup = reader.ReadUInt32();
                    ulong iconInstance = reader.ReadUInt64();
                    var iconKey = new TGI_Key(iconType, iconGroup, iconInstance);

                    int itemCount = reader.ReadInt32();
                    var items = new Dictionary<TGI_Key, uint>();

                    for (int i = 0; i < itemCount; i++)
                    {
                        uint iType = reader.ReadUInt32();
                        uint iGroup = reader.ReadUInt32();
                        ulong iInstance = reader.ReadUInt64();
                        uint iVal = reader.ReadUInt32();
                        var itemKey = new TGI_Key(iType, iGroup, iInstance);
                        if (!items.ContainsKey(itemKey))
                        {
                            items[itemKey] = iVal;
                        }
                    }

                    string iconName = "Custom Icon";
                    string iconCategory = "General";
                    if (iconMap.TryGetValue(iconKey, out var foundDef))
                    {
                        iconName = foundDef.DisplayName;
                        iconCategory = foundDef.Category;
                    }

                    var fi = new FileInfo(filePath);
                    bool isPlumbobForge = false;
                    bool hideFromCatalog = false;
                    var linkedSetIds = new List<long>();
                    var linkedSetNames = new List<string>();

                    if (dbCollMap.TryGetValue(collectionId, out var dbColl))
                    {
                        isPlumbobForge = dbColl.IsPlumbobForge;
                        hideFromCatalog = dbColl.HideFromCatalog;
                        linkedSetIds = dbColl.CollectionSets.Select(cs => cs.SetsEntityId).ToList();
                        linkedSetNames = dbColl.CollectionSets.Where(cs => cs.Set != null).Select(cs => cs.Set!.Name).ToList();
                    }

                    collections.Add(new CollectionModel
                    {
                        Id = collectionId,
                        Name = name,
                        FileName = fileName,
                        FilePath = filePath,
                        IconKey = iconKey,
                        IconName = iconName,
                        IconCategory = iconCategory,
                        IconBytes = GetIconImageBytes(iconKey),
                        Flags = flags,
                        ItemCount = items.Count,
                        Items = items,
                        IsPlumbobForge = isPlumbobForge,
                        HideFromCatalog = hideFromCatalog,
                        LinkedSetIds = linkedSetIds,
                        LinkedSetNames = linkedSetNames,
                        LastModified = fi.LastWriteTime
                    });
                }
                catch (Exception ex)
                {
                    log.Warn($"Error reading collection file '{filePath}': {ex.Message}");
                }
            }

            return collections.OrderBy(c => c.Name).ToList();
        }, cancellationToken);
    }

    public async Task<List<CollectionSetOption>> GetAvailableSetOptionsAsync(CancellationToken cancellationToken = default)
    {
        var sets = await _db.SetsEntities
            .AsNoTracking()
            .Include(s => s.MetaEntities)
            .ToListAsync(cancellationToken);

        var setsById = sets.ToDictionary(s => s.Id);

        string BuildBreadcrumb(SetsEntity set)
        {
            var ancestors = new List<string>();
            long? currentParentId = set.ParentSetsEntityId;
            var visited = new HashSet<long>();

            while (currentParentId.HasValue && !visited.Contains(currentParentId.Value))
            {
                visited.Add(currentParentId.Value);
                if (setsById.TryGetValue(currentParentId.Value, out var parent))
                {
                    ancestors.Insert(0, parent.Name);
                    currentParentId = parent.ParentSetsEntityId;
                }
                else
                {
                    break;
                }
            }

            return ancestors.Count > 0 ? string.Join("  ›  ", ancestors) : string.Empty;
        }

        return sets
            .OrderBy(s => s.Name)
            .Select(s => new CollectionSetOption
            {
                SetId = s.Id,
                SetName = s.Name,
                BreadcrumbPath = BuildBreadcrumb(s),
                Icon = string.IsNullOrWhiteSpace(s.Icon) ? "RegularFolder" : s.Icon,
                Color = string.IsNullOrWhiteSpace(s.Color) ? null : s.Color,
                ItemCount = s.MetaEntities.Count(m => m.Enabled && (
                    string.Equals(m.PackageType, "BuildBuy", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(m.PackageType, "Build/Buy", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(m.PackageType, "Object", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(m.PackageType, "Build-Buy", StringComparison.OrdinalIgnoreCase)
                )),
                IsSelected = false
            })
            .ToList();
    }

    private async Task EnsureDatabaseTablesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS Collections (
    Id INTEGER PRIMARY KEY NOT NULL,
    Name TEXT NOT NULL,
    IconType INTEGER NOT NULL,
    IconGroup INTEGER NOT NULL,
    IconInstance INTEGER NOT NULL,
    Flags INTEGER NOT NULL,
    IsPlumbobForge INTEGER NOT NULL DEFAULT 1,
    HideFromCatalog INTEGER NOT NULL DEFAULT 0,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS CollectionSets (
    Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
    CollectionEntityId INTEGER NOT NULL,
    SetsEntityId INTEGER NOT NULL,
    FOREIGN KEY (CollectionEntityId) REFERENCES Collections(Id) ON DELETE CASCADE,
    FOREIGN KEY (SetsEntityId) REFERENCES SetsEntities(Id) ON DELETE CASCADE
);", cancellationToken);
            try { await _db.Database.ExecuteSqlRawAsync("ALTER TABLE Collections ADD COLUMN HideFromCatalog INTEGER NOT NULL DEFAULT 0;", cancellationToken); } catch { }
        }
        catch { }
    }

    public async Task<CollectionSaveResult> SaveCollectionAsync(
        ulong? existingCollectionId,
        string name,
        TGI_Key iconKey,
        IEnumerable<long> includedSetIds,
        bool hideFromCatalog = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseTablesAsync(cancellationToken);
        var result = new CollectionSaveResult();

        if (string.IsNullOrWhiteSpace(name))
        {
            result.ErrorMessage = "Collection name cannot be empty.";
            return result;
        }

        string folder = GetCollectionsFolderPath();
        if (!Directory.Exists(folder))
        {
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Failed to create Collections folder: {ex.Message}";
                return result;
            }
        }

        ulong finalId = existingCollectionId.HasValue && existingCollectionId.Value != 0
            ? existingCollectionId.Value
            : GenerateCollectionId(name);

        string fileName = $"coll_0x{finalId:x16}.package";
        string targetPath = Path.Combine(folder, fileName);

        var setIdsList = includedSetIds?.Distinct().ToList() ?? new List<long>();
        var allSelectedSetIds = new HashSet<long>(setIdsList);

        // Include child sets recursively
        var allSets = await _db.SetsEntities.AsNoTracking().ToListAsync(cancellationToken);
        void AddDescendants(long parentId)
        {
            var children = allSets.Where(s => s.ParentSetsEntityId == parentId).Select(s => s.Id);
            foreach (var childId in children)
            {
                if (allSelectedSetIds.Add(childId))
                {
                    AddDescendants(childId);
                }
            }
        }
        foreach (var id in setIdsList)
        {
            AddDescendants(id);
        }

        // Query items from selected sets
        var metaItems = await _db.MetaEntities
            .AsNoTracking()
            .Where(m => m.SetsEntityId.HasValue && allSelectedSetIds.Contains(m.SetsEntityId.Value))
            .ToListAsync(cancellationToken);

        var itemKeys = new Dictionary<TGI_Key, uint>();

        foreach (var item in metaItems)
        {
            if (cancellationToken.IsCancellationRequested) break;

            string filePath = item.CompleteFileName;
            if (!File.Exists(filePath))
            {
                string fallback = Path.Combine(_optionsMonitor.CurrentValue.ManagedPackageFolderPath, item.FileName);
                if (File.Exists(fallback))
                {
                    filePath = fallback;
                }
                else
                {
                    continue;
                }
            }

            try
            {
                if (item.FileName.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase))
                {
                    using var s3p = new Sims3Pack(filePath);
                    foreach (var package in s3p.Packages)
                    {
                        var objdKeys = package.Resources
                            .Where(r => r.Key.Type == 0x319E4F1D)
                            .Select(r => r.Key)
                            .ToList();

                        if (objdKeys.Count > 0)
                        {
                            foreach (var key in objdKeys)
                            {
                                if (!itemKeys.ContainsKey(key))
                                {
                                    itemKeys[key] = 32767u;
                                }
                            }
                        }
                        else if (package.Resources.Count > 0)
                        {
                            var firstKey = package.Resources[0].Key;
                            if (!itemKeys.ContainsKey(firstKey))
                            {
                                itemKeys[firstKey] = 32767u;
                            }
                        }
                    }
                }
                else
                {
                    using var pkg = new DBPFPackage(filePath);
                    var objdKeys = pkg.Resources
                        .Where(r => r.Key.Type == 0x319E4F1D)
                        .Select(r => r.Key)
                        .ToList();

                    if (objdKeys.Count > 0)
                    {
                        foreach (var key in objdKeys)
                        {
                            if (!itemKeys.ContainsKey(key))
                            {
                                itemKeys[key] = 32767u;
                            }
                        }
                    }
                    else if (pkg.Resources.Count > 0)
                    {
                        var firstKey = pkg.Resources[0].Key;
                        if (!itemKeys.ContainsKey(firstKey))
                        {
                            itemKeys[firstKey] = 32767u;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.Warn($"Error extracting object keys from '{filePath}': {ex.Message}");
            }
        }

        // Encode 0x0C07456D (201803117u) binary resource
        byte[] resourceBytes;
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            writer.Write(1u); // version = 1
            writer.Write((uint)name.Length);
            writer.Write(Encoding.Unicode.GetBytes(name));
            writer.Write(0u); // flags
            writer.Write(iconKey.Type);
            writer.Write(iconKey.Group);
            writer.Write(iconKey.Instance);
            writer.Write((uint)itemKeys.Count);

            foreach (var kvp in itemKeys)
            {
                writer.Write(kvp.Key.Type);
                writer.Write(kvp.Key.Group);
                writer.Write(kvp.Key.Instance);
                writer.Write(kvp.Value);
            }

            resourceBytes = ms.ToArray();
        }

        // Build package file
        string tempFile = targetPath + ".tmp";
        try
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);

            using (var builder = new DBPFPackageBuilder(tempFile, false))
            {
                using var ms = new MemoryStream(resourceBytes, writable: false);
                var entry = new ResourceEntry(ms, new TGI_Key(0x0C07456D, 0, finalId));
                builder.AddResource(entry);

                // If icon bytes are in cache (custom icons or preloaded game icons), also embed image resource
                if (_iconBytesCache.TryGetValue(iconKey, out var iconBytes) && iconBytes != null && iconBytes.Length > 0)
                {
                    using var iconMs = new MemoryStream(iconBytes, writable: false);
                    builder.AddResource(new ResourceEntry(iconMs, iconKey));
                }
            }

            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(tempFile, targetPath);

            // Persist to Database as a PlumbobForge-managed collection
            var dbColl = await _db.Collections.Include(c => c.CollectionSets).FirstOrDefaultAsync(c => c.Id == finalId, cancellationToken);
            if (dbColl == null)
            {
                dbColl = new CollectionEntity
                {
                    Id = finalId,
                    Name = name,
                    IconType = iconKey.Type,
                    IconGroup = iconKey.Group,
                    IconInstance = iconKey.Instance,
                    Flags = 0,
                    IsPlumbobForge = true,
                    HideFromCatalog = hideFromCatalog,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _db.Collections.Add(dbColl);
            }
            else
            {
                dbColl.Name = name;
                dbColl.IconType = iconKey.Type;
                dbColl.IconGroup = iconKey.Group;
                dbColl.IconInstance = iconKey.Instance;
                dbColl.IsPlumbobForge = true;
                dbColl.HideFromCatalog = hideFromCatalog;
                dbColl.UpdatedAt = DateTime.UtcNow;
                _db.CollectionSets.RemoveRange(dbColl.CollectionSets);
            }

            foreach (var setId in setIdsList)
            {
                _db.CollectionSets.Add(new CollectionSetsEntity
                {
                    CollectionEntityId = finalId,
                    SetsEntityId = setId
                });
            }

            await _db.SaveChangesAsync(cancellationToken);

            // Synchronize Hider Override Package in Mods/Overrides
            if (hideFromCatalog)
            {
                await _hiderService.GenerateHiderPackageAsync(finalId, name, setIdsList, cancellationToken);
            }
            else
            {
                _hiderService.DeleteHiderPackage(finalId);
            }

            result.Success = true;
            result.CollectionId = finalId;
            result.FilePath = targetPath;
            result.ItemCount = itemKeys.Count;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Failed to write collection package: {ex.Message}";
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }

        return result;
    }

    public async Task<bool> DeleteCollectionAsync(ulong collectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            string folder = GetCollectionsFolderPath();
            string fileName = $"coll_0x{collectionId:x16}.package";
            string filePath = Path.Combine(folder, fileName);

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            string sigPath = filePath + ".sig";
            if (File.Exists(sigPath))
            {
                try { File.Delete(sigPath); } catch { }
            }

            // Remove associated hider package
            _hiderService.DeleteHiderPackage(collectionId);

            var dbColl = await _db.Collections.FirstOrDefaultAsync(c => c.Id == collectionId, cancellationToken);
            if (dbColl != null)
            {
                _db.Collections.Remove(dbColl);
                await _db.SaveChangesAsync(cancellationToken);
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Error($"Failed to delete collection 0x{collectionId:x16}: {ex.Message}");
            return false;
        }
    }

    public async Task<string> ExportHiderXmlAsync(
        ulong collectionId,
        string collectionName,
        IEnumerable<long> includedSetIds,
        CancellationToken cancellationToken = default)
    {
        var hiderRes = await _hiderService.GenerateHiderPackageAsync(collectionId, collectionName, includedSetIds, cancellationToken);
        return _hiderService.GenerateHiderXml(collectionName, hiderRes.ObjectGuids);
    }

    public bool DeleteCollection(ulong collectionId)
    {
        return DeleteCollectionAsync(collectionId).GetAwaiter().GetResult();
    }

    private static ulong ParseCollectionIdFromFileName(string fileName)
    {
        string raw = Path.GetFileNameWithoutExtension(fileName).Replace("coll_0x", "", StringComparison.OrdinalIgnoreCase);
        if (ulong.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong id))
        {
            return id;
        }
        return 0;
    }

    private static ulong GenerateCollectionId(string name)
    {
        ulong hash = 14695981039346656037UL;
        string keyStr = $"PlumbobForge:Coll_{DateTime.UtcNow.Ticks}_{name}";
        foreach (byte b in Encoding.UTF8.GetBytes(keyStr))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash & 0x7FFFFFFFFFFFFFFFL;
    }

    private static byte[]? LoadEmbeddedIconBytes(string keyName)
    {
        try
        {
            var asm = typeof(Sims3CollectionService).Assembly;
            string resName = $"PlumbobForge.Backend.Assets.CollectionIcons.{keyName}.png";
            using var stream = asm.GetManifestResourceStream(resName);
            if (stream != null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }
        catch { }
        return null;
    }

    private void EnsureStandardIconsInitialized()
    {
        if (_standardIcons.Count > 0) return;

        lock (_standardIcons)
        {
            if (_standardIcons.Count > 0) return;

            void AddIcon(string key, string display, string cat, uint group, string boxIcon, ulong inst = 0)
            {
                if (inst == 0)
                {
                    inst = S3Launcher.FNV.FNV64(key);
                }
                var tgi = new TGI_Key(0x2F7D0004, group, inst);
                byte[]? embeddedBytes = LoadEmbeddedIconBytes(key);
                if (embeddedBytes != null && embeddedBytes.Length > 0)
                {
                    _iconBytesCache[tgi] = embeddedBytes;
                }

                _standardIcons.Add(new CollectionIconDef
                {
                    KeyName = key,
                    DisplayName = display,
                    Category = cat,
                    Key = tgi,
                    BoxIconKind = boxIcon,
                    IconBytes = embeddedBytes,
                    IsCustom = false
                });
            }

            // Furniture & Rooms
            AddIcon("w_home_s", "Home / House", "Furniture", 0, "RegularHome", 13431223140568206672uL);
            AddIcon("w_couch_potato_s", "Sofa / Living", "Furniture", 0, "RegularFolder", 349898087017155377uL);
            AddIcon("w_sleep_s", "Bed / Bedroom", "Furniture", 0, "RegularBed", 17233111300066886534uL);
            AddIcon("w_bathtub", "Bath / Cleaning", "Furniture", 0, "RegularBath", 14765122311315112553uL);
            AddIcon("w_eat_s", "Dining / Kitchen", "Furniture", 0, "RegularRestaurant", 13405665210905376829uL);
            AddIcon("w_buffet_table", "Party & Entertainment", "Furniture", 0, "RegularDrink", 2102930425101080782uL);
            AddIcon("w_videogame_system", "Gaming & Arcade", "Furniture", 0, "RegularJoystick", 11589221968105629590uL);
            AddIcon("w_ride_playground_s", "Kids & Playground", "Furniture", 0, "RegularPlay", 15898644979653680901uL);
            AddIcon("w_toddler_toy", "Kids & Toys", "Furniture", 0, "RegularSmile", 17169555141882971880uL);
            AddIcon("w_dryer", "Laundry & Washer", "Furniture", 0, "RegularCloset", 3728279584209084851uL);
            AddIcon("bb_floors_carpeting", "Rugs & Carpets", "Furniture", 0, "RegularGrid", 8864277438041121679uL);
            AddIcon("w_mirror", "Mirrors & Vanity", "Furniture", 0, "RegularWindow", 16388138558365196054uL);
            AddIcon("w_dresser", "Wardrobe & Closets", "Furniture", 0, "RegularCloset", 17602023768067119421uL);
            AddIcon("w_tv", "Television & Screens", "Furniture", 0, "RegularTv", 8045873692398082869uL);

            // Build & Architecture
            AddIcon("bb_walls", "Walls & Architecture", "Build", 0, "RegularGridAlt", 3489046228903101895uL);
            AddIcon("bb_doors", "Doors & Entries", "Build", 0, "RegularDoorOpen", 7006000625687479299uL);
            AddIcon("bb_windows", "Windows", "Build", 0, "RegularWindow", 14023651921816850639uL);
            AddIcon("bb_pool", "Pools & Fountains", "Build", 0, "RegularWater", 16579006282195591498uL);
            AddIcon("bb_fireplace", "Fireplaces", "Build", 0, "RegularFlame", 2946752572512101445uL);
            AddIcon("buy_subcat_lighting", "Lighting & Lamps", "Build", 0, "RegularBulb", 7695239338387544163uL);
            AddIcon("bb_roofs", "Roofs & Trimmings", "Build", 0, "RegularBuilding", 3691989508411117693uL);
            AddIcon("bb_stairs", "Stairs & Steps", "Build", 0, "RegularLayer", 1478475941658259450uL);
            AddIcon("bb_fences", "Fences & Railings", "Build", 0, "RegularGrid", 15019238842399817802uL);
            AddIcon("bb_trees", "Landscaping & Trees", "Build", 0, "RegularSpa", 877013317416687899uL);

            // Occult & Supernatural
            AddIcon("w_ghosthunter_career_s", "Ghost Hunter", "Occult", 402653184u, "RegularGhost", 16581782598328850621uL);
            AddIcon("w_vampire_ep3", "Vampire & Night", "Occult", 0, "RegularMoon", 15810730221637532136uL);
            AddIcon("w_full_moon_ep7", "Werewolf & Lycan", "Occult", 0, "RegularGhost", 10151739414498660179uL);
            AddIcon("w_fairy_gift_ep7", "Fairy & Pixie", "Occult", 0, "RegularStar", 13357300171182368140uL);
            AddIcon("w_magic_wand_ep7", "Witch & Sorcery", "Occult", 0, "RegularWand", 1238057540053452566uL);
            AddIcon("w_occult_ep7", "Occult & Supernatural", "Occult", 0, "RegularPlanet", 8308057639212026894uL);
            AddIcon("w_potion_ep7", "Alchemy & Potions", "Occult", 0, "RegularTestTube", 13436839868117488093uL);
            AddIcon("w_clean_dusty_lamp_ep6", "Genie & Lamp", "Occult", 0, "RegularStar", 12729499136383965069uL);
            AddIcon("motivehunger_mermaid1_ep10", "Mermaid & Sea", "Occult", 0, "RegularWater", 7256474733153041210uL);
            AddIcon("moodlet_hadspaceadventure_ep8", "Alien & UFO", "Occult", 0, "RegularPlanet", 11081943380451804671uL);
            AddIcon("servobot_ep11", "Plumbot / Android", "Occult", 0, "RegularBot", 4666995623503175231uL);

            // Seasons & Weather
            AddIcon("w_go_festival_spring_ep8", "Spring & Flowers", "Seasons", 0, "RegularSpa", 10149073806252310008uL);
            AddIcon("w_go_festival_summer_ep8", "Summer & Sun", "Seasons", 0, "RegularSun", 5557374622462172736uL);
            AddIcon("w_go_festival_fall_ep8", "Fall & Harvest", "Seasons", 0, "RegularLeaf", 3596058832395218860uL);
            AddIcon("w_go_festival_winter_ep8", "Winter & Snow", "Seasons", 0, "RegularSnowflake", 3797952893573978472uL);
            AddIcon("iq_puddlesplash_ep8", "Rain & Storm", "Seasons", 0, "RegularCloudRain", 2373642721096760803uL);
            AddIcon("w_buy_umbrella_ep8", "Umbrella & Weather", "Seasons", 0, "RegularWind", 17917244511216265112uL);
            AddIcon("w_snowman_s_ep8", "Snowman & Winter", "Seasons", 0, "RegularCheck", 3747497997994096000uL);

            // Vehicles & Transportation
            AddIcon("w_car", "Car & Motors", "Vehicles", 0, "RegularCar", 14400389607023703195uL);
            AddIcon("w_motorcycle_ep2", "Motorcycle & Scooter", "Vehicles", 0, "RegularCycling", 16979660329937627150uL);
            AddIcon("w_buy_speedboat_ep10", "Boats & Yachts", "Vehicles", 0, "RegularNavigation", 5218338163242665526uL);
            AddIcon("w_buy_sailboat_ep10", "Sailboat & Marine", "Vehicles", 0, "RegularNavigation", 1645743391564244524uL);
            AddIcon("hoverboard_ep11", "Hoverboard & Sci-Fi", "Vehicles", 0, "RegularRun", 3087933494101925919uL);
            AddIcon("bb_coll_theme_ep5_equestrianretreat", "Equestrian & Riding", "Vehicles", 0, "RegularRun", 9534973812616623688uL);

            // Skills & Hobbies
            AddIcon("w_painting_skill_s", "Painting & Art", "Skills", 0, "RegularPalette", 6412869030744870251uL);
            AddIcon("w_guitar", "Guitar & Strings", "Skills", 0, "RegularMusic", 11819428873332109653uL);
            AddIcon("w_piano", "Piano & Keyboard", "Skills", 0, "RegularMusic", 102038176110452554uL);
            AddIcon("w_drum_skill", "Drums & Percussion", "Skills", 0, "RegularMusic", 6184186860299130133uL);
            AddIcon("w_bass", "Bass Guitar", "Skills", 0, "RegularMusic", 8914140118395003422uL);
            AddIcon("w_cooking_skill_s", "Cooking & Baking", "Skills", 0, "RegularDish", 14009932656682780093uL);
            AddIcon("w_gardening_skill_s", "Gardening & Plants", "Skills", 0, "RegularSpa", 2632455485532706188uL);
            AddIcon("w_handiness_skill_s", "Handiness & Tools", "Skills", 0, "RegularBandAid", 14848281363015158836uL);
            AddIcon("w_logic_skill_s", "Logic & Chess", "Skills", 0, "RegularBrain", 7288174871501280383uL);
            AddIcon("w_athletic_skill_s", "Athletics & Fitness", "Skills", 0, "RegularRun", 12588198943910802379uL);
            AddIcon("w_computer_wiz_s", "Electronics & Tech", "Skills", 0, "RegularLaptop", 8730254204249577933uL);
            AddIcon("w_book_s", "Books & Study", "Skills", 0, "RegularBook", 10836860823180711606uL);
            AddIcon("w_telescope_s", "Science & Astronomy", "Skills", 0, "RegularPlanet", 2091957692479783511uL);
            AddIcon("w_photo_skill_s", "Photography", "Skills", 134217728u, "RegularCamera", 6900002860941229217uL);
            AddIcon("w_martialarts_s", "Martial Arts", "Skills", 134217728u, "RegularTarget", 4435109264121082115uL);
            AddIcon("w_nectarmaking_career_s", "Nectar Making", "Skills", 402653184u, "RegularDrink", 17486485369906969814uL);
            AddIcon("w_sculpting_career_s", "Sculpting", "Skills", 402653184u, "RegularCuboid", 12813879718261119601uL);
            AddIcon("w_inventing_career_s", "Inventing & Steam", "Skills", 402653184u, "RegularCog", 3189919357406051680uL);
            AddIcon("w_professional_bar_ep3", "Mixology & Bars", "Skills", 0, "RegularDrink", 7528190823602898755uL);
            AddIcon("hud_underwater_skill_ep10", "Scuba Diving", "Skills", 0, "RegularWater", 9040954247178599137uL);

            // Careers & Themes
            AddIcon("w_medical_career_s", "Medical / Hospital", "Careers", 0, "RegularPlusMedical", 1597278430174395283uL);
            AddIcon("w_science_career_s", "Science Laboratory", "Careers", 0, "RegularTestTube", 11744929630006264746uL);
            AddIcon("w_criminal_career_s", "Criminal & Noir", "Careers", 0, "RegularMask", 3935251838754409359uL);
            AddIcon("w_political_career_s", "Politics & Business", "Careers", 0, "RegularBriefcase", 16167045831226547347uL);
            AddIcon("w_firefighter_career_s", "Firefighter & Rescue", "Careers", 402653184u, "RegularShield", 11971874667582875373uL);
            AddIcon("w_private_eye_career_s", "Private Detective", "Careers", 402653184u, "RegularSearch", 14581330975937924517uL);
            AddIcon("w_interior_designer_career_s", "Interior Design", "Careers", 402653184u, "RegularPencil", 5999921525794312956uL);
            AddIcon("w_stylist_career_s", "Fashion & Styling", "Careers", 402653184u, "RegularHanger", 1498699839604262234uL);
            AddIcon("w_education_career_s", "Education & School", "Careers", 402653184u, "RegularBookOpen", 11429775370998613858uL);

            // Pets & Animals
            AddIcon("w_pet_s", "Pets General", "Pets", 1207959552u, "RegularBone", 2701058264638723072uL);
            AddIcon("w_cat_play_with_toy_s", "Cat & Feline", "Pets", 1207959552u, "RegularSmile", 5647147829601312342uL);
            AddIcon("w_tell_x_dog_to_dig_up_something_s", "Dog & Canine", "Pets", 1207959552u, "RegularHeart", 8844161471515812668uL);
            AddIcon("w_wild_horse_s", "Horse & Equestrian", "Pets", 1207959552u, "RegularRun", 1974881418888673879uL);
            AddIcon("w_buy_pet_house_s", "Dog House & Kennels", "Pets", 1207959552u, "RegularHome", 4245475133420907696uL);
            AddIcon("w_buy_bird_cage_s", "Birds & Aviary", "Pets", 1207959552u, "RegularPaperPlane", 13877950684474079526uL);
            AddIcon("w_buy_a_terrarium_s", "Terrarium & Reptiles", "Pets", 1207959552u, "RegularGrid", 11845899303375539617uL);
            AddIcon("w_fishbowl_s", "Aquarium & Fish", "Pets", 0, "RegularDrop", 7278730009589027075uL);
            AddIcon("w_blessed_by_unicorn_s", "Unicorn & Magic", "Pets", 1207959552u, "RegularStar", 13914468450655970146uL);

            // World Adventures & Travel
            AddIcon("w_travel_s", "World Travel", "Adventures", 134217728u, "RegularGlobe", 850792485395769953uL);
            AddIcon("w_egypt_s", "Al Simhara / Egypt", "Adventures", 134217728u, "RegularPyramid", 11087558834100491878uL);
            AddIcon("w_france_s", "Champs Les Sims / France", "Adventures", 134217728u, "RegularMap", 17617142808003696044uL);
            AddIcon("w_china_s", "Shang Simla / China", "Adventures", 134217728u, "RegularCompass", 53116682472784526uL);
            AddIcon("w_tent_s", "Camping & Tents", "Adventures", 134217728u, "RegularLayer", 17419943269130791262uL);
            AddIcon("w_tomb_s", "Tomb Exploration", "Adventures", 134217728u, "RegularKey", 3589590227996324559uL);
            AddIcon("w_bottlenectar_s", "Nectar & Vineyard", "Adventures", 134217728u, "RegularDrink", 12629257608940546990uL);

            // General & Symbols
            AddIcon("w_simoleon_s", "Simoleons / Economy", "Symbols", 0, "RegularDollar", 11591230579183455011uL);
            AddIcon("w_romantic_s", "Romance & Heart", "Symbols", 0, "RegularHeart", 2311250787796833384uL);
            AddIcon("w_gravestone_s", "Spooky & Gothic", "Symbols", 0, "RegularMoon", 3497821097887694243uL);
            AddIcon("w_birthday_s", "Birthday & Celebrations", "Symbols", 0, "RegularGift", 4275178815378101722uL);
            AddIcon("w_gift_s", "Gifts & Packages", "Symbols", 1207959552u, "RegularGift", 3918229316566429059uL);
            AddIcon("w_baby_symbol_s", "Baby & Nursery", "Symbols", 0, "RegularSmile", 18397924720014362084uL);
            AddIcon("w_boy_s", "Male / Boy", "Symbols", 0, "RegularUser", 7343818169667166045uL);
            AddIcon("w_girl_s", "Female / Girl", "Symbols", 0, "RegularUser", 14844265476782663783uL);
            AddIcon("w_marriage_s", "Wedding & Rings", "Symbols", 0, "RegularHeart", 4383951219147006545uL);
            AddIcon("w_top_level_s", "Star / Prestige", "Symbols", 0, "RegularStar", 5253005557680388729uL);
            AddIcon("w_number_1_s", "Number 1", "Symbols", 0, "RegularCheck", 4030496396050760130uL);
            AddIcon("w_number_2_s", "Number 2", "Symbols", 0, "RegularCheck", 4033381514562617989uL);
            AddIcon("w_number_3_s", "Number 3", "Symbols", 0, "RegularCheck", 4032537089632341356uL);
            AddIcon("w_number_4_s", "Number 4", "Symbols", 0, "RegularCheck", 4027752015027313055uL);
            AddIcon("w_number_5_s", "Number 5", "Symbols", 0, "RegularCheck", 4026837221352830758uL);
        }
    }

    public string GetCustomIconsFolderPath()
    {
        string dir = Path.Combine(_optionsMonitor.CurrentValue.DocumentBaseDir ?? "", "CustomIcons");
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
        return dir;
    }

    private void LoadCustomIcons()
    {
        try
        {
            string dir = GetCustomIconsFolderPath();
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

            string metaPath = Path.Combine(dir, "icons.json");
            var metadataList = new List<CustomIconMetadata>();

            if (File.Exists(metaPath))
            {
                try
                {
                    string json = File.ReadAllText(metaPath);
                    metadataList = JsonSerializer.Deserialize<List<CustomIconMetadata>>(json) ?? new();
                }
                catch { }
            }

            _customIcons.Clear();
            var visitedIds = new HashSet<ulong>();

            foreach (var item in metadataList)
            {
                string filePath = Path.Combine(dir, item.FileName);
                if (File.Exists(filePath))
                {
                    try
                    {
                        byte[] pngBytes = File.ReadAllBytes(filePath);
                        var key = new TGI_Key(0x2F7D0004, 0, item.InstanceId);
                        _iconBytesCache[key] = pngBytes;

                        _customIcons.Add(new CollectionIconDef
                        {
                            KeyName = "custom_" + item.InstanceId.ToString("x16"),
                            DisplayName = item.Name,
                            Category = "Custom",
                            Key = key,
                            BoxIconKind = "RegularImage",
                            IconBytes = pngBytes,
                            IsCustom = true
                        });
                        visitedIds.Add(item.InstanceId);
                    }
                    catch { }
                }
            }

            // Also pick up loose PNG files starting with 0x
            var pngFiles = Directory.GetFiles(dir, "*.png");
            foreach (var pf in pngFiles)
            {
                string fn = Path.GetFileName(pf);
                if (fn.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    string hexPart = fn.Substring(2).Split('_')[0];
                    if (ulong.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong id) && !visitedIds.Contains(id))
                    {
                        try
                        {
                            byte[] pngBytes = File.ReadAllBytes(pf);
                            string name = Path.GetFileNameWithoutExtension(pf);
                            int underscore = name.IndexOf('_');
                            if (underscore >= 0) name = name.Substring(underscore + 1);

                            var key = new TGI_Key(0x2F7D0004, 0, id);
                            _iconBytesCache[key] = pngBytes;

                            _customIcons.Add(new CollectionIconDef
                            {
                                KeyName = "custom_" + id.ToString("x16"),
                                DisplayName = name,
                                Category = "Custom",
                                Key = key,
                                BoxIconKind = "RegularImage",
                                IconBytes = pngBytes,
                                IsCustom = true
                            });
                        }
                        catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Error loading custom collection icons: {ex.Message}");
        }
    }

    public async Task<CollectionIconDef> ImportCustomIconAsync(Stream imageStream, string originalFileName, string? customName = null)
    {
        string dir = GetCustomIconsFolderPath();
        string name = !string.IsNullOrWhiteSpace(customName)
            ? customName.Trim()
            : Path.GetFileNameWithoutExtension(originalFileName).Replace('_', ' ').Trim();

        if (string.IsNullOrWhiteSpace(name)) name = "Custom Icon";

        using var originalBitmap = SkiaSharp.SKBitmap.Decode(imageStream);
        if (originalBitmap == null)
        {
            throw new InvalidOperationException("Could not decode image format. Please select a valid PNG, JPG, or BMP file.");
        }

        const int targetSize = 128;
        var imageInfo = new SkiaSharp.SKImageInfo(targetSize, targetSize, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
        using var resizedBitmap = originalBitmap.Resize(imageInfo, SkiaSharp.SKFilterQuality.High);
        using var skImage = SkiaSharp.SKImage.FromBitmap(resizedBitmap ?? originalBitmap);
        using var encodedData = skImage.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        byte[] pngBytes = encodedData.ToArray();

        ulong instanceId = S3Launcher.FNV.FNV64($"PF_CUSTOM_ICON_{DateTime.UtcNow.Ticks}_{name}") & 0x7FFFFFFFFFFFFFFFL;

        string sanitized = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string savedFileName = $"0x{instanceId:x16}_{sanitized}.png";
        string savedFilePath = Path.Combine(dir, savedFileName);
        await File.WriteAllBytesAsync(savedFilePath, pngBytes);

        var key = new TGI_Key(0x2F7D0004, 0, instanceId);
        _iconBytesCache[key] = pngBytes;

        var iconDef = new CollectionIconDef
        {
            KeyName = "custom_" + instanceId.ToString("x16"),
            DisplayName = name,
            Category = "Custom",
            Key = key,
            BoxIconKind = "RegularImage",
            IconBytes = pngBytes,
            IsCustom = true
        };

        _customIcons.RemoveAll(i => i.Key.Instance == instanceId);
        _customIcons.Add(iconDef);

        SaveCustomIconsMetadata();
        await DeployCustomIconsPackageAsync();

        return iconDef;
    }

    public async Task<bool> DeleteCustomIconAsync(ulong instanceId)
    {
        try
        {
            string dir = GetCustomIconsFolderPath();
            var existing = _customIcons.FirstOrDefault(i => i.Key.Instance == instanceId);
            if (existing != null)
            {
                _customIcons.Remove(existing);
                _iconBytesCache.TryRemove(existing.Key, out _);
            }

            if (Directory.Exists(dir))
            {
                var files = Directory.GetFiles(dir, $"0x{instanceId:x16}*.png");
                foreach (var f in files)
                {
                    try { File.Delete(f); } catch { }
                }
            }

            SaveCustomIconsMetadata();
            await DeployCustomIconsPackageAsync();
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"Error deleting custom icon 0x{instanceId:x16}: {ex.Message}");
            return false;
        }
    }

    private void SaveCustomIconsMetadata()
    {
        try
        {
            string dir = GetCustomIconsFolderPath();
            if (!Directory.Exists(dir)) return;

            string metaPath = Path.Combine(dir, "icons.json");
            var list = _customIcons.Select(i =>
            {
                string sanitized = string.Concat(i.DisplayName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                return new CustomIconMetadata
                {
                    InstanceId = i.Key.Instance,
                    Name = i.DisplayName,
                    FileName = $"0x{i.Key.Instance:x16}_{sanitized}.png",
                    CreatedAt = DateTime.UtcNow
                };
            }).ToList();

            string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metaPath, json);
        }
        catch { }
    }

    public async Task DeployCustomIconsPackageAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                string s3Dir = _cacheBuilderService?.GetSims3FolderPath() ?? _optionsMonitor.CurrentValue.DocumentBaseDir;
                if (string.IsNullOrWhiteSpace(s3Dir) || !Directory.Exists(s3Dir)) return;

                string modsPackages = Path.Combine(s3Dir, "Mods", "Packages");
                string packagePath = Path.Combine(modsPackages, "PlumbobForge_CustomIcons.package");

                if (_customIcons.Count == 0)
                {
                    if (File.Exists(packagePath))
                    {
                        try { File.Delete(packagePath); } catch { }
                    }
                    return;
                }

                if (!Directory.Exists(modsPackages))
                {
                    Directory.CreateDirectory(modsPackages);
                }

                string tempFile = packagePath + ".tmp";
                if (File.Exists(tempFile)) File.Delete(tempFile);

                using (var builder = new DBPFPackageBuilder(tempFile, false))
                {
                    foreach (var icon in _customIcons)
                    {
                        if (icon.IconBytes != null && icon.IconBytes.Length > 0)
                        {
                            using var ms = new MemoryStream(icon.IconBytes, writable: false);
                            builder.AddResource(new ResourceEntry(ms, icon.Key));
                        }
                    }
                }

                if (File.Exists(packagePath)) File.Delete(packagePath);
                File.Move(tempFile, packagePath);
            }
            catch (Exception ex)
            {
                log.Warn($"Error deploying PlumbobForge_CustomIcons.package: {ex.Message}");
            }
        });
    }

    public IReadOnlyList<CollectionIconDef> GetStandardIcons()
    {
        EnsureStandardIconsInitialized();
        LoadCustomIcons();
        return _standardIcons.Concat(_customIcons).ToList();
    }
}

public class CustomIconMetadata
{
    public ulong InstanceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
