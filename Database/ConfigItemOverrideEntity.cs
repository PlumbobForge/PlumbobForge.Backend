namespace PlumbobForge.Backend.Database;

public class ConfigItemOverrideEntity
{
    public long Id { get; set; }

    public long ConfigEntityId { get; set; }
    public ConfigEntity? ConfigEntity { get; set; }

    public long MetaEntityId { get; set; }
    public MetaEntity? MetaEntity { get; set; }

    /// <summary>True: always loaded in this configuration. False: never loaded in it.</summary>
    public bool Include { get; set; }
}
