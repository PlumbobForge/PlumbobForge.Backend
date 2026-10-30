using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;
using S3ForgeTools.GameFiles.Resources;
using S3ForgeTools.GameFiles.TS3Pack;

namespace PlumbobForge.Backend.Services;

public sealed class PackageInspection : IDisposable
{
    private readonly object _readLock = new();
    private readonly List<IDisposable> _owned = new();

    public string FileName { get; }
    public bool IsVault { get; }
    public bool IsSims3Pack { get; }

    public List<InspectedPart> Parts { get; } = new();
    public List<InspectedResource> Resources { get; } = new();
    public List<InspectedObject> Objects { get; } = new();
    public List<InspectedThumbnail> UnlinkedThumbnails { get; } = new();
    public List<byte[]> PackPreviews { get; } = new();
    public List<InspectionWarning> Warnings { get; } = new();

    public long TotalBytes => Resources.Sum(r => (long)r.ChunkLength);

    internal PackageInspection(string fileName, bool isVault, bool isSims3Pack)
    {
        FileName = fileName;
        IsVault = isVault;
        IsSims3Pack = isSims3Pack;
    }

    internal void Own(IDisposable disposable) => _owned.Add(disposable);

    public byte[] Read(InspectedResource resource)
    {
        lock (_readLock)
        {
            if (resource.Entry != null)
            {
                return resource.Entry.Read();
            }
            if (resource.Chunk != null && resource.ShardPath != null)
            {
                var c = resource.Chunk;
                return DBPFVaultShard.ReadDecompressedChunk(resource.ShardPath, c.Offset, c.ChunkLength, c.ResourceLength, c.IsCompressed);
            }
        }
        throw new InvalidOperationException("Resource has no source");
    }

    public void Dispose()
    {
        lock (_readLock)
        {
            foreach (var d in _owned)
            {
                try { d.Dispose(); } catch { }
            }
            _owned.Clear();
        }
    }
}

public class PackageInspectorService
{
    private readonly AppDbContext _db;

    public PackageInspectorService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<PackageInspection> InspectAsync(long metaEntityId, CancellationToken ct = default)
    {
        var meta = await _db.MetaEntities.AsNoTracking()
            .Include(m => m.VaultChunks)
            .FirstOrDefaultAsync(m => m.Id == metaEntityId, ct)
            ?? throw new InvalidOperationException($"Item {metaEntityId} no longer exists");

        // A vault Sims3Pack keeps its packages on child items, one per package.
        var children = new List<MetaEntity>();
        if (meta.IsVaultManaged && meta.VaultChunks.Count == 0)
        {
            children = await _db.MetaEntities.AsNoTracking()
                .Include(m => m.VaultChunks)
                .Where(m => m.ParentMetaEntityId == meta.Id)
                .OrderBy(m => m.Id)
                .ToListAsync(ct);
        }

        var disabled = (await _db.DisabledObjects.AsNoTracking()
                .Where(d => d.MetaEntityId == meta.Id)
                .Select(d => new { d.Type, d.Group, d.Instance })
                .ToListAsync(ct))
            .Select(d => new TGI_Key(d.Type, d.Group, d.Instance))
            .ToHashSet();

        var inspection = await Task.Run(() => Build(meta, children, ct), ct);
        foreach (var obj in inspection.Objects)
        {
            obj.IsDisabled = disabled.Contains(obj.Resource.Key);
        }
        return inspection;
    }

    public static PackageInspection InspectFile(string path, CancellationToken ct = default)
    {
        var meta = new MetaEntity { FileName = Path.GetFileName(path), CompleteFileName = path };
        return Build(meta, new List<MetaEntity>(), ct);
    }

