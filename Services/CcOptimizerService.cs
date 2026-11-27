using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;

namespace PlumbobForge.Backend.Services;

// One GEOM mesh and its level of detail.
public sealed class CcMesh
{
    public const int Unlisted = -1;

    public required InspectedResource Resource { get; init; }
    public required GeomMesh Geom { get; init; }
    // 0 = most detailed; Unlisted when no VPXY lists it.
    public int Lod { get; init; }
    public int Slot { get; init; }
    // Duplicate keys can't be saved safely, so they are never changed.
    public bool HasDuplicateKey { get; init; }
    // Objects: index of the mesh in its MLOD resource.
    public int? ObjectMeshIndex { get; init; }
    // Objects: the diffuse texture, when it is in the package.
    public InspectedResource? Diffuse { get; init; }
    // Objects: the texture compositor (TXTC) that paints the mesh, when it is in the package.
    public InspectedResource? Compositor { get; init; }
    // Triangle count saved in the package, when working from a backup.
    public int? StoredTriangles { get; internal set; }

    public TGI_Key Key => Resource.Key;
    public int Triangles => Geom.Triangles;
    public int TrianglesNow => StoredTriangles ?? Triangles;
    public int Vertices => Geom.VertexCount;
    // Tiny dummy meshes left by converters; never reduced.
    public bool IsPlaceholder => Triangles <= 2;
    // Objects: a shadow plane or tub water, which the game draws only as a helper.
    public bool IsHelper { get; init; }
    // What the 3D preview draws. An object's flat parts are real (a rug, a painting's canvas, a glass pane); a CAS
    // part's tiny meshes are converter leftovers.
    public bool IsShownInPreview => ObjectMeshIndex != null ? !IsHelper : !IsPlaceholder;
    public bool CanReduce => !IsPlaceholder && !HasDuplicateKey && !IsGenerated && !IsFromGame;
    // Read from the game's own packages for the preview; never changed.
    public bool IsFromGame => Resource.PartIndex == GameResources.GamePart;
    // Made up for the preview (a terrain paint's flat tile); not in the package.
    public bool IsGenerated { get; init; }

    private CcMeshSimplifier.Layers? _layers;
    private (double Ratio, bool Lock, CcSimplifyResult Result)? _last;

    public CcMeshSimplifier.Layers Layers => _layers ??= CcMeshSimplifier.AnalyzeLayers(Geom);
    public bool IsDoubleSided => Layers.DoubleSided;

    // The last result is cached for the live preview.
    public CcSimplifyResult Reduce(double ratio, bool lockEdges)
    {
        if (!CanReduce || ratio >= 0.999) return new CcSimplifyResult(Geom, Triangles, Vertices, _layers?.DoubleSided ?? false);
        if (_last is { } l && l.Ratio == ratio && l.Lock == lockEdges) return l.Result;
        var r = CcMeshSimplifier.Simplify(Geom, ratio, lockEdges, Layers);
        _last = (ratio, lockEdges, r);
        return r;
    }
}

