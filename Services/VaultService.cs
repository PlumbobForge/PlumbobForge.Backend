using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlumbobForge.Backend.Configuration;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;
using S3ForgeTools.GameFiles.Textures;
using S3ForgeTools.GameFiles.TS3Pack;
using S3ForgeTools.Utils.Logging;

#nullable enable
namespace PlumbobForge.Backend.Services;

public record VaultMigrationBatchResult(int MigratedCount, long BytesReclaimed, List<long> MigratedIds, List<string> Failures);

public class VaultService
{
    private static readonly ILog log = LogManager.GetLogger(nameof(VaultService));

    private const long CompactionMinDeadBytes = 16L * 1024 * 1024;
    private const double CompactionMinDeadRatio = 0.10;

    // Automatic compaction after edits: only when a shard is badly fragmented, since it rewrites the whole file.
    private const long AutoCompactionDeadBytes = 256L * 1024 * 1024;
    private const long AutoCompactionMinDeadBytes = 64L * 1024 * 1024;
    private const double AutoCompactionDeadRatio = 0.35;

    private const int TextureBatchSize = 64;

    private readonly AppDbContext _db;
    private readonly IOptionsMonitor<PlumbobForgeOptions> _optionsMonitor;
    private readonly CacheBuilderService _cacheBuilderService;
    private readonly PackageTypeService _packageTypeService;
    private readonly ThumbnailService _thumbnailService;
    private readonly LocalizationService _localizer;
    private readonly Sims3HiderService _hiderService;
    private PlumbobForgeOptions Options => _optionsMonitor.CurrentValue;

    private int ConfiguredTextureProfile => TextureOptimizer.GetProfile(Options.TextureMaxDimension);

    private int ActiveTextureProfile => Options.OptimizeTextures ? ConfiguredTextureProfile : 0;

    public VaultService(
        AppDbContext db,
        IOptionsMonitor<PlumbobForgeOptions> optionsMonitor,
        CacheBuilderService cacheBuilderService,
        PackageTypeService packageTypeService,
        ThumbnailService thumbnailService,
        LocalizationService localizer,
        Sims3HiderService hiderService)
    {
        _db = db;
        _optionsMonitor = optionsMonitor;
        _cacheBuilderService = cacheBuilderService;
        _packageTypeService = packageTypeService;
        _thumbnailService = thumbnailService;
        _localizer = localizer;
        _hiderService = hiderService;
    }

    #region Paths

    public static string GetVaultRootDirectory(string sims3UserDir) => Path.Combine(sims3UserDir, "Mods", "PF_Vault");

    public string GetVaultRootDirectory()
    {
        string s3Dir = _cacheBuilderService.GetSims3FolderPath();
        string legacyVaultDir = Path.Combine(s3Dir, "Mods", "Packages", "PF_Vault");
        string vaultDir = GetVaultRootDirectory(s3Dir);

        if (Directory.Exists(legacyVaultDir) && !Directory.Exists(vaultDir))
        {
            try { Directory.Move(legacyVaultDir, vaultDir); }
            catch (Exception ex) { log.Warn($"Could not move legacy vault folder {legacyVaultDir}: {ex.Message}"); }
        }

        return vaultDir;
    }

    public static string GetSetFolderName(long setId) => $"Set_{setId}";

    public static string GetLegacySetFolderName(SetsEntity? set)
    {
        string folderSlug = set != null && !string.IsNullOrWhiteSpace(set.FolderName)
            ? set.FolderName
            : set?.Name ?? "Default";
        return SanitizeFileName(folderSlug);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        name = name.Trim().TrimEnd('.');
        return string.IsNullOrEmpty(name) ? "Set" : name;
    }

    private static bool IsLegacyRelativePath(string relativePath) =>
        relativePath.IndexOf('/') < 0 && relativePath.IndexOf('\\') < 0;

