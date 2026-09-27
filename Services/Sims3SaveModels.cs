using System;
using System.Collections.Generic;

namespace PlumbobForge.Backend.Services;

public class Sims3SaveSummary
{
    public string SaveName { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public string WorldName { get; set; } = string.Empty;
    public string ActiveHouseholdName { get; set; } = string.Empty;
    public int HouseholdFunds { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime LastSavedDate { get; set; }
    public long TotalSizeBytes { get; set; }
    public string FormattedSize => FormatBytes(TotalSizeBytes);
    public int FileCount { get; set; }
    public bool IsBackup { get; set; }
    public byte[]? PreviewPortraitBytes { get; set; }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:F2} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:F2} MB";
        double gb = mb / 1024.0;
        return $"{gb:F2} GB";
    }
}

public class Sims3SaveDetail
{
    public Sims3SaveSummary Summary { get; set; } = new();
    public string GameVersion { get; set; } = string.Empty;
    public Sims3HouseholdInfo? ActiveHousehold { get; set; }
    public List<Sims3HouseholdInfo> Households { get; set; } = new();
    public List<Sims3SaveFileEntry> Files { get; set; } = new();
    public List<Sims3SaveImage> Images { get; set; } = new();
}

public enum Sims3HouseholdFilter
{
    All,
    WithSims,
    Empty
}

public class Sims3HouseholdInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Funds { get; set; }
    public string HomeLotName { get; set; } = string.Empty;
    public string HomeLotDescription { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int MemberCount => Members.Count;
    public byte[]? FamilyPortraitBytes { get; set; }
    public byte[]? LotThumbnailBytes { get; set; }
    public List<Sims3SimMember> Members { get; set; } = new();
}

public class Sims3SimMember
{
    public string FullName { get; set; } = string.Empty;
    public string Biography { get; set; } = string.Empty;
    public string CareerOrSchool { get; set; } = string.Empty;
    public byte[]? PortraitBytes { get; set; }
}

public enum Sims3SaveImageCategory
{
    All,
    FamilyPortrait,
    SimPortrait,
    LotThumbnail,
    Screenshot
}

public class Sims3SaveImage
{
    public string Id { get; set; } = string.Empty;
    public ulong Instance { get; set; }
    public uint Type { get; set; }
    public Sims3SaveImageCategory Category { get; set; }
    public string CategoryName => Category switch
    {
        Sims3SaveImageCategory.FamilyPortrait => "Family Portrait",
        Sims3SaveImageCategory.SimPortrait => "Sim Portrait",
        Sims3SaveImageCategory.LotThumbnail => "Lot Thumbnail",
        Sims3SaveImageCategory.Screenshot => "World Snapshot",
        _ => "Image"
    };
    public string Format { get; set; } = "PNG";
    public long SizeBytes { get; set; }
    public string FormattedSize => Sims3SaveSummary.FormatBytes(SizeBytes);
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string SourceFileName { get; set; } = string.Empty;
}

public class Sims3SaveFileEntry
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string FormattedSize => Sims3SaveSummary.FormatBytes(SizeBytes);
    public DateTime LastModified { get; set; }
}
