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
    private PlumbobForgeOptions Options => _optionsMonitor.CurrentValue;

    public Sims3HealthService(
        AppDbContext db,
        IOptionsMonitor<PlumbobForgeOptions> optionsMonitor,
        CacheBuilderService cacheBuilderService,
        LocalizationService localizer)
    {
        _db = db;
        _optionsMonitor = optionsMonitor;
        _cacheBuilderService = cacheBuilderService;
        _localizer = localizer;
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

                    ulong instance = instLo | ((ulong)instInstLo(instHi, instLo));

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

        static ulong instInstLo(uint hi, uint lo) => lo | ((ulong)hi << 32);
    }

    public async Task<HealthScanSummary> ScanLibraryConflictsAsync(
        ITaskProgressReporter? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            var summary = new HealthScanSummary();
            string libraryDir = Options.ManagedPackageFolderPath;

            if (string.IsNullOrEmpty(libraryDir) || !Directory.Exists(libraryDir))
            {
                return summary;
            }

            progress?.StartStep("fetch_db", _localizer.GetString("progress.loading_db_index"), badge: null, progress: 0.0);

            // 1. Fetch metadata & sets from database
            var metaEntities = await _db.MetaEntities
                .Include(m => m.SetsEntity)
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            var metaMap = new Dictionary<string, MetaEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var meta in metaEntities)
            {
                if (!string.IsNullOrEmpty(meta.FileName) && !metaMap.ContainsKey(meta.FileName))
                {
                    metaMap[meta.FileName] = meta;
                }
            }

            // 2. Discover package files in Library directory
            var diskFiles = Directory.GetFiles(libraryDir, "*.package", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).StartsWith("._", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            summary.TotalPackagesScanned = diskFiles.Count;
            progress?.CompleteStep("fetch_db", finalBadge: _localizer.GetString("progress.total_sets_packages_badge", 0, diskFiles.Count));

            progress?.StartStep("scan_packages", _localizer.GetString("progress.scanning_conflicts_integrity"), badge: $"0/{diskFiles.Count}", progress: 0.0);

            var packagesMeta = new CompactPackageMeta[diskFiles.Count];
            var packageKeysList = new List<TGI_Key>[diskFiles.Count];
            var corruptCards = new ConcurrentBag<ConflictCardModel>();

            int scannedCount = 0;
            int total = diskFiles.Count;

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
                metaMap.TryGetValue(fileName, out var meta);

                string setName = meta?.SetsEntity?.Name ?? "Uncategorized";
                long? setId = meta?.SetsEntityId;
                long? metaId = meta?.Id;
                string? packageType = meta?.PackageType;
                bool isEnabled = meta?.Enabled ?? true;

                if (!TryReadPackageIndexFast(filePath, out var keys, out ulong checksum, out string? error))
                {
                    bool isS2 = error?.Contains("Sims 2") == true;
                    var corruptPkg = new PackageConflictItem
                    {
                        PackageFileName = fileName,
                        PackagePath = filePath,
                        SetName = setName,
                        SetId = setId,
                        MetaEntityId = metaId,
                        PackageType = packageType,
                        IsEnabled = isEnabled,
                        IsWinningInLoadOrder = isEnabled
                    };
                    corruptCards.Add(new ConflictCardModel
                    {
                        Category = ConflictCardCategory.CorruptFile,
                        Title = isS2 ? "Sims 2 Package Detected" : "Corrupt / Invalid Package",
                        Explanation = isS2
                            ? "This file is in Sims 2 package format. Placing it in The Sims 3 may crash the game on startup."
                            : (error ?? "Package header is broken or truncated and cannot be read by the game."),
                        Recommendation = "We recommend disabling or deleting this file from your library.",
                        Packages = new List<PackageConflictItem> { corruptPkg },
                        ConflictFingerprint = $"Corrupt:{fileName.ToLowerInvariant()}",
                        TechnicalDetails = new List<string> { error ?? "DBPF format error" }
                    });
                    return;
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
                    ContentChecksum = checksum
                };

                packageKeysList[i] = keys;
            });

            progress?.CompleteStep("scan_packages", finalBadge: _localizer.GetString("progress.items_merged_badge", total, total));
            progress?.StartStep("analyze_conflicts", _localizer.GetString("progress.grouping_problems"), badge: null, progress: 0.0);

            // 3. Inverted Index: TGI_Key -> List of package IDs
            var resourceMap = new Dictionary<TGI_Key, List<int>>();
            for (int i = 0; i < diskFiles.Count; i++)
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

                var pkgItems = groupList.Select(p => new PackageConflictItem
                {
                    PackageFileName = p.FileName,
                    PackagePath = p.FilePath,
                    SetName = p.SetName,
                    SetId = p.SetId,
                    MetaEntityId = p.MetaEntityId,
                    PackageType = p.PackageType,
                    IsEnabled = p.IsEnabled
                }).ToList();

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

                var pkgItems = clusterPkgs.Select(p => new PackageConflictItem
                {
                    PackageFileName = p.FileName,
                    PackagePath = p.FilePath,
                    SetName = p.SetName,
                    SetId = p.SetId,
                    MetaEntityId = p.MetaEntityId,
                    PackageType = p.PackageType,
                    IsEnabled = p.IsEnabled
                }).ToList();

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

                var pkgItems = clusterPkgs.Select(p => new PackageConflictItem
                {
                    PackageFileName = p.FileName,
                    PackagePath = p.FilePath,
                    SetName = p.SetName,
                    SetId = p.SetId,
                    MetaEntityId = p.MetaEntityId,
                    PackageType = p.PackageType,
                    IsEnabled = p.IsEnabled
                }).ToList();

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
                    Explanation = $"These packages contain the same {overrideSummary}. The package loaded last will override the earlier ones in game.",
                    Recommendation = "Usually safe to keep if one is intended to replace or recolor the other (e.g. default skins, eyes, or mesh replacements).",
                    Packages = pkgItems,
                    AffectedResourceCount = cluster.Keys.Count,
                    AffectedSummary = overrideSummary,
                    IsIntraSet = isIntraSet,
                    ConflictFingerprint = fingerprint,
                    TechnicalDetails = techLines
                });
            }

            cards.AddRange(corruptCards);
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

            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            return summary;
        }, cancellationToken);
    }

        private static void ComputeLoadOrderWinners(List<PackageConflictItem> items)
    {
        var enabledItems = items.Where(x => x.IsEnabled).ToList();
        foreach (var item in items)
        {
            item.IsWinningInLoadOrder = false;
        }

        if (enabledItems.Count == 0) return;

        var winner = enabledItems
            .OrderByDescending(p => p.PackagePath.Contains("overrides", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.PackageFileName, StringComparer.OrdinalIgnoreCase)
            .First();

        winner.IsWinningInLoadOrder = true;
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

    private static readonly Regex PatternKeyRegex = new(
        @"((?:key:|<reskey>\s*\d+\s*:)\s*[0-9a-fA-F]{8}\s*:\s*[0-9a-fA-F]{8}\s*):\s+([0-9a-fA-F]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

            var metaItems = _db.MetaEntities
                .AsNoTracking()
                .Include(m => m.SetsEntity)
                .ToList();

            summary.TotalPackagesScanned = metaItems.Count;
            var brokenList = new ConcurrentBag<PatternIssueItem>();
            var cleanList = new ConcurrentBag<PatternIssueItem>();

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                CancellationToken = cancellationToken
            };

            Parallel.ForEach(metaItems, parallelOptions, item =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                string filePath = item.CompleteFileName;
                if (!File.Exists(filePath)) return;

                // Only inspect DBPF (.package) files directly
                if (filePath.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase)) return;

                try
                {
                    using var pkg = new DBPFPackage(filePath);
                    if (pkg.Resources.Count == 0) return;

                    // Check if package has pattern definition resource (Type 0xD4D9FBE5 - PTRN)
                    bool hasPatternResource = pkg.Resources.Any(r => r.Key.Type == 0xD4D9FBE5);

                    if (!hasPatternResource) return;

                    string patternName = Path.GetFileNameWithoutExtension(item.FileName);
                    string category = "General";
                    int brokenKeysCount = 0;
                    var brokenSamples = new List<string>();

                    foreach (var res in pkg.Resources)
                    {
                        // Inspect XML resources: 0x0333406C, 0xD4D9FBE5, 0x73E93EEB
                        if (res.Key.Type == 0x0333406C || res.Key.Type == 0xD4D9FBE5 || res.Key.Type == 0x73E93EEB)
                        {
                            try
                            {
                                byte[] bytes = res.Read();
                                string text = Encoding.UTF8.GetString(bytes);

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

                                var matches = PatternKeyRegex.Matches(text);
                                if (matches.Count > 0)
                                {
                                    brokenKeysCount += matches.Count;
                                    foreach (Match m in matches)
                                    {
                                        if (brokenSamples.Count < 5)
                                        {
                                            brokenSamples.Add(m.Value);
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // Decompression or parsing error on single resource
                            }
                        }
                    }

                    var issue = new PatternIssueItem
                    {
                        PackagePath = filePath,
                        FileName = item.FileName,
                        SetName = item.SetsEntity?.Name ?? "Uncategorized",
                        SetId = item.SetsEntityId,
                        MetaEntityId = item.Id,
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

    public bool FixPatternPackage(string packagePath)
    {
        if (!File.Exists(packagePath)) return false;

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
                        if (res.Key.Type == 0x0333406C || res.Key.Type == 0xD4D9FBE5 || res.Key.Type == 0x73E93EEB)
                        {
                            try
                            {
                                byte[] bytes = res.Read();
                                string text = Encoding.UTF8.GetString(bytes);

                                if (PatternKeyRegex.IsMatch(text))
                                {
                                    string fixedText = PatternKeyRegex.Replace(text, "$1:$2");
                                    byte[] fixedBytes = Encoding.UTF8.GetBytes(fixedText);

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

    public async Task<PatternFixResult> FixPatternPackagesAsync(IEnumerable<string> packagePaths, IProgress<int>? progress = null)
    {
        return await Task.Run(() =>
        {
            var result = new PatternFixResult();
            var pathsList = packagePaths.Distinct().ToList();
            int current = 0;

            foreach (var path in pathsList)
            {
                bool ok = FixPatternPackage(path);
                if (ok)
                {
                    result.FixedCount++;
                }
                else
                {
                    result.FailedCount++;
                    result.Errors.Add($"Failed to repair: {Path.GetFileName(path)}");
                }
                current++;
                progress?.Report((int)((double)current / pathsList.Count * 100));
            }

            result.Success = result.FailedCount == 0;
            return result;
        });
    }

    #endregion
}