public sealed class CcAnalysis
{
    public required IReadOnlyList<CcMesh> Meshes { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool IsObject { get; init; }
    // The CAS part whose presets colour the preview.
    public InspectedResource? Casp { get; init; }
    // A wall or floor pattern or a terrain paint: its colour variants colour the whole tile, like a CAS part's presets.
    public InspectedResource? Pattern { get; init; }
    // Objects are painted mesh by mesh, from their own materials; everything else gets one texture from a preset.
    public bool UsesObjectTextures => IsObject && Pattern == null;

    public List<CasPreset> ReadPresets(PackageInspection inspection)
    {
        if (Pattern != null)
        {
            try { return CcMaterial.PatternPresets(inspection, Pattern); }
            catch (Exception) { return new List<CasPreset>(); }
        }
        if (IsObject)
        {
            if (!Meshes.Any(m => m.Compositor != null)) return new List<CasPreset>();
            try { return CcMaterial.ObjectPresets(inspection, Meshes.Select(m => m.Key.Instance).ToHashSet()); }
            catch (Exception) { return new List<CasPreset>(); }
        }
        if (Casp == null) return new List<CasPreset>();
        var own = TryReadPresets(inspection, Casp);
        if (own.Count > 0) return own;

        // Parts with no presets borrow them from a part with the same meshes, or (hair) from another hair.
        HashSet<TGI_Key> mine;
        try { mine = MeshStatsReader.ReadCaspTable(inspection.Read(Casp)).Where(IsMeshKey).ToHashSet(); }
        catch (Exception) { return own; }
        var others = inspection.Resources.Where(r => r.Key.Type == Casp.Key.Type && r != Casp).ToList();
        foreach (var other in others)
        {
            try
            {
                if (!MeshStatsReader.ReadCaspTable(inspection.Read(other)).Any(k => IsMeshKey(k) && mine.Contains(k))) continue;
            }
            catch (Exception)
            {
                continue;
            }
            var borrowed = TryReadPresets(inspection, other);
            if (borrowed.Count > 0) return borrowed;
        }

        var hairMesh = Lods.Count == 0 ? null : MeshesIn(Lods[0]).Select(m => m.Geom).FirstOrDefault(g => g.IsHairShader);
        if (hairMesh == null || !hairMesh.MaterialTextures().TryGetValue(DiffuseMapParam, out var diffuse)) return own;
        if (!inspection.Resources.Any(r => r.Key.Type == CcMaterial.TextureType && r.Key.Instance == diffuse.Instance)) return own;
        foreach (var other in others)
        {
            var hair = TryReadPresets(inspection, other).Where(p => p.IsHair).ToList();
            if (hair.Count == 0) continue;
            string value = $"key:{CcMaterial.TextureType:X8}:{diffuse.Group:X8}:{diffuse.Instance:X16}";
            return hair.Select(p => p with { Values = new Dictionary<string, string>(p.Values) { ["Diffuse Map"] = value } }).ToList();
        }
        return own;
    }

    private const uint DiffuseMapParam = 0x6CC0FD85u;

    private static bool IsMeshKey(TGI_Key k) => k.Type == 0x736884F1u || k.Type == GeomMesh.ResourceType;

    private static List<CasPreset> TryReadPresets(PackageInspection inspection, InspectedResource casp)
    {
        try { return CasPreset.ReadAll(inspection.Read(casp)); }
        catch (Exception) { return new List<CasPreset>(); }
    }

    public IEnumerable<GeomMesh> TopMeshes => Lods.Count == 0 ? Enumerable.Empty<GeomMesh>() : MeshesIn(Lods[0]).Select(m => m.Geom);

    public IReadOnlyList<int> Lods => Meshes.Select(m => m.Lod).Distinct().ToList();

    // Every level with a real mesh; converters often leave a lower one heavier than the level above.
    public List<int> EditableLods
    {
        get
        {
            var all = Lods;
            var listed = all.Where(l => l != CcMesh.Unlisted).ToList();
            var pool = listed.Count > 0 ? listed : all;
            return pool.Where((l, i) => i < 2 || MeshesIn(l).Any(m => m.CanReduce && !m.IsPlaceholder)).ToList();
        }
    }

    public IEnumerable<CcMesh> MeshesIn(int lod) => Meshes.Where(m => m.Lod == lod);
    public int TrianglesIn(int lod) => MeshesIn(lod).Sum(m => m.Triangles);
    public int RealTrianglesIn(int lod) => MeshesIn(lod).Where(m => !m.IsPlaceholder).Sum(m => m.Triangles);
    public int RealTrianglesNowIn(int lod) => MeshesIn(lod).Where(m => !m.IsPlaceholder).Sum(m => m.TrianglesNow);
    public IEnumerable<CcMesh> PreviewMeshesIn(int lod) => MeshesIn(lod).Where(m => m.IsShownInPreview);
    public int PreviewTrianglesIn(int lod) => PreviewMeshesIn(lod).Sum(m => m.Triangles);

    // Built from backed-up originals.
    public bool IsFromOriginals => Meshes.Any(m => m.StoredTriangles != null);

    // The ratio the package is at now, for the slider.
    public double StoredRatioFor(int lod)
    {
        int original = RealTrianglesIn(lod);
        if (original <= 0) return 1;
        double r = Math.Round((double)RealTrianglesNowIn(lod) / original, 2);
        return r >= 0.995 ? 1 : Math.Clamp(r, 0.05, 1);
    }

    // Clothing and objects keep their open edges by default.
    public bool SuggestLockEdges => IsObject || Meshes.Any(m => m.Geom.HasVertexIds);

    // Converters often copy the top level into the lower ones.
    public bool SuggestLinkSecondLod
    {
        get
        {
            var lods = EditableLods;
            return lods.Skip(1).Select((l, i) => (Own: RealTrianglesIn(l), Above: RealTrianglesIn(lods[i])))
                .Any(p => p.Own >= p.Above * 0.7);
        }
    }
}

public sealed class CcOptimizePlan
{
    // Fraction of triangles to keep per level (missing = 1).
    public Dictionary<int, double> Ratios { get; } = new();
    // Each lower level becomes half of the one above.
    public bool LinkSecondLod { get; set; }
    public bool LockEdges { get; set; } = true;

