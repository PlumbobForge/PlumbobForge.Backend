using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using S3ForgeTools.GameFiles.Package;

namespace PlumbobForge.Backend.Services;

// Build/buy object meshes (MLOD), following s3pi. Unchanged data is kept byte for byte.

public sealed class RcolFile
{
    public bool AlignChunks { get; set; } = true;
    public uint Version { get; set; }
    public uint PublicChunks { get; set; }
    public uint Unused { get; set; }
    public List<TGI_Key> ChunkKeys { get; } = new();
    public List<TGI_Key> External { get; } = new();
    public List<byte[]> Chunks { get; } = new();

    public static RcolFile Read(byte[] d)
    {
        if (d.Length < 20) throw new InvalidDataException("Too short for an RCOL resource");
        var f = new RcolFile { Version = U32(d, 0), PublicChunks = U32(d, 4), Unused = U32(d, 8) };
        int external = (int)U32(d, 12), inner = (int)U32(d, 16);
        if (f.Version is < 1 or > 3 || external > 4096 || inner > 4096) throw new InvalidDataException("Not an RCOL resource");
        int pos = 20;
        if (pos + (inner + external) * 16L + inner * 8L > d.Length) throw new InvalidDataException("The RCOL tables run past the end");
        for (int i = 0; i < inner; i++, pos += 16) f.ChunkKeys.Add(Key(d, pos));
        for (int i = 0; i < external; i++, pos += 16) f.External.Add(Key(d, pos));
        long expected = pos + inner * 8L;
        for (int i = 0; i < inner; i++, pos += 8)
        {
            long at = U32(d, pos), size = U32(d, pos + 4);
            if (at + size > d.Length) throw new InvalidDataException($"Chunk {i} runs past the end");
            f.Chunks.Add(d.AsSpan((int)at, (int)size).ToArray());
            if (expected % 4 != 0 && at == expected) f.AlignChunks = false;
            expected = at + size;
        }
        return f;
    }

    public byte[] Write()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Version); w.Write(PublicChunks); w.Write(Unused); w.Write(External.Count); w.Write(Chunks.Count);
        foreach (var k in ChunkKeys) WriteKey(w, k);
        foreach (var k in External) WriteKey(w, k);
        long table = ms.Position;
        for (int i = 0; i < Chunks.Count; i++) { w.Write(0u); w.Write(0u); }
        var places = new List<(long At, int Size)>();
        foreach (var c in Chunks)
        {
            while (AlignChunks && ms.Position % 4 != 0) w.Write((byte)0);
            places.Add((ms.Position, c.Length));
            w.Write(c);
        }
        ms.Position = table;
        foreach (var (at, size) in places) { w.Write((uint)at); w.Write((uint)size); }
        w.Flush();
        return ms.ToArray();
    }

    // Chunk reference: top 4 bits = kind, rest = index + 1. Private chunks come after public ones.
    public int ChunkIndex(uint reference)
    {
        if (reference == 0) return -1;
        uint kind = reference >> 28;
        int index = (int)(reference & 0x0FFFFFFF) - 1;
        if (kind == 1) index += (int)PublicChunks;
        else if (kind != 0) return -1;
        return index >= 0 && index < Chunks.Count ? index : -1;
    }

    public TGI_Key? ExternalKey(uint reference)
    {
        uint kind = reference >> 28;
        int index = (int)(reference & 0x0FFFFFFF) - 1;
        if (kind is not (2 or 3) || index < 0 || index >= External.Count) return null;
        return External[index];
    }

    public string TagOf(int chunk) => chunk >= 0 && chunk < Chunks.Count && Chunks[chunk].Length >= 4 ? Encoding.ASCII.GetString(Chunks[chunk], 0, 4) : "";

    internal static uint U32(byte[] d, int p) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p));
    private static TGI_Key Key(byte[] d, int p) =>
        new(U32(d, p + 8), U32(d, p + 12), BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p)));
    private static void WriteKey(BinaryWriter w, TGI_Key k) { w.Write(k.Instance); w.Write(k.Type); w.Write(k.Group); }
}

