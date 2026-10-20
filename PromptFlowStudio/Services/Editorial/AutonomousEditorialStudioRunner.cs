using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Editorial;

/// <summary>
/// [PromptFlow Studio v16.79.0] ✂️ AI 자율 편집 &amp; 몽타주 조립 스튜디오 러너(30 Hz · 오디오 블록 1600 · <see cref="TickOnce"/> 무할당).
/// 합성 멀티캠 테이크(60 s): 2인 대화(0~20 s 드라마 · 화자 교대) → 액션(20~40 s · 격한 동작/타격음) → 결말(40~60 s) · 6 앵글(와이드 · 클로즈 A/B · 오버숄더 · 탑 크레인(라인 위 중립) · 역방향 와이드(180° 반대편)).
/// 앵글별 64×36 루마 렌더 + 라발리에 오디오 → 편집 엔진 · BGM 124 BPM(킥 · 하이햇 · 베이스) → 리듬 조율기. 테이크 종료 시 V2 몽타주(액션 구간) · A1 BGM 조립 → EDL/FCPXML/AAF.
/// </summary>
public sealed class AutonomousEditorialStudioRunner : IDisposable
{
    public const int Hz = 30, SampleRate = 48000, AudioBlock = SampleRate / Hz, W = 64, H = 36, Angles = 6, TakeSeconds = 60, TakeFrames = TakeSeconds * Hz;
    public const float Bpm = 124f;
    public const int TrackV1 = 0, TrackV2 = 1, TrackA1 = 2, BgmSource = Angles;

    private readonly object _gate = new();
    private readonly AutonomousMultiCameraEditorialEngine _engine;
    private readonly NeuralRhythmMontageSynthesizer _rhythm;
    private readonly NleTimelineFormatExportGuard _export;
    private readonly EditTimeline _tl = new(Hz);
    private readonly byte[][] _luma = new byte[Angles][];
    private readonly float[] _voice = new float[AudioBlock], _angleAudio = new float[AudioBlock], _bgm = new float[AudioBlock];
    private readonly float[] _bgmLevel = new float[TakeFrames];
    private readonly Vector3[] _camPos = new Vector3[Angles], _camF = new Vector3[Angles], _camR = new Vector3[Angles], _camU = new Vector3[Angles];
    private readonly float[] _focal = new float[Angles];
    private CancellationTokenSource? _cts;
    private long _sample;
    private double _vPhase, _bassPhase;
    private ulong _rng = 0xED17_1679UL;
    private Vector3 _a, _b;
    private int _frame;
    private bool _finalized;

    public AutonomousEditorialStudioRunner(IServiceProvider sp)
        : this((AutonomousMultiCameraEditorialEngine?)sp.GetService(typeof(AutonomousMultiCameraEditorialEngine)) ?? new AutonomousMultiCameraEditorialEngine(),
               (NeuralRhythmMontageSynthesizer?)sp.GetService(typeof(NeuralRhythmMontageSynthesizer)) ?? new NeuralRhythmMontageSynthesizer(SampleRate),
               (NleTimelineFormatExportGuard?)sp.GetService(typeof(NleTimelineFormatExportGuard)) ?? new NleTimelineFormatExportGuard()) { }

    public AutonomousEditorialStudioRunner(AutonomousMultiCameraEditorialEngine engine, NeuralRhythmMontageSynthesizer rhythm, NleTimelineFormatExportGuard export)
    {
        _engine = engine; _rhythm = rhythm; _export = export;
        _engine.Rhythm = rhythm;
        for (var i = 0; i < Angles; i++) _luma[i] = new byte[W * H];
        NewTake();
    }

    public event Action? OnChange;

    public AutonomousMultiCameraEditorialEngine Engine => _engine;
    public NeuralRhythmMontageSynthesizer Rhythm => _rhythm;
    public NleTimelineFormatExportGuard Export => _export;
    public EditTimeline Timeline => _tl;
    public bool IsRunning => _cts is not null;
    public int Frame { get { lock (_gate) return _frame; } }
    public bool Finalized => _finalized;
    public ReadOnlySpan<byte> Luma(int angle) => _luma[angle];
    public float BgmLevel(int frame) => (uint)frame < TakeFrames ? _bgmLevel[frame] : 0f;
    public string? LastAction { get; private set; }

