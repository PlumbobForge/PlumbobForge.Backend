using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PlumbobForge.Backend.Database;

[Table("CollectionItems")]
public class CollectionItemsEntity
{
    [Key]
    public long Id { get; set; }

    [Required]
    public ulong CollectionEntityId { get; set; }

    [ForeignKey(nameof(CollectionEntityId))]
    public virtual CollectionEntity? Collection { get; set; }

    [Required]
    public long MetaEntityId { get; set; }

    [ForeignKey(nameof(MetaEntityId))]
    public virtual MetaEntity? Item { get; set; }
}
