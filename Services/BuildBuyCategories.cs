using System;
using System.Collections.Generic;
using System.Linq;
using S3ForgeTools.GameFiles.Package;

namespace PlumbobForge.Backend.Services;

public static class BuildBuyCategories
{
    // Kinds
    public const string KindObjects = "Objects";
    public const string KindDoorsWindows = "DoorsWindows";
    public const string KindLandscaping = "Landscaping";
    public const string KindWallsFloors = "WallsFloors";
    public const string KindTerrain = "Terrain";
    public const string KindFencesRailings = "FencesRailings";
    public const string KindStairs = "Stairs";
    public const string KindRoofs = "Roofs";
    public const string KindOtherBuild = "OtherBuild";

    // OBJD BuildCategory bits
    private const uint BuildDoorsWindows = 0x00000002 | 0x00000004 | 0x00000008 | 0x00000100; // Door, Window, Gate, Arch
    private const uint BuildLandscaping = 0x00000200 | 0x00000400 | 0x00000800 | 0x00002000;  // Flower, Shrub, Tree, Rock
    private const uint BuildStairs = 0x00010000 | 0x00020000;                                   // Elevator, SpiralStaircase
    private const uint BuildOther = 0x00000010 | 0x00000020 | 0x00000040 | 0x00000080 |       // Column, RabbitHole, Fireplace, Chimney
                                    0x00004000 | 0x00008000 |                                   // Shell, Landmark
                                    0x10000000 | 0x40000000;                                    // Blueprint, ModularArch

    private static readonly (string Name, uint Mask)[] RoomFlags =
    {
        ("Living", 0x00000002),
        ("Dining", 0x00000004),
        ("Kitchen", 0x00000008),
        ("Nursery", 0x00000010),
        ("Bathroom", 0x00000020),
        ("Bedroom", 0x00000040),
        ("Study", 0x00000080),
        ("Outdoor", 0x00000100 | 0x00000800 | 0x00001000), // Outside, Pool, Fountain
        ("Community", 0x00000200),
        ("Resort", 0x007FE000),
    };

    private static readonly (string Name, uint Mask)[] FunctionFlags =
    {
        ("Appliances", 0x00000002),
        ("Electronics", 0x00000004),
        ("Entertainment", 0x00000008),
        ("Lighting", 0x00000020),
        ("Plumbing", 0x00000040),
        ("Decor", 0x00000080),
        ("Kids", 0x00000100),
        ("Storage", 0x00000200),
        ("Comfort", 0x00000800),
        ("Surfaces", 0x00001000),
        ("Vehicles", 0x00002000),
        ("Pets", 0x00004000),
        ("Misc", 0x00008000 | 0x00010000 | 0x40000000), // ShowStage, Resort, Debug
    };

    public static string? KindForCatalogType(uint type) => type switch
    {
        Sims3ResourceTypes.CatalogWallFloorPattern or Sims3ResourceTypes.CatalogWall => KindWallsFloors,
        Sims3ResourceTypes.CatalogTerrainPaintBrush or Sims3ResourceTypes.CatalogTerrainGeometryBrush or
            Sims3ResourceTypes.CatalogTerrainWaterBrush => KindTerrain,
        Sims3ResourceTypes.CatalogFence or Sims3ResourceTypes.CatalogRailing => KindFencesRailings,
        Sims3ResourceTypes.CatalogStairs => KindStairs,
        Sims3ResourceTypes.CatalogRoofStyle or Sims3ResourceTypes.CatalogRoofPattern => KindRoofs,
        Sims3ResourceTypes.CatalogFireplace or Sims3ResourceTypes.CatalogFountainPool or
            Sims3ResourceTypes.CatalogFoundation or Sims3ResourceTypes.CatalogProxyProduct => KindOtherBuild,
        _ => null
    };

    public static string KindForObject(uint buildCategoryFlags)
    {
        if ((buildCategoryFlags & BuildDoorsWindows) != 0) return KindDoorsWindows;
        if ((buildCategoryFlags & BuildLandscaping) != 0) return KindLandscaping;
        if ((buildCategoryFlags & BuildStairs) != 0) return KindStairs;
        if ((buildCategoryFlags & BuildOther) != 0) return KindOtherBuild;
        return KindObjects;
    }

    // TS3 has no Laundry room flag; laundry items are marked by this room sub-category bit instead.
    private const ulong RoomSubLaundry = 0x0400000000000000;

    public static IEnumerable<string> RoomNames(uint roomCategoryFlags, ulong roomSubCategoryFlags)
    {
        var names = RoomFlags.Where(f => (roomCategoryFlags & f.Mask) != 0).Select(f => f.Name);
        return (roomSubCategoryFlags & RoomSubLaundry) != 0 ? names.Append("Laundry") : names;
    }

    public static IEnumerable<string> FunctionNames(uint functionCategoryFlags) =>
        FunctionFlags.Where(f => (functionCategoryFlags & f.Mask) != 0).Select(f => f.Name);
}

public sealed class BuildBuyAccumulator
{
    private readonly HashSet<string> _kinds = new();
    private readonly HashSet<string> _rooms = new();
    private readonly HashSet<string> _functions = new();
    private double? _priceMin;
    private double? _priceMax;

    public void AddCatalogType(uint type)
    {
        var kind = BuildBuyCategories.KindForCatalogType(type);
        if (kind != null) _kinds.Add(kind);
    }

    public void AddObjd(byte[] data)
    {
        S3ForgeTools.GameFiles.Resources.ResourceOBJD objd;
        try
        {
            objd = new S3ForgeTools.GameFiles.Resources.ResourceOBJD(data);
        }
        catch
        {
            _kinds.Add(BuildBuyCategories.KindObjects);
            return;
        }

        if (!objd.HasCatalogFlags)
        {
            _kinds.Add(BuildBuyCategories.KindObjects);
        }
        else
        {
            _kinds.Add(BuildBuyCategories.KindForObject(objd.BuildCategoryFlags));
            _rooms.UnionWith(BuildBuyCategories.RoomNames(objd.RoomCategoryFlags, objd.RoomSubCategoryFlags));
            _functions.UnionWith(BuildBuyCategories.FunctionNames(objd.FunctionCategoryFlags));
        }

        double price = objd.Price;
        if (double.IsFinite(price) && price >= 0)
        {
            _priceMin = _priceMin.HasValue ? Math.Min(_priceMin.Value, price) : price;
            _priceMax = _priceMax.HasValue ? Math.Max(_priceMax.Value, price) : price;
        }
    }

    public PackageTypeInfo ApplyTo(PackageTypeInfo info) => info with
    {
        BBKind = string.Join(",", _kinds),
        BBRooms = string.Join(",", _rooms),
        BBFunctions = string.Join(",", _functions),
        BBPriceMin = _priceMin,
        BBPriceMax = _priceMax
    };
}