    public static string ResolveShardPath(string vaultRootDir, VaultShardEntity shard, SetsEntity? set)
    {
        if (IsLegacyRelativePath(shard.RelativePath))
        {
            return Path.Combine(vaultRootDir, GetLegacySetFolderName(set), shard.RelativePath);
        }
        return Path.Combine(vaultRootDir, shard.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string GetShardDirectory(SetsEntity set) => Path.Combine(GetVaultRootDirectory(), GetSetFolderName(set.Id));

    public string GetShardFullPath(VaultShardEntity shard)
    {
        var set = shard.SetsEntity;
        if (set == null && IsLegacyRelativePath(shard.RelativePath))
        {
            set = _db.SetsEntities.Find(shard.SetsEntityId);
        }
        return ResolveShardPath(GetVaultRootDirectory(), shard, set);
    }

    #endregion

    #region Shared helpers

    private async Task<List<VaultChunkEntry>> GetIndexedChunksAsync(long shardId)
    {
        int profile = ActiveTextureProfile;
        var rows = await _db.VaultChunks
            .AsNoTracking()
            .Where(c => c.VaultShardId == shardId && c.MetaEntity!.Enabled)
            .Select(c => new { Chunk = c, Skip = c.MetaEntity!.SkipTextureOptimization, ItemId = c.MetaEntity!.ParentMetaEntityId ?? c.MetaEntityId })
            .ToListAsync();
        var disabled = await LoadDisabledObjectKeysAsync();
        return rows
            .Where(r => !disabled.Contains((r.ItemId, r.Chunk.Type, r.Chunk.Group, r.Chunk.Instance)))
            .Select(r => ToIndexEntry(r.Chunk, profile, r.Skip))
            .ToList();
    }

    private async Task<HashSet<(long ItemId, uint Type, uint Group, ulong Instance)>> LoadDisabledObjectKeysAsync()
    {
        var rows = await _db.DisabledObjects.AsNoTracking()
            .Select(d => new { d.MetaEntityId, d.Type, d.Group, d.Instance })
            .ToListAsync();
        return rows.Select(d => (d.MetaEntityId, d.Type, d.Group, d.Instance)).ToHashSet();
    }

    private static bool UsesOptimizedCopy(VaultChunkEntity c, int activeProfile, bool skip) =>
        activeProfile != 0 && !skip && c.OptimizedLength > 0 && c.OptimizedProfile == activeProfile;

    private static VaultChunkEntry ToIndexEntry(VaultChunkEntity c, int activeProfile, bool skip)
    {
        var key = new TGI_Key(c.Type, c.Group, c.Instance);
        if (!UsesOptimizedCopy(c, activeProfile, skip))
        {
            return new VaultChunkEntry(key, c.Offset, c.ChunkLength, c.ResourceLength, c.IsCompressed, c.MetaEntityId, c.Id);
        }
        var (resourceLength, isCompressed) = CopyFormat(c);
        return new VaultChunkEntry(key, c.OptimizedOffset, c.OptimizedLength, resourceLength, isCompressed, c.MetaEntityId, c.Id);
    }

    private static (int ResourceLength, bool IsCompressed) CopyFormat(VaultChunkEntity c) =>
        c.OptimizedResourceLength > 0 ? (c.OptimizedResourceLength, true) : (c.OptimizedLength, false);

    private static void ClearOptimizedCopy(VaultChunkEntity c)
    {
        c.OptimizedOffset = 0;
        c.OptimizedLength = 0;
        c.OptimizedResourceLength = 0;
        c.OptimizedProfile = 0;
    }

    private Task<VaultShardEntity?> LoadShardAsync(long shardId) =>
        _db.VaultShards.Include(s => s.SetsEntity).FirstOrDefaultAsync(s => s.Id == shardId);

    // Shards whose index could not be rewritten because the running game holds them open. Shared by every
    // scope; retried once the game has exited (and re-derived by startup recovery in any case).
    private static readonly ConcurrentDictionary<long, byte> _pendingIndexSync = new();

    public static bool HasPendingIndexSync => !_pendingIndexSync.IsEmpty;

    private async Task<bool> SyncShardIndexAsync(VaultShardEntity shard)
    {
        string path = GetShardFullPath(shard);
        if (!File.Exists(path))
        {
            log.Warn($"Vault shard file missing, cannot update its index: {path}");
            return true;
        }

        var indexed = await GetIndexedChunksAsync(shard.Id);
        try
        {
            DBPFVaultShard.RewriteIndexOnly(path, indexed);
        }
        catch (IOException ex) when (Sims3GameProcess.IsGameRunning())
        {
            log.Warn($"Vault shard {path} is open in the game; its index is updated after the game exits ({ex.Message})");
            _pendingIndexSync[shard.Id] = 0;
            return false;
        }
        _pendingIndexSync.TryRemove(shard.Id, out _);
        shard.ResourceCount = indexed.Count;
        shard.FileSize = new FileInfo(path).Length;
        return true;
    }

    private async Task<bool> RetryPendingIndexSyncAsync()
    {
        if (_pendingIndexSync.IsEmpty) return true;
        if (Sims3GameProcess.IsGameRunning()) return false;

        bool allDone = true;
        foreach (var shardId in _pendingIndexSync.Keys.ToList())
        {
            var shard = await LoadShardAsync(shardId);
            if (shard == null)
            {
                _pendingIndexSync.TryRemove(shardId, out _);
                continue;
            }
            try
            {
                allDone &= await SyncShardIndexAsync(shard);
            }
            catch (Exception ex)
            {
                log.Warn($"Deferred index rewrite of vault shard {shardId} failed: {ex.Message}");
                allDone = false;
            }
        }
        await _db.SaveChangesAsync();
        return allDone;
    }

    private void ReleaseChunkRegions(VaultShardEntity shard, IReadOnlyCollection<(long Offset, int Length)> regions, bool deferPunch = false)
    {
        if (regions.Count == 0) return;

        string path = GetShardFullPath(shard);
        if (!File.Exists(path)) return;

        if (deferPunch || Sims3GameProcess.IsGameRunning())
        {
            foreach (var r in regions)
            {
                _db.VaultHoles.Add(new VaultHoleEntity { VaultShardId = shard.Id, Offset = r.Offset, Length = r.Length, IsPending = true });
            }
            shard.DeadHoleBytes += regions.Sum(r => (long)r.Length);
            return;
        }

        long punched = DBPFVaultShard.PunchHoles(path, regions.Select(r => (r.Offset, (long)r.Length)));
        foreach (var r in regions)
        {
            _db.VaultHoles.Add(new VaultHoleEntity { VaultShardId = shard.Id, Offset = r.Offset, Length = r.Length });
        }
        shard.DeadHoleBytes += punched;
    }

    private async Task SyncAndReleaseAsync(Dictionary<long, List<(long Offset, int Length)>> regionsByShard, IEnumerable<long>? extraShardIds = null)
    {
        var shardIds = regionsByShard.Keys.ToHashSet();
        if (extraShardIds != null) shardIds.UnionWith(extraShardIds);

        await PunchPendingHolesAsync();

        foreach (var shardId in shardIds)
        {
            var shard = await LoadShardAsync(shardId);
            if (shard == null) continue;

            bool indexWritten = await SyncShardIndexAsync(shard);
            if (regionsByShard.TryGetValue(shardId, out var regions))
            {
                // With the old index still on disk the regions are still referenced: only record them.
                ReleaseChunkRegions(shard, regions, deferPunch: !indexWritten);
            }
        }

        await _db.SaveChangesAsync();

        foreach (var shardId in regionsByShard.Keys)
        {
            await CompactIfFragmentedAsync(shardId);
        }

        _cacheBuilderService.OnGameContentChanged();
    }

    private static void AddRegion(Dictionary<long, List<(long, int)>> map, VaultChunkEntity chunk)
    {
        if (!map.TryGetValue(chunk.VaultShardId, out var list))
        {
            list = new List<(long, int)>();
            map[chunk.VaultShardId] = list;
        }
        list.Add((chunk.Offset, chunk.ChunkLength));
        if (chunk.OptimizedLength > 0) list.Add((chunk.OptimizedOffset, chunk.OptimizedLength));
    }

    public async Task<int> PunchPendingHolesAsync()
    {
        // A region may only be zeroed once no index on disk points at it.
        if (!await RetryPendingIndexSyncAsync()) return 0;

        var pending = await _db.VaultHoles.Where(h => h.IsPending).ToListAsync();
        if (pending.Count == 0 || Sims3GameProcess.IsGameRunning()) return 0;

        foreach (var group in pending.GroupBy(h => h.VaultShardId))
        {
            var shard = await LoadShardAsync(group.Key);
            if (shard != null)
            {
                string path = GetShardFullPath(shard);
                if (File.Exists(path))
                {
                    DBPFVaultShard.PunchHoles(path, group.Select(h => (h.Offset, h.Length)));
                }
            }
            foreach (var hole in group) hole.IsPending = false;
        }

        await _db.SaveChangesAsync();
        return pending.Count;
    }

    private static List<VaultChunkEntity> ToChunkEntities(IEnumerable<VaultChunkEntry> entries, long metaId, long shardId) =>
        entries.Select(e => new VaultChunkEntity
        {
            MetaEntityId = metaId,
            VaultShardId = shardId,
            Type = e.Key.Type,
            Group = e.Key.Group,
            Instance = e.Key.Instance,
            Offset = e.Offset,
            ChunkLength = e.ChunkLength,
            ResourceLength = e.ResourceLength,
            IsCompressed = e.IsCompressed
        }).ToList();

    private static List<RawChunkPayload> ReadPackagePayloads(DBPFPackage package, long metaId, bool indexed) =>
        package.Resources.Select(res => new RawChunkPayload(res.Key, res.ReadRaw(), (int)res.Length, res.IsCompressed, metaId) { Indexed = indexed }).ToList();

    private static long EstimateBytes(IReadOnlyCollection<RawChunkPayload> chunks) =>
        chunks.Sum(c => (long)c.ChunkLength) + (chunks.Count * 32L) + 1024;

    private async Task<List<MetaEntity>> LoadWithChildrenAsync(List<long> idList, bool vaultOnly)
    {
        var metas = await _db.MetaEntities
            .Include(m => m.VaultChunks)
            .Include(m => m.SetsEntity)
            .Where(m => idList.Contains(m.Id) && (!vaultOnly || m.IsVaultManaged))
            .ToListAsync();

        var parentIds = metas.Select(m => m.Id).ToList();
        var children = await _db.MetaEntities
            .Include(m => m.VaultChunks)
            .Include(m => m.SetsEntity)
            .Where(m => m.ParentMetaEntityId.HasValue && parentIds.Contains(m.ParentMetaEntityId.Value) && (!vaultOnly || m.IsVaultManaged))
            .ToListAsync();

        foreach (var child in children)
        {
            if (!metas.Any(m => m.Id == child.Id)) metas.Add(child);
        }
        return metas;
    }

    #endregion

    #region Shard allocation

    public async Task<(VaultShardEntity Shard, string FullPath)> GetOrCreateTargetShardAsync(SetsEntity set, long estimatedModSizeBytes)
    {
        var shards = await _db.VaultShards
            .Include(s => s.SetsEntity)
            .Where(s => s.SetsEntityId == set.Id)
            .OrderBy(s => s.ShardIndex)
            .ToListAsync();

        VaultShardEntity? lastShard = shards.LastOrDefault();
        if (lastShard != null)
        {
            string lastPath = GetShardFullPath(lastShard);
            bool fileExists = File.Exists(lastPath);
            // A shard whose file vanished must not be recreated in place: its chunk rows point at offsets that no longer exist.
            bool reusable = fileExists || !await _db.VaultChunks.AnyAsync(c => c.VaultShardId == lastShard.Id);
            long currentLength = fileExists ? new FileInfo(lastPath).Length : 0;

            if (reusable && currentLength + estimatedModSizeBytes <= DBPFVaultShard.MaxShardSizeBytes)
            {
                DBPFVaultShard.EnsureShardCreated(lastPath);
                return (lastShard, lastPath);
            }
        }

        int nextIndex = lastShard != null ? lastShard.ShardIndex + 1 : 0;
        string folderName = GetSetFolderName(set.Id);
        string shardDir = Path.Combine(GetVaultRootDirectory(), folderName);
        Directory.CreateDirectory(shardDir);

        string filePrefix = SanitizeFileName(!string.IsNullOrWhiteSpace(set.FolderName) ? set.FolderName : set.Name);
        string shardFileName = $"{filePrefix}_{nextIndex}.package";
        int suffix = 0;
        while (File.Exists(Path.Combine(shardDir, shardFileName)))
        {
            // Never adopt a stray file that the database does not know about.
            shardFileName = $"{filePrefix}_{nextIndex}_{++suffix}.package";
        }

        var targetShard = new VaultShardEntity
        {
            SetsEntityId = set.Id,
            ShardIndex = nextIndex,
            RelativePath = $"{folderName}/{shardFileName}",
            FileSize = 0,
            DeadHoleBytes = 0,
            ResourceCount = 0
        };

        _db.VaultShards.Add(targetShard);
        await _db.SaveChangesAsync();

        string fullPath = Path.Combine(shardDir, shardFileName);
        DBPFVaultShard.EnsureShardCreated(fullPath);
        return (targetShard, fullPath);
    }

    #endregion

    #region Import

    public async Task<MetaEntity> ImportPackageDirectAsync(string packageFilePath, SetsEntity targetSet, Action<string>? onProgress = null)
    {
        string fileName = Path.GetFileName(packageFilePath);
        var fileInfo = new FileInfo(packageFilePath);
        long sourceLength = fileInfo.Length;

        onProgress?.Invoke($"Inspecting package: {fileName}");

        List<RawChunkPayload> rawChunks;
        PackageTypeInfo typeInfo;

        using (var sourcePkg = new DBPFPackage(packageFilePath))
        {
            typeInfo = _packageTypeService.DetectPackageType(packageFilePath, false);
            rawChunks = ReadPackagePayloads(sourcePkg, 0, indexed: true);
        }

        var (shard, shardFullPath) = await GetOrCreateTargetShardAsync(targetSet, EstimateBytes(rawChunks));

        onProgress?.Invoke($"Streaming {rawChunks.Count} chunks to vault shard: {Path.GetFileName(shardFullPath)}");

        var existingIndexed = await GetIndexedChunksAsync(shard.Id);
        var appendedEntries = DBPFVaultShard.AppendChunksAndCommit(shardFullPath, rawChunks, existingIndexed);

        var meta = new MetaEntity
        {
            FileName = fileName,
            CompleteFileName = shardFullPath,
            FileType = "DBPF",
            FileSize = sourceLength / 1024.0,
            Filehash = $"{sourceLength}_{fileInfo.LastWriteTimeUtc.Ticks}",
            PackageType = typeInfo.PackageType,
            CASCategories = typeInfo.CASCategories,
            CASAge = typeInfo.CASAge,
            CASGender = typeInfo.CASGender,
            CASOutfitCategory = typeInfo.CASOutfitCategory,
            Enabled = true,
            IsVaultManaged = true,
            SetsEntityId = targetSet.Id,
            VaultShardId = shard.Id,
            InstallDate = DateTime.Now.ToString()
        };
        typeInfo.ApplyBuildBuyTo(meta);

        _db.MetaEntities.Add(meta);
        await _db.SaveChangesAsync();

        _db.VaultChunks.AddRange(ToChunkEntities(appendedEntries, meta.Id, shard.Id));
        shard.FileSize = new FileInfo(shardFullPath).Length;
        shard.ResourceCount = existingIndexed.Count + appendedEntries.Count;
        await _db.SaveChangesAsync();

        await OptimizeNewModsAsync(new[] { meta.Id });
        _cacheBuilderService.OnGameContentChanged();

        // Extract thumbnail directly to disk cache
        await _thumbnailService.GetThumbnailPathAsync(meta.Id, shardFullPath);

        // Delete source package only after commit
        try
        {
            File.Delete(packageFilePath);
        }
        catch (Exception ex)
        {
            log.Warn($"Could not delete source package {packageFilePath} after vault import: {ex.Message}");
        }

        onProgress?.Invoke($"Successfully imported {fileName} to vault.");
        return meta;
    }

    private MetaEntity ExportLibrarySubItem(Action<Stream> writeContent, string fileName, string packageType, MetaEntity parentMeta, SetsEntity targetSet, bool enabled)
    {
        string libraryDir = Options.ManagedPackageFolderPath;
        Directory.CreateDirectory(libraryDir);
        var (destPath, _) = ArchiveService.ResolveDestinationPath(libraryDir, fileName, "rename");
        destPath ??= Path.Combine(libraryDir, $"{Guid.NewGuid().ToString("N")[..8]}_{fileName}");
        using (var fs = File.Create(destPath)) writeContent(fs);

        var info = new FileInfo(destPath);
        var child = new MetaEntity
        {
            FileName = Path.GetFileName(destPath),
            CompleteFileName = destPath,
            FileType = packageType == PackageTypeHelper.World ? "WORLD" : packageType == PackageTypeHelper.Sim ? "SIM" : "DBPF",
            FileSize = info.Length / 1024.0,
            Filehash = $"{info.Length}_{info.LastWriteTimeUtc.Ticks}",
            PackageType = packageType,
            Enabled = enabled,
            IsVaultManaged = false,
            ParentMetaEntityId = parentMeta.Id,
            SetsEntityId = targetSet.Id,
            InstallDate = DateTime.Now.ToString(),
            Manifest = string.Empty
        };
        _db.MetaEntities.Add(child);
        return child;
    }

    /// <param name="deleteSource">False during migration, where the library copy must survive until it is backed up.</param>
    public async Task<MetaEntity?> ImportSims3PackDirectAsync(string sims3PackPath, SetsEntity targetSet, Action<string>? onProgress = null, MetaEntity? existingMeta = null, bool deleteSource = true)
    {
        string fileName = Path.GetFileName(sims3PackPath);
        var fileInfo = new FileInfo(sims3PackPath);
        onProgress?.Invoke($"Streaming Sims3Pack to vault: {fileName}");

        string s3Dir = _cacheBuilderService.GetSims3FolderPath();

        using var s3p = new Sims3Pack(sims3PackPath);
        if (s3p.IsCorrupt)
        {
            log.Warn($"Sims3Pack is corrupt: {sims3PackPath}");
            return null;
        }

        MetaEntity parentMeta;
        if (existingMeta != null)
        {
            parentMeta = existingMeta;
            parentMeta.FileName = fileName;
            parentMeta.FileType = "TS3PACK";
            parentMeta.FileSize = fileInfo.Length / 1024.0;
            parentMeta.Filehash = $"{fileInfo.Length}_{fileInfo.LastWriteTimeUtc.Ticks}";
            parentMeta.PackageType = "Sims3Pack";
            parentMeta.IsVaultManaged = true;
            parentMeta.SetsEntityId = targetSet.Id;
        }
        else
        {
            parentMeta = new MetaEntity
            {
                FileName = fileName,
                CompleteFileName = sims3PackPath,
                FileType = "TS3PACK",
                FileSize = fileInfo.Length / 1024.0,
                Filehash = $"{fileInfo.Length}_{fileInfo.LastWriteTimeUtc.Ticks}",
                PackageType = "Sims3Pack",
                Enabled = true,
                IsVaultManaged = true,
                SetsEntityId = targetSet.Id,
                InstallDate = DateTime.Now.ToString()
            };
            _db.MetaEntities.Add(parentMeta);
        }

        await _db.SaveChangesAsync();

        bool enabled = parentMeta.Enabled;
        int subPackageIndex = 0;
        var childIds = new List<long>();
        foreach (var subPkg in s3p.Packages)
        {
            subPackageIndex++;
            string subName = $"{Path.GetFileNameWithoutExtension(fileName)}_Part{subPackageIndex}.package";

            // Lots, Sims and worlds are never stored in shards: they become regular library sub-items, which the sync
            // deploys to the game's Library, SavedSims or Worlds folder like any other library lot, Sim or world.
            string? specialType = subPkg.Resources.Any(r => r.Key.Type == 107542056) ? PackageTypeHelper.World
                : subPkg.Resources.Any(r => r.Key.Type == 3496170587u) ? PackageTypeHelper.Lot
                : subPkg.Resources.Any(r => r.Key.Type == 83396964) ? PackageTypeHelper.Sim
                : null;
            if (specialType != null)
            {
                string ext = specialType == PackageTypeHelper.World ? ".world" : specialType == PackageTypeHelper.Sim ? ".sim" : ".package";
                ExportLibrarySubItem(fs => subPkg.Export(fs), Path.ChangeExtension(subName, ext), specialType, parentMeta, targetSet, enabled);
                await _db.SaveChangesAsync();
                continue;
            }

            // Normal CC subpackage -> stream directly to shard
            var rawChunks = ReadPackagePayloads(subPkg, 0, enabled);
            long totalChunkSize = rawChunks.Sum(c => (long)c.ChunkLength);
            var (shard, shardFullPath) = await GetOrCreateTargetShardAsync(targetSet, EstimateBytes(rawChunks));

            var existingIndexed = await GetIndexedChunksAsync(shard.Id);
            var appended = DBPFVaultShard.AppendChunksAndCommit(shardFullPath, rawChunks, existingIndexed);

            var childMeta = new MetaEntity
            {
                FileName = subName,
                CompleteFileName = shardFullPath,
                FileType = "DBPF",
                FileSize = totalChunkSize / 1024.0,
                Filehash = $"{totalChunkSize}_{DateTime.UtcNow.Ticks}",
                PackageType = "Other",
                Enabled = enabled,
                IsVaultManaged = true,
                ParentMetaEntityId = parentMeta.Id,
                SetsEntityId = targetSet.Id,
                VaultShardId = shard.Id,
                InstallDate = DateTime.Now.ToString()
            };

            _db.MetaEntities.Add(childMeta);
            await _db.SaveChangesAsync();

            _db.VaultChunks.AddRange(ToChunkEntities(appended, childMeta.Id, shard.Id));
            shard.FileSize = new FileInfo(shardFullPath).Length;
            shard.ResourceCount = existingIndexed.Count + (enabled ? appended.Count : 0);
            if (string.IsNullOrEmpty(parentMeta.CompleteFileName) || parentMeta.CompleteFileName == sims3PackPath)
            {
                parentMeta.CompleteFileName = shardFullPath;
                parentMeta.VaultShardId = shard.Id;
            }
            await _db.SaveChangesAsync();
            childIds.Add(childMeta.Id);
        }

        // A pack of only lots, Sims or worlds has no shard part; never leave the parent pointing at the deleted source.
        if (parentMeta.CompleteFileName == sims3PackPath)
        {
            var firstLibraryChild = _db.MetaEntities.Local.FirstOrDefault(m => m.ParentMetaEntityId == parentMeta.Id && !m.IsVaultManaged);
            if (firstLibraryChild != null)
            {
                parentMeta.CompleteFileName = firstLibraryChild.CompleteFileName;
                await _db.SaveChangesAsync();
            }
        }

        await OptimizeNewModsAsync(childIds);
        _cacheBuilderService.OnGameContentChanged();

        if (s3p.Thumbnails != null && s3p.Thumbnails.Count > 0)
        {
            try
            {
                using var thumbMs = new MemoryStream();
                s3p.Thumbnails[0].Position = 0;
                s3p.Thumbnails[0].CopyTo(thumbMs);
                byte[] thumbData = thumbMs.ToArray();
                string thumbDir = _thumbnailService.GetThumbnailDirectory();
                string thumbPath = Path.Combine(thumbDir, $"{parentMeta.Id}.thumb");
                ThumbnailService.SaveOptimizedThumbnail(thumbData, thumbPath);
            }
            catch (Exception ex)
            {
                log.Warn($"Could not extract thumbnail for Sims3Pack {fileName}: {ex.Message}");
            }
        }

        if (deleteSource)
        {
            try
            {
                s3p.Dispose();
                File.Delete(sims3PackPath);
            }
            catch (Exception ex)
            {
                log.Warn($"Could not delete source Sims3Pack {sims3PackPath}: {ex.Message}");
            }
        }

        onProgress?.Invoke($"Finished streaming Sims3Pack: {fileName}");
        return parentMeta;
    }

    #endregion

    #region Toggle / move / delete

    public async Task<bool> ToggleModAsync(long metaEntityId, bool? newEnabledState = null)
    {
        return await ToggleModsBatchAsync(new[] { metaEntityId }, newEnabledState);
    }

    public async Task<bool> ToggleModsBatchAsync(IEnumerable<long> metaEntityIds, bool? newEnabledState = null)
    {
        var idList = metaEntityIds.Distinct().ToList();
        if (idList.Count == 0) return true;

        var metas = await _db.MetaEntities
            .Where(m => idList.Contains(m.Id) && m.IsVaultManaged)
            .ToListAsync();

        if (metas.Count == 0) return true;

        var targetById = metas.ToDictionary(m => m.Id, m => newEnabledState ?? !m.Enabled);

        // Sub-packages of a Sims3Pack follow their parent's new state.
        var parentIds = metas.Select(m => m.Id).ToList();
        var childMetas = await _db.MetaEntities
            .Where(m => m.ParentMetaEntityId.HasValue && parentIds.Contains(m.ParentMetaEntityId.Value) && m.IsVaultManaged)
            .ToListAsync();

        foreach (var child in childMetas)
        {
            if (targetById.ContainsKey(child.Id)) continue;
            metas.Add(child);
            targetById[child.Id] = targetById[child.ParentMetaEntityId!.Value];
        }

        var affectedShardIds = new HashSet<long>();
        foreach (var meta in metas)
        {
            bool targetState = targetById[meta.Id];
            if (meta.Enabled == targetState) continue;

            meta.Enabled = targetState;
            if (meta.VaultShardId.HasValue) affectedShardIds.Add(meta.VaultShardId.Value);
        }

        // Lot, Sim and world parts of a Sims3Pack are library files; the sync deploys or removes them.
        var libraryChildren = await _db.MetaEntities
            .Where(m => m.ParentMetaEntityId.HasValue && parentIds.Contains(m.ParentMetaEntityId.Value) && !m.IsVaultManaged)
            .ToListAsync();
        bool libraryChanged = false;
        foreach (var child in libraryChildren)
        {
            bool targetState = targetById[child.ParentMetaEntityId!.Value];
            if (child.Enabled == targetState) continue;
            child.Enabled = targetState;
            libraryChanged = true;
        }

        // The index is derived from the committed state, so save first.
        await _db.SaveChangesAsync();
        await SyncAndReleaseAsync(new Dictionary<long, List<(long, int)>>(), affectedShardIds);
        if (libraryChanged) await _cacheBuilderService.SyncToSims3Async();
        return true;
    }

    public async Task<bool> MoveModAsync(long metaEntityId, SetsEntity targetSet)
    {
        return await MoveModsBatchAsync(new[] { metaEntityId }, targetSet);
    }

    public async Task<bool> MoveModsBatchAsync(IEnumerable<long> metaEntityIds, SetsEntity targetSet)
    {
        var idList = metaEntityIds.Distinct().ToList();
        if (idList.Count == 0) return true;

        var metas = await LoadWithChildrenAsync(idList, vaultOnly: true);
        if (metas.Count == 0) return true;

        bool allMoved = true;

        foreach (var meta in metas)
        {
            if (meta.SetsEntityId == targetSet.Id)
            {
                continue;
            }

            if (meta.VaultChunks.Count == 0 || !meta.VaultShardId.HasValue)
            {
                meta.SetsEntityId = targetSet.Id;
                await _db.SaveChangesAsync();
                continue;
            }

            var sourceShard = await LoadShardAsync(meta.VaultShardId.Value);
            string? sourceFullPath = sourceShard != null ? GetShardFullPath(sourceShard) : null;
            if (sourceShard == null || sourceFullPath == null || !File.Exists(sourceFullPath))
            {
                log.Warn($"Cannot move vault mod {meta.FileName}: its shard file is missing");
                allMoved = false;
                continue;
            }

            // 1. Copy the chunks, and any optimised texture copies, into the destination shard
            var oldChunks = meta.VaultChunks.ToList();
            var withCopies = oldChunks.Where(c => c.OptimizedLength > 0).ToList();
            var reads = oldChunks.Select(c => (c.Offset, c.ChunkLength))
                .Concat(withCopies.Select(c => (c.OptimizedOffset, c.OptimizedLength)))
                .ToList();
            var raws = DBPFVaultShard.ReadRawChunks(sourceFullPath, reads);
            var payloads = oldChunks.Select((c, i) => new RawChunkPayload(
                new TGI_Key(c.Type, c.Group, c.Instance),
                raws[i],
                c.ResourceLength,
                c.IsCompressed,
                meta.Id) { Indexed = meta.Enabled }).ToList();
            payloads.AddRange(withCopies.Select((c, i) => new RawChunkPayload(
                new TGI_Key(c.Type, c.Group, c.Instance),
                raws[oldChunks.Count + i],
                CopyFormat(c).ResourceLength,
                CopyFormat(c).IsCompressed,
                meta.Id) { Indexed = false }));

            var (destShard, destFullPath) = await GetOrCreateTargetShardAsync(targetSet, EstimateBytes(payloads));
            var destIndexed = await GetIndexedChunksAsync(destShard.Id);
            var appendedEntries = DBPFVaultShard.AppendChunksAndCommit(destFullPath, payloads, destIndexed);

            // 2. Commit the new mapping
            var oldRegions = new Dictionary<long, List<(long, int)>>();
            foreach (var c in oldChunks) AddRegion(oldRegions, c);

            var newChunks = ToChunkEntities(appendedEntries.Take(oldChunks.Count), meta.Id, destShard.Id);
            for (int i = 0; i < oldChunks.Count; i++)
            {
                // -1 ("keep original") verdicts carry over too; only real copies need a new offset.
                newChunks[i].OptimizedLength = oldChunks[i].OptimizedLength;
                newChunks[i].OptimizedResourceLength = oldChunks[i].OptimizedResourceLength;
                newChunks[i].OptimizedProfile = oldChunks[i].OptimizedProfile;
            }
            for (int i = 0; i < withCopies.Count; i++)
            {
                newChunks[oldChunks.IndexOf(withCopies[i])].OptimizedOffset = appendedEntries[oldChunks.Count + i].Offset;
            }

            _db.VaultChunks.RemoveRange(oldChunks);
            _db.VaultChunks.AddRange(newChunks);
            meta.SetsEntityId = targetSet.Id;
            meta.VaultShardId = destShard.Id;
            meta.CompleteFileName = destFullPath;
            destShard.FileSize = new FileInfo(destFullPath).Length;
            await _db.SaveChangesAsync();

            // 3. Point the destination index at the right copies, drop the old chunks from the source index, then free them
            await SyncAndReleaseAsync(oldRegions, new[] { destShard.Id });
        }

        // Sims3Pack parents carry no chunks; point them at a moved sub-package.
        foreach (var parent in metas.Where(m => m.VaultChunks.Count == 0))
        {
            var child = metas.FirstOrDefault(m => m.ParentMetaEntityId == parent.Id && m.VaultShardId.HasValue && m.SetsEntityId == targetSet.Id);
            if (child != null)
            {
                parent.VaultShardId = child.VaultShardId;
                parent.CompleteFileName = child.CompleteFileName;
            }
        }

        // Lot, Sim and world parts of a moved Sims3Pack are library files: only their set changes.
        var movedParentIds = metas.Where(m => m.SetsEntityId == targetSet.Id).Select(m => m.Id).ToList();
        var libraryChildren = await _db.MetaEntities
            .Where(m => m.ParentMetaEntityId.HasValue && movedParentIds.Contains(m.ParentMetaEntityId.Value) && !m.IsVaultManaged && m.SetsEntityId != targetSet.Id)
            .ToListAsync();
        foreach (var child in libraryChildren) child.SetsEntityId = targetSet.Id;

        await _db.SaveChangesAsync();
        if (libraryChildren.Count > 0) await _cacheBuilderService.SyncToSims3Async();
        return allMoved;
    }

    public async Task<bool> DeleteModAsync(long metaEntityId)
    {
        return await DeleteModsBatchAsync(new[] { metaEntityId });
    }

    /// <param name="removeLibraryFile">
    /// Disposes of the library file of a lot, Sim or world part of a Sims3Pack (for example by sending it to the
    /// Recycle Bin). Defaults to deleting it.
    /// </param>
    public async Task<bool> DeleteModsBatchAsync(IEnumerable<long> metaEntityIds, Action<string>? removeLibraryFile = null)
    {
        var idList = metaEntityIds.Distinct().ToList();
        if (idList.Count == 0) return true;

        var metas = await LoadWithChildrenAsync(idList, vaultOnly: false);
        if (metas.Count == 0) return true;

        var regions = new Dictionary<long, List<(long, int)>>();
        var allChunks = metas.SelectMany(m => m.VaultChunks).ToList();
        foreach (var c in allChunks) AddRegion(regions, c);

        _db.VaultChunks.RemoveRange(allChunks);
        _db.MetaEntities.RemoveRange(metas);
        await _db.SaveChangesAsync();

        await SyncAndReleaseAsync(regions);

        var libraryFiles = metas.Where(m => !m.IsVaultManaged && File.Exists(m.CompleteFileName)).Select(m => m.CompleteFileName).ToList();
        foreach (var path in libraryFiles)
        {
            try
            {
                if (removeLibraryFile != null) removeLibraryFile(path);
                else File.Delete(path);
            }
            catch (Exception ex)
            {
                log.Warn($"Could not remove library file {path}: {ex.Message}");
            }
        }
        // Takes deployed copies of deleted lots and Sims out of the game folders.
        if (metas.Any(m => !m.IsVaultManaged)) await _cacheBuilderService.SyncToSims3Async();

        foreach (var m in metas)
        {
            _thumbnailService.DeleteThumbnail(m.Id);
        }

        return true;
    }

    public async Task DeleteEmptyShardsForSetAsync(long setId)
    {
        // The game may still hold the file open; an empty shard is harmless and is removed on a later call.
        if (Sims3GameProcess.IsGameRunning()) return;

        var shards = await _db.VaultShards
            .Include(s => s.SetsEntity)
            .Where(s => s.SetsEntityId == setId && !s.Chunks.Any())
            .ToListAsync();

        foreach (var shard in shards)
        {
            string path = GetShardFullPath(shard);
            try
            {
                if (File.Exists(path)) File.Delete(path);
                _db.VaultShards.Remove(shard);
            }
            catch (Exception ex)
            {
                log.Warn($"Could not delete empty vault shard {path}: {ex.Message}");
            }
        }
        await _db.SaveChangesAsync();

        string setDir = Path.Combine(GetVaultRootDirectory(), GetSetFolderName(setId));
        try
        {
            if (Directory.Exists(setDir) && !Directory.EnumerateFileSystemEntries(setDir).Any())
            {
                Directory.Delete(setDir);
            }
        }
        catch { }
    }

    #endregion

    #region Migration

    public async Task<VaultMigrationBatchResult> MigrateSetBatchAsync(
        SetsEntity targetSet,
        List<MetaEntity> metas,
        Action<string>? onProgress = null,
        Action<int, string>? onItemProgress = null)
    {
        var migratedIds = new List<long>();
        var failures = new List<string>();
        if (metas.Count == 0) return new VaultMigrationBatchResult(0, 0, migratedIds, failures);

        int migratedCount = 0;
        long bytesReclaimed = 0;
        const long maxShardBytes = 950 * 1024 * 1024;
        const int batchPackageLimit = 40;

        int metaIdx = 0;
        while (metaIdx < metas.Count)
        {
            var currentBatch = new List<(MetaEntity Meta, List<RawChunkPayload> Chunks)>();
            long batchBytes = 0;

            while (metaIdx < metas.Count && currentBatch.Count < batchPackageLimit)
            {
                var meta = metas[metaIdx++];
                // Lots, Sims and worlds stay library items: the game only reads them from its own folders.
                if (PackageTypeHelper.IsLibraryOnlyType(meta.PackageType)) continue;

                string filePath = meta.CompleteFileName;
                if (!File.Exists(filePath))
                {
                    string fallback = Path.Combine(Options.ManagedPackageFolderPath, meta.FileName);
                    if (File.Exists(fallback)) filePath = fallback;
                }
                if (!File.Exists(filePath))
                {
                    failures.Add($"{meta.FileName}: file not found");
                    continue;
                }

                if (filePath.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var parent = await ImportSims3PackDirectAsync(filePath, targetSet, onProgress, existingMeta: meta, deleteSource: false);
                        if (parent != null)
                        {
                            migratedCount++;
                            migratedIds.Add(meta.Id);
                            bytesReclaimed += (long)(meta.FileSize * 1024.0);
                            onItemProgress?.Invoke(migratedCount, meta.FileName);
                        }
                        else
                        {
                            failures.Add($"{meta.FileName}: corrupt Sims3Pack");
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Warn($"Error migrating Sims3Pack {meta.FileName} to vault: {ex.Message}");
                        failures.Add($"{meta.FileName}: {ex.Message}");
                    }
                    continue;
                }

                // .world / .sim and anything else stays in the library.
                if (!filePath.EndsWith(".package", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    List<RawChunkPayload> rawChunks;
                    using (var sourcePkg = new DBPFPackage(filePath))
                    {
                        rawChunks = ReadPackagePayloads(sourcePkg, meta.Id, meta.Enabled);
                    }

                    long pkgBytes = rawChunks.Sum(c => (long)c.ChunkLength) + (rawChunks.Count * 32);
                    if (currentBatch.Count > 0 && (batchBytes + pkgBytes) > maxShardBytes)
                    {
                        metaIdx--;
                        break;
                    }

                    currentBatch.Add((meta, rawChunks));
                    batchBytes += pkgBytes;
                }
                catch (Exception ex)
                {
                    log.Warn($"Error reading package {meta.FileName} for batch vault migration: {ex.Message}");
                    failures.Add($"{meta.FileName}: {ex.Message}");
                }
            }

            if (currentBatch.Count == 0) continue;

            var (shard, shardFullPath) = await GetOrCreateTargetShardAsync(targetSet, batchBytes + 65536);
            var existingIndexed = await GetIndexedChunksAsync(shard.Id);

            foreach (var item in currentBatch)
            {
                onProgress?.Invoke($"Writing {item.Meta.FileName} into shard...");
            }

            var allPayloads = currentBatch.SelectMany(i => i.Chunks).ToList();
            var appended = DBPFVaultShard.AppendChunksAndCommit(shardFullPath, allPayloads, existingIndexed);

            int indexedAppended = 0;
            int cursor = 0;
            foreach (var item in currentBatch)
            {
                var entries = appended.Skip(cursor).Take(item.Chunks.Count).ToList();
                cursor += item.Chunks.Count;
                if (item.Meta.Enabled) indexedAppended += entries.Count;

                _db.VaultChunks.AddRange(ToChunkEntities(entries, item.Meta.Id, shard.Id));
                item.Meta.IsVaultManaged = true;
                item.Meta.VaultShardId = shard.Id;
                item.Meta.CompleteFileName = shardFullPath;

                migratedCount++;
                migratedIds.Add(item.Meta.Id);
                bytesReclaimed += (long)(item.Meta.FileSize * 1024.0);
                onItemProgress?.Invoke(migratedCount, item.Meta.FileName);
            }

            shard.FileSize = new FileInfo(shardFullPath).Length;
            shard.ResourceCount = existingIndexed.Count + indexedAppended;
            await _db.SaveChangesAsync();

            await OptimizeNewModsAsync(currentBatch.Select(i => i.Meta.Id).ToList());
        }

        if (migratedCount > 0) _cacheBuilderService.OnGameContentChanged();
        return new VaultMigrationBatchResult(migratedCount, bytesReclaimed, migratedIds, failures);
    }

    #endregion

    #region Export / replace

    private async Task<bool> ExportOwnChunksAsync(MetaEntity meta, string destinationFilePath)
    {
        if (meta.VaultChunks.Count == 0 || !meta.VaultShardId.HasValue) return false;

        var shard = await LoadShardAsync(meta.VaultShardId.Value);
        if (shard == null) return false;

        string shardPath = GetShardFullPath(shard);
        if (!File.Exists(shardPath)) return false;

        var entries = meta.VaultChunks.Select(c => new VaultChunkEntry(
            new TGI_Key(c.Type, c.Group, c.Instance),
            c.Offset,
            c.ChunkLength,
            c.ResourceLength,
            c.IsCompressed)).ToList();

        string? dir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tempPath = destinationFilePath + ".pfexport";
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                DBPFVaultShard.ExportStandalonePackage(shardPath, entries, fs);
            }
            File.Move(tempPath, destinationFilePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
        return true;
    }

    public async Task<bool> ExportSinglePackageAsync(long metaEntityId, string destinationFilePath)
    {
        var meta = await _db.MetaEntities
            .Include(m => m.VaultChunks)
            .FirstOrDefaultAsync(m => m.Id == metaEntityId);
        return meta != null && await ExportOwnChunksAsync(meta, destinationFilePath);
    }

    public async Task<bool> ExportModAsync(long metaEntityId, string destinationFilePath)
    {
        var meta = await _db.MetaEntities
            .Include(m => m.VaultChunks)
            .FirstOrDefaultAsync(m => m.Id == metaEntityId);

        if (meta == null)
            return false;

        string? dir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (!meta.IsVaultManaged)
        {
            if (File.Exists(meta.CompleteFileName))
            {
                File.Copy(meta.CompleteFileName, destinationFilePath, true);
                return true;
            }
            return false;
        }

        if (meta.VaultChunks.Count > 0)
        {
            return await ExportOwnChunksAsync(meta, destinationFilePath);
        }

        var children = await _db.MetaEntities
            .Include(m => m.VaultChunks)
            .Where(m => m.ParentMetaEntityId == meta.Id && m.VaultChunks.Count > 0)
            .OrderBy(m => m.Id)
            .ToListAsync();

        // Lot, Sim and world parts are library files; they are copied out under their own names.
        var libraryParts = await _db.MetaEntities
            .Where(m => m.ParentMetaEntityId == meta.Id && !m.IsVaultManaged)
            .OrderBy(m => m.Id)
            .ToListAsync();

        bool any = false;
        foreach (var part in libraryParts)
        {
            if (!File.Exists(part.CompleteFileName)) continue;
            File.Copy(part.CompleteFileName, Path.Combine(dir ?? "", part.FileName), true);
            any = true;
        }

        if (children.Count == 0) return any;
        if (children.Count == 1 && libraryParts.Count == 0) return await ExportOwnChunksAsync(children[0], destinationFilePath);

        string baseName = Path.GetFileNameWithoutExtension(destinationFilePath);
        for (int i = 0; i < children.Count; i++)
        {
            // Next to library parts, keep each part's own "{pack}_PartN" name so the numbering cannot collide.
            string partPath = libraryParts.Count > 0
                ? Path.Combine(dir ?? "", children[i].FileName)
                : Path.Combine(dir ?? "", $"{baseName}_Part{i + 1}.package");
            any |= await ExportOwnChunksAsync(children[i], partPath);
        }
        return any;
    }

    public async Task<bool> ReplaceModAsync(long metaEntityId, string replacementFilePath)
    {
        if (!File.Exists(replacementFilePath))
            return false;

        var meta = await _db.MetaEntities
            .Include(m => m.SetsEntity)
            .Include(m => m.VaultChunks)
            .FirstOrDefaultAsync(m => m.Id == metaEntityId);

        if (meta == null || !meta.IsVaultManaged)
            return false;

        var oldChildren = await _db.MetaEntities
            .Include(m => m.VaultChunks)
            .Where(m => m.ParentMetaEntityId == meta.Id)
            .ToListAsync();

        SetsEntity targetSet = meta.SetsEntity ?? await _db.SetsEntities.FirstOrDefaultAsync(s => s.Id == (meta.SetsEntityId ?? 1))
            ?? await _db.SetsEntities.FirstAsync();

        // 1. Read new package chunks (nothing is changed if this fails)
        var rawChunks = new List<RawChunkPayload>();
        // Lot, Sim and world parts of a replacement Sims3Pack become library sub-items, never shard chunks.
        var librarySubItems = new List<(byte[] Data, string PackageType)>();
        PackageTypeInfo typeInfo;
        bool isSims3PackReplacement = replacementFilePath.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (isSims3PackReplacement)
            {
                using var s3p = new Sims3Pack(replacementFilePath);
                if (s3p.IsCorrupt) return false;
                foreach (var subPkg in s3p.Packages)
                {
                    string? specialType = subPkg.Resources.Any(r => r.Key.Type == 107542056) ? PackageTypeHelper.World
                        : subPkg.Resources.Any(r => r.Key.Type == 3496170587u) ? PackageTypeHelper.Lot
                        : subPkg.Resources.Any(r => r.Key.Type == 83396964) ? PackageTypeHelper.Sim
                        : null;
                    if (specialType != null)
                    {
                        using var ms = new MemoryStream();
                        subPkg.Export(ms);
                        librarySubItems.Add((ms.ToArray(), specialType));
                        continue;
                    }
                    rawChunks.AddRange(ReadPackagePayloads(subPkg, meta.Id, meta.Enabled));
                }
                typeInfo = new PackageTypeInfo("Sims3Pack");
            }
            else
            {
                using var sourcePkg = new DBPFPackage(replacementFilePath);
                typeInfo = _packageTypeService.DetectPackageType(replacementFilePath, false);
                if (PackageTypeHelper.IsLibraryOnlyType(typeInfo.PackageType))
                {
                    log.Warn($"Not replacing vault mod {meta.FileName} with {replacementFilePath}: lots, Sims and worlds are never stored in the vault");
                    return false;
                }
                rawChunks = ReadPackagePayloads(sourcePkg, meta.Id, meta.Enabled);
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to read replacement file {replacementFilePath}: {ex.Message}");
            return false;
        }

        if (rawChunks.Count == 0 && librarySubItems.Count == 0)
            return false;

        // Taken before any new chunk record is added: EF would put those into meta.VaultChunks too.
        var oldRegions = new Dictionary<long, List<(long, int)>>();
        var allOldChunks = meta.VaultChunks.Concat(oldChildren.SelectMany(c => c.VaultChunks)).ToList();
        foreach (var c in allOldChunks) AddRegion(oldRegions, c);

        // 2. Append the new chunks. The index keeps everything except the chunks being replaced.
        var replacedMetaIds = oldChildren.Select(c => c.Id).Append(meta.Id).ToHashSet();
        VaultShardEntity? targetShard = null;
        string? targetFullPath = null;
        if (rawChunks.Count > 0)
        {
            (targetShard, targetFullPath) = await GetOrCreateTargetShardAsync(targetSet, EstimateBytes(rawChunks));
            var targetIndexed = (await GetIndexedChunksAsync(targetShard.Id))
                .Where(e => !replacedMetaIds.Contains(e.MetaEntityId))
                .ToList();

            var appendedEntries = DBPFVaultShard.AppendChunksAndCommit(targetFullPath, rawChunks, targetIndexed);
            _db.VaultChunks.AddRange(ToChunkEntities(appendedEntries, meta.Id, targetShard.Id));
            targetShard.FileSize = new FileInfo(targetFullPath).Length;
            targetShard.ResourceCount = targetIndexed.Count + (meta.Enabled ? appendedEntries.Count : 0);
        }

        // 3. Commit the new mapping
        _db.VaultChunks.RemoveRange(allOldChunks);
        var oldLibraryFiles = oldChildren.Where(c => !c.IsVaultManaged && !string.IsNullOrEmpty(c.CompleteFileName)).Select(c => c.CompleteFileName).ToList();
        if (oldChildren.Count > 0)
        {
            _db.MetaEntities.RemoveRange(oldChildren);
        }

        var newLibraryChildren = new List<MetaEntity>();
        string partBase = Path.GetFileNameWithoutExtension(replacementFilePath);
        for (int i = 0; i < librarySubItems.Count; i++)
        {
            var (data, type) = librarySubItems[i];
            string ext = type == PackageTypeHelper.World ? ".world" : type == PackageTypeHelper.Sim ? ".sim" : ".package";
            newLibraryChildren.Add(ExportLibrarySubItem(fs => fs.Write(data), $"{partBase}_Part{i + 1}{ext}", type, meta, targetSet, meta.Enabled));
        }

        meta.VaultShardId = targetShard?.Id;
        meta.CompleteFileName = targetFullPath ?? newLibraryChildren[0].CompleteFileName;

        // Preserve folder, tags, ID, favorite
        var fileInfo = new FileInfo(replacementFilePath);
        meta.FileName = Path.GetFileName(replacementFilePath);
        meta.FileSize = fileInfo.Length / 1024.0;
        meta.Filehash = $"{fileInfo.Length}_{fileInfo.LastWriteTimeUtc.Ticks}";
        if (isSims3PackReplacement)
        {
            meta.PackageType = "Sims3Pack";
            meta.FileType = "TS3PACK";
        }
        else
        {
            meta.FileType = "DBPF";
            if (!meta.IsUserTagged)
            {
                meta.PackageType = typeInfo.PackageType;
                meta.CASCategories = typeInfo.CASCategories;
                meta.CASAge = typeInfo.CASAge;
                meta.CASGender = typeInfo.CASGender;
                meta.CASOutfitCategory = typeInfo.CASOutfitCategory;
            }
        }
        typeInfo.ApplyBuildBuyTo(meta);

        await _db.SaveChangesAsync();

        // 4. Drop the old chunks from their own shards' indexes, then free them. The target shard is rewritten
        // too, so objects the user turned off stay off in the new version.
        await SyncAndReleaseAsync(oldRegions, targetShard != null ? new[] { targetShard.Id } : null);
        await OptimizeNewModsAsync(new[] { meta.Id });

        foreach (var path in oldLibraryFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { log.Warn($"Could not delete replaced library part {path}: {ex.Message}"); }
        }
        if (oldLibraryFiles.Count > 0 || newLibraryChildren.Count > 0) await _cacheBuilderService.SyncToSims3Async();

        if (meta.SetsEntityId.HasValue)
        {
            try { await _hiderService.RefreshHidersForSetsAsync(new[] { meta.SetsEntityId.Value }); }
            catch (Exception ex) { log.Warn($"Could not refresh hider packages after replacing mod {meta.Id}: {ex.Message}"); }
        }

        // 5. Invalidate and re-extract thumbnail
        try
        {
            _thumbnailService.DeleteThumbnail(meta.Id);
            if (targetFullPath != null) await _thumbnailService.GetThumbnailPathAsync(meta.Id, targetFullPath);
        }
        catch (Exception ex)
        {
            log.Warn($"Could not regenerate thumbnail after replacing mod {meta.Id}: {ex.Message}");
        }

        return true;
    }

    public async Task<bool> ApplyResourceEditsAsync(long metaEntityId, IReadOnlyList<ResourceEdit> edits)
    {
        if (edits.Count == 0) return true;
        Sims3GameProcess.ThrowIfGameRunning("Close The Sims 3 before changing the contents of a vault item.");

        var meta = await _db.MetaEntities
            .Include(m => m.VaultChunks)
            .FirstOrDefaultAsync(m => m.Id == metaEntityId);
        if (meta == null || !meta.IsVaultManaged || meta.VaultShardId == null)
            return false;

        var shard = await LoadShardAsync(meta.VaultShardId.Value);
        if (shard == null) return false;
        string shardPath = GetShardFullPath(shard);
        if (!File.Exists(shardPath)) return false;

        var editedKeys = edits.Select(e => e.Key).ToHashSet();
        var replacedChunks = meta.VaultChunks.Where(c => editedKeys.Contains(new TGI_Key(c.Type, c.Group, c.Instance))).ToList();
        var replacedIds = replacedChunks.Select(c => c.Id).ToHashSet();

        // 1. Append the new contents (thumbnails are small PNGs, stored uncompressed like the game's own).
        var payloads = edits
            .Where(e => e.NewData != null)
            .GroupBy(e => e.Key).Select(g => g.Last())
            .Select(e => new RawChunkPayload(e.Key, e.NewData!, e.NewData!.Length, false, meta.Id) { Indexed = meta.Enabled })
            .ToList();
        var appended = new List<VaultChunkEntry>();
        if (payloads.Count > 0)
        {
            var indexed = (await GetIndexedChunksAsync(shard.Id)).Where(e => !replacedIds.Contains(e.ChunkId)).ToList();
            appended = DBPFVaultShard.AppendChunksAndCommit(shardPath, payloads, indexed);
        }

        // 2. Commit the new mapping
        var oldRegions = new Dictionary<long, List<(long, int)>>();
        foreach (var c in replacedChunks) AddRegion(oldRegions, c);
        long sizeDelta = appended.Sum(e => (long)e.ChunkLength) - replacedChunks.Sum(c => (long)c.ChunkLength);
        meta.FileSize = Math.Max(0, meta.FileSize + sizeDelta / 1024.0);
        _db.VaultChunks.RemoveRange(replacedChunks);
        _db.VaultChunks.AddRange(ToChunkEntities(appended, meta.Id, shard.Id));
        await _db.SaveChangesAsync();

        // 3. Rewrite the indexes from the database, then free the old chunks
        await SyncAndReleaseAsync(oldRegions, new[] { shard.Id });

        // 4. Refresh the card image. A Sims3Pack's card may be its pack preview, which the vault does not keep, so it is left alone.
        if (meta.ParentMetaEntityId == null)
        {
            try
            {
                _thumbnailService.DeleteThumbnail(meta.Id);
                await _thumbnailService.GetThumbnailPathAsync(meta.Id, shardPath);
            }
            catch (Exception ex)
            {
                log.Warn($"Could not regenerate thumbnail after editing mod {meta.Id}: {ex.Message}");
            }
        }
        return true;
    }

    #endregion

    #region Texture optimisation

    public async Task<int> OptimizeTexturesAsync(IReadOnlyCollection<long>? metaIds = null, Action<int, int>? onProgress = null)
    {
        int profile = ActiveTextureProfile;
        if (profile == 0) return 0;
        int maxDimension = Options.TextureMaxDimension;

        var query = _db.VaultChunks.Where(c => c.Type == TextureOptimizer.ImageResourceType && c.OptimizedProfile != profile && !c.MetaEntity!.SkipTextureOptimization);
        if (metaIds != null)
        {
            var ids = metaIds.ToList();
            query = query.Where(c => ids.Contains(c.MetaEntityId));
        }
        var pending = await query.OrderBy(c => c.VaultShardId).ThenBy(c => c.Offset).ToListAsync();
        if (pending.Count == 0) return 0;

        int done = 0, optimizedCount = 0;
        onProgress?.Invoke(0, pending.Count);

        foreach (var shardGroup in pending.GroupBy(c => c.VaultShardId))
        {
            var shard = await LoadShardAsync(shardGroup.Key);
            string? path = shard != null ? GetShardFullPath(shard) : null;
            if (shard == null || path == null || !File.Exists(path))
            {
                done += shardGroup.Count();
                continue;
            }

            var staleCopies = new List<(long Offset, int Length)>();
            foreach (var batch in shardGroup.Chunk(TextureBatchSize))
            {
                var raws = DBPFVaultShard.ReadRawChunks(path, batch.Select(c => (c.Offset, c.ChunkLength)).ToList());
                var results = new OptimizedTexture?[batch.Length];
                Parallel.For(0, batch.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i =>
                {
                    try
                    {
                        var c = batch[i];
                        byte[] plain = raws[i];
                        if (c.IsCompressed)
                        {
                            plain = GC.AllocateUninitializedArray<byte>(c.ResourceLength);
                            if (!Compression.Decompress(raws[i], plain)) return;
                        }
                        if (TextureOptimizer.TryOptimize(plain, maxDimension) is { } dds) results[i] = TextureOptimizer.Pack(dds);
                    }
                    catch
                    {
                        // Keep the original.
                    }
                });

                var payloads = new List<RawChunkPayload>();
                for (int i = 0; i < batch.Length; i++)
                {
                    if (results[i] is { } opt)
                    {
                        var c = batch[i];
                        payloads.Add(new RawChunkPayload(new TGI_Key(c.Type, c.Group, c.Instance), opt.Chunk, opt.ResourceLength, opt.IsCompressed, c.MetaEntityId) { Indexed = false });
                    }
                }

                // Appending only adds data after the committed index; the index itself is switched below.
                var appended = payloads.Count > 0
                    ? DBPFVaultShard.AppendChunksAndCommit(path, payloads, await GetIndexedChunksAsync(shard.Id))
                    : new List<VaultChunkEntry>();

                int next = 0;
                for (int i = 0; i < batch.Length; i++)
                {
                    var c = batch[i];
                    if (c.OptimizedLength > 0) staleCopies.Add((c.OptimizedOffset, c.OptimizedLength));

                    if (results[i] is { } opt)
                    {
                        var entry = appended[next++];
                        c.OptimizedOffset = entry.Offset;
                        c.OptimizedLength = entry.ChunkLength;
                        c.OptimizedResourceLength = opt.IsCompressed ? opt.ResourceLength : 0;
                        optimizedCount++;
                    }
                    else
                    {
                        c.OptimizedOffset = 0;
                        c.OptimizedLength = -1;
                        c.OptimizedResourceLength = 0;
                    }
                    c.OptimizedProfile = profile;
                }
                await _db.SaveChangesAsync();

                done += batch.Length;
                onProgress?.Invoke(done, pending.Count);
            }

            bool indexWritten = await SyncShardIndexAsync(shard);
            ReleaseChunkRegions(shard, staleCopies, deferPunch: !indexWritten);
            await _db.SaveChangesAsync();
        }

        if (optimizedCount > 0) _cacheBuilderService.OnGameContentChanged();
        return optimizedCount;
    }

    private async Task OptimizeNewModsAsync(IReadOnlyCollection<long> metaIds)
    {
        if (ActiveTextureProfile == 0 || metaIds.Count == 0) return;
        try
        {
            await OptimizeTexturesAsync(metaIds);
        }
        catch (Exception ex)
        {
            log.Warn($"Texture optimisation of new vault mods failed: {ex.Message}");
            _db.ChangeTracker.Clear();
        }
    }

    public async Task RefreshTextureIndexesAsync()
    {
        var shards = await _db.VaultShards.Include(s => s.SetsEntity).ToListAsync();
        foreach (var shard in shards)
        {
            try
            {
                await SyncShardIndexAsync(shard);
            }
            catch (Exception ex)
            {
                log.Warn($"Could not refresh the index of vault shard {shard.RelativePath}: {ex.Message}");
            }
        }
        await _db.SaveChangesAsync();
        _cacheBuilderService.OnGameContentChanged();
    }

    public async Task RefreshItemIndexesAsync(long metaEntityId)
    {
        var shardIds = await _db.VaultChunks
            .Where(c => c.MetaEntityId == metaEntityId || c.MetaEntity!.ParentMetaEntityId == metaEntityId)
            .Select(c => c.VaultShardId)
            .Distinct()
            .ToListAsync();
        if (shardIds.Count > 0)
        {
            await SyncAndReleaseAsync(new Dictionary<long, List<(long, int)>>(), shardIds);
        }
    }

    public async Task SetSkipTextureOptimizationAsync(IEnumerable<long> metaEntityIds, bool skip)
    {
        var idList = metaEntityIds.Distinct().ToList();
        if (idList.Count == 0) return;

        var metas = await _db.MetaEntities
            .Where(m => idList.Contains(m.Id) || (m.ParentMetaEntityId.HasValue && idList.Contains(m.ParentMetaEntityId.Value)))
            .ToListAsync();

        var shardIds = new HashSet<long>();
        foreach (var meta in metas)
        {
            if (meta.SkipTextureOptimization == skip) continue;
            meta.SkipTextureOptimization = skip;
            if (meta.IsVaultManaged && meta.VaultShardId.HasValue) shardIds.Add(meta.VaultShardId.Value);
        }
        await _db.SaveChangesAsync();

        if (shardIds.Count > 0 && ActiveTextureProfile != 0)
        {
            await SyncAndReleaseAsync(new Dictionary<long, List<(long, int)>>(), shardIds);
        }
    }

    #endregion

    #region Compaction

    private async Task<Dictionary<long, long>> GetLiveBytesByShardAsync()
    {
        return await _db.VaultChunks
            .GroupBy(c => c.VaultShardId)
            .Select(g => new { ShardId = g.Key, Live = g.Sum(c => (long)c.ChunkLength + (c.OptimizedLength > 0 ? c.OptimizedLength : 0)) })
            .ToDictionaryAsync(x => x.ShardId, x => x.Live);
    }

    private async Task CompactIfFragmentedAsync(long shardId)
    {
        try
        {
            if (Sims3GameProcess.IsGameRunning()) return;

            var shard = await LoadShardAsync(shardId);
            if (shard == null) return;
            string path = GetShardFullPath(shard);
            if (!File.Exists(path)) return;

            long length = new FileInfo(path).Length;
            long live = await _db.VaultChunks
                .Where(c => c.VaultShardId == shardId)
                .SumAsync(c => (long)c.ChunkLength + (c.OptimizedLength > 0 ? c.OptimizedLength : 0));
            long dead = length - 96 - live;

            if (dead >= AutoCompactionDeadBytes || (dead >= AutoCompactionMinDeadBytes && (double)dead / length >= AutoCompactionDeadRatio))
            {
                log.Info($"Auto-compacting fragmented vault shard {path} ({dead / (1024 * 1024)} MB unused)");
                await CompactShardAsync(shardId);
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Automatic compaction of vault shard {shardId} failed: {ex.Message}");
            _db.ChangeTracker.Clear();
        }
    }

    public async Task<(int Compacted, long BytesReclaimed)> CompactVaultAsync(Action<string, int, int>? onProgress = null)
    {
        Sims3GameProcess.ThrowIfGameRunning(_localizer.GetString("progress.game_running_vault"));
        await PunchPendingHolesAsync();

        var shards = await _db.VaultShards.Include(s => s.SetsEntity).ToListAsync();
        var liveBytes = await GetLiveBytesByShardAsync();

        var candidates = new List<VaultShardEntity>();
        foreach (var shard in shards)
        {
            string path = GetShardFullPath(shard);
            if (!File.Exists(path)) continue;

            long length = new FileInfo(path).Length;
            long live = liveBytes.TryGetValue(shard.Id, out var l) ? l : 0;
            long dead = length - 96 - live;
            if (dead >= CompactionMinDeadBytes || (length > 0 && dead >= 1024 * 1024 && (double)dead / length >= CompactionMinDeadRatio))
            {
                candidates.Add(shard);
            }
        }

        int done = 0;
        long reclaimed = 0;
        foreach (var shard in candidates)
        {
            onProgress?.Invoke(Path.GetFileName(shard.RelativePath), done, candidates.Count);
            long before = new FileInfo(GetShardFullPath(shard)).Length;
            if (await CompactShardAsync(shard.Id))
            {
                reclaimed += Math.Max(0, before - shard.FileSize);
            }
            done++;
        }
        onProgress?.Invoke(string.Empty, done, candidates.Count);
        return (done, reclaimed);
    }

    public async Task<bool> CompactShardAsync(long shardId, Action<string>? onProgress = null)
    {
        var shard = await LoadShardAsync(shardId);
        if (shard == null) return false;

        string shardFullPath = GetShardFullPath(shard);
        if (!File.Exists(shardFullPath)) return false;

        // The file is replaced; the running game has it open.
        Sims3GameProcess.ThrowIfGameRunning(_localizer.GetString("progress.game_running_vault"));

        onProgress?.Invoke($"Compacting vault shard: {Path.GetFileName(shardFullPath)}");

        // Every chunk is kept; disabled mods just stay out of the index.
        var chunks = await _db.VaultChunks
            .Include(c => c.MetaEntity)
            .Where(c => c.VaultShardId == shard.Id)
            .ToListAsync();

        var enabledMetaIds = chunks.Where(c => c.MetaEntity?.Enabled == true).Select(c => c.MetaEntityId).ToHashSet();
        var disabledObjects = await LoadDisabledObjectKeysAsync();
        var turnedOffChunkIds = chunks
            .Where(c => disabledObjects.Contains((c.MetaEntity?.ParentMetaEntityId ?? c.MetaEntityId, c.Type, c.Group, c.Instance)))
            .Select(c => c.Id)
            .ToHashSet();
        int activeProfile = ActiveTextureProfile;
        int keepProfile = ConfiguredTextureProfile;

        // Original chunks carry their row Id; optimised copies carry the negated Id. Copies made with other
        // settings are dropped (their chunks get re-checked by the next optimisation pass).
        var entries = new List<VaultChunkEntry>(chunks.Count);
        var indexOptimized = new HashSet<long>();
        foreach (var c in chunks)
        {
            var key = new TGI_Key(c.Type, c.Group, c.Instance);
            entries.Add(new VaultChunkEntry(key, c.Offset, c.ChunkLength, c.ResourceLength, c.IsCompressed, c.MetaEntityId, c.Id));
            if (c.OptimizedLength > 0 && c.OptimizedProfile == keepProfile)
            {
                var (copyLength, copyCompressed) = CopyFormat(c);
                entries.Add(new VaultChunkEntry(key, c.OptimizedOffset, c.OptimizedLength, copyLength, copyCompressed, c.MetaEntityId, -c.Id));
                if (UsesOptimizedCopy(c, activeProfile, c.MetaEntity?.SkipTextureOptimization == true)) indexOptimized.Add(c.Id);
            }
        }

        bool IsIndexed(VaultChunkEntry e) =>
            enabledMetaIds.Contains(e.MetaEntityId) && !turnedOffChunkIds.Contains(Math.Abs(e.ChunkId))
            && (e.ChunkId > 0 ? !indexOptimized.Contains(e.ChunkId) : indexOptimized.Contains(-e.ChunkId));

        // Phase marker: HeaderIndexOffset is only set to the new file's index offset once the new
        // offsets are committed, which tells startup recovery which copy the database matches.
        shard.HeaderIndexOffset = 0;
        await _db.SaveChangesAsync();

        var (compacted, newIndexOffset) = DBPFVaultShard.CompactShard(shardFullPath, entries, IsIndexed);
        var byChunkId = compacted.ToDictionary(e => e.ChunkId);

        try
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            foreach (var chunk in chunks)
            {
                chunk.Offset = byChunkId[chunk.Id].Offset;
                if (byChunkId.TryGetValue(-chunk.Id, out var copy))
                {
                    chunk.OptimizedOffset = copy.Offset;
                }
                else if (chunk.OptimizedLength > 0)
                {
                    ClearOptimizedCopy(chunk);
                }
            }

            var holes = await _db.VaultHoles.Where(h => h.VaultShardId == shard.Id).ToListAsync();
            _db.VaultHoles.RemoveRange(holes);

            shard.DeadHoleBytes = 0;
            shard.FileSize = new FileInfo(shardFullPath).Length;
            shard.ResourceCount = compacted.Count(IsIndexed);
            shard.HeaderIndexOffset = newIndexOffset;

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            log.Error($"Compaction of {shardFullPath} could not be committed; restoring the original shard: {ex.Message}");
            DBPFVaultShard.RollbackCompaction(shardFullPath);
            _db.ChangeTracker.Clear();
            throw;
        }

        DBPFVaultShard.FinishCompaction(shardFullPath);
        onProgress?.Invoke($"Compacted {Path.GetFileName(shardFullPath)} successfully.");
        return true;
    }

    #endregion

    #region Recovery

    public async Task<int> MigrateLegacyShardsAsync(long? onlySetId = null)
    {
        int moved = 0;
        // Moving files the game has open would break its next read; retry on a later start.
        if (Sims3GameProcess.IsGameRunning()) return 0;

        var shards = await _db.VaultShards
            .Include(s => s.SetsEntity)
            .Where(s => !onlySetId.HasValue || s.SetsEntityId == onlySetId.Value)
            .ToListAsync();

        string vaultRoot = GetVaultRootDirectory();
        var legacyDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var shard in shards.Where(s => IsLegacyRelativePath(s.RelativePath)))
        {
            string legacyPath = ResolveShardPath(vaultRoot, shard, shard.SetsEntity);
            string folderName = GetSetFolderName(shard.SetsEntityId);
            string targetDir = Path.Combine(vaultRoot, folderName);
            string targetPath = Path.Combine(targetDir, shard.RelativePath);
            legacyDirs.Add(Path.GetDirectoryName(legacyPath)!);

            try
            {
                if (File.Exists(legacyPath))
                {
                    Directory.CreateDirectory(targetDir);
                    if (File.Exists(targetPath))
                    {
                        targetPath = Path.Combine(targetDir, $"{Path.GetFileNameWithoutExtension(shard.RelativePath)}_{shard.Id}.package");
                    }

                    var sync = DBPFVaultShard.GetShardLock(legacyPath);
                    await sync.WaitAsync();
                    try { File.Move(legacyPath, targetPath); }
                    finally { sync.Release(); }
                }
                else if (!File.Exists(targetPath))
                {
                    log.Error($"Vault shard {shard.RelativePath} of set {shard.SetsEntityId} not found. If two sets shared a vault folder name, this shard's file was shared with another set and may be mixed.");
                    continue;
                }

                shard.RelativePath = $"{folderName}/{Path.GetFileName(targetPath)}";
                var metas = await _db.MetaEntities.Where(m => m.VaultShardId == shard.Id).ToListAsync();
                foreach (var m in metas) m.CompleteFileName = targetPath;
                await _db.SaveChangesAsync();
                moved++;
            }
            catch (Exception ex)
            {
                log.Warn($"Could not move legacy vault shard {legacyPath}: {ex.Message}");
            }
        }

        foreach (var dir in legacyDirs)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch { }
        }

        return moved;
    }

    private static bool SameIndex(List<VaultChunkEntry> a, List<VaultChunkEntry> b)
    {
        if (a.Count != b.Count) return false;
        static (TGI_Key, long, int) K(VaultChunkEntry e) => (e.Key, e.Offset, e.ChunkLength);
        var sa = a.Select(K).OrderBy(x => x.Item2).ThenBy(x => x.Item1).ToList();
        var sb = b.Select(K).OrderBy(x => x.Item2).ThenBy(x => x.Item1).ToList();
        return sa.SequenceEqual(sb);
    }

    public async Task<bool> CheckAndRecoverAllShardsAsync()
    {
        bool layoutChanged = await MigrateLegacyShardsAsync() > 0;

        var shards = await _db.VaultShards.Include(s => s.SetsEntity).ToListAsync();
        foreach (var shard in shards)
        {
            string fullPath = GetShardFullPath(shard);
            try
            {
                string tempPath = fullPath + DBPFVaultShard.CompactionTempSuffix;
                if (File.Exists(tempPath)) File.Delete(tempPath);

                if (File.Exists(fullPath + DBPFVaultShard.CompactionBackupSuffix))
                {
                    bool committed = File.Exists(fullPath)
                        && shard.HeaderIndexOffset != 0
                        && DBPFVaultShard.GetCommittedIndexOffset(fullPath) == shard.HeaderIndexOffset;

                    if (committed)
                    {
                        DBPFVaultShard.FinishCompaction(fullPath);
                    }
                    else
                    {
                        log.Warn($"Rolling back interrupted compaction of {fullPath}");
                        DBPFVaultShard.RollbackCompaction(fullPath);
                    }
                }

                if (!File.Exists(fullPath))
                {
                    log.Error($"Vault shard file is missing: {fullPath}");
                    continue;
                }

                DBPFVaultShard.CheckAndRecoverOrphanBytes(fullPath);

                long length = new FileInfo(fullPath).Length;

                // An optimised copy is disposable: forget any that points outside the file, and re-check that texture later.
                var brokenCopies = await _db.VaultChunks
                    .Where(c => c.VaultShardId == shard.Id && c.OptimizedLength > 0 && c.OptimizedOffset + c.OptimizedLength > length)
                    .ToListAsync();
                foreach (var c in brokenCopies)
                {
                    ClearOptimizedCopy(c);
                }
                if (brokenCopies.Count > 0) await _db.SaveChangesAsync();

                bool dbFits = !await _db.VaultChunks.AnyAsync(c => c.VaultShardId == shard.Id && c.Offset + c.ChunkLength > length);
                if (!dbFits)
                {
                    log.Error($"Database chunks of shard {fullPath} point past the end of the file; leaving the shard untouched");
                    continue;
                }

                var dbIndexed = await GetIndexedChunksAsync(shard.Id);
                var fileIndex = DBPFVaultShard.ReadIndex(fullPath);
                if (fileIndex == null || !SameIndex(fileIndex, dbIndexed))
                {
                    log.Warn($"Index of vault shard {fullPath} did not match the database; rewriting it");
                    try
                    {
                        DBPFVaultShard.RewriteIndexOnly(fullPath, dbIndexed);
                    }
                    catch (IOException) when (Sims3GameProcess.IsGameRunning())
                    {
                        // Keeps pending holes of this shard from being zeroed until the index is right.
                        _pendingIndexSync[shard.Id] = 0;
                        throw;
                    }
                }

                shard.ResourceCount = dbIndexed.Count;
                shard.FileSize = new FileInfo(fullPath).Length;
            }
            catch (Exception ex)
            {
                log.Warn($"Vault shard check failed for {fullPath}: {ex.Message}");
            }
        }

        await _db.SaveChangesAsync();

        // Regions freed while the game was running during the last session.
        await PunchPendingHolesAsync();
        return layoutChanged;
    }

    public async Task<int> ExtractLibraryOnlyItemsAsync()
    {
        var candidates = (await _db.MetaEntities
                .Include(m => m.VaultChunks)
                .Where(m => m.IsVaultManaged)
                .ToListAsync())
            .Where(m => PackageTypeHelper.IsLibraryOnlyType(m.PackageType) && m.VaultChunks.Count > 0)
            .ToList();
        if (candidates.Count == 0) return 0;

        string libraryDir = Options.ManagedPackageFolderPath;
        Directory.CreateDirectory(libraryDir);
        int moved = 0;

        foreach (var meta in candidates)
        {
            // World sync looks for .world files; lots and Sims keep their name.
            string fileName = string.Equals(meta.PackageType, PackageTypeHelper.World, StringComparison.OrdinalIgnoreCase)
                ? Path.ChangeExtension(meta.FileName, ".world")
                : meta.FileName;
            var (destPath, _) = ArchiveService.ResolveDestinationPath(libraryDir, fileName, "rename");
            if (destPath == null) continue;

            try
            {
                if (!await ExportOwnChunksAsync(meta, destPath)) continue;
            }
            catch (Exception ex)
            {
                log.Warn($"Could not move {meta.FileName} out of the vault: {ex.Message}");
                continue;
            }

            var regions = new Dictionary<long, List<(long, int)>>();
            foreach (var c in meta.VaultChunks) AddRegion(regions, c);
            _db.VaultChunks.RemoveRange(meta.VaultChunks.ToList());

            var info = new FileInfo(destPath);
            meta.IsVaultManaged = false;
            meta.VaultShardId = null;
            meta.FileName = Path.GetFileName(destPath);
            meta.CompleteFileName = destPath;
            meta.FileSize = info.Length / 1024.0;
            meta.Filehash = $"{info.Length}_{info.LastWriteTimeUtc.Ticks}";
            if (destPath.EndsWith(".world", StringComparison.OrdinalIgnoreCase)) meta.FileType = "WORLD";
            await _db.SaveChangesAsync();

            await SyncAndReleaseAsync(regions);
            moved++;
        }

        if (moved > 0) log.Info($"Moved {moved} lots, Sims or worlds out of the vault into the library");
        return moved;
    }

    #endregion
}
