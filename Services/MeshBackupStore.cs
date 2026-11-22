using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using PlumbobForge.Backend.Configuration;
using S3ForgeTools.GameFiles.Package;

namespace PlumbobForge.Backend.Services;

// Originals of meshes the CC optimizer changed, so it can be undone. A backup is only valid while the resource still holds what was written.
public static class MeshBackupStore
{
    private sealed record Entry(int Part, uint Type, uint Group, ulong Instance, string File, string WrittenSha);

    public sealed record Change(int Part, TGI_Key Key, byte[] Current, byte[] Written);

    private const string IndexName = "index.json";
    private static readonly object _lock = new();

    private static string Root => Path.Combine(AppPaths.AppDataDir, "OptimizerBackups");
    private static string Folder(long itemId) => Path.Combine(Root, itemId.ToString());

    // Items with a backup, and when they were last optimized (the index is rewritten on every save).
    public static Dictionary<long, DateTime> OptimizedItems()
    {
        var result = new Dictionary<long, DateTime>();
        lock (_lock)
        {
            if (!Directory.Exists(Root)) return result;
            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                if (!long.TryParse(Path.GetFileName(dir), out long id)) continue;
                string index = Path.Combine(dir, IndexName);
                try
                {
                    if (File.Exists(index) && Load(id).Count > 0) result[id] = File.GetLastWriteTime(index);
                }
                catch (Exception)
                {
                }
            }
        }
        return result;
    }

    public static bool HasEntries(long itemId)
    {
        lock (_lock) return Load(itemId).Count > 0;
    }

    public static Dictionary<InspectedResource, byte[]> ValidOriginals(long itemId, PackageInspection inspection)
    {
        var result = new Dictionary<InspectedResource, byte[]>();
        lock (_lock)
        {
            var entries = Load(itemId);
            if (entries.Count == 0) return result;
            var keep = new List<Entry>();
            foreach (var e in entries)
            {
                var key = new TGI_Key(e.Type, e.Group, e.Instance);
                var matches = inspection.Resources.Where(r => r.PartIndex == e.Part && r.Key == key).Take(2).ToList();
                string path = Path.Combine(Folder(itemId), e.File);
                try
                {
                    if (matches.Count == 1 && File.Exists(path) && Sha(inspection.ReadStored(matches[0])) == e.WrittenSha)
                    {
                        result[matches[0]] = File.ReadAllBytes(path);
                        keep.Add(e);
                        continue;
                    }
                }
                catch (Exception)
                {
                }
                TryDelete(path);
            }
            if (keep.Count != entries.Count) Save(itemId, keep);
        }
        return result;
    }

    // Call after saving.
    public static void Record(long itemId, IEnumerable<Change> changes)
    {
        lock (_lock)
        {
            var entries = Load(itemId);
            string folder = Folder(itemId);
            Directory.CreateDirectory(folder);
            foreach (var c in changes)
            {
                string written = Sha(c.Written);
                int at = entries.FindIndex(e => e.Part == c.Part && e.Type == c.Key.Type && e.Group == c.Key.Group && e.Instance == c.Key.Instance);
                // Changed since, so the backup is stale.
                if (at >= 0 && entries[at].WrittenSha != Sha(c.Current))
                {
                    TryDelete(Path.Combine(folder, entries[at].File));
                    entries.RemoveAt(at);
                    at = -1;
                }

                if (at >= 0)
                {
                    string path = Path.Combine(folder, entries[at].File);
                    bool backToOriginal = File.Exists(path) && Sha(File.ReadAllBytes(path)) == written;
                    if (backToOriginal)
                    {
                        TryDelete(path);
                        entries.RemoveAt(at);
                    }
                    else
                    {
                        entries[at] = entries[at] with { WrittenSha = written };
                    }
                }
                else if (!c.Current.AsSpan().SequenceEqual(c.Written))
                {
                    string file = $"{c.Part}_{c.Key.Type:X8}_{c.Key.Group:X8}_{c.Key.Instance:X16}.bin";
                    File.WriteAllBytes(Path.Combine(folder, file), c.Current);
                    entries.Add(new Entry(c.Part, c.Key.Type, c.Key.Group, c.Key.Instance, file, written));
                }
            }
            Save(itemId, entries);
        }
    }

    public static void Prune(IReadOnlySet<long> existingItems)
    {
        string root = Root;
        lock (_lock)
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (!long.TryParse(Path.GetFileName(dir), out long id) || existingItems.Contains(id)) continue;
                try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
            }
        }
    }

    private static List<Entry> Load(long itemId)
    {
        string path = Path.Combine(Folder(itemId), IndexName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    private static void Save(long itemId, List<Entry> entries)
    {
        string folder = Folder(itemId);
        if (entries.Count == 0)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch (Exception) { }
            return;
        }
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, IndexName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(entries));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }
}