public sealed class MlodGeometryState
{
    public uint Name { get; set; }
    public int StartIndex { get; set; }
    public int MinVertexIndex { get; set; }
    public int VertexCount { get; set; }
    public int PrimitiveCount { get; set; }
}

public sealed class MlodMesh
{
    public const int TriangleList = 3;
    public const uint ShadowCasterFlag = 0x10;

    public uint Name { get; set; }
    public uint MaterialRef { get; set; }
    public uint VrtfRef { get; set; }
    public uint VbufRef { get; set; }
    public uint IbufRef { get; set; }
    public uint PrimitiveAndFlags { get; set; }
    public uint StreamOffset { get; set; }
    public int StartVertex { get; set; }
    public int StartIndex { get; set; }
    public int MinVertexIndex { get; set; }
    public int VertexCount { get; set; }
    public int PrimitiveCount { get; set; }
    public byte[] Bounds { get; set; } = new byte[24];
    public uint SkinRef { get; set; }
    public List<uint> Joints { get; } = new();
    public uint ScaleOffsetRef { get; set; }
    public List<MlodGeometryState> States { get; } = new();
    public uint ParentName { get; set; }
    public byte[] MirrorPlane { get; set; } = new byte[16];
    public byte[] Extra { get; set; } = Array.Empty<byte>();

    public int PrimitiveType => (int)(PrimitiveAndFlags & 0xFF);
    public uint Flags => PrimitiveAndFlags >> 8;
}

public sealed class MlodChunk
{
    public uint Version { get; set; }
    public List<MlodMesh> Meshes { get; } = new();

    public static MlodChunk Read(byte[] c)
    {
        if (c.Length < 12 || Encoding.ASCII.GetString(c, 0, 4) != "MLOD") throw new InvalidDataException("Not an MLOD chunk");
        var m = new MlodChunk { Version = RcolFile.U32(c, 4) };
        int count = (int)RcolFile.U32(c, 8);
        if (count > 4096) throw new InvalidDataException("Too many meshes");
        var r = new BinaryReader(new MemoryStream(c, 12, c.Length - 12));
        for (int i = 0; i < count; i++)
        {
            int size = (int)r.ReadUInt32();
            long start = r.BaseStream.Position;
            if (start + size > r.BaseStream.Length) throw new InvalidDataException($"Mesh {i} runs past the end of the MLOD");
            var mesh = new MlodMesh
            {
                Name = r.ReadUInt32(), MaterialRef = r.ReadUInt32(), VrtfRef = r.ReadUInt32(), VbufRef = r.ReadUInt32(), IbufRef = r.ReadUInt32(),
                PrimitiveAndFlags = r.ReadUInt32(), StreamOffset = r.ReadUInt32(), StartVertex = r.ReadInt32(), StartIndex = r.ReadInt32(),
                MinVertexIndex = r.ReadInt32(), VertexCount = r.ReadInt32(), PrimitiveCount = r.ReadInt32(),
                Bounds = r.ReadBytes(24), SkinRef = r.ReadUInt32(),
            };
            int joints = r.ReadInt32();
            if (joints is < 0 or > 4096) throw new InvalidDataException("Bad joint count");
            for (int j = 0; j < joints; j++) mesh.Joints.Add(r.ReadUInt32());
            mesh.ScaleOffsetRef = r.ReadUInt32();
            int states = r.ReadInt32();
            if (states is < 0 or > 4096) throw new InvalidDataException("Bad geometry state count");
            for (int j = 0; j < states; j++)
                mesh.States.Add(new MlodGeometryState { Name = r.ReadUInt32(), StartIndex = r.ReadInt32(), MinVertexIndex = r.ReadInt32(), VertexCount = r.ReadInt32(), PrimitiveCount = r.ReadInt32() });
            if (m.Version > 0x201)
            {
                mesh.ParentName = r.ReadUInt32();
                mesh.MirrorPlane = r.ReadBytes(16);
            }
            long used = r.BaseStream.Position - start;
            if (used > size) throw new InvalidDataException($"Mesh {i} is shorter than its fields");
            mesh.Extra = r.ReadBytes((int)(size - used));
            m.Meshes.Add(mesh);
        }
        return m;
    }

