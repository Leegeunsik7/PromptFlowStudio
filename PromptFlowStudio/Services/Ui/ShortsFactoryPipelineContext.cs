using System.Text;
using PromptFlowStudio.Services.KineticBRoll;
using PromptFlowStudio.Services.RetentionHeatmap;

namespace PromptFlowStudio.Services.Ui;

/// <summary>① 절삭 — 원본 링 구간 [Start, End) · 원본 프레임 레이트(milli-fps).</summary>
public struct SourceTrim
{
    public TimeSpan Start, End;
    public int FrameRateMilli;
    public int Revision;
    public readonly TimeSpan Duration => End - Start;
    public readonly long StartFrame => Start.Ticks * FrameRateMilli / (TimeSpan.TicksPerSecond * 1000L);
    public readonly long EndFrame => End.Ticks * FrameRateMilli / (TimeSpan.TicksPerSecond * 1000L);
}

/// <summary>① 리프레임 — 원본(16:9) 위 9:16 바운딩 박스 + Reels UI 안전영역 오프셋(px).</summary>
public struct ReframeConfig
{
    public int SafeOffsetX, SafeOffsetY;
    public int BoxX, BoxY, BoxWidth, BoxHeight;
    public int Revision;
}

/// <summary>② 자막 · 더빙 — 호르모지 프리셋 · 폰트 · 더빙 언어 · 보이스 프리셋 · 절삭 구간 대본 슬라이스(단어 인덱스).</summary>
public struct CaptionDubbingConfig
{
    public HormoziPreset Preset;
    public float FontPx;
    public int LanguageMask, VoicePreset;
    public int WordStart, WordCount;
    public int Revision;
}

/// <summary>③ 리텐션 · 사운드 — SPS 줌 펀치 · 구간 내 이탈 슬럼프 수 · 저작권 치환 BGM 볼륨(‰ · 0 = 원음 유지).</summary>
public struct RetentionAudioConfig
{
    public int PunchCount, SlumpCount;
    public bool BgmSafe;
    public int BgmVolumePermille;
    public int Revision;
}

/// <summary>④ 원자적 fMP4 렌더 스펙 — ①~③ 설정 + 인출 길이 · 썸네일 피크 프레임 · 추정 크기.</summary>
public struct ExportManifest
{
    public SourceTrim Trim;
    public ReframeConfig Reframe;
    public CaptionDubbingConfig Caption;
    public RetentionAudioConfig Retention;
    public int DurationMs, ThumbnailPeakMs;
    public long ThumbnailPeakFrame, EstimatedBytes;
    public int Revision;
}

/// <summary>
/// [PromptFlow Studio v18.0.1] 🏭 숏폼 팩토리 심층 파이프라인 컨텍스트 — <see cref="ShortsFactoryWorkspaceContext"/>(상단 바 값)를
/// 4 단계 엔진 파라미터로 관통(Deep Hydration)한다. ① 구간이 바뀌면 ② 대본 재슬라이싱 · ③ 슬럼프/펀치/리먹서 컷 · ④ 길이/썸네일 피크를
/// 같은 Revision 으로 한 번에 갱신한다. 상태는 고정 필드/배열에 제자리 기록(ref 접근 · 무할당), 대본 문자열은 요청 시에만 만든다.
/// </summary>
public sealed class ShortsFactoryPipelineContext
{
    public const int SourceWidth = 1920, SourceHeight = 1080;
    /// <summary>9:16 박스 폭(1080 × 9 / 16 → 짝수 정렬).</summary>
    public const int BoxWidth = 608;
    /// <summary>Reels UI 상단 안전영역(출력 1920 중 220 px → 원본 1080 스케일).</summary>
    public const int SafeTopPx = 124;
    public const int FrameRateMilli = 30_000;
    public const int WordMs = 400, BucketMs = 500;
    public const int SourceWords = ShortsFactoryWorkspaceContext.SourceMs / WordMs;
    public const int Buckets = ShortsFactoryWorkspaceContext.SourceMs / BucketMs;
    public const int MaxSlumps = 8, SlumpDropPermille = 30, PeakWindowBuckets = 3;
    public const int SafeBgmPermille = 220;
    public const float BaseFontPx = 30f;

