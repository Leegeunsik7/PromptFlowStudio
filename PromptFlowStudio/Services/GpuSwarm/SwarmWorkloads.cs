using System.Numerics;
using PromptFlowStudio.Services.PathTracing;
using PromptFlowStudio.Services.VideoDiffusion;

namespace PromptFlowStudio.Services.GpuSwarm;

/// <summary>스웜 타일 워크로드 — 픽셀 결정적(타일 분할 무관 동일 결과) · 무할당(scratch는 워커 소유).</summary>
public interface ISwarmWorkload
{
    string Name { get; }
    int Width { get; }
    int Height { get; }
    /// <summary>워커별 scratch 요구량(floats).</summary>
    int ScratchFloats { get; }
    /// <summary>dst = 타일 원점 RGB(행 간격 dstStride floats).</summary>
    void RenderTile(long frame, int x, int y, int w, int h, Span<float> dst, int dstStride, Span<float> scratch);
}

/// <summary>v16.74 볼류메트릭 패스트레이서 타일 워크로드(RenderFrame Color와 비트 일치).</summary>
public sealed class PathTraceSwarmWorkload : ISwarmWorkload
{
    public PathTraceSwarmWorkload(VolumetricPathTracingLightTransportEngine engine) => Engine = engine;

    /// <summary>스웜 전용 데모 장면(지면 · 금속/유전체/발광 구 · 박스).</summary>
    public static PathTraceSwarmWorkload CreateDemo(int w = 160, int h = 90)
    {
        var mtl = new MaterialXBsdfShaderSynthesizer();
        var red = mtl.Register("swarm-red", new PbrMaterial { BaseColor = new Vector3(0.8f, 0.15f, 0.12f), Roughness = 0.4f, Specular = 0.5f, Ior = 1.5f, Opacity = 1f });
        var gold = mtl.Register("swarm-gold", new PbrMaterial { BaseColor = new Vector3(1f, 0.78f, 0.34f), Metallic = 1f, Roughness = 0.2f, Specular = 0.5f, Ior = 1.5f, Opacity = 1f });
        var glow = mtl.Register("swarm-glow", new PbrMaterial { BaseColor = new Vector3(0.9f), Emission = new Vector3(4f, 3.2f, 2.2f), Roughness = 0.8f, Specular = 0.5f, Ior = 1.5f, Opacity = 1f });
        var blue = mtl.Register("swarm-blue", new PbrMaterial { BaseColor = new Vector3(0.2f, 0.35f, 0.8f), Roughness = 0.6f, Specular = 0.5f, Ior = 1.5f, Opacity = 1f });
        var e = new VolumetricPathTracingLightTransportEngine(mtl) { Spp = 1, MaxBounces = 4 };
        e.SetResolution(w, h);
        e.SetCamera(new Vector3(0, 1.4f, 6), new Vector3(0, 0.7f, 0), 45f);
        e.AddSphere(new Vector3(-1.4f, 0.7f, 0), 0.7f, red);
        e.AddSphere(new Vector3(0.2f, 0.9f, -0.6f), 0.9f, gold);
        e.AddSphere(new Vector3(1.6f, 0.45f, 0.6f), 0.45f, glow);
        e.AddBox(new Vector3(-0.4f, 0, 1f), new Vector3(0.4f, 0.5f, 1.8f), blue);
        return new PathTraceSwarmWorkload(e);
    }

    public VolumetricPathTracingLightTransportEngine Engine { get; }
    public string Name => "패스트레이싱(v16.74)";
    public int Width => Engine.Width;
    public int Height => Engine.Height;
    public int ScratchFloats => 0;

    public void RenderTile(long frame, int x, int y, int w, int h, Span<float> dst, int dstStride, Span<float> scratch) =>
        Engine.RenderTile(frame, x, y, w, h, dst, dstStride);
}

