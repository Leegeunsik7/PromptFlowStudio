using PromptFlowStudio.Services.SmartPublishing;

namespace PromptFlowStudio.Services.VisualHooks;

public enum ImpactFlash : byte { Dark, Gold }

/// <summary>셰이크 이벤트(값 형식) — 시작 프레임 · 지속 프레임 · 진폭 · 플래시 색 · 난수 시드.</summary>
public struct ShakeEvent
{
    public long Frame;
    public byte Frames;
    public float AmpPx;
    public ImpactFlash Flash;
    public uint Seed;
}

/// <summary>
/// [PromptFlow Studio v17.77.0] 💥 오디오 연동 마이크로 스크린 셰이크 &amp; 비네트 필터(<see cref="Apply"/> 무할당).
/// v17.76 <see cref="ProceduralViralSoundFxSynthesizer"/> 큐 중 임팩트(5 프레임 ≈ 83 ms · 암전 비네트) · 차칭(3 프레임 ≈ 50 ms · 골드 플래시)만 트리거로 받아
/// 정수 프레임으로 정렬 저장하고, 활성 프레임마다 (시드 · 프레임) 해시 xorshift 로 결정적 수평/수직 ±4 px 정수 변위(첫 프레임 최대 · 선형 감쇠)를
/// 가장자리 클램프 표본화로 적용한 뒤, <see cref="Configure"/> 시 사전 계산한 타원 반경 smoothstep(0.42→1.0) 마스크로 외곽부를 암전/컬러 플래시 합성한다.
/// </summary>
public sealed class AudioReactiveScreenShakeVignetteFilter
{
    public const int Cap = 128, Fps = 60, ImpactFrames = 5, ChachingFrames = 3;
    public const float MaxAmpPx = 4f;
    public static readonly uint[] FlashColor = { 0xFF000000u, 0xFFFFC53Du };

    private readonly ShakeEvent[] _e = new ShakeEvent[Cap];
    private int _n;
    private byte[] _mask = Array.Empty<byte>();
    private int _mw, _mh;

    public uint Seed { get; set; } = 0x5EED77u;
    /// <summary>비네트 최대 강도(0..1).</summary>
    public float VignetteStrength { get; set; } = 0.85f;
    public int Count => _n;
    public ref readonly ShakeEvent At(int i) => ref _e[i];
    public long Dropped { get; private set; }
    public long ShakenFrames { get; private set; }
    public int MaxOffsetPx { get; private set; }
    public double LastApplyUs { get; private set; }

    public void Clear() => _n = 0;

    /// <summary>SFX 종류별 트리거(임팩트/차칭만) — 정렬 삽입 · 동일 프레임 = 더 긴 쪽 유지.</summary>
    public bool Trigger(double timeMs, ViralSfxKind kind)
    {
        if (kind != ViralSfxKind.Impact && kind != ViralSfxKind.Chaching) return false;
        if (timeMs < 0 || double.IsNaN(timeMs)) return false;
        var f = KineticZoomPunchCutEngine.FrameOf(timeMs, Fps);
        var ev = new ShakeEvent
        {
            Frame = f,
            Frames = kind == ViralSfxKind.Impact ? (byte)ImpactFrames : (byte)ChachingFrames,
            AmpPx = MaxAmpPx,
            Flash = kind == ViralSfxKind.Impact ? ImpactFlash.Dark : ImpactFlash.Gold,
            Seed = Seed ^ (uint)f * 0x9E3779B9u,
        };
        var pos = _n;
        while (pos > 0 && _e[pos - 1].Frame > f) pos--;
        if (pos > 0 && _e[pos - 1].Frame == f) { if (ev.Frames > _e[pos - 1].Frames) _e[pos - 1] = ev; return false; }
        if (_n == Cap) { Dropped++; return false; }
        Array.Copy(_e, pos, _e, pos + 1, _n - pos);
        _e[pos] = ev; _n++;
        return true;
    }

