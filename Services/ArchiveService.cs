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
        var names = new List<string>();
        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
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
        }
        catch { }
        return names;
    }

    public static List<string> GetArchivePackageFileNames(Stream stream)
    {
        var names = new List<string>();
        try
        {
            using var archive = ArchiveFactory.OpenArchive(stream);
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
        }
        catch { }
        return names;
    }

    public List<string> GetDuplicateFiles(IEnumerable<string> fileNames)
    {
        var duplicates = new List<string>();
        if (!Directory.Exists(_options.ManagedPackageFolderPath)) return duplicates;

        var existingInDb = _db.MetaEntities.Select(m => m.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in fileNames)
        {
            string fileName = Path.GetFileName(name);
            if (string.IsNullOrEmpty(fileName)) continue;

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

                string destPath = Path.Combine(destinationFolder, fileName);
                if (File.Exists(destPath))
                {
                    if (string.Equals(duplicateAction, "skip", StringComparison.OrdinalIgnoreCase))
                    {
                        onProgress?.Invoke($"Skipped duplicate file in archive: {fileName}");
                        continue;
                    }
                    else if (string.Equals(duplicateAction, "replace", StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(destPath); } catch { }
                        onMarkDirty?.Invoke(fileName);
                    }
                    else
                    {
                        destPath = Path.Combine(destinationFolder, Guid.NewGuid().ToString().Substring(0, 8) + "_" + fileName);
                    }
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
