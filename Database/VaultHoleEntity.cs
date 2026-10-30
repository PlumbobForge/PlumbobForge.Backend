namespace PlumbobForge.Backend.Database;

public class VaultHoleEntity
{
    public long Id { get; set; }

    public long VaultShardId { get; set; }
    public VaultShardEntity? VaultShard { get; set; }

    public long Offset { get; set; }
    public long Length { get; set; }

    public bool IsPending { get; set; }
}
