using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PlumbobForge.Backend.Database;

public class MetaEntity
{
    public long Id { get; set; }
    [Required] public string FileName { get; set; } = string.Empty;
    public string CompleteFileName { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public double FileSize { get; set; }
    [Required] public string Filehash { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsFavorite { get; set; } = false;
    public string? URL { get; set; }
    [Required] public string PackageType { get; set; } = string.Empty;
    public string? ResourceID { get; set; }
    public string? ThumbnailID { get; set; }
    public string? InstallDate { get; set; }
    public string? Manifest { get; set; }
    public string? CASCategories { get; set; }
    public string? CASAge { get; set; }
    public string? CASGender { get; set; }
    public string? CASOutfitCategory { get; set; }
    public string? BBKind { get; set; }
    public string? BBRooms { get; set; }
    public string? BBFunctions { get; set; }
    public double? BBPriceMin { get; set; }
    public double? BBPriceMax { get; set; }
    public bool IsUserTagged { get; set; } = false;
    public string? UserTags { get; set; }
    public bool SkipTextureOptimization { get; set; } = false;
    public int DisabledObjectCount { get; set; }
    public string? DisabledObjectsStamp { get; set; }
    // Set when some configuration excludes this item. A dynamic library cache then leaves it out of its set's bundle
    // (the game loads everything in a bundle) and builds it into the configuration item cache when it is loaded.
    // Includes alone never set it: the item stays in its set's bundle and gets a copy only where its set is off.
    // The column keeps its old name, from when any rule set it.
    [Column("HasConfigOverrides")]
    public bool ExcludedByAnyConfig { get; set; }

    public long? SetsEntityId { get; set; }
    public SetsEntity? SetsEntity { get; set; }

    public bool IsVaultManaged { get; set; } = false;
    public long? VaultShardId { get; set; }
    public VaultShardEntity? VaultShard { get; set; }

    // A file in the game's Mods folder, managed where it lies. MetaEntities leaves these out (query filter in
    // AppDbContext) so library and vault code never sees them; AllMetaEntities includes them.
    // CompleteFileName is the file's current path (ending in .disabled while it is off); FileName never has
    // the .disabled suffix. ModsPriority is the Resource.cfg priority it loads at, null when Resource.cfg doesn't
    // load it (too deep, a .sims3pack, or a folder Resource.cfg doesn't list).
    public bool IsModsItem { get; set; }
    public long? ModsFolderId { get; set; }
    public ModsFolderEntity? ModsFolder { get; set; }
    public int? ModsPriority { get; set; }

    public long? ParentMetaEntityId { get; set; }
    public MetaEntity? ParentMetaEntity { get; set; }
    public ICollection<MetaEntity> SubItems { get; set; } = new List<MetaEntity>();

    public ICollection<VaultChunkEntity> VaultChunks { get; set; } = new List<VaultChunkEntity>();
}
