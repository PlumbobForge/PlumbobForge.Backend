namespace PlumbobForge.Backend.Database;

public class SettingEntity
{
    public long Id { get; set; }
    public string? Key { get; set; }
    public string? Value { get; set; }

    public long ConfigEntityId { get; set; }
    public ConfigEntity? ConfigEntity { get; set; }
}