    public byte[] Write()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("MLOD")); w.Write(Version); w.Write(Meshes.Count);
        foreach (var m in Meshes)
        {
            long sizeAt = ms.Position;
            w.Write(0u);
            w.Write(m.Name); w.Write(m.MaterialRef); w.Write(m.VrtfRef); w.Write(m.VbufRef); w.Write(m.IbufRef);
            w.Write(m.PrimitiveAndFlags); w.Write(m.StreamOffset); w.Write(m.StartVertex); w.Write(m.StartIndex);
            w.Write(m.MinVertexIndex); w.Write(m.VertexCount); w.Write(m.PrimitiveCount);
            w.Write(m.Bounds); w.Write(m.SkinRef);
            w.Write(m.Joints.Count); foreach (var j in m.Joints) w.Write(j);
            w.Write(m.ScaleOffsetRef);
            w.Write(m.States.Count);
            foreach (var s in m.States) { w.Write(s.Name); w.Write(s.StartIndex); w.Write(s.MinVertexIndex); w.Write(s.VertexCount); w.Write(s.PrimitiveCount); }
            if (Version > 0x201) { w.Write(m.ParentName); w.Write(m.MirrorPlane); }
            w.Write(m.Extra);
            long end = ms.Position;
            ms.Position = sizeAt;
            w.Write((uint)(end - sizeAt - 4));
            ms.Position = end;
        }
        w.Flush();
        return ms.ToArray();
    }
}

public sealed record VrtfElement(byte Usage, byte UsageIndex, byte Format, byte Offset)
{
    public const byte Position = 0, Normal = 1, Uv = 2, BlendIndex = 3, BlendWeight = 4, Tangent = 5, Colour = 6;
    public const byte Float2 = 1, Float3 = 2, Float4 = 3, ColorUByte4 = 5, Short2 = 6, Short4 = 7;
}

public sealed class VrtfChunk
{
    public uint Version { get; set; }
    public int Stride { get; set; }
    public uint Extended { get; set; }
    public List<VrtfElement> Elements { get; } = new();

    public static VrtfChunk Read(byte[] c)
    {
        if (c.Length < 20 || Encoding.ASCII.GetString(c, 0, 4) != "VRTF") throw new InvalidDataException("Not a VRTF chunk");
        var v = new VrtfChunk { Version = RcolFile.U32(c, 4), Stride = (int)RcolFile.U32(c, 8), Extended = RcolFile.U32(c, 16) };
        int count = (int)RcolFile.U32(c, 12);
        if (count < 0 || 20 + count * 4 > c.Length) throw new InvalidDataException("Bad VRTF element count");
        for (int i = 0; i < count; i++) v.Elements.Add(new VrtfElement(c[20 + i * 4], c[21 + i * 4], c[22 + i * 4], c[23 + i * 4]));
        return v;
    }

    public VrtfElement? Find(byte usage, int usageIndex = 0) => Elements.FirstOrDefault(e => e.Usage == usage && e.UsageIndex == usageIndex);
}

public sealed class VbufChunk
{
    public uint Version { get; set; }
    public uint Flags { get; set; }
    public uint SwizzleRef { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public static VbufChunk Read(byte[] c)
    {
        if (c.Length < 16 || Encoding.ASCII.GetString(c, 0, 4) != "VBUF") throw new InvalidDataException("Not a VBUF chunk");
        return new VbufChunk { Version = RcolFile.U32(c, 4), Flags = RcolFile.U32(c, 8), SwizzleRef = RcolFile.U32(c, 12), Data = c.AsSpan(16).ToArray() };
    }

    public byte[] Write()
    {
        var o = new byte[16 + Data.Length];
        Encoding.ASCII.GetBytes("VBUF").CopyTo(o, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(8), Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(12), SwizzleRef);
        Data.CopyTo(o, 16);
        return o;
    }
}

public sealed class IbufChunk
{
    public const uint Differenced = 0x1, Indices32 = 0x2, DisplayList = 0x4;

    public uint Version { get; set; }
    public uint Flags { get; set; }
    public uint DisplayListUsage { get; set; }
    public int[] Indices { get; set; } = Array.Empty<int>();

