using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlumbobForge.Backend.Configuration;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;
using S3ForgeTools.GameFiles.TS3Pack;
using S3ForgeTools.Utils;

namespace PlumbobForge.Backend.Services;

public class CacheBuilderService
{
    private static readonly TGI_Key DollDressedKey = new(832458525u, 0u, 4064452635095512314uL);
    private static readonly byte[] ColonSpaceBytes = [(byte)':', (byte)' '];

    private enum SpecialPackageType
    {
        None,
        World,
        Lot,
        Sim,
        Pattern
    }

    private static SpecialPackageType DetectSpecialPackageType(DBPFPackage package)
    {
        foreach (var res in package.Resources)
        {
            uint t = res.Key.Type;
            if (t == 107542056) return SpecialPackageType.World;
            if (t == 3496170587u) return SpecialPackageType.Lot;
            if (t == 83396964) return SpecialPackageType.Sim;
            if (t == 0xD4D9FBE5) return SpecialPackageType.Pattern;
        }
        return SpecialPackageType.None;
    }

    private static int GetPackageTypePriority(string? packageType)
    {
        if (string.IsNullOrEmpty(packageType)) return 4;
        // Group CAS items together first so StaticBundle_0 holds CAS items for fast CAS loading
        if (packageType.Equals("CAS", StringComparison.OrdinalIgnoreCase) ||
            packageType.Equals("Hair", StringComparison.OrdinalIgnoreCase) ||
            packageType.Equals("Clothing", StringComparison.OrdinalIgnoreCase) ||
            packageType.Equals("Accessories", StringComparison.OrdinalIgnoreCase) ||
            packageType.Equals("Details", StringComparison.OrdinalIgnoreCase) ||
            packageType.Equals("Skin", StringComparison.OrdinalIgnoreCase) ||
            packageType.Equals("Skins", StringComparison.OrdinalIgnoreCase) ||
            packageType.Equals("Sliders", StringComparison.OrdinalIgnoreCase) ||
            packageType.Equals("Presets", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        // Group Build/Buy objects together second
        if (packageType.Equals("BuildBuy", StringComparison.OrdinalIgnoreCase) ||
            packageType.Equals("Object", StringComparison.OrdinalIgnoreCase) ||
            packageType.Equals("Objects", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        // Patterns
        if (packageType.Equals("Pattern", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return 4;
    }

    private static bool ShouldCompressResource(ResourceEntry r, int compressionLevel)
    {
        if (r.IsCompressed || r.Length < 64) return false;

        // Skip textures (_IMG: 0x00B2D882) and audio (AUDO: 0x01EEF63A) at low/balanced compression (level <= 2).
        // They are already internally compressed (DXT1/3/5, MP3/XAS). Compressing them with RefPack
        // wastes significant CPU during cache builds and causes in-game CAS/Buy Mode loading stutter.
        // At level >= 3 (High/Max), compress everything as requested by user.
        if (compressionLevel <= 2)
        {
            uint type = r.Key.Type;
            if (type == 0x00B2D882u || type == 0x01EEF63Au)
            {
                return false;
            }
        }

        return true;
    }

    private readonly AppDbContext _db;
    private readonly IOptionsMonitor<PlumbobForgeOptions> _optionsMonitor;
    private PlumbobForgeOptions _options => _optionsMonitor.CurrentValue;
    private readonly LocalizationService _localizer;

    private string? _cachedSims3FolderPath;

    public CacheBuilderService(AppDbContext db, IOptionsMonitor<PlumbobForgeOptions> options, LocalizationService localizer)
    {
        _db = db;
        _optionsMonitor = options;
        _localizer = localizer;
    }

    public string GetSims3FolderPath()
    {
        if (_cachedSims3FolderPath != null) return _cachedSims3FolderPath;

        string eaDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Electronic Arts");
        if (Directory.Exists(eaDir))
        {
            var candidates = Directory.GetDirectories(eaDir)
                .Where(d => Path.GetFileName(d).Contains("Sims 3", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count > 0)
            {
                var exactMatch = candidates.FirstOrDefault(d => Path.GetFileName(d).Equals("The Sims 3", StringComparison.OrdinalIgnoreCase));
                _cachedSims3FolderPath = exactMatch ?? candidates[0];
                return _cachedSims3FolderPath;
            }
        }

        // Default fallback if no existing folder is found
        _cachedSims3FolderPath = Path.Combine(eaDir, "The Sims 3");
        return _cachedSims3FolderPath;
    }

    public async Task SyncToSims3Async(Action<string>? onProgress = null, bool forceRebuildStatic = false)
    {
        try
        {
            onProgress?.Invoke(_localizer.GetString("syncing_cache_to_sims3"));

            string eaDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Electronic Arts");
            if (eaDir == null) return;

            string sims3ModsDir = Path.Combine(GetSims3FolderPath(), "Mods");
            string sims3CacheDir = Path.Combine(sims3ModsDir, "Cache");
            string sims3ConfigDir = Path.Combine(sims3CacheDir, "Config");
            string staticCacheDir = Path.Combine(sims3CacheDir, "StaticCache");

            Directory.CreateDirectory(sims3ModsDir);
            Directory.CreateDirectory(sims3CacheDir);
            Directory.CreateDirectory(sims3ConfigDir);

            EnsureMainResourceCfg(sims3ModsDir);

            bool isStatic = string.Equals(_options.CacheMethod, "Static", StringComparison.OrdinalIgnoreCase);
            if (isStatic)
            {
                string configResourceCfgStatic = Path.Combine(sims3ConfigDir, "Resource.cfg");
                using (StreamWriter sw = new StreamWriter(configResourceCfgStatic, false))
                {
                    sw.WriteLine("Priority 500");
                    sw.WriteLine("PackedFile \"../StaticCache/*.package\"");
                    sw.WriteLine("PackedFile \"../StaticCache/*/*.package\"");
                }

                // In Static mode: Clean up old dynamic set folders from Mods/Cache/ (keep only Config and StaticCache)
                foreach (var dir in Directory.GetDirectories(sims3CacheDir))
                {
                    string dirName = Path.GetFileName(dir);
                    if (!dirName.Equals("Config", StringComparison.OrdinalIgnoreCase) &&
                        !dirName.Equals("StaticCache", StringComparison.OrdinalIgnoreCase))
                    {
                        try { Directory.Delete(dir, true); } catch { }
                    }
                }

                bool staticCacheExists = Directory.Exists(staticCacheDir) && Directory.GetFiles(staticCacheDir, "StaticBundle*.package").Length > 0;
                if (forceRebuildStatic || !staticCacheExists)
                {
                    await RebuildStaticCacheAsync(onProgress);
                }
            }
            else
            {
                // In Dynamic mode: Clean up StaticCache folder if it exists
                if (Directory.Exists(staticCacheDir))
                {
                    try { Directory.Delete(staticCacheDir, true); } catch { }
                }

                // Clean up old orphaned dynamic cache sets
                var allSetsList = await _db.SetsEntities.ToListAsync();
                var allSetsMap = allSetsList.ToDictionary(s => s.Id);
                var allSetFolderNames = allSetsList.Select(s => GetSetFolderName(s, allSetsMap)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                allSetFolderNames.Add("Config");

                foreach (var dir in Directory.GetDirectories(sims3CacheDir))
                {
                    string dirName = Path.GetFileName(dir);
                    if (!allSetFolderNames.Contains(dirName))
                    {
                        try { Directory.Delete(dir, true); } catch { }
                    }
                }
            }

            var activeConfig = await _db.ConfigEntities
                .Include(c => c.ConfigSetsEntities)
                .ThenInclude(cs => cs.SetsEntity)
                .FirstOrDefaultAsync(c => c.Active);

            var targetFilesToSync = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!isStatic)
            {
                var allSetsList = await _db.SetsEntities.ToListAsync();
                var allSetsMap = allSetsList.ToDictionary(s => s.Id);

                string configResourceCfg = Path.Combine(sims3ConfigDir, "Resource.cfg");
                using (StreamWriter sw = new StreamWriter(configResourceCfg, false))
                {
                    sw.WriteLine("Priority 500");
                    if (activeConfig != null)
                    {
                        foreach (var cs in activeConfig.ConfigSetsEntities)
                        {
                            if (cs.SetsEntity != null)
                            {
                                string folderName = GetSetFolderName(cs.SetsEntity, allSetsMap);
                                sw.WriteLine($"PackedFile \"../{folderName}/*.package\"");
                                sw.WriteLine($"PackedFile \"../{folderName}/*/*.package\"");

                                string sourceSetCacheDir = GetSetPath(cs.SetsEntity, allSetsMap);
                                if (Directory.Exists(sourceSetCacheDir))
                                {
                                    string nonPackageFile = Path.Combine(sourceSetCacheDir, "NonPackageItems.txt");
                                    if (File.Exists(nonPackageFile))
                                    {
                                        foreach (var line in File.ReadAllLines(nonPackageFile))
                                        {
                                            if (string.IsNullOrWhiteSpace(line)) continue;
                                            string fileName = Path.GetFileName(line);
                                            string folder = Path.GetFileName(Path.GetDirectoryName(line)!);
                                            string destPath = GetTS3FolderPath(folder, fileName);
                                            targetFilesToSync[destPath] = line;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                string nonPackageFile = Path.Combine(staticCacheDir, "NonPackageItems.txt");
                if (File.Exists(nonPackageFile))
                {
                    foreach (var line in File.ReadAllLines(nonPackageFile))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        string fileName = Path.GetFileName(line);
                        string folder = Path.GetFileName(Path.GetDirectoryName(line)!);
                        string destPath = GetTS3FolderPath(folder, fileName);
                        targetFilesToSync[destPath] = line;
                    }
                }
            }

            // Sync SavedSims & Library (Lots) into Documents/Electronic Arts/The Sims 3/
            // Safely manage ONLY files deployed and tracked by PlumbobForge (never touch unmanaged user/game files)
            string managedNonPkgFile = Path.Combine(_options.DocumentBaseDir, "ManagedNonPackageItems.txt");
            var previouslyManagedNonPkg = File.Exists(managedNonPkgFile)
                ? File.ReadAllLines(managedNonPkgFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Clean up ONLY items that were previously deployed by PlumbobForge and are no longer in the active configuration
            foreach (var oldFile in previouslyManagedNonPkg)
            {
                if (!targetFilesToSync.ContainsKey(oldFile))
                {
                    if (File.Exists(oldFile))
                    {
                        try { File.Delete(oldFile); } catch { }
                    }
                }
            }

            // Copy missing or updated target files (SavedSims, Library)
            var currentManagedNonPkg = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in targetFilesToSync)
            {
                string destPath = kvp.Key;
                string sourcePath = kvp.Value;
                if (File.Exists(sourcePath))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        if (!File.Exists(destPath) || File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(destPath))
                        {
                            File.Copy(sourcePath, destPath, overwrite: true);
                        }
                        currentManagedNonPkg.Add(destPath);
                    }
                    catch { }
                }
            }

            // Save the updated list of files PlumbobForge actively manages in SavedSims / Library
            try
            {
                File.WriteAllLines(managedNonPkgFile, currentManagedNonPkg);
            }
            catch { }

            // Sync Worlds to {The Sims 3 installation}/GameData/Shared/NonPackaged/Worlds/
            string gameInstallDir = !string.IsNullOrWhiteSpace(_options.GameFilesDir) && Directory.Exists(_options.GameFilesDir)
                ? _options.GameFilesDir
                : GamePathValidator.AutodetectGameFilesPath();

            string? gameWorldsDir = null;
            if (!string.IsNullOrWhiteSpace(gameInstallDir))
            {
                string candidate = Path.Combine(gameInstallDir, "GameData", "Shared", "NonPackaged", "Worlds");
                if (!Directory.Exists(candidate))
                {
                    try { Directory.CreateDirectory(candidate); } catch { }
                }
                if (Directory.Exists(candidate))
                {
                    gameWorldsDir = candidate;
                }
            }

            if (gameWorldsDir != null)
            {
                var allWorlds = await _db.MetaEntities
                    .Where(m => m.PackageType == "World" || m.FileType == "WORLD" || m.FileName.EndsWith(".world"))
                    .ToListAsync();

                var activeSetIds = activeConfig?.ConfigSetsEntities.Select(cs => cs.SetsEntityId).ToHashSet() ?? new HashSet<long>();

                string managedWorldsFile = Path.Combine(_options.DocumentBaseDir, "ManagedWorlds.txt");
                var previouslyManaged = File.Exists(managedWorldsFile)
                    ? File.ReadAllLines(managedWorldsFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var currentManaged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var worldItem in allWorlds)
                {
                    string stagedWorldFile = Path.Combine(_options.DocumentBaseDir, "Worlds", Path.ChangeExtension(worldItem.FileName, ".world"));
                    if (!File.Exists(stagedWorldFile))
                    {
                        string candidateName = Path.GetFileNameWithoutExtension(worldItem.FileName);
                        string directCandidate = Path.Combine(_options.DocumentBaseDir, "Worlds", $"{candidateName}.world");
                        if (File.Exists(directCandidate)) stagedWorldFile = directCandidate;
                        else if (File.Exists(worldItem.CompleteFileName) && worldItem.CompleteFileName.EndsWith(".world", StringComparison.OrdinalIgnoreCase))
                        {
                            stagedWorldFile = worldItem.CompleteFileName;
                        }
                    }

                    string worldBaseName = File.Exists(stagedWorldFile)
                        ? Path.GetFileNameWithoutExtension(stagedWorldFile)
                        : Path.GetFileNameWithoutExtension(worldItem.FileName);

                    currentManaged.Add(worldBaseName);

                    bool isWorldEnabled = worldItem.Enabled && (worldItem.SetsEntityId.HasValue && activeSetIds.Contains(worldItem.SetsEntityId.Value));
                    string enabledPath = Path.Combine(gameWorldsDir, $"{worldBaseName}.world");
                    string disabledPath = Path.Combine(gameWorldsDir, $"{worldBaseName}.world.disabled");

                    try
                    {
                        if (isWorldEnabled)
                        {
                            if (File.Exists(disabledPath))
                            {
                                File.Move(disabledPath, enabledPath, overwrite: true);
                            }
                            else if (!File.Exists(enabledPath) && File.Exists(stagedWorldFile))
                            {
                                File.Copy(stagedWorldFile, enabledPath, overwrite: true);
                            }
                        }
                        else
                        {
                            if (File.Exists(enabledPath))
                            {
                                File.Move(enabledPath, disabledPath, overwrite: true);
                            }
                            else if (!File.Exists(disabledPath) && File.Exists(stagedWorldFile))
                            {
                                File.Copy(stagedWorldFile, disabledPath, overwrite: true);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        onProgress?.Invoke($"Failed to sync world {worldBaseName}: {ex.Message}");
                    }
                }

                // Cleanup removed custom worlds
                foreach (var delWorld in previouslyManaged)
                {
                    if (!currentManaged.Contains(delWorld))
                    {
                        try
                        {
                            string enPath = Path.Combine(gameWorldsDir, $"{delWorld}.world");
                            string disPath = Path.Combine(gameWorldsDir, $"{delWorld}.world.disabled");
                            if (File.Exists(enPath)) File.Delete(enPath);
                            if (File.Exists(disPath)) File.Delete(disPath);
                        }
                        catch { }
                    }
                }

                try
                {
                    File.WriteAllLines(managedWorldsFile, currentManaged);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"Error syncing cache to Sims 3: {ex.Message}");
        }
    }

    public async Task RebuildCacheAsync(bool forceRebuild = false, Action<string>? onProgress = null, List<(string FileName, string Reason)>? skippedFiles = null, ITaskProgressReporter? progress = null)
    {
        bool isStatic = string.Equals(_options.CacheMethod, "Static", StringComparison.OrdinalIgnoreCase);
        if (isStatic)
        {
            await RebuildStaticCacheAsync(onProgress, skippedFiles, progress);
            return;
        }

        try
        {
            string eaDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Electronic Arts");
            if (!string.IsNullOrEmpty(eaDir))
            {
                string staticDir = Path.Combine(GetSims3FolderPath(), "Mods", "Cache", "StaticCache");
                if (Directory.Exists(staticDir)) Directory.Delete(staticDir, true);
            }
        }
        catch { }

        var allSets = await _db.SetsEntities.Include(s => s.MetaEntities).ToListAsync();
        var allSetsMap = allSets.ToDictionary(s => s.Id);
        var setsToRebuild = allSets.Where(s => forceRebuild || s.Dirty).ToList();
        int total = setsToRebuild.Count;
        int current = 0;

        if (setsToRebuild.Count > 1)
        {
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
            Parallel.ForEach(setsToRebuild, parallelOptions, set =>
            {
                int index = Interlocked.Increment(ref current);
                string stepId = $"set_{set.Id}";
                int itemCount = set.MetaEntities?.Count ?? 0;
                bool shouldReport = itemCount > 0;
                string initialBadge = $"0/{itemCount}";
                if (shouldReport)
                {
                    progress?.StartStep(stepId, _localizer.GetString("progress.rebuilding_cache_for_set_title", set.Name), badge: initialBadge, progress: 0.0);
                }
                onProgress?.Invoke(_localizer.GetString("rebuilding_cache_for_set", set.Name, index, total));

                try
                {
                    RebuildSet(set, onProgress, skippedFiles, shouldReport ? progress : null, shouldReport ? stepId : null, itemCount, allSetsMap, isParallelSet: true);
                    set.CachedHash = SetDirtyTracker.ComputeContentHash(set);
                    set.Dirty = false;
                    if (shouldReport)
                    {
                        progress?.CompleteStep(stepId, finalBadge: $"{itemCount}/{itemCount}");
                    }
                }
                catch (Exception ex)
                {
                    if (shouldReport)
                    {
                        progress?.WarningStep(stepId, _localizer.GetString("progress.set_error_title", set.Name), ex.Message, new List<string> { ex.ToString() });
                    }
                    onProgress?.Invoke($"Error rebuilding set \"{set.Name}\": {ex.Message}");
                }
            });
        }
        else
        {
            foreach (var set in setsToRebuild)
            {
                current++;
                string stepId = $"set_{set.Id}";
                int itemCount = set.MetaEntities?.Count ?? 0;
                bool shouldReport = itemCount > 0;
                string initialBadge = $"0/{itemCount}";
                if (shouldReport)
                {
                    progress?.StartStep(stepId, _localizer.GetString("progress.rebuilding_cache_for_set_title", set.Name), badge: initialBadge, progress: 0.0);
                }
                onProgress?.Invoke(_localizer.GetString("rebuilding_cache_for_set", set.Name, current, total));

                try
                {
                    RebuildSet(set, onProgress, skippedFiles, shouldReport ? progress : null, shouldReport ? stepId : null, itemCount, allSetsMap, isParallelSet: false);
                    set.CachedHash = SetDirtyTracker.ComputeContentHash(set);
                    set.Dirty = false;
                    if (shouldReport)
                    {
                        progress?.CompleteStep(stepId, finalBadge: $"{itemCount}/{itemCount}");
                    }
                }
                catch (Exception ex)
                {
                    if (shouldReport)
                    {
                        progress?.WarningStep(stepId, _localizer.GetString("progress.set_error_title", set.Name), ex.Message, new List<string> { ex.ToString() });
                    }
                    onProgress?.Invoke($"Error rebuilding set \"{set.Name}\": {ex.Message}");
                }
            }
        }

        await _db.SaveChangesAsync();
    }

    public async Task RebuildStaticCacheAsync(Action<string>? onProgress = null, List<(string FileName, string Reason)>? skippedFiles = null, ITaskProgressReporter? progress = null)
    {
        onProgress?.Invoke(_localizer.GetString("rebuilding_static_cache"));
        string staticStepId = "rebuild_static_cache";
        progress?.StartStep(staticStepId, _localizer.GetString("progress.rebuilding_static_cache_title"), badge: _localizer.GetString("progress.badge_initializing"), progress: 0.0);

        string eaDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Electronic Arts");
        if (string.IsNullOrEmpty(eaDir))
        {
            progress?.WarningStep(staticStepId, "Cannot locate Electronic Arts folder", "Skipped");
            return;
        }

        string sims3CacheDir = Path.Combine(GetSims3FolderPath(), "Mods", "Cache");
        string staticCacheDir = Path.Combine(sims3CacheDir, "StaticCache");
        Directory.CreateDirectory(staticCacheDir);

        // Clean out any existing files in StaticCache before rebuilding
        try
        {
            foreach (var file in Directory.GetFiles(staticCacheDir))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }

        var activeConfig = await _db.ConfigEntities
            .Include(c => c.ConfigSetsEntities)
            .ThenInclude(cs => cs.SetsEntity!)
            .ThenInclude(s => s.MetaEntities)
            .FirstOrDefaultAsync(c => c.Active);

        var activeSets = activeConfig?.ConfigSetsEntities
            .Where(cs => cs.SetsEntity != null)
            .Select(cs => cs.SetsEntity!)
            .ToList() ?? new List<SetsEntity>();

        if (activeSets.Count == 0)
        {
            activeSets = await _db.SetsEntities
                .Include(s => s.MetaEntities)
                .Where(s => s.IsDefault)
                .ToListAsync();
        }

        // Sort items by PackageType category so that CAS items, BuildBuy items, etc., are clustered into contiguous bundles
        var allMetaItems = activeSets
            .SelectMany(s => s.MetaEntities ?? new List<MetaEntity>())
            .Where(m => m.Enabled)
            .GroupBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(m => GetPackageTypePriority(m.PackageType))
            .ThenBy(m => m.PackageType)
            .ThenBy(m => m.FileName)
            .ToList();

        int totalItems = allMetaItems.Count;
        int currentItem = 0;

        DBPFPackageBuilder? outputPkg = null;
        int packageCount = 0;
        var addedTgis = new HashSet<TGI_Key>();
        var nonPackageItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int reportInterval = Math.Max(1, totalItems / 50);
        object outputLock = new();
        object nonPkgLock = new();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
        };

        Parallel.ForEach(Partitioner.Create(allMetaItems, EnumerablePartitionerOptions.NoBuffering), parallelOptions, item =>
        {
            int current = Interlocked.Increment(ref currentItem);
            if (current % reportInterval == 0 || current == totalItems)
            {
                double pct = totalItems > 0 ? (double)current / totalItems : 1.0;
                string badge = $"{current}/{totalItems}";
                progress?.UpdateStep(staticStepId, progress: pct, badge: badge);
                onProgress?.Invoke(_localizer.GetString("merging_item", current, totalItems, "StaticCache"));
            }

            string filePath = item.CompleteFileName;
            if (!File.Exists(filePath))
            {
                var fallback = Path.Combine(_options.ManagedPackageFolderPath, item.FileName);
                if (File.Exists(fallback))
                {
                    filePath = fallback;
                }
                else
                {
                    SafeAddSkippedFile(skippedFiles, item.FileName, "File not found on disk");
                    return;
                }
            }

            if (!item.FileName.ToLower().EndsWith(".sims3pack"))
            {
                DBPFPackage? dbpfPackage = null;
                try { dbpfPackage = new DBPFPackage(filePath); }
                catch (Exception ex)
                {
                    SafeAddSkippedFile(skippedFiles, item.FileName, ex.Message);
                    return;
                }

                if (dbpfPackage != null)
                {
                    try
                    {
                        string originalName = Path.GetFileNameWithoutExtension(item.FileName);
                        var specialType = DetectSpecialPackageType(dbpfPackage);
                        switch (specialType)
                        {
                            case SpecialPackageType.World:
                            {
                                lock (nonPkgLock)
                                {
                                    string? path = InstallAsWorld(dbpfPackage, originalName, nonPackageItems);
                                    if (path != null) nonPackageItems.Add(path);
                                }
                                break;
                            }
                            case SpecialPackageType.Lot:
                            {
                                lock (nonPkgLock)
                                {
                                    string? path = InstallAsLot(dbpfPackage, originalName, nonPackageItems);
                                    if (path != null) nonPackageItems.Add(path);
                                }
                                break;
                            }
                            case SpecialPackageType.Sim:
                            {
                                lock (nonPkgLock)
                                {
                                    string? path = InstallAsSim(dbpfPackage, originalName, nonPackageItems);
                                    if (path != null) nonPackageItems.Add(path);
                                }
                                break;
                            }
                            case SpecialPackageType.Pattern:
                            {
                                lock (nonPkgLock)
                                {
                                    InstallAsPattern(dbpfPackage, originalName, staticCacheDir);
                                }
                                break;
                            }
                            default:
                            {
                                if (ValidatePackage(dbpfPackage))
                                {
                                    PreparePackageResources(dbpfPackage);
                                    lock (outputLock)
                                    {
                                        RebuildPackageStatic(ref outputPkg, ref packageCount, staticCacheDir, dbpfPackage, addedTgis, prePrepared: true);
                                    }
                                }
                                else
                                {
                                    SafeAddSkippedFile(skippedFiles, item.FileName, "Failed validation (possible corrupt data)");
                                }
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        SafeAddSkippedFile(skippedFiles, item.FileName, ex.Message);
                    }
                    finally
                    {
                        try { dbpfPackage.Close(); } catch { }
                    }
                }
            }
            else
            {
                try
                {
                    using (Sims3Pack sims3Pack = new Sims3Pack(filePath))
                    {
                        string originalName = Path.GetFileNameWithoutExtension(item.FileName);
                        var worldPkg = sims3Pack.Packages.FirstOrDefault(p => DetectSpecialPackageType(p) == SpecialPackageType.World);
                        if (worldPkg != null)
                        {
                            lock (nonPkgLock)
                            {
                                string? path = InstallAsWorld(worldPkg, originalName, nonPackageItems);
                                if (path != null) nonPackageItems.Add(path);
                            }

                            foreach (DBPFPackage package in sims3Pack.Packages)
                            {
                                if (package == worldPkg) continue;

                                var pkgType = DetectSpecialPackageType(package);
                                switch (pkgType)
                                {
                                    case SpecialPackageType.Lot:
                                    {
                                        lock (nonPkgLock)
                                        {
                                            string? lotPath = InstallAsLot(package, originalName, nonPackageItems);
                                            if (lotPath != null) nonPackageItems.Add(lotPath);
                                        }
                                        break;
                                    }
                                    case SpecialPackageType.Sim:
                                    {
                                        lock (nonPkgLock)
                                        {
                                            string? simPath = InstallAsSim(package, originalName, nonPackageItems);
                                            if (simPath != null) nonPackageItems.Add(simPath);
                                        }
                                        break;
                                    }
                                    case SpecialPackageType.Pattern:
                                    {
                                        lock (nonPkgLock)
                                        {
                                            InstallAsPattern(package, originalName, staticCacheDir);
                                        }
                                        break;
                                    }
                                    default:
                                    {
                                        if (ValidatePackage(package))
                                        {
                                            PreparePackageResources(package);
                                            lock (outputLock)
                                            {
                                                RebuildPackageStatic(ref outputPkg, ref packageCount, staticCacheDir, package, addedTgis, prePrepared: true);
                                            }
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                        else
                        {
                            foreach (DBPFPackage package in sims3Pack.Packages)
                            {
                                var pkgType = DetectSpecialPackageType(package);
                                switch (pkgType)
                                {
                                    case SpecialPackageType.Lot:
                                    {
                                        lock (nonPkgLock)
                                        {
                                            string? lotPath = InstallAsLot(package, originalName, nonPackageItems);
                                            if (lotPath != null) nonPackageItems.Add(lotPath);
                                        }
                                        break;
                                    }
                                    case SpecialPackageType.Sim:
                                    {
                                        lock (nonPkgLock)
                                        {
                                            string? simPath = InstallAsSim(package, originalName, nonPackageItems);
                                            if (simPath != null) nonPackageItems.Add(simPath);
                                        }
                                        break;
                                    }
                                    case SpecialPackageType.Pattern:
                                    {
                                        lock (nonPkgLock)
                                        {
                                            InstallAsPattern(package, originalName, staticCacheDir);
                                        }
                                        break;
                                    }
                                    default:
                                    {
                                        if (ValidatePackage(package))
                                        {
                                            PreparePackageResources(package);
                                            lock (outputLock)
                                            {
                                                RebuildPackageStatic(ref outputPkg, ref packageCount, staticCacheDir, package, addedTgis, prePrepared: true);
                                            }
                                        }
                                        else
                                        {
                                            SafeAddSkippedFile(skippedFiles, item.FileName, "Failed validation (possible corrupt data)");
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    SafeAddSkippedFile(skippedFiles, item.FileName, ex.Message);
                }
            }
        });

        if (outputPkg != null)
        {
            try { outputPkg.Close(); } catch { }
            outputPkg = null;
        }

        try
        {
            string[] oldFiles = Directory.GetFiles(staticCacheDir, "StaticBundle*.package");
            foreach (string path in oldFiles)
            {
                try { File.Delete(path); } catch { }
            }

            string[] newFiles = Directory.GetFiles(staticCacheDir, "StaticBundle*.new");
            foreach (string path in newFiles)
            {
                try
                {
                    string dest = Path.ChangeExtension(path, ".package");
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(path, dest);
                }
                catch { }
            }

            string nonPackageFile = Path.Combine(staticCacheDir, "NonPackageItems.txt");
            if (nonPackageItems.Count > 0)
            {
                try { File.WriteAllLines(nonPackageFile, nonPackageItems); } catch { }
            }
            else if (File.Exists(nonPackageFile))
            {
                try { File.Delete(nonPackageFile); } catch { }
            }

            // In Static mode: Clean up old dynamic set folders from Mods/Cache/
            if (Directory.Exists(sims3CacheDir))
            {
                foreach (var dir in Directory.GetDirectories(sims3CacheDir))
                {
                    string dirName = Path.GetFileName(dir);
                    if (!dirName.Equals("Config", StringComparison.OrdinalIgnoreCase) &&
                        !dirName.Equals("StaticCache", StringComparison.OrdinalIgnoreCase))
                    {
                        try { Directory.Delete(dir, true); } catch { }
                    }
                }
            }
        }
        catch { }

        // Update CachedHash and reset Dirty flag for all sets in database
        var allSets = await _db.SetsEntities.Include(s => s.MetaEntities).ToListAsync();
        foreach (var set in allSets)
        {
            set.CachedHash = SetDirtyTracker.ComputeContentHash(set);
            set.Dirty = false;
        }
        await _db.SaveChangesAsync();

        progress?.CompleteStep(staticStepId, finalBadge: _localizer.GetString("progress.items_merged_total_badge", totalItems));
    }

    public static string SanitizeForCache(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        string sanitized = name.Trim().Replace(' ', '-');
        var invalidChars = Path.GetInvalidFileNameChars();
        sanitized = new string(sanitized.Where(c => !invalidChars.Contains(c) && c != '/' && c != '\\').ToArray());
        while (sanitized.Contains("--"))
        {
            sanitized = sanitized.Replace("--", "-");
        }
        return sanitized.Trim('-');
    }

    public string GetSetFolderName(SetsEntity activeSet, Dictionary<long, SetsEntity>? allSetsMap = null)
    {
        if (activeSet == null) return "Default";

        if (activeSet.IsDefault || string.Equals(activeSet.Name, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return "Default";
        }
        if (activeSet.IsLegacy || string.Equals(activeSet.Name, "Legacy", StringComparison.OrdinalIgnoreCase))
        {
            return "Legacy";
        }

        var chain = new List<string>();
        var current = activeSet;
        var visited = new HashSet<long>();

        while (current != null && visited.Add(current.Id))
        {
            string clean = SanitizeForCache(!string.IsNullOrWhiteSpace(current.Name) ? current.Name : $"Set_{current.Id}");
            if (string.IsNullOrWhiteSpace(clean)) clean = $"Set_{current.Id}";
            chain.Insert(0, clean);

            if (current.ParentSetsEntityId.HasValue)
            {
                if (allSetsMap != null && allSetsMap.TryGetValue(current.ParentSetsEntityId.Value, out var parent))
                {
                    current = parent;
                }
                else
                {
                    try
                    {
                        current = _db.SetsEntities.Find(current.ParentSetsEntityId.Value);
                    }
                    catch
                    {
                        current = null;
                    }
                }
            }
            else
            {
                break;
            }
        }

        string folderName = string.Join("-", chain);
        return string.IsNullOrWhiteSpace(folderName) ? $"Set_{activeSet.Id}" : folderName;
    }

    public string GetSetPath(SetsEntity activeSet, Dictionary<long, SetsEntity>? allSetsMap = null)
    {
        if (activeSet == null) throw new ArgumentException("ActiveSet cannot be null");

        string folderName = GetSetFolderName(activeSet, allSetsMap);

        string eaDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Electronic Arts");
        if (eaDir != null)
        {
            return Path.Combine(GetSims3FolderPath(), "Mods", "Cache", folderName);
        }

        return Path.Combine(_options.SetCacheFolderPath, "Sets", folderName);
    }

    public string GetSetCachePath(string folderName)
    {
        string eaDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Electronic Arts");
        if (!string.IsNullOrEmpty(eaDir))
        {
            return Path.Combine(GetSims3FolderPath(), "Mods", "Cache", folderName);
        }
        return Path.Combine(_options.SetCacheFolderPath, "Sets", folderName);
    }

    public void RebuildSet(
        SetsEntity activeSet,
        Action<string>? onProgress = null,
        List<(string FileName, string Reason)>? skippedFiles = null,
        ITaskProgressReporter? progress = null,
        string? stepId = null,
        int totalCount = 0,
        Dictionary<long, SetsEntity>? allSetsMap = null,
        bool isParallelSet = false)
    {
        if (activeSet.IsLegacy) return;

        string setPath = GetSetPath(activeSet, allSetsMap);
        Directory.CreateDirectory(setPath);

        DBPFPackageBuilder? outputPkg = null;
        int packageCount = 0;
        var addedTgis = new HashSet<TGI_Key>();
        var nonPackageItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var metaEntities = activeSet.MetaEntities?.Where(m => m.Enabled).ToList() ?? new List<MetaEntity>();
        int totalItems = metaEntities.Count;
        int currentItem = 0;
        int reportInterval = Math.Max(1, totalItems / 50);

        object outputLock = new();
        object nonPkgLock = new();

        // If multiple sets are already running concurrently, process items inside this set sequentially
        // to prevent thread oversubscription. If only this single set is running and it has many items,
        // process items using all CPU cores in parallel.
        bool runItemsInParallel = !isParallelSet && totalItems > 10;

        void ProcessItem(MetaEntity item)
        {
            int current = Interlocked.Increment(ref currentItem);
            bool isLast = current == totalItems;
            if (current % reportInterval == 0 || isLast)
            {
                double pct = totalItems > 0 ? (double)current / totalItems : 1.0;
                string badge = $"{current}/{totalItems}";
                if (stepId != null)
                {
                    progress?.UpdateStep(stepId, progress: pct, badge: badge);
                }
                onProgress?.Invoke(_localizer.GetString("merging_item", current, totalItems, activeSet.Name));
            }

            string filePath = item.CompleteFileName;
            if (!File.Exists(filePath))
            {
                var fallback = Path.Combine(_options.ManagedPackageFolderPath, item.FileName);
                if (File.Exists(fallback))
                {
                    filePath = fallback;
                    item.CompleteFileName = fallback;
                }
                else
                {
                    SafeAddSkippedFile(skippedFiles, item.FileName, "File not found on disk");
                    onProgress?.Invoke(_localizer.GetString("skipping_missing_file", item.FileName));
                    return;
                }
            }

            if (!item.FileName.ToLower().EndsWith(".sims3pack"))
            {
                DBPFPackage? dbpfPackage = null;
                try
                {
                    dbpfPackage = new DBPFPackage(filePath);
                }
                catch (Exception ex)
                {
                    SafeAddSkippedFile(skippedFiles, item.FileName, ex.Message);
                    onProgress?.Invoke(_localizer.GetString("skipping_unreadable_file", item.FileName, ex.Message));
                    return;
                }

                if (dbpfPackage != null)
                {
                    try
                    {
                        string originalName = Path.GetFileNameWithoutExtension(item.FileName);
                        var specialType = DetectSpecialPackageType(dbpfPackage);
                        switch (specialType)
                        {
                            case SpecialPackageType.World:
                            {
                                lock (nonPkgLock)
                                {
                                    string? path = InstallAsWorld(dbpfPackage, originalName, nonPackageItems);
                                    if (path != null) nonPackageItems.Add(path);
                                }
                                break;
                            }
                            case SpecialPackageType.Lot:
                            {
                                lock (nonPkgLock)
                                {
                                    string? path = InstallAsLot(dbpfPackage, originalName, nonPackageItems);
                                    if (path != null) nonPackageItems.Add(path);
                                }
                                break;
                            }
                            case SpecialPackageType.Sim:
                            {
                                lock (nonPkgLock)
                                {
                                    string? path = InstallAsSim(dbpfPackage, originalName, nonPackageItems);
                                    if (path != null) nonPackageItems.Add(path);
                                }
                                break;
                            }
                            case SpecialPackageType.Pattern:
                            {
                                lock (nonPkgLock)
                                {
                                    InstallAsPattern(dbpfPackage, originalName, setPath);
                                }
                                break;
                            }
                            default:
                            {
                                if (ValidatePackage(dbpfPackage))
                                {
                                    PreparePackageResources(dbpfPackage);
                                    lock (outputLock)
                                    {
                                        RebuildPackage(ref outputPkg, ref packageCount, activeSet, dbpfPackage, addedTgis, allSetsMap, prePrepared: true);
                                    }
                                }
                                else
                                {
                                    SafeAddSkippedFile(skippedFiles, item.FileName, "Failed validation (possible corrupt data)");
                                    onProgress?.Invoke(_localizer.GetString("skipping_invalid_package", item.FileName));
                                }
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        SafeAddSkippedFile(skippedFiles, item.FileName, ex.Message);
                        onProgress?.Invoke(_localizer.GetString("error_processing_package", item.FileName, ex.Message));
                    }
                    finally
                    {
                        try { dbpfPackage.Close(); } catch { }
                    }
                }
            }
            else
            {
                try
                {
                    using (Sims3Pack sims3Pack = new Sims3Pack(filePath))
                    {
                        string originalName = Path.GetFileNameWithoutExtension(item.FileName);
                        var worldPkg = sims3Pack.Packages.FirstOrDefault(p => DetectSpecialPackageType(p) == SpecialPackageType.World);
                        if (worldPkg != null)
                        {
                            lock (nonPkgLock)
                            {
                                string? path = InstallAsWorld(worldPkg, originalName, nonPackageItems);
                                if (path != null) nonPackageItems.Add(path);
                            }

                            foreach (DBPFPackage package in sims3Pack.Packages)
                            {
                                if (package == worldPkg) continue;

                                var pkgType = DetectSpecialPackageType(package);
                                switch (pkgType)
                                {
                                    case SpecialPackageType.Lot:
                                    {
                                        lock (nonPkgLock)
                                        {
                                            string? lotPath = InstallAsLot(package, originalName, nonPackageItems);
                                            if (lotPath != null) nonPackageItems.Add(lotPath);
                                        }
                                        break;
                                    }
                                    case SpecialPackageType.Sim:
                                    {
                                        lock (nonPkgLock)
                                        {
                                            string? simPath = InstallAsSim(package, originalName, nonPackageItems);
                                            if (simPath != null) nonPackageItems.Add(simPath);
                                        }
                                        break;
                                    }
                                    case SpecialPackageType.Pattern:
                                    {
                                        lock (nonPkgLock)
                                        {
                                            InstallAsPattern(package, originalName, setPath);
                                        }
                                        break;
                                    }
                                    default:
                                    {
                                        if (ValidatePackage(package))
                                        {
                                            PreparePackageResources(package);
                                            lock (outputLock)
                                            {
                                                RebuildPackage(ref outputPkg, ref packageCount, activeSet, package, addedTgis, allSetsMap, prePrepared: true);
                                            }
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                        else
                        {
                            foreach (DBPFPackage package in sims3Pack.Packages)
                            {
                                var pkgType = DetectSpecialPackageType(package);
                                switch (pkgType)
                                {
                                    case SpecialPackageType.Lot:
                                    {
                                        lock (nonPkgLock)
                                        {
                                            string? lotPath = InstallAsLot(package, originalName, nonPackageItems);
                                            if (lotPath != null) nonPackageItems.Add(lotPath);
                                        }
                                        break;
                                    }
                                    case SpecialPackageType.Sim:
                                    {
                                        lock (nonPkgLock)
                                        {
                                            string? simPath = InstallAsSim(package, originalName, nonPackageItems);
                                            if (simPath != null) nonPackageItems.Add(simPath);
                                        }
                                        break;
                                    }
                                    case SpecialPackageType.Pattern:
                                    {
                                        lock (nonPkgLock)
                                        {
                                            InstallAsPattern(package, originalName, setPath);
                                        }
                                        break;
                                    }
                                    default:
                                    {
                                        if (ValidatePackage(package))
                                        {
                                            PreparePackageResources(package);
                                            lock (outputLock)
                                            {
                                                RebuildPackage(ref outputPkg, ref packageCount, activeSet, package, addedTgis, allSetsMap, prePrepared: true);
                                            }
                                        }
                                        else
                                        {
                                            SafeAddSkippedFile(skippedFiles, item.FileName, "Failed validation (possible corrupt data)");
                                            onProgress?.Invoke(_localizer.GetString("skipping_invalid_package_sims3pack", item.FileName));
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    SafeAddSkippedFile(skippedFiles, item.FileName, ex.Message);
                    onProgress?.Invoke(_localizer.GetString("skipping_unreadable_sims3pack", item.FileName, ex.Message));
                }
            }
        }

        if (runItemsInParallel)
        {
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
            };
            Parallel.ForEach(Partitioner.Create(metaEntities, EnumerablePartitionerOptions.NoBuffering), parallelOptions, ProcessItem);
        }
        else
        {
            foreach (var item in metaEntities)
            {
                ProcessItem(item);
            }
        }

        if (outputPkg != null)
        {
            try { outputPkg.Close(); } catch { }
            outputPkg = null;
        }

        try
        {
            string[] oldFiles = Directory.GetFiles(setPath, "ModBUILD*.package");
            foreach (string path in oldFiles)
            {
                try { File.Delete(path); } catch { }
            }

            string[] newFiles = Directory.GetFiles(setPath, "ModBUILD*.new");
            foreach (string path in newFiles)
            {
                try
                {
                    string dest = Path.ChangeExtension(path, ".package");
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(path, dest);
                }
                catch { }
            }
        }
        catch { }

        string nonPackageFile = Path.Combine(setPath, "NonPackageItems.txt");
        if (nonPackageItems.Count > 0)
        {
            try { File.WriteAllLines(nonPackageFile, nonPackageItems); } catch { }
        }
        else if (File.Exists(nonPackageFile))
        {
            try { File.Delete(nonPackageFile); } catch { }
        }

        activeSet.CachedHash = SetDirtyTracker.ComputeContentHash(activeSet);
        activeSet.Dirty = false;
    }

    private static void SafeAddSkippedFile(List<(string FileName, string Reason)>? skippedFiles, string fileName, string reason)
    {
        if (skippedFiles != null)
        {
            lock (skippedFiles)
            {
                skippedFiles.Add((fileName, reason));
            }
        }
    }

    private void PreparePackageResources(DBPFPackage package)
    {
        foreach (var resource in package.Resources)
        {
            if (resource.Key.Type == 3571055589u) FixPTRN(resource);
            else if (resource.Key.Type == 53690476) FixPTRN_XML(resource);
        }

        if (_options.CompressionLevel > 0)
        {
            var uncompressedResources = package.Resources
                .Where(r => ShouldCompressResource(r, _options.CompressionLevel))
                .ToList();

            if (uncompressedResources.Count == 1)
            {
                try { uncompressedResources[0].Compress(_options.CompressionLevel); } catch { }
            }
            else if (uncompressedResources.Count > 1)
            {
                var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
                Parallel.ForEach(uncompressedResources, parallelOptions, resource =>
                {
                    try { resource.Compress(_options.CompressionLevel); }
                    catch (Exception) { /* If compression fails, keep original uncompressed resource */ }
                });
            }
        }
    }

    private void RebuildPackageStatic(
        ref DBPFPackageBuilder? outputPkg,
        ref int packageCount,
        string staticCachePath,
        DBPFPackage inputPkg,
        HashSet<TGI_Key> addedTgis,
        bool prePrepared = false)
    {
        if (!prePrepared)
        {
            PreparePackageResources(inputPkg);
        }

        foreach (var resource in inputPkg.Resources)
        {
            if (!ValidateResource(resource.Key)) continue;
            if (!addedTgis.Add(resource.Key)) continue;

            try
            {
                if (outputPkg == null)
                {
                    string path = Path.Combine(staticCachePath, $"StaticBundle_{packageCount++}.new");
                    outputPkg = new DBPFPackageBuilder(path);
                }

                outputPkg.AddResource(resource);

                if (outputPkg.PackageSize >= 1073741824)
                {
                    outputPkg.Close();
                    outputPkg = null;
                }
            }
            catch (Exception) { }
        }
    }

    private string GetTS3FolderPath(string folderName, string fileName)
    {
        string subFolder = folderName;
        if (folderName.Equals("Sims", StringComparison.OrdinalIgnoreCase)) subFolder = "SavedSims";
        else if (folderName.Equals("Lots", StringComparison.OrdinalIgnoreCase)) subFolder = "Library";

        return Path.Combine(GetSims3FolderPath(), subFolder, fileName);
    }

    private void RebuildPackage(
        ref DBPFPackageBuilder? outputPkg,
        ref int packageCount,
        SetsEntity activeSet,
        DBPFPackage package,
        HashSet<TGI_Key> addedTgis,
        Dictionary<long, SetsEntity>? allSetsMap = null,
        bool prePrepared = false)
    {
        if (!prePrepared)
        {
            PreparePackageResources(package);
        }

        foreach (var resource in package.Resources)
        {
            if (!ValidateResource(resource.Key)) continue;
            if (!addedTgis.Add(resource.Key)) continue;

            try
            {
                if (outputPkg == null)
                {
                    string path = Path.Combine(GetSetPath(activeSet, allSetsMap), $"ModBUILD{packageCount++}.new");
                    outputPkg = new DBPFPackageBuilder(path);
                }

                outputPkg.AddResource(resource);

                if (outputPkg.PackageSize >= 1073741824)
                {
                    outputPkg.Close();
                    outputPkg = null;
                }
            }
            catch (Exception) { }
        }
    }

    private void FixPTRN(ResourceEntry resource)
    {
        byte[] data = resource.Read();
        if (data.AsSpan().IndexOf(ColonSpaceBytes) < 0) return;
        string text = System.Text.Encoding.UTF8.GetString(data);
        if (!text.Contains(": ")) return;

        XmlDocument xmlDocument = new XmlDocument();
        try { xmlDocument.LoadXml(text); } catch { }
        bool flag = false;
        XmlNodeList elementsByTagName = xmlDocument.GetElementsByTagName("pattern");
        foreach (XmlElement item in elementsByTagName)
        {
            string attribute = item.GetAttribute("reskey");
            if (!string.IsNullOrEmpty(attribute) && attribute.Contains(": "))
            {
                item.SetAttribute("reskey", attribute.Replace(": ", ":"));
                flag = true;
            }
        }
        if (flag)
        {
            MemoryStream memoryStream = new MemoryStream();
            xmlDocument.Save(memoryStream);
            resource.ChangeStream(memoryStream);
        }
    }

    private void FixPTRN_XML(ResourceEntry resource)
    {
        byte[] data = resource.Read();
        if (data.AsSpan().IndexOf(ColonSpaceBytes) < 0) return;
        string text = System.Text.Encoding.UTF8.GetString(data);
        if (!text.Contains(": ")) return;

        XmlDocument xmlDocument = new XmlDocument();
        try { xmlDocument.LoadXml(text); } catch { }
        bool flag = false;
        XmlNodeList elementsByTagName = xmlDocument.GetElementsByTagName("complate");
        foreach (XmlElement item in elementsByTagName)
        {
            string attribute = item.GetAttribute("reskey");
            if (!string.IsNullOrEmpty(attribute) && attribute.Contains(": "))
            {
                item.SetAttribute("reskey", attribute.Replace(": ", ":"));
                flag = true;
            }
        }
        if (flag)
        {
            MemoryStream memoryStream = new MemoryStream();
            xmlDocument.Save(memoryStream);
            resource.ChangeStream(memoryStream);
        }
    }

    private void EnsureMainResourceCfg(string sims3ModsDir)
    {
        string mainResourceCfg = Path.Combine(sims3ModsDir, "Resource.cfg");
        if (!File.Exists(mainResourceCfg))
        {
            try
            {
                using var sw = new StreamWriter(mainResourceCfg, false);
                sw.WriteLine("Priority 501");
                sw.WriteLine("Scan \"Cache/Config/\"");
                sw.WriteLine("Priority 1000");
                sw.WriteLine("PackedFile \"Overrides/*.package\"");
                sw.WriteLine("PackedFile \"Overrides/*/*.package\"");
                sw.WriteLine("PackedFile \"Overrides/*/*/*.package\"");
                sw.WriteLine("PackedFile \"Overrides/*/*/*/*.package\"");
                sw.WriteLine("Priority 500");
                sw.WriteLine("PackedFile \"Packages/*.package\"");
                sw.WriteLine("PackedFile \"Packages/*/*.package\"");
                sw.WriteLine("PackedFile \"Packages/*/*/*.package\"");
                sw.WriteLine("PackedFile \"Packages/*/*/*/*.package\"");
                sw.WriteLine("PackedFile \"Packages/*/*/*/*/*.package\"");
            }
            catch { }
        }
        else
        {
            try
            {
                string content = File.ReadAllText(mainResourceCfg);
                var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
                bool modified = false;

                // 1. Remove any erroneous "PackedFile ... Resource.cfg" entries that cause TS3 to treat
                // a text config file as a DBPF package archive, producing disk stalls and engine lag.
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    string trimmed = lines[i].Trim();
                    if (trimmed.StartsWith("PackedFile", StringComparison.OrdinalIgnoreCase) &&
                        trimmed.Contains("Resource.cfg", StringComparison.OrdinalIgnoreCase))
                    {
                        lines.RemoveAt(i);
                        modified = true;
                    }
                }

                // 2. Ensure "Scan \"Cache/Config/\"" directive exists (relative or absolute) so the game
                // engine properly includes the secondary Resource.cfg generated by PlumbobForge / CC Magic.
                bool hasScan = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    string trimmed = lines[i].Trim();
                    if (trimmed.StartsWith("Scan", StringComparison.OrdinalIgnoreCase) &&
                        (trimmed.Contains("Cache/Config", StringComparison.OrdinalIgnoreCase) ||
                         trimmed.Contains(@"Cache\Config", StringComparison.OrdinalIgnoreCase)))
                    {
                        hasScan = true;
                        // Normalize unquoted relative Scan to Scan "Cache/Config/"
                        if (!trimmed.Contains('"'))
                        {
                            lines[i] = "Scan \"Cache/Config/\"";
                            modified = true;
                        }
                        break;
                    }
                }

                if (!hasScan)
                {
                    // Insert Scan "Cache/Config/" near the top of the file
                    int insertIdx = lines.FindIndex(l => l.Trim().StartsWith("Priority", StringComparison.OrdinalIgnoreCase));
                    if (insertIdx < 0) insertIdx = 0;
                    lines.Insert(insertIdx, "Scan \"Cache/Config/\"");
                    modified = true;
                }

                // 3. Ensure Priority 501 is set for the Scan or Priority 500+ exists
                bool hasPriority501 = lines.Any(l => l.Trim().Equals("Priority 501", StringComparison.OrdinalIgnoreCase));
                if (!hasPriority501)
                {
                    int scanIdx = lines.FindIndex(l => l.Trim().StartsWith("Scan", StringComparison.OrdinalIgnoreCase) &&
                        l.Contains("Cache/Config", StringComparison.OrdinalIgnoreCase));
                    if (scanIdx >= 0)
                    {
                        lines.Insert(scanIdx, "Priority 501");
                        modified = true;
                    }
                }

                if (modified)
                {
                    File.WriteAllLines(mainResourceCfg, lines);
                }
            }
            catch { }
        }
    }

    private string? InstallAsWorld(DBPFPackage package, string defaultName, ISet<string> nonPackageItems)
    {
        string worldName = GetWorldName(package, defaultName);
        string worldsDir = Path.Combine(_options.DocumentBaseDir, "Worlds");
        Directory.CreateDirectory(worldsDir);

        string basePath = Path.Combine(worldsDir, $"{worldName}.world");
        string path = basePath;
        int count = 1;
        while (nonPackageItems.Contains(path))
        {
            path = Path.Combine(worldsDir, $"{worldName}_{count}.world");
            count++;
        }

        package.Export(path);
        return path;
    }

    private string? InstallAsLot(DBPFPackage package, string defaultName, ISet<string> nonPackageItems)
    {
        string lotName = GetLotName(package, defaultName);
        string lotsDir = Path.Combine(_options.DocumentBaseDir, "Lots");
        Directory.CreateDirectory(lotsDir);

        string basePath = Path.Combine(lotsDir, $"{lotName}.package");
        string path = basePath;
        int count = 1;
        while (nonPackageItems.Contains(path))
        {
            path = Path.Combine(lotsDir, $"{lotName}_{count}.package");
            count++;
        }

        package.Export(path);
        return path;
    }

    private string? InstallAsSim(DBPFPackage package, string defaultName, ISet<string> nonPackageItems)
    {
        string simName = GetSimName(package, defaultName);
        string simsDir = Path.Combine(_options.DocumentBaseDir, "Sims");
        Directory.CreateDirectory(simsDir);

        string basePath = Path.Combine(simsDir, $"{simName}.sim");
        string path = basePath;
        int count = 1;
        while (nonPackageItems.Contains(path))
        {
            path = Path.Combine(simsDir, $"{simName}_{count}.sim");
            count++;
        }

        package.Export(path);
        return path;
    }

    private void InstallAsPattern(DBPFPackage package, string defaultName, string outputDir)
    {
        string patternName = GetPatternName(package, defaultName);
        Directory.CreateDirectory(outputDir);
        string path = Path.Combine(outputDir, $"Pattern_{patternName}.package");
        package.Export(path);
    }

    private string GetPatternName(DBPFPackage package, string defaultName)
    {
        string name = defaultName;
        ResourceEntry? resourceEntry = package.Resources.Find((ResourceEntry r) => r.Key.Type == 3571055589u);
        if (resourceEntry != null)
        {
            byte[] bytes = resourceEntry.Read();
            string @string = System.Text.Encoding.UTF8.GetString(bytes);
            XmlDocument xmlDocument = new XmlDocument();
            try
            {
                xmlDocument.LoadXml(@string);
                XmlNode? xmlNode = xmlDocument.SelectSingleNode("/pattern/name");
                if (xmlNode != null && !string.IsNullOrEmpty(xmlNode.InnerText))
                {
                    name = xmlNode.InnerText;
                }
            }
            catch (Exception)
            {
            }
        }
        name = SanitizeFileName(name);
        return name;
    }

    private string GetSimName(DBPFPackage package, string defaultName)
    {
        string name = defaultName;
        ResourceEntry? resourceEntry = package.Resources.Find((ResourceEntry r) => r.Key.Type == 83396964);
        if (resourceEntry != null)
        {
            byte[] bytes = resourceEntry.Read();
            string @string = System.Text.Encoding.Unicode.GetString(bytes);
            name = @string.Split(new char[1])[0];
        }
        name = SanitizeFileName(name);
        return name;
    }

    private string GetWorldName(DBPFPackage package, string defaultName)
    {
        string text = defaultName;
        ResourceEntry? resourceEntry = package.Resources.Find((ResourceEntry r) => r.Key.Type == 107542056);
        if (resourceEntry != null)
        {
            byte[] bytes = resourceEntry.Read();
            string @string = System.Text.Encoding.Unicode.GetString(bytes);
            text = @string.Split(new char[1])[0];
        }
        text = SanitizeFileName(text);
        return text;
    }

    private string GetLotName(DBPFPackage package, string defaultName)
    {
        string text = defaultName;
        ResourceEntry? resourceEntry = package.Resources.Find((ResourceEntry r) => r.Key.Type == 3496170587u);
        if (resourceEntry != null)
        {
            byte[] bytes = resourceEntry.Read();
            string @string = System.Text.Encoding.Unicode.GetString(bytes);
            text = @string.Split(new char[1])[0];
        }
        text = SanitizeFileName(text);
        return text;
    }

    private string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private bool ValidateResource(TGI_Key key)
    {
        if (key.Type == 1944665835) return false;
        return true;
    }

    private bool ValidatePackage(DBPFPackage package)
    {
        foreach (var resource in package.Resources)
        {
            if (resource.Key.Equals(DollDressedKey)) return false;
        }
        return true;
    }
}
