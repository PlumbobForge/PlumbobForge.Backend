using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using S3ForgeTools.GameFiles.Package;

namespace PlumbobForge.Backend.Services;

public sealed record RgbaImage(int Width, int Height, byte[] Rgba, string Label = "");

// DDS decoding (DXT1/3/5, RGB(A)).
public static class DdsDecoder
{
    public static RgbaImage Decode(byte[] bytes)
    {
        if (bytes.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x20534444) throw new InvalidDataException("Not a DDS file");
        int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192) throw new InvalidDataException($"Implausible DDS size {width}x{height}");
        uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(80));
        string fourCC = Encoding.ASCII.GetString(bytes, 84, 4);
        var o = new byte[width * height * 4];
        int p = 128;

        if ((pfFlags & 4) != 0)
        {
            if (fourCC is not ("DXT1" or "DXT3" or "DXT5")) throw new InvalidDataException($"Unsupported DDS format {fourCC}");
            int bw = Math.Max(1, (width + 3) >> 2), bh = Math.Max(1, (height + 3) >> 2);
            int blockBytes = fourCC == "DXT1" ? 8 : 16;
            if (p + (long)bw * bh * blockBytes > bytes.Length) throw new InvalidDataException("The DDS file ends early");
            var pal = new byte[16];
            var alpha = new byte[16];
            var lut = new byte[8];
            for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                if (fourCC == "DXT3")
                {
                    for (int i = 0; i < 16; i++) alpha[i] = (byte)(((bytes[p + (i >> 1)] >> ((i & 1) * 4)) & 15) * 17);
                    p += 8;
                }
                else if (fourCC == "DXT5")
                {
                    int a0 = bytes[p], a1 = bytes[p + 1];
                    lut[0] = (byte)a0; lut[1] = (byte)a1;
                    if (a0 > a1) for (int i = 1; i < 7; i++) lut[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
                    else
                    {
                        for (int i = 1; i < 5; i++) lut[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
                        lut[6] = 0; lut[7] = 255;
                    }
                    int bits = 0, nbits = 0, q = p + 2;
                    for (int i = 0; i < 16; i++)
                    {
                        if (nbits < 3) { bits |= bytes[q++] << nbits; nbits += 8; }
                        alpha[i] = lut[bits & 7]; bits >>= 3; nbits -= 3;
                    }
                    p += 8;
                }
                else Array.Fill(alpha, (byte)255);

                int c0 = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p)), c1 = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 2));
                Rgb565(c0, pal, 0); Rgb565(c1, pal, 4);
                pal[3] = pal[7] = pal[11] = pal[15] = 255;
                if (c0 > c1 || fourCC != "DXT1")
                {
                    for (int k = 0; k < 3; k++) { pal[8 + k] = (byte)((2 * pal[k] + pal[4 + k]) / 3); pal[12 + k] = (byte)((pal[k] + 2 * pal[4 + k]) / 3); }
                }
                else
                {
                    for (int k = 0; k < 3; k++) { pal[8 + k] = (byte)((pal[k] + pal[4 + k]) >> 1); pal[12 + k] = 0; }
                    pal[15] = 0;
                }
                uint idx = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 4));
                p += 8;
                for (int i = 0; i < 16; i++)
                {
                    int x = bx * 4 + (i & 3), y = by * 4 + (i >> 2);
                    if (x >= width || y >= height) continue;
                    int c = (int)((idx >> (i * 2)) & 3), d = (y * width + x) * 4;
                    o[d] = pal[c * 4]; o[d + 1] = pal[c * 4 + 1]; o[d + 2] = pal[c * 4 + 2];
                    o[d + 3] = fourCC == "DXT1" ? pal[c * 4 + 3] : alpha[i];
                }
            }
        }
        else
        {
            int bpp = (int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(88)) / 8);
            if (bpp is not (3 or 4)) throw new InvalidDataException($"Unsupported DDS bit depth {bpp * 8}");
            if (p + (long)width * height * bpp > bytes.Length) throw new InvalidDataException("The DDS file ends early");
            var masks = new[] { 92, 96, 100, 104 }.Select(off => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(off))).ToArray();
            var shifts = masks.Select(m => m == 0 ? -1 : System.Numerics.BitOperations.TrailingZeroCount(m)).ToArray();
            bool hasAlpha = (pfFlags & 1) != 0 && masks[3] != 0;
            for (int i = 0; i < width * height; i++, p += bpp)
            {
                uint v = bpp == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p)) : (uint)(bytes[p] | (bytes[p + 1] << 8) | (bytes[p + 2] << 16));
                for (int k = 0; k < 3; k++) o[i * 4 + k] = shifts[k] < 0 ? (byte)0 : (byte)((v & masks[k]) >> shifts[k]);
                o[i * 4 + 3] = hasAlpha ? (byte)((v & masks[3]) >> shifts[3]) : (byte)255;
            }
        }
        return new RgbaImage(width, height, o, (pfFlags & 4) != 0 ? fourCC : "RGB");
    }

    private static void Rgb565(int c, byte[] o, int at)
    {
        o[at] = (byte)(((c >> 11) & 31) * 255 / 31);
        o[at + 1] = (byte)(((c >> 5) & 63) * 255 / 63);
        o[at + 2] = (byte)((c & 31) * 255 / 31);
    }
}