    public static IbufChunk Read(byte[] c)
    {
        if (c.Length < 16 || Encoding.ASCII.GetString(c, 0, 4) != "IBUF") throw new InvalidDataException("Not an IBUF chunk");
        var b = new IbufChunk { Version = RcolFile.U32(c, 4), Flags = RcolFile.U32(c, 8), DisplayListUsage = RcolFile.U32(c, 12) };
        bool wide = (b.Flags & Indices32) != 0, diff = (b.Flags & Differenced) != 0;
        int size = wide ? 4 : 2, count = (c.Length - 16) / size;
        if ((c.Length - 16) % size != 0) throw new InvalidDataException("The IBUF has a partial index");
        var o = new int[count];
        int last = 0;
        for (int i = 0; i < count; i++)
        {
            int raw = wide ? BinaryPrimitives.ReadInt32LittleEndian(c.AsSpan(16 + i * 4)) : BinaryPrimitives.ReadUInt16LittleEndian(c.AsSpan(16 + i * 2));
            // Indices are stored as differences.
            if (diff) raw = wide ? last + raw : (ushort)(last + raw);
            o[i] = raw;
            last = raw;
        }
        b.Indices = o;
        return b;
    }

    public byte[] Write()
    {
        bool wide = (Flags & Indices32) != 0, diff = (Flags & Differenced) != 0;
        int size = wide ? 4 : 2;
        var o = new byte[16 + Indices.Length * size];
        Encoding.ASCII.GetBytes("IBUF").CopyTo(o, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(8), Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(12), DisplayListUsage);
        int last = 0;
        for (int i = 0; i < Indices.Length; i++)
        {
            int v = diff ? Indices[i] - last : Indices[i];
            last = Indices[i];
            if (wide) BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(16 + i * 4), v);
            else BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(16 + i * 2), (ushort)v);
        }
        return o;
    }
}

public sealed class ObjectMeshGeometry
{
    public required float[] Positions { get; init; }
    public float[]? Normals { get; init; }
    public float[]? Uvs { get; init; }
    public required uint[] Indices { get; init; }
    public int VertexCount => Positions.Length / 3;
    public int Triangles => Indices.Length / 3;
}

public sealed record ObjectMeshEdit(IReadOnlyList<int> Keep, uint[] Indices);

public sealed class MlodModel
{
    // Short UVs are /32767. The material's "uvscales" doesn't match the textures, so it isn't used.
    private const float ShortUvScale = 1f / 32767f;

    public RcolFile Rcol { get; }
    public IReadOnlyDictionary<int, MlodChunk> Mlods { get; }
    public IReadOnlyList<MlodMesh> Meshes { get; }
    private readonly int[] _chunkOf;

    private MlodModel(RcolFile rcol, Dictionary<int, MlodChunk> mlods)
    {
        Rcol = rcol;
        Mlods = mlods;
        var meshes = new List<MlodMesh>();
        var chunkOf = new List<int>();
        foreach (var (chunk, mlod) in mlods.OrderBy(kv => kv.Key))
            foreach (var m in mlod.Meshes) { meshes.Add(m); chunkOf.Add(chunk); }
        Meshes = meshes;
        _chunkOf = chunkOf.ToArray();
    }

    public static MlodModel Read(byte[] resource)
    {
        var rcol = RcolFile.Read(resource);
        var mlods = Enumerable.Range(0, rcol.Chunks.Count).Where(i => rcol.TagOf(i) == "MLOD").ToDictionary(i => i, i => MlodChunk.Read(rcol.Chunks[i]));
        if (mlods.Count == 0) throw new InvalidDataException("No MLOD chunk");
        return new MlodModel(rcol, mlods);
    }

    public int ChunkOf(int mesh) => _chunkOf[mesh];
    public IEnumerable<int> MeshesOf(int chunk) => Enumerable.Range(0, Meshes.Count).Where(i => _chunkOf[i] == chunk);

    public byte[] Write() => Write(new Dictionary<int, ObjectMeshEdit>());

    public int RealTriangles(int mesh) => Meshes[mesh].PrimitiveType == MlodMesh.TriangleList ? Meshes[mesh].PrimitiveCount : 0;

