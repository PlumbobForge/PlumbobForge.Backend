namespace PlumbobForge.Backend.Database;

// A Mods folder a configuration turns off. Stored as "off" so folders that appear later, and configurations made
// before Mods support, start out on.
public class ConfigModsFolderOffEntity
{
    public long Id { get; set; }

    public long ConfigEntityId { get; set; }
    public ConfigEntity? ConfigEntity { get; set; }

    public long ModsFolderId { get; set; }
    public ModsFolderEntity? ModsFolder { get; set; }
}
