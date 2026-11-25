using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlumbobForge.Backend.Configuration;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;
using S3ForgeTools.Utils.Logging;

namespace PlumbobForge.Backend.Services;

public class Sims3HealthService
{
    private static readonly ILog log = LogManager.GetLogger(nameof(Sims3HealthService));

    private readonly AppDbContext _db;
    private readonly IOptionsMonitor<PlumbobForgeOptions> _optionsMonitor;
    private readonly CacheBuilderService _cacheBuilderService;
    private readonly LocalizationService _localizer;
    // VaultService depends on this service (through Sims3HiderService), so it is resolved only when needed.
    private readonly IServiceProvider? _services;
    private PlumbobForgeOptions Options => _optionsMonitor.CurrentValue;

    public Sims3HealthService(
        AppDbContext db,
        IOptionsMonitor<PlumbobForgeOptions> optionsMonitor,
        CacheBuilderService cacheBuilderService,
        LocalizationService localizer,
        IServiceProvider? services = null)
    {
        _db = db;
        _optionsMonitor = optionsMonitor;
        _cacheBuilderService = cacheBuilderService;
        _localizer = localizer;
        _services = services;
    }

    public string GetSims3FolderPath()
    {
        return _cacheBuilderService.GetSims3FolderPath();
    }

    #region Cache Cleaner Logic

