using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PlumbobForge.Backend.Database;

public class SetsEntity
{
    public long Id { get; set; }
    [Required] public string Name { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public string LongName { get; set; } = string.Empty;
    public bool IsLegacy { get; set; }
    public bool IsExpanded { get; set; }
    public bool IsDefault { get; set; }
    public bool Dirty { get; set; }
    public string? CachedHash { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? Color { get; set; }
    
    public long? ParentSetsEntityId { get; set; }
    public SetsEntity? ParentSetsEntity { get; set; }

    public ICollection<SetsEntity> Children { get; set; } = new List<SetsEntity>();
    public ICollection<MetaEntity> MetaEntities { get; set; } = new List<MetaEntity>();
}
