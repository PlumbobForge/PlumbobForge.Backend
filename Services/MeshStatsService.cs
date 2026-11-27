using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using S3ForgeTools.GameFiles.Package;

namespace PlumbobForge.Backend.Services;

/// <summary>Works out triangle counts for every CAS part and object of an inspected package. Nothing is written.</summary>
public static class MeshStatsService
{
    private const uint Geom = 0x015A1849u;
    private const uint Vpxy = 0x736884F1u;

    /// <summary>Fills <see cref="InspectedObject.Mesh"/> for each object. Reading meshes is slow, so call it off the UI thread.</summary>
    // originals: backups, used to fill OriginalMesh.
    public static void Analyse(PackageInspection inspection, CancellationToken ct = default, IReadOnlyDictionary<InspectedResource, byte[]>? originals = null)
    {
        var byKey = new Dictionary<(int Part, TGI_Key Key), InspectedResource>();
        var anyKey = new Dictionary<TGI_Key, InspectedResource>();
        foreach (var r in inspection.Resources)
        {
            byKey.TryAdd((r.PartIndex, r.Key), r);
            anyKey.TryAdd(r.Key, r);
        }

        var cache = new Dictionary<TGI_Key, MeshLod?>();
        var orphansByPart = new Dictionary<int, List<TGI_Key>>();
        var caspCountByPart = inspection.Objects
            .Where(o => o.Kind == InspectedObjectKind.Cas)
            .GroupBy(o => o.Resource.PartIndex)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var obj in inspection.Objects)
        {
            ct.ThrowIfCancellationRequested();
            if (obj.CouldNotRead) continue;

            int part = obj.Resource.PartIndex;
            MeshStatsReader.ResourceReader read = key =>
            {
                if (!byKey.TryGetValue((part, key), out var resource) && !anyKey.TryGetValue(key, out resource)) return null;
                try { return inspection.ReadStored(resource); }
                catch { return null; }
            };

            MeshStats Measure(MeshStatsReader.ResourceReader reader, Dictionary<TGI_Key, MeshLod?> meshCache)
            {
                var data = inspection.Read(obj.Resource);
                if (obj.Kind == InspectedObjectKind.Cas)
                {
                    if (!orphansByPart.TryGetValue(part, out var orphans))
                    {
                        orphans = FindOrphans(inspection, part, read);
                        orphansByPart[part] = orphans;
                    }
                    return MeshStatsReader.ReadCas(data, reader, orphans, caspCountByPart.GetValueOrDefault(part) == 1, meshCache);
                }
                if (MeshStatsReader.HasObjectModel(obj.Resource.Key.Type))
                {
                    return MeshStatsReader.ReadObject(obj.Resource.Key.Type, data, reader, meshCache);
                }
                return MeshStats.None;
            }

            try
            {
                obj.Mesh = Measure(read, cache);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                obj.Mesh = MeshStats.None;
            }

            if (originals is not { Count: > 0 } || obj.Mesh is not { Lods.Count: > 0 } now) continue;
            var used = new HashSet<(int Part, TGI_Key Key)>();
            MeshStatsReader.ResourceReader readOriginal = key =>
            {
                if (!byKey.TryGetValue((part, key), out var resource) && !anyKey.TryGetValue(key, out resource)) return null;
                if (originals.TryGetValue(resource, out var original))
                {
                    used.Add((resource.PartIndex, resource.Key));
                    return original;
                }
                try { return inspection.ReadStored(resource); }
                catch { return null; }
            };
            try
            {
                var before = Measure(readOriginal, new Dictionary<TGI_Key, MeshLod?>());
                if (used.Count > 0 && before.Lods.Count > 0 && !before.Lods.SequenceEqual(now.Lods))
                {
                    obj.OriginalMesh = before;
                    obj.OptimizedKeys = used.ToList();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
        }
    }

    private static List<TGI_Key> FindOrphans(PackageInspection inspection, int part, MeshStatsReader.ResourceReader read)
    {
        var inPart = inspection.Resources.Where(r => r.PartIndex == part).ToList();
        var geoms = inPart.Where(r => r.Key.Type == Geom).Select(r => r.Key).ToList();
        if (geoms.Count == 0) return new List<TGI_Key>();

        var vpxys = new List<byte[]>();
        foreach (var r in inPart.Where(r => r.Key.Type == Vpxy))
        {
            var data = read(r.Key);
            if (data != null) vpxys.Add(data);
        }
        return MeshStatsReader.FindOrphanGeoms(geoms, vpxys);
    }
}
