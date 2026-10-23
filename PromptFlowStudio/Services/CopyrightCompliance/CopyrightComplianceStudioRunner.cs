using System.Diagnostics;
using System.Globalization;
using PromptFlowStudio.Services.Sound;

namespace PromptFlowStudio.Services.CopyrightCompliance;

/// <summary>
/// [PromptFlow Studio v17.91.0] 🛡️ 저작권 검역 &amp; Content ID 가드 러너(HUD <c>CpyRun</c>).
///   ▸ 데모 클립(20 s · 48 kHz): 절차적 보컬(성문 펄스 + 포먼트 3 개 · 음절/쉼) 전 구간 + 선택한 모의 상용 곡 5~13 s 깔림 + 룸 노이즈.
///   ▸ 검역 1 회(백그라운드 · in-flight 1): <see cref="AcousticFingerprintContentIdAnalyzerEngine"/> 위험도/침해 구간 →
///     <see cref="RoyaltyFreeBgmAutoSubstitutionMixer"/> 원음 제거 + 로열티 프리 루프 치환 → <see cref="PlatformPolicyBrandSafetyGuard"/> 대본 마스킹 + 비프 →
///     −14 LUFS 마스터 → 치환본 재분석. 재분석 &lt; 70 · 클리핑 0 · 잔여 금칙어 0 이면 Safe to Publish.
///   ▸ 원본/치환 비교 모니터 토글 · 100 ms 재생 헤드 · 파형 피크 200 열(원본/치환) · 0.5 s 창 위험도 타임라인.
///   ▸ v17.76 스마트 퍼블리싱(SFX 뱅크) · v17.90 옴니 바이럴 콕핏은 DI 에 있으면 출처로 표시만 하고 상태를 변경하지 않는다.
/// </summary>
public sealed class CopyrightComplianceStudioRunner : IDisposable
{
    public const int ClipSeconds = 20, ClipSamples = ClipSeconds * AcousticFingerprintContentIdAnalyzerEngine.Fs, ClipMs = ClipSeconds * 1000;
    public const int InfringeStartSec = 5, InfringeEndSec = InfringeStartSec + AcousticFingerprintContentIdAnalyzerEngine.RefSeconds, WavePoints = 200, PlayStepMs = 100;
    public const int MaxNotchCollect = RoyaltyFreeBgmAutoSubstitutionMixer.MaxNotches;
    private const long NotifyIntervalMs = 250;
    private const int Fs = AcousticFingerprintContentIdAnalyzerEngine.Fs;

    public const string DefaultScript = "오늘은 나이키 신상 리뷰! 이 소스 맛은 거의 마약 수준… 19금 아니니까 걱정 마세요. 대마도 여행 브이로그도 곧 올라갑니다. 구독 · 좋아요 부탁해요!";

    private readonly CancellationTokenSource _life = new();
    private readonly float[] _original = new float[ClipSamples], _processed = new float[ClipSamples];
    private readonly float[] _peakO = new float[WavePoints], _peakP = new float[WavePoints];
    private readonly float[] _riskBefore = new float[AcousticFingerprintContentIdAnalyzerEngine.MaxWindows], _riskAfter = new float[AcousticFingerprintContentIdAnalyzerEngine.MaxWindows];
    private readonly InfringementSegment[] _segs = new InfringementSegment[AcousticFingerprintContentIdAnalyzerEngine.MaxSegments];
    private readonly PolicyHit[] _hits = new PolicyHit[PlatformPolicyBrandSafetyGuard.MaxHits];
    private readonly float[] _notch = new float[MaxNotchCollect];
    private readonly char[] _maskBuf = new char[PlatformPolicyBrandSafetyGuard.MaxText * 2];
    private readonly bool _linkedSfx, _linkedOmni;
    private System.Threading.Timer? _play;
    private Task? _audit;
    private int _busy, _windows, _hitCount, _playBusy;
    private long _lastNotify;
    private bool _dirty = true;

