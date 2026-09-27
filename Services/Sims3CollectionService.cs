using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlumbobForge.Backend.Configuration;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;
using S3ForgeTools.GameFiles.TS3Pack;
using S3ForgeTools.Utils.Logging;

namespace PlumbobForge.Backend.Services;

public class Sims3CollectionService
{
    private static readonly ILog log = LogManager.GetLogger(nameof(Sims3CollectionService));

    private readonly AppDbContext _db;
    private readonly CacheBuilderService _cacheBuilderService;
    private readonly IOptionsMonitor<PlumbobForgeOptions> _optionsMonitor;
    private readonly Sims3HiderService _hiderService;

    private static readonly List<CollectionIconDef> _standardIcons = new();
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
                    else
                    {
                        var found = Directory.GetFiles(gameDir, "FullBuild*.package", SearchOption.AllDirectories);
                        if (found.Length > 0)
                        {
                            packagesToScan.AddRange(found);
                        }
                        else
                        {
                            packagesToScan.AddRange(Directory.GetFiles(gameDir, "*.package", SearchOption.AllDirectories).Take(20));
                        }
                    }

                    foreach (var pkgPath in packagesToScan)
                    {
                        try
                        {
                            using var pkg = new DBPFPackage(pkgPath);
                            var iconResources = pkg.Resources.Where(r => r.Key.Type == 0x2F7D0004).ToList();
                            foreach (var res in iconResources)
                            {
                                if ((standardKeys.Count == 0 || standardKeys.Contains(res.Key)) && !_iconBytesCache.ContainsKey(res.Key))
                                {
                                    _iconBytesCache[res.Key] = res.Read();
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

            // Populate IconBytes on standard icons
            foreach (var def in _standardIcons)
            {
                if (def.IconBytes == null && _iconBytesCache.TryGetValue(def.Key, out var bytes))
                {
                    def.IconBytes = bytes;
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

    private void EnsureStandardIconsInitialized()
    {
        if (_standardIcons.Count > 0) return;

        lock (_standardIcons)
        {
            if (_standardIcons.Count > 0) return;

            void AddIcon(string key, string display, string cat, uint group, ulong inst, string boxIcon)
            {
                _standardIcons.Add(new CollectionIconDef
                {
                    KeyName = key,
                    DisplayName = display,
                    Category = cat,
                    Key = new TGI_Key(0x2F7D0004, group, inst),
                    BoxIconKind = boxIcon
                });
            }

            // Furniture & Rooms
            AddIcon("w_home_s", "Home / House", "Furniture", 0, 13431223140568206672uL, "RegularHome");
            AddIcon("w_couch_potato_s", "Sofa / Living", "Furniture", 0, 349898087017155377uL, "RegularFolder");
            AddIcon("w_sleep_s", "Bed / Bedroom", "Furniture", 0, 17233111300066886534uL, "RegularBed");
            AddIcon("w_clean_s", "Bath / Cleaning", "Furniture", 0, 8237267956809811776uL, "RegularBath");
            AddIcon("w_eat_s", "Dining / Kitchen", "Furniture", 0, 13405665210905376829uL, "RegularRestaurant");
            AddIcon("w_party_s", "Party & Entertainment", "Furniture", 0, 1364584565409985451uL, "RegularDrink");
            AddIcon("w_game_s", "Gaming & Arcade", "Furniture", 0, 6975360638405831355uL, "RegularJoystick");
            AddIcon("w_ride_playground_s", "Kids & Playground", "Furniture", 0, 15898644979653680901uL, "RegularPlay");

            // Skills & Hobbies
            AddIcon("w_painting_skill_s", "Painting & Art", "Skills", 0, 6412869030744870251uL, "RegularPalette");
            AddIcon("w_guitar_skill_s", "Music & Instruments", "Skills", 0, 9469911055023900453uL, "RegularMusic");
            AddIcon("w_cooking_skill_s", "Cooking & Baking", "Skills", 0, 14009932656682780093uL, "RegularDish");
            AddIcon("w_gardening_skill_s", "Gardening & Plants", "Skills", 0, 2632455485532706188uL, "RegularSpa");
            AddIcon("w_handiness_skill_s", "Handiness & Tools", "Skills", 0, 14848281363015158836uL, "RegularBandAid");
            AddIcon("w_logic_skill_s", "Logic & Chess", "Skills", 0, 7288174871501280383uL, "RegularBrain");
            AddIcon("w_athletic_skill_s", "Athletics & Fitness", "Skills", 0, 12588198943910802379uL, "RegularRun");
            AddIcon("w_computer_wiz_s", "Electronics & Tech", "Skills", 0, 8730254204249577933uL, "RegularLaptop");
            AddIcon("w_book_s", "Books & Study", "Skills", 0, 10836860823180711606uL, "RegularBook");
            AddIcon("w_telescope_s", "Science & Astronomy", "Skills", 0, 2091957692479783511uL, "RegularPlanet");
            AddIcon("w_photo_skill_s", "Photography", "Skills", 134217728u, 6900002860941229217uL, "RegularCamera");
            AddIcon("w_martialarts_s", "Martial Arts", "Skills", 134217728u, 4435109264121082115uL, "RegularTarget");
            AddIcon("w_nectarmaking_career_s", "Nectar Making", "Skills", 402653184u, 17486485369906969814uL, "RegularDrink");
            AddIcon("w_sculpting_career_s", "Sculpting", "Skills", 402653184u, 12813879718261119601uL, "RegularCuboid");
            AddIcon("w_inventing_career_s", "Inventing & Steam", "Skills", 402653184u, 3189919357406051680uL, "RegularCog");

            // Careers & Themes
            AddIcon("w_medical_career_s", "Medical / Hospital", "Careers", 0, 1597278430174395283uL, "RegularPlusMedical");
            AddIcon("w_science_career_s", "Science Laboratory", "Careers", 0, 11744929630006264746uL, "RegularTestTube");
            AddIcon("w_criminal_career_s", "Criminal & Noir", "Careers", 0, 3935251838754409359uL, "RegularMask");
            AddIcon("w_political_career_s", "Politics & Business", "Careers", 0, 16167045831226547347uL, "RegularBriefcase");
            AddIcon("w_firefighter_career_s", "Firefighter & Rescue", "Careers", 402653184u, 11971874667582875373uL, "RegularShield");
            AddIcon("w_private_eye_career_s", "Private Detective", "Careers", 402653184u, 14581330975937924517uL, "RegularSearch");
            AddIcon("w_interior_designer_career_s", "Interior Design", "Careers", 402653184u, 5999921525794312956uL, "RegularPencil");
            AddIcon("w_stylist_career_s", "Fashion & Styling", "Careers", 402653184u, 1498699839604262234uL, "RegularHanger");
            AddIcon("w_ghosthunter_career_s", "Ghost Hunter", "Careers", 402653184u, 16581782598328850621uL, "RegularGhost");
            AddIcon("w_education_career_s", "Education & School", "Careers", 402653184u, 11429775370998613858uL, "RegularBookOpen");

            // Pets & Animals
            AddIcon("w_pet_s", "Pets General", "Pets", 1207959552u, 2701058264638723072uL, "RegularBone");
            AddIcon("w_cat_play_with_toy_s", "Cat & Feline", "Pets", 1207959552u, 5647147829601312342uL, "RegularSmile");
            AddIcon("w_tell_x_dog_to_dig_up_something_s", "Dog & Canine", "Pets", 1207959552u, 8844161471515812668uL, "RegularHeart");
            AddIcon("w_wild_horse_s", "Horse & Equestrian", "Pets", 1207959552u, 1974881418888673879uL, "RegularRun");
            AddIcon("w_buy_pet_house_s", "Dog House & Kennels", "Pets", 1207959552u, 4245475133420907696uL, "RegularHome");
            AddIcon("w_buy_bird_cage_s", "Birds & Aviary", "Pets", 1207959552u, 13877950684474079526uL, "RegularPaperPlane");
            AddIcon("w_buy_a_terrarium_s", "Terrarium & Reptiles", "Pets", 1207959552u, 11845899303375539617uL, "RegularGrid");
            AddIcon("w_fishbowl_s", "Aquarium & Fish", "Pets", 0, 7278730009589027075uL, "RegularDrop");
            AddIcon("w_blessed_by_unicorn_s", "Unicorn & Magic", "Pets", 1207959552u, 13914468450655970146uL, "RegularStar");

            // World Adventures & Travel
            AddIcon("w_travel_s", "World Travel", "Adventures", 134217728u, 850792485395769953uL, "RegularGlobe");
            AddIcon("w_egypt_s", "Al Simhara / Egypt", "Adventures", 134217728u, 11087558834100491878uL, "RegularPyramid");
            AddIcon("w_france_s", "Champs Les Sims / France", "Adventures", 134217728u, 17617142808003696044uL, "RegularMap");
            AddIcon("w_china_s", "Shang Simla / China", "Adventures", 134217728u, 53116682472784526uL, "RegularCompass");
            AddIcon("w_tent_s", "Camping & Tents", "Adventures", 134217728u, 17419943269130791262uL, "RegularLayer");
            AddIcon("w_tomb_s", "Tomb Exploration", "Adventures", 134217728u, 3589590227996324559uL, "RegularKey");
            AddIcon("w_bottlenectar_s", "Nectar & Vineyard", "Adventures", 134217728u, 12629257608940546990uL, "RegularDrink");

            // General & Symbols
            AddIcon("w_simoleon_s", "Simoleons / Economy", "Symbols", 0, 11591230579183455011uL, "RegularDollar");
            AddIcon("w_romantic_s", "Romance & Heart", "Symbols", 0, 2311250787796833384uL, "RegularHeart");
            AddIcon("w_gravestone_s", "Spooky & Gothic", "Symbols", 0, 3497821097887694243uL, "RegularMoon");
            AddIcon("w_birthday_s", "Birthday & Celebrations", "Symbols", 0, 4275178815378101722uL, "RegularGift");
            AddIcon("w_gift_s", "Gifts & Packages", "Symbols", 1207959552u, 3918229316566429059uL, "RegularGift");
            AddIcon("w_baby_symbol_s", "Baby & Nursery", "Symbols", 0, 18397924720014362084uL, "RegularSmile");
            AddIcon("w_boy_s", "Male / Boy", "Symbols", 0, 7343818169667166045uL, "RegularUser");
            AddIcon("w_girl_s", "Female / Girl", "Symbols", 0, 14844265476782663783uL, "RegularUser");
            AddIcon("w_marriage_s", "Wedding & Rings", "Symbols", 0, 4383951219147006545uL, "RegularHeart");
            AddIcon("w_top_level_s", "Star / Prestige", "Symbols", 0, 5253005557680388729uL, "RegularStar");
            AddIcon("w_number_1_s", "Number 1", "Symbols", 0, 4030496396050760130uL, "RegularCheck");
            AddIcon("w_number_2_s", "Number 2", "Symbols", 0, 4033381514562617989uL, "RegularCheck");
            AddIcon("w_number_3_s", "Number 3", "Symbols", 0, 4032537089632341356uL, "RegularCheck");
            AddIcon("w_number_4_s", "Number 4", "Symbols", 0, 4027752015027313055uL, "RegularCheck");
            AddIcon("w_number_5_s", "Number 5", "Symbols", 0, 4026837221352830758uL, "RegularCheck");
        }
    }

    public IReadOnlyList<CollectionIconDef> GetStandardIcons()
    {
        EnsureStandardIconsInitialized();
        return _standardIcons;
    }
}
