using System;
using System.IO;
using System.Security.Cryptography;
using S3ForgeTools.GameFiles.Textures;

namespace PlumbobForge.Backend.Services;

public sealed class TextureOptimizationCache
{
    private static readonly TimeSpan UnusedEntryLifetime = TimeSpan.FromDays(60);

    private readonly string _dir;
    private readonly int _maxDimension;

    public int Profile { get; }

    public TextureOptimizationCache(string appDataDir, int maxDimension)
    {
        _maxDimension = maxDimension;
        Profile = TextureOptimizer.GetProfile(maxDimension);
        _dir = Path.Combine(appDataDir, "TextureCache", $"p{Profile}");
        Directory.CreateDirectory(_dir);
    }

    public OptimizedTexture? GetOrCreate(byte[] rawChunk, Func<byte[]> decompress)
    {
        string key = Convert.ToHexString(SHA256.HashData(rawChunk), 0, 16);
        string sub = Path.Combine(_dir, key.Substring(0, 2));
        string texPath = Path.Combine(sub, key + ".tex");
        string skipPath = Path.Combine(sub, key + ".skip");

        try
        {
            if (File.Exists(texPath))
            {
                byte[] entry = File.ReadAllBytes(texPath);
                if (entry.Length > 5)
                {
                    Touch(texPath);
                    return new OptimizedTexture(entry[5..], BitConverter.ToInt32(entry, 0), entry[4] != 0);
                }
            }
            if (File.Exists(skipPath))
            {
                Touch(skipPath);
                return null;
            }
        }
        catch
        {
            // Unreadable entry: recompute below.
        }

        byte[]? dds = TextureOptimizer.TryOptimize(decompress(), _maxDimension);
        OptimizedTexture? optimized = dds != null ? TextureOptimizer.Pack(dds) : null;

        try
        {
            Directory.CreateDirectory(sub);
            string target = optimized != null ? texPath : skipPath;
            string temp = $"{target}.{Environment.CurrentManagedThreadId}.{Guid.NewGuid():N}.tmp";
            byte[] content = Array.Empty<byte>();
            if (optimized != null)
            {
                content = new byte[5 + optimized.Chunk.Length];
                BitConverter.TryWriteBytes(content.AsSpan(0, 4), optimized.ResourceLength);
                content[4] = optimized.IsCompressed ? (byte)1 : (byte)0;
                optimized.Chunk.CopyTo(content, 5);
            }
            File.WriteAllBytes(temp, content);
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            // A failed cache write only costs a re-encode next time.
        }

        return optimized;
    }

    private static void Touch(string path)
    {
        try
        {
            // Throttle: only bump entries that look stale, to avoid a metadata write per texture per rebuild.
            var last = File.GetLastWriteTimeUtc(path);
            if (DateTime.UtcNow - last > TimeSpan.FromDays(7)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch { }
    }

    public static void Prune(string appDataDir, int currentProfile)
    {
        string root = Path.Combine(appDataDir, "TextureCache");
        if (!Directory.Exists(root)) return;

        try
        {
            foreach (var profileDir in Directory.GetDirectories(root))
            {
                if (!string.Equals(Path.GetFileName(profileDir), $"p{currentProfile}", StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(profileDir, true); } catch { }
                    continue;
                }

                var cutoff = DateTime.UtcNow - UnusedEntryLifetime;
                foreach (var file in Directory.EnumerateFiles(profileDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || File.GetLastWriteTimeUtc(file) < cutoff)
                        {
                            File.Delete(file);
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
    }
}