    public CopyrightComplianceStudioRunner(IServiceProvider sp) : this((sp.GetService(typeof(PromptFlowStudio.Services.SmartPublishing.SmartPublishingSchedulerStudioRunner)) as PromptFlowStudio.Services.SmartPublishing.SmartPublishingSchedulerStudioRunner)?.Sfx)
    {
        _linkedSfx = sp.GetService(typeof(PromptFlowStudio.Services.SmartPublishing.SmartPublishingSchedulerStudioRunner)) is not null;
        _linkedOmni = sp.GetService(typeof(PromptFlowStudio.Services.OmniViral.OmniViralGrowthStudioRunner)) is not null;
    }

    public CopyrightComplianceStudioRunner(PromptFlowStudio.Services.SmartPublishing.ProceduralViralSoundFxSynthesizer? sfx = null)
    {
        Analyzer = new AcousticFingerprintContentIdAnalyzerEngine();
        Mixer = new RoyaltyFreeBgmAutoSubstitutionMixer(sfx);
        Guard = new PlatformPolicyBrandSafetyGuard();
    }

    public event Action? OnChange;
    public AcousticFingerprintContentIdAnalyzerEngine Analyzer { get; }
    public RoyaltyFreeBgmAutoSubstitutionMixer Mixer { get; }
    public PlatformPolicyBrandSafetyGuard Guard { get; }
    public bool Busy => Volatile.Read(ref _busy) != 0;
    /// <summary>깔린 모의 상용 곡(−1 = 없음 · 0~2).</summary>
    public int Track { get; private set; }
    public BgmStyle Style { get; private set; } = BgmStyle.LoFi;
    public string Script { get; private set; } = DefaultScript;
    public string? MaskedScript { get; private set; }
    public bool Audited { get; private set; }
    public bool MonitorSubstituted { get; private set; } = true;
    public bool Playing => _play is not null;
    public int PlayheadMs { get; private set; }
    public float RiskBefore { get; private set; }
    public float RiskAfter { get; private set; }
    public int MatchedTrack { get; private set; } = -1;
    public int SegmentCount { get; private set; }
    public ref readonly InfringementSegment Segment(int i) => ref _segs[i];
    public int HitCount => _hitCount;
    public ref readonly PolicyHit Hit(int i) => ref _hits[i];
    public int ResidualHits { get; private set; }
    public int NotchCount { get; private set; }
    public float NotchHz(int i) => _notch[i];
    public int Windows => _windows;
    public float WindowRiskBefore(int w) => _riskBefore[w];
    public float WindowRiskAfter(int w) => _riskAfter[w];
    public float PeakOriginal(int i) => _peakO[i];
    public float PeakSubstituted(int i) => _peakP[i];
    public long Audits { get; private set; }
    public long Failures { get; private set; }
    public double LastAuditMs { get; private set; }
    public string? LastAction { get; private set; }
    public int LinkedCount => (_linkedSfx ? 1 : 0) + (_linkedOmni ? 1 : 0);
    public bool SafeToPublish => Audited && RiskAfter < AcousticFingerprintContentIdAnalyzerEngine.RiskThreshold && Mixer.ClippedSamples == 0 && ResidualHits == 0;
    public ReadOnlySpan<float> Original => _original;
    public ReadOnlySpan<float> Substituted => _processed;

    public static string TrackName(int t) => t < 0 ? "없음(자체 제작 음원)" : AcousticFingerprintContentIdAnalyzerEngine.TrackNames[t];

    // ══════════════════════ 설정(비핫패스) ══════════════════════

    public void SetTrack(int track)
    {
        Track = Math.Clamp(track, -1, AcousticFingerprintContentIdAnalyzerEngine.RefTracks - 1);
        _dirty = true;
        Invalidate();
        LastAction = "🎵 배경 음원: " + TrackName(Track);
        Raise(true);
    }

