using System;
using System.IO;
using System.Runtime.CompilerServices;
using CatalogResource;
using S3ForgeTools.GameFiles.Package;
using S3ForgeTools.GameFiles.Resources;

namespace PlumbobForge.Backend.Services;

internal static class CatalogResourceReader
{
    public readonly record struct CatalogInfo(string Name, string Description, ulong NameGuid, ulong DescGuid, float Price, ulong PngInstance, string? InstanceName);

    public static CatalogInfo? TryRead(uint type, byte[] data)
    {
        try
        {
            var info = ReadWithS3pi(type, data);
            if (info != null) return info;
        }
        catch
        {
            // Fall through to the app's own OBJD reader
        }

        if (type == Sims3ResourceTypes.Objd)
        {
            try
            {
                var objd = new ResourceOBJD(data);
                return new CatalogInfo(objd.Name ?? "", objd.Description ?? "", 0, 0, objd.Price, 0, objd.InstanceName);
            }
            catch { }
        }
        return null;
    }

    // Kept out of TryRead so a failure to load the s3pi assemblies surfaces as an exception here, not when TryRead is compiled.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CatalogInfo? ReadWithS3pi(uint type, byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        CatalogResource.CatalogResource? res = type switch
        {
            Sims3ResourceTypes.Objd => new ObjectCatalogResource(0, ms),
            Sims3ResourceTypes.CatalogFence => new FenceCatalogResource(0, ms),
            Sims3ResourceTypes.CatalogStairs => new StairsCatalogResource(0, ms),
            Sims3ResourceTypes.CatalogProxyProduct => new ProxyProductCatalogResource(0, ms),
            Sims3ResourceTypes.CatalogTerrainGeometryBrush or Sims3ResourceTypes.CatalogTerrainWaterBrush => new TerrainGeometryWaterBrushCatalogResource(0, ms),
            Sims3ResourceTypes.CatalogRailing => new RailingCatalogResource(0, ms),
            Sims3ResourceTypes.CatalogTerrainPaintBrush => new TerrainPaintBrushCatalogResource(0, ms),
            Sims3ResourceTypes.CatalogFireplace => new FireplaceCatalogResource(0, ms),
            Sims3ResourceTypes.CatalogFountainPool => new FountainPoolCatalogResource(0, ms),
            Sims3ResourceTypes.CatalogFoundation => new FoundationCatalogResource(0, ms),
            Sims3ResourceTypes.CatalogWallFloorPattern => new WallFloorPatternCatalogResource(0, ms),
            Sims3ResourceTypes.CatalogWall => new WallCatalogResource(0, ms),
            Sims3ResourceTypes.CatalogRoofStyle => new RoofStyleCatalogResource(0, ms),
            Sims3ResourceTypes.CatalogRoofPattern => new RoofPatternCatalogResource(0, ms),
            _ => null
        };
        if (res?.CommonBlock == null) return null;

        var c = res.CommonBlock;
        string? instanceName = res is ObjectCatalogResource objd ? objd.InstanceName : null;
        return new CatalogInfo(c.Name ?? "", c.Desc ?? "", c.NameGUID, c.DescGUID, c.Price, c.PngInstance, instanceName);
    }
}
