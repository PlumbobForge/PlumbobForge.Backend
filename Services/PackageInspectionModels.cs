using System;
using System.Collections.Generic;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;

namespace PlumbobForge.Backend.Services;

public enum InspectedObjectKind
{
    Cas,
    BuildBuy
}

public sealed class InspectedResource
{
    public TGI_Key Key { get; init; }
    public int PartIndex { get; init; }
    public int ChunkLength { get; init; }
    public int ResourceLength { get; init; }
    public bool IsCompressed { get; init; }
    // A turned-off object's catalog resource in a Mods file, stored under a PlumbobForge-only type (Key is its own).
    public bool IsParked { get; init; }

    public string TypeTag => Sims3ResourceTypes.GetTag(Key.Type);

    // Exactly one of these is set, depending on where the resource lives.
    internal ResourceEntry? Entry { get; init; }
    internal VaultChunkEntity? Chunk { get; init; }
    internal string? ShardPath { get; init; }
}

public sealed record InspectedPart(int Index, string Label, int ResourceCount, long Bytes, long? MetaEntityId = null);

public sealed record PartEdits(int PartIndex, long? MetaEntityId, IReadOnlyList<ResourceEdit> Edits);

public sealed record ObjectToggle(TGI_Key Key, bool Enabled);

public sealed class InspectedThumbnail
{
    public required InspectedResource Resource { get; init; }
    // 0 small, 1 medium, 2 large, 3 extra large.
    public int SizeIndex { get; init; }
    public uint FamilyBase { get; init; }
    public uint Group => Resource.Key.Group;
    public bool IsMatchedByPackage { get; init; }
}

public sealed class InspectedObject
{
    public InspectedObjectKind Kind { get; init; }
    public required InspectedResource Resource { get; init; }

    public string? Name { get; set; }
    public string? InternalName { get; set; }
    public string? Description { get; set; }
    public float? Price { get; set; }
    public ulong PngInstance { get; set; }

    public string CategoryKey { get; set; } = "Other";
    public string? ClothingType { get; set; }
    public IReadOnlyList<string> Ages { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Genders { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> OutfitCategories { get; set; } = Array.Empty<string>();
    public uint CasCategory { get; set; }

    public bool CouldNotRead { get; set; }
    // Saving an edit keeps a single copy of a duplicated key, so such a resource is never edited itself.
    public bool HasDuplicateKey { get; set; }
    public bool IsDisabled { get; set; }
    // Filled in separately by MeshStatsService, because reading meshes takes a while.
    public MeshStats? Mesh { get; set; }
    // Set when the item was optimized: counts before, and the resources to restore.
    public MeshStats? OriginalMesh { get; set; }
    public IReadOnlyList<(int Part, TGI_Key Key)> OptimizedKeys { get; set; } = Array.Empty<(int Part, TGI_Key Key)>();
    public List<InspectedThumbnail> Thumbnails { get; } = new();
}

public sealed record InspectionWarning(string Key, params object[] Args);