    private static PackageInspection Build(MetaEntity meta, List<MetaEntity> children, CancellationToken ct)
    {
        bool isSims3Pack = meta.FileName.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase)
            || meta.FileType == "TS3PACK" || meta.PackageType == "Sims3Pack";
        var inspection = new PackageInspection(meta.FileName, meta.IsVaultManaged, isSims3Pack);
        try
        {
            if (meta.IsVaultManaged)
            {
                LoadFromVault(inspection, meta, children);
            }
            else
            {
                LoadFromFile(inspection, meta.CompleteFileName);
            }
            Analyse(inspection, ct);
            return inspection;
        }
        catch
        {
            inspection.Dispose();
            throw;
        }
    }

    private static void LoadFromFile(PackageInspection inspection, string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The item's file is missing", path);
        }

        if (path.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase))
        {
            var s3p = new Sims3Pack(path);
            inspection.Own(s3p);
            if (s3p.IsEncrypted) inspection.Warnings.Add(new InspectionWarning("look_inside.warning_encrypted"));
            if (s3p.IsCorrupt) inspection.Warnings.Add(new InspectionWarning("look_inside.warning_corrupt"));

            for (int i = 0; i < s3p.Packages.Count; i++)
            {
                var pkg = s3p.Packages[i];
                string label = string.IsNullOrEmpty(pkg.GUID) ? $"Package {i + 1}" : pkg.GUID + ".package";
                AddPackage(inspection, pkg, i, label);
            }

            foreach (var thumb in s3p.Thumbnails)
            {
                try
                {
                    using var ms = new MemoryStream();
                    thumb.Position = 0;
                    thumb.CopyTo(ms);
                    inspection.PackPreviews.Add(ms.ToArray());
                }
                catch { }
            }
        }
        else
        {
            var pkg = new DBPFPackage(path);
            inspection.Own(pkg);
            AddPackage(inspection, pkg, 0, Path.GetFileName(path));
        }
    }

    private static void AddPackage(PackageInspection inspection, DBPFPackage pkg, int partIndex, string label)
    {
        long bytes = 0;
        foreach (var r in pkg.Resources)
        {
            inspection.Resources.Add(new InspectedResource
            {
                Key = r.Key,
                PartIndex = partIndex,
                ChunkLength = r.ChunkLength,
                ResourceLength = (int)r.Length,
                IsCompressed = r.IsCompressed,
                Entry = r
            });
            bytes += r.Length;
        }
        inspection.Parts.Add(new InspectedPart(partIndex, label, pkg.Resources.Count, bytes));
    }

    private static void LoadFromVault(PackageInspection inspection, MetaEntity meta, List<MetaEntity> children)
    {
        var sources = meta.VaultChunks.Count > 0 ? new List<MetaEntity> { meta } : children;
        for (int i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            string shardPath = source.CompleteFileName;
            if (!File.Exists(shardPath))
            {
                inspection.Warnings.Add(new InspectionWarning("look_inside.warning_missing_shard", source.FileName));
                continue;
            }

            long bytes = 0;
            foreach (var c in source.VaultChunks.OrderBy(c => c.Id))
            {
                inspection.Resources.Add(new InspectedResource
                {
                    Key = new TGI_Key(c.Type, c.Group, c.Instance),
                    PartIndex = i,
                    ChunkLength = c.ChunkLength,
                    ResourceLength = c.ResourceLength,
                    IsCompressed = c.IsCompressed,
                    Chunk = c,
                    ShardPath = shardPath
                });
                bytes += c.ResourceLength;
            }
            inspection.Parts.Add(new InspectedPart(i, source.FileName, source.VaultChunks.Count, bytes, source.Id));
        }
    }

    private static void Analyse(PackageInspection inspection, CancellationToken ct)
    {
        var thumbsByInstance = new Dictionary<ulong, List<InspectedResource>>();
        foreach (var r in inspection.Resources)
        {
            if (!Sims3ResourceTypes.IsThumbnailType(r.Key.Type)) continue;
            if (!thumbsByInstance.TryGetValue(r.Key.Instance, out var list))
            {
                thumbsByInstance[r.Key.Instance] = list = new List<InspectedResource>();
            }
            list.Add(r);
        }

        var claimed = new HashSet<InspectedResource>();
        Dictionary<ulong, string>? strings = null;
        int unreadable = 0;

        foreach (var r in inspection.Resources)
        {
            ct.ThrowIfCancellationRequested();
            uint type = r.Key.Type;
            InspectedObject? obj = null;

            if (type == Sims3ResourceTypes.Casp)
            {
                obj = InspectCas(inspection, r);
                AttachThumbnails(obj, thumbsByInstance, r.Key.Instance, Sims3ResourceTypes.ThumbCas, claimed);
            }
            else if (Sims3ResourceTypes.IsCatalogType(type))
            {
                strings ??= ReadStrings(inspection);
                obj = InspectCatalog(inspection, r, strings, out ulong pngInstance);
                if (pngInstance != 0)
                {
                    AttachThumbnails(obj, thumbsByInstance, pngInstance, Sims3ResourceTypes.ThumbIcon, claimed);
                }
                if (obj.Thumbnails.Count == 0 && Sims3ResourceTypes.GetThumbnailFamilyForCatalog(type) is uint family)
                {
                    AttachThumbnails(obj, thumbsByInstance, r.Key.Instance, family, claimed);
                }
            }

            if (obj != null)
            {
                if (obj.CouldNotRead) unreadable++;
                inspection.Objects.Add(obj);
            }
        }

        var duplicateKeys = inspection.Resources
            .GroupBy(r => (r.PartIndex, r.Key))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();
        foreach (var obj in inspection.Objects)
        {
            obj.HasDuplicateKey = duplicateKeys.Contains((obj.Resource.PartIndex, obj.Resource.Key));
        }

        // Packages with a single object (typical for Sims3Packs) often carry a thumbnail under an unrelated instance.
        var singleObjectByPart = inspection.Objects
            .GroupBy(o => o.Resource.PartIndex)
            .Where(g => g.Count() == 1 && g.First().Thumbnails.Count == 0)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var r in inspection.Resources)
        {
            if (claimed.Contains(r) || !Sims3ResourceTypes.TryGetThumbnailFamily(r.Key.Type, out uint familyBase, out int size)) continue;
            if (singleObjectByPart.TryGetValue(r.PartIndex, out var owner))
            {
                owner.Thumbnails.Add(new InspectedThumbnail { Resource = r, SizeIndex = size, FamilyBase = familyBase, IsMatchedByPackage = true });
                continue;
            }
            inspection.UnlinkedThumbnails.Add(new InspectedThumbnail { Resource = r, SizeIndex = size, FamilyBase = familyBase });
        }

        if (unreadable > 0)
        {
            inspection.Warnings.Add(new InspectionWarning("look_inside.warning_unreadable_objects", unreadable));
        }
    }

    private static void AttachThumbnails(InspectedObject obj, Dictionary<ulong, List<InspectedResource>> thumbsByInstance, ulong instance, uint preferredFamily, HashSet<InspectedResource> claimed)
    {
        if (!thumbsByInstance.TryGetValue(instance, out var candidates)) return;

        var matches = new List<(InspectedResource Res, uint Family, int Size)>();
        foreach (var c in candidates)
        {
            Sims3ResourceTypes.TryGetThumbnailFamily(c.Key.Type, out uint family, out int size);
            matches.Add((c, family, size));
        }
        if (matches.Any(m => m.Family == preferredFamily))
        {
            matches = matches.Where(m => m.Family == preferredFamily).ToList();
        }

        foreach (var (res, family, size) in matches.OrderBy(m => m.Res.Key.Group).ThenBy(m => m.Size))
        {
            obj.Thumbnails.Add(new InspectedThumbnail { Resource = res, SizeIndex = size, FamilyBase = family });
            claimed.Add(res);
        }
    }

    private static InspectedObject InspectCas(PackageInspection inspection, InspectedResource r)
    {
        var obj = new InspectedObject { Kind = InspectedObjectKind.Cas, Resource = r };
        try
        {
            var casp = new ResourceCASP(inspection.Read(r));
            if (casp.ClothingType == 0 && casp.AgeGender == 0)
            {
                obj.CouldNotRead = true;
                return obj;
            }

            // CASP names are usually the instance written as "high:low" decimals, so only keep real ones.
            if (!string.IsNullOrWhiteSpace(casp.Name) && !IsNumericName(casp.Name))
            {
                obj.Name = casp.Name;
            }
            obj.ClothingType = GetClothingTypeName(casp.ClothingType);
            obj.CategoryKey = GetCasCategoryKey(casp.ClothingType);

            var ages = new List<string>();
            if ((casp.AgeGender & 0x0001) != 0) ages.Add("Baby");
            if ((casp.AgeGender & 0x0002) != 0) ages.Add("Toddler");
            if ((casp.AgeGender & 0x0004) != 0) ages.Add("Child");
            if ((casp.AgeGender & 0x0008) != 0) ages.Add("Teen");
            if ((casp.AgeGender & 0x0010) != 0) ages.Add("YoungAdult");
            if ((casp.AgeGender & 0x0020) != 0) ages.Add("Adult");
            if ((casp.AgeGender & 0x0040) != 0) ages.Add("Elder");
            obj.Ages = ages;

            var genders = new List<string>();
            if ((casp.AgeGender & 0x1000) != 0) genders.Add("Male");
            if ((casp.AgeGender & 0x2000) != 0) genders.Add("Female");
            obj.Genders = genders;

            obj.CasCategory = casp.Category;
            obj.OutfitCategories = CasFlagsService.OutfitBits
                .Where(b => (casp.Category & b.Bit) != 0)
                .Select(b => b.Key)
                .ToList();
        }
        catch
        {
            obj.CouldNotRead = true;
        }
        return obj;
    }

    private static InspectedObject InspectCatalog(PackageInspection inspection, InspectedResource r, Dictionary<ulong, string> strings, out ulong pngInstance)
    {
        pngInstance = 0;
        var obj = new InspectedObject
        {
            Kind = InspectedObjectKind.BuildBuy,
            Resource = r,
            CategoryKey = Sims3ResourceTypes.GetCatalogKind(r.Key.Type) ?? "object"
        };

        CatalogResourceReader.CatalogInfo? info = null;
        try
        {
            info = CatalogResourceReader.TryRead(r.Key.Type, inspection.Read(r));
        }
        catch { }

        if (info is not { } c)
        {
            obj.CouldNotRead = true;
            return obj;
        }

        obj.Name = (c.NameGuid != 0 && strings.TryGetValue(c.NameGuid, out var name) ? name : null) ?? StripCatalogKey(c.Name);
        obj.Description = (c.DescGuid != 0 && strings.TryGetValue(c.DescGuid, out var desc) ? desc : null) ?? StripCatalogKey(c.Description);
        obj.InternalName = string.IsNullOrWhiteSpace(c.InstanceName) ? null : c.InstanceName;
        obj.Price = c.Price;
        obj.PngInstance = c.PngInstance;
        pngInstance = c.PngInstance;
        return obj;
    }

    private static string? StripCatalogKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        int colon = value.IndexOf(':');
        if (colon > 0 && value.StartsWith("Catalog", StringComparison.Ordinal) && colon < value.Length - 1)
        {
            return value[(colon + 1)..];
        }
        return value;
    }

    private static bool IsNumericName(string name)
    {
        foreach (char ch in name)
        {
            if (!char.IsDigit(ch) && ch != ':') return false;
        }
        return true;
    }

    private static Dictionary<ulong, string> ReadStrings(PackageInspection inspection)
    {
        var result = new Dictionary<ulong, string>();
        var tables = inspection.Resources
            .Where(r => r.Key.Type == Sims3ResourceTypes.Stbl)
            .OrderBy(r => (r.Key.Instance >> 56) == 0 ? 0 : 1);
        foreach (var table in tables)
        {
            try
            {
                ReadStringTable(inspection.Read(table), result);
            }
            catch { }
        }
        return result;
    }

    // STBL: "STBL", version byte, 2 reserved bytes, entry count, 6 reserved bytes; then per entry
    // an FNV-64 key, a length in characters and the UTF-16LE text.
    private static void ReadStringTable(byte[] data, Dictionary<ulong, string> into)
    {
        if (data.Length < 17 || data[0] != 'S' || data[1] != 'T' || data[2] != 'B' || data[3] != 'L') return;
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(7, 4));
        int pos = 17;
        for (int i = 0; i < count && pos + 12 <= data.Length; i++)
        {
            ulong key = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos, 8));
            int chars = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 8, 4));
            pos += 12;
            if (chars < 0 || pos + chars * 2 > data.Length) return;
            if (!into.ContainsKey(key))
            {
                into[key] = Encoding.Unicode.GetString(data, pos, chars * 2);
            }
            pos += chars * 2;
        }
    }

    private static string GetCasCategoryKey(uint clothingType) => clothingType switch
    {
        0x1 => "Hair",
        0x4 => "FullBody",
        0x5 => "Tops",
        0x6 => "Bottoms",
        0x7 => "Shoes",
        >= 0x8 and <= 0xF => "Accessories",
        >= 0x10 and <= 0x17 => "Details",
        _ => "Other"
    };

    // Clothing type names from s3pi's CASPartResource.ClothingType.
    private static readonly string[] ClothingTypeNames =
    {
        "None", "Hair", "Scalp", "Face", "Body", "Top", "Bottom", "Shoes", "Accessory", "Necklace", "Nose ring", "Earrings",
        "Glasses", "Bracelet", "Ring", "Wedding ring", "Beard", "Lipstick", "Eyeshadow", "Eyeliner", "Blush", "Costume makeup",
        "Eyebrows", "Eye color", "Gloves", "Socks", "Mascara", "Moles", "Freckles", "Weathering", "Earring (left)", "Earring (right)",
        "Armband", "Tattoo", "Tattoo template", "Dental", "Garter (left)", "Garter (right)", "Birthmark", "Body hair (upper chest)",
        "Body hair (chest and stomach)", "Body hair (lower back)", "Body hair (upper back)", "Body hair (full back)", "Body hair (forearms)",
        "Body hair (calves)", "Body hair (feet)", "Pet body", "Pet tail", "Pet ears", "Pet mane", "Pet collar / bridle", "Pet saddle",
        "Pelt layer", "Base pelt layer", "Pet hooves", "Pet horn", "Pet blanket", "Pet breast collar", "Pet beard"
    };

    private static string GetClothingTypeName(uint clothingType) =>
        clothingType < ClothingTypeNames.Length ? ClothingTypeNames[clothingType] : $"0x{clothingType:X2}";
}
