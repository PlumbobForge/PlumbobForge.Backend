using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PlumbobForge.Backend.Database;

public class VaultShardEntity
{
    public long Id { get; set; }

    public long SetsEntityId { get; set; }
    public SetsEntity? SetsEntity { get; set; }

    public int ShardIndex { get; set; }

    [Required]
    public string RelativePath { get; set; } = string.Empty;

    public long FileSize { get; set; }

    public long DeadHoleBytes { get; set; }

    public uint HeaderIndexOffset { get; set; }

    public uint HeaderIndexSize { get; set; }

    public int ResourceCount { get; set; }

    public ICollection<VaultChunkEntity> Chunks { get; set; } = new List<VaultChunkEntity>();
    public ICollection<VaultHoleEntity> Holes { get; set; } = new List<VaultHoleEntity>();
}