    public static string SectionName(float t) => t < 20f ? "드라마 · 대화" : t < 40f ? "액션" : "결말";

    /// <summary>제어 평면: 새 테이크(타임라인 · 카메라 · 감정 곡선 초기화).</summary>
    public void NewTake()
    {
        lock (_gate)
        {
            _tl.ResetAll();
            _tl.Title = "PromptFlow Autonomous Edit";
            _tl.AddTrack("V1 프로그램", EditTrackKind.Video);
            _tl.AddTrack("V2 몽타주", EditTrackKind.Video);
            _tl.AddTrack("A1 BGM", EditTrackKind.Audio);
            Span<Vector3> pos = stackalloc Vector3[] { new(0, 1.7f, 5f), new(1.3f, 1.6f, 2.4f), new(-1.3f, 1.6f, 2.4f), new(1.5f, 1.7f, 0.9f), new(0, 5.5f, 0f), new(0, 1.7f, -5f) };
            ReadOnlySpan<int> subj = stackalloc int[] { -1, 0, 1, 0, -1, -1 };
            ReadOnlySpan<ShotSize> size = stackalloc ShotSize[] { ShotSize.Wide, ShotSize.Close, ShotSize.Close, ShotSize.OverShoulder, ShotSize.Medium, ShotSize.Wide };
            string[] names = { "와이드 마스터", "A 클로즈업", "B 클로즈업", "A 오버숄더", "탑 크레인", "역방향 와이드" };
            string[] reels = { "WIDE", "CU_A", "CU_B", "OTS_A", "CRANE", "REVWIDE" };
            _a = new Vector3(-0.8f, 1.6f, 0); _b = new Vector3(0.8f, 1.6f, 0);
            _engine.Configure(Angles, W, H, Hz, _tl, TrackV1);
            for (var i = 0; i < Angles; i++)
            {
                var target = subj[i] == 0 ? _a : subj[i] == 1 ? _b : new Vector3(0, 1.3f, 0);
                _engine.SetCamera(i, pos[i], target, subj[i], size[i], names[i]);
                _tl.AddSource(names[i], reels[i]);
                _camPos[i] = pos[i];
                _camF[i] = Vector3.Normalize(target - pos[i]);
                var up = MathF.Abs(_camF[i].Y) > 0.95f ? -Vector3.UnitZ : Vector3.UnitY;
                _camR[i] = Vector3.Normalize(Vector3.Cross(_camF[i], up)); _camU[i] = Vector3.Cross(_camR[i], _camF[i]);
                _focal[i] = size[i] switch { ShotSize.Close => 120f, ShotSize.OverShoulder => 80f, ShotSize.Medium => 55f, _ => 45f };
            }
            _tl.AddSource("BGM " + Bpm.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " BPM", "BGM");
            _rhythm.ClearCurve();
            _rhythm.AddCurveKey(0, 0.15f); _rhythm.AddCurveKey(18, 0.25f); _rhythm.AddCurveKey(21, 0.9f); _rhythm.AddCurveKey(38, 0.85f); _rhythm.AddCurveKey(42, 0.5f); _rhythm.AddCurveKey(60, 0.35f);
            _frame = 0; _finalized = false; _sample = 0;
            Array.Clear(_bgmLevel);
        }
        LastAction = "🎬 새 테이크";
        Raise();
    }

    public void SetBeatSync(bool on) { _engine.BeatSync = on; LastAction = on ? "🥁 비트 동기 컷 ON" : "🥁 비트 동기 컷 OFF"; Raise(); }

    /// <summary>1프레임(무할당). 테이크 종료 시 몽타주/BGM 조립 후 루프 정지.</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            if (_finalized) return;
            using var guard = ZeroGcRegionScope.Enter("Editorial.Tick");
            var t = _frame / (float)Hz;
            var action = t >= 20f && t < 40f;

