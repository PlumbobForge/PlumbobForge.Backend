using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PlumbobForge.Backend.Database;

[Table("CollectionSets")]
public class CollectionSetsEntity
{
    [Key]
    public long Id { get; set; }

    [Required]
    public ulong CollectionEntityId { get; set; }

    [ForeignKey(nameof(CollectionEntityId))]
    public virtual CollectionEntity? Collection { get; set; }

    [Required]
    public long SetsEntityId { get; set; }

    [ForeignKey(nameof(SetsEntityId))]
    public virtual SetsEntity? Set { get; set; }
}
