using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using S3ForgeTools.GameFiles.Package;
using S3ForgeTools.GameFiles.Resources;

namespace PlumbobForge.Backend.Services;

/// <summary>Size of one level of detail of a mesh.</summary>
public sealed record MeshLod(int Triangles, int Vertices);

/// <summary>Triangle counts of one CAS part or build/buy object, most detailed level first.</summary>
public sealed record MeshStats(IReadOnlyList<MeshLod> Lods, bool HasExternalMesh)
{
    public static readonly MeshStats None = new(Array.Empty<MeshLod>(), false);

    public bool HasData => Lods.Count > 0;
    public int Triangles => Lods.Count == 0 ? 0 : Lods[0].Triangles;
    public int Vertices => Lods.Count == 0 ? 0 : Lods[0].Vertices;
}

/// <summary>
/// Reads triangle counts straight from package resources, following the links the game uses:
/// CAS part (CASP) -> VPXY -> GEOM, and object (OBJD) -> OBJK -> VPXY -> MODL -> MLOD.
/// Meshes that are not in the package (the game's own) are reported as external, never as zero.
/// </summary>
public static class MeshStatsReader
{
    public delegate byte[]? ResourceReader(TGI_Key key);

    private const uint Geom = 0x015A1849u;
    private const uint Modl = 0x01661233u;
    private const uint Mlod = 0x01D10F34u;
    private const uint Vpxy = 0x736884F1u;
    private const uint Objk = 0x02DC343Fu;

    private const int MaxEntries = 4096;

    /// <param name="orphanGeoms">Meshes in the same package that no VPXY in it lists. Packages that replace a base-game
    /// mesh carry those but point at a VPXY that only exists in the game files.</param>
    /// <param name="onlyCasp">True when the package holds a single CAS part, so any orphan mesh must be its own.</param>
    public static MeshStats ReadCas(byte[] casp, ResourceReader read, IReadOnlyList<TGI_Key>? orphanGeoms = null, bool onlyCasp = false, Dictionary<TGI_Key, MeshLod?>? cache = null)
    {
        try
        {
            var lods = new List<MeshLod>();
            bool external = false;
            var seen = new HashSet<TGI_Key>();

            var table = ReadCaspTable(casp);
            var geomKeys = new List<TGI_Key>();
            var missingVpxy = new List<TGI_Key>();
            foreach (var vpxyKey in table.Where(k => k.Type == Vpxy))
            {
                var vpxy = read(vpxyKey);
                if (vpxy == null) { missingVpxy.Add(vpxyKey); continue; }
                geomKeys.AddRange(ReadChunkTable(vpxy).Where(k => k.Type == Geom));
            }
            geomKeys.AddRange(table.Where(k => k.Type == Geom));

            if (missingVpxy.Count > 0)
            {
                // The replacement meshes use the VPXY's instance as their group.
                var matched = orphanGeoms?.Where(g => missingVpxy.Any(v => g.Group == (uint)v.Instance)).ToList();
                if ((matched == null || matched.Count == 0) && onlyCasp && orphanGeoms != null) matched = orphanGeoms.ToList();
                if (matched is { Count: > 0 }) geomKeys.AddRange(matched);
                else external = true;
            }

            foreach (var key in geomKeys)
            {
                if (!seen.Add(key)) continue;
                if (!TryReadCached(key, read, cache, TryReadGeom, out var lod, out bool missing)) { external |= missing; continue; }
                lods.Add(lod);
            }
            return Build(lods, external);
        }
        catch (Exception)
        {
            return MeshStats.None;
        }
    }

    /// <summary>Meshes in <paramref name="allGeoms"/> that none of the given VPXY resources lists.</summary>
    public static List<TGI_Key> FindOrphanGeoms(IEnumerable<TGI_Key> allGeoms, IEnumerable<byte[]> vpxyResources)
    {
        var listed = new HashSet<TGI_Key>();
        foreach (var vpxy in vpxyResources)
        {
            foreach (var key in ReadChunkTable(vpxy))
            {
                if (key.Type == Geom) listed.Add(key);
            }
        }
        return allGeoms.Where(g => !listed.Contains(g)).ToList();
    }
    // Catalog resources whose model is read like an object's: objects, and wall and floor patterns (their sample
    // wall or floor tile).
    public static bool HasObjectModel(uint type) => type == Sims3ResourceTypes.Objd || type == Sims3ResourceTypes.CatalogWallFloorPattern;

