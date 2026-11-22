using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PlumbobForge.Backend.Database;

// A real folder inside the game's Mods folder that holds files Resource.cfg loads (or files it would load once
// re-enabled). RelativePath is relative to Mods with forward slashes, e.g. "Packages/Hair". Icon, colour and
// description are the user's own customisation, like a set's.
public class ModsFolderEntity
{
    public long Id { get; set; }
    [Required] public string RelativePath { get; set; } = string.Empty;
    [Required] public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? Color { get; set; }
    public bool IsExpanded { get; set; } = true;

    public long? ParentModsFolderId { get; set; }
    public ModsFolderEntity? ParentModsFolder { get; set; }
    public ICollection<ModsFolderEntity> Children { get; set; } = new List<ModsFolderEntity>();
    public ICollection<MetaEntity> MetaEntities { get; set; } = new List<MetaEntity>();
}
