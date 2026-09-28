using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                    sw.WriteLine(@"PackedFile ../StaticCache/*.package");
                    sw.WriteLine(@"PackedFile ../StaticCache/*/*.package");
                    sw.WriteLine(@"PackedFile ../StaticCache/*/*/*.package");
                    sw.WriteLine(@"PackedFile ../StaticCache/*/*/*/*.package");
                    sw.WriteLine(@"PackedFile ../StaticCache/*/*/*/*/*.package");
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
                                sw.WriteLine($"PackedFile ../{folderName}/*.package");
                                sw.WriteLine($"PackedFile ../{folderName}/*/*.package");
                                sw.WriteLine($"PackedFile ../{folderName}/*/*/*.package");
                                sw.WriteLine($"PackedFile ../{folderName}/*/*/*/*.package");
                                sw.WriteLine($"PackedFile ../{folderName}/*/*/*/*/*.package");

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
            string managedNonPkgFile = Path.Combine(_options.DocumentBaseDir, "ManagedNonPackageItems.txt");
            var previouslyManagedNonPkg = File.Exists(managedNonPkgFile)
                ? File.ReadAllLines(managedNonPkgFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };
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
                    RebuildSet(set, onProgress, skippedFiles, shouldReport ? progress : null, shouldReport ? stepId : null, itemCount, allSetsMap);
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
                    RebuildSet(set, onProgress, skippedFiles, shouldReport ? progress : null, shouldReport ? stepId : null, itemCount, allSetsMap);
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

        var allMetaItems = activeSets
            .SelectMany(s => s.MetaEntities ?? new List<MetaEntity>())
            .Where(m => m.Enabled)
            .GroupBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        int totalItems = allMetaItems.Count;
        int currentItem = 0;

        DBPFPackageBuilder? outputPkg = null;
        int packageCount = 0;
        var addedTgis = new HashSet<TGI_Key>();
        var nonPackageItems = new List<string>();

        int reportInterval = Math.Max(1, totalItems / 50);

        foreach (var item in allMetaItems)
        {
            currentItem++;
            if (currentItem % reportInterval == 0 || currentItem == totalItems)
            {
                double pct = totalItems > 0 ? (double)currentItem / totalItems : 1.0;
                string badge = $"{currentItem}/{totalItems}";
                progress?.UpdateStep(staticStepId, progress: pct, badge: badge);
                onProgress?.Invoke(_localizer.GetString("merging_item", currentItem, totalItems, "StaticCache"));
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
                    continue;
                }
            }

            if (!item.FileName.ToLower().EndsWith(".sims3pack"))
            {
                DBPFPackage? dbpfPackage = null;
                try { dbpfPackage = new DBPFPackage(filePath); }
                catch (Exception ex)
                {
                    SafeAddSkippedFile(skippedFiles, item.FileName, ex.Message);
                    continue;
                }

                if (dbpfPackage != null)
                {
                    try
                    {
                        string originalName = Path.GetFileNameWithoutExtension(item.FileName);
                        if (dbpfPackage.Resources.Any(r => r.Key.Type == 107542056))
                        {
                            string path = InstallAsWorld(dbpfPackage, originalName, nonPackageItems);
                            if (path != null) nonPackageItems.Add(path);
                        }
                        else if (dbpfPackage.Resources.Any(r => r.Key.Type == 3496170587u))
                        {
                            string path = InstallAsLot(dbpfPackage, originalName, nonPackageItems);
                            if (path != null) nonPackageItems.Add(path);
                        }
                        else if (dbpfPackage.Resources.Any(r => r.Key.Type == 83396964))
                        {
                            string path = InstallAsSim(dbpfPackage, originalName, nonPackageItems);
                            if (path != null) nonPackageItems.Add(path);
                        }
                        else if (dbpfPackage.Resources.Any(r => r.Key.Type == 0xD4D9FBE5)) // Pattern (PTRN)
                        {
                            InstallAsPattern(dbpfPackage, originalName, staticCacheDir);
                        }
                        else if (ValidatePackage(dbpfPackage))
                        {
                            RebuildPackageStatic(ref outputPkg, ref packageCount, staticCacheDir, dbpfPackage, addedTgis);
                        }
                        else
                        {
                            SafeAddSkippedFile(skippedFiles, item.FileName, "Failed validation (possible corrupt data)");
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
                        var worldPkg = sims3Pack.Packages.FirstOrDefault(p => p.Resources.Any(r => r.Key.Type == 107542056));
                        if (worldPkg != null)
                        {
                            string path = InstallAsWorld(worldPkg, originalName, nonPackageItems);
                            if (path != null) nonPackageItems.Add(path);

                            foreach (DBPFPackage package in sims3Pack.Packages)
                            {
                                if (package == worldPkg) continue;

                                if (package.Resources.Any(r => r.Key.Type == 3496170587u))
                                {
                                    string lotPath = InstallAsLot(package, originalName, nonPackageItems);
                                    if (lotPath != null) nonPackageItems.Add(lotPath);
                                }
                                else if (package.Resources.Any(r => r.Key.Type == 83396964))
                                {
                                    string simPath = InstallAsSim(package, originalName, nonPackageItems);
                                    if (simPath != null) nonPackageItems.Add(simPath);
                                }
                                else if (package.Resources.Any(r => r.Key.Type == 0xD4D9FBE5)) // Pattern (PTRN)
                                {
                                    InstallAsPattern(package, originalName, staticCacheDir);
                                }
                                else if (ValidatePackage(package))
                                {
                                    RebuildPackageStatic(ref outputPkg, ref packageCount, staticCacheDir, package, addedTgis);
                                }
                            }
                        }
                        else
                        {
                            foreach (DBPFPackage package in sims3Pack.Packages)
                            {
                                if (package.Resources.Any(r => r.Key.Type == 3496170587u))
                                {
                                    string lotPath = InstallAsLot(package, originalName, nonPackageItems);
                                    if (lotPath != null) nonPackageItems.Add(lotPath);
                                }
                                else if (package.Resources.Any(r => r.Key.Type == 83396964))
                                {
                                    string simPath = InstallAsSim(package, originalName, nonPackageItems);
                                    if (simPath != null) nonPackageItems.Add(simPath);
                                }
                                else if (package.Resources.Any(r => r.Key.Type == 0xD4D9FBE5)) // Pattern (PTRN)
                                {
                                    InstallAsPattern(package, originalName, staticCacheDir);
                                }
                                else if (ValidatePackage(package))
                                {
                                    RebuildPackageStatic(ref outputPkg, ref packageCount, staticCacheDir, package, addedTgis);
                                }
                                else
                                {
                                    SafeAddSkippedFile(skippedFiles, item.FileName, "Failed validation (possible corrupt data)");
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
        }

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

    public void RebuildSet(SetsEntity activeSet, Action<string>? onProgress = null, List<(string FileName, string Reason)>? skippedFiles = null, ITaskProgressReporter? progress = null, string? stepId = null, int totalCount = 0, Dictionary<long, SetsEntity>? allSetsMap = null)
    {
        if (activeSet.IsLegacy) return;

        string setPath = GetSetPath(activeSet, allSetsMap);
        Directory.CreateDirectory(setPath);

        DBPFPackageBuilder? outputPkg = null;
        int packageCount = 0;
        var addedTgis = new HashSet<TGI_Key>();
        var nonPackageItems = new List<string>();

        var metaEntities = activeSet.MetaEntities?.ToList() ?? new List<MetaEntity>();
        int totalItems = metaEntities.Count;
        int currentItem = 0;
        int reportInterval = Math.Max(1, totalItems / 50);

        foreach (var item in metaEntities)
        {
            currentItem++;
            bool isLast = currentItem == totalItems;
            if (currentItem % reportInterval == 0 || isLast)
            {
                double pct = totalItems > 0 ? (double)currentItem / totalItems : 1.0;
                string badge = $"{currentItem}/{totalItems}";
                if (stepId != null)
                {
                    progress?.UpdateStep(stepId, progress: pct, badge: badge);
                }
                onProgress?.Invoke(_localizer.GetString("merging_item", currentItem, totalItems, activeSet.Name));
            }
            if (!item.Enabled) continue;

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
                    continue;
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
                    continue;
                }

                if (dbpfPackage != null)
                {
                    try
                    {
                        string originalName = Path.GetFileNameWithoutExtension(item.FileName);
                        if (dbpfPackage.Resources.Any(r => r.Key.Type == 107542056)) // World
                        {
                            string path = InstallAsWorld(dbpfPackage, originalName, nonPackageItems);
                            if (path != null) nonPackageItems.Add(path);
                        }
                        else if (dbpfPackage.Resources.Any(r => r.Key.Type == 3496170587u)) // Lot
                        {
                            string path = InstallAsLot(dbpfPackage, originalName, nonPackageItems);
                            if (path != null) nonPackageItems.Add(path);
                        }
                        else if (dbpfPackage.Resources.Any(r => r.Key.Type == 83396964)) // Sim
                        {
                            string path = InstallAsSim(dbpfPackage, originalName, nonPackageItems);
                            if (path != null) nonPackageItems.Add(path);
                        }
                        else if (dbpfPackage.Resources.Any(r => r.Key.Type == 0xD4D9FBE5)) // Pattern (PTRN)
                        {
                            InstallAsPattern(dbpfPackage, originalName, setPath);
                        }
                        else if (ValidatePackage(dbpfPackage))
                        {
                            RebuildPackage(ref outputPkg, ref packageCount, activeSet, dbpfPackage, addedTgis, allSetsMap);
                        }
                        else
                        {
                            SafeAddSkippedFile(skippedFiles, item.FileName, "Failed validation (possible corrupt data)");
                            onProgress?.Invoke(_localizer.GetString("skipping_invalid_package", item.FileName));
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
                        var worldPkg = sims3Pack.Packages.FirstOrDefault(p => p.Resources.Any(r => r.Key.Type == 107542056));
                        if (worldPkg != null)
                        {
                            string path = InstallAsWorld(worldPkg, originalName, nonPackageItems);
                            if (path != null) nonPackageItems.Add(path);

                            foreach (DBPFPackage package in sims3Pack.Packages)
                            {
                                if (package == worldPkg) continue;

                                if (package.Resources.Any(r => r.Key.Type == 3496170587u)) // Lot
                                {
                                    string lotPath = InstallAsLot(package, originalName, nonPackageItems);
                                    if (lotPath != null) nonPackageItems.Add(lotPath);
                                }
                                else if (package.Resources.Any(r => r.Key.Type == 83396964)) // Sim
                                {
                                    string simPath = InstallAsSim(package, originalName, nonPackageItems);
                                    if (simPath != null) nonPackageItems.Add(simPath);
                                }
                                else if (package.Resources.Any(r => r.Key.Type == 0xD4D9FBE5)) // Pattern (PTRN)
                                {
                                    InstallAsPattern(package, originalName, setPath);
                                }
                                else if (ValidatePackage(package))
                                {
                                    RebuildPackage(ref outputPkg, ref packageCount, activeSet, package, addedTgis, allSetsMap);
                                }
                            }
                        }
                        else
                        {
                            foreach (DBPFPackage package in sims3Pack.Packages)
                            {
                                if (package.Resources.Any(r => r.Key.Type == 3496170587u)) // Lot
                                {
                                    string lotPath = InstallAsLot(package, originalName, nonPackageItems);
                                    if (lotPath != null) nonPackageItems.Add(lotPath);
                                }
                                else if (package.Resources.Any(r => r.Key.Type == 83396964)) // Sim
                                {
                                    string simPath = InstallAsSim(package, originalName, nonPackageItems);
                                    if (simPath != null) nonPackageItems.Add(simPath);
                                }
                                else if (package.Resources.Any(r => r.Key.Type == 0xD4D9FBE5)) // Pattern (PTRN)
                                {
                                    InstallAsPattern(package, originalName, setPath);
                                }
                                else if (ValidatePackage(package))
                                {
                                    RebuildPackage(ref outputPkg, ref packageCount, activeSet, package, addedTgis, allSetsMap);
                                }
                                else
                                {
                                    SafeAddSkippedFile(skippedFiles, item.FileName, "Failed validation (possible corrupt data)");
                                    onProgress?.Invoke(_localizer.GetString("skipping_invalid_package_sims3pack", item.FileName));
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

    private void RebuildPackageStatic(ref DBPFPackageBuilder? outputPkg, ref int packageCount, string staticCachePath, DBPFPackage inputPkg, HashSet<TGI_Key> addedTgis)
    {
        var validResources = new List<ResourceEntry>();

        foreach (var resource in inputPkg.Resources)
        {
            if (resource.Key.Type == 3571055589u) FixPTRN(resource);
            else if (resource.Key.Type == 53690476) FixPTRN_XML(resource);

            if (ValidateResource(resource.Key) && addedTgis.Add(resource.Key))
            {
                validResources.Add(resource);
            }
        }

        if (_options.CompressionLevel > 0)
        {
            var uncompressedResources = validResources.Where(r => !r.IsCompressed && r.Length >= 32).ToList();
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

        foreach (var resource in validResources)
        {
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

    private string InstallAsSim(DBPFPackage package, string name, List<string> nonPackageItems)
    {
        string basePath = Path.ChangeExtension(Path.Combine(_options.DocumentBaseDir, "Sims", name), ".sim");
        string path = basePath;
        int count = 1;
        while (nonPackageItems.Contains(path))
        {
            path = Path.ChangeExtension(Path.Combine(_options.DocumentBaseDir, "Sims", $"{name}_{count}"), ".sim");
            count++;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) package.Export(path);
        return path;
    }

    private string InstallAsLot(DBPFPackage package, string name, List<string> nonPackageItems)
    {
        string basePath = Path.ChangeExtension(Path.Combine(_options.DocumentBaseDir, "Lots", name), ".package");
        string path = basePath;
        int count = 1;
        while (nonPackageItems.Contains(path))
        {
            path = Path.ChangeExtension(Path.Combine(_options.DocumentBaseDir, "Lots", $"{name}_{count}"), ".package");
            count++;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) package.Export(path);
        return path;
    }

    public static string GetWorldName(DBPFPackage package, string defaultName)
    {
        try
        {
            // 0xD9BE8E89 (3653044489u) is the World Name resource in Sims 3 .world DBPF packages
            var res = package.Resources.FirstOrDefault(r => r.Key.Type == 3653044489u);
            if (res != null)
            {
                byte[] bytes = res.Read();
                if (bytes.Length >= 4)
                {
                    int len = BitConverter.ToInt32(bytes, 0);
                    if (len > 0 && bytes.Length >= 4 + len * 2)
                    {
                        string name = System.Text.Encoding.Unicode.GetString(bytes, 4, len * 2).Trim();
                        var invalidChars = Path.GetInvalidFileNameChars();
                        string cleanName = new string(name.Where(c => !invalidChars.Contains(c) && c != '/' && c != '\\').ToArray()).Trim();
                        if (!string.IsNullOrWhiteSpace(cleanName))
                        {
                            return cleanName;
                        }
                    }
                }
            }
        }
        catch { }

        string fallback = Path.GetFileNameWithoutExtension(defaultName);
        var invalid = Path.GetInvalidFileNameChars();
        return new string(fallback.Where(c => !invalid.Contains(c) && c != '/' && c != '\\').ToArray()).Trim();
    }

    private string InstallAsWorld(DBPFPackage package, string defaultName, List<string> nonPackageItems)
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

    private string InstallAsPattern(DBPFPackage package, string name, string targetFolder)
    {
        string cleanName = SanitizeForCache(name);
        if (string.IsNullOrWhiteSpace(cleanName)) cleanName = "Pattern";
        string basePath = Path.Combine(targetFolder, $"Pattern_{cleanName}.package");
        string path = basePath;
        int count = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(targetFolder, $"Pattern_{cleanName}_{count}.package");
            count++;
        }
        Directory.CreateDirectory(targetFolder);
        package.Export(path);
        return path;
    }

    private bool ValidatePackage(DBPFPackage package)
    {
        var dollDressedKey = new TGI_Key(832458525u, 0u, 4064452635095512314uL);
        foreach (var resource in package.Resources)
        {
            if (resource.Key.Type == dollDressedKey.Type && resource.Key.Group == dollDressedKey.Group && resource.Key.Instance == dollDressedKey.Instance)
            {
                return false;
            }
        }
        return true;
    }

    private bool ValidateResource(TGI_Key key)
    {
        if (key.Type == 1944665835) return false;
        return true;
    }

    private void RebuildPackage(ref DBPFPackageBuilder? outputPkg, ref int packageCount, SetsEntity activeSet, DBPFPackage package, HashSet<TGI_Key> addedTgis, Dictionary<long, SetsEntity>? allSetsMap = null)
    {
        var validResources = new List<ResourceEntry>();

        foreach (var resource in package.Resources)
        {
            if (resource.Key.Type == 3571055589u) FixPTRN(resource);
            else if (resource.Key.Type == 53690476) FixPTRN_XML(resource);

            if (ValidateResource(resource.Key) && addedTgis.Add(resource.Key))
            {
                validResources.Add(resource);
            }
        }

        if (_options.CompressionLevel > 0)
        {
            var uncompressedResources = validResources.Where(r => !r.IsCompressed && r.Length >= 32).ToList();
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

        foreach (var resource in validResources)
        {
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
        if (!data.AsSpan().Contains((byte)':')) return;
        string text = System.Text.Encoding.UTF8.GetString(data);
        if (!text.Contains(": ")) return;

        XmlDocument xmlDocument = new XmlDocument();
        try { xmlDocument.LoadXml(text); } catch (Exception) { return; }
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
        if (!data.AsSpan().Contains((byte)':')) return;
        string text = System.Text.Encoding.UTF8.GetString(data);
        if (!text.Contains(": ")) return;

        XmlDocument xmlDocument = new XmlDocument();
        try { xmlDocument.LoadXml(text); } catch (Exception) { return; }
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
                sw.WriteLine("Priority 500");
                sw.WriteLine("PackedFile Cache/Config/Resource.cfg");
                sw.WriteLine("PackedFile Packages/*.package");
                sw.WriteLine("PackedFile Packages/*/*.package");
                sw.WriteLine("PackedFile Packages/*/*/*.package");
                sw.WriteLine("PackedFile Packages/*/*/*/*.package");
                sw.WriteLine("PackedFile Packages/*/*/*/*/*.package");
            }
            catch { }
        }
        else
        {
            try
            {
                string content = File.ReadAllText(mainResourceCfg);
                if (!content.Contains("PackedFile Cache/Config/Resource.cfg", StringComparison.OrdinalIgnoreCase))
                {
                    var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
                    int insertIndex = 0;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        if (lines[i].TrimStart().StartsWith("Priority", StringComparison.OrdinalIgnoreCase))
                        {
                            insertIndex = i + 1;
                            break;
                        }
                    }
                    lines.Insert(insertIndex, "PackedFile Cache/Config/Resource.cfg");
                    File.WriteAllText(mainResourceCfg, string.Join(Environment.NewLine, lines));
                }
            }
            catch { }
        }
    }
}