    public static CcOptimizePlan SuggestedFor(CcAnalysis analysis) => new()
    {
        LockEdges = analysis.SuggestLockEdges,
        LinkSecondLod = analysis.SuggestLinkSecondLod,
    };

    public bool IsLinked(CcAnalysis analysis, int lod)
    {
        return LinkSecondLod && analysis.EditableLods.IndexOf(lod) >= 1;
    }

    public double RatioFor(CcAnalysis analysis, int lod)
    {
        if (IsLinked(analysis, lod))
        {
            var lods = analysis.EditableLods;
            int above = lods[lods.IndexOf(lod) - 1];
            double target = analysis.MeshesIn(above).Where(m => !m.IsPlaceholder)
                .Sum(m => m.Reduce(RatioFor(analysis, above), LockEdges).Triangles) / 2.0;
            int own = analysis.RealTrianglesIn(lod);
            return own > 0 ? Math.Clamp(Math.Floor(target / own * 100 + 0.5) / 100, 0.05, 1) : 1;
        }
        return Ratios.TryGetValue(lod, out var r) ? r : 1;
    }

    public int TrianglesAfter(CcAnalysis analysis, int lod)
    {
        double ratio = RatioFor(analysis, lod);
        return analysis.MeshesIn(lod).Sum(m => m.Reduce(ratio, LockEdges).Triangles);
    }
}

public sealed record CcMeshChange(TGI_Key Key, int Lod, int Before, int After);

public sealed record CcOptimizeResult(IReadOnlyList<CcMeshChange> Changes)
{
    public int TrianglesBefore => Changes.Sum(c => c.Before);
    public int TrianglesAfter => Changes.Sum(c => c.After);
}

// Lowers the triangle count of CC meshes.
public class CcOptimizerService
{
    private const uint Vpxy = 0x736884F1u;
    private const uint Casp = 0x034AEECBu;
    private const uint Objd = 0x319E4F1Du;
    private const uint Txtc = 0x033A1435u;
    private const uint Mlod = 0x01D10F34u;

    // All objects of a package at once (colour variants share meshes).
    public static readonly TGI_Key AllObjects = new(Objd, uint.MaxValue, ulong.MaxValue);

    private readonly AppDbContext _db;
    private readonly PackageInspectorService _inspector;
    private readonly PackageEditService _editor;
    private readonly CacheBuilderService _cacheBuilder;

    public CcOptimizerService(AppDbContext db, PackageInspectorService inspector, PackageEditService editor, CacheBuilderService cacheBuilder)
    {
        _db = db;
        _inspector = inspector;
        _editor = editor;
        _cacheBuilder = cacheBuilder;
    }

    // casPart: one CAS part or OBJD; null for the whole package.
    // game: the game's own packages, for objects whose model is the game's (preview only; the optimizer leaves it out).
    public static CcAnalysis Analyse(PackageInspection inspection, TGI_Key? casPart = null, CancellationToken ct = default, GameResources? game = null)
    {
        if (game != null) inspection.Game = game;
        var analysis = AnalyseCore(inspection, casPart, ct, game);
        if (inspection.Originals.Count > 0) MeasureStored(inspection, analysis);
        return analysis;
    }

    private static void MeasureStored(PackageInspection inspection, CcAnalysis analysis)
    {
        foreach (var group in analysis.Meshes.Where(m => inspection.Originals.ContainsKey(m.Resource)).GroupBy(m => m.Resource))
        {
            try
            {
                var stored = inspection.ReadStored(group.Key);
                if (group.First().ObjectMeshIndex == null)
                {
                    int triangles = GeomMesh.Read(stored).Triangles;
                    foreach (var m in group) m.StoredTriangles = triangles;
                }
                else
                {
                    var model = MlodModel.Read(stored);
                    foreach (var m in group)
                    {
                        if (m.ObjectMeshIndex is int i && i < model.Meshes.Count) m.StoredTriangles = model.RealTriangles(i);
                    }
                }
            }
            catch (Exception)
            {
            }
        }
    }