public sealed record CasPattern(string Name, double[]? Color, bool Enabled);

public sealed record CasPreset(string Complate, IReadOnlyDictionary<string, string> Values, IReadOnlyDictionary<string, CasPattern> Patterns)
{
    public bool IsHair => Complate.Contains("hair", StringComparison.OrdinalIgnoreCase)
        || (Values.ContainsKey("Root Color") && Values.ContainsKey("Diffuse Map"));

    private static readonly Regex PresetRx = new(@"<preset>[\s\S]*?</preset>", RegexOptions.Compiled);
    private static readonly Regex ComplateRx = new(@"<complate name=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex PatternRx = new(@"<pattern ([^>]*)>([\s\S]*?)</pattern>", RegexOptions.Compiled);
    private static readonly Regex AttrRx = new(@"(\w+)=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex ValueRx = new(@"<value ([^>]*?)/?>", RegexOptions.Compiled);

    // CASPs hold UTF-16 text; object preset resources are UTF-8.
    public static List<CasPreset> ReadAll(byte[] data, Encoding? encoding = null)
    {
        string text = (encoding ?? Encoding.Unicode).GetString(data);
        var list = new List<CasPreset>();
        foreach (Match m in PresetRx.Matches(text))
        {
            string xml = m.Value;
            string complate = ComplateRx.Match(xml) is { Success: true } c ? c.Groups[1].Value : "";
            var patterns = new Dictionary<string, (string Name, double[]? Color)>();
            string top = PatternRx.Replace(xml, pm =>
            {
                var at = Attrs(pm.Groups[1].Value);
                var v = ParseValues(pm.Groups[2].Value);
                string variable = at.GetValueOrDefault("variable") ?? "";
                patterns[variable] = (at.GetValueOrDefault("name") ?? "", ParseColor(v.GetValueOrDefault("Color") ?? v.GetValueOrDefault("Color 0")) ?? HsvColor(v));
                return "";
            });
            var values = ParseValues(top);
            var withState = patterns.ToDictionary(p => p.Key, p => new CasPattern(p.Value.Name, p.Value.Color,
                string.Equals(values.GetValueOrDefault(p.Key + " Enabled") ?? "true", "true", StringComparison.OrdinalIgnoreCase)));
            list.Add(new CasPreset(complate, values, withState));
        }
        return list;
    }

    private static Dictionary<string, string> Attrs(string s)
    {
        var d = new Dictionary<string, string>();
        foreach (Match m in AttrRx.Matches(s)) d[m.Groups[1].Value] = m.Groups[2].Value;
        return d;
    }

    private static Dictionary<string, string> ParseValues(string s)
    {
        var d = new Dictionary<string, string>();
        foreach (Match m in ValueRx.Matches(s))
        {
            var a = Attrs(m.Groups[1].Value);
            if (a.TryGetValue("key", out var k)) d[k] = a.GetValueOrDefault("value") ?? "";
        }
        return d;
    }

    // Game paint patterns: a base colour shifted in HSV.
    internal static double[]? HsvColor(Dictionary<string, string> v)
    {
        double? N(string key) => v.TryGetValue(key, out var t) && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        if (N("Base H Bg") is not double h || N("Base S Bg") is not double sat || N("Base V Bg") is not double val) return null;
        var shift = ParseColor(v.GetValueOrDefault("HSVShift Bg"));
        if (shift is { Length: 3 } && shift.All(double.IsFinite))
        {
            h += shift[0];
            sat += shift[1];
            val += shift[2];
        }
        h -= Math.Floor(h);
        sat = Math.Clamp(sat, 0, 1);
        val = Math.Clamp(val, 0, 1);
        double c = val * sat, x = c * (1 - Math.Abs(h * 6 % 2 - 1)), m = val - c;
        var (r, g, b) = (int)(h * 6) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return new[] { r + m, g + m, b + m };
    }

    internal static double[]? ParseColor(string? s)
    {
        if (s == null) return null;
        return s.Split(',').Take(3).Select(x => string.IsNullOrWhiteSpace(x) ? 0
            : double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
    }
}

// Approximate in-game look for the preview.
public static class CcMaterial
{
    public const uint TextureType = 0x00B2D882u;
    public const string InGame = "ingame";

    private static readonly string[] RoleOrder = { "Diffuse Map", "Multiplier", "Overlay", "Mask", "Control Map", "Clothing Specular", "Specular Map" };

    public static Dictionary<string, InspectedResource> PresetTextures(PackageInspection inspection, CasPreset preset)
    {
        var byInstance = new Dictionary<ulong, InspectedResource>();
        foreach (var r in inspection.Resources.Where(r => r.Key.Type == TextureType)) byInstance.TryAdd(r.Key.Instance, r);
        var o = new Dictionary<string, InspectedResource>();
        foreach (var (role, value) in preset.Values)
        {
            if (!value.StartsWith("key:00b2d882:", StringComparison.OrdinalIgnoreCase) || value.Length < 16) continue;
            if (!ulong.TryParse(value[^16..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var instance)) continue;
            if ((byInstance.GetValueOrDefault(instance) ?? inspection.Game?.FindByInstance(TextureType, instance)) is { } res) o[role] = res;
        }
        return o;
    }

    public static List<string> TextureRoles(PackageInspection inspection, CasPreset preset) =>
        PresetTextures(inspection, preset).Keys
            .OrderBy(r => Array.IndexOf(RoleOrder, r) is var i && i >= 0 ? i + 1 : 99)
            .ThenBy(r => r, StringComparer.Ordinal)
            .ToList();

    // mode: InGame or a role name.
    public static RgbaImage? PreviewTexture(PackageInspection inspection, CasPreset preset, string mode = InGame, Dictionary<InspectedResource, RgbaImage>? cache = null,
        IEnumerable<GeomMesh>? meshes = null)
    {
        var roles = PresetTextures(inspection, preset);
        RgbaImage Decode(InspectedResource r)
        {
            if (cache != null && cache.TryGetValue(r, out var hit)) return hit;
            var img = DdsDecoder.Decode(inspection.Read(r));
            cache?.TryAdd(r, img);
            return img;
        }

        if (mode != InGame)
        {
            if (!roles.TryGetValue(mode, out var e)) return null;
            var img = Decode(e);
            var rgba = (byte[])img.Rgba.Clone();
            for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
            return new RgbaImage(img.Width, img.Height, rgba, mode);
        }
        // A compositor's base image can be the game's own; its overlay then goes over grey.
        if (preset.Complate == CompositorName && !roles.ContainsKey("Multiplier"))
            return roles.TryGetValue("Overlay", out var oe) ? OverGrey(Decode(oe)) : null;
        var result = preset.IsHair ? Hair(preset, roles, Decode) : Clothing(preset, roles, Decode);
        if (result != null && preset.IsHair && meshes != null) PaintSolidParts(result, preset, roles, Decode, meshes);
        if (result != null) BleedTransparent(result.Rgba);
        return result;
    }

    private static RgbaImage OverGrey(RgbaImage over)
    {
        var o = new byte[over.Rgba.Length];
        for (int i = 0; i < o.Length; i += 4)
        {
            double a = over.Rgba[i + 3] / 255.0;
            for (int k = 0; k < 3; k++) o[i + k] = ToByte(180 + (over.Rgba[i + k] - 180) * a);
            o[i + 3] = 255;
        }
        return new RgbaImage(over.Width, over.Height, o, "Overlay on grey");
    }

    // Paints solid parts of a hair (like a hat) with the clothing colours.
    private static void PaintSolidParts(RgbaImage hair, CasPreset preset, Dictionary<string, InspectedResource> roles, Func<InspectedResource, RgbaImage> decode, IEnumerable<GeomMesh> meshes)
    {
        var solid = meshes.Where(m => m.Shader != 0 && !m.IsHairShader).ToList();
        if (solid.Count == 0 || !roles.ContainsKey("Multiplier")) return;
        var clothing = Clothing(preset, roles, decode);
        if (clothing == null) return;

        int w = hair.Width, h = hair.Height;
        var covered = new bool[w * h];
        foreach (var g in solid)
        {
            var uv = g.GetFloats(GeomVertexFormat.UsageUv, out int n);
            if (uv == null || n < 2) continue;
            var I = g.Indices;
            for (int t = 0; t + 2 < I.Length; t += 3)
            {
                if (I[t] >= g.VertexCount || I[t + 1] >= g.VertexCount || I[t + 2] >= g.VertexCount) continue;
                FillTriangle(covered, w, h,
                    uv[I[t] * n] * w, uv[I[t] * n + 1] * h,
                    uv[I[t + 1] * n] * w, uv[I[t + 1] * n + 1] * h,
                    uv[I[t + 2] * n] * w, uv[I[t + 2] * n + 1] * h);
            }
        }

        var cs = Sampler(clothing, w, h)!;
        // Converted hairs often have a solid inner layer over blank clothing images; that layer keeps the hair colour.
        long area = 0, blank = 0;
        for (int i = 0; i < w * h; i++)
        {
            if (!covered[i]) continue;
            area++;
            int q = cs(i);
            var c = clothing.Rgba;
            if (c[q + 3] < 16 || (c[q] > 235 && c[q + 1] > 235 && c[q + 2] > 235)) blank++;
        }
        if (blank * 2 > area) return;

        var o = hair.Rgba;
        for (int i = 0; i < w * h; i++)
        {
            if (!covered[i]) continue;
            int d = i * 4, q = cs(i);
            o[d] = clothing.Rgba[q]; o[d + 1] = clothing.Rgba[q + 1]; o[d + 2] = clothing.Rgba[q + 2]; o[d + 3] = 255;
        }
    }

    private static void FillTriangle(bool[] covered, int w, int h, double x0, double y0, double x1, double y1, double x2, double y2)
    {
        int minX = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2))) - 1), maxX = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))) + 1);
        int minY = Math.Max(0, (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2))) - 1), maxY = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2))) + 1);
        if (minX > maxX || minY > maxY) return;
        double area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (Math.Abs(area) < 1e-9) return;
        double l01 = Math.Max(1e-9, Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)));
        double l12 = Math.Max(1e-9, Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)));
        double l20 = Math.Max(1e-9, Math.Sqrt((x0 - x2) * (x0 - x2) + (y0 - y2) * (y0 - y2)));
        double sign = Math.Sign(area);
        for (int y = minY; y <= maxY; y++)
        {
            double py = y + 0.5;
            for (int x = minX; x <= maxX; x++)
            {
                double px = x + 0.5;
                double e0 = sign * ((x1 - x0) * (py - y0) - (y1 - y0) * (px - x0)) / l01;
                double e1 = sign * ((x2 - x1) * (py - y1) - (y2 - y1) * (px - x1)) / l12;
                double e2 = sign * ((x0 - x2) * (py - y2) - (y0 - y2) * (px - x2)) / l20;
                if (e0 >= -1.5 && e1 >= -1.5 && e2 >= -1.5) covered[y * w + x] = true;
            }
        }
    }

    // Fills see-through pixels with the average colour, so edges don't get a bright fringe.
    private static void BleedTransparent(byte[] p)
    {
        double s0 = 0, s1 = 0, s2 = 0;
        long n = 0;
        for (int o = 0; o < p.Length; o += 4) if (p[o + 3] >= 128) { s0 += p[o]; s1 += p[o + 1]; s2 += p[o + 2]; n++; }
        if (n == 0) return;
        double a0 = s0 / n, a1 = s1 / n, a2 = s2 / n;
        for (int o = 0; o < p.Length; o += 4)
        {
            double a = p[o + 3] / 255.0;
            if (a >= 0.5) continue;
            double k = a * 2;
            p[o] = ToByte(a0 + (p[o] - a0) * k);
            p[o + 1] = ToByte(a1 + (p[o + 1] - a1) * k);
            p[o + 2] = ToByte(a2 + (p[o + 2] - a2) * k);
        }
    }

    private static byte ToByte(double v) => double.IsNaN(v) ? (byte)0 : (byte)Math.Clamp((int)v, 0, 255);

    private static Func<int, int>? Sampler(RgbaImage? img, int w, int h)
    {
        if (img == null) return null;
        if (img.Width == w && img.Height == h) return i => i * 4;
        double sx = (double)img.Width / w, sy = (double)img.Height / h;
        return i => ((int)Math.Floor((i / w) * sy) * img.Width + (int)Math.Floor((i % w) * sx)) * 4;
    }

    private static double[]? Col(IReadOnlyDictionary<string, string> v, string key) => CasPreset.ParseColor(v.GetValueOrDefault(key));

    // Base colour plus root, highlight and tip colours, as the preview paints them.
    public static (double[] Base, double[]?[] Tints) HairColours(CasPreset preset)
    {
        var v = preset.Values;
        var baseColor = Col(v, "Diffuse Color") ?? new[] { 0.4, 0.3, 0.2 };
        var tints = new[] { Col(v, "Root Color"), Col(v, "Highlight Color"), Col(v, "Tip Color") };
        if (!IsMarkerColours(tints.Prepend(baseColor))) return (baseColor, tints);
        // The game paints hair in the Sim's own colour, so use its default brown.
        return (new[] { 0.3765, 0.2196, 0.0863 },
            new double[]?[] { new[] { 0.1882, 0.1490, 0.1098 }, new[] { 0.4667, 0.2863, 0.1686 }, new[] { 0.2863, 0.1882, 0.1176 } });
    }

    private static RgbaImage? Hair(CasPreset preset, Dictionary<string, InspectedResource> roles, Func<InspectedResource, RgbaImage> decode)
    {
        var dEntry = roles.GetValueOrDefault("Diffuse Map") ?? roles.GetValueOrDefault("Multiplier");
        if (dEntry == null) return null;
        var diff = decode(dEntry);
        var ctrl = roles.TryGetValue("Control Map", out var ce) ? decode(ce) : null;
        int w = diff.Width, h = diff.Height;
        var cs = Sampler(ctrl, w, h);
        var (baseColor, tints) = HairColours(preset);
        var o = new byte[w * h * 4];
        var c = new double[3];
        for (int i = 0; i < w * h; i++)
        {
            int d = i * 4;
            c[0] = baseColor[0]; c[1] = baseColor[1]; c[2] = baseColor[2];
            if (cs != null)
            {
                int q = cs(i);
                for (int t = 0; t < 3; t++)
                {
                    double k = ctrl!.Rgba[q + t] / 255.0;
                    var tint = tints[t];
                    if (tint != null && k > 0) for (int j = 0; j < 3; j++) c[j] += (tint[j] - c[j]) * k;
                }
            }
            double g = diff.Rgba[d] / 255.0;
            for (int j = 0; j < 3; j++) o[d + j] = ToByte(Math.Min(255, c[j] * g * 255 * 1.6));
            o[d + 3] = diff.Rgba[d + 3];
        }
        return new RgbaImage(w, h, o, "Hair colour (approx.)");
    }

    // Some creators fill the hair colours with marker colours (red, green, blue...) to see the control map.
    // That's a pure channel colour like #ff0000, or two bright non-hair hues (not red, brown or blonde) far apart.
    private static bool IsMarkerColours(IEnumerable<double[]?> colours)
    {
        var hues = new List<double>();
        foreach (var c in colours)
        {
            if (c == null) continue;
            double max = c.Take(3).Max(), min = c.Take(3).Min();
            if (max >= 0.85 && min <= 0.08 && c.Take(3).All(x => x <= 0.08 || x >= 0.85)) return true;
            if (max < 0.25 || max - min < 0.3 * max) continue;
            double d = max - min;
            double hue = (max == c[0] ? (c[1] - c[2]) / d : max == c[1] ? 2 + (c[2] - c[0]) / d : 4 + (c[0] - c[1]) / d) * 60;
            hue = (hue + 360) % 360;
            if (hue > 60) hues.Add(hue);
        }
        for (int i = 0; i < hues.Count; i++)
            for (int j = i + 1; j < hues.Count; j++)
                if (Math.Min(Math.Abs(hues[i] - hues[j]), 360 - Math.Abs(hues[i] - hues[j])) >= 75) return true;
        return false;
    }

    private static RgbaImage? Clothing(CasPreset preset, Dictionary<string, InspectedResource> roles, Func<InspectedResource, RgbaImage> decode)
    {
        var mEntry = roles.GetValueOrDefault("Multiplier") ?? roles.GetValueOrDefault("Diffuse Map") ?? roles.Values.FirstOrDefault();
        if (mEntry == null) return null;
        var mult = decode(mEntry);
        int w = mult.Width, h = mult.Height;
        var mask = roles.TryGetValue("Mask", out var me) ? decode(me) : null;
        var over = roles.TryGetValue("Overlay", out var oe) ? decode(oe) : null;
        var ms = Sampler(mask, w, h);
        var os = Sampler(over, w, h);
        var pats = new[] { "Pattern A", "Pattern B", "Pattern C", "Pattern D" }
            .Select(n => preset.Patterns.TryGetValue(n, out var p) && p.Enabled && p.Color != null ? p.Color : null)
            .ToArray();
        var o = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int d = i * 4;
            double r = 1, g = 1, b = 1;
            if (ms != null)
            {
                int q = ms(i);
                double wsum = 0, tr = 0, tg = 0, tb = 0;
                for (int k = 0; k < 4; k++)
                {
                    var p = pats[k];
                    if (p == null) continue;
                    double wk = (k == 3 ? mask!.Rgba[q + 3] : mask!.Rgba[q + k]) / 255.0;
                    if (k == 3 && mask.Rgba[q + 3] == 255) continue; // opaque alpha = no channel D
                    wsum += wk; tr += p[0] * wk; tg += p[1] * wk; tb += p[2] * wk;
                }
                if (wsum > 1) { tr /= wsum; tg /= wsum; tb /= wsum; wsum = 1; }
                r = 1 - wsum + tr; g = 1 - wsum + tg; b = 1 - wsum + tb;
            }
            double R = mult.Rgba[d] * r, G = mult.Rgba[d + 1] * g, B = mult.Rgba[d + 2] * b;
            if (os != null)
            {
                int q = os(i);
                double a = over!.Rgba[q + 3] / 255.0;
                R += (over.Rgba[q] - R) * a; G += (over.Rgba[q + 1] - G) * a; B += (over.Rgba[q + 2] - B) * a;
            }
            o[d] = ToByte(R); o[d + 1] = ToByte(G); o[d + 2] = ToByte(B); o[d + 3] = mult.Rgba[d + 3];
        }
        return new RgbaImage(w, h, o, "Preset colours (approx.)");
    }

    // TXTC property ids.
    private const uint TxtcId = 0x687720A6, TxtcImage = 0xF6CC8471, TxtcSrcBlend = 0xE055EE36, TxtcDstBlend = 0x048F7567,
        TxtcColorWrite = 0xB07B3B93, TxtcChannel = 0xD0E69002;

    public const string CompositorName = "Texture compositor";
    private const uint PresetXml = 0x0333406Cu;

    // An object's preset XMLs. Their image keys are often stale, but their pattern colours match the compositor.
    public static List<CasPreset> ObjectPresets(PackageInspection inspection, IReadOnlySet<ulong> modelInstances)
    {
        var all = inspection.Resources.Where(r => r.Key.Type == PresetXml)
            .Select(r => (Res: r, Presets: CasPreset.ReadAll(inspection.Read(r), Encoding.UTF8)))
            .Where(x => x.Presets.Count > 0)
            .ToList();
        var own = all.Where(x => modelInstances.Contains(x.Res.Key.Instance)).ToList();
        if (own.Count == 0 && all.Count == 1) own = all;
        return own.OrderBy(x => x.Res.Key.Group).SelectMany(x => x.Presets).ToList();
    }

    // A wall or floor pattern's colour variants, or a terrain paint's brush texture, as presets.
    public static List<CasPreset> PatternPresets(PackageInspection inspection, InspectedResource catalog)
    {
        using var ms = new MemoryStream(inspection.Read(catalog), writable: false);
        if (catalog.Key.Type == Sims3ResourceTypes.CatalogTerrainPaintBrush)
        {
            var paint = new CatalogResource.TerrainPaintBrushCatalogResource(0, ms);
            var brush = paint.BrushTexture;
            var values = new Dictionary<string, string> { ["Multiplier"] = KeyValue(brush.ResourceType, brush.ResourceGroup, brush.Instance) };
            return new List<CasPreset> { new("Terrain paint", values, new Dictionary<string, CasPattern>()) };
        }
        if (catalog.Key.Type != Sims3ResourceTypes.CatalogWallFloorPattern) return new List<CasPreset>();

        var pattern = new CatalogResource.WallFloorPatternCatalogResource(0, ms);
        var presets = new List<CasPreset>();
        foreach (var material in pattern.Materials)
        {
            if (material.MaterialBlock is { } block) presets.Add(PresetFromBlock(block, material.TGIBlocks));
        }
        return presets;
    }

    private static string KeyValue(uint type, uint group, ulong instance) => $"key:{type:x8}:{group:x8}:{instance:x16}";

    // A catalog material block holds what a CAS preset's XML holds: the template's values, and one sub-block per
    // pattern with its colour.
    private static CasPreset PresetFromBlock(CatalogResource.CatalogResource.MaterialBlock block, IList<s3pi.Interfaces.TGIBlock> keys)
    {
        var values = BlockValues(block, keys);
        var patterns = new Dictionary<string, CasPattern>();
        foreach (var sub in block.MaterialBlocks)
        {
            if (string.IsNullOrEmpty(sub.Pattern)) continue;
            var v = BlockValues(sub, keys);
            var color = CasPreset.ParseColor(v.GetValueOrDefault("Color") ?? v.GetValueOrDefault("Color 0")) ?? CasPreset.HsvColor(v);
            bool enabled = string.Equals(values.GetValueOrDefault(sub.Pattern + " Enabled") ?? "true", "true", StringComparison.OrdinalIgnoreCase);
            patterns[sub.Pattern] = new CasPattern(sub.Name ?? "", color, enabled);
        }
        return new CasPreset(block.Name ?? "", values, patterns);
    }

    private static Dictionary<string, string> BlockValues(CatalogResource.CatalogResource.MaterialBlock block, IList<s3pi.Interfaces.TGIBlock> keys)
    {
        static string F(float f) => f.ToString(CultureInfo.InvariantCulture);
        var values = new Dictionary<string, string>();
        foreach (var e in block.ComplateOverrides)
        {
            string? value = e switch
            {
                CatalogResource.CatalogResource.TC01_String s => s.Data,
                CatalogResource.CatalogResource.TC02_ARGB c => string.Join(",",
                    new[] { (c.ARGB >> 16) & 0xFF, (c.ARGB >> 8) & 0xFF, c.ARGB & 0xFF, c.ARGB >> 24 }.Select(x => F(x / 255f))),
                CatalogResource.CatalogResource.TC03_TGIIndex t => t.TGIIndex < keys.Count
                    ? KeyValue(keys[t.TGIIndex].ResourceType, keys[t.TGIIndex].ResourceGroup, keys[t.TGIIndex].Instance) : null,
                CatalogResource.CatalogResource.TC04_Single f => F(f.Unknown1),
                CatalogResource.CatalogResource.TC05_XY xy => $"{F(xy.Unknown1)},{F(xy.Unknown2)}",
                CatalogResource.CatalogResource.TC07_Boolean b => b.Unknown1 ? "true" : "false",
                _ => null
            };
            if (value != null && !string.IsNullOrEmpty(e.VariableName)) values[e.VariableName] = value;
        }
        return values;
    }

    // The images an object's texture compositor (TXTC) draws from the package, as a preset:
    // multiplied in = Multiplier, blended over = Overlay, split by channel = Mask. Pattern colours come from colours.
    public static CasPreset? CompositorPreset(PackageInspection inspection, InspectedResource txtc, CasPreset? colours = null)
    {
        var d = inspection.Read(txtc);
        if (d.Length < 9) return null;
        int keysAt = 8 + BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(4));
        if (keysAt < 8 || keysAt >= d.Length) return null;
        int count = d[keysAt];
        var keys = new List<TGI_Key>();
        for (int i = 0; i < count && keysAt + 1 + i * 16 + 16 <= d.Length; i++)
        {
            int p = keysAt + 1 + i * 16;
            keys.Add(new TGI_Key(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 12)), BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p))));
        }
        var inPackage = inspection.Resources.Where(r => r.Key.Type == TextureType).Select(r => r.Key.Instance).ToHashSet();

        var steps = new List<int>();
        for (int p = 0; p + 6 <= keysAt; p++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p)) == TxtcId && d[p + 4] == 0 && d[p + 5] == 7) steps.Add(p);

        var values = new Dictionary<string, string>();
        for (int s = 0; s < steps.Count; s++)
        {
            int from = steps[s], to = s + 1 < steps.Count ? steps[s + 1] : keysAt;
            if (TxtcProp(d, from, to, TxtcImage, 0x0C, 1) is not int at || d[at] >= keys.Count) continue;
            var key = keys[d[at]];
            if (key.Type != TextureType || (!inPackage.Contains(key.Instance) && inspection.Game?.FindByInstance(TextureType, key.Instance) == null)) continue;
            int? src = TxtcProp(d, from, to, TxtcSrcBlend, 3, 4) is int a ? BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(a)) : null;
            int? dst = TxtcProp(d, from, to, TxtcDstBlend, 3, 4) is int b ? BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(b)) : null;
            int? write = TxtcProp(d, from, to, TxtcColorWrite, 3, 4) is int c ? BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(c)) : null;
            string? role = TxtcProp(d, from, to, TxtcChannel, 0x0B, 16) != null ? "Mask"
                : write != 7 ? null
                : src == 8 && dst == 2 ? "Multiplier"
                : src == 4 && dst == 5 ? "Overlay"
                : null;
            if (role != null && !values.ContainsKey(role)) values[role] = $"key:{TextureType:X8}:{key.Group:X8}:{key.Instance:X16}";
        }
        return values.Count == 0 ? null
            : new CasPreset(CompositorName, values, colours?.Patterns ?? new Dictionary<string, CasPattern>());
    }

    // Offset of a property's value: id, a zero byte, the type, then the value.
    private static int? TxtcProp(byte[] d, int from, int to, uint id, byte type, int size)
    {
        for (int p = from; p + 6 + size <= to; p++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p)) == id && d[p + 4] == 0 && d[p + 5] == type) return p + 6;
        return null;
    }

    // Whether the alpha should cut holes in these meshes. wrap: UVs repeat (objects).
    public static bool UsesCutout(RgbaImage tex, IEnumerable<GeomMesh> geoms, bool wrap = false)
    {
        long total = 0, clear = 0;
        foreach (var g in geoms)
        {
            var uv = g.GetFloats(GeomVertexFormat.UsageUv, out int n);
            if (uv == null || n < 2) continue;
            var I = g.Indices;
            int tris = I.Length / 3;
            int step = Math.Max(1, tris / 5000);
            for (int t = 0; t < tris; t += step)
            {
                uint a = I[t * 3], b = I[t * 3 + 1], c = I[t * 3 + 2];
                double u = (uv[a * n] + uv[b * n] + uv[c * n]) / 3.0, v = (uv[a * n + 1] + uv[b * n + 1] + uv[c * n + 1]) / 3.0;
                if (wrap) { u -= Math.Floor(u); v -= Math.Floor(v); }
                int x = Math.Min(tex.Width - 1, Math.Max(0, (int)Math.Floor(u * tex.Width)));
                int y = Math.Min(tex.Height - 1, Math.Max(0, (int)Math.Floor(v * tex.Height)));
                total++;
                if (tex.Rgba[(y * tex.Width + x) * 4 + 3] < 128) clear++;
            }
        }
        return total > 0 && (double)clear / total > 0.1;
    }
}