            // 인물: 대화 = 미세 움직임 · 액션 = 격한 이동
            var amp = action ? 0.35f : 0.03f;
            _a = new Vector3(-0.8f + amp * MathF.Sin(t * 6.1f), 1.6f + amp * 0.4f * MathF.Sin(t * 9.3f), amp * 0.5f * MathF.Cos(t * 5.2f));
            _b = new Vector3(0.8f + amp * MathF.Sin(t * 5.3f + 1f), 1.6f + amp * 0.4f * MathF.Cos(t * 8.1f), amp * 0.5f * MathF.Sin(t * 4.7f));
            var speaker = action ? -1 : ((int)(t / 3.7f) & 1);
            var listenerTurn = speaker >= 0 && (t % 3.7f) > 3f; // 화자 말미 = 반응
            _engine.SetSubject(0, _a, Vector3.Normalize(_b - _a + new Vector3(0, 0, speaker == 0 ? 0.6f : 0.2f)));
            _engine.SetSubject(1, _b, Vector3.Normalize(_a - _b + new Vector3(0, 0, speaker == 1 ? 0.6f : 0.2f)));
            _engine.SetSpeaker(listenerTurn ? 1 - speaker : speaker);

            for (var i = 0; i < Angles; i++) { Render(i, t, action); _engine.SubmitLuma(i, _luma[i]); }

            // 오디오: 화자 음성(라발리에 · 카메라 거리 감쇠) + 액션 타격음 · BGM
            Synthesize(t, speaker, action);
            var spk = speaker == 0 ? _a : speaker == 1 ? _b : 0.5f * (_a + _b);
            for (var i = 0; i < Angles; i++)
            {
                var g = 1f / (1f + Vector3.Distance(_camPos[i], spk));
                for (var k = 0; k < AudioBlock; k++) _angleAudio[k] = _voice[k] * g;
                _engine.SubmitAudio(i, _angleAudio);
            }
            _rhythm.ProcessAudio(_bgm);
            var (_, ss) = AutonomousMultiCameraEditorialEngine.PeakAndEnergy(_bgm);
            _bgmLevel[_frame] = (float)Math.Sqrt(ss / AudioBlock);