    private static CcAnalysis AnalyseCore(PackageInspection inspection, TGI_Key? casPart, CancellationToken ct, GameResources? game)
    {
        if (casPart is { Type: Sims3ResourceTypes.CatalogTerrainPaintBrush } paintKey
            && inspection.Resources.FirstOrDefault(r => r.Key == paintKey) is { } paint)
        {
            return AnalyseTerrainPaint(paint);
        }

        if ((casPart is { } part && MeshStatsReader.HasObjectModel(part.Type)) || (casPart == null
            && !inspection.Resources.Any(r => r.Key.Type == GeomMesh.ResourceType)
            && inspection.Resources.Any(r => MeshStatsReader.HasObjectModel(r.Key.Type))))
        {
            return AnalyseObject(inspection, casPart == AllObjects ? null : casPart, ct, game);
        }

        var warnings = new List<string>();

        var casp = casPart is { } wantedPart
            ? inspection.Resources.FirstOrDefault(r => r.Key == wantedPart)
            : inspection.Resources.FirstOrDefault(r => r.Key.Type == Casp);

        // With a CAS part given, only its own meshes count.
        HashSet<TGI_Key>? ownVpxy = null;
        HashSet<ulong>? ownGeoms = null;
        HashSet<uint>? ownGroups = null;
        if (casPart != null && casp != null)
        {
            try
            {
                var table = MeshStatsReader.ReadCaspTable(inspection.Read(casp));
                ownVpxy = table.Where(k => k.Type == Vpxy).ToHashSet();
                ownGeoms = table.Where(k => k.Type == GeomMesh.ResourceType).Select(k => k.Instance).ToHashSet();
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not read the CAS part {casp.Key}: {ex.Message}");
            }
        }
        var vpxyResources = inspection.Resources.Where(r => r.Key.Type == Vpxy).ToList();
        if (ownVpxy != null && vpxyResources.Any(r => ownVpxy.Contains(r.Key)))
        {
            vpxyResources = vpxyResources.Where(r => ownVpxy.Contains(r.Key)).ToList();
        }
        else if (ownVpxy is { Count: > 0 } && ownGeoms != null)
        {
            // Replacement meshes: matched by group = VPXY instance.
            var groups = ownVpxy.Select(k => (uint)k.Instance).ToHashSet();
            if (inspection.Resources.Any(r => r.Key.Type == GeomMesh.ResourceType && groups.Contains(r.Key.Group)))
            {
                ownGroups = groups;
                ownGeoms = null;
                vpxyResources = [];
            }
            else
            {
                ownGeoms = null;
            }
        }
        else
        {
            ownGeoms = null;
        }

        var lodOf = new Dictionary<(int Part, ulong Instance), (int Lod, int Slot)>();
        foreach (var res in vpxyResources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var (lod, keys) in ReadVpxy(inspection.Read(res)))
                {
                    for (int slot = 0; slot < keys.Count; slot++)
                    {
                        if (keys[slot].Type != GeomMesh.ResourceType) continue;
                        lodOf[(res.PartIndex, keys[slot].Instance)] = (lod, slot);
                        ownGeoms?.Add(keys[slot].Instance);
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not read the LOD table {res.Key}: {ex.Message}");
            }
        }

        var keyCounts = inspection.Resources
            .GroupBy(r => (r.PartIndex, r.Key))
            .ToDictionary(g => g.Key, g => g.Count());

        var meshes = new List<CcMesh>();
        foreach (var res in inspection.Resources.Where(r => r.Key.Type == GeomMesh.ResourceType))
        {
            ct.ThrowIfCancellationRequested();
            if (ownGeoms != null && !ownGeoms.Contains(res.Key.Instance)) continue;
            if (ownGroups != null && !ownGroups.Contains(res.Key.Group)) continue;
            try
            {
                var geom = GeomMesh.Read(inspection.Read(res));
                var where = lodOf.TryGetValue((res.PartIndex, res.Key.Instance), out var w) ? w : (Lod: CcMesh.Unlisted, Slot: 0);
                meshes.Add(new CcMesh
                {
                    Resource = res,
                    Geom = geom,
                    Lod = where.Lod,
                    Slot = where.Slot,
                    HasDuplicateKey = keyCounts[(res.PartIndex, res.Key)] > 1,
                });
            }
            catch (Exception ex)
            {
                warnings.Add($"Mesh {res.Key} could not be read: {ex.Message}");
            }
        }

        // No VPXY to give levels: rank by size, like Mesh Report.
        if (ownGroups != null && meshes.Count > 1 && meshes.All(m => m.Lod == CcMesh.Unlisted))
        {
            meshes = meshes
                .OrderByDescending(m => m.Triangles)
                .Select((m, i) => new CcMesh { Resource = m.Resource, Geom = m.Geom, Lod = i, Slot = 0, HasDuplicateKey = m.HasDuplicateKey })
                .ToList();
        }

        var ordered = meshes
            .OrderBy(m => m.Lod == CcMesh.Unlisted ? 99 : m.Lod)
            .ThenBy(m => m.Slot)
            .ToList();
        return new CcAnalysis { Meshes = ordered, Warnings = warnings, Casp = casp };
    }

