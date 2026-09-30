using System.ComponentModel.DataAnnotations;

namespace PlumbobForge.Backend.Database;

public class TombstoneEntity
{
    public long Id { get; set; }
    [Required] public string FileName { get; set; } = string.Empty;
    public string PackageType { get; set; } = string.Empty;
    public string? CASCategories { get; set; }
    public string? CASAge { get; set; }
    public string? CASGender { get; set; }
    public string? CASOutfitCategory { get; set; }
    public bool IsUserTagged { get; set; } = false;
    public string? UserTags { get; set; }
    public string? Description { get; set; }
    public bool IsFavorite { get; set; } = false;
    public long? SetsEntityId { get; set; }
    public DateTime DeletedAt { get; set; } = DateTime.UtcNow;
}