    private static readonly string[] Vocab =
    {
        "이", "방법", "하나로", "매출이", "3배", "올랐습니다", "AI", "자동차", "돈", "스마트폰", "폭발", "비밀은",
        "첫", "3초", "훅에", "있습니다", "지금", "바로", "따라", "해보세요", "끝까지", "보면", "반전이", "나옵니다",
    };
    private static readonly int[] SlumpCentersMs = { 8_500, 19_000, 27_500, 36_000, 51_000, 63_500, 78_000, 92_500, 104_000, 113_500 };
    private static readonly short[] Retention = BuildRetention();
    private static readonly short[] Energy = BuildEnergy();

    private ExportManifest _m;
    private readonly int[] _slumpMs = new int[MaxSlumps];
    private readonly int[] _punchMs = new int[ShortsFactoryWorkspaceContext.MaxZoomPunches];
    private readonly RemuxCut[] _cuts = new RemuxCut[1];
    private readonly StringBuilder _sb = new(512);
    private int _sourceRev = -1, _scriptRev = -1;
    private string _script = string.Empty;

    /// <summary>하이드레이션 완료 통지 — 하위 엔진 모달이 구독(호출 무할당 · 예외 격리).</summary>
    public event Action<ShortsFactoryPipelineContext>? Hydrated;

    public ref readonly ExportManifest Manifest => ref _m;
    public ref readonly SourceTrim Trim => ref _m.Trim;
    public ref readonly ReframeConfig Reframe => ref _m.Reframe;
    public ref readonly CaptionDubbingConfig Caption => ref _m.Caption;
    public ref readonly RetentionAudioConfig RetentionAudio => ref _m.Retention;
    public int Revision => _m.Revision;
    public long Hydrations { get; private set; }
    /// <summary>③ 구간 내 이탈 슬럼프 시각(원본 ms · 오름차순).</summary>
    public ReadOnlySpan<int> SlumpPointsMs => _slumpMs.AsSpan(0, _m.Retention.SlumpCount);
    /// <summary>③ SPS 줌 펀치 적용 타임코드(원본 ms · 오름차순) — 슬럼프 직전 우선 배치.</summary>
    public ReadOnlySpan<int> PunchTimecodesMs => _punchMs.AsSpan(0, _m.Retention.PunchCount);
    /// <summary>③ <see cref="NaluSmartReMuxer"/> 에 넘길 절삭 컷(GOP 스냅은 리먹서 몫).</summary>
    public ReadOnlySpan<RemuxCut> RemuxCuts => _cuts;
    public static string Word(int index) => Vocab[(uint)index % (uint)Vocab.Length];
    public static int WordStartMs(int index) => index * WordMs;
    public static int RetentionAt(int ms) => Retention[Math.Clamp(ms / BucketMs, 0, Buckets - 1)];

