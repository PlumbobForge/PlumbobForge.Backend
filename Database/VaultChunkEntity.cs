namespace PlumbobForge.Backend.Database;

public class VaultChunkEntity
{
    public long Id { get; set; }

    public long MetaEntityId { get; set; }
    public MetaEntity? MetaEntity { get; set; }

    public long VaultShardId { get; set; }
    public VaultShardEntity? VaultShard { get; set; }

    public uint Type { get; set; }
    public uint Group { get; set; }
    public ulong Instance { get; set; }

    public long Offset { get; set; }
    public int ChunkLength { get; set; }
    public int ResourceLength { get; set; }
    public bool IsCompressed { get; set; }

    // Optimised copy of a texture, stored uncompressed in the same shard next to the original (which is never changed).
    // OptimizedLength: 0 = not checked yet, -1 = checked and the original is kept, > 0 = copy at OptimizedOffset.
    public long OptimizedOffset { get; set; }
    public int OptimizedLength { get; set; }
    public int OptimizedResourceLength { get; set; }
    public int OptimizedProfile { get; set; }
}