    // Objects: OBJD -> OBJK -> MODL -> MLOD. Shadows are skipped.
    private static CcAnalysis AnalyseObject(PackageInspection inspection, TGI_Key? objdKey, CancellationToken ct, GameResources? game)
    {
        var warnings = new List<string>();
        // Objects, and wall and floor patterns (their sample tile).
        var objds = inspection.Resources.Where(r => MeshStatsReader.HasObjectModel(r.Key.Type) && (objdKey == null || r.Key == objdKey)).ToList();

        InspectedResource? InPackage(TGI_Key key, int part) =>
            inspection.Resources.FirstOrDefault(r => r.Key == key && r.PartIndex == part) ?? inspection.Resources.FirstOrDefault(r => r.Key == key);
        // What the package doesn't hold comes from the game's own packages, when they are given.
        InspectedResource? Find(TGI_Key key, int part) => InPackage(key, part) ?? game?.Find(key);

        // Older materials often have the wrong group, so match by type and instance too.
        InspectedResource? FindTexture(TGI_Key? key, int part, uint type = CcMaterial.TextureType)
        {
            if (key is not { } k || k.Type != type) return null;
            return InPackage(k, part)
                ?? inspection.Resources.FirstOrDefault(r => r.Key.Type == k.Type && r.Key.Instance == k.Instance && r.PartIndex == part)
                ?? inspection.Resources.FirstOrDefault(r => r.Key.Type == k.Type && r.Key.Instance == k.Instance)
                ?? game?.Find(k)
                ?? game?.FindByInstance(k.Type, k.Instance);
        }

        // -1 = the whole MLOD resource.
        var levelOf = new Dictionary<(InspectedResource Owner, int Chunk), int>();
        var owners = new List<InspectedResource>();
        void Add(InspectedResource owner, int chunk, int level)
        {
            if (!owners.Contains(owner)) owners.Add(owner);
            levelOf.TryAdd((owner, chunk), level);
        }

        var seenModl = new HashSet<InspectedResource>();
        bool external = false;
        int unreadable = 0;
        string? firstError = null;
        foreach (var objd in objds)
        {
            ct.ThrowIfCancellationRequested();
            List<TGI_Key> modlKeys;
            try
            {
                modlKeys = MeshStatsReader.ObjectModelKeys(objd.Key.Type, inspection.Read(objd), k => Find(k, objd.PartIndex) is { } r ? inspection.Read(r) : null, out bool ext);
                external |= ext;
            }
            catch (Exception ex)
            {
                unreadable++;
                firstError ??= $"{objd.Key}: {ex.Message}";
                continue;
            }

            foreach (var modlKey in modlKeys.Distinct())
            {
                var modl = Find(modlKey, objd.PartIndex);
                if (modl == null) { external = true; continue; }
                if (!seenModl.Add(modl)) continue;
                try
                {
                    var rcol = RcolFile.Read(inspection.Read(modl));
                    int chunk = Enumerable.Range(0, rcol.Chunks.Count).FirstOrDefault(i => rcol.TagOf(i) == "MODL", -1);
                    if (chunk < 0) throw new InvalidDataException("No MODL chunk");
                    foreach (var lod in ModlChunk.ReadLods(rcol.Chunks[chunk]))
                    {
                        if (lod.IsShadow) continue;
                        if (rcol.ExternalKey(lod.ModelRef) is { } mlodKey)
                        {
                            if (mlodKey.Type != Mlod) continue;
                            var mlod = Find(mlodKey, modl.PartIndex);
                            if (mlod == null) { external = true; continue; }
                            Add(mlod, -1, lod.Level);
                        }
                        else if (rcol.ChunkIndex(lod.ModelRef) is var inner and >= 0 && rcol.TagOf(inner) == "MLOD")
                        {
                            Add(modl, inner, lod.Level);
                        }
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not read the model {modl.Key}: {ex.Message}");
                }
            }
        }
        if (unreadable > 0) warnings.Add(unreadable == 1 ? $"Could not read the object {firstError}" : $"Could not read {unreadable} objects ({firstError})");
        if (external) warnings.Add("Some of this object's meshes are not in the package (game meshes); only the ones inside it are listed.");

        var keyCounts = inspection.Resources.GroupBy(r => (r.PartIndex, r.Key)).ToDictionary(g => g.Key, g => g.Count());
        var meshes = new List<CcMesh>();
        foreach (var owner in owners)
        {
            ct.ThrowIfCancellationRequested();
            MlodModel model;
            try { model = MlodModel.Read(inspection.Read(owner)); }
            catch (Exception ex) { warnings.Add($"Mesh {owner.Key} could not be read: {ex.Message}"); continue; }

            foreach (var chunk in model.Mlods.Keys.OrderBy(k => k))
            {
                if (!levelOf.TryGetValue((owner, -1), out int level) && !levelOf.TryGetValue((owner, chunk), out level)) continue;
                int slot = 0;
                foreach (int i in model.MeshesOf(chunk))
                {
                    if (model.Meshes[i].VrtfRef == 0) continue;
                    if (model.WhyNotEditable(i) is { } why)
                    {
                        warnings.Add($"A mesh in {owner.Key} is left as it is: {why}.");
                        continue;
                    }
                    try
                    {
                        var g = model.ReadGeometry(i);
                        var diffuseKey = model.DiffuseKey(i);
                        meshes.Add(new CcMesh
                        {
                            Resource = owner,
                            Geom = GeomMesh.FromArrays(g.Positions, g.Normals, g.Uvs, g.Indices),
                            Lod = level,
                            Slot = slot++,
                            HasDuplicateKey = keyCounts.GetValueOrDefault((owner.PartIndex, owner.Key)) > 1,
                            ObjectMeshIndex = i,
                            IsHelper = model.IsHelper(i),
                            Diffuse = FindTexture(diffuseKey, owner.PartIndex),
                            Compositor = FindTexture(diffuseKey, owner.PartIndex, Txtc),
                        });
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"A mesh in {owner.Key} could not be read: {ex.Message}");
                    }
                }
            }
        }

        var ordered = meshes.OrderBy(m => m.Lod).ThenBy(m => m.Slot).ToList();
        // A wall or floor pattern's tile is coloured by the pattern, not by the tile's own material.
        var pattern = objds.Count > 0 && objds.All(o => o.Key.Type == Sims3ResourceTypes.CatalogWallFloorPattern) ? objds[0] : null;
        return new CcAnalysis { Meshes = ordered, Warnings = warnings, IsObject = true, Pattern = pattern };
    }