    public void SetStyle(BgmStyle style)
    {
        Style = style;
        Invalidate();
        LastAction = "🎼 치환 BGM 스타일: " + RoyaltyFreeBgmAutoSubstitutionMixer.StyleNames[(int)style] + " " + RoyaltyFreeBgmAutoSubstitutionMixer.Bpm[(int)style].ToString(CultureInfo.InvariantCulture) + " BPM";
        Raise(true);
    }

    public void SetScript(string? text)
    {
        Script = text ?? "";
        Invalidate();
        LastAction = "📝 대본 " + Script.Length.ToString(CultureInfo.InvariantCulture) + "자 — 재검역 필요";
        Raise(true);
    }

    public void ToggleMonitor()
    {
        MonitorSubstituted = !MonitorSubstituted;
        LastAction = MonitorSubstituted ? "🎧 모니터: 치환본(로열티 프리 BGM)" : "🎧 모니터: 원본(침해 음원 포함)";
        Raise(true);
    }

    public void TogglePlay()
    {
        if (Playing) { StopPlay(); LastAction = "⏹ 재생 정지 · " + Ms(PlayheadMs); }
        else
        {
            if (PlayheadMs >= ClipMs) PlayheadMs = 0;
            LastAction = "▶ 재생 — " + (MonitorSubstituted ? "치환본" : "원본");
            _play = new System.Threading.Timer(static s => ((CopyrightComplianceStudioRunner)s!).PlayTick(), this, PlayStepMs, PlayStepMs);
        }
        Raise(true);
    }

    public void PlayTick()
    {
        if (Interlocked.Exchange(ref _playBusy, 1) != 0) return;
        try
        {
            PlayheadMs = Math.Min(ClipMs, PlayheadMs + PlayStepMs);
            if (PlayheadMs >= ClipMs) { StopPlay(); LastAction = "⏹ 재생 완료"; Raise(true); }
            else Raise(false);
        }
        catch (Exception ex) { PromptFlowStudio.Program.Log("Copyright.Play", ex); }
        finally { Volatile.Write(ref _playBusy, 0); }
    }

    private void StopPlay()
    {
        var t = Interlocked.Exchange(ref _play, null);
        t?.Dispose();
    }

    private void Invalidate()
    {
        if (Busy) return;
        Audited = false;
    }

    /// <summary>현재 재생 헤드가 침해 구간 안인지(원본 모니터 기준 경고 표시용).</summary>
    public bool PlayheadInSegment()
    {
        for (var i = 0; i < SegmentCount; i++) if (PlayheadMs >= _segs[i].StartMs && PlayheadMs < _segs[i].EndMs) return true;
        return false;
    }

    // ══════════════════════ 검역 ══════════════════════

