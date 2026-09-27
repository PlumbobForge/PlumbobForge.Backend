using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace PlumbobForge.Backend.Services;

public static class GamePathValidator
{
    private static readonly string[] KnownRegistryPaths = new[]
    {
        @"SOFTWARE\WOW6432Node\Sims\The Sims 3",
        @"SOFTWARE\WOW6432Node\Sims(Steam)\The Sims 3",
        @"SOFTWARE\Sims\The Sims 3",
        @"SOFTWARE\Sims(Steam)\The Sims 3",
        @"SOFTWARE\Electronic Arts\The Sims 3",
        @"SOFTWARE\Electronic Arts\EA Core\Staging"
    };

    public static (bool Valid, string NormalizedPath) ValidateAndNormalize(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            return (false, string.Empty);

        string targetDir = inputPath.Trim();

        try
        {
            if (File.Exists(targetDir))
            {
                string? parent = Path.GetDirectoryName(targetDir);
                if (!string.IsNullOrEmpty(parent)) targetDir = parent;
            }

            if (!Directory.Exists(targetDir))
                return (false, string.Empty);

            targetDir = Path.GetFullPath(targetDir);

            var dirInfo = new DirectoryInfo(targetDir);
            if (dirInfo.Name.Equals("Bin", StringComparison.OrdinalIgnoreCase) &&
                dirInfo.Parent != null && dirInfo.Parent.Name.Equals("Game", StringComparison.OrdinalIgnoreCase))
            {
                if (dirInfo.Parent.Parent != null) targetDir = dirInfo.Parent.Parent.FullName;
            }
            else if (dirInfo.Name.Equals("Bin", StringComparison.OrdinalIgnoreCase) && dirInfo.Parent != null)
            {
                targetDir = dirInfo.Parent.FullName;
            }

            if (Directory.Exists(targetDir))
            {
                return (true, targetDir);
            }
        }
        catch { }

        return (false, string.Empty);
    }

    public static bool IsValidSims3Directory(string dirPath)
    {
        if (string.IsNullOrWhiteSpace(dirPath)) return false;
        try
        {
            return Directory.Exists(dirPath);
        }
        catch
        {
            return false;
        }
    }

    public static string AutodetectGameFilesPath()
    {
        if (OperatingSystem.IsWindows())
        {
            RegistryHive[] hives = new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser };
            RegistryView[] views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

            foreach (var hive in hives)
            {
                foreach (var view in views)
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        foreach (var regPath in KnownRegistryPaths)
                        {
                            try
                            {
                                using var key = baseKey.OpenSubKey(regPath);
                                if (key != null)
                                {
                                    var exePath = key.GetValue("exepath") as string
                                               ?? key.GetValue("Install Dir") as string
                                               ?? key.GetValue("Path") as string;

                                    if (!string.IsNullOrWhiteSpace(exePath))
                                    {
                                        var (valid, normalized) = ValidateAndNormalize(exePath);
                                        if (valid) return normalized;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
        }

        // Fallback for common default installation folders
        string[] defaultPaths = new[]
        {
            @"C:\Program Files\EA Games\The Sims 3",
            @"C:\Program Files (x86)\Electronic Arts\The Sims 3",
            @"C:\Program Files (x86)\Steam\steamapps\common\The Sims 3",
            @"C:\Program Files\Steam\steamapps\common\The Sims 3",
            @"C:\Games\The Sims 3",
            @"D:\Games\The Sims 3",
            @"E:\Games\The Sims 3"
        };

        foreach (var path in defaultPaths)
        {
            var (valid, normalized) = ValidateAndNormalize(path);
            if (valid) return normalized;
        }

        return string.Empty;
    }
}