            _engine.Step();
            _frame++;
            if (_frame >= TakeFrames) FinalizeTake();
        }
    }

    private void Render(int i, float t, bool action)
    {
        var img = _luma[i];
        // 배경: 카메라 방향별 그라디언트(앵글 구분)
        for (var y = 0; y < H; y++)
        {
            var bg = (byte)(30 + y * 2 + i * 6);
            img.AsSpan(y * W, W).Fill(bg);
        }
        Blob(i, img, _a, 0.22f, 210);
        Blob(i, img, _b, 0.22f, 170);
        if (action)
        {
            // 팔/소품(빠른 궤적)
            Blob(i, img, _a + new Vector3(0.4f * MathF.Cos(t * 11f), -0.3f + 0.3f * MathF.Sin(t * 13f), 0.2f), 0.1f, 250);
            Blob(i, img, _b + new Vector3(-0.4f * MathF.Sin(t * 12f), -0.3f + 0.3f * MathF.Cos(t * 10f), 0.2f), 0.1f, 240);
        }
    }

    private void Blob(int i, byte[] img, Vector3 p, float radius, byte value)
    {
        var d = p - _camPos[i];
        var z = Vector3.Dot(d, _camF[i]);
        if (z < 0.1f) return;
        var cx = W * 0.5f + _focal[i] * Vector3.Dot(d, _camR[i]) / z;
        var cy = H * 0.5f - _focal[i] * Vector3.Dot(d, _camU[i]) / z;
        var r = _focal[i] * radius / z;
        var r2 = r * r;
        int x0 = Math.Max(0, (int)(cx - r)), x1 = Math.Min(W - 1, (int)(cx + r)), y0 = Math.Max(0, (int)(cy - r)), y1 = Math.Min(H - 1, (int)(cy + r));
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var dx = x - cx; var dy = y - cy; var q = (dx * dx + dy * dy) / r2;
                if (q < 1f) img[y * W + x] = (byte)Math.Max(img[y * W + x], value * (1f - q * 0.6f));
            }
    }

    private void Synthesize(float t, int speaker, bool action)
    {
        var beat = 60.0 / Bpm;
        for (var k = 0; k < AudioBlock; k++)
        {
            var s = (_sample + k) / (double)SampleRate;
            // 음성(성문 버즈 · 음절 AM)
            var f0 = speaker == 0 ? 130.0 : 190.0;
            _vPhase += f0 / SampleRate; if (_vPhase >= 1) _vPhase -= 1;
            var syl = Math.Max(0, Math.Sin(2 * Math.PI * 4.3 * s));
            var v = speaker >= 0 ? (float)((_vPhase < 0.3 ? 1 : -0.3) * syl * 0.3) : 0f;
            if (action && NextFloat() < 0.0004f) v += 0.9f; // 타격음 트랜지언트
            _voice[k] = v;
            // BGM: 킥(비트) · 하이햇(8분 뒷박) · 베이스
            var bt = s % beat; var ht = (s + beat * 0.5) % beat;
            var kick = (float)(Math.Sin(2 * Math.PI * (50 + 80 * Math.Exp(-bt / 0.03)) * bt) * Math.Exp(-bt / 0.12));
            var hat = (NextFloat() * 2f - 1f) * (float)Math.Exp(-ht / 0.02) * 0.15f;
            _bassPhase += 55.0 / SampleRate; if (_bassPhase >= 1) _bassPhase -= 1;
            var bass = (float)Math.Sin(2 * Math.PI * _bassPhase) * 0.12f;
            _bgm[k] = 0.6f * kick + hat + bass;
        }
        _sample += AudioBlock;
    }

    /// <summary>테이크 마감: V2 몽타주(액션 20~40 s · 앵글 풀) + A1 BGM 전 구간.</summary>
    private void FinalizeTake()
    {
        _finalized = true;
        _tl.RemoveTrackClips(TrackV2); _tl.RemoveTrackClips(TrackA1);
        _rhythm.ClearPool();
        for (var i = 0; i < Angles - 1; i++) // 역방향 와이드 제외(180°)
        {
            var inten = _engine.Angle(i).Size switch { ShotSize.Close => 0.9f, ShotSize.OverShoulder => 0.65f, ShotSize.Medium => 0.5f, _ => 0.2f };
            _rhythm.AddPoolClip(i, 20 * Hz, 20 * Hz, inten);
            _rhythm.AddPoolClip(i, 25 * Hz, 15 * Hz, MathF.Min(1f, inten + 0.1f));
        }
        _rhythm.Assemble(_tl, TrackV2, 20 * Hz, 40 * Hz);
        _tl.Add(TrackA1, BgmSource, 0, TakeFrames, CutReason.Music);
        LastAction = "🏁 테이크 완료 — 컷 " + _engine.CutCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " · 몽타주 " + _tl.ClipsOn(TrackV2).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>제어 평면: 포맷 내보내기 → 바이트 사본(UI 다운로드용). 실패 시 null.</summary>
    public byte[]? ExportBytes(NleFormat fmt)
    {
        lock (_gate)
        {
            if (!_finalized) { _tl.RemoveTrackClips(TrackV2); _tl.RemoveTrackClips(TrackA1); if (_frame > 0) _tl.Add(TrackA1, BgmSource, 0, _frame, CutReason.Music); }
            var ok = _export.Export(_tl, fmt);
            LastAction = ok ? "📤 " + fmt + " " + _export.LastBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " B" : "⚠ 내보내기 거부 · " + _export.LastIssue;
            return ok ? _export.Output.ToArray() : null;
        }
    }

    private float NextFloat()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 40) / (float)(1 << 24);
    }

    public void Start()
    {
        if (_cts is not null) return;
        if (_finalized) NewTake();
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 자율 편집 가동";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / Hz));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { TickOnce(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Editorial.Tick", ex); }
                if (_finalized) { Stop(); break; }
                if ((Frame & 3) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Editorial.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
