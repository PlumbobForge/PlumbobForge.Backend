using System;
using System.Collections.Generic;
using System.IO;
using S3ForgeTools.GameFiles.Package;
using SkiaSharp;

namespace PlumbobForge.Backend.Services;

public static class ThumbnailImageTools
{
    public static (int Width, int Height)? ReadSize(byte[] image)
    {
        try
        {
            using var codec = SKCodec.Create(new MemoryStream(image, writable: false));
            return codec == null ? null : (codec.Info.Width, codec.Info.Height);
        }
        catch
        {
            return null;
        }
    }

    public static byte[] CreateThumbnail(byte[] source, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));

        if (ThumbnailExporter.GetExtension(source) == ".png" && ReadSize(source) is { } size && size.Width == width && size.Height == height)
        {
            return source;
        }

        using var original = SKBitmap.Decode(source) ?? throw new InvalidDataException("The image could not be read");
        float scale = Math.Max((float)width / original.Width, (float)height / original.Height);
        float drawWidth = original.Width * scale;
        float drawHeight = original.Height * scale;
        var dest = new SKRect((width - drawWidth) / 2f, (height - drawHeight) / 2f, (width + drawWidth) / 2f, (height + drawHeight) / 2f);

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create an image surface");
        surface.Canvas.Clear(SKColors.Transparent);
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
        {
            surface.Canvas.DrawBitmap(original, dest, paint);
        }
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("Could not encode the thumbnail");
        return encoded.ToArray();
    }

    private static int[] StandardSizes(uint familyBase) => familyBase switch
    {
        Sims3ResourceTypes.ThumbCas => new[] { 32, 128, 256 },
        Sims3ResourceTypes.ThumbIcon => new[] { 64, 256, 256, 256 },
        _ => new[] { 32, 64, 128 }
    };

    public static List<(TGI_Key Key, int Size)> GetNewThumbnailSlots(InspectedObject obj)
    {
        var slots = new List<(TGI_Key, int)>();
        uint family;
        ulong instance = obj.Resource.Key.Instance;
        uint group = 0;

        if (obj.Kind == InspectedObjectKind.Cas)
        {
            family = Sims3ResourceTypes.ThumbCas;
        }
        else if (obj.PngInstance != 0)
        {
            family = Sims3ResourceTypes.ThumbIcon;
            instance = obj.PngInstance;
        }
        else if (Sims3ResourceTypes.GetThumbnailFamilyForCatalog(obj.Resource.Key.Type) is uint catalogFamily)
        {
            family = catalogFamily;
            // Wall and floor patterns need a non-zero group.
            if (obj.Resource.Key.Type == Sims3ResourceTypes.CatalogWallFloorPattern) group = 1;
        }
        else
        {
            return slots;
        }

        int[] sizes = StandardSizes(family);
        int count = family == Sims3ResourceTypes.ThumbIcon ? 3 : sizes.Length;
        for (int i = 0; i < count; i++)
        {
            slots.Add((new TGI_Key(family + (uint)i, group, instance), sizes[i]));
        }
        return slots;
    }
}
