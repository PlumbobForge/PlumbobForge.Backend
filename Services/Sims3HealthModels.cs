using System;
using System.Collections.Generic;

namespace PlumbobForge.Backend.Services;

public enum ConflictCardCategory
{
    Incompatibility, // Mod tuning, XML, ITUN, Scripts, Buffs
    Duplicate,       // Exact identical copies of package files
    DefaultOverride, // CAS Parts, Object Definitions, Sliders
    CorruptFile      // Sims 2 format or corrupt package
}

public static class Sims3ResourceTags
{
    public static readonly Dictionary<uint, (string Tag, string Name, bool IsTuning, bool IsOverride)> TagMap = new()
    {
        [0x03B33DDF] = ("ITUN", "Interaction Tuning", true, false),
        [0x0333406C] = ("_XML", "XML Tuning / Data", true, false),
        [0x073FAA07] = ("S3SA", "C# Script Assembly", true, false),
        [0x02D5DF13] = ("JAZZ", "Jazz State Machine", true, false),
        [0x025C95B6] = ("LAYO", "Layout / Buff", true, false),
        [0xDD3223A7] = ("BUFF", "Buff Tuning", true, false),
        [0x1F886EAD] = ("_INI", "Configuration INI", true, false),

        [0x034AEECB] = ("CASP", "CAS Part", false, true),
        [0x319E4F1D] = ("OBJD", "Object Definition", false, true),
        [0x319E3F52] = ("OBJD", "Object Definition", false, true),
        [0x02DC343F] = ("OBJK", "Object Catalog", false, true),
        [0x736884F8] = ("FTPT", "Object Footprint", false, true),
        [0xD382BF57] = ("FTPT", "Object Footprint", false, true),
        [0x01661233] = ("MODL", "3D Model", false, true),
        [0x01D10F34] = ("MLOD", "Model LOD", false, true),
        [0x015A1849] = ("GEOM", "Mesh Geometry", false, true),
        [0x00B2D882] = ("SKIN", "Skin Controller", false, true),
        [0x0354796A] = ("TONE", "Skin Tone Slider", false, true),
        [0x03555BA8] = ("TONE", "Skin Tone Slider", false, true),
        [0x0355E0A6] = ("BOND", "Bone Delta Slider", false, true),
        [0x0358B08A] = ("FACE", "Facial Modifier Slider", false, true),
        [0x067CAA11] = ("BGEO", "Blend Geometry", false, true),
        [0x6B20C4F3] = ("CLIP", "Animation Clip", false, true),
        [0x63A33EA7] = ("ANIM", "Animation", false, true),
        [0x220557DA] = ("STBL", "String Table", false, false),
        [0x0044AE27] = ("_IMG", "Texture / Image", false, false),
        [0x00AE6C67] = ("BONE", "Skeleton Rig", false, false),
        [0x8EAF13DE] = ("_RIG", "Rig Definition", false, false),
        [0x01A527DB] = ("_AUD", "Audio Clip", false, false),
        [0x02019972] = ("MTST", "Audio Material Preset", false, false),
        [0xD3044521] = ("MATD", "Material Definition", false, false),
        [0x044AE110] = ("COMP", "Composite Texture", false, false)
    };

    public static string GetTag(uint type)
    {
        return TagMap.TryGetValue(type, out var info) ? info.Tag : $"0x{type:X8}";
    }

    public static string GetFriendlyName(uint type)
    {
        return TagMap.TryGetValue(type, out var info) ? info.Name : $"Resource 0x{type:X8}";
    }

    public static bool IsModTuning(uint type)
    {
        return TagMap.TryGetValue(type, out var info) && info.IsTuning;
    }

    public static bool IsOverride(uint type)
    {
        return TagMap.TryGetValue(type, out var info) && info.IsOverride;
    }

    public static bool IsBenignRepeatingResource(uint type)
    {
        return type switch
        {
            0x0166038C or // NMAP (Namemap)
            0x73E93EEB or // Sims3Pack manifest XML
            0xE86B1EEF or // Directory record
            0x00000000 => true,
            _ => false
        };
    }
}

