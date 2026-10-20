using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PromptFlowStudio.Services.Cad;

/// <summary>3D 정점(단정밀도 부동소수 x,y,z).</summary>
public readonly record struct Vec3(float X, float Y, float Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    public float Length => (float)Math.Sqrt((double)X * X + (double)Y * Y + (double)Z * Z);
}

/// <summary>파싱된 CAD 메시 — 용접(weld)된 정점 + 유니크 엣지(선) + 바운딩 박스. 순수 기하만 보관(렌더러 소비).</summary>
public sealed record CadMesh(
    IReadOnlyList<Vec3> Vertices,
    IReadOnlyList<(int A, int B)> Edges,
    int TriangleCount,
    Vec3 Min, Vec3 Max, bool IsBinary)
{
    /// <summary>바운딩 박스 중심.</summary>
    public Vec3 Center => new((Min.X + Max.X) / 2f, (Min.Y + Max.Y) / 2f, (Min.Z + Max.Z) / 2f);
    /// <summary>바운딩 반경(중심→최대 코너 거리) — 프레임 맞춤 스케일 기준.</summary>
    public float Radius { get { var d = Max - Center; var r = d.Length; return r <= 0 ? 1f : r; } }
    /// <summary>유효 메시 여부(정점·엣지 존재).</summary>
    public bool IsValid => Vertices.Count >= 2 && Edges.Count >= 1;
}

/// <summary>
/// [PromptFlowStudio] 순수 C# BCL STL(Binary/ASCII) 3D 메시 파서(v2.6.0) — 외부 3D 라이브러리 0.
///   ▸ <b>Binary STL</b>: 80바이트 헤더 + uint32 삼각형 수 + 삼각형당 50바이트(법선 3×float + 정점 3×3 float + 2바이트 속성)를 <see cref="BitConverter"/>로 리틀엔디안 디코드.
///   ▸ <b>ASCII STL</b>: <c>facet normal … outer loop … vertex x y z …</c> 토큰을 스트림 파싱.
///   ▸ 삼각형 정점을 격자 양자화로 <b>용접(weld)</b>해 중복 제거 → 인덱스 정점 + <b>유니크 무방향 엣지</b> 집합 산출(와이어프레임 오버드로 최소화).
///   ▸ 파일 크기 = 84 + 50×N 이면 Binary, 아니면 ASCII 로 자동 판별(헤더 "solid" 위장 대응). 실패는 예외 없이 null.
/// </summary>
public static class StlMeshParser
{
    private const int MaxTriangles = 2_000_000;   // 방어(비정상 카운트로 인한 과대 할당 차단)

    /// <summary>STL 파일을 파싱한다(없거나 손상 시 null).</summary>
    public static CadMesh? Parse(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            return ParseBytes(File.ReadAllBytes(path));
        }
        catch (Exception ex) { Program.Log("StlMeshParser.Parse", ex); return null; }
    }

    /// <summary>STL 바이트 배열을 파싱한다(자동 Binary/ASCII 판별).</summary>
    public static CadMesh? ParseBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 15) return null;

        // Binary 판별: 헤더(80) + count(4) 이후 정확히 50*count 바이트인지.
        var binary = false;
        if (bytes.Length >= 84)
        {
            var n = BitConverter.ToUInt32(bytes, 80);
            if (n <= MaxTriangles && 84L + 50L * n == bytes.Length) binary = true;
        }

        var tris = binary ? ReadBinary(bytes) : ReadAscii(bytes);
        if (tris.Count == 0) return null;
        return Weld(tris, binary);
    }

    // ── Binary STL ──
    private static List<(Vec3 A, Vec3 B, Vec3 C)> ReadBinary(byte[] b)
    {
        var list = new List<(Vec3, Vec3, Vec3)>();
        var count = BitConverter.ToUInt32(b, 80);
        if (count > MaxTriangles) return list;
        var off = 84;
        for (uint i = 0; i < count; i++, off += 50)
        {
            if (off + 50 > b.Length) break;
            // 0..11 = 법선(무시 · 엣지엔 불필요), 12..47 = 정점 3개, 48..49 = 속성
            var a = new Vec3(F(b, off + 12), F(b, off + 16), F(b, off + 20));
            var c1 = new Vec3(F(b, off + 24), F(b, off + 28), F(b, off + 32));
            var c2 = new Vec3(F(b, off + 36), F(b, off + 40), F(b, off + 44));
            list.Add((a, c1, c2));
        }
        return list;
    }

    private static float F(byte[] b, int o) => BitConverter.ToSingle(b, o);

    // ── ASCII STL ──
    private static List<(Vec3 A, Vec3 B, Vec3 C)> ReadAscii(byte[] bytes)
    {
        var list = new List<(Vec3, Vec3, Vec3)>();
        string text;
        try { text = Encoding.ASCII.GetString(bytes); } catch { return list; }

        var verts = new List<Vec3>(3);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 6) continue;
            if (line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);   // 공백류 분해
                if (p.Length >= 4
                    && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                    && float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                {
                    verts.Add(new Vec3(x, y, z));
                    if (verts.Count == 3) { list.Add((verts[0], verts[1], verts[2])); verts.Clear(); }
                }
            }
            else if (line.StartsWith("endfacet", StringComparison.OrdinalIgnoreCase)) verts.Clear();   // 방어(불완전 facet 리셋)
        }
        return list;
    }

    // ── 정점 용접 + 유니크 엣지 ──
    private static CadMesh Weld(List<(Vec3 A, Vec3 B, Vec3 C)> tris, bool binary)
    {
        // 바운딩 박스(양자화 격자 크기 산정용).
        var min = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vec3(float.MinValue, float.MinValue, float.MinValue);
        foreach (var (a, b, c) in tris) { Acc(ref min, ref max, a); Acc(ref min, ref max, b); Acc(ref min, ref max, c); }

        var span = max - min;
        var diag = span.Length;
        var quant = diag > 0 ? diag / 4096f : 1e-4f;   // 대각선 1/4096 격자로 용접(모델 크기 무관 일관)
        if (quant <= 0) quant = 1e-4f;

        var index = new Dictionary<(int, int, int), int>();
        var verts = new List<Vec3>();
        var edges = new HashSet<(int, int)>();

        int VId(Vec3 v)
        {
            var key = ((int)MathF.Round(v.X / quant), (int)MathF.Round(v.Y / quant), (int)MathF.Round(v.Z / quant));
            if (index.TryGetValue(key, out var id)) return id;
            id = verts.Count; index[key] = id; verts.Add(v); return id;
        }
        void Edge(int a, int b) { if (a != b) edges.Add(a < b ? (a, b) : (b, a)); }

        foreach (var (a, b, c) in tris)
        {
            int ia = VId(a), ib = VId(b), ic = VId(c);
            Edge(ia, ib); Edge(ib, ic); Edge(ic, ia);
        }

        return new CadMesh(verts, new List<(int, int)>(edges), tris.Count, min, max, binary);
    }

    private static void Acc(ref Vec3 min, ref Vec3 max, Vec3 v)
    {
        min = new Vec3(Math.Min(min.X, v.X), Math.Min(min.Y, v.Y), Math.Min(min.Z, v.Z));
        max = new Vec3(Math.Max(max.X, v.X), Math.Max(max.Y, v.Y), Math.Max(max.Z, v.Z));
    }
}
