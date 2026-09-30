using System;
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

    public void DeleteThumbnail(long itemId)
    {
        try
        {
            string thumbDir = Path.Combine(_options.DocumentBaseDir, "Thumbnails");
            string thumbPath = Path.Combine(thumbDir, $"{itemId}.thumb");
            string noThumbPath = Path.Combine(thumbDir, $"{itemId}.nothumb");

            if (File.Exists(thumbPath)) File.Delete(thumbPath);
            if (File.Exists(noThumbPath)) File.Delete(noThumbPath);
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
        0x626F60CC, 0x626F60CD, 0x626F60CE, // Custom thumbnails (highest priority)
        0x2E75C765, 0x2E75C764, 0x2E75C766, // Auto-generated CAS / Object thumbnails
        0x0B202AD9, // THUM
        0x0580A2B4, 0x0580A2B5, 0x0580A2B6 // Other UI thumbnails
    };

    private static ResourceEntry? FindBestThumbnail(DBPFPackage package)
    {
        ResourceEntry? best = null;
        int bestPriority = int.MaxValue;

        foreach (var r in package.Resources)
        {
            uint type = r.Key.Type;
            for (int i = 0; i < ValidThumbTypes.Length; i++)
            {
                if (type == ValidThumbTypes[i])
                {
                    if (i == 0) // Highest priority found (custom thumbnail) - return immediately!
                    {
                        return r;
                    }
                    if (i < bestPriority)
                    {
                        best = r;
                        bestPriority = i;
                    }
                    break;
                }
            }
        }

        return best;
    }

    public async Task<string?> GetThumbnailPathAsync(long itemId)
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

        var item = await _db.MetaEntities.FindAsync(itemId);
        if (item == null || !File.Exists(item.CompleteFileName)) return null;

        try
        {
            if (item.FileName.ToLower().EndsWith(".sims3pack"))
            {
                using var sims3Pack = new Sims3Pack(item.CompleteFileName);
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
                    foreach (var pkg in sims3Pack.Packages)
                    {
                        var res = FindBestThumbnail(pkg);
                        if (res != null)
                        {
                            var bytes = res.Read();
                            SaveOptimizedThumbnail(bytes, thumbPath);
                            return thumbPath;
                        }
                    }
                }
            }
            else
            {
                using var package = new DBPFPackage(item.CompleteFileName);
                var res = FindBestThumbnail(package);

                if (res != null)
                {
                    var bytes = res.Read();
                    SaveOptimizedThumbnail(bytes, thumbPath);
                    return thumbPath;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"Error extracting thumbnail from {item.CompleteFileName}", ex, "ThumbnailService");
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
