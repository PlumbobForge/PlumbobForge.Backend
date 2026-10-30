using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace PlumbobForge.Backend.Services;

public sealed record ThumbnailExportResult(int Exported, int Failed, string Folder);

public static class ThumbnailExporter
{
    private static readonly string[] SizeNames = { "small", "medium", "large", "xlarge" };

    public static string GetExtension(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G') return ".png";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return ".jpg";
        if (data.Length >= 4 && data[0] == 'D' && data[1] == 'D' && data[2] == 'S' && data[3] == ' ') return ".dds";
        if (data.Length >= 2 && data[0] == 'B' && data[1] == 'M') return ".bmp";
        return ".bin";
    }

    public static string GetBaseFileName(InspectedObject? owner, InspectedThumbnail thumbnail)
    {
        string name;
        if (owner == null)
        {
            name = $"thumbnail_{thumbnail.Resource.Key.Instance:X16}";
        }
        else if (owner.Name != null)
        {
            name = owner.Name;
        }
        else
        {
            string kind = owner.InternalName ?? owner.ClothingType ?? (owner.Kind == InspectedObjectKind.Cas ? "cas_part" : "object");
            name = $"{kind}_{owner.Resource.Key.Instance:X16}";
        }

        string size = thumbnail.SizeIndex >= 0 && thumbnail.SizeIndex < SizeNames.Length ? SizeNames[thumbnail.SizeIndex] : "size" + thumbnail.SizeIndex;
        int variant = owner != null ? GetVariantNumber(owner, thumbnail) : 0;
        string variantSuffix = variant > 0 ? $"_v{variant}" : string.Empty;
        return Sanitize($"{name}_{size}{variantSuffix}");
    }

    public static int GetVariantNumber(InspectedObject owner, InspectedThumbnail thumbnail)
    {
        var groups = owner.Thumbnails.Select(t => t.Group).Distinct().ToList();
        return groups.Count > 1 ? groups.IndexOf(thumbnail.Group) + 1 : 0;
    }

    public static string GetSuggestedFileName(PackageInspection inspection, InspectedObject? owner, InspectedThumbnail thumbnail)
    {
        string ext;
        try
        {
            ext = GetExtension(inspection.Read(thumbnail.Resource));
        }
        catch
        {
            ext = ".png";
        }
        return GetBaseFileName(owner, thumbnail) + ext;
    }

    public static void ExportOne(PackageInspection inspection, InspectedThumbnail thumbnail, string destinationPath)
    {
        byte[] data = inspection.Read(thumbnail.Resource);
        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(destinationPath, data);
    }

    public static ThumbnailExportResult ExportMany(
        PackageInspection inspection,
        string folder,
        IReadOnlyList<(InspectedObject? Owner, InspectedThumbnail Thumbnail)>? only = null,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);

        var work = only?.ToList() ?? inspection.Objects
            .SelectMany(o => o.Thumbnails.Select(t => ((InspectedObject?)o, t)))
            .Concat(inspection.UnlinkedThumbnails.Select(t => ((InspectedObject?)null, t)))
            .ToList();
        var previews = only == null ? inspection.PackPreviews : new List<byte[]>();

        int total = work.Count + previews.Count;
        int exported = 0, failed = 0, done = 0;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (owner, thumbnail) in work)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                byte[] data = inspection.Read(thumbnail.Resource);
                string ext = GetExtension(data);
                string baseName = GetBaseFileName(owner, thumbnail);
                // Merged packages can hold several objects with the same name; the instance tells them apart.
                if (!used.Add(baseName + ext))
                {
                    baseName = Sanitize($"{baseName}_{thumbnail.Resource.Key.Instance:X16}");
                    used.Add(baseName + ext);
                }
                File.WriteAllBytes(UniquePath(folder, baseName, ext), data);
                exported++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
            }
            progress?.Report((++done, total));
        }

        for (int i = 0; i < previews.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                byte[] data = previews[i];
                File.WriteAllBytes(UniquePath(folder, $"sims3pack_preview_{i + 1}", GetExtension(data)), data);
                exported++;
            }
            catch
            {
                failed++;
            }
            progress?.Report((++done, total));
        }

        return new ThumbnailExportResult(exported, failed, folder);
    }

    public static string CreateExportFolder(string parent, string itemFileName)
    {
        string baseName = Sanitize(Path.GetFileNameWithoutExtension(itemFileName) + "_thumbnails");
        string path = Path.Combine(parent, baseName);
        for (int n = 2; Directory.Exists(path) || File.Exists(path); n++)
        {
            path = Path.Combine(parent, $"{baseName} ({n})");
        }
        Directory.CreateDirectory(path);
        return path;
    }

    private static string UniquePath(string folder, string baseName, string ext)
    {
        string path = Path.Combine(folder, baseName + ext);
        for (int n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{baseName} ({n}){ext}");
        }
        return path;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char ch in name.Trim())
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }
        string result = sb.ToString().TrimEnd('.', ' ');
        if (result.Length > 120) result = result[..120];
        return result.Length == 0 ? "thumbnail" : result;
    }
}
