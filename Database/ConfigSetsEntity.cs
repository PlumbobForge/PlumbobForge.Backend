namespace PlumbobForge.Backend.Database;

public class ConfigSetsEntity
{
    public long Id { get; set; }
    
    public long ConfigEntityId { get; set; }
    public ConfigEntity? ConfigEntity { get; set; }

    public long SetsEntityId { get; set; }
    public SetsEntity? SetsEntity { get; set; }
}
