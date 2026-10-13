using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlumbobForge.Backend.Services;

public static class Sims3PathResolver
{
    /// <summary>
    /// Known localized folder names used by Electronic Arts for The Sims 3 user data across all languages.
    /// </summary>
    private static readonly string[] KnownLocalizedFolderNames = new[]
    {
        "The Sims 3",
        "De Sims 3",      // Dutch
        "Les Sims 3",     // French
        "Die Sims 3",     // German
        "Los Sims 3",     // Spanish
        "Os Sims 3",      // Portuguese
        "ザ・シムズ３",    // Japanese (full-width '３' - official EA name)
        "ザ・シムズ3",     // Japanese (standard '3')
        "심즈 3",         // Korean
        "模拟人生3",      // Simplified Chinese
        "模擬市民3",      // Traditional Chinese
        "เดอะซิมส์ 3"     // Thai
    };

    /// <summary>
    /// Resolves the user's The Sims 3 folder inside Documents/Electronic Arts.
    /// Uses a prioritized 4-tier detection algorithm supporting English, European, and Asian localizations.
    /// </summary>
    public static string ResolveSims3UserFolder(string? userOverride = null)
    {
        // Tier 1: Explicit user-configured override
        if (!string.IsNullOrWhiteSpace(userOverride) && Directory.Exists(userOverride))
        {
            try { return Path.GetFullPath(userOverride.Trim()); } catch { return userOverride.Trim(); }
        }

        return AutodetectSims3UserFolder();
    }

    /// <summary>
    /// Automatically detects The Sims 3 user folder from the system registry, known localized folder names,
    /// and structural folder fingerprints.
    /// </summary>
    public static string AutodetectSims3UserFolder()
    {
        // Tier 2: Check Windows Registry Locale mapping via InstallationInfo
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string regBaseDir = S3ForgeTools.Utils.Registry.InstallationInfo.Instance.DocumentBaseDir;
                if (!string.IsNullOrWhiteSpace(regBaseDir) && Directory.Exists(regBaseDir))
                {
                    return Path.GetFullPath(regBaseDir);
                }
            }
        }
        catch { }

        // Tier 3 & 4: Search all potential Electronic Arts directories (standard Documents & OneDrive Documents)
        var eaRoots = GetPotentialElectronicArtsDirectories();
        foreach (var eaDir in eaRoots)
        {
            if (!Directory.Exists(eaDir)) continue;

            // Tier 3: Check known official localized directory names
            foreach (var localizedName in KnownLocalizedFolderNames)
            {
                string candidate = Path.Combine(eaDir, localizedName);
                if (Directory.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            // Tier 4: Fingerprint subdirectories for Sims 3 specific markers
            try
            {
                var subDirs = Directory.GetDirectories(eaDir);
                foreach (var dir in subDirs)
                {
                    if (IsSims3UserDirectory(dir))
                    {
                        return Path.GetFullPath(dir);
                    }
                }
            }
            catch { }
        }

        // Tier 5: Default fallback
        string myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(myDocs, "Electronic Arts", "The Sims 3");
    }

    /// <summary>
    /// Verifies if a given directory exhibits the unmistakable signature / fingerprint of a The Sims 3 user folder.
    /// </summary>
    public static bool IsSims3UserDirectory(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return false;

        try
        {
            // High-confidence Sims 3 unique files
            if (File.Exists(Path.Combine(folderPath, "DeviceConfig.log"))) return true;
            if (File.Exists(Path.Combine(folderPath, "Version.tag"))) return true;
            if (File.Exists(Path.Combine(folderPath, "simCompositorCache.package"))) return true;
            if (File.Exists(Path.Combine(folderPath, "CASPartCache.package"))) return true;
            if (File.Exists(Path.Combine(folderPath, "compositorCache.package"))) return true;

            // Structural signature: Options.ini with Sims 3 specific subfolders
            if (File.Exists(Path.Combine(folderPath, "Options.ini")))
            {
                if (Directory.Exists(Path.Combine(folderPath, "InstalledWorlds")) ||
                    Directory.Exists(Path.Combine(folderPath, "CurrentGame.sims3")) ||
                    Directory.Exists(Path.Combine(folderPath, "SavedSims")) ||
                    Directory.Exists(Path.Combine(folderPath, "DCCache")))
                {
                    return true;
                }
            }

            // Fallback: folder name contains "Sims 3"
            string dirName = Path.GetFileName(folderPath);
            if (dirName.Contains("Sims 3", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch { }

        return false;
    }

    private static IEnumerable<string> GetPotentialElectronicArtsDirectories()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Standard Documents
        string myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(myDocs))
        {
            roots.Add(Path.Combine(myDocs, "Electronic Arts"));
        }

        string personal = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        if (!string.IsNullOrWhiteSpace(personal))
        {
            roots.Add(Path.Combine(personal, "Electronic Arts"));
        }

        // OneDrive Documents (common redirection on Windows 10/11)
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            roots.Add(Path.Combine(userProfile, "OneDrive", "Documents", "Electronic Arts"));
            roots.Add(Path.Combine(userProfile, "OneDrive", "Dokumenty", "Electronic Arts"));
        }

        return roots;
    }
}
