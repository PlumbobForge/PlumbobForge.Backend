using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PlumbobForge.Backend.Database;

public class ConfigEntity
{
    public long Id { get; set; }
    [Required] public string Name { get; set; } = string.Empty;
    public bool Default { get; set; }
    public string? Description { get; set; }
    public bool Active { get; set; }
    public string? Icon { get; set; }
    public string? Color { get; set; }

    public ICollection<ConfigSetsEntity> ConfigSetsEntities { get; set; } = new List<ConfigSetsEntity>();
    public ICollection<SettingEntity> SettingEntities { get; set; } = new List<SettingEntity>();
}
