using PromptFlowStudio.Services.MotionTypography;
using PromptFlowStudio.Services.RetentionOptimizer;
using PromptFlowStudio.Services.RhythmAction;
using PromptFlowStudio.Services.SmartPublishing;

namespace PromptFlowStudio.Services.VisualHooks;

public enum ZoomPunchSource : byte { Hook, BeatDrop, Accent, Impact, Keyword, Manual }

/// <summary>줌 펀치 이벤트(값 형식) — 정수 시작 프레임 · 피크 배율 · 근거.</summary>
public struct ZoomPunch
{
    public long Frame;
    public float PeakScale;
    public ZoomPunchSource Source;
}

/// <summary>
/// [PromptFlow Studio v17.77.0] 🔍 무할당 키네틱 줌 펀치컷 엔진(<see cref="Apply"/> 무할당 · 60 fps).
/// v17.75 훅 분석기 자극 지점 · v17.54 드롭/악센트 컷 · v17.76 임팩트/차칭 SFX 큐 · v17.52 강조 키워드 단어 시작을
/// <b>정수 프레임</b>(round(ms × fps / 1000))으로 양자화해 정렬 저장 → 해당 프레임에서 정확히 피크 배율(1.12×~1.25×)로 즉시 컷,
/// <see cref="HoldFrames"/> 유지 후 <see cref="ReleaseFrames"/> 동안 3차 이즈아웃 복귀. 근접(&lt; 15 프레임) 펀치는 더 강한 쪽으로 병합한다.
/// <b>스마트 피벗</b>: 아바타 안면 박스 중심을 고정점으로 크롭 원점을 잡고(피벗 줌), 정수리 위 헤드룸이 크롭 밖으로 밀리면 원점을 끌어올려
/// 머리가 잘리지 않게 보정한 뒤 사전 할당 출력 버퍼에 16.16 고정소수 쌍선형 표본화로 직접 기록한다(중간 복사 0).
/// </summary>
public sealed class KineticZoomPunchCutEngine
{
    public const int Cap = 128, Fps = 60, HoldFrames = 4, ReleaseFrames = 9, MinGapFrames = 15;
    public const float MinScale = 1.12f, MaxScale = 1.25f, Headroom = 0.06f;

    private readonly ZoomPunch[] _p = new ZoomPunch[Cap];
    private int _n;
    private HookBox _face = new(0.36f, 0.20f, 0.28f, 0.20f);

    public int Count => _n;
    public ref readonly ZoomPunch At(int i) => ref _p[i];
    /// <summary>전역 펀치 강도(0..1) — 수집 가중치에 곱해 [1.12, 1.25] 배율로 사상.</summary>
    public float Strength { get; set; } = 0.7f;
    public HookBox Face => _face;
    public long Merged { get; private set; }
    public long Dropped { get; private set; }
    public long FramesApplied { get; private set; }
    public long HeadGuardCorrections { get; private set; }
    public long HeadClips { get; private set; }
    public double LastApplyUs { get; private set; }

    public static long FrameOf(double ms, int fps = Fps) => (long)Math.Round(ms * fps / 1000.0, MidpointRounding.AwayFromZero);
    public static float ScaleFor(float strength) => MinScale + (MaxScale - MinScale) * Math.Clamp(strength, 0f, 1f);

    public void Clear() => _n = 0;

    public void SetFace(in HookBox face)
    {
        var w = Math.Clamp(face.W, 0.02f, 1f); var h = Math.Clamp(face.H, 0.02f, 1f);
        _face = new HookBox(Math.Clamp(face.X, 0f, 1f - w), Math.Clamp(face.Y, 0f, 1f - h), w, h);
    }

    /// <summary>펀치 추가(정렬 삽입 · 근접 병합). 반환 = 신규 삽입 여부.</summary>
    public bool AddPunch(double timeMs, float weight, ZoomPunchSource src)
    {
        if (timeMs < 0 || double.IsNaN(timeMs)) return false;
        var f = FrameOf(timeMs);
        var s = ScaleFor(weight * Strength);
        var pos = _n;
        while (pos > 0 && _p[pos - 1].Frame > f) pos--;
        var near = pos > 0 && f - _p[pos - 1].Frame < MinGapFrames ? pos - 1 : pos < _n && _p[pos].Frame - f < MinGapFrames ? pos : -1;
        if (near >= 0)
        {
            Merged++;
            if (s > _p[near].PeakScale) { _p[near].PeakScale = s; _p[near].Source = src; }
            return false;
        }
        if (_n == Cap) { Dropped++; return false; }
        Array.Copy(_p, pos, _p, pos + 1, _n - pos);
        _p[pos] = new ZoomPunch { Frame = f, PeakScale = s, Source = src };
        _n++;
        return true;
    }

    /// <summary>v17.75 훅 계획의 자극 출력 시각(콜드 오픈 포함) → 최대 강도 펀치.</summary>
    public int CollectHook(ViralHookDeadAirAutoTrimmerEngine hook, double offsetMs = 0)
    {
        var r = hook.Last;
        return r.Ok && r.StimulusOutMs >= 0 && AddPunch(offsetMs + r.StimulusOutMs, 1f, ZoomPunchSource.Hook) ? 1 : 0;
    }

    /// <summary>v17.54 리듬 컷 — 드롭 = 1.0 · 악센트 = 0.55(그 외 무시).</summary>
    public int CollectCuts(ReadOnlySpan<RhythmCutEvent> cuts, double offsetMs = 0)
    {
        var n = 0;
        for (var i = 0; i < cuts.Length; i++)
        {
            var c = cuts[i];
            if (c.Reason == RhythmCutReason.Drop) { if (AddPunch(offsetMs + c.PresentSec * 1000, 1f, ZoomPunchSource.BeatDrop)) n++; }
            else if (c.Reason == RhythmCutReason.Accent) { if (AddPunch(offsetMs + c.PresentSec * 1000, 0.55f, ZoomPunchSource.Accent)) n++; }
        }
        return n;
    }