    public string? WhyNotEditable(int index)
    {
        var m = Meshes[index];
        if (m.PrimitiveType != MlodMesh.TriangleList) return "not a triangle list";
        if (m.VrtfRef == 0) return "shadow mesh (built-in vertex layout)";
        if (HasRealStates(m)) return "has geometry states";
        if (m.StartVertex != 0 || m.MinVertexIndex != 0) return "uses a vertex base";
        int vrtf = Rcol.ChunkIndex(m.VrtfRef), vbuf = Rcol.ChunkIndex(m.VbufRef), ibuf = Rcol.ChunkIndex(m.IbufRef);
        if (Rcol.TagOf(vrtf) != "VRTF" || Rcol.TagOf(vbuf) != "VBUF" || Rcol.TagOf(ibuf) != "IBUF") return "buffers are not in this resource";
        var layout = VrtfChunk.Read(Rcol.Chunks[vrtf]);
        var pos = layout.Find(VrtfElement.Position);
        if (pos == null || pos.Format is not (VrtfElement.Short4 or VrtfElement.Float3)) return "unsupported position format";
        var vb = VbufChunk.Read(Rcol.Chunks[vbuf]);
        if (vb.Flags != 0) return "packed vertex buffer";
        if (layout.Stride <= 0 || m.StreamOffset + (long)m.VertexCount * layout.Stride > vb.Data.Length) return "vertices run past the buffer";
        var ib = IbufChunk.Read(Rcol.Chunks[ibuf]);
        if ((ib.Flags & IbufChunk.DisplayList) != 0) return "display-list indices";
        if (m.StartIndex < 0 || m.StartIndex + (long)m.PrimitiveCount * 3 > ib.Indices.Length) return "indices run past the buffer";
        for (int i = m.StartIndex; i < m.StartIndex + m.PrimitiveCount * 3; i++)
            if (ib.Indices[i] < 0 || ib.Indices[i] >= m.VertexCount) return "index outside its vertices";
        if (Overlaps(index)) return "shares vertices or indices with another mesh";
        return null;
    }

    private static bool HasRealStates(MlodMesh m) => m.States.Any(s => s.PrimitiveCount > 0 || s.VertexCount > 0);

    private bool Overlaps(int index)
    {
        var m = Meshes[index];
        int stride = StrideOf(m);
        long v0 = m.StreamOffset, v1 = v0 + (long)m.VertexCount * stride;
        long i0 = m.StartIndex, i1 = i0 + m.PrimitiveCount * 3L;
        for (int k = 0; k < Meshes.Count; k++)
        {
            if (k == index) continue;
            var o = Meshes[k];
            if (Rcol.ChunkIndex(o.VbufRef) == Rcol.ChunkIndex(m.VbufRef))
            {
                long a = o.StreamOffset + (long)(o.StartVertex + o.MinVertexIndex) * StrideOf(o), b = a + (long)o.VertexCount * StrideOf(o);
                if (a < v1 && v0 < b) return true;
            }
            if (Rcol.ChunkIndex(o.IbufRef) == Rcol.ChunkIndex(m.IbufRef))
            {
                long a = o.StartIndex, b = a + o.PrimitiveCount * 3L;
                if (a < i1 && i0 < b) return true;
            }
            // Meshes sharing buffers with real geometry states are left alone.
            if (HasRealStates(o) && (Rcol.ChunkIndex(o.IbufRef) == Rcol.ChunkIndex(m.IbufRef) || Rcol.ChunkIndex(o.VbufRef) == Rcol.ChunkIndex(m.VbufRef))) return true;
        }
        return false;
    }

    private int StrideOf(MlodMesh m)
    {
        int vrtf = Rcol.ChunkIndex(m.VrtfRef);
        if (Rcol.TagOf(vrtf) == "VRTF") return VrtfChunk.Read(Rcol.Chunks[vrtf]).Stride;
        return (m.Flags & MlodMesh.ShadowCasterFlag) != 0 ? 8 : 16;
    }