    public int CollectSfx(ProceduralViralSoundFxSynthesizer sfx, double offsetMs = 0)
    {
        var n = 0;
        for (var i = 0; i < sfx.CueCount; i++) { ref readonly var q = ref sfx.Cue(i); if (Trigger(offsetMs + q.TimeMs, q.Kind)) n++; }
        return n;
    }

    /// <summary>프레임 표본 — 활성 시 정수 변위 · 플래시 강도(0..1) · 색.</summary>
    public bool Sample(long frame, out int dx, out int dy, out float flash, out ImpactFlash kind)
    {
        dx = dy = 0; flash = 0; kind = ImpactFlash.Dark;
        int lo = 0, hi = _n - 1, k = -1;
        while (lo <= hi) { var m = (lo + hi) >> 1; if (_e[m].Frame <= frame) { k = m; lo = m + 1; } else hi = m - 1; }
        if (k < 0) return false;
        ref readonly var e = ref _e[k];
        var age = frame - e.Frame;
        if (age >= e.Frames) return false;
        var decay = 1f - age / (float)e.Frames;
        var h = Hash(e.Seed + (uint)age * 0x85EBCA6Bu);
        var amp = e.AmpPx * (0.5f + 0.5f * decay);
        dx = (int)MathF.Round(((h & 0xFFFF) / 32767.5f - 1f) * amp);
        dy = (int)MathF.Round((((h >> 16) & 0xFFFF) / 32767.5f - 1f) * amp);
        if (dx == 0 && dy == 0) dx = (h & 0x10000000u) != 0 ? (int)e.AmpPx : -(int)e.AmpPx;   // 무변위 프레임 방지
        flash = decay * VignetteStrength;
        kind = e.Flash;
        return true;
    }

    private static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7FEB352Du; x ^= x >> 15; x *= 0x846CA68Bu; x ^= x >> 16;
        return x;
    }

    /// <summary>해상도별 비네트 마스크(0..255) 사전 계산 — 크기 변경 시에만 할당.</summary>
    public void Configure(int w, int h)
    {
        if (w == _mw && h == _mh) return;
        _mask = new byte[w * h]; _mw = w; _mh = h;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var nx = (x + 0.5f) / w * 2f - 1f; var ny = (y + 0.5f) / h * 2f - 1f;
                var r = MathF.Sqrt(nx * nx + ny * ny) / 1.41421356f;
                var t = Math.Clamp((r - 0.42f) / (1.0f - 0.42f), 0f, 1f);
                _mask[y * w + x] = (byte)(t * t * (3f - 2f * t) * 255f + 0.5f);
            }
    }

    /// <summary>셰이크(가장자리 클램프 이동) + 외곽 플래시 비네트(src ≠ dst) — 무할당. 반환 = 활성 여부.</summary>
    public bool Apply(ReadOnlySpan<uint> src, Span<uint> dst, int w, int h, long frame)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!Sample(frame, out var dx, out var dy, out var flash, out var kind) || _mw != w || _mh != h)
        {
            src[..(w * h)].CopyTo(dst);
            LastApplyUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
            return false;
        }
        var color = FlashColor[(int)kind];
        var f8 = (uint)(flash * 256f);
        for (var y = 0; y < h; y++)
        {
            var sy = Math.Clamp(y - dy, 0, h - 1);
            var row = src.Slice(sy * w, w);
            var o = dst.Slice(y * w, w);
            var m = _mask.AsSpan(y * w, w);
            for (var x = 0; x < w; x++)
            {
                var p = row[Math.Clamp(x - dx, 0, w - 1)];
                var a = (m[x] * f8) >> 8;
                o[x] = a == 0 ? p : ProceduralVectorStickerOverlaySynthesizer.Blend(p, color, a);
            }
        }
        ShakenFrames++;
        var mag = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (mag > MaxOffsetPx) MaxOffsetPx = mag;
        LastApplyUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return true;
    }
}
