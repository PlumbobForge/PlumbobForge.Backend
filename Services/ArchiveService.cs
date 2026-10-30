using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PlumbobForge.Backend.Configuration;
using PlumbobForge.Backend.Database;
using Microsoft.Extensions.Options;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace PlumbobForge.Backend.Services;

public class ArchiveService
{
    private readonly AppDbContext _db;
    private readonly PlumbobForgeOptions _options;

    public ArchiveService(AppDbContext db, IOptionsSnapshot<PlumbobForgeOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public static bool IsArchiveExtension(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext == ".zip" || ext == ".rar" || ext == ".7z";
    }

    public static List<string> GetArchivePackageFileNames(string archivePath)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            return ExtractPackageNames(archive);
        }
        catch { }
        return new List<string>();
    }

    public static List<string> GetArchivePackageFileNames(Stream stream)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(stream);
            return ExtractPackageNames(archive);
        }
        catch { }
        return new List<string>();
    }

    private static List<string> ExtractPackageNames(IArchive archive)
    {
        var names = new List<string>();
        foreach (var entry in archive.Entries)
        {
            if (!entry.IsDirectory && !string.IsNullOrEmpty(entry.Key))
            {
                string ext = Path.GetExtension(entry.Key).ToLowerInvariant();
                if (ext == ".package" || ext == ".sims3pack")
                {
                    string? fn = Path.GetFileName(entry.Key);
                    if (!string.IsNullOrEmpty(fn)) names.Add(fn);
                }
            }
        }
        return names;
    }

    public static (string? ResolvedPath, bool ShouldSkip) ResolveDestinationPath(
        string destinationFolder,
        string fileName,
        string duplicateAction,
        Action<string>? onMarkDirty = null)
    {
        string destPath = Path.Combine(destinationFolder, fileName);
        if (!File.Exists(destPath))
        {
            return (destPath, false);
        }

        if (string.Equals(duplicateAction, "skip", StringComparison.OrdinalIgnoreCase))
        {
            return (null, true);
        }

        if (string.Equals(duplicateAction, "replace", StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(destPath); } catch { }
            onMarkDirty?.Invoke(fileName);
            return (destPath, false);
        }

        string uniqueName = $"{Guid.NewGuid().ToString("N")[..8]}_{fileName}";
        return (Path.Combine(destinationFolder, uniqueName), false);
    }

    public List<string> GetDuplicateFiles(IEnumerable<string> fileNames)
    {
        var duplicates = new List<string>();
        if (!Directory.Exists(_options.ManagedPackageFolderPath)) return duplicates;

        var nameList = fileNames
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (nameList.Count == 0) return duplicates;

        var existingInDb = _db.MetaEntities
            .Where(m => nameList.Contains(m.FileName))
            .Select(m => m.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var fileName in nameList)
        {
            string dest = Path.Combine(_options.ManagedPackageFolderPath, fileName);
            if (File.Exists(dest) || existingInDb.Contains(fileName))
            {
                duplicates.Add(fileName);
            }
        }

        return duplicates;
    }

    public List<string> ExtractPackagesFromArchive(string archivePath, string destinationFolder, string duplicateAction = "rename", Action<string>? onProgress = null, Action<string>? onMarkDirty = null)
    {
        var extractedPaths = new List<string>();
        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key)) continue;

                string ext = Path.GetExtension(entry.Key).ToLowerInvariant();
                if (ext != ".package" && ext != ".sims3pack") continue;

                string? fileName = Path.GetFileName(entry.Key);
                if (string.IsNullOrEmpty(fileName)) continue;

                var (destPath, shouldSkip) = ResolveDestinationPath(destinationFolder, fileName, duplicateAction, onMarkDirty);
                if (shouldSkip || destPath == null)
                {
                    onProgress?.Invoke($"Skipped duplicate file in archive: {fileName}");
                    continue;
                }

                entry.WriteToFile(destPath, new ExtractionOptions { ExtractFullPath = false, Overwrite = true });
                extractedPaths.Add(destPath);
                onProgress?.Invoke($"Extracted from archive: {fileName}");
            }
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"Failed to extract archive {Path.GetFileName(archivePath)}: {ex.Message}");
        }
        return extractedPaths;
    }
}
