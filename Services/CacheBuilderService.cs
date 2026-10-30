using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlumbobForge.Backend.Configuration;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;
using S3ForgeTools.GameFiles.Textures;
using S3ForgeTools.GameFiles.TS3Pack;
using S3ForgeTools.Utils;

namespace PlumbobForge.Backend.Services;

public class CacheBuilderService
{
    private static readonly TGI_Key DollDressedKey = new(832458525u, 0u, 4064452635095512314uL);
    private static readonly byte[] ColonSpaceBytes = [(byte)':', (byte)' '];
    private static readonly HashSet<char> InvalidFileNameChars = [.. Path.GetInvalidFileNameChars()];
    private static readonly HashSet<char> InvalidCacheFolderChars = [.. Path.GetInvalidFileNameChars(), '/', '\\'];
    private const long MaxBundleSize = 1024L * 1024L * 1024L; // 1 GB bundle limit

    // Lots, Sims, worlds and patterns are exported to files; sets rebuilt in parallel must not write them at the same time.
    private static readonly object NonPackageExportLock = new();

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
        if (PackageTypeHelper.IsCas(packageType))
        {
            return 1;
        }

        // Group Build/Buy objects together second
        if (PackageTypeHelper.IsBuildBuy(packageType))
        {
            return 2;
        }

        // Patterns
        if (PackageTypeHelper.IsPattern(packageType))
        {
            return 3;
        }

        return 4;
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
        if (_cachedSims3FolderPath != null && Directory.Exists(_cachedSims3FolderPath))
            return _cachedSims3FolderPath;