    /// <summary>v17.76 SFX 큐 — 임팩트 = 0.9 · 차칭 = 0.6.</summary>
    public int CollectSfx(ProceduralViralSoundFxSynthesizer sfx, double offsetMs = 0)
    {
        var n = 0;
        for (var i = 0; i < sfx.CueCount; i++)
        {
            ref readonly var q = ref sfx.Cue(i);
            if (q.Kind == ViralSfxKind.Impact) { if (AddPunch(offsetMs + q.TimeMs, 0.9f, ZoomPunchSource.Impact)) n++; }
            else if (q.Kind == ViralSfxKind.Chaching) { if (AddPunch(offsetMs + q.TimeMs, 0.6f, ZoomPunchSource.Impact)) n++; }
        }
        return n;
    }

    /// <summary>v17.52 자막 단어 중 리액션 키워드(돈/불/해골/하트/느낌표 계열) 시작 시각 → 0.75 펀치.</summary>
    public int CollectKeywords(RealtimeWordByWordMotionTypographyEngine typo, double offsetMs = 0)
    {
        var n = 0;
        for (var i = 0; i < typo.WordCount; i++)
            if (ProceduralVectorStickerOverlaySynthesizer.TryClassify(typo.WordText(i), out _) && AddPunch(offsetMs + typo.Word(i).StartMs, 0.75f, ZoomPunchSource.Keyword)) n++;
        return n;
    }

    /// <summary>프레임 배율 — 펀치 프레임 = 피크(즉시 컷) · 유지 · 3차 이즈아웃 복귀 · 그 외 1.0.</summary>
    public float ScaleAt(long frame)
    {
        int lo = 0, hi = _n - 1, k = -1;
        while (lo <= hi) { var m = (lo + hi) >> 1; if (_p[m].Frame <= frame) { k = m; lo = m + 1; } else hi = m - 1; }
        if (k < 0) return 1f;
        var d = frame - _p[k].Frame;
        if (d < HoldFrames) return _p[k].PeakScale;
        if (d >= HoldFrames + ReleaseFrames) return 1f;
        var u = 1f - (d - HoldFrames + 1) / (float)(ReleaseFrames + 1);
        return 1f + (_p[k].PeakScale - 1f) * u * u * u;
    }

    /// <summary>스마트 피벗 크롭(픽셀 · 실수) — 안면 중심 고정 + 헤드룸 가드 + 프레임 클램프.</summary>
    public void CropFor(float scale, int w, int h, out float x0, out float y0, out float cw, out float ch)
    {
        cw = w / scale; ch = h / scale;
        var fx = _face.Cx * w; var fy = _face.Cy * h;
        x0 = fx - fx / scale; y0 = fy - fy / scale;          // 피벗 고정: 안면 중심의 화면 위치 불변
        var top = _face.Y * h - Headroom * ch;
        if (top < y0) { y0 = top; HeadGuardCorrections++; }  // 정수리가 크롭 밖 → 원점 상향
        x0 = Math.Clamp(x0, 0f, w - cw); y0 = Math.Clamp(y0, 0f, h - ch);
        if (_face.Y * h < y0 - 0.5f) HeadClips++;
    }

    /// <summary>프레임 적용(src ≠ dst · 0xAARRGGBB) — 무할당.</summary>
    public float Apply(ReadOnlySpan<uint> src, Span<uint> dst, int w, int h, long frame)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var s = ScaleAt(frame);
        FramesApplied++;
        if (s <= 1.0001f) { src[..(w * h)].CopyTo(dst); }
        else
        {
            CropFor(s, w, h, out var x0, out var y0, out _, out _);
            var step = (int)(65536f / s);
            var fx0 = (int)(x0 * 65536f);
            var maxX = w - 1; var maxY = h - 1;
            for (var y = 0; y < h; y++)
            {
                var fy = (int)((y0 + y / s) * 65536f);
                var iy = Math.Min(fy >> 16, maxY); var iy1 = Math.Min(iy + 1, maxY); var wy = (uint)((fy >> 8) & 255);
                var r0 = src.Slice(iy * w, w); var r1 = src.Slice(iy1 * w, w);
                var o = dst.Slice(y * w, w);
                var fx = fx0;
                for (var x = 0; x < w; x++, fx += step)
                {
                    var ix = Math.Min(fx >> 16, maxX); var ix1 = Math.Min(ix + 1, maxX); var wx = (uint)((fx >> 8) & 255);
                    o[x] = Lerp(Lerp(r0[ix], r0[ix1], wx), Lerp(r1[ix], r1[ix1], wx), wy);
                }
            }
        }
        LastApplyUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return s;
    }

    /// <summary>8-bit 가중 패킹 보간(RB/G 병렬).</summary>
    public static uint Lerp(uint a, uint b, uint w)
    {
        var iw = 256u - w;
        var rb = (((a & 0xFF00FFu) * iw + (b & 0xFF00FFu) * w) >> 8) & 0xFF00FFu;
        var g = (((a & 0xFF00u) * iw + (b & 0xFF00u) * w) >> 8) & 0xFF00u;
        return 0xFF000000u | rb | g;
    }

    public static string SourceName(ZoomPunchSource s) => s switch
    {
        ZoomPunchSource.Hook => "훅",
        ZoomPunchSource.BeatDrop => "드롭",
        ZoomPunchSource.Accent => "악센트",
        ZoomPunchSource.Impact => "SFX",
        ZoomPunchSource.Keyword => "키워드",
        _ => "수동",
    };
}