    // A terrain paint has no model: it is shown on a flat patch of ground, one metre across.
    private static CcAnalysis AnalyseTerrainPaint(InspectedResource paint)
    {
        const int cells = 8;
        var positions = new List<float>();
        var normals = new List<float>();
        var uvs = new List<float>();
        for (int z = 0; z <= cells; z++)
        {
            for (int x = 0; x <= cells; x++)
            {
                float u = x / (float)cells, v = z / (float)cells;
                positions.AddRange(new[] { u - 0.5f, 0f, v - 0.5f });
                normals.AddRange(new[] { 0f, 1f, 0f });
                uvs.AddRange(new[] { u, v });
            }
        }
        var indices = new List<uint>();
        for (int z = 0; z < cells; z++)
        {
            for (int x = 0; x < cells; x++)
            {
                uint a = (uint)(z * (cells + 1) + x), b = a + 1, c = a + cells + 1, d = c + 1;
                indices.AddRange(new[] { a, c, b, b, c, d });
            }
        }
        var tile = new CcMesh
        {
            Resource = paint,
            Geom = GeomMesh.FromArrays(positions.ToArray(), normals.ToArray(), uvs.ToArray(), indices.ToArray()),
            Lod = 0,
            IsGenerated = true,
        };
        return new CcAnalysis { Meshes = new[] { tile }, IsObject = true, Pattern = paint };
    }