    public async Task<List<CacheGroupInfo>> GetCacheStatusAsync()
    {
        return await Task.Run(() =>
        {
            var results = new List<CacheGroupInfo>();
            string s3Dir = GetSims3FolderPath();

            if (!Directory.Exists(s3Dir))
            {
                return results;
            }

            // 1. Core Cache Files
            var coreFiles = new[]
            {
                "CASPartCache.package",
                "compositorCache.package",
                "scriptCache.package",
                "simCompositorCache.package",
                "socialCache.package"
            };

            var coreGroup = new CacheGroupInfo
            {
                Id = "core_caches",
                Title = "Core Game Caches (5 Package Files)",
                Description = "Primary cache files: CASPartCache, compositorCache, scriptCache, simCompositorCache, and socialCache.",
                WhenToClear = "Whenever you add, update, disable, or delete mods, default replacements, sliders, or script packages.",
                WhyClear = "Forces The Sims 3 to recompile and load your latest mod changes. Prevents outdated script assemblies and stale XML tuning from persisting across play sessions.",
                WhyNotClear = "The game will rebuild these 5 files on startup, which adds a minor 5–15 second delay to your first loading screen.",
                RegenerationImpact = "Automatically regenerates on next launch (minor initial load delay).",
                IsRecommended = true,
                IsSelected = true
            };

            foreach (var name in coreFiles)
            {
                string p = Path.Combine(s3Dir, name);
                if (File.Exists(p))
                {
                    try
                    {
                        var fi = new FileInfo(p);
                        coreGroup.FilePaths.Add(p);
                        coreGroup.TotalSizeBytes += fi.Length;
                    }
                    catch { }
                }
            }
            coreGroup.FileCount = coreGroup.FilePaths.Count;
            results.Add(coreGroup);

            // 2. Featured Items (Store Previews)
            var featuredGroup = new CacheGroupInfo
            {
                Id = "featured_items",
                Title = "Featured Items (Store Ad Previews)",
                Description = "Promotional store preview images silently downloaded in the background by the launcher and game.",
                WhenToClear = "Anytime. Highly recommended for routine cleanup.",
                WhyClear = "Frees hundreds of megabytes of completely useless image clutter and speeds up directory indexing.",
                WhyNotClear = "There is no drawback to deleting these files. They are purely EA Store advertisements.",
                RegenerationImpact = "The game downloads new ads when online, but deleting never breaks any content.",
                IsRecommended = true,
                IsSelected = true
            };
            string featuredDir = Path.Combine(s3Dir, "FeaturedItems");
            ScanDirectoryFiles(featuredDir, featuredGroup);
            results.Add(featuredGroup);

            // 3. Thumbnails Cache
            var thumbGroup = new CacheGroupInfo
            {
                Id = "thumbnails",
                Title = "Thumbnails Cache (CAS & Catalog Icons)",
                Description = "CASThumbnails, ObjectThumbnails, and DownloadsThumbnails packages storing pre-rendered catalog icons.",
                WhenToClear = "When Buy/Build mode or CAS thumbnails appear blank, black, corrupted, or show outdated textures after modifying CC.",
                WhyClear = "Forces the game to redraw fresh, clean thumbnail snapshots for all items.",
                WhyNotClear = "While scrolling through Buy/Build mode or CAS for the first time after clearing, the game will generate icons on the fly, causing temporary catalog lag.",
                RegenerationImpact = "Regenerates dynamically while browsing CAS and Buy Mode catalogs.",
                IsRecommended = false,
                IsSelected = false
            };
            string thumbDir = Path.Combine(s3Dir, "Thumbnails");
            ScanDirectoryFiles(thumbDir, thumbGroup, "*.package");
            results.Add(thumbGroup);

            // 4. World Caches
            var worldGroup = new CacheGroupInfo
            {
                Id = "world_caches",
                Title = "World Caches (Custom World Data)",
                Description = "Cached terrain geometry, object instances, and Sims data for installed custom worlds.",
                WhenToClear = "When playing a custom world that suffers from graphical corruption, missing lots, or after updating a world to a new version.",
                WhyClear = "Purges stale world data and fixes terrain glitching or missing object coordinates.",
                WhyNotClear = "Custom worlds take noticeably longer to load for the first time while their geometry cache is rebuilt (can add 2–5 minutes on first load).",
                RegenerationImpact = "Regenerates when loading custom worlds.",
                IsRecommended = false,
                IsSelected = false
            };
            string worldDir = Path.Combine(s3Dir, "WorldCaches");
            ScanDirectoryFiles(worldDir, worldGroup, "*.package");
            results.Add(worldGroup);

            // 5. IGA Cache
            var igaGroup = new CacheGroupInfo
            {
                Id = "iga_cache",
                Title = "In-Game Store (IGA) Cache",
                Description = "Temporary web files and browser cache for the in-game EA Store window.",
                WhenToClear = "If the in-game store crashes, freezes, or fails to display items properly.",
                WhyClear = "Resets the embedded web browser cache and eliminates stale web tokens.",
                WhyNotClear = "If you regularly use the in-game store, clearing requires it to re-download web banners from EA servers.",
                RegenerationImpact = "Regenerates when opening the in-game store.",
                IsRecommended = false,
                IsSelected = false
            };
            string igaDir = Path.Combine(s3Dir, "IGACache");
            ScanDirectoryFiles(igaDir, igaGroup);
            results.Add(igaGroup);

            // 6. DCBackup (Safe Clean - ccmerged protected)
            var dcGroup = new CacheGroupInfo
            {
                Id = "dcbackup",
                Title = "DCBackup Package Archives (Safe Clean)",
                Description = "Redundant package duplicates extracted during Sims3Pack installation (can easily reach 5–20 GB).",
                WhenToClear = "When you need to reclaim massive amounts of disk space from old Sims3Pack installations.",
                WhyClear = "Can free gigabytes of storage without affecting installed custom content in your active save files.",
                WhyNotClear = "Do not clear if you plan to export Lots or Sims with custom Sims3Packs attached via the launcher.",
                RegenerationImpact = "Does not regenerate. Cleaned packages are safe to discard.",
                SafetyNotice = "Safety Guard: 'ccmerged.package' is strictly protected and locked against deletion to ensure Store premium items and moodlets work properly.",
                IsRecommended = false,
                IsSelected = false
            };
            string dcDir = Path.Combine(s3Dir, "DCBackup");
            if (Directory.Exists(dcDir))
            {
                try
                {
                    foreach (var f in Directory.GetFiles(dcDir, "*.package"))
                    {
                        string fileName = Path.GetFileName(f);
                        if (string.Equals(fileName, "ccmerged.package", StringComparison.OrdinalIgnoreCase))
                        {
                            continue; // NEVER delete ccmerged.package!
                        }

                        try
                        {
                            var fi = new FileInfo(f);
                            dcGroup.FilePaths.Add(f);
                            dcGroup.TotalSizeBytes += fi.Length;
                        }
                        catch { }
                    }
                    dcGroup.FileCount = dcGroup.FilePaths.Count;
                }
                catch { }
            }
            results.Add(dcGroup);

            // 7. Error & Crash Logs
            var logsGroup = new CacheGroupInfo
            {
                Id = "error_logs",
                Title = "Script & Crash Error Logs",
                Description = "ScriptError XML dumps, NRaas ErrorTrap/Overwatch logs, and crash dump files (xcpt).",
                WhenToClear = "After you have reviewed and resolved any game crashes or errors and want a clean directory.",
                WhyClear = "Keeps your main Sims 3 directory tidy and clutter-free.",
                WhyNotClear = "Do not delete if you are currently seeking help from mod authors (such as the NRaas team) who need recent ScriptError files to diagnose your issue.",
                RegenerationImpact = "New logs are generated only when errors or crashes occur.",
                IsRecommended = false,
                IsSelected = false
            };
            try
            {
                foreach (var f in Directory.GetFiles(s3Dir))
                {
                    string name = Path.GetFileName(f);
                    if (name.StartsWith("ScriptError_", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("xcpt_", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            logsGroup.FilePaths.Add(f);
                            logsGroup.TotalSizeBytes += fi.Length;
                        }
                        catch { }
                    }
                }
                logsGroup.FileCount = logsGroup.FilePaths.Count;
            }
            catch { }
            results.Add(logsGroup);

            return results;
        });
    }

    private static void ScanDirectoryFiles(string dir, CacheGroupInfo group, string searchPattern = "*.*")
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            foreach (var file in Directory.GetFiles(dir, searchPattern, SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    group.FilePaths.Add(file);
                    group.TotalSizeBytes += fi.Length;
                }
                catch { }
            }
            group.FileCount = group.FilePaths.Count;
        }
        catch { }
    }

    public async Task<CacheCleanResult> ClearCachesAsync(IEnumerable<string> groupIds)
    {
        return await Task.Run(async () =>
        {
            var result = new CacheCleanResult { Success = true };
            var activeStatuses = await GetCacheStatusAsync();
            var targetGroups = activeStatuses.Where(g => groupIds.Contains(g.Id)).ToList();

            foreach (var group in targetGroups)
            {
                foreach (var path in group.FilePaths)
                {
                    // Guard: absolute safety check for ccmerged.package
                    if (Path.GetFileName(path).Equals("ccmerged.package", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (File.Exists(path))
                    {
                        try
                        {
                            long size = new FileInfo(path).Length;
                            File.Delete(path);
                            result.FilesDeleted++;
                            result.BytesReclaimed += size;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add($"Could not delete {Path.GetFileName(path)}: {ex.Message}");
                        }
                    }
                }
            }

            return result;
        });
    }

    #endregion

    #region Simplified Actionable Conflict Engine

    private struct CompactPackageMeta
    {
        public int Id;
        public string FileName;
        public string FilePath;
        public string SetName;
        public long? SetId;
        public long? MetaEntityId;
        public string? PackageType;
        public bool IsEnabled;
        public int ResourceCount;
        public ulong ContentChecksum;
        public bool IsLoaded;
        public int LoadRank;
        public bool IsModsItem;
        public long? ModsFolderId;
        // Mods files: Resource.cfg priority and line of the pattern that loads them.
        public int CfgPriority;
        public int CfgOrder;
    }

    // The fast index reader rejected the file. The full reader says why in plain words, and may still read it when the
    // damage is one it works around (then it is a damaged package, not an unreadable one).
    private static ConflictCardModel UnreadablePackageCard(string filePath, string? fastError, PackageConflictItem item, bool isMods, bool vaultMode)
    {
        var check = PackageValidator.Check(filePath, checkResources: false);
        if (check.Readable && check.Problems.Count > 0) return DamagedPackageCard(check.Problems, item, isMods, vaultMode);

        bool isS2 = check.FatalCode == PackageProblemCode.Sims2Package || fastError?.Contains("Sims 2") == true;
        string message = check.FatalMessage ?? fastError ?? "Package header is broken or truncated and cannot be read by the game.";
        return new ConflictCardModel
        {
            Category = ConflictCardCategory.CorruptFile,
            Title = isS2 ? "Sims 2 Package Detected" : "Corrupt / Invalid Package",
            Explanation = isS2
                ? "This file is in Sims 2 package format. Placing it in The Sims 3 may crash the game on startup."
                : message,
            Recommendation = isMods
                ? "We recommend disabling or deleting this file from your Mods folder."
                : "We recommend disabling or deleting this file from your library.",
            Packages = new List<PackageConflictItem> { item },
            ConflictFingerprint = $"Corrupt:{item.PackageFileName.ToLowerInvariant()}",
            TechnicalDetails = new List<string> { fastError ?? message }
        };
    }

    // A readable package with damage: resources whose data is missing, a header the reader had to work around, or a
    // texture compositor with a bad offset. The cache build works around these in memory; the game reading the file
    // straight from the Mods folder does not.
    private static ConflictCardModel DamagedPackageCard(IReadOnlyList<PackageProblem> problems, PackageConflictItem item, bool isMods, bool vaultMode)
    {
        string what = PackageValidator.Describe(problems);
        return new ConflictCardModel
        {
            Category = ConflictCardCategory.CorruptFile,
            Title = "Damaged Package",
            Explanation = isMods
                ? $"This package is damaged ({what}). The game reads it straight from the Mods folder, so the damage is not repaired and some of its content may be missing or broken."
                : vaultMode
                    ? $"This package is damaged ({what}). Some of its content may be missing or broken."
                    : $"This package is damaged ({what}). PlumbobForge works around this when it builds the cache; the file itself is unchanged.",
            Recommendation = isMods
                ? "Re-download it from the creator, or move it into the library so the cache build can work around the damage."
                : "Re-download it from the creator to get a clean copy.",
            Packages = new List<PackageConflictItem> { item },
            ConflictFingerprint = $"Damaged:{item.PackageFileName.ToLowerInvariant()}",
            TechnicalDetails = problems.Select(p => p.Message).Take(10).ToList()
        };
    }

    private static bool TryReadPackageIndexFast(
        string filePath,
        out List<TGI_Key> keys,
        out ulong checksum,
        out string? integrityError)
    {
        keys = new List<TGI_Key>();
        checksum = 0;
        integrityError = null;

        var fi = new FileInfo(filePath);
        if (fi.Length < 96)
        {
            integrityError = $"Truncated file: size is only {fi.Length} bytes (smaller than standard DBPF header).";
            return false;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);

            Span<byte> header = stackalloc byte[96];
            stream.ReadExactly(header);

            string magic = Encoding.ASCII.GetString(header.Slice(0, 4));
            if (magic != "DBPF")
            {
                integrityError = $"Invalid header magic '{magic}'. Expected 'DBPF'.";
                return false;
            }

            uint major = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
            if (major == 1)
            {
                integrityError = "Sims 2 Package format (DBPF 1.0) detected. May crash The Sims 3.";
                return false;
            }

            uint recordCount = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(36, 4));
            uint indexSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(44, 4));
            uint indexOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(64, 4));

            if (recordCount == 0 || indexSize == 0)
            {
                return true; // Empty package
            }

            if (indexOffset + indexSize > (ulong)fi.Length)
            {
                integrityError = $"Corrupt index offset: index extends beyond file length.";
                return false;
            }

            byte[] rentedIndex = ArrayPool<byte>.Shared.Rent((int)indexSize);
            try
            {
                stream.Position = indexOffset;
                stream.ReadExactly(rentedIndex, 0, (int)indexSize);
                ReadOnlySpan<byte> indexSpan = rentedIndex.AsSpan(0, (int)indexSize);

                uint indexType = BinaryPrimitives.ReadUInt32LittleEndian(indexSpan.Slice(0, 4));
                int pos = 4;
                uint constType = 0;
                uint constGroup = 0;
                uint constInstHi = uint.MaxValue;
                uint constInstLo = uint.MaxValue;

                if ((indexType & 1) == 1) { constType = BinaryPrimitives.ReadUInt32LittleEndian(indexSpan.Slice(pos, 4)); pos += 4; }
                if ((indexType & 2) == 2) { constGroup = BinaryPrimitives.ReadUInt32LittleEndian(indexSpan.Slice(pos, 4)); pos += 4; }
                if ((indexType & 4) == 4) { constInstHi = BinaryPrimitives.ReadUInt32LittleEndian(indexSpan.Slice(pos, 4)); pos += 4; }
                if ((indexType & 8) == 8) { constInstLo = BinaryPrimitives.ReadUInt32LittleEndian(indexSpan.Slice(pos, 4)); pos += 4; }

                keys.Capacity = (int)recordCount;
                ulong xorHash = 0;

                for (int i = 0; i < recordCount; i++)
                {
                    if (pos > indexSpan.Length - 16) break;

                    uint type = (indexType & 1) != 0 ? constType : BinaryPrimitives.ReadUInt32LittleEndian(indexSpan.Slice(pos, 4));
                    if ((indexType & 1) == 0) pos += 4;

                    uint group = (indexType & 2) != 0 ? constGroup : BinaryPrimitives.ReadUInt32LittleEndian(indexSpan.Slice(pos, 4));
                    if ((indexType & 2) == 0) pos += 4;

                    uint instHi = (indexType & 4) != 0 ? constInstHi : BinaryPrimitives.ReadUInt32LittleEndian(indexSpan.Slice(pos, 4));
                    if ((indexType & 4) == 0) pos += 4;

                    uint instLo = (indexType & 8) != 0 ? constInstLo : BinaryPrimitives.ReadUInt32LittleEndian(indexSpan.Slice(pos, 4));
                    if ((indexType & 8) == 0) pos += 4;

                    ulong instance = ((ulong)instHi << 32) | instLo;

                    pos += 16;

                    if (!Sims3ResourceTags.IsBenignRepeatingResource(type))
                    {
                        var key = new TGI_Key(type, group, instance);
                        keys.Add(key);
                        xorHash ^= (ulong)type ^ ((ulong)group << 16) ^ instance;
                    }
                }

                checksum = xorHash;
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedIndex);
            }
        }
        catch (Exception ex)
        {
            integrityError = $"Failed to read package index: {ex.Message}";
            return false;
        }


    }

    public async Task<HealthScanSummary> ScanLibraryConflictsAsync(
        ITaskProgressReporter? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            var summary = new HealthScanSummary();
            string libraryDir = Options.ManagedPackageFolderPath;

            bool vaultMode = Options.EnableSingleSourceVault;
            bool hasLibraryDir = !string.IsNullOrEmpty(libraryDir) && Directory.Exists(libraryDir);

            progress?.StartStep("fetch_db", _localizer.GetString("progress.loading_db_index"), badge: null, progress: 0.0);

            // 1. Fetch metadata & sets from database
            var metaEntities = await _db.MetaEntities
                .Include(m => m.SetsEntity)
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            // Different mods may share a file name in different library sub-folders, so match the full path first.
            var metaByPath = new Dictionary<string, MetaEntity>(StringComparer.OrdinalIgnoreCase);
            var metaMap = new Dictionary<string, MetaEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var meta in metaEntities)
            {
                string? fullPath = TryGetFullPath(meta.CompleteFileName);
                if (fullPath != null) metaByPath.TryAdd(fullPath, meta);
                if (!string.IsNullOrEmpty(meta.FileName) && !metaMap.ContainsKey(meta.FileName))
                {
                    metaMap[meta.FileName] = meta;
                }
            }
            var scope = await ConfigurationScope.LoadActiveAsync(_db);
            if (scope.SetIds.Count == 0 && scope.Overrides.Count == 0 && !Options.EnableSingleSourceVault &&
                string.Equals(Options.CacheMethod, "Static", StringComparison.OrdinalIgnoreCase))
            {
                // With no active configuration the static cache holds the default sets.
                var defaultSetIds = await _db.SetsEntities.Where(s => s.IsDefault).Select(s => s.Id).ToListAsync(cancellationToken);
                scope = new ConfigurationScope(defaultSetIds.ToHashSet(), new Dictionary<long, bool>());
            }

            CompactPackageMeta[] packagesMeta;
            MetaEntity?[] packageEntities;
            List<TGI_Key>[] packageKeysList;
            var corruptCards = new ConcurrentBag<ConflictCardModel>();
            int total;

            if (vaultMode)
            {
                // Vault items live in shards, not library files: their resource keys come from the vault index.
                (packagesMeta, packageEntities, packageKeysList) = await LoadVaultPackagesAsync(metaEntities, scope, cancellationToken);
                total = packagesMeta.Length;
                summary.TotalPackagesScanned = total;
                progress?.CompleteStep("fetch_db", finalBadge: _localizer.GetString("progress.total_sets_packages_badge", 0, total));
                progress?.StartStep("scan_packages", _localizer.GetString("progress.scanning_conflicts_integrity"), badge: $"{total}/{total}", progress: 1.0);
            }
            else
            {
                // 2. Discover package files in Library directory
                var diskFiles = (hasLibraryDir ? Directory.GetFiles(libraryDir, "*.package", SearchOption.AllDirectories) : Array.Empty<string>())
                    .Where(f => !Path.GetFileName(f).StartsWith("._", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                summary.TotalPackagesScanned = diskFiles.Count;
                progress?.CompleteStep("fetch_db", finalBadge: _localizer.GetString("progress.total_sets_packages_badge", 0, diskFiles.Count));

                progress?.StartStep("scan_packages", _localizer.GetString("progress.scanning_conflicts_integrity"), badge: $"0/{diskFiles.Count}", progress: 0.0);

                packagesMeta = new CompactPackageMeta[diskFiles.Count];
                packageEntities = new MetaEntity?[diskFiles.Count];
                packageKeysList = new List<TGI_Key>[diskFiles.Count];

                int scannedCount = 0;
                total = diskFiles.Count;

                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8),
                    CancellationToken = cancellationToken
                };

                Parallel.For(0, diskFiles.Count, parallelOptions, i =>
                {
                    if (cancellationToken.IsCancellationRequested) return;

                    int current = Interlocked.Increment(ref scannedCount);
                    if (current % 100 == 0 || current == total)
                    {
                        double pct = (double)current / total;
                        progress?.UpdateStep("scan_packages", badge: $"{current}/{total}", progress: pct);
                    }

                    string filePath = diskFiles[i];
                    string fileName = Path.GetFileName(filePath);
                    string? diskFullPath = TryGetFullPath(filePath);
                    MetaEntity? meta = null;
                    if (diskFullPath == null || !metaByPath.TryGetValue(diskFullPath, out meta))
                    {
                        metaMap.TryGetValue(fileName, out meta);
                    }
                    packageEntities[i] = meta;
                    // Whether the active configuration loads the item when it is enabled. Library files missing from the
                    // database are never deployed.
                    bool isLoaded = meta != null && scope.Includes(meta);

                    string setName = meta?.SetsEntity?.Name ?? "Uncategorized";
                    long? setId = meta?.SetsEntityId;
                    long? metaId = meta?.Id;
                    string? packageType = meta?.PackageType;
                    bool isEnabled = meta?.Enabled ?? true;

                    PackageConflictItem CardItem() => new()
                    {
                        PackageFileName = fileName,
                        PackagePath = filePath,
                        SetName = setName,
                        SetId = setId,
                        MetaEntityId = metaId,
                        PackageType = packageType,
                        IsEnabled = isEnabled,
                        IsLoadedByConfiguration = isLoaded,
                        IsWinningInLoadOrder = isEnabled && isLoaded
                    };

                    if (!TryReadPackageIndexFast(filePath, out var keys, out ulong checksum, out string? error))
                    {
                        corruptCards.Add(UnreadablePackageCard(filePath, error, CardItem(), isMods: false, vaultMode));
                        return;
                    }
                    var damage = PackageValidator.Check(filePath);
                    if (damage.Readable && damage.Problems.Count > 0)
                    {
                        corruptCards.Add(DamagedPackageCard(damage.Problems, CardItem(), isMods: false, vaultMode));
                    }

                    packagesMeta[i] = new CompactPackageMeta
                    {
                        Id = i,
                        FileName = fileName,
                        FilePath = filePath,
                        SetName = setName,
                        SetId = setId,
                        MetaEntityId = metaId,
                        PackageType = packageType,
                        IsEnabled = isEnabled,
                        ResourceCount = keys.Count,
                        ContentChecksum = checksum,
                        IsLoaded = isLoaded,
                        LoadRank = int.MaxValue
                    };

                    packageKeysList[i] = keys;
                });
            }

            // Files in the game's Mods folder load next to the library and can collide with it.
            var modsCfg = ModsResourceCfg.Load(Path.Combine(_cacheBuilderService.GetSims3FolderPath(), "Mods"));
            var (modsMeta, modsEntities, modsKeys) = await LoadModsPackagesAsync(modsCfg, scope, vaultMode, corruptCards, cancellationToken);
            if (modsMeta.Length > 0)
            {
                int offset = packagesMeta.Length;
                for (int i = 0; i < modsMeta.Length; i++) modsMeta[i].Id = offset + i;
                packagesMeta = packagesMeta.Concat(modsMeta).ToArray();
                packageEntities = packageEntities.Concat(modsEntities).ToArray();
                packageKeysList = packageKeysList.Concat(modsKeys).ToArray();
                summary.TotalPackagesScanned += modsMeta.Length;
                total += modsMeta.Length;
            }
            if (packagesMeta.Length == 0 && corruptCards.IsEmpty)
            {
                return summary;
            }

            progress?.CompleteStep("scan_packages", finalBadge: _localizer.GetString("progress.items_merged_badge", total, total));
            progress?.StartStep("analyze_conflicts", _localizer.GetString("progress.grouping_problems"), badge: null, progress: 0.0);

            await AssignLoadRanksAsync(packagesMeta, packageEntities, scope, modsCfg);

            PackageConflictItem ToConflictItem(CompactPackageMeta p) => new()
            {
                PackageFileName = p.FileName,
                PackagePath = p.FilePath,
                SetName = p.SetName,
                SetId = p.SetId,
                MetaEntityId = p.MetaEntityId,
                PackageType = p.PackageType,
                IsEnabled = p.IsEnabled,
                IsLoadedByConfiguration = p.IsLoaded,
                LoadRank = p.LoadRank,
                IsModsItem = p.IsModsItem,
                ModsFolderId = p.ModsFolderId
            };

            // 3. Inverted Index: TGI_Key -> List of package IDs
            var resourceMap = new Dictionary<TGI_Key, List<int>>();
            for (int i = 0; i < packagesMeta.Length; i++)
            {
                var keys = packageKeysList[i];
                if (keys == null || keys.Count == 0) continue;

                var uniqueKeys = keys.ToHashSet();
                foreach (var key in uniqueKeys)
                {
                    if (!resourceMap.TryGetValue(key, out var list))
                    {
                        list = new List<int>(2);
                        resourceMap[key] = list;
                    }
                    list.Add(i);
                }
            }

            Array.Clear(packageKeysList, 0, packageKeysList.Length);

            // 4. Clustered Multi-File Conflict Aggregator
            var cards = new List<ConflictCardModel>(corruptCards);
            int incompCount = 0;
            int dupeCount = 0;
            int overrideCount = 0;

            // Step A: Exact Duplicates (Clustered by Checksum & ResourceCount)
            var handledDuplicatePackageIds = new HashSet<int>();
            var dupeGroups = packagesMeta
                .Where(p => p.ResourceCount > 0)
                .GroupBy(p => (p.ResourceCount, p.ContentChecksum))
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var group in dupeGroups)
            {
                var groupList = group.ToList();
                foreach (var p in groupList)
                {
                    handledDuplicatePackageIds.Add(p.Id);
                }

                dupeCount++;
                bool isIntraSet = groupList.Select(p => p.SetName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
                string setName = groupList[0].SetName;
                var samplePkg = groupList[0];

                var pkgItems = groupList.Select(ToConflictItem).ToList();

                ComputeLoadOrderWinners(pkgItems);

                string fingerprint = $"Duplicate:{string.Join("|", groupList.Select(x => x.FileName.ToLowerInvariant()).OrderBy(x => x))}";

                cards.Add(new ConflictCardModel
                {
                    Category = ConflictCardCategory.Duplicate,
                    Title = isIntraSet
                        ? $"Exact Duplicate ({groupList.Count} copies in '{setName}')"
                        : $"Exact Duplicate ({groupList.Count} copies across sets)",
                    Explanation = isIntraSet
                        ? $"All {groupList.Count} files in Set '{setName}' are identical copies with matching content checksums."
                        : $"Found {groupList.Count} identical copies of '{samplePkg.FileName}' across your sets.",
                    Recommendation = "Keep only one copy enabled to save game memory and load time.",
                    Packages = pkgItems,
                    AffectedResourceCount = samplePkg.ResourceCount,
                    AffectedSummary = $"{samplePkg.ResourceCount} identical resources",
                    IsIntraSet = isIntraSet,
                    ConflictFingerprint = fingerprint,
                    TechnicalDetails = new List<string> { $"Checksum: {samplePkg.ContentChecksum:X16}, Resources: {samplePkg.ResourceCount}" }
                });
            }

            // Step B: Mod Tuning Incompatibilities (Connected components of tuning collisions)
            var ufTuning = new DisjointSet(packagesMeta.Length);
            var tuningCollisions = new List<(TGI_Key Key, List<int> PkgIds)>();

            foreach (var (key, packageIds) in resourceMap)
            {
                if (packageIds.Count <= 1) continue;
                if (Sims3ResourceTags.IsModTuning(key.Type))
                {
                    tuningCollisions.Add((key, packageIds));
                    for (int i = 1; i < packageIds.Count; i++)
                    {
                        ufTuning.Union(packageIds[0], packageIds[i]);
                    }
                }
            }

            var tuningClusters = new Dictionary<int, (HashSet<int> PackageIds, HashSet<TGI_Key> Keys)>();
            foreach (var (key, packageIds) in tuningCollisions)
            {
                int root = ufTuning.Find(packageIds[0]);
                if (!tuningClusters.TryGetValue(root, out var cluster))
                {
                    cluster = (new HashSet<int>(), new HashSet<TGI_Key>());
                    tuningClusters[root] = cluster;
                }
                foreach (var pid in packageIds)
                {
                    cluster.PackageIds.Add(pid);
                }
                cluster.Keys.Add(key);
            }

            foreach (var (_, cluster) in tuningClusters)
            {
                // Skip if this cluster consists entirely of exact duplicates already handled
                if (cluster.PackageIds.All(pid => handledDuplicatePackageIds.Contains(pid)) &&
                    cluster.PackageIds.Select(pid => packagesMeta[pid].ContentChecksum).Distinct().Count() == 1)
                {
                    continue;
                }

                incompCount++;
                var clusterPkgs = cluster.PackageIds.Select(pid => packagesMeta[pid]).OrderBy(p => p.FileName).ToList();
                bool isIntraSet = clusterPkgs.Select(p => p.SetName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
                string setName = clusterPkgs[0].SetName;

                var distinctTuningNames = cluster.Keys
                    .Select(k => Sims3ResourceTags.GetFriendlyName(k.Type))
                    .Distinct()
                    .ToList();
                string tuningSummary = string.Join(", ", distinctTuningNames);

                var pkgItems = clusterPkgs.Select(ToConflictItem).ToList();

                ComputeLoadOrderWinners(pkgItems);

                var techLines = cluster.Keys.Select(k =>
                    $"[{Sims3ResourceTags.GetTag(k.Type)}] {k} ({Sims3ResourceTags.GetFriendlyName(k.Type)})").ToList();

                string fingerprint = $"Incompatible:{string.Join("|", clusterPkgs.Select(x => x.FileName.ToLowerInvariant()).OrderBy(x => x))}";

                cards.Add(new ConflictCardModel
                {
                    Category = ConflictCardCategory.Incompatibility,
                    Title = isIntraSet
                        ? $"Incompatible Mods ({clusterPkgs.Count} files in '{setName}')"
                        : $"Incompatible Mods ({clusterPkgs.Count} files colliding)",
                    Explanation = $"All {clusterPkgs.Count} mods modify {tuningSummary}. Because they alter the same gameplay tuning system, only one mod's changes can take effect in game.",
                    Recommendation = "Decide which mod's features you prefer and disable the others (or use 'Keep Only This').",
                    Packages = pkgItems,
                    AffectedResourceCount = cluster.Keys.Count,
                    AffectedSummary = tuningSummary,
                    IsIntraSet = isIntraSet,
                    ConflictFingerprint = fingerprint,
                    TechnicalDetails = techLines
                });
            }

            // Step C: Default Overrides (Connected components of CAS / Objects / Sliders overrides)
            var ufOverrides = new DisjointSet(packagesMeta.Length);
            var overrideCollisions = new List<(TGI_Key Key, List<int> PkgIds)>();

            foreach (var (key, packageIds) in resourceMap)
            {
                if (packageIds.Count <= 1) continue;
                if (!Sims3ResourceTags.IsModTuning(key.Type))
                {
                    overrideCollisions.Add((key, packageIds));
                    for (int i = 1; i < packageIds.Count; i++)
                    {
                        ufOverrides.Union(packageIds[0], packageIds[i]);
                    }
                }
            }

            resourceMap.Clear();

            var overrideClusters = new Dictionary<int, (HashSet<int> PackageIds, HashSet<TGI_Key> Keys)>();
            foreach (var (key, packageIds) in overrideCollisions)
            {
                int root = ufOverrides.Find(packageIds[0]);
                if (!overrideClusters.TryGetValue(root, out var cluster))
                {
                    cluster = (new HashSet<int>(), new HashSet<TGI_Key>());
                    overrideClusters[root] = cluster;
                }
                foreach (var pid in packageIds)
                {
                    cluster.PackageIds.Add(pid);
                }
                cluster.Keys.Add(key);
            }

            foreach (var (_, cluster) in overrideClusters)
            {
                // Skip if this cluster consists entirely of exact duplicates already handled
                if (cluster.PackageIds.All(pid => handledDuplicatePackageIds.Contains(pid)) &&
                    cluster.PackageIds.Select(pid => packagesMeta[pid].ContentChecksum).Distinct().Count() == 1)
                {
                    continue;
                }

                overrideCount++;
                var clusterPkgs = cluster.PackageIds.Select(pid => packagesMeta[pid]).OrderBy(p => p.FileName).ToList();
                bool isIntraSet = clusterPkgs.Select(p => p.SetName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
                string setName = clusterPkgs[0].SetName;

                var distinctOverrideNames = cluster.Keys
                    .Select(k => Sims3ResourceTags.GetFriendlyName(k.Type))
                    .Distinct()
                    .Take(3)
                    .ToList();
                string overrideSummary = string.Join(", ", distinctOverrideNames);

                var pkgItems = clusterPkgs.Select(ToConflictItem).ToList();

                ComputeLoadOrderWinners(pkgItems);

                var techLines = cluster.Keys.Select(k =>
                    $"[{Sims3ResourceTags.GetTag(k.Type)}] {k} ({Sims3ResourceTags.GetFriendlyName(k.Type)})").ToList();

                string fingerprint = $"Override:{string.Join("|", clusterPkgs.Select(x => x.FileName.ToLowerInvariant()).OrderBy(x => x))}";

                cards.Add(new ConflictCardModel
                {
                    Category = ConflictCardCategory.DefaultOverride,
                    Title = isIntraSet
                        ? $"Resource Override ({clusterPkgs.Count} files in '{setName}')"
                        : $"Resource Override ({clusterPkgs.Count} files colliding)",
                    Explanation = $"These packages contain the same {overrideSummary}. Only one copy of each reaches the game: the package marked Active in Game.",
                    Recommendation = "Usually safe to keep if one is intended to replace or recolor the other (e.g. default skins, eyes, or mesh replacements).",
                    Packages = pkgItems,
                    AffectedResourceCount = cluster.Keys.Count,
                    AffectedSummary = overrideSummary,
                    IsIntraSet = isIntraSet,
                    ConflictFingerprint = fingerprint,
                    TechnicalDetails = techLines
                });
            }


            cards = cards
                .OrderBy(card => card.Category switch
                {
                    ConflictCardCategory.CorruptFile => 0,
                    ConflictCardCategory.Incompatibility => 1,
                    ConflictCardCategory.Duplicate => 2,
                    ConflictCardCategory.DefaultOverride => 3,
                    _ => 4
                })
                .ThenBy(card => card.Title)
                .ToList();

            summary.ConflictCards = cards;
            summary.IncompatibilitiesCount = incompCount;
            summary.DuplicatesCount = dupeCount;
            summary.OverridesCount = overrideCount;
            summary.CorruptCount = corruptCards.Count;

            progress?.CompleteStep("analyze_conflicts", finalBadge: $"{summary.TotalIssuesCount} issue cards found");



            return summary;
        }, cancellationToken);
    }

    // Only one copy of a resource reaches the game: caches and vault shards keep the first copy merged in, and between
    // packages of the same priority the game keeps the first one it loads. So the lowest load rank wins, not the last.
    public static void ComputeLoadOrderWinners(IEnumerable<PackageConflictItem> items)
    {
        PackageConflictItem? winner = null;
        foreach (var item in items)
        {
            item.IsWinningInLoadOrder = false;
            if (!item.IsEnabled || !item.IsLoadedByConfiguration) continue;
            if (winner == null || item.LoadRank < winner.LoadRank) winner = item;
        }

        if (winner != null) winner.IsWinningInLoadOrder = true;
    }

    // Ranks every package the active configuration loads in the order its copy of a shared resource would be kept,
    // mirroring how CacheBuilderService and VaultService lay items out. Disabled items are ranked too, so a package
    // switched on from the scanner lands in the right place.
    private async Task AssignLoadRanksAsync(CompactPackageMeta[] packages, MetaEntity?[] entities, ConfigurationScope scope, ModsResourceCfg? modsCfg = null)
    {
        var included = Enumerable.Range(0, packages.Length)
            .Where(i => packages[i].IsLoaded && entities[i] != null && !packages[i].IsModsItem)
            .ToList();
        var includedMods = Enumerable.Range(0, packages.Length)
            .Where(i => packages[i].IsLoaded && packages[i].IsModsItem)
            .ToList();
        if (included.Count == 0 && includedMods.Count == 0) return;

        IOrderedEnumerable<int> ordered;
        if (Options.EnableSingleSourceVault)
        {
            // Resource.cfg lists the shard folders alphabetically; inside a shard the item imported first wins.
            var shardPaths = new Dictionary<long, string>();
            try
            {
                string vaultRoot = VaultService.GetVaultRootDirectory(_cacheBuilderService.GetSims3FolderPath());
                var shards = await _db.VaultShards.AsNoTracking().Include(s => s.SetsEntity).ToListAsync();
                foreach (var shard in shards)
                {
                    shardPaths[shard.Id] = VaultService.ResolveShardPath(vaultRoot, shard, shard.SetsEntity);
                }
            }
            catch
            {
                // Without shard paths the item order inside the vault still decides.
            }

            string ShardPath(int i) =>
                entities[i]!.VaultShardId is long id && shardPaths.TryGetValue(id, out var path) ? path : string.Empty;

            ordered = included
                .OrderBy(i => ShardPath(i).Length == 0 ? 1 : 0)
                .ThenBy(ShardPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => ConfigurationScope.TopLevelId(entities[i]!))
                .ThenBy(i => entities[i]!.Id);
        }
        else
        {
            // Same sort as the cache merge (first copy kept). Dynamic mode builds one cache per set, listed in
            // Resource.cfg by set id, with the configuration item cache last; static mode merges everything into one.
            bool isStatic = string.Equals(Options.CacheMethod, "Static", StringComparison.OrdinalIgnoreCase);
            long FolderOrder(MetaEntity m)
            {
                if (isStatic) return 0;
                bool inSetCache = !m.ExcludedByAnyConfig && m.SetsEntityId is long setId && scope.SetIds.Contains(setId);
                return inSetCache ? m.SetsEntityId!.Value : long.MaxValue;
            }

            ordered = included
                .OrderBy(i => FolderOrder(entities[i]!))
                .ThenBy(i => CacheBuilderService.GetPackageTypePriority(entities[i]!.PackageType))
                .ThenBy(i => entities[i]!.PackageType)
                .ThenBy(i => entities[i]!.FileName)
                .ThenBy(i => entities[i]!.Id);
        }

        // Library and Mods files together: the higher Resource.cfg priority wins; at the same priority, the line read
        // first. PlumbobForge's caches (or shards) all load from the Scan of Cache/Config, at that line's place; a set
        // with its own priority moves its items up or down from there.
        int libraryPriority = modsCfg?.CacheConfigPriority ?? 500;
        int libraryOrder = modsCfg?.CacheConfigOrder ?? -1;
        var setPriorities = await SetPriorities.LoadAsync(_db);
        var positions = new List<(int Index, int Priority, int Order, int Sequence, string Path)>();
        int seq = 0;
        foreach (int i in ordered)
        {
            int priority = libraryPriority + SetPriorities.Of(entities[i]!, setPriorities) - SetPriorities.Default;
            positions.Add((i, priority, libraryOrder, seq++, string.Empty));
        }
        foreach (int i in includedMods) positions.Add((i, packages[i].CfgPriority, packages[i].CfgOrder, 0, packages[i].FilePath));

        int rank = 0;
        foreach (var p in positions
                     .OrderByDescending(p => p.Priority)
                     .ThenBy(p => p.Order)
                     .ThenBy(p => p.Sequence)
                     .ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
        {
            packages[p.Index].LoadRank = rank++;
        }
    }

    // One entry per package file in the game's Mods folder (Sims3Packs aren't loaded from there). Loaded when the
    // active configuration lets it and a Resource.cfg line reaches it. In vault mode the keys are filtered like the
    // vault's index, so a Mods copy of a vault item shows up as an exact duplicate.
    private async Task<(CompactPackageMeta[] Packages, MetaEntity?[] Entities, List<TGI_Key>[] Keys)> LoadModsPackagesAsync(
        ModsResourceCfg cfg, ConfigurationScope scope, bool vaultMode, ConcurrentBag<ConflictCardModel> corruptCards, CancellationToken cancellationToken)
    {
        var items = await _db.AllMetaEntities.AsNoTracking()
            .Include(m => m.ModsFolder)
            .Where(m => m.IsModsItem && m.ParentMetaEntityId == null)
            .ToListAsync(cancellationToken);
        items = items
            .Where(m => ModsResourceCfg.StripDisabled(m.CompleteFileName).EndsWith(".package", StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.CompleteFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var packages = new CompactPackageMeta[items.Count];
        var keyLists = new List<TGI_Key>[items.Count];
        var readable = new bool[items.Count];
        await Task.Run(() => Parallel.For(0, items.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8), CancellationToken = cancellationToken },
            i =>
            {
                var meta = items[i];
                string folder = meta.ModsFolder?.RelativePath ?? string.Empty;
                string setName = folder.Length == 0 ? "Mods" : $"Mods/{folder}";
                string? rel = cfg.ToRelative(meta.CompleteFileName);
                var position = rel == null ? null : cfg.LoadPositionOf(rel);
                bool isLoaded = position != null && scope.Includes(meta);

                if (!File.Exists(meta.CompleteFileName)) return;
                PackageConflictItem CardItem() => new()
                {
                    PackageFileName = meta.FileName,
                    PackagePath = meta.CompleteFileName,
                    SetName = setName,
                    MetaEntityId = meta.Id,
                    PackageType = meta.PackageType,
                    IsEnabled = meta.Enabled,
                    IsLoadedByConfiguration = isLoaded,
                    IsWinningInLoadOrder = meta.Enabled && isLoaded,
                    IsModsItem = true,
                    ModsFolderId = meta.ModsFolderId
                };

                if (!TryReadPackageIndexFast(meta.CompleteFileName, out var keys, out ulong checksum, out string? error))
                {
                    corruptCards.Add(UnreadablePackageCard(meta.CompleteFileName, error, CardItem(), isMods: true, vaultMode));
                    return;
                }
                var damage = PackageValidator.Check(meta.CompleteFileName);
                if (damage.Readable && damage.Problems.Count > 0)
                {
                    corruptCards.Add(DamagedPackageCard(damage.Problems, CardItem(), isMods: true, vaultMode));
                }

                if (vaultMode)
                {
                    keys = keys.Where(k => CacheContentRules.IsLoadableType(k.Type)).ToList();
                    checksum = 0;
                    foreach (var key in keys.Distinct()) checksum ^= (ulong)key.Type ^ ((ulong)key.Group << 16) ^ key.Instance;
                }

                packages[i] = new CompactPackageMeta
                {
                    FileName = meta.FileName,
                    FilePath = meta.CompleteFileName,
                    SetName = setName,
                    SetId = null,
                    MetaEntityId = meta.Id,
                    PackageType = meta.PackageType,
                    IsEnabled = meta.Enabled,
                    ResourceCount = keys.Count,
                    ContentChecksum = checksum,
                    IsLoaded = isLoaded,
                    LoadRank = int.MaxValue,
                    IsModsItem = true,
                    ModsFolderId = meta.ModsFolderId,
                    CfgPriority = position?.Priority ?? 0,
                    CfgOrder = position?.Order ?? int.MaxValue
                };
                keyLists[i] = keys;
                readable[i] = true;
            }), cancellationToken);

        var keep = Enumerable.Range(0, items.Count).Where(i => readable[i]).ToList();
        return (keep.Select(i => packages[i]).ToArray(),
                keep.Select(i => (MetaEntity?)items[i]).ToArray(),
                keep.Select(i => keyLists[i]).ToArray());
    }

    // Vault mode: one entry per library item, with the resource keys its shard chunks hold. A Sims3Pack's parts count
    // as their parent item, which is what the library shows and what enabling or disabling acts on. Resources the
    // vault never indexes (manifests, objects switched off in Look Inside) are left out, as the game never sees them.
    private async Task<(CompactPackageMeta[] Packages, MetaEntity?[] Entities, List<TGI_Key>[] Keys)> LoadVaultPackagesAsync(
        List<MetaEntity> metaEntities, ConfigurationScope scope, CancellationToken cancellationToken)
    {
        var metaById = metaEntities.ToDictionary(m => m.Id);
        var chunks = await _db.VaultChunks.AsNoTracking()
            .Select(c => new { c.MetaEntityId, c.Type, c.Group, c.Instance })
            .ToListAsync(cancellationToken);
        var disabled = (await _db.DisabledObjects.AsNoTracking()
                .Select(d => new { d.MetaEntityId, d.Type, d.Group, d.Instance })
                .ToListAsync(cancellationToken))
            .Select(d => (d.MetaEntityId, d.Type, d.Group, d.Instance))
            .ToHashSet();

        var keysByItem = new Dictionary<long, HashSet<TGI_Key>>();
        foreach (var c in chunks)
        {
            if (!metaById.TryGetValue(c.MetaEntityId, out var holder)) continue;
            long itemId = ConfigurationScope.TopLevelId(holder);
            if (!metaById.ContainsKey(itemId)) continue;
            if (!CacheContentRules.IsLoadableType(c.Type) || Sims3ResourceTags.IsBenignRepeatingResource(c.Type)) continue;
            if (disabled.Contains((itemId, c.Type, c.Group, c.Instance))) continue;

            if (!keysByItem.TryGetValue(itemId, out var keySet))
            {
                keySet = new HashSet<TGI_Key>();
                keysByItem[itemId] = keySet;
            }
            keySet.Add(new TGI_Key(c.Type, c.Group, c.Instance));
        }

        var items = keysByItem.Keys
            .Select(id => metaById[id])
            .OrderBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Id)
            .ToList();

        var packages = new CompactPackageMeta[items.Count];
        var entities = new MetaEntity?[items.Count];
        var keyLists = new List<TGI_Key>[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var meta = items[i];
            var keys = keysByItem[meta.Id].ToList();
            ulong checksum = 0;
            foreach (var key in keys)
            {
                checksum ^= (ulong)key.Type ^ ((ulong)key.Group << 16) ^ key.Instance;
            }

            packages[i] = new CompactPackageMeta
            {
                Id = i,
                FileName = meta.FileName,
                FilePath = meta.CompleteFileName,
                SetName = meta.SetsEntity?.Name ?? "Uncategorized",
                SetId = meta.SetsEntityId,
                MetaEntityId = meta.Id,
                PackageType = meta.PackageType,
                IsEnabled = meta.Enabled,
                ResourceCount = keys.Count,
                ContentChecksum = checksum,
                IsLoaded = scope.Includes(meta),
                LoadRank = int.MaxValue
            };
            entities[i] = meta;
            keyLists[i] = keys;
        }

        return (packages, entities, keyLists);
    }

    private static string? TryGetFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    private class DisjointSet
    {
        private readonly int[] _parent;

        public DisjointSet(int size)
        {
            _parent = new int[size];
            for (int i = 0; i < size; i++) _parent[i] = i;
        }

        public int Find(int i)
        {
            if (_parent[i] == i) return i;
            return _parent[i] = Find(_parent[i]);
        }

        public void Union(int i, int j)
        {
            int rootI = Find(i);
            int rootJ = Find(j);
            if (rootI != rootJ)
            {
                _parent[rootI] = rootJ;
            }
        }
    }

    #endregion

    #region Pattern Fixer Logic

    private const uint PatternResourceType = 0xD4D9FBE5u;

    // A vault item's pattern-related resources, read from its shard instead of opening the shard as a package.
    private sealed record VaultPatternChunk(long MetaEntityId, long ShardId, uint Type, long Offset, int ChunkLength, int ResourceLength, bool IsCompressed);

    private static readonly Regex ComplateNameRegex = new(
        @"<complate\s+[^>]*name=""([^""]+)""(?:[^>]*category=""([^""]+)"")?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PatternlistNameRegex = new(
        @"<pattern\s+[^>]*name=""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<PatternScanSummary> ScanPatternsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var summary = new PatternScanSummary();

            // Mods folder files too; their own files are repaired like library files.
            var metaItems = _db.AllMetaEntities
                .AsNoTracking()
                .Include(m => m.SetsEntity)
                .Include(m => m.ModsFolder)
                .Where(m => !m.IsModsItem || m.ParentMetaEntityId == null)
                .ToList();

            summary.TotalPackagesScanned = metaItems.Count;
            var brokenList = new ConcurrentBag<PatternIssueItem>();
            var cleanList = new ConcurrentBag<PatternIssueItem>();

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                CancellationToken = cancellationToken
            };

            // A vault item's CompleteFileName is its shard, which holds many other items: vault items are read
            // through their own chunks instead.
            var vaultPatternChunks = LoadVaultPatternChunks();
            var shardPaths = LoadShardPaths(vaultPatternChunks.Values.SelectMany(l => l).Select(c => c.ShardId).ToHashSet());

            Parallel.ForEach(metaItems, parallelOptions, item =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                string filePath = item.CompleteFileName;

                try
                {
                    List<string> texts;
                    if (item.IsVaultManaged)
                    {
                        if (!vaultPatternChunks.TryGetValue(item.Id, out var chunks)) return;
                        texts = new List<string>();
                        foreach (var c in chunks)
                        {
                            if (!shardPaths.TryGetValue(c.ShardId, out var shardPath)) return;
                            try
                            {
                                texts.Add(Encoding.UTF8.GetString(DBPFVaultShard.ReadDecompressedChunk(shardPath, c.Offset, c.ChunkLength, c.ResourceLength, c.IsCompressed)));
                            }
                            catch
                            {
                                // Unreadable chunk: skip it.
                            }
                        }
                    }
                    else
                    {
                        if (!File.Exists(filePath)) return;
                        // Only inspect DBPF (.package) files directly
                        if (ModsResourceCfg.StripDisabled(filePath).EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase)) return;

                        using var pkg = new DBPFPackage(filePath);
                        // Only packages with a pattern definition resource (PTRN)
                        if (!pkg.Resources.Any(r => r.Key.Type == PatternResourceType)) return;

                        texts = new List<string>();
                        foreach (var res in pkg.Resources)
                        {
                            if (!PatternKeyRepair.AppliesTo(res.Key.Type)) continue;
                            try
                            {
                                texts.Add(Encoding.UTF8.GetString(res.Read()));
                            }
                            catch
                            {
                                // Decompression error on a single resource
                            }
                        }
                    }

                    string patternName = Path.GetFileNameWithoutExtension(item.FileName);
                    string category = "General";
                    int brokenKeysCount = 0;
                    var brokenSamples = new List<string>();

                    foreach (string text in texts)
                    {
                        // Try extract pattern name and category
                        var complateMatch = ComplateNameRegex.Match(text);
                        if (complateMatch.Success)
                        {
                            if (!string.IsNullOrWhiteSpace(complateMatch.Groups[1].Value))
                                patternName = complateMatch.Groups[1].Value.Trim();
                            if (complateMatch.Groups.Count > 2 && !string.IsNullOrWhiteSpace(complateMatch.Groups[2].Value))
                                category = complateMatch.Groups[2].Value.Trim();
                        }
                        else
                        {
                            var plMatch = PatternlistNameRegex.Match(text);
                            if (plMatch.Success && !string.IsNullOrWhiteSpace(plMatch.Groups[1].Value))
                            {
                                patternName = plMatch.Groups[1].Value.Trim();
                            }
                        }

                        var matches = PatternKeyRepair.FindBrokenKeys(text);
                        brokenKeysCount += matches.Count;
                        foreach (Match m in matches)
                        {
                            if (brokenSamples.Count < 5)
                            {
                                brokenSamples.Add(m.Value);
                            }
                        }
                    }

                    var issue = new PatternIssueItem
                    {
                        PackagePath = filePath,
                        FileName = item.FileName,
                        SetName = item.IsModsItem
                            ? (string.IsNullOrEmpty(item.ModsFolder?.RelativePath) ? "Mods" : $"Mods/{item.ModsFolder!.RelativePath}")
                            : item.SetsEntity?.Name ?? "Uncategorized",
                        SetId = item.SetsEntityId,
                        MetaEntityId = item.Id,
                        IsVaultItem = item.IsVaultManaged,
                        IsModsItem = item.IsModsItem,
                        PatternName = patternName,
                        Category = category,
                        CorruptedKeyCount = brokenKeysCount,
                        SampleBrokenKeys = brokenSamples,
                        IsBroken = brokenKeysCount > 0,
                        IsFixed = false,
                        IsSelected = brokenKeysCount > 0
                    };

                    if (brokenKeysCount > 0)
                    {
                        brokenList.Add(issue);
                    }
                    else
                    {
                        cleanList.Add(issue);
                    }
                }
                catch
                {
                    // Non-pattern, unreadable, or invalid package skipped safely
                }
            });

            summary.BrokenPatterns = brokenList
                .OrderBy(p => p.PatternName)
                .ThenBy(p => p.FileName)
                .ToList();

            summary.CleanPatterns = cleanList
                .OrderBy(p => p.PatternName)
                .ThenBy(p => p.FileName)
                .ToList();

            summary.TotalPatternsFound = summary.BrokenPatterns.Count + summary.CleanPatterns.Count;
            summary.BrokenPatternsCount = summary.BrokenPatterns.Count;
            summary.FixedPatternsCount = 0;

            return summary;
        }, cancellationToken);
    }

    // Pattern-related resources of vault items that have a pattern definition, by item.
    private Dictionary<long, List<VaultPatternChunk>> LoadVaultPatternChunks()
    {
        return _db.VaultChunks.AsNoTracking()
            .Where(c => c.MetaEntity!.IsVaultManaged
                && (c.Type == 0x0333406Cu || c.Type == PatternResourceType || c.Type == CacheContentRules.ManifestType))
            .Select(c => new { c.MetaEntityId, c.VaultShardId, c.Type, c.Offset, c.ChunkLength, c.ResourceLength, c.IsCompressed })
            .AsEnumerable()
            .Select(c => new VaultPatternChunk(c.MetaEntityId, c.VaultShardId, c.Type, c.Offset, c.ChunkLength, c.ResourceLength, c.IsCompressed))
            .GroupBy(c => c.MetaEntityId)
            .Where(g => g.Any(c => c.Type == PatternResourceType))
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    private Dictionary<long, string> LoadShardPaths(HashSet<long> shardIds)
    {
        if (shardIds.Count == 0) return new Dictionary<long, string>();
        string vaultRoot = VaultService.GetVaultRootDirectory(GetSims3FolderPath());
        return _db.VaultShards.AsNoTracking()
            .Include(s => s.SetsEntity)
            .Where(s => shardIds.Contains(s.Id))
            .AsEnumerable()
            .ToDictionary(s => s.Id, s => VaultService.ResolveShardPath(vaultRoot, s, s.SetsEntity));
    }

    // Vault items are repaired inside their shard; a library item's own file is rewritten.
    public async Task<bool> FixPatternItemAsync(PatternIssueItem item)
    {
        if (item.IsVaultItem)
        {
            if (item.MetaEntityId == null || _services?.GetService(typeof(VaultService)) is not VaultService vault) return false;
            try
            {
                return await vault.RepairPatternKeysAsync(item.MetaEntityId.Value) >= 0;
            }
            catch (Exception ex)
            {
                log.Warn($"Failed to fix vault pattern item {item.MetaEntityId}: {ex.Message}");
                return false;
            }
        }
        return await Task.Run(() => FixPatternPackage(item.PackagePath));
    }

    public async Task<PatternFixResult> FixPatternItemsAsync(IEnumerable<PatternIssueItem> items, IProgress<int>? progress = null)
    {
        var result = new PatternFixResult();
        // Vault items share their shard path, so they are told apart by item, library files by path.
        var list = items
            .GroupBy(i => i.IsVaultItem ? $"vault:{i.MetaEntityId}" : $"file:{i.PackagePath}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        int current = 0;

        foreach (var item in list)
        {
            if (await FixPatternItemAsync(item))
            {
                result.FixedCount++;
            }
            else
            {
                result.FailedCount++;
                result.Errors.Add($"Failed to repair: {item.FileName}");
            }
            current++;
            progress?.Report((int)((double)current / list.Count * 100));
        }

        result.Success = result.FailedCount == 0;
        return result;
    }

    public bool FixPatternPackage(string packagePath)
    {
        if (!File.Exists(packagePath)) return false;

        // A vault shard holds many items and its layout belongs to the database: rewriting it as a package would
        // drop every chunk outside its index and break every stored offset. Vault items go through FixPatternItemAsync.
        if (_db.MetaEntities.AsNoTracking().Any(m => m.IsVaultManaged && m.CompleteFileName == packagePath))
        {
            log.Warn($"Refusing to rewrite vault shard {packagePath} as a package");
            return false;
        }

        string tempPath = packagePath + ".tmp_fix";
        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);

            using (var pkg = new DBPFPackage(packagePath))
            {
                if (pkg.Resources.Count == 0) return false;

                using (var builder = new DBPFPackageBuilder(tempPath, pkg.IsEncrypted))
                {
                    foreach (var res in pkg.Resources)
                    {
                        if (PatternKeyRepair.AppliesTo(res.Key.Type))
                        {
                            try
                            {
                                if (PatternKeyRepair.TryRepair(res.Read(), out byte[] fixedBytes))
                                {
                                    using var ms = new MemoryStream(fixedBytes);
                                    var newRes = new ResourceEntry(ms, res.Key);
                                    if (res.IsCompressed)
                                    {
                                        try { newRes.Compress(); } catch { }
                                    }
                                    builder.AddResource(newRes);
                                    continue;
                                }
                            }
                            catch
                            {
                                // If any issue reading/fixing this resource, fall back to original
                            }
                        }

                        builder.AddResource(res);
                    }
                }
            }

            if (File.Exists(tempPath))
            {
                File.Move(tempPath, packagePath, overwrite: true);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to fix pattern package {packagePath}: {ex.Message}");
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
            return false;
        }
    }


    #endregion
}
