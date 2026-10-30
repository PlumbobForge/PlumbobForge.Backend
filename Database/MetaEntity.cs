using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

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

    public long? SetsEntityId { get; set; }
    public SetsEntity? SetsEntity { get; set; }

    public bool IsVaultManaged { get; set; } = false;
    public long? VaultShardId { get; set; }
    public VaultShardEntity? VaultShard { get; set; }

    public long? ParentMetaEntityId { get; set; }
    public MetaEntity? ParentMetaEntity { get; set; }
    public ICollection<MetaEntity> SubItems { get; set; } = new List<MetaEntity>();

    public ICollection<VaultChunkEntity> VaultChunks { get; set; } = new List<VaultChunkEntity>();
}
