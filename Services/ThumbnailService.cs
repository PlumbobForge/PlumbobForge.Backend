using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlumbobForge.Backend.Configuration;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;
using S3ForgeTools.GameFiles.TS3Pack;
using SkiaSharp;

namespace PlumbobForge.Backend.Services;

public class ThumbnailService
{
    private readonly AppDbContext _db;
    private readonly PlumbobForgeOptions _options;

    public ThumbnailService(AppDbContext db, IOptionsSnapshot<PlumbobForgeOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public string GetThumbnailDirectory()
    {
        string thumbDir = Path.Combine(_options.DocumentBaseDir, "Thumbnails");
        Directory.CreateDirectory(thumbDir);
        return thumbDir;
    }

    public static event Action<long>? ThumbnailDeleted;

    public void DeleteThumbnail(long itemId)
    {
        try
        {
            string thumbDir = Path.Combine(_options.DocumentBaseDir, "Thumbnails");
            string thumbPath = Path.Combine(thumbDir, $"{itemId}.thumb");
            string noThumbPath = Path.Combine(thumbDir, $"{itemId}.nothumb");

            if (File.Exists(thumbPath)) File.Delete(thumbPath);
            if (File.Exists(noThumbPath)) File.Delete(noThumbPath);
            TryDelete(Path.Combine(thumbDir, $"{itemId}.fallback"));

            ThumbnailDeleted?.Invoke(itemId);
        }
        catch { }
    }

    public async Task<int> CleanupOrphanedThumbnailsAsync()
    {
        return await Task.Run(async () =>
        {
            try
            {
                string thumbDir = GetThumbnailDirectory();
                if (!Directory.Exists(thumbDir)) return 0;

                var existingItemIds = (await _db.MetaEntities.AsNoTracking().Select(m => m.Id).ToListAsync())
                    .ToHashSet();

                var files = Directory.GetFiles(thumbDir, "*.*");
                int removedCount = 0;

                foreach (var file in files)
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext != ".thumb" && ext != ".nothumb") continue;

                    var name = Path.GetFileNameWithoutExtension(file);
                    if (long.TryParse(name, out var id))
                    {
                        if (!existingItemIds.Contains(id))
                        {
                            try
                            {
                                File.Delete(file);
                                removedCount++;
                            }
                            catch { }
                        }
                    }
                }

                return removedCount;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning("Failed to clean up orphaned thumbnails", ex, "ThumbnailService");
                return 0;
            }
        });
    }

    private static readonly uint[] ValidThumbTypes = new uint[] {
        0x626F60CC, 0x626F60CD, 0x626F60CE, // CAS part thumbnails, small / medium / large (highest priority)
        0x0B202AD9, // Listed as THUM before; not in the published type tables
        0x0580A2B4, 0x0580A2B5, 0x0580A2B6, // Object thumbnails
        Sims3ResourceTypes.ThumbFence + 1, Sims3ResourceTypes.ThumbFence + 2, Sims3ResourceTypes.ThumbFence,
        Sims3ResourceTypes.ThumbStairs + 1, Sims3ResourceTypes.ThumbStairs + 2, Sims3ResourceTypes.ThumbStairs,
        Sims3ResourceTypes.ThumbRailing + 1, Sims3ResourceTypes.ThumbRailing + 2, Sims3ResourceTypes.ThumbRailing,
        Sims3ResourceTypes.ThumbFireplace + 1, Sims3ResourceTypes.ThumbFireplace + 2, Sims3ResourceTypes.ThumbFireplace,
        Sims3ResourceTypes.ThumbTerrainPaint + 1, Sims3ResourceTypes.ThumbTerrainPaint + 2, Sims3ResourceTypes.ThumbTerrainPaint,
        Sims3ResourceTypes.ThumbWallFloorPattern + 1, Sims3ResourceTypes.ThumbWallFloorPattern + 2, Sims3ResourceTypes.ThumbWallFloorPattern,
        // ICON thumbnails (catalog PNG icons) come last: the game's CAS thumbnail cache is tried before them
        Sims3ResourceTypes.ThumbIcon + 1, Sims3ResourceTypes.ThumbIcon, Sims3ResourceTypes.ThumbIcon + 2
    };

    private static readonly int FirstIconRank = ValidThumbTypes.Length - 3;

    private static int ThumbRank(uint type) => Array.IndexOf(ValidThumbTypes, type);

    private static bool IsIconThumb(uint type) => ThumbRank(type) >= FirstIconRank;

    // Bumped when the thumbnail priority changes so previews extracted by older versions are redone once.
    private const string PriorityVersionFile = "priority.v2";
    // Holds the CASThumbnails.package timestamp that fallback previews were last checked against.
    private const string GameCacheStampFile = "casthumbs.stamp";

    private string? _sims3Dir;

    private string GetSims3Dir() => _sims3Dir ??= Sims3PathResolver.ResolveSims3UserFolder(_options.Sims3UserDir);

    private static ResourceEntry? FindBestThumbnail(IEnumerable<DBPFPackage> packages)
    {
        ResourceEntry? best = null;
        int bestPriority = int.MaxValue;

        foreach (var package in packages)
        {
            foreach (var r in package.Resources)
            {
                int i = ThumbRank(r.Key.Type);
                if (i < 0) continue;
                if (i == 0) // Highest priority found (custom thumbnail) - return immediately!
                {
                    return r;
                }
                if (i < bestPriority)
                {
                    best = r;
                    bestPriority = i;
                }
            }
        }

        return best;
    }

    private static List<ulong> CasPartInstances(IEnumerable<DBPFPackage> packages) =>
        packages.SelectMany(p => p.Resources).Where(r => r.Key.Type == Sims3ResourceTypes.Casp).Select(r => r.Key.Instance).ToList();

    private bool TrySavePreview(string thumbPath, Func<byte[]>? readEmbedded, bool embeddedIsIcon, IReadOnlyCollection<ulong> casParts)
    {
        string fallbackPath = Path.ChangeExtension(thumbPath, ".fallback");
        if (readEmbedded != null && !embeddedIsIcon)
        {
            SaveOptimizedThumbnail(readEmbedded(), thumbPath);
            TryDelete(fallbackPath);
            return true;
        }

        if (casParts.Count > 0)
        {
            var cached = GameThumbnailCache.TryReadCasThumbnail(GetSims3Dir(), casParts);
            if (cached != null)
            {
                SaveOptimizedThumbnail(cached, thumbPath);
                TryDelete(fallbackPath);
                return true;
            }
            try { File.WriteAllBytes(fallbackPath, Array.Empty<byte>()); } catch { }
        }

        if (readEmbedded == null) return false;
        SaveOptimizedThumbnail(readEmbedded(), thumbPath);
        return true;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void RefreshStalePreviews(IEnumerable<MetaEntity> items)
    {
        try
        {
            string thumbDir = GetThumbnailDirectory();

            string versionPath = Path.Combine(thumbDir, PriorityVersionFile);
            if (!File.Exists(versionPath))
            {
                // Sims3Pack previews come from the Sims3Pack itself and did not change.
                foreach (var item in items)
                {
                    if (item.FileType == "TS3PACK") continue;
                    if (File.Exists(Path.Combine(thumbDir, $"{item.Id}.thumb")) || File.Exists(Path.Combine(thumbDir, $"{item.Id}.nothumb")))
                    {
                        DeleteThumbnail(item.Id);
                    }
                }
                File.WriteAllBytes(versionPath, Array.Empty<byte>());
            }

            var cacheInfo = new FileInfo(GameThumbnailCache.GetCasThumbnailsPath(GetSims3Dir()));
            if (!cacheInfo.Exists) return;

            string stampPath = Path.Combine(thumbDir, GameCacheStampFile);
            string stamp = $"{cacheInfo.LastWriteTimeUtc.Ticks}:{cacheInfo.Length}";
            string? previous = null;
            try { previous = File.Exists(stampPath) ? File.ReadAllText(stampPath) : null; } catch { }
            if (previous == stamp) return;

            foreach (var marker in Directory.GetFiles(thumbDir, "*.fallback"))
            {
                if (long.TryParse(Path.GetFileNameWithoutExtension(marker), out var id)) DeleteThumbnail(id);
                else TryDelete(marker);
            }
            File.WriteAllText(stampPath, stamp);
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning("Failed to refresh stale thumbnails", ex, "ThumbnailService");
        }
    }

    public async Task<string?> GetThumbnailPathAsync(long itemId, string? completeFileName = null)
    {
        string thumbDir = GetThumbnailDirectory();
        string thumbPath = Path.Combine(thumbDir, $"{itemId}.thumb");
        string noThumbPath = Path.Combine(thumbDir, $"{itemId}.nothumb");

        if (File.Exists(thumbPath))
        {
            return thumbPath;
        }

        if (File.Exists(noThumbPath))
        {
            return null;
        }

        var item = await _db.MetaEntities.AsNoTracking().Include(m => m.VaultChunks).FirstOrDefaultAsync(m => m.Id == itemId);
        if (item != null && item.IsVaultManaged)
        {
            var targetChunks = item.VaultChunks;
            string shardFile = item.CompleteFileName;
            if (targetChunks.Count == 0 && (item.FileType == "TS3PACK" || item.PackageType == "Sims3Pack"))
            {
                var firstChild = await _db.MetaEntities
                    .AsNoTracking()
                    .Include(m => m.VaultChunks)
                    .FirstOrDefaultAsync(m => m.ParentMetaEntityId == item.Id && m.VaultChunks.Count > 0);
                if (firstChild != null)
                {
                    targetChunks = firstChild.VaultChunks;
                    shardFile = firstChild.CompleteFileName;
                }
            }

            if (targetChunks.Count > 0 && File.Exists(shardFile))
            {
                var thumbChunk = targetChunks
                    .Where(c => ThumbRank(c.Type) >= 0)
                    .OrderBy(c => ThumbRank(c.Type))
                    .FirstOrDefault();
                var casParts = targetChunks.Where(c => c.Type == Sims3ResourceTypes.Casp).Select(c => c.Instance).ToList();

                try
                {
                    Func<byte[]>? readEmbedded = thumbChunk == null ? null
                        : () => DBPFVaultShard.ReadDecompressedChunk(shardFile, thumbChunk.Offset, thumbChunk.ChunkLength, thumbChunk.ResourceLength, thumbChunk.IsCompressed);
                    if (TrySavePreview(thumbPath, readEmbedded, thumbChunk != null && IsIconThumb(thumbChunk.Type), casParts))
                    {
                        return thumbPath;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"Error extracting thumbnail of vault item {itemId}", ex, "ThumbnailService");
                    return null;
                }

                // The item's file is the shared shard: reading it as a package would pick another mod's thumbnail.
                try { await File.WriteAllBytesAsync(noThumbPath, Array.Empty<byte>()); } catch { }
                return null;
            }

            // Same for an item whose chunks are gone or whose shard is missing.
            if (item.VaultShardId != null) return null;
        }

        string? filePath = completeFileName;
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            if (item == null || !File.Exists(item.CompleteFileName)) return null;
            filePath = item.CompleteFileName;
        }

        try
        {
            if (filePath.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase))
            {
                using var sims3Pack = new Sims3Pack(filePath);
                if (sims3Pack.Thumbnails != null && sims3Pack.Thumbnails.Count > 0)
                {
                    using var firstThumb = sims3Pack.Thumbnails[0];
                    using var ms = new MemoryStream();
                    await firstThumb.CopyToAsync(ms);
                    SaveOptimizedThumbnail(ms.ToArray(), thumbPath);
                    return thumbPath;
                }
                else if (sims3Pack.Thumbnail != null)
                {
                    using var thumb = sims3Pack.Thumbnail;
                    using var ms = new MemoryStream();
                    await thumb.CopyToAsync(ms);
                    SaveOptimizedThumbnail(ms.ToArray(), thumbPath);
                    return thumbPath;
                }
                else if (sims3Pack.Packages != null)
                {
                    var res = FindBestThumbnail(sims3Pack.Packages);
                    if (TrySavePreview(thumbPath, res == null ? null : res.Read, res != null && IsIconThumb(res.Key.Type), CasPartInstances(sims3Pack.Packages)))
                    {
                        return thumbPath;
                    }
                }
            }
            else
            {
                using var package = new DBPFPackage(filePath);
                var packages = new[] { package };
                var res = FindBestThumbnail(packages);

                if (TrySavePreview(thumbPath, res == null ? null : res.Read, res != null && IsIconThumb(res.Key.Type), CasPartInstances(packages)))
                {
                    return thumbPath;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"Error extracting thumbnail from {filePath}", ex, "ThumbnailService");
            return null;
        }

        // Negative-cache marker: package has no thumbnail
        try
        {
            await File.WriteAllBytesAsync(noThumbPath, Array.Empty<byte>());
        }
        catch { }

        return null;
    }

    public static void SaveOptimizedThumbnail(byte[] rawBytes, string destPath)
    {
        try
        {
            using var original = SKBitmap.Decode(rawBytes);
            if (original != null && original.Width > 0 && original.Height > 0)
            {
                int maxDim = 256;
                int targetWidth = original.Width;
                int targetHeight = original.Height;

                if (original.Width > maxDim || original.Height > maxDim)
                {
                    if (original.Width >= original.Height)
                    {
                        targetWidth = maxDim;
                        targetHeight = Math.Max(1, (int)Math.Round((double)original.Height / original.Width * maxDim));
                    }
                    else
                    {
                        targetHeight = maxDim;
                        targetWidth = Math.Max(1, (int)Math.Round((double)original.Width / original.Height * maxDim));
                    }
                }

                var info = new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(info);
                if (surface != null)
                {
                    using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium };
                    surface.Canvas.DrawBitmap(original, new SKRect(0, 0, targetWidth, targetHeight), paint);
                    using var image = surface.Snapshot();
                    using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 82);
                    if (encoded != null)
                    {
                        using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan);
                        encoded.SaveTo(fs);
                        return;
                    }
                }
            }
        }
        catch
        {
            // Fallback to raw bytes if decoding fails
        }

        File.WriteAllBytes(destPath, rawBytes);
    }
}