/// <summary>
/// v16.87 라텐트 디퓨전 타일 워크로드: 픽셀 결정적 노이즈 → DiffusionSimd.GuidedStep(CFG 가우시안 사후 DDIM) N스텝 → RGB.
/// 레이아웃 μ = 하늘/지면 그라디언트 + 프레임별 이동 피사체(디퓨전 팔레트).
/// </summary>
public sealed class DiffusionSwarmWorkload : ISwarmWorkload
{
    public DiffusionSwarmWorkload(int w = 160, int h = 90, int steps = 12) { Width = w; Height = h; Steps = steps; }

    public string Name => "라텐트 디퓨전(v16.87)";
    public int Width { get; }
    public int Height { get; }
    public int Steps { get; set; }
    public float Guidance { get; set; } = 3f;
    public int ScratchFloats => Width * 4;

    public void RenderTile(long frame, int x, int y, int w, int h, Span<float> dst, int dstStride, Span<float> scratch)
    {
        var xb = scratch[..w]; var mu = scratch.Slice(w, w); var v = scratch.Slice(w * 2, w); var x0 = scratch.Slice(w * 3, w);
        var pal = DiffusionPalette.Default;
        var steps = Math.Clamp(Steps, 1, RealtimeLatentVideoDiffusionEngine.MaxSteps);
        var subX = 0.3f + 0.4f * (0.5f + 0.5f * MathF.Sin(frame * 0.15f));
        for (var row = 0; row < h; row++)
        {
            var py = y + row;
            var fy = (py + 0.5f) / Height;
            var o = dst.Slice(row * dstStride, w * 3);
            for (var c = 0; c < 3; c++)
            {
                for (var i = 0; i < w; i++)
                {
                    var px = x + i;
                    var fx = (px + 0.5f) / Width;
                    xb[i] = Gauss(Hash((ulong)frame, (ulong)(py * Width + px), (ulong)c));
                    var sky = Vector3.Lerp(pal.SkyHorizon, pal.SkyTop, Math.Clamp(1f - fy / 0.55f, 0f, 1f));
                    var gnd = Vector3.Lerp(pal.GroundFar, pal.GroundNear, Math.Clamp((fy - 0.55f) / 0.45f, 0f, 1f));
                    var col = fy < 0.55f ? sky : gnd;
                    var dx = (fx - subX) * Width / Height; var dy = fy - 0.52f;
                    if (dx * dx + dy * dy < 0.018f) col = pal.Subject;
                    mu[i] = 2f * (c == 0 ? col.X : c == 1 ? col.Y : col.Z) - 1f;
                    v[i] = pal.Texture * pal.Texture;
                }
                for (var k = 0; k < steps; k++)
                {
                    var ab = RealtimeLatentVideoDiffusionEngine.AlphaBarAt(RealtimeLatentVideoDiffusionEngine.Timestep(k, steps));
                    var abp = k + 1 < steps ? RealtimeLatentVideoDiffusionEngine.AlphaBarAt(RealtimeLatentVideoDiffusionEngine.Timestep(k + 1, steps)) : 1f;
                    DiffusionSimd.GuidedStep(xb, mu, v, x0, MathF.Sqrt(ab), MathF.Sqrt(Math.Max(1e-8f, 1f - ab)), MathF.Sqrt(abp), MathF.Sqrt(Math.Max(0f, 1f - abp)), Guidance, 1.5f);
                }
                for (var i = 0; i < w; i++) o[i * 3 + c] = Math.Clamp(0.5f + 0.5f * x0[i], 0f, 1f);
            }
        }
    }

    private static ulong Hash(ulong a, ulong b, ulong c)
    {
        var z = a * 0x9E3779B97F4A7C15UL ^ b * 0xC2B2AE3D27D4EB4FUL ^ c * 0x165667B19E3779F9UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>근사 정규(균등 4합 · 분산 1).</summary>
    private static float Gauss(ulong h)
    {
        var s = (h & 0xFFFF) + ((h >> 16) & 0xFFFF) + ((h >> 32) & 0xFFFF) + (h >> 48);
        return (float)((s / 65535.0 - 2.0) * 1.7320508);
    }
}