    /// <summary>워크스페이스 컨텍스트 → 4 단계 엔진 파라미터 관통. Revision 이 같으면 no-op(false).</summary>
    public bool Hydrate(ShortsFactoryWorkspaceContext ws)
    {
        if (ws.Revision == _sourceRev) return false;
        _sourceRev = ws.Revision;
        var rev = _m.Revision + 1;
        int inMs = ws.ClipInMs, outMs = ws.ClipOutMs;

        ref var t = ref _m.Trim;
        t.Start = TimeSpan.FromTicks(inMs * TimeSpan.TicksPerMillisecond);
        t.End = TimeSpan.FromTicks(outMs * TimeSpan.TicksPerMillisecond);
        t.FrameRateMilli = FrameRateMilli;
        t.Revision = rev;

        ref var r = ref _m.Reframe;
        r.BoxWidth = BoxWidth;
        r.BoxHeight = SourceHeight;
        r.BoxX = Math.Clamp(ws.ReframeCenterPermille * SourceWidth / 1000 - BoxWidth / 2, 0, SourceWidth - BoxWidth);
        r.BoxY = 0;
        r.SafeOffsetX = r.BoxX;
        r.SafeOffsetY = SafeTopPx;
        r.Revision = rev;

        ref var c = ref _m.Caption;
        c.Preset = (HormoziPreset)ws.CaptionPreset;
        c.FontPx = BaseFontPx;
        c.LanguageMask = ws.DubLanguageMask;
        c.VoicePreset = ws.DubLanguageMask == 0 ? 0 : System.Numerics.BitOperations.TrailingZeroCount(ws.DubLanguageMask) + 1;
        c.WordStart = (inMs + WordMs - 1) / WordMs;
        c.WordCount = Math.Max(0, Math.Min(SourceWords, outMs / WordMs) - c.WordStart);
        c.Revision = rev;

        ref var a = ref _m.Retention;
        a.SlumpCount = DetectSlumps(inMs, outMs, _slumpMs);
        a.PunchCount = PlacePunches(inMs, outMs, ws.ZoomPunchCount, _slumpMs.AsSpan(0, a.SlumpCount), _punchMs);
        a.BgmSafe = ws.BgmSafe;
        a.BgmVolumePermille = ws.BgmSafe ? SafeBgmPermille : 0;
        a.Revision = rev;
        _cuts[0] = new RemuxCut(inMs, outMs);

        _m.DurationMs = outMs - inMs;
        _m.ThumbnailPeakMs = PeakNear(ws.ThumbnailAtMs, inMs, outMs);
        _m.ThumbnailPeakFrame = (long)_m.ThumbnailPeakMs * FrameRateMilli / 1_000_000;
        _m.EstimatedBytes = (long)_m.DurationMs * ShortsFactoryWorkspaceContext.ExportBytesPerMs;
        _m.Revision = rev;
        Hydrations++;

        try { Hydrated?.Invoke(this); }
        catch (Exception) { /* 구독자 예외 격리 — 파이프라인 상태는 이미 확정 */ }
        return true;
    }

    /// <summary>4 단계 매니페스트가 같은 Revision · 같은 절삭 구간을 가리키는지(단절 0) 검증.</summary>
    public bool IsCoherent(ShortsFactoryWorkspaceContext ws)
    {
        ref readonly var m = ref _m;
        if (_sourceRev != ws.Revision) return false;
        if (m.Trim.Revision != m.Revision || m.Reframe.Revision != m.Revision || m.Caption.Revision != m.Revision || m.Retention.Revision != m.Revision) return false;
        int inMs = ws.ClipInMs, outMs = ws.ClipOutMs;
        if (m.Trim.Start.Ticks != inMs * TimeSpan.TicksPerMillisecond || m.Trim.End.Ticks != outMs * TimeSpan.TicksPerMillisecond) return false;
        if (m.DurationMs != ws.DurationMs || m.EstimatedBytes != (long)ws.DurationMs * ShortsFactoryWorkspaceContext.ExportBytesPerMs) return false;
        if (_cuts[0].SrcStartMs != inMs || _cuts[0].SrcEndMs != outMs) return false;
        if (WordStartMs(m.Caption.WordStart) < inMs || WordStartMs(m.Caption.WordStart + m.Caption.WordCount) > outMs) return false;
        if ((int)m.Caption.Preset != ws.CaptionPreset || m.Caption.LanguageMask != ws.DubLanguageMask || m.Retention.BgmSafe != ws.BgmSafe) return false;
        if (m.Retention.PunchCount != Math.Min(ws.ZoomPunchCount, ShortsFactoryWorkspaceContext.MaxZoomPunches)) return false;
        foreach (var s in SlumpPointsMs) if (s < inMs || s >= outMs) return false;
        foreach (var p in PunchTimecodesMs) if (p < inMs || p >= outMs) return false;
        return m.ThumbnailPeakMs >= inMs && m.ThumbnailPeakMs < outMs;
    }

