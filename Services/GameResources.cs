using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;

namespace PlumbobForge.Backend.Services;

// The game's own models and textures, read from The Sims 3's build packages (FullBuild, DeltaBuild) so the 3D preview
// can show CC that uses them: recolours of the game's paintings, posters and rugs carry only their own textures.
// Only the index is kept in memory; each resource is read from its file when asked for, so no game file stays open.
public sealed class GameResources
{
    private static readonly HashSet<uint> IndexedTypes = new()
    {
        0x02DC343Fu, // OBJK
        0x736884F1u, // VPXY
        0x01661233u, // MODL
        0x01D10F34u, // MLOD
        0x033A1435u, // TXTC
        0x00B2D882u, // texture
        0x0333406Cu, // preset XML
    };

    private static readonly object SharedLock = new();
    private static GameResources? _shared;

    private readonly string _gameDir;
    private readonly Lazy<Task<Index>> _index;

    private sealed record Location(string Path, long Offset, int ChunkLength, int ResourceLength, bool Compressed);

    private sealed class Index
    {
        public readonly Dictionary<TGI_Key, Location> ByKey = new();
        // Older materials often name a texture with the wrong group, so textures are found by type and instance too.
        public readonly Dictionary<(uint Type, ulong Instance), TGI_Key> ByInstance = new();
    }

    private GameResources(string gameDir)
    {
        _gameDir = gameDir;
        _index = new Lazy<Task<Index>>(() => Task.Run(Build), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    // One per game folder for the whole session; the index takes a few seconds to build.
    public static GameResources? Shared(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
        {
            try { gameDir = GamePathValidator.AutodetectGameFilesPath(); } catch { gameDir = null; }
        }
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return null;
        lock (SharedLock)
        {
            if (_shared == null || !string.Equals(_shared._gameDir, gameDir, StringComparison.OrdinalIgnoreCase)) _shared = new GameResources(gameDir);
            return _shared;
        }
    }

    public string GameDir => _gameDir;

    // Builds the index if needed. False when no build package could be read.
    public async Task<bool> LoadAsync()
    {
        try { return (await _index.Value).ByKey.Count > 0; }
        catch (Exception ex)
        {
            AppLogger.LogWarning("Could not read the game's packages", ex, "GameResources");
            return false;
        }
    }

    public bool IsLoaded => _index.IsValueCreated && _index.Value.IsCompletedSuccessfully && _index.Value.Result.ByKey.Count > 0;

    // Null until LoadAsync has finished.
    public InspectedResource? Find(TGI_Key key)
    {
        if (!IsLoaded) return null;
        return _index.Value.Result.ByKey.TryGetValue(key, out var at) ? ToResource(key, at) : null;
    }

    public InspectedResource? FindByInstance(uint type, ulong instance)
    {
        if (!IsLoaded) return null;
        var index = _index.Value.Result;
        return index.ByInstance.TryGetValue((type, instance), out var key) && index.ByKey.TryGetValue(key, out var at) ? ToResource(key, at) : null;
    }

    // Read like a vault chunk: straight from the file at its offset. PartIndex -1 marks it as the game's, never edited.
    private static InspectedResource ToResource(TGI_Key key, Location at) => new()
    {
        Key = key,
        PartIndex = GamePart,
        ChunkLength = at.ChunkLength,
        ResourceLength = at.ResourceLength,
        IsCompressed = at.Compressed,
        Chunk = new VaultChunkEntity
        {
            Type = key.Type, Group = key.Group, Instance = key.Instance,
            Offset = at.Offset, ChunkLength = at.ChunkLength, ResourceLength = at.ResourceLength, IsCompressed = at.Compressed
        },
        ShardPath = at.Path,
    };

    public const int GamePart = -1;

    private Index Build()
    {
        var index = new Index();
        var started = DateTime.UtcNow;
        foreach (string path in BuildPackages(_gameDir))
        {
            try
            {
                using var package = new DBPFPackage(path);
                foreach (var entry in package.Resources)
                {
                    if (!IndexedTypes.Contains(entry.Key.Type) || entry.IsEncrypted) continue;
                    // Later packages (expansions, then the delta patches) replace earlier ones.
                    index.ByKey[entry.Key] = new Location(path, entry.ChunkOffset, entry.ChunkLength, (int)entry.Length, entry.IsCompressed);
                    index.ByInstance[(entry.Key.Type, entry.Key.Instance)] = entry.Key;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"Could not read game package {path}", ex, "GameResources");
            }
        }
        AppLogger.LogInfo($"Indexed {index.ByKey.Count} game resources in {(DateTime.UtcNow - started).TotalSeconds:0.0}s", "GameResources");
        return index;
    }

    // Base game first, then expansions and stuff packs, then the patches (DeltaBuild), each replacing what came before.
    internal static List<string> BuildPackages(string gameDir)
    {
        var roots = new List<string> { gameDir };
        // Disc and Origin installs keep each pack in its own "The Sims 3 ..." folder next to the base game.
        var parent = Directory.GetParent(gameDir);
        if (parent != null)
        {
            try
            {
                roots.AddRange(Directory.GetDirectories(parent.FullName, "The Sims 3*")
                    .Where(d => !string.Equals(Path.GetFullPath(d).TrimEnd('\\'), Path.GetFullPath(gameDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)));
            }
            catch { }
        }

        var full = new List<string>();
        var delta = new List<string>();
        foreach (string root in roots)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*Build*.package", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            foreach (string f in files)
            {
                string name = Path.GetFileName(f);
                if (name.StartsWith("FullBuild", StringComparison.OrdinalIgnoreCase)) full.Add(f);
                else if (name.StartsWith("DeltaBuild", StringComparison.OrdinalIgnoreCase)) delta.Add(f);
            }
        }

        // The base game's own packages sort first ("GameData" before "EP1"...), the rest by folder and name.
        static string Order(string gameDir, string f)
        {
            string rel = Path.GetRelativePath(gameDir, f);
            return (rel.StartsWith("GameData", StringComparison.OrdinalIgnoreCase) ? "0" : "1") + rel;
        }
        return full.OrderBy(f => Order(gameDir, f), StringComparer.OrdinalIgnoreCase)
            .Concat(delta.OrderBy(f => Order(gameDir, f), StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