public class CacheGroupInfo
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string WhenToClear { get; set; } = string.Empty;
    public string WhyClear { get; set; } = string.Empty;
    public string WhyNotClear { get; set; } = string.Empty;
    public string RegenerationImpact { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public bool IsRecommended { get; set; }
    public bool IsSelected { get; set; }
    public string? SafetyNotice { get; set; }
    public List<string> FilePaths { get; set; } = new();

    public string FormattedSize => FormatBytes(TotalSizeBytes);

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int digitGroups = (int)(Math.Log(bytes) / Math.Log(1024));
        digitGroups = Math.Min(digitGroups, units.Length - 1);
        double val = bytes / Math.Pow(1024, digitGroups);
        return $"{val:0.##} {units[digitGroups]}";
    }
}

public class CacheCleanResult
{
    public bool Success { get; set; }
    public int FilesDeleted { get; set; }
    public long BytesReclaimed { get; set; }
    public List<string> Errors { get; set; } = new();

    public string FormattedBytesReclaimed => CacheGroupInfo.FormatBytes(BytesReclaimed);
}

public class PackageConflictItem
{
    public string PackageFileName { get; set; } = string.Empty;
    public string PackagePath { get; set; } = string.Empty;
    public string SetName { get; set; } = "Uncategorized";
    public long? SetId { get; set; }
    public long? MetaEntityId { get; set; }
    public string? PackageType { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsWinningInLoadOrder { get; set; }
}

public class ConflictCardModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ConflictFingerprint { get; set; } = string.Empty;
    public bool IsIgnored { get; set; }
    public ConflictCardCategory Category { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;

    public List<PackageConflictItem> Packages { get; set; } = new();

    public PackageConflictItem PrimaryPackage
    {
        get => Packages.Count > 0 ? Packages[0] : (_primaryPackageFallback ??= new());
        set
        {
            _primaryPackageFallback = value;
            if (Packages.Count == 0) Packages.Add(value);
            else Packages[0] = value;
        }
    }
    private PackageConflictItem? _primaryPackageFallback;

    public PackageConflictItem? SecondaryPackage
    {
        get => Packages.Count > 1 ? Packages[1] : null;
        set
        {
            if (value != null)
            {
                if (Packages.Count < 2)
                {
                    if (Packages.Count == 0) Packages.Add(new PackageConflictItem());
                    Packages.Add(value);
                }
                else
                {
                    Packages[1] = value;
                }
            }
        }
    }

    public int AffectedResourceCount { get; set; }
    public string AffectedSummary { get; set; } = string.Empty;
    public bool IsIntraSet { get; set; }

    public List<string> TechnicalDetails { get; set; } = new();
}

public class HealthScanSummary
{
    public int TotalPackagesScanned { get; set; }
    public int IncompatibilitiesCount { get; set; }
    public int DuplicatesCount { get; set; }
    public int OverridesCount { get; set; }
    public int CorruptCount { get; set; }
    public int IgnoredCount { get; set; }
    public int TotalIssuesCount => IncompatibilitiesCount + DuplicatesCount + OverridesCount + CorruptCount;

    public List<ConflictCardModel> ConflictCards { get; set; } = new();
}

public class PatternIssueItem
{
    public string PackagePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string SetName { get; set; } = "Uncategorized";
    public long? SetId { get; set; }
    public long? MetaEntityId { get; set; }
    public string PatternName { get; set; } = "Unknown Pattern";
    public string Category { get; set; } = "General";
    public int CorruptedKeyCount { get; set; }
    public List<string> SampleBrokenKeys { get; set; } = new();
    public bool IsBroken { get; set; }
    public bool IsFixed { get; set; }
    public bool IsSelected { get; set; }
}

public class PatternScanSummary
{
    public int TotalPackagesScanned { get; set; }
    public int TotalPatternsFound { get; set; }
    public int BrokenPatternsCount { get; set; }
    public int FixedPatternsCount { get; set; }
    public List<PatternIssueItem> BrokenPatterns { get; set; } = new();
    public List<PatternIssueItem> CleanPatterns { get; set; } = new();
}

public class PatternFixResult
{
    public bool Success { get; set; }
    public int FixedCount { get; set; }
    public int FailedCount { get; set; }
    public List<string> Errors { get; set; } = new();
}