    public ObjectMeshGeometry ReadGeometry(int index)
    {
        if (WhyNotEditable(index) is { } why) throw new InvalidOperationException("This mesh can't be read: " + why);
        var m = Meshes[index];
        var layout = VrtfChunk.Read(Rcol.Chunks[Rcol.ChunkIndex(m.VrtfRef)]);
        var data = VbufChunk.Read(Rcol.Chunks[Rcol.ChunkIndex(m.VbufRef)]).Data;
        var ib = IbufChunk.Read(Rcol.Chunks[Rcol.ChunkIndex(m.IbufRef)]);
        int n = m.VertexCount, stride = layout.Stride;
        var posEl = layout.Find(VrtfElement.Position)!;
        var nrmEl = layout.Find(VrtfElement.Normal);
        var uvEl = layout.Find(VrtfElement.Uv);
        float uvScale = ShortUvScale;

        var pos = new float[n * 3];
        float[]? nrm = nrmEl is { Format: VrtfElement.ColorUByte4 or VrtfElement.Float3 } ? new float[n * 3] : null;
        float[]? uvs = uvEl is { Format: VrtfElement.Short2 or VrtfElement.Float2 } ? new float[n * 2] : null;
        for (int v = 0; v < n; v++)
        {
            var vx = data.AsSpan((int)m.StreamOffset + v * stride, stride);
            ReadPosition(vx, posEl, pos.AsSpan(v * 3, 3));
            if (nrm != null) ReadNormal(vx, nrmEl!, nrm.AsSpan(v * 3, 3));
            if (uvs != null)
            {
                if (uvEl!.Format == VrtfElement.Short2)
                {
                    uvs[v * 2] = BinaryPrimitives.ReadInt16LittleEndian(vx[uvEl.Offset..]) * uvScale;
                    uvs[v * 2 + 1] = BinaryPrimitives.ReadInt16LittleEndian(vx[(uvEl.Offset + 2)..]) * uvScale;
                }
                else
                {
                    uvs[v * 2] = BinaryPrimitives.ReadSingleLittleEndian(vx[uvEl.Offset..]);
                    uvs[v * 2 + 1] = BinaryPrimitives.ReadSingleLittleEndian(vx[(uvEl.Offset + 4)..]);
                }
            }
        }
        var idx = new uint[m.PrimitiveCount * 3];
        for (int i = 0; i < idx.Length; i++) idx[i] = (uint)ib.Indices[m.StartIndex + i];
        return new ObjectMeshGeometry { Positions = pos, Normals = nrm, Uvs = uvs, Indices = idx };
    }

    private static void ReadPosition(ReadOnlySpan<byte> vx, VrtfElement e, Span<float> o)
    {
        if (e.Format == VrtfElement.Float3)
        {
            for (int k = 0; k < 3; k++) o[k] = BinaryPrimitives.ReadSingleLittleEndian(vx[(e.Offset + k * 4)..]);
            return;
        }
        int scale = BinaryPrimitives.ReadUInt16LittleEndian(vx[(e.Offset + 6)..]);
        if (scale == 0) scale = 32767;
        for (int k = 0; k < 3; k++) o[k] = BinaryPrimitives.ReadInt16LittleEndian(vx[(e.Offset + k * 2)..]) / (float)scale;
    }

    private static void ReadNormal(ReadOnlySpan<byte> vx, VrtfElement e, Span<float> o)
    {
        if (e.Format == VrtfElement.Float3)
        {
            for (int k = 0; k < 3; k++) o[k] = BinaryPrimitives.ReadSingleLittleEndian(vx[(e.Offset + k * 4)..]);
            return;
        }
        static float N(byte b) => b == 0 ? -1f : (b + 1) / 128f - 1f;
        o[0] = N(vx[e.Offset + 2]); o[1] = N(vx[e.Offset + 1]); o[2] = N(vx[e.Offset]);
    }

    // "diffusemap", plus two found in older MTRL materials.
    private static readonly uint[] DiffuseParams = { 0x6CC0FD85u, 0xBDCF71C5u, 0x53521204u };

    private int MaterialChunk(MlodMesh m)
    {
        int at = Rcol.ChunkIndex(m.MaterialRef);
        if (Rcol.TagOf(at) == "MTST" && Rcol.Chunks[at].Length >= 16) at = Rcol.ChunkIndex(RcolFile.U32(Rcol.Chunks[at], 12));
        return Rcol.TagOf(at) == "MATD" ? at : -1;
    }

