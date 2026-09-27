using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PlumbobForge.Backend.Database;

[Table("Collections")]
public class CollectionEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public ulong Id { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    public uint IconType { get; set; } = 0x2F7D0004;

    public uint IconGroup { get; set; } = 0;

    public ulong IconInstance { get; set; } = 0;

    public uint Flags { get; set; } = 0;

    public bool IsPlumbobForge { get; set; } = true;
    public bool HideFromCatalog { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<CollectionSetsEntity> CollectionSets { get; set; } = new List<CollectionSetsEntity>();
}