    /// <summary>② 절삭 구간 대본(비핫패스 · Revision 캐시) — 키네틱 자막 러너 SetScript 입력.</summary>
    public string BuildSlicedScript()
    {
        if (_scriptRev == _m.Revision) return _script;
        _sb.Clear();
        var end = _m.Caption.WordStart + _m.Caption.WordCount;
        for (var i = _m.Caption.WordStart; i < end; i++)
        {
            if (_sb.Length > 0) _sb.Append(' ');
            _sb.Append(Word(i));
        }
        _script = _sb.ToString();
        _scriptRev = _m.Revision;
        return _script;
    }

    /// <summary>구간 내 국소 최소점 중 직전 4 버킷 최대 대비 30 ‰ 이상 떨어진 지점.</summary>
    private static int DetectSlumps(int inMs, int outMs, int[] dst)
    {
        var n = 0;
        var end = Math.Min(Buckets - 1, (outMs - 1) / BucketMs);
        for (var b = Math.Max(1, (inMs + BucketMs - 1) / BucketMs); b < end && n < dst.Length; b++)
        {
            int v = Retention[b];
            if (v >= Retention[b - 1] || v > Retention[b + 1]) continue;
            int peak = Retention[b - 1];
            for (var k = Math.Max(0, b - 4); k < b; k++) peak = Math.Max(peak, Retention[k]);
            if (peak - v >= SlumpDropPermille) dst[n++] = b * BucketMs;
        }
        return n;
    }

    /// <summary>슬럼프 0.5 s 전 우선 → 나머지 균등 배치 → 오름차순 정렬(삽입 · 최대 8).</summary>
    private static int PlacePunches(int inMs, int outMs, int count, ReadOnlySpan<int> slumps, int[] dst)
    {
        var n = Math.Clamp(count, 0, dst.Length);
        var fromSlump = Math.Min(n, slumps.Length);
        for (var i = 0; i < fromSlump; i++) dst[i] = Math.Max(inMs, slumps[i] - BucketMs);
        var rest = n - fromSlump;
        var dur = outMs - inMs;
        for (var k = 0; k < rest; k++) dst[fromSlump + k] = inMs + (int)((long)dur * (k + 1) / (rest + 1));
        for (var i = 1; i < n; i++)
        {
            var x = dst[i];
            var j = i - 1;
            while (j >= 0 && dst[j] > x) { dst[j + 1] = dst[j]; j--; }
            dst[j + 1] = x;
        }
        return n;
    }

    /// <summary>④ 썸네일 지정 위치 ±1.5 s 안 에너지 최대 버킷(구간 밖으로 나가지 않음).</summary>
    private static int PeakNear(int atMs, int inMs, int outMs)
    {
        var lo = Math.Max(inMs / BucketMs, atMs / BucketMs - PeakWindowBuckets);
        var hi = Math.Min((outMs - 1) / BucketMs, atMs / BucketMs + PeakWindowBuckets);
        var best = Math.Clamp(atMs, inMs, outMs - 1) / BucketMs;
        for (var b = lo; b <= hi; b++) if (Energy[b] > Energy[best]) best = b;
        return Math.Clamp(best * BucketMs, inMs, outMs - 1);
    }

    private static short[] BuildRetention()
    {
        var r = new short[Buckets];
        for (var b = 0; b < Buckets; b++)
        {
            var v = 1000 - b * 2;
            foreach (var c in SlumpCentersMs)
            {
                var d = Math.Abs(b - c / BucketMs);
                if (d < 3) v -= 60 - d * 20;
            }
            r[b] = (short)v;
        }
        return r;
    }

    private static short[] BuildEnergy()
    {
        var e = new short[Buckets];
        for (var b = 0; b < Buckets; b++) e[b] = (short)((b * 7919 + 131) % 613 + (b % 17 == 5 ? 400 : 0));
        return e;
    }
}