    /// <summary>검역 1 회(분석 → 치환 → 마스킹/비프 → 마스터 → 재분석) — Safe to Publish 여부.</summary>
    public async Task<bool> AuditAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) { var p = _audit; if (p is not null) await p.ConfigureAwait(false); return false; }
        var ok = false;
        try
        {
            LastAction = "🔎 Content ID 지문 분석 중…";
            Raise(true);
            var script = Script;
            var token = _life.Token;
            var t0 = Stopwatch.GetTimestamp();
            var task = Task.Run(() => RunPipeline(script, token), token);
            _audit = task;
            await task.ConfigureAwait(false);
            LastAuditMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            Audits++;
            Audited = true;
            ok = SafeToPublish;
            LastAction = (ok ? "✅ Safe to Publish — " : "⚠️ 게시 보류 — ") + "위험도 " + F0(RiskBefore) + " → " + F0(RiskAfter) + " · 구간 " + SegmentCount.ToString(CultureInfo.InvariantCulture) +
                         " · " + Mixer.PostLufs.ToString("0.0", CultureInfo.InvariantCulture) + " LUFS · 금칙어 " + _hitCount.ToString(CultureInfo.InvariantCulture) + " 마스킹";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Failures++;
            LastAction = "⚠️ 검역 실패 — " + ex.Message;
            PromptFlowStudio.Program.Log("Copyright.Audit", ex);
        }
        finally { Volatile.Write(ref _busy, 0); Raise(true); }
        return ok;
    }

    private void RunPipeline(string script, CancellationToken token)
    {
        if (_dirty) { BuildClip(_original, Track); _dirty = false; }
        token.ThrowIfCancellationRequested();

        // ① Content ID
        RiskBefore = Analyzer.Analyze(_original);
        MatchedTrack = Analyzer.MatchedTrack;
        _windows = Math.Min(Analyzer.Windows, _riskBefore.Length);
        for (var w = 0; w < _windows; w++) _riskBefore[w] = Analyzer.WindowRisk(w);
        SegmentCount = Analyzer.SegmentCount;
        Analyzer.Segments.CopyTo(_segs);
        var notch = 0;
        for (var s = 0; s < SegmentCount && notch < _notch.Length; s++) notch += Analyzer.CollectSegmentPeakHz(s, _notch.AsSpan(notch));
        NotchCount = notch;
        token.ThrowIfCancellationRequested();

        // ② BGM 치환
        _original.CopyTo(_processed, 0);
        Mixer.Style = Style;
        Mixer.Substitute(_processed, _segs.AsSpan(0, SegmentCount), _notch.AsSpan(0, NotchCount));

        // ③ 브랜드 세이프티 — 대본 1 큐(클립 전체) 마스킹 + 비프
        Guard.ClearBeeps();
        var written = Guard.MaskCue(script, 0, ClipMs, _maskBuf);
        _hitCount = Guard.HitCount;
        for (var i = 0; i < _hitCount; i++) _hits[i] = Guard.Hit(i);
        MaskedScript = new string(_maskBuf, 0, written);
        Guard.RenderBeeps(_processed);
        Guard.Scan(MaskedScript);
        ResidualHits = Guard.MaskTrademarks ? Guard.HitCount : Guard.PolicyHitCount;

        // ④ 마스터 −14 LUFS · 재분석
        Mixer.Normalize(_processed);
        token.ThrowIfCancellationRequested();
        RiskAfter = Analyzer.Analyze(_processed);
        var after = Math.Min(Analyzer.Windows, _riskAfter.Length);
        for (var w = 0; w < _riskAfter.Length; w++) _riskAfter[w] = w < after ? Analyzer.WindowRisk(w) : 0;
        Peaks(_original, _peakO);
        Peaks(_processed, _peakP);
    }

    private static void Peaks(ReadOnlySpan<float> pcm, float[] dst)
    {
        var col = pcm.Length / dst.Length;
        for (var c = 0; c < dst.Length; c++)
        {
            float m = 0;
            var o = c * col;
            for (var i = 0; i < col; i++) { var a = MathF.Abs(pcm[o + i]); if (a > m) m = a; }
            dst[c] = m;
        }
    }

    /// <summary>데모 클립 합성 — 보컬 전 구간 + 모의 상용 곡(트랙 ≥ 0) 5~13 s + 노이즈.</summary>
    public static void BuildClip(Span<float> dst, int track)
    {
        SynthesizeVoice(dst, 0x5EEDu);
        if (track >= 0)
        {
            var music = dst.Slice(InfringeStartSec * Fs, (InfringeEndSec - InfringeStartSec) * Fs);
            var tmp = new float[music.Length];
            AcousticFingerprintContentIdAnalyzerEngine.SynthesizeReference(track, tmp, 0);
            for (var i = 0; i < music.Length; i++) music[i] += tmp[i] * 0.7f;
        }
        var rng = 0xC0FFEEu;
        for (var i = 0; i < dst.Length; i++) { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; dst[i] += ((rng & 0xFFFF) / 32768f - 1f) * 0.004f; }
    }

    /// <summary>절차적 보컬 — 성문 펄스(110~190 Hz 억양) → 포먼트 대역통과 3 개(모음 순환) · 180~320 ms 음절 · 문장 끝 400 ms 쉼.</summary>
    public static void SynthesizeVoice(Span<float> dst, uint seed)
    {
        ReadOnlySpan<float> f1 = stackalloc float[] { 730, 270, 530, 570, 440 };
        ReadOnlySpan<float> f2 = stackalloc float[] { 1090, 2290, 1840, 840, 1020 };
        var rng = seed == 0 ? 1u : seed;
        var n = 0;
        var syll = 0;
        float phase = 0;
        while (n < dst.Length)
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            var len = (int)((0.18f + (rng & 0xFF) / 255f * 0.14f) * Fs);
            var vowel = (int)(rng >> 8) % f1.Length;
            var pitch = 140f + ((rng >> 16) & 0x3F) - 20f;
            var b1 = Biquad.BandPass(f1[vowel], 6, Fs);
            var b2 = Biquad.BandPass(f2[vowel], 8, Fs);
            var b3 = Biquad.BandPass(2600, 10, Fs);
            var end = Math.Min(dst.Length, n + len);
            for (var i = n; i < end; i++)
            {
                var t = (i - n) / (float)len;
                var f = pitch * (1f + 0.08f * MathF.Sin(MathF.PI * t));
                phase += f / Fs;
                if (phase >= 1f) phase -= 1f;
                var src = phase < 0.08f ? 1f - phase / 0.08f : -0.09f;
                var env = MathF.Sin(MathF.PI * t);
                dst[i] = (b1.Process(src) * 1.0f + b2.Process(src) * 0.6f + b3.Process(src) * 0.25f) * env * 0.55f;
            }
            n = end;
            if (++syll % 5 == 0)
            {
                var gap = Math.Min(dst.Length, n + (int)(0.4f * Fs));
                dst[n..gap].Clear();
                n = gap;
            }
        }
    }

    // ══════════════════════ 공용 ══════════════════════

    public void Reset()
    {
        if (Busy) return;
        StopPlay();
        Track = 0;
        Style = BgmStyle.LoFi;
        Script = DefaultScript;
        MaskedScript = null;
        Audited = false;
        MonitorSubstituted = true;
        PlayheadMs = 0;
        RiskBefore = RiskAfter = 0;
        MatchedTrack = -1;
        SegmentCount = _hitCount = ResidualHits = NotchCount = _windows = 0;
        Array.Clear(_peakO); Array.Clear(_peakP);
        Audits = Failures = 0;
        LastAuditMs = 0;
        _dirty = true;
        LastAction = "♻ 초기화";
        Raise(true);
    }

    private static string F0(float v) => v.ToString("0", CultureInfo.InvariantCulture);

    public static string Ms(int ms) => (ms / 1000).ToString(CultureInfo.InvariantCulture) + "." + (ms % 1000).ToString("000", CultureInfo.InvariantCulture) + "s";

    private void Raise(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - Volatile.Read(ref _lastNotify) < NotifyIntervalMs) return;
        Volatile.Write(ref _lastNotify, now);
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Copyright.OnChange", ex); }
    }

    public string Summary() =>
        "검역 " + Audits.ToString(CultureInfo.InvariantCulture) + " · 위험도 " + F0(RiskBefore) + "→" + F0(RiskAfter) + " · 구간 " + SegmentCount.ToString(CultureInfo.InvariantCulture) +
        " · 금칙어 " + _hitCount.ToString(CultureInfo.InvariantCulture) + " · " + (SafeToPublish ? "Safe" : Audited ? "보류" : "미검역");

    public void Dispose()
    {
        _life.Cancel();
        StopPlay();
        try { _audit?.Wait(2000); } catch (AggregateException) { }
        _life.Dispose();
        OnChange = null;
    }
}
