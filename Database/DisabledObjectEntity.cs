namespace PlumbobForge.Backend.Database;

public class DisabledObjectEntity
{
    public long Id { get; set; }
    public long MetaEntityId { get; set; }
    public MetaEntity? MetaEntity { get; set; }
    public uint Type { get; set; }
    public uint Group { get; set; }
    public ulong Instance { get; set; }
}