    // MTNF (newer) or MTRL (older).
    private static int ParamBlock(byte[] c)
    {
        int at = c.AsSpan().IndexOf("MTNF"u8);
        if (at < 0) at = c.AsSpan().IndexOf("MTRL"u8);
        return at >= 0 && at + 16 <= c.Length ? at : -1;
    }

    // FNV-32 of the shader name, or 0 when the mesh has no material.
    public uint ShaderOf(int mesh)
    {
        int at = MaterialChunk(Meshes[mesh]);
        return at >= 0 && Rcol.Chunks[at].Length >= 16 ? RcolFile.U32(Rcol.Chunks[at], 12) : 0;
    }

    // Shaders of meshes the game draws only as helpers: shadow planes, and the water in a tub or sink, which shows
    // only when it is filled. "dropshadow", "shadowmap", "basinwater".
    private static readonly HashSet<uint> HelperShaders = new() { 0xC09C7582u, 0x21FE207Du, 0x6AAD2AD5u };

    public bool IsHelper(int mesh) => HelperShaders.Contains(ShaderOf(mesh));

    // MTNF: index into the external keys. MTRL: key written in place.
    public TGI_Key? DiffuseKey(int mesh)
    {
        int at = MaterialChunk(Meshes[mesh]);
        if (at < 0) return null;
        var c = Rcol.Chunks[at];
        int block = ParamBlock(c);
        if (block < 0) return null;
        int count = (int)RcolFile.U32(c, block + 12);
        foreach (uint wanted in DiffuseParams)
        {
            for (int i = 0; i < count && block + 32 + i * 16 <= c.Length; i++)
            {
                int e = block + 16 + i * 16;
                if (RcolFile.U32(c, e) != wanted || RcolFile.U32(c, e + 4) != 4) continue;
                uint values = RcolFile.U32(c, e + 8);
                int off = block + (int)RcolFile.U32(c, e + 12);
                if (values == 5 && off + 16 <= c.Length)
                    return new TGI_Key(RcolFile.U32(c, off + 8), RcolFile.U32(c, off + 12), BinaryPrimitives.ReadUInt64LittleEndian(c.AsSpan(off)));
                if (off + 4 <= c.Length) return Rcol.ExternalKey(RcolFile.U32(c, off));
            }
        }
        return null;
    }