        _cachedSims3FolderPath = Sims3PathResolver.ResolveSims3UserFolder(_options.Sims3UserDir);
        return _cachedSims3FolderPath;
    }

    public async Task SyncToSims3Async(Action<string>? onProgress = null, bool forceRebuildStatic = false)
    {
        try
        {
            onProgress?.Invoke(_localizer.GetString("syncing_cache_to_sims3"));

            string sims3ModsDir = Path.Combine(GetSims3FolderPath(), "Mods");
            string sims3CacheDir = Path.Combine(sims3ModsDir, "Cache");
            string sims3ConfigDir = Path.Combine(sims3CacheDir, "Config");
            string staticCacheDir = Path.Combine(sims3CacheDir, "StaticCache");

            Directory.CreateDirectory(sims3ModsDir);
            Directory.CreateDirectory(sims3CacheDir);
            Directory.CreateDirectory(sims3ConfigDir);

            EnsureMainResourceCfg(sims3ModsDir);

            var activeConfig = await _db.ConfigEntities
                .Include(c => c.ConfigSetsEntities)
                .ThenInclude(cs => cs.SetsEntity)
                .FirstOrDefaultAsync(c => c.Active);

            // Vault mode has no cache: Resource.cfg only points at the shard folders of the active configuration.
            bool vaultMode = _options.EnableSingleSourceVault;
            bool isStatic = !vaultMode && string.Equals(_options.CacheMethod, "Static", StringComparison.OrdinalIgnoreCase);
            bool resourceCfgChanged = false;
            if (vaultMode)
            {
                resourceCfgChanged = await WriteVaultResourceCfgAsync(sims3ConfigDir, activeConfig);
            }
            else if (isStatic)
            {
                string configResourceCfgStatic = Path.Combine(sims3ConfigDir, "Resource.cfg");
                var cfg = new StringBuilder();
                cfg.AppendLine("Priority 500");
                cfg.AppendLine("PackedFile \"../StaticCache/*.package\"");
                cfg.AppendLine("PackedFile \"../StaticCache/*/*.package\"");
                resourceCfgChanged = WriteResourceCfgIfChanged(configResourceCfgStatic, cfg.ToString());

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

                // Clean up cache folders of deleted sets
                var allSetFolderNames = (await _db.SetsEntities.Select(s => s.Id).ToListAsync())
                    .Select(GetSetFolderName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

            var targetFilesToSync = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (vaultMode)
            {
                // Lots and Sims stay library files in vault mode; deploy those of the active configuration directly.
                foreach (var (dest, source) in await GetVaultModeLibraryDeploymentsAsync(activeConfig))
                {
                    targetFilesToSync[dest] = source;
                }
            }
            else if (!isStatic)
            {
                string configResourceCfg = Path.Combine(sims3ConfigDir, "Resource.cfg");
                var cfg = new StringBuilder();
                cfg.AppendLine("Priority 500");
                if (activeConfig != null)
                {
                    foreach (var cs in activeConfig.ConfigSetsEntities.OrderBy(cs => cs.SetsEntityId))
                    {
                        if (cs.SetsEntity == null) continue;

                        string folderName = GetSetFolderName(cs.SetsEntity.Id);
                        cfg.AppendLine($"PackedFile \"../{folderName}/*.package\"");
                        cfg.AppendLine($"PackedFile \"../{folderName}/*/*.package\"");

                        AddNonPackageTargets(Path.Combine(GetSetPath(cs.SetsEntity), "NonPackageItems.txt"), targetFilesToSync);
                    }
                }
                resourceCfgChanged = WriteResourceCfgIfChanged(configResourceCfg, cfg.ToString());
            }
            else
            {
                AddNonPackageTargets(Path.Combine(staticCacheDir, "NonPackageItems.txt"), targetFilesToSync);
            }

            // Sync SavedSims & Library (Lots) into Documents/Electronic Arts/The Sims 3/
            // Safely manage ONLY files deployed and tracked by PlumbobForge (never touch unmanaged user/game files)
            string managedNonPkgFile = Path.Combine(_options.DocumentBaseDir, "ManagedNonPackageItems.txt");
            var previouslyManagedNonPkg = File.Exists(managedNonPkgFile)
                ? File.ReadAllLines(managedNonPkgFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Clean up ONLY items that were previously deployed by PlumbobForge and are no longer in the active configuration.
            // (This also removes the world copies older versions put into The Sims 3/Worlds, which the game never reads.)
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
                    // Library-mode rebuilds stage worlds as {item name}.world; otherwise use the item's own file.
                    string stagedWorldFile = Path.Combine(_options.DocumentBaseDir, "Worlds", Path.ChangeExtension(worldItem.FileName, ".world"));
                    if (!File.Exists(stagedWorldFile) && File.Exists(worldItem.CompleteFileName) &&
                        (worldItem.CompleteFileName.EndsWith(".world", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(worldItem.PackageType, PackageTypeHelper.World, StringComparison.OrdinalIgnoreCase)))
                    {
                        stagedWorldFile = worldItem.CompleteFileName;
                    }

                    string worldBaseName = Path.GetFileNameWithoutExtension(worldItem.FileName);

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

                // Cleanup removed custom worlds (including ones older versions installed under a wrongly parsed name)
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

            // A different Resource.cfg (e.g. another configuration) loads different mods.
            if (resourceCfgChanged)
            {
                OnGameContentChanged();
            }
            else
            {
                ProcessPendingGameCacheClear();
            }
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"Error syncing cache to Sims 3: {ex.Message}");
        }
    }

    private void AddNonPackageTargets(string nonPackageFile, Dictionary<string, string> targets)
    {
        if (!File.Exists(nonPackageFile)) return;

        foreach (var line in File.ReadAllLines(nonPackageFile))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.EndsWith(".world", StringComparison.OrdinalIgnoreCase)) continue;

            string fileName = Path.GetFileName(line);
            string folder = Path.GetFileName(Path.GetDirectoryName(line)!);
            targets[GetTS3FolderPath(folder, fileName)] = line;
        }
    }

    private async Task<List<(string Dest, string Source)>> GetVaultModeLibraryDeploymentsAsync(ConfigEntity? activeConfig)
    {
        var result = new List<(string, string)>();
        var activeSetIds = activeConfig?.ConfigSetsEntities.Select(cs => cs.SetsEntityId).ToList() ?? new List<long>();
        if (activeSetIds.Count == 0) return result;

        var items = await _db.MetaEntities
            .AsNoTracking()
            .Where(m => !m.IsVaultManaged && m.Enabled && m.SetsEntityId.HasValue && activeSetIds.Contains(m.SetsEntityId.Value))
            .ToListAsync();

        string sims3Dir = GetSims3FolderPath();
        foreach (var item in items)
        {
            bool isSim = string.Equals(item.PackageType, PackageTypeHelper.Sim, StringComparison.OrdinalIgnoreCase) || item.FileType == "SIM";
            bool isLot = !isSim && string.Equals(item.PackageType, PackageTypeHelper.Lot, StringComparison.OrdinalIgnoreCase);
            if (!isSim && !isLot) continue;

            string source = File.Exists(item.CompleteFileName)
                ? item.CompleteFileName
                : Path.Combine(_options.ManagedPackageFolderPath, item.FileName);
            if (!File.Exists(source)) continue;

            string dest = isSim
                ? Path.Combine(sims3Dir, "SavedSims", Path.ChangeExtension(item.FileName, ".sim"))
                : Path.Combine(sims3Dir, "Library", Path.ChangeExtension(item.FileName, ".package"));
            result.Add((dest, source));
        }
        return result;
    }

    private static bool WriteResourceCfgIfChanged(string path, string content)
    {
        try
        {
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch { }

        File.WriteAllText(path, content);
        return true;
    }

    private async Task<bool> WriteVaultResourceCfgAsync(string sims3ConfigDir, ConfigEntity? activeConfig)
    {
        var activeSetIds = activeConfig?.ConfigSetsEntities.Select(cs => cs.SetsEntityId).ToList() ?? new List<long>();
        var shards = await _db.VaultShards
            .Include(s => s.SetsEntity)
            .Where(s => activeSetIds.Contains(s.SetsEntityId))
            .ToListAsync();

        string vaultRoot = VaultService.GetVaultRootDirectory(GetSims3FolderPath());
        var shardDirs = shards
            .Select(s => Path.GetDirectoryName(VaultService.ResolveShardPath(vaultRoot, s, s.SetsEntity))!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string configResourceCfg = Path.Combine(sims3ConfigDir, "Resource.cfg");
        var cfg = new StringBuilder();
        cfg.AppendLine("Priority 500");
        foreach (var dir in shardDirs)
        {
            string rel = Path.GetRelativePath(vaultRoot, dir).Replace('\\', '/');
            cfg.AppendLine($"PackedFile \"../../PF_Vault/{rel}/*.package\"");
        }
        return WriteResourceCfgIfChanged(configResourceCfg, cfg.ToString());
    }

    public async Task<bool> UpgradeSetCacheFoldersAsync()
    {
        if (_options.EnableSingleSourceVault || string.Equals(_options.CacheMethod, "Static", StringComparison.OrdinalIgnoreCase)) return false;

        var allSets = await _db.SetsEntities.Include(s => s.MetaEntities).ToListAsync();
        var allSetsMap = allSets.ToDictionary(s => s.Id);
        var claimedLegacyFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool moved = false;
        bool dirtied = false;

        foreach (var set in allSets.OrderBy(s => s.Id))
        {
            string setPath = GetSetPath(set);
            if (Directory.Exists(setPath)) continue;

            // Two sets could share one old folder (names that sanitize the same); only the first may take it over.
            string legacyPath = GetSetCachePath(GetLegacySetFolderName(set, allSetsMap));
            if (Directory.Exists(legacyPath) && claimedLegacyFolders.Add(legacyPath))
            {
                try
                {
                    Directory.Move(legacyPath, setPath);
                    moved = true;
                    continue;
                }
                catch { }
            }

            if (!set.Dirty && (set.MetaEntities?.Any(m => m.Enabled) ?? false))
            {
                set.Dirty = true;
                dirtied = true;
            }
        }

        if (dirtied) await _db.SaveChangesAsync();
        return moved;
    }

    public async Task RebuildCacheAsync(bool forceRebuild = false, Action<string>? onProgress = null, List<(string FileName, string Reason)>? skippedFiles = null, ITaskProgressReporter? progress = null)
    {
        // Rebuilding belongs to library mode only. Vault mods point at whole shards and must never be merged into a cache.
        if (_options.EnableSingleSourceVault)
        {
            return;
        }

        bool isStatic = string.Equals(_options.CacheMethod, "Static", StringComparison.OrdinalIgnoreCase);
        if (isStatic)
        {
            await RebuildStaticCacheAsync(onProgress, skippedFiles, progress);
            return;
        }

        try
        {
            string staticDir = Path.Combine(GetSims3FolderPath(), "Mods", "Cache", "StaticCache");
            if (Directory.Exists(staticDir)) Directory.Delete(staticDir, true);
        }
        catch { }

        var allSets = await _db.SetsEntities.Include(s => s.MetaEntities).ToListAsync();
        // A set whose cache folder is gone (deleted by hand, or never built) is rebuilt even if it looks clean.
        var setsToRebuild = allSets
            .Where(s => forceRebuild || s.Dirty || ((s.MetaEntities?.Any(m => m.Enabled) ?? false) && !Directory.Exists(GetSetPath(s))))
            .ToList();
        int total = setsToRebuild.Count;
        int current = 0;

        // ModBUILD packages are deleted and replaced; the running game has them open.
        if (setsToRebuild.Count > 0)
        {
            Sims3GameProcess.ThrowIfGameRunning(_localizer.GetString("progress.game_running_rebuild"));
            await LoadDisabledObjectsAsync();
        }

        if (setsToRebuild.Count > 1)
        {
            var updatedHashes = new ConcurrentDictionary<long, string>();
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
                    RebuildSet(set, onProgress, skippedFiles, shouldReport ? progress : null, shouldReport ? stepId : null, itemCount, isParallelSet: true);
                    updatedHashes[set.Id] = SetDirtyTracker.ComputeContentHash(set);
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

            foreach (var set in setsToRebuild)
            {
                if (updatedHashes.TryGetValue(set.Id, out var hash))
                {
                    set.CachedHash = hash;
                    set.Dirty = false;
                }
            }
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
                    RebuildSet(set, onProgress, skippedFiles, shouldReport ? progress : null, shouldReport ? stepId : null, itemCount, isParallelSet: false);
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

        if (total > 0)
        {
            PruneStagedNonPackageFiles(allSets.Select(s => Path.Combine(GetSetPath(s), "NonPackageItems.txt")));
            OnGameContentChanged();
            PruneTextureCache();
        }
    }

    private TextureOptimizationCache? CreateTextureCache()
    {
        if (!_options.OptimizeTextures || string.IsNullOrWhiteSpace(_options.DocumentBaseDir)) return null;
        try
        {
            return new TextureOptimizationCache(_options.DocumentBaseDir, _options.TextureMaxDimension);
        }
        catch
        {
            return null;
        }
    }

    private void PruneTextureCache()
    {
        if (!_options.OptimizeTextures || string.IsNullOrWhiteSpace(_options.DocumentBaseDir)) return;
        TextureOptimizationCache.Prune(_options.DocumentBaseDir, TextureOptimizer.GetProfile(_options.TextureMaxDimension));
    }

    private void PruneStagedNonPackageFiles(IEnumerable<string> nonPackageListFiles)
    {
        try
        {
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var listFile in nonPackageListFiles)
            {
                if (!File.Exists(listFile)) continue;
                foreach (var line in File.ReadAllLines(listFile))
                {
                    if (!string.IsNullOrWhiteSpace(line)) listed.Add(Path.GetFullPath(line));
                }
            }

            foreach (var sub in new[] { "Worlds", "Lots", "Sims" })
            {
                string dir = Path.Combine(_options.DocumentBaseDir, sub);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir))
                {
                    if (listed.Contains(Path.GetFullPath(file))) continue;
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }
    }

    public void OnGameContentChanged()
    {
        if (!_options.AutoClearGameCaches) return;
        try
        {
            Sims3GameProcess.ClearCoreCachesOrDefer(GetSims3FolderPath(), _options.DocumentBaseDir);
        }
        catch { }
    }

    public void ProcessPendingGameCacheClear()
    {
        try
        {
            Sims3GameProcess.ProcessPendingCacheClear(GetSims3FolderPath(), _options.DocumentBaseDir);
        }
        catch { }
    }

    public async Task RebuildStaticCacheAsync(Action<string>? onProgress = null, List<(string FileName, string Reason)>? skippedFiles = null, ITaskProgressReporter? progress = null)
    {
        if (_options.EnableSingleSourceVault)
        {
            return;
        }

        // Static bundles are deleted and replaced; the running game has them open.
        Sims3GameProcess.ThrowIfGameRunning(_localizer.GetString("progress.game_running_rebuild"));

        onProgress?.Invoke(_localizer.GetString("rebuilding_static_cache"));
        string staticStepId = "rebuild_static_cache";
        progress?.StartStep(staticStepId, _localizer.GetString("progress.rebuilding_static_cache_title"), badge: _localizer.GetString("progress.badge_initializing"), progress: 0.0);

        string sims3CacheDir = Path.Combine(GetSims3FolderPath(), "Mods", "Cache");
        string staticCacheDir = Path.Combine(sims3CacheDir, "StaticCache");
        Directory.CreateDirectory(staticCacheDir);

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

        // Sort items by PackageType category so that CAS items, BuildBuy items, etc., are clustered into contiguous bundles.
        // One entry per item: different mods may share a file name (in different library sub-folders).
        var allMetaItems = activeSets
            .SelectMany(s => s.MetaEntities ?? new List<MetaEntity>())
            .Where(m => m.Enabled)
            .GroupBy(m => m.Id)
            .Select(g => g.First())
            .OrderBy(m => GetPackageTypePriority(m.PackageType))
            .ThenBy(m => m.PackageType)
            .ThenBy(m => m.FileName)
            .ThenBy(m => m.Id)
            .ToList();

        int totalItems = allMetaItems.Count;
        int reportInterval = Math.Max(1, totalItems / 50);

        // The old bundles stay in place until the new ones are complete.
        await LoadDisabledObjectsAsync();
        var ctx = new MergeContext(staticCacheDir, "StaticBundle_", CreateTextureCache(), skippedFiles, onProgress) { DisabledObjects = _disabledObjects };
        DeleteStaleNewFiles(ctx);
        MergeItems(allMetaItems, ctx, parallel: true, current =>
        {
            if (current % reportInterval == 0 || current == totalItems)
            {
                double pct = totalItems > 0 ? (double)current / totalItems : 1.0;
                progress?.UpdateStep(staticStepId, progress: pct, badge: $"{current}/{totalItems}");
                onProgress?.Invoke(_localizer.GetString("merging_item", current, totalItems, "StaticCache"));
            }
        });

        foreach (var (entity, fallback) in ctx.FallbackUpdates)
        {
            entity.CompleteFileName = fallback;
        }

        try
        {
            FinalizeOutput(ctx);

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

        PruneStagedNonPackageFiles(new[] { Path.Combine(staticCacheDir, "NonPackageItems.txt") });
        OnGameContentChanged();
        PruneTextureCache();

        progress?.CompleteStep(staticStepId, finalBadge: _localizer.GetString("progress.items_merged_total_badge", totalItems));
    }

    public static string SanitizeForCache(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var trimmed = name.AsSpan().Trim();
        var sb = new StringBuilder(trimmed.Length);
        bool lastWasDash = false;

        foreach (char c in trimmed)
        {
            char current = c == ' ' ? '-' : c;
            if (InvalidCacheFolderChars.Contains(current)) continue;

            if (current == '-')
            {
                if (!lastWasDash && sb.Length > 0)
                {
                    sb.Append('-');
                    lastWasDash = true;
                }
            }
            else
            {
                sb.Append(current);
                lastWasDash = false;
            }
        }

        while (sb.Length > 0 && sb[^1] == '-')
        {
            sb.Length--;
        }

        return sb.ToString();
    }

    public static string GetSetFolderName(long setId) => $"Set_{setId}";

    public string GetSetFolderName(SetsEntity activeSet, Dictionary<long, SetsEntity>? allSetsMap = null) => GetSetFolderName(activeSet.Id);

    private string GetLegacySetFolderName(SetsEntity activeSet, Dictionary<long, SetsEntity> allSetsMap)
    {
        if (activeSet.IsDefault || string.Equals(activeSet.Name, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return "Default";
        }
        if (activeSet.IsLegacy || string.Equals(activeSet.Name, "Legacy", StringComparison.OrdinalIgnoreCase))
        {
            return "Legacy";
        }

        var chain = new List<string>();
        SetsEntity? current = activeSet;
        var visited = new HashSet<long>();

        while (current != null && visited.Add(current.Id))
        {
            string clean = SanitizeForCache(!string.IsNullOrWhiteSpace(current.Name) ? current.Name : $"Set_{current.Id}");
            if (string.IsNullOrWhiteSpace(clean)) clean = $"Set_{current.Id}";
            chain.Insert(0, clean);

            current = current.ParentSetsEntityId.HasValue && allSetsMap.TryGetValue(current.ParentSetsEntityId.Value, out var parent)
                ? parent
                : null;
        }

        string folderName = string.Join("-", chain);
        return string.IsNullOrWhiteSpace(folderName) ? $"Set_{activeSet.Id}" : folderName;
    }

    public string GetSetPath(SetsEntity activeSet, Dictionary<long, SetsEntity>? allSetsMap = null)
    {
        if (activeSet == null) throw new ArgumentException("ActiveSet cannot be null");
        return GetSetCachePath(GetSetFolderName(activeSet.Id));
    }

    public string GetSetCachePath(string folderName)
    {
        return Path.Combine(GetSims3FolderPath(), "Mods", "Cache", folderName);
    }

    public void RebuildSet(
        SetsEntity activeSet,
        Action<string>? onProgress = null,
        List<(string FileName, string Reason)>? skippedFiles = null,
        ITaskProgressReporter? progress = null,
        string? stepId = null,
        int totalCount = 0,
        bool isParallelSet = false)
    {
        string setPath = GetSetPath(activeSet);
        Directory.CreateDirectory(setPath);

        var metaEntities = (activeSet.MetaEntities?.Where(m => m.Enabled) ?? Enumerable.Empty<MetaEntity>())
            .OrderBy(m => GetPackageTypePriority(m.PackageType))
            .ThenBy(m => m.PackageType)
            .ThenBy(m => m.FileName)
            .ThenBy(m => m.Id)
            .ToList();
        int totalItems = metaEntities.Count;
        int reportInterval = Math.Max(1, totalItems / 50);

        var ctx = new MergeContext(setPath, "ModBUILD", CreateTextureCache(), skippedFiles, onProgress) { DisabledObjects = _disabledObjects };
        DeleteStaleNewFiles(ctx);

        // If multiple sets are already running concurrently, process items inside this set sequentially
        // to prevent thread oversubscription. If only this single set is running and it has many items,
        // read and prepare items on all CPU cores.
        bool runItemsInParallel = !isParallelSet && totalItems > 10;
        MergeItems(metaEntities, ctx, runItemsInParallel, current =>
        {
            if (current % reportInterval == 0 || current == totalItems)
            {
                double pct = totalItems > 0 ? (double)current / totalItems : 1.0;
                if (stepId != null)
                {
                    progress?.UpdateStep(stepId, progress: pct, badge: $"{current}/{totalItems}");
                }
                onProgress?.Invoke(_localizer.GetString("merging_item", current, totalItems, activeSet.Name));
            }
        });

        foreach (var (entity, fallback) in ctx.FallbackUpdates)
        {
            entity.CompleteFileName = fallback;
        }

        FinalizeOutput(ctx);

        activeSet.CachedHash = SetDirtyTracker.ComputeContentHash(activeSet);
        activeSet.Dirty = false;
    }

    private sealed class MergeContext
    {
        public MergeContext(string outputDir, string bundlePrefix, TextureOptimizationCache? textureCache,
            List<(string FileName, string Reason)>? skippedFiles, Action<string>? onProgress)
        {
            OutputDir = outputDir;
            BundlePrefix = bundlePrefix;
            TextureCache = textureCache;
            SkippedFiles = skippedFiles;
            OnProgress = onProgress;
        }

        public string OutputDir { get; }
        public string BundlePrefix { get; }
        public TextureOptimizationCache? TextureCache { get; }
        public List<(string FileName, string Reason)>? SkippedFiles { get; }
        public Action<string>? OnProgress { get; }
        public HashSet<string> NonPackageItems { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> PatternFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentBag<(MetaEntity Entity, string FallbackPath)> FallbackUpdates { get; } = new();
        public IReadOnlyDictionary<long, HashSet<TGI_Key>> DisabledObjects { get; init; } = new Dictionary<long, HashSet<TGI_Key>>();
    }

    // Loaded before set rebuilds start (they may run in parallel and must not share the DbContext).
    private IReadOnlyDictionary<long, HashSet<TGI_Key>> _disabledObjects = new Dictionary<long, HashSet<TGI_Key>>();

    private async Task LoadDisabledObjectsAsync()
    {
        var rows = await _db.DisabledObjects.AsNoTracking()
            .Select(d => new { d.MetaEntityId, d.Type, d.Group, d.Instance })
            .ToListAsync();
        _disabledObjects = rows
            .GroupBy(d => d.MetaEntityId)
            .ToDictionary(g => g.Key, g => g.Select(d => new TGI_Key(d.Type, d.Group, d.Instance)).ToHashSet());
    }

    private sealed class PreparedItem : IDisposable
    {
        public List<DBPFPackage> ToMerge { get; } = new();
        public List<Action> Closers { get; } = new();

        public void Dispose()
        {
            foreach (var close in Closers)
            {
                try { close(); } catch { }
            }
        }
    }

    private void MergeItems(IReadOnlyList<MetaEntity> items, MergeContext ctx, bool parallel, Action<int> onItemDone)
    {
        DBPFPackageBuilder? outputPkg = null;
        int packageCount = 0;
        var addedTgis = new HashSet<TGI_Key>();
        int done = 0;
        var pending = new Queue<Task<PreparedItem?>>();

        void Consume(PreparedItem? prepared)
        {
            if (prepared != null)
            {
                using (prepared)
                {
                    foreach (var package in prepared.ToMerge)
                    {
                        AppendPackageResources(ref outputPkg, ref packageCount, ctx.OutputDir, ctx.BundlePrefix, package, addedTgis);
                    }
                }
            }
            onItemDone(++done);
        }

        try
        {
            if (!parallel)
            {
                foreach (var item in items)
                {
                    Consume(PrepareItem(item, ctx));
                }
            }
            else
            {
                // A bounded window of items is read ahead; results are merged strictly in order.
                int window = Math.Max(2, Environment.ProcessorCount - 1);
                foreach (var item in items)
                {
                    if (pending.Count >= window)
                    {
                        Consume(pending.Dequeue().GetAwaiter().GetResult());
                    }
                    var captured = item;
                    pending.Enqueue(Task.Run(() => PrepareItem(captured, ctx)));
                }
                while (pending.Count > 0)
                {
                    Consume(pending.Dequeue().GetAwaiter().GetResult());
                }
            }
        }
        finally
        {
            // Only reached with items left after a failure: close what was already read.
            while (pending.Count > 0)
            {
                try { pending.Dequeue().GetAwaiter().GetResult()?.Dispose(); } catch { }
            }
            if (outputPkg != null)
            {
                try { outputPkg.Close(); } catch { }
            }
        }
    }

    private PreparedItem? PrepareItem(MetaEntity item, MergeContext ctx)
    {
        string filePath = item.CompleteFileName;
        if (!File.Exists(filePath))
        {
            var fallback = Path.Combine(_options.ManagedPackageFolderPath, item.FileName);
            if (File.Exists(fallback))
            {
                filePath = fallback;
                ctx.FallbackUpdates.Add((item, fallback));
            }
            else
            {
                SafeAddSkippedFile(ctx.SkippedFiles, item.FileName, "File not found on disk");
                ctx.OnProgress?.Invoke(_localizer.GetString("skipping_missing_file", item.FileName));
                return null;
            }
        }

        bool isSims3Pack = item.FileName.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase);
        // Exported lots, Sims, worlds and patterns are named after the item, which is unique in the library.
        string originalName = Path.GetFileNameWithoutExtension(item.FileName);
        var textureCache = item.SkipTextureOptimization ? null : ctx.TextureCache;
        ctx.DisabledObjects.TryGetValue(item.Id, out var disabledObjects);
        var prepared = new PreparedItem();

        try
        {
            IEnumerable<DBPFPackage> packages;
            if (isSims3Pack)
            {
                var sims3Pack = new Sims3Pack(filePath);
                prepared.Closers.Add(sims3Pack.Dispose);
                packages = sims3Pack.Packages;
            }
            else
            {
                var package = new DBPFPackage(filePath);
                prepared.Closers.Add(() => package.Close());
                packages = new[] { package };
            }

            foreach (var package in packages)
            {
                switch (DetectSpecialPackageType(package))
                {
                    case SpecialPackageType.World:
                        lock (NonPackageExportLock) ctx.NonPackageItems.Add(ExportNonPackage(package, "Worlds", originalName, ".world", ctx.NonPackageItems));
                        break;
                    case SpecialPackageType.Lot:
                        lock (NonPackageExportLock) ctx.NonPackageItems.Add(ExportNonPackage(package, "Lots", originalName, ".package", ctx.NonPackageItems));
                        break;
                    case SpecialPackageType.Sim:
                        lock (NonPackageExportLock) ctx.NonPackageItems.Add(ExportNonPackage(package, "Sims", originalName, ".sim", ctx.NonPackageItems));
                        break;
                    case SpecialPackageType.Pattern:
                        lock (NonPackageExportLock) ctx.PatternFiles.Add(InstallAsPattern(package, originalName, ctx.OutputDir, ctx.PatternFiles));
                        break;
                    default:
                        if (ValidatePackage(package))
                        {
                            PreparePackageResources(package, textureCache, disabledObjects);
                            prepared.ToMerge.Add(package);
                        }
                        else
                        {
                            SafeAddSkippedFile(ctx.SkippedFiles, item.FileName, "Failed validation (possible corrupt data)");
                            ctx.OnProgress?.Invoke(_localizer.GetString(isSims3Pack ? "skipping_invalid_package_sims3pack" : "skipping_invalid_package", item.FileName));
                        }
                        break;
                }
            }
            return prepared;
        }
        catch (Exception ex)
        {
            prepared.Dispose();
            SafeAddSkippedFile(ctx.SkippedFiles, item.FileName, ex.Message);
            ctx.OnProgress?.Invoke(_localizer.GetString(isSims3Pack ? "skipping_unreadable_sims3pack" : "skipping_unreadable_file", item.FileName, ex.Message));
            return null;
        }
    }

    private static void DeleteStaleNewFiles(MergeContext ctx)
    {
        try
        {
            foreach (var path in Directory.GetFiles(ctx.OutputDir, $"{ctx.BundlePrefix}*.new"))
            {
                try { File.Delete(path); } catch { }
            }
        }
        catch { }
    }

    private static void FinalizeOutput(MergeContext ctx)
    {
        try
        {
            foreach (string path in Directory.GetFiles(ctx.OutputDir, $"{ctx.BundlePrefix}*.package"))
            {
                try { File.Delete(path); } catch { }
            }

            foreach (string path in Directory.GetFiles(ctx.OutputDir, $"{ctx.BundlePrefix}*.new"))
            {
                try { File.Move(path, Path.ChangeExtension(path, ".package"), overwrite: true); } catch { }
            }

            foreach (string path in Directory.GetFiles(ctx.OutputDir, "Pattern_*.package"))
            {
                if (ctx.PatternFiles.Contains(path)) continue;
                try { File.Delete(path); } catch { }
            }
        }
        catch { }

        string nonPackageFile = Path.Combine(ctx.OutputDir, "NonPackageItems.txt");
        if (ctx.NonPackageItems.Count > 0)
        {
            try { File.WriteAllLines(nonPackageFile, ctx.NonPackageItems.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)); } catch { }
        }
        else if (File.Exists(nonPackageFile))
        {
            try { File.Delete(nonPackageFile); } catch { }
        }
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

    private void PreparePackageResources(DBPFPackage package, TextureOptimizationCache? textureCache = null, HashSet<TGI_Key>? disabledObjects = null)
    {
        var resources = package.Resources;
        if (disabledObjects is { Count: > 0 })
        {
            resources.RemoveAll(r => disabledObjects.Contains(r.Key));
        }
        for (int i = 0; i < resources.Count; i++)
        {
            var resource = resources[i];
            uint type = resource.Key.Type;
            if (type == 3571055589u) FixPTRN(resource);
            else if (type == 53690476) FixPTRN_XML(resource);
            else if (textureCache != null && TextureOptimizer.IsTextureType(type))
            {
                resources[i] = TryUseOptimizedTexture(resource, textureCache) ?? resource;
            }
        }
    }

    private static ResourceEntry? TryUseOptimizedTexture(ResourceEntry resource, TextureOptimizationCache textureCache)
    {
        try
        {
            byte[] raw = resource.ReadRaw();
            var optimized = textureCache.GetOrCreate(raw, () => resource.IsCompressed ? resource.Read() : raw);
            if (optimized == null) return null;
            return new ResourceEntry(new MemoryStream(optimized.Chunk, writable: false), resource.Key, 0, optimized.Chunk.Length,
                optimized.ResourceLength, optimized.IsCompressed, resource.IsEncrypted);
        }
        catch
        {
            return null; // Keep the original texture.
        }
    }

    private void AppendPackageResources(
        ref DBPFPackageBuilder? outputPkg,
        ref int packageCount,
        string targetDirectory,
        string bundlePrefix,
        DBPFPackage inputPkg,
        HashSet<TGI_Key> addedTgis)
    {
        foreach (var resource in inputPkg.Resources)
        {
            if (!ValidateResource(resource.Key)) continue;
            if (!addedTgis.Add(resource.Key)) continue;

            try
            {
                if (outputPkg == null)
                {
                    string path = Path.Combine(targetDirectory, $"{bundlePrefix}{packageCount++}.new");
                    outputPkg = new DBPFPackageBuilder(path, false, catalogFirstLayout: true);
                }

                outputPkg.AddResource(resource);

                if (outputPkg.PackageSize >= MaxBundleSize)
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

    private void FixResKeyColonSpace(ResourceEntry resource, string tagName)
    {
        byte[] data = resource.Read();
        if (data.AsSpan().IndexOf(ColonSpaceBytes) < 0) return;
        string text = System.Text.Encoding.UTF8.GetString(data);

        XmlDocument xmlDocument = new XmlDocument();
        try { xmlDocument.LoadXml(text); } catch { return; }
        bool flag = false;
        XmlNodeList elementsByTagName = xmlDocument.GetElementsByTagName(tagName);
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

    private void FixPTRN(ResourceEntry resource) => FixResKeyColonSpace(resource, "pattern");

    private void FixPTRN_XML(ResourceEntry resource) => FixResKeyColonSpace(resource, "complate");

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
                sw.WriteLine("Priority 1000");
                sw.WriteLine("PackedFile Overrides/*.package");
                sw.WriteLine("PackedFile Overrides/*/*.package");
                sw.WriteLine("PackedFile Overrides/*/*/*.package");
                sw.WriteLine("PackedFile Overrides/*/*/*/*.package");
                sw.WriteLine("PackedFile Overrides/*/*/*/*/*.package");
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

                // Remove invalid relative Scan "Cache/Config/" if previously inserted
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    string trimmed = lines[i].Trim();
                    if (trimmed.Equals("Scan \"Cache/Config/\"", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.Equals("Scan Cache/Config/", StringComparison.OrdinalIgnoreCase))
                    {
                        lines.RemoveAt(i);
                        modified = true;
                    }
                }

                // Ensure PackedFile Cache/Config/Resource.cfg is present
                if (!lines.Any(l => l.Trim().Contains("Cache/Config/Resource.cfg", StringComparison.OrdinalIgnoreCase)))
                {
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
                    modified = true;
                }

                if (modified)
                {
                    File.WriteAllLines(mainResourceCfg, lines);
                }
            }
            catch { }
        }
    }

    private string ExportNonPackage(DBPFPackage package, string subDir, string baseName, string extension, ISet<string> claimed)
    {
        string dir = Path.Combine(_options.DocumentBaseDir, subDir);
        Directory.CreateDirectory(dir);

        string name = SanitizeFileName(baseName);
        if (string.IsNullOrWhiteSpace(name)) name = "Item";

        string path = Path.Combine(dir, $"{name}{extension}");
        int count = 1;
        while (claimed.Contains(path))
        {
            path = Path.Combine(dir, $"{name}_{count}{extension}");
            count++;
        }

        package.Export(path);
        return path;
    }

    private static string InstallAsPattern(DBPFPackage package, string defaultName, string outputDir, ISet<string> claimed)
    {
        string name = SanitizeFileName(defaultName);
        if (string.IsNullOrWhiteSpace(name)) name = "Pattern";
        Directory.CreateDirectory(outputDir);

        string path = Path.Combine(outputDir, $"Pattern_{name}.package");
        int count = 1;
        while (claimed.Contains(path))
        {
            path = Path.Combine(outputDir, $"Pattern_{name}_{count}.package");
            count++;
        }

        package.Export(path);
        return path;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        char[]? buffer = null;
        for (int i = 0; i < name.Length; i++)
        {
            if (InvalidFileNameChars.Contains(name[i]))
            {
                buffer ??= name.ToCharArray();
                buffer[i] = '_';
            }
        }
        return buffer != null ? new string(buffer) : name;
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