    // From backups, reductions start from the original and 100% restores it.
    public static List<PartEdits> BuildEdits(PackageInspection inspection, CcAnalysis analysis, CcOptimizePlan plan, out CcOptimizeResult result)
    {
        var changes = new List<CcMeshChange>();
        var byPart = new Dictionary<int, List<ResourceEdit>>();
        var written = new HashSet<InspectedResource>();
        // Objects: each resource is written once.
        var objectEdits = new Dictionary<InspectedResource, Dictionary<int, ObjectMeshEdit>>();
        foreach (var lod in analysis.Lods)
        {
            double ratio = plan.RatioFor(analysis, lod);
            if (ratio >= 0.999) continue;
            foreach (var m in analysis.MeshesIn(lod))
            {
                if (!m.CanReduce) continue;
                var r = m.Reduce(ratio, plan.LockEdges);
                if (r.Triangles >= m.Triangles) continue;
                if (m.ObjectMeshIndex is int index)
                {
                    if (r.Keep == null) continue;
                    if (!objectEdits.TryGetValue(m.Resource, out var edits)) objectEdits[m.Resource] = edits = new();
                    edits[index] = new ObjectMeshEdit(r.Keep, r.Geom.Indices);
                }
                else
                {
                    EditsFor(m.Resource.PartIndex).Add(new ResourceEdit(m.Key, r.Geom.Write()));
                    written.Add(m.Resource);
                }
                changes.Add(new CcMeshChange(m.Key, lod, m.Triangles, r.Triangles));
            }
        }
        foreach (var (res, edits) in objectEdits)
        {
            var model = MlodModel.Read(inspection.Read(res));
            EditsFor(res.PartIndex).Add(new ResourceEdit(res.Key, model.Write(edits)));
            written.Add(res);
        }
        foreach (var res in analysis.Meshes.Select(m => m.Resource).Distinct())
        {
            if (!written.Contains(res) && inspection.Originals.TryGetValue(res, out var original))
            {
                EditsFor(res.PartIndex).Add(new ResourceEdit(res.Key, original));
            }
        }

        List<ResourceEdit> EditsFor(int part)
        {
            if (!byPart.TryGetValue(part, out var list)) byPart[part] = list = new List<ResourceEdit>();
            return list;
        }
        result = new CcOptimizeResult(changes);
        return Group(inspection, byPart);
    }

    private static List<PartEdits> Group(PackageInspection inspection, Dictionary<int, List<ResourceEdit>> byPart) => byPart
        .Where(p => p.Value.Count > 0)
        .Select(p => new PartEdits(p.Key, inspection.Parts.FirstOrDefault(x => x.Index == p.Key)?.MetaEntityId, p.Value))
        .ToList();

    // Skips edits that change nothing and collects backup info.
    public static List<PartEdits> DropUnchanged(PackageInspection inspection, IReadOnlyList<PartEdits> edits, List<MeshBackupStore.Change> changes)
    {
        var byPart = new Dictionary<int, List<ResourceEdit>>();
        foreach (var part in edits)
        {
            var kept = byPart[part.PartIndex] = new List<ResourceEdit>();
            foreach (var edit in part.Edits)
            {
                if (edit.NewData == null) { kept.Add(edit); continue; }
                var res = inspection.Resources.FirstOrDefault(r => r.PartIndex == part.PartIndex && r.Key == edit.Key);
                var stored = res != null ? inspection.ReadStored(res) : null;
                if (stored != null && stored.AsSpan().SequenceEqual(edit.NewData)) continue;
                kept.Add(edit);
                if (stored != null) changes.Add(new MeshBackupStore.Change(part.PartIndex, edit.Key, stored, edit.NewData));
            }
        }
        return Group(inspection, byPart);
    }

    // Reduces and saves the item, always starting from the original.
    public async Task<CcOptimizeResult> OptimizeAsync(long itemId, TGI_Key? casPart, Action<CcAnalysis, CcOptimizePlan> configure, CancellationToken ct = default)
    {
        List<PartEdits> edits;
        CcOptimizeResult result;
        var changes = new List<MeshBackupStore.Change>();
        using (var inspection = await _inspector.InspectAsync(itemId, ct))
        {
            (edits, result) = await Task.Run(() =>
            {
                inspection.UseOriginals(MeshBackupStore.ValidOriginals(itemId, inspection));
                var analysis = Analyse(inspection, casPart, ct);
                var plan = CcOptimizePlan.SuggestedFor(analysis);
                configure(analysis, plan);
                var e = BuildEdits(inspection, analysis, plan, out var r);
                return (DropUnchanged(inspection, e, changes), r);
            }, ct);
        }
        await SaveAsync(itemId, edits);
        MeshBackupStore.Record(itemId, changes);
        return result;
    }