    // OBJD -> OBJK -> MODLs, or wall/floor pattern -> VPXY -> MODLs. external: something is missing from the package.
    internal static List<TGI_Key> ObjectModelKeys(byte[] objd, ResourceReader read, out bool external) =>
        ObjectModelKeys(Sims3ResourceTypes.Objd, objd, read, out external);

    internal static List<TGI_Key> ObjectModelKeys(uint type, byte[] objd, ResourceReader read, out bool external)
    {
        if (type == Sims3ResourceTypes.CatalogWallFloorPattern) return PatternModelKeys(objd, read, out external);

        var item = new ResourceOBJD(objd);
        var refs = item.ReferenceTable;
        var objkKeys = new List<TGI_Key>();
        if (item.ObjkIndex >= 0 && item.ObjkIndex < refs.Count && refs[item.ObjkIndex].Type == Objk)
        {
            objkKeys.Add(refs[item.ObjkIndex]);
        }
        else
        {
            objkKeys.AddRange(refs.Where(k => k.Type == Objk));
        }

        external = false;
        var modlKeys = new List<TGI_Key>();
        foreach (var objkKey in objkKeys)
        {
            var objk = read(objkKey);
            if (objk == null) { external = true; continue; }

            foreach (var key in ReadObjkTable(objk))
            {
                if (key.Type == Modl)
                {
                    modlKeys.Add(key);
                }
                else if (key.Type == Vpxy)
                {
                    var vpxy = read(key);
                    if (vpxy == null) { external = true; continue; }
                    modlKeys.AddRange(ReadChunkTable(vpxy).Where(k => k.Type == Modl));
                }
            }
        }
        return modlKeys;
    }

    // A wall or floor pattern lists its sample tile's VPXY among its keys.
    private static List<TGI_Key> PatternModelKeys(byte[] data, ResourceReader read, out bool external)
    {
        external = false;
        var pattern = new CatalogResource.WallFloorPatternCatalogResource(0, new System.IO.MemoryStream(data, writable: false));
        var keys = pattern.TGIBlocks.Select(t => new TGI_Key(t.ResourceType, t.ResourceGroup, t.Instance)).ToList();
        int at = (int)pattern.MaterialVPXYIndex;
        var vpxyKeys = at >= 0 && at < keys.Count && keys[at].Type == Vpxy ? new List<TGI_Key> { keys[at] } : keys.Where(k => k.Type == Vpxy).ToList();

        var modlKeys = new List<TGI_Key>();
        foreach (var key in vpxyKeys)
        {
            var vpxy = read(key);
            if (vpxy == null) { external = true; continue; }
            modlKeys.AddRange(ReadChunkTable(vpxy).Where(k => k.Type == Modl));
        }
        return modlKeys;
    }

    public static MeshStats ReadObject(byte[] objd, ResourceReader read, Dictionary<TGI_Key, MeshLod?>? cache = null) =>
        ReadObject(Sims3ResourceTypes.Objd, objd, read, cache);

    public static MeshStats ReadObject(uint type, byte[] objd, ResourceReader read, Dictionary<TGI_Key, MeshLod?>? cache = null)
    {
        try
        {
            var modlKeys = ObjectModelKeys(type, objd, read, out bool external);

            var levels = new SortedDictionary<int, MeshLod>();
            void AddLevel(int level, MeshLod lod) =>
                levels[level] = levels.TryGetValue(level, out var had) ? new MeshLod(had.Triangles + lod.Triangles, had.Vertices + lod.Vertices) : lod;

            foreach (var modlKey in modlKeys.Distinct())
            {
                var modl = read(modlKey);
                if (modl == null) { external = true; continue; }

                List<ModlLod> modlLods;
                RcolFile rcol;
                try
                {
                    rcol = RcolFile.Read(modl);
                    int at = Enumerable.Range(0, rcol.Chunks.Count).FirstOrDefault(i => rcol.TagOf(i) == "MODL", -1);
                    if (at < 0) continue;
                    modlLods = ModlChunk.ReadLods(rcol.Chunks[at]);
                }
                catch (Exception)
                {
                    continue;
                }

                // Shadow levels are skipped.
                var seen = new HashSet<(TGI_Key, int)>();
                foreach (var entry in modlLods)
                {
                    if (entry.IsShadow) continue;
                    if (rcol.ExternalKey(entry.ModelRef) is { } mlodKey)
                    {
                        if (mlodKey.Type != Mlod || !seen.Add((mlodKey, -1))) continue;
                        if (!TryReadCached(mlodKey, read, cache, TryReadMlod, out var lod, out bool missing)) { external |= missing; continue; }
                        AddLevel(entry.Level, lod);
                    }
                    else if (rcol.ChunkIndex(entry.ModelRef) is var chunk and >= 0 && rcol.TagOf(chunk) == "MLOD" && seen.Add((modlKey, chunk)))
                    {
                        if (TryCountMlodChunk(rcol.Chunks[chunk], out var lod)) AddLevel(entry.Level, lod);
                    }
                }
            }
            var lods = levels.Values.ToList();
            lods.RemoveAll(l => l.Triangles <= 0);
            return lods.Count == 0 && !external ? MeshStats.None : new MeshStats(lods, external);
        }
        catch (Exception)
        {
            return MeshStats.None;
        }
    }