    // Rebuilds only the buffers that hold reduced meshes.
    public byte[] Write(IReadOnlyDictionary<int, ObjectMeshEdit> edits)
    {
        if (edits.Count == 0) return Rcol.Write();
        foreach (var i in edits.Keys)
            if (WhyNotEditable(i) is { } why) throw new InvalidOperationException($"Mesh {i} can't be changed: {why}");

        var meshes = Meshes;
        var vbufs = edits.Keys.Select(i => Rcol.ChunkIndex(meshes[i].VbufRef)).Distinct().ToList();
        var ibufs = edits.Keys.Select(i => Rcol.ChunkIndex(meshes[i].IbufRef)).Distinct().ToList();

        foreach (int chunk in vbufs)
        {
            var vb = VbufChunk.Read(Rcol.Chunks[chunk]);
            var users = Enumerable.Range(0, meshes.Count).Where(i => Rcol.ChunkIndex(meshes[i].VbufRef) == chunk).ToList();
            var cut = users.Where(edits.ContainsKey).OrderBy(i => meshes[i].StreamOffset).ToList();
            var o = new MemoryStream();
            var runs = new List<(long From, long To, long Delta)>();
            var placed = new Dictionary<int, long>();
            long read = 0;
            foreach (int i in cut)
            {
                var m = meshes[i];
                int stride = StrideOf(m);
                runs.Add((read, m.StreamOffset, o.Length - read));
                o.Write(vb.Data, (int)read, (int)(m.StreamOffset - read));
                placed[i] = o.Length;
                foreach (int v in edits[i].Keep) o.Write(vb.Data, (int)(m.StreamOffset + (long)v * stride), stride);
                read = m.StreamOffset + (long)m.VertexCount * stride;
            }
            runs.Add((read, vb.Data.Length + 1L, o.Length - read));
            o.Write(vb.Data, (int)read, vb.Data.Length - (int)read);
            vb.Data = o.ToArray();
            Rcol.Chunks[chunk] = vb.Write();
            foreach (int i in users)
            {
                var m = meshes[i];
                if (placed.TryGetValue(i, out long at)) { m.StreamOffset = (uint)at; continue; }
                long first = m.StreamOffset + (long)(m.StartVertex + m.MinVertexIndex) * StrideOf(m);
                m.StreamOffset = (uint)(m.StreamOffset + Delta(runs, first));
            }
        }

        foreach (int chunk in ibufs)
        {
            var ib = IbufChunk.Read(Rcol.Chunks[chunk]);
            var users = Enumerable.Range(0, meshes.Count).Where(i => Rcol.ChunkIndex(meshes[i].IbufRef) == chunk).ToList();
            var cut = users.Where(edits.ContainsKey).OrderBy(i => meshes[i].StartIndex).ToList();
            var o = new List<int>(ib.Indices.Length);
            var runs = new List<(long From, long To, long Delta)>();
            var placed = new Dictionary<int, int>();
            int read = 0;
            foreach (int i in cut)
            {
                var m = meshes[i];
                runs.Add((read, m.StartIndex, o.Count - read));
                o.AddRange(ib.Indices.AsSpan(read, m.StartIndex - read).ToArray());
                placed[i] = o.Count;
                o.AddRange(edits[i].Indices.Select(x => (int)x));
                read = m.StartIndex + m.PrimitiveCount * 3;
            }
            runs.Add((read, ib.Indices.Length + 1L, o.Count - read));
            o.AddRange(ib.Indices.AsSpan(read).ToArray());
            ib.Indices = o.ToArray();
            if ((ib.Flags & IbufChunk.Indices32) == 0 && ib.Indices.Any(x => x > ushort.MaxValue)) throw new InvalidOperationException("Indices no longer fit 16 bits");
            Rcol.Chunks[chunk] = ib.Write();
            foreach (int i in users)
            {
                var m = meshes[i];
                if (placed.TryGetValue(i, out int at)) { m.StartIndex = at; continue; }
                m.StartIndex += (int)Delta(runs, m.StartIndex);
            }
        }

        foreach (var (i, e) in edits)
        {
            meshes[i].VertexCount = e.Keep.Count;
            meshes[i].PrimitiveCount = e.Indices.Length / 3;
        }

        foreach (var (chunk, mlod) in Mlods) Rcol.Chunks[chunk] = mlod.Write();
        return Rcol.Write();
    }

    private static long Delta(List<(long From, long To, long Delta)> runs, long position)
    {
        foreach (var r in runs) if (position >= r.From && position < r.To) return r.Delta;
        throw new InvalidOperationException("A mesh points into a part of a buffer that was replaced");
    }
}

public sealed record ModlLod(uint ModelRef, uint Flags, uint Id)
{
    public const uint ShadowBit = 0x10000;
    public bool IsShadow => (Id & ShadowBit) != 0;
    public int Level => (int)(Id & 0xFFFF);
}

public static class ModlChunk
{
    public static List<ModlLod> ReadLods(byte[] c)
    {
        if (c.Length < 36 || Encoding.ASCII.GetString(c, 0, 4) != "MODL") throw new InvalidDataException("Not a MODL chunk");
        uint version = RcolFile.U32(c, 4);
        int count = (int)RcolFile.U32(c, 8);
        int p = 12 + 24;
        if (version >= 0x102)
        {
            int extra = (int)RcolFile.U32(c, p);
            if (extra is < 0 or > 4096) throw new InvalidDataException("Bad MODL bounds count");
            p += 4 + extra * 24 + 8;
        }
        if (count is < 0 or > 64 || p + count * 20L > c.Length) throw new InvalidDataException("Bad MODL level count");
        var o = new List<ModlLod>();
        for (int i = 0; i < count; i++, p += 20) o.Add(new ModlLod(RcolFile.U32(c, p), RcolFile.U32(c, p + 4), RcolFile.U32(c, p + 8)));
        return o;
    }
}