    public static List<PartEdits> BuildRestore(long itemId, PackageInspection inspection, IReadOnlyCollection<(int Part, TGI_Key Key)>? which, List<MeshBackupStore.Change> changes)
    {
        var byPart = new Dictionary<int, List<ResourceEdit>>();
        foreach (var (res, original) in MeshBackupStore.ValidOriginals(itemId, inspection))
        {
            if (which != null && !which.Contains((res.PartIndex, res.Key))) continue;
            if (!byPart.TryGetValue(res.PartIndex, out var list)) byPart[res.PartIndex] = list = new List<ResourceEdit>();
            list.Add(new ResourceEdit(res.Key, original));
        }
        return DropUnchanged(inspection, Group(inspection, byPart), changes);
    }

    // Restores the originals (all when which is null). False when there was nothing to restore.
    public async Task<bool> RestoreAsync(long itemId, IReadOnlyCollection<(int Part, TGI_Key Key)>? which = null, CancellationToken ct = default)
    {
        List<PartEdits> edits;
        var changes = new List<MeshBackupStore.Change>();
        using (var inspection = await _inspector.InspectAsync(itemId, ct))
        {
            edits = await Task.Run(() => BuildRestore(itemId, inspection, which, changes), ct);
        }
        if (edits.Count == 0) return false;
        await SaveAsync(itemId, edits);
        MeshBackupStore.Record(itemId, changes);
        return true;
    }

    public async Task SaveAsync(long itemId, IReadOnlyList<PartEdits> edits)
    {
        if (edits.Count == 0) return;
        await _editor.ApplyAsync(itemId, edits);
        if (await _db.AllMetaEntities.Where(m => m.Id == itemId).Select(m => m.IsVaultManaged).FirstOrDefaultAsync())
        {
            _cacheBuilder.OnGameContentChanged();
        }
    }

    // VPXY: entry type 0 is a level (level, key count, key indices), type 1 a single index.
    internal static List<(int Lod, List<TGI_Key> Keys)> ReadVpxy(byte[] rcol)
    {
        if (rcol.Length < 20) throw new InvalidDataException("VPXY is too short");
        uint intCount = BinaryPrimitives.ReadUInt32LittleEndian(rcol.AsSpan(16));
        uint extCount = BinaryPrimitives.ReadUInt32LittleEndian(rcol.AsSpan(12));
        if (intCount == 0 || intCount > 4096 || extCount > 4096) throw new InvalidDataException("VPXY has no chunk");
        int table = 20 + (int)(intCount + extCount) * 16;
        int start = (int)BinaryPrimitives.ReadUInt32LittleEndian(rcol.AsSpan(table));
        int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(rcol.AsSpan(table + 4));
        if (start < 0 || size < 16 || (long)start + size > rcol.Length) throw new InvalidDataException("VPXY chunk lies outside the resource");
        var c = rcol.AsSpan(start, size);
        if (c[0] != 'V' || c[1] != 'P' || c[2] != 'X' || c[3] != 'Y') throw new InvalidDataException("Missing VPXY tag");

        int p = 8;
        int keyBase = 12 + (int)BinaryPrimitives.ReadUInt32LittleEndian(c[p..]);
        p = 16;
        int count = c[p++];
        var entries = new List<(int Lod, List<int> Idx)>();
        for (int i = 0; i < count; i++)
        {
            int kind = c[p++];
            if (kind == 0)
            {
                int lod = c[p++], n = c[p++];
                var idx = new List<int>(n);
                for (int k = 0; k < n; k++, p += 4) idx.Add((int)BinaryPrimitives.ReadUInt32LittleEndian(c[p..]));
                entries.Add((lod, idx));
            }
            else if (kind == 1) p += 4;
            else throw new InvalidDataException($"Unknown VPXY entry type {kind}");
        }

        p = keyBase;
        int keyCount = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(c[p..]), 4096u);
        p += 4;
        var keys = new List<TGI_Key>(keyCount);
        for (int i = 0; i < keyCount; i++, p += 16)
        {
            keys.Add(new TGI_Key(
                BinaryPrimitives.ReadUInt32LittleEndian(c[p..]),
                BinaryPrimitives.ReadUInt32LittleEndian(c[(p + 4)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(c[(p + 8)..])));
        }
        return entries
            .Select(e => (e.Lod, e.Idx.Where(i => i >= 0 && i < keys.Count).Select(i => keys[i]).ToList()))
            .ToList();
    }
}
