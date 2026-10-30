using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using S3ForgeTools.GameFiles.Package;

namespace PlumbobForge.Backend.Services;

public static class GameThumbnailCache
{
    private const uint CasThumbMedium = Sims3ResourceTypes.ThumbCas + 1;
    private const uint CasThumbLarge = Sims3ResourceTypes.ThumbCas + 2;

    private static readonly object _lock = new();
    private static string? _loadedPath;
    private static DateTime _loadedWriteTime;
    private static long _loadedLength;
    private static Dictionary<ulong, TGI_Key> _best = new();

    public static string GetCasThumbnailsPath(string sims3Dir) => Path.Combine(sims3Dir, "Thumbnails", "CASThumbnails.package");

    public static byte[]? TryReadCasThumbnail(string sims3Dir, IEnumerable<ulong> casPartInstances)
    {
        string path = GetCasThumbnailsPath(sims3Dir);
        lock (_lock)
        {
            try
            {
                if (!EnsureIndex(path) || _best.Count == 0) return null;

                foreach (var instance in casPartInstances)
                {
                    if (!_best.TryGetValue(instance, out var key)) continue;

                    using var package = OpenShared(path);
                    var entry = package.Resources.FirstOrDefault(r => r.Key == key);
                    if (entry != null) return entry.Read();
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"Could not read the game's CAS thumbnail cache {path}", ex, "GameThumbnailCache");
                _loadedPath = null;
            }
            return null;
        }
    }

    private static bool EnsureIndex(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            _loadedPath = null;
            _best = new();
            return false;
        }

        if (_loadedPath == path && _loadedWriteTime == info.LastWriteTimeUtc && _loadedLength == info.Length) return true;

        var best = new Dictionary<ulong, TGI_Key>();
        using (var package = OpenShared(path))
        {
            foreach (var r in package.Resources)
            {
                var key = r.Key;
                if (key.Type != CasThumbLarge && key.Type != CasThumbMedium) continue;
                if (!best.TryGetValue(key.Instance, out var current) || IsBetter(key, current)) best[key.Instance] = key;
            }
        }

        _best = best;
        _loadedPath = path;
        _loadedWriteTime = info.LastWriteTimeUtc;
        _loadedLength = info.Length;
        return true;
    }

    private static bool IsBetter(TGI_Key candidate, TGI_Key current)
    {
        if (candidate.Type != current.Type) return candidate.Type == CasThumbLarge;
        return candidate.Group < current.Group;
    }

    private static DBPFPackage OpenShared(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536);
        var package = new DBPFPackage();
        try
        {
            package.Import(stream, UseBuffer: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        return package;
    }
}