    private delegate bool MeshParser(byte[] data, out MeshLod lod);

    // A mesh is often shared by many parts of a merged package, so parsed sizes are remembered by key.
    private static bool TryReadCached(TGI_Key key, ResourceReader read, Dictionary<TGI_Key, MeshLod?>? cache, MeshParser parse, out MeshLod lod, out bool missing)
    {
        lod = new MeshLod(0, 0);
        missing = false;
        if (cache != null && cache.TryGetValue(key, out var known))
        {
            if (known is null) return false;
            lod = known;
            return true;
        }

        var data = read(key);
        if (data == null)
        {
            missing = true;
            return false;
        }

        bool ok = parse(data, out lod);
        cache?.TryAdd(key, ok ? lod : null);
        return ok;
    }

    private static MeshStats Build(List<MeshLod> lods, bool external)
    {
        lods.RemoveAll(l => l.Triangles <= 0);
        return BuildCore(lods, external);
    }

    private static MeshStats BuildCore(List<MeshLod> lods, bool external) =>
        lods.Count == 0 && !external
            ? MeshStats.None
            : new MeshStats(lods.OrderByDescending(l => l.Triangles).ThenByDescending(l => l.Vertices).ToList(), external);

    // CASP: header (version, table offset), then ... a byte count and keys stored instance, group, type.
    internal static List<TGI_Key> ReadCaspTable(byte[] d)
    {
        var list = new List<TGI_Key>();
        if (d.Length < 16) return list;
        long pos = 8L + BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4));
        if (pos < 0 || pos >= d.Length) return list;
        int count = d[pos++];
        for (int i = 0; i < count && pos + 16 <= d.Length; i++, pos += 16)
        {
            var span = d.AsSpan((int)pos, 16);
            list.Add(new TGI_Key(
                BinaryPrimitives.ReadUInt32LittleEndian(span[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(span)));
        }
        return list;
    }

    // OBJK: version, table offset (from byte 8), table size, ... ; the table is a 32-bit count then type, group, instance.
    private static List<TGI_Key> ReadObjkTable(byte[] d)
    {
        var list = new List<TGI_Key>();
        if (d.Length < 12) return list;
        long pos = 8L + BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4));
        return ReadCountedKeys(d, pos, list);
    }

    // VPXY/MODL style chunk: tag, version, table offset (from byte 12), table size; the table is a 32-bit count then type, group, instance.
    // Keys listed in the resource's own reference list are included too.
    private static List<TGI_Key> ReadChunkTable(byte[] rcol)
    {
        var list = new List<TGI_Key>();
        if (!TryReadRcol(rcol, out var references, out var chunks)) return list;
        list.AddRange(references);
        foreach (var (pos, size) in chunks)
        {
            if (size < 16 || pos < 0 || pos + size > rcol.Length) continue;
            long table = pos + 12L + BinaryPrimitives.ReadUInt32LittleEndian(rcol.AsSpan(pos + 8));
            if (table + 4 <= pos + size)
            {
                ReadCountedKeys(rcol, table, list, pos + size);
            }
        }
        return list;
    }

    private static List<TGI_Key> ReadCountedKeys(byte[] d, long pos, List<TGI_Key> into, long? end = null)
    {
        long limit = end ?? d.Length;
        if (pos < 0 || pos + 4 > limit) return into;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos));
        pos += 4;
        for (uint i = 0; i < count && i < MaxEntries && pos + 16 <= limit; i++, pos += 16)
        {
            var span = d.AsSpan((int)pos, 16);
            into.Add(new TGI_Key(
                BinaryPrimitives.ReadUInt32LittleEndian(span),
                BinaryPrimitives.ReadUInt32LittleEndian(span[4..]),
                BinaryPrimitives.ReadUInt64LittleEndian(span[8..])));
        }
        return into;
    }

    // RCOL: version, public/internal count, unused, external count, internal count; internal keys, external keys
    // (instance, type, group), then a (position, size) pair per internal chunk.
    private static bool TryReadRcol(byte[] d, out List<TGI_Key> references, out List<(int Pos, int Size)> chunks)
    {
        references = new List<TGI_Key>();
        chunks = new List<(int, int)>();
        if (d.Length < 20) return false;

        uint external = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(12));
        uint inner = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(16));
        if (external > MaxEntries || inner > MaxEntries) return false;

        long pos = 20 + inner * 16L;
        if (pos + external * 16L + inner * 8L > d.Length) return false;
        for (uint i = 0; i < external; i++, pos += 16)
        {
            var span = d.AsSpan((int)pos, 16);
            references.Add(new TGI_Key(
                BinaryPrimitives.ReadUInt32LittleEndian(span[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[12..]),
                BinaryPrimitives.ReadUInt64LittleEndian(span)));
        }
        for (uint i = 0; i < inner; i++, pos += 8)
        {
            long chunkPos = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos));
            long chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos + 4));
            if (chunkPos + chunkSize > d.Length) return false;
            chunks.Add(((int)chunkPos, (int)chunkSize));
        }
        return true;
    }

    private static bool TryReadTag(byte[] d, int pos, int size, string tag) =>
        size >= 8 && d[pos] == tag[0] && d[pos + 1] == tag[1] && d[pos + 2] == tag[2] && d[pos + 3] == tag[3];

    // GEOM: tag, version, table offset/size, optional embedded material, merge group, sort order, vertex count,
    // vertex attributes and buffer, then the index lists. Triangles are indices / 3.
    private static bool TryReadGeom(byte[] d, out MeshLod lod)
    {
        lod = new MeshLod(0, 0);
        if (!TryReadRcol(d, out _, out var chunks) || chunks.Count == 0) return false;
        var (start, size) = chunks[0];
        if (!TryReadTag(d, start, size, "GEOM")) return false;

        var span = d.AsSpan(start, size);
        int pos = 16;
        if (!Read32(span, ref pos, out uint material)) return false;
        if (material != 0)
        {
            if (!Read32(span, ref pos, out uint materialSize)) return false;
            pos += (int)materialSize;
        }
        pos += 8; // merge group, sort order
        if (!Read32(span, ref pos, out uint vertexCount) || !Read32(span, ref pos, out uint attributeCount)) return false;
        if (attributeCount > 256) return false;

        long vertexBytes = 0;
        for (uint i = 0; i < attributeCount; i++)
        {
            pos += 8;
            if (pos >= span.Length) return false;
            vertexBytes += span[pos++];
        }
        long after = pos + vertexBytes * vertexCount;
        if (after + 4 > span.Length) return false;
        pos = (int)after;

        if (!Read32(span, ref pos, out uint listCount) || listCount > 64) return false;
        long indices = 0;
        for (uint i = 0; i < listCount; i++)
        {
            if (pos + 5 > span.Length) return false;
            int indexSize = span[pos++];
            if (!Read32(span, ref pos, out uint count)) return false;
            indices += count;
            pos += (int)Math.Min((long)indexSize * count, int.MaxValue / 2);
        }

        lod = new MeshLod((int)(indices / 3), (int)vertexCount);
        return true;
    }

    private static bool TryReadMlod(byte[] d, out MeshLod lod)
    {
        lod = new MeshLod(0, 0);
        if (!TryReadRcol(d, out _, out var chunks)) return false;

        foreach (var (start, size) in chunks)
        {
            if (!TryReadTag(d, start, size, "MLOD")) continue;
            return TryCountMlodChunk(d.AsSpan(start, size), out lod);
        }
        return false;
    }

    // Counts an MLOD chunk; shadow meshes (no vertex layout) don't count.
    private static bool TryCountMlodChunk(ReadOnlySpan<byte> span, out MeshLod lod)
    {
        lod = new MeshLod(0, 0);
        int pos = 8;
        if (!Read32(span, ref pos, out uint meshCount) || meshCount > 4096) return false;

        long triangles = 0, vertices = 0;
        for (uint i = 0; i < meshCount; i++)
        {
            if (!Read32(span, ref pos, out uint recordSize) || recordSize < 52 || pos + recordSize > span.Length) return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 8)..]) != 0)
            {
                vertices += BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 40)..]);
                triangles += BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 44)..]);
            }
            pos += (int)recordSize;
        }
        lod = new MeshLod((int)triangles, (int)vertices);
        return true;
    }

    private static bool Read32(ReadOnlySpan<byte> span, ref int pos, out uint value)
    {
        if (pos < 0 || pos + 4 > span.Length) { value = 0; return false; }
        value = BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]);
        pos += 4;
        return true;
    }
}
