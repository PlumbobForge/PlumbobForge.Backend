using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using S3ForgeTools.GameFiles.Package;

namespace PlumbobForge.Backend.Services;

// Turning an object off in a Mods folder file. There is no cache rebuild that could leave its catalog resource out,
// so the resource stays in the same file under a PlumbobForge-only type the game ignores ("parked"), and goes back
// under its own key when the object is turned on again. The file carries its own state: moving or renaming it, or
// rescanning, loses nothing.
public static class ModsObjectParking
{
    // "PF" in the high bytes, the catalog type's position below.
    private const uint ParkedBase = 0x50460000u;

    private static readonly uint[] CatalogTypes =
    {
        Sims3ResourceTypes.Casp, Sims3ResourceTypes.Objd, Sims3ResourceTypes.CatalogFence, Sims3ResourceTypes.CatalogStairs,
        Sims3ResourceTypes.CatalogProxyProduct, Sims3ResourceTypes.CatalogTerrainGeometryBrush, Sims3ResourceTypes.CatalogRailing,
        Sims3ResourceTypes.CatalogTerrainPaintBrush, Sims3ResourceTypes.CatalogFireplace, Sims3ResourceTypes.CatalogTerrainWaterBrush,
        Sims3ResourceTypes.CatalogFountainPool, Sims3ResourceTypes.CatalogFoundation, Sims3ResourceTypes.CatalogWallFloorPattern,
        Sims3ResourceTypes.CatalogWall, Sims3ResourceTypes.CatalogRoofStyle, Sims3ResourceTypes.CatalogRoofPattern
    };

    public static bool CanPark(uint type) => Array.IndexOf(CatalogTypes, type) >= 0;

    public static TGI_Key Park(TGI_Key key)
    {
        int index = Array.IndexOf(CatalogTypes, key.Type);
        if (index < 0) throw new ArgumentException($"{key} is not a catalog resource", nameof(key));
        return new TGI_Key(ParkedBase | (uint)(index + 1), key.Group, key.Instance);
    }

    public static bool TryUnpark(TGI_Key key, out TGI_Key original)
    {
        uint slot = key.Type ^ ParkedBase;
        if ((key.Type & 0xFFFF0000u) == ParkedBase && slot >= 1 && slot <= CatalogTypes.Length)
        {
            original = new TGI_Key(CatalogTypes[slot - 1], key.Group, key.Instance);
            return true;
        }
        original = key;
        return false;
    }

    // The edits that turn objects on or off in a package file. Objects already in the asked state are skipped.
    public static List<ResourceEdit> BuildToggleEdits(string packagePath, IReadOnlyList<ObjectToggle> changes)
    {
        var edits = new List<ResourceEdit>();
        using var pkg = new DBPFPackage(packagePath);
        var keys = pkg.Resources.Select(r => r.Key).ToHashSet();
        foreach (var change in changes)
        {
            if (!CanPark(change.Key.Type)) continue;
            var parked = Park(change.Key);
            var (from, to) = change.Enabled ? (parked, change.Key) : (change.Key, parked);
            if (!keys.Contains(from) || keys.Contains(to)) continue;
            byte[] data = pkg.GetResource(from)?.Read() ?? throw new InvalidDataException($"Resource {from} could not be read");
            edits.Add(new ResourceEdit(from, null));
            edits.Add(new ResourceEdit(to, data));
        }
        return edits;
    }

    // Edits aimed at a turned-off object go to its parked copy, so it stays off.
    public static List<ResourceEdit> RedirectToParked(IEnumerable<ResourceEdit> edits, ISet<TGI_Key> keysInFile) =>
        edits.Select(e => !keysInFile.Contains(e.Key) && CanPark(e.Key.Type) && keysInFile.Contains(Park(e.Key))
                ? new ResourceEdit(Park(e.Key), e.NewData)
                : e)
            .ToList();
}
