using PlumbobForge.Backend.Database;

namespace PlumbobForge.Backend.Services;

public sealed record PackageTypeInfo(
    string PackageType,
    string CASCategories = "",
    string CASAge = "",
    string CASGender = "",
    string CASOutfitCategory = "")
{
    public string BBKind { get; init; } = "";
    public string BBRooms { get; init; } = "";
    public string BBFunctions { get; init; } = "";
    public double? BBPriceMin { get; init; }
    public double? BBPriceMax { get; init; }

    public static PackageTypeInfo Other { get; } = new("Other");

    public void ApplyBuildBuyTo(MetaEntity meta)
    {
        meta.BBKind = Pick(meta, meta.BBKind, BBKind);
        meta.BBRooms = Pick(meta, meta.BBRooms, BBRooms);
        meta.BBFunctions = Pick(meta, meta.BBFunctions, BBFunctions);
        meta.BBPriceMin = BBPriceMin;
        meta.BBPriceMax = BBPriceMax;
    }

    public bool BuildBuyEquals(MetaEntity meta) =>
        meta.BBKind == Pick(meta, meta.BBKind, BBKind) &&
        meta.BBRooms == Pick(meta, meta.BBRooms, BBRooms) &&
        meta.BBFunctions == Pick(meta, meta.BBFunctions, BBFunctions) &&
        meta.BBPriceMin == BBPriceMin &&
        meta.BBPriceMax == BBPriceMax;

    private static string Pick(MetaEntity meta, string? current, string detected) =>
        meta.IsUserTagged && !string.IsNullOrEmpty(current) ? current : detected;
}
