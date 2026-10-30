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

    public string TypeTag => Sims3ResourceTypes.GetTag(Key.Type);

    // Exactly one of these is set, depending on where the resource lives.
    internal ResourceEntry? Entry { get; init; }
    internal VaultChunkEntity? Chunk { get; init; }
    internal string? ShardPath { get; init; }
}

/// <param name="MetaEntityId">Vault mode: the item whose chunks make up this package (a child item for a Sims3Pack).</param>
public sealed record InspectedPart(int Index, string Label, int ResourceCount, long Bytes, long? MetaEntityId = null);

public sealed record PartEdits(int PartIndex, long? MetaEntityId, IReadOnlyList<ResourceEdit> Edits);

public sealed record ObjectToggle(TGI_Key Key, bool Enabled);

public sealed class InspectedThumbnail
{
    public required InspectedResource Resource { get; init; }
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
    public bool HasDuplicateKey { get; set; }
    public bool IsDisabled { get; set; }
    public List<InspectedThumbnail> Thumbnails { get; } = new();
}

public sealed record InspectionWarning(string Key, params object[] Args);
