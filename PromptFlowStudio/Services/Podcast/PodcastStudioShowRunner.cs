using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PromptFlowStudio.Services.Podcast;

/// <summary>
/// [PromptFlow Studio v16.49.0] ▶ 1-Click 자율 팟캐스트 녹화/송출 러너.
///   ▸ 30fps 틱: 오케스트레이터 발화권 → 좌석별 합성 음성(배음 · 음절 엔벨로프 · 반박 초반 크로스토크 · 유머 웃음 리액션) → 액티브 스피커 탐지 → 자동 컷 → 비디오 합성 · 시청자 개입 틱 · 데모 시청자 채팅.
///   ▸ 녹화: 세션 패키지(대본 · 컷 리스트 · 하이라이트 JSON + SRT 자막) 내보내기 — Utf8JsonWriter.
///   ▸ 전용 스레드 · 틱 예외 격리 · 정지 시 Join · 버퍼 1회 할당.
/// </summary>
public sealed class PodcastStudioShowRunner : IDisposable
{
    public const int Fps = 30, FrameW = 640, FrameH = 360, MaxBlock = 2048;

    private readonly MultiAgentTalkshowConversationOrchestrator _talk;
    private readonly AutonomousPodcastVideoMultiCamEngine _cam;
    private readonly InteractiveAudienceAgendaInterventionEngine _audience;
    private readonly float[] _pcm = new float[MaxBlock];
    private readonly double[] _phase = new double[AutonomousPodcastVideoMultiCamEngine.MaxSeats];
    private readonly byte[] _frame = new byte[FrameW * FrameH * 4];
    private readonly object _frameGate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Thread? _thread;
    private volatile bool _run;
    private uint _noise = 2463534242;
    private int _demoIdx;
    private double _startMs, _nextDemoChat;
    private long _frames;

    public PodcastStudioShowRunner(IServiceProvider sp)
        : this((MultiAgentTalkshowConversationOrchestrator)sp.GetService(typeof(MultiAgentTalkshowConversationOrchestrator))!,
               (AutonomousPodcastVideoMultiCamEngine)sp.GetService(typeof(AutonomousPodcastVideoMultiCamEngine))!,
               (InteractiveAudienceAgendaInterventionEngine)sp.GetService(typeof(InteractiveAudienceAgendaInterventionEngine))!) { Guard = sp.GetService(typeof(PromptFlowStudio.Services.Hardening.ZeroAllocHotPathProfilingGuard)) as PromptFlowStudio.Services.Hardening.ZeroAllocHotPathProfilingGuard; Guard?.SetBudget(GuardSite, 32 * 1024); }   // v16.51.0 루프 프로브(이벤트성 발화/토스트 할당 허용 예산)

    /// <summary>v16.51.0 핫패스 프로파일링 가드(선택) · 사이트 인덱스.</summary>
    public PromptFlowStudio.Services.Hardening.ZeroAllocHotPathProfilingGuard? Guard { get; set; }
    public const int GuardSite = 49;

    public PodcastStudioShowRunner(MultiAgentTalkshowConversationOrchestrator talk, AutonomousPodcastVideoMultiCamEngine cam, InteractiveAudienceAgendaInterventionEngine audience)
    {
        _talk = talk; _cam = cam; _audience = audience;
        _talk.OnLine += OnLine;
        _talk.OnEnded += OnEnded;
    }

    public event Action? OnFrame;

    public bool IsLive => _thread is not null;
    public bool Recording { get; set; } = true;
    public bool DemoAudience { get; set; } = true;
    public double DemoChatEverySec { get; set; } = 25;
    public long Frames => Interlocked.Read(ref _frames);
    public double NowMs => _clock.Elapsed.TotalMilliseconds;
    public double StartMs => _startMs;
    public double ElapsedMs => IsLive ? NowMs - _startMs : 0;
    public string? LastExport { get; private set; }

    public void CopyFrame(Span<byte> rgba) { lock (_frameGate) _frame.CopyTo(rgba); }

    /// <summary>방송 준비(좌석 색 · 상태 초기화) + 오케스트레이터 시작.</summary>
    public void Prepare(double nowMs)
    {
        Span<uint> colors = stackalloc uint[AutonomousPodcastVideoMultiCamEngine.MaxSeats];
        var n = _talk.AgentCount;
        for (var i = 0; i < n; i++) colors[i] = _talk.Agent(i).Color;
        _cam.SetSeats(colors[..n]);
        Array.Clear(_phase);
        _talk.Start(nowMs);
        _startMs = nowMs; _demoIdx = 0; _nextDemoChat = nowMs + DemoChatEverySec * 1000;
    }

    public void Start()
    {
        if (_thread is not null) return;
        Prepare(NowMs);
        _run = true;
        var th = new Thread(Loop) { IsBackground = true, Name = "PFS.Podcast" };
        _thread = th;
        th.Start();
    }

    public void Stop()
    {
        var th = Interlocked.Exchange(ref _thread, null);
        if (th is null) return;
        _run = false;
        if (Thread.CurrentThread != th) th.Join(1500);
        _talk.Stop(NowMs);
    }

    private void OnEnded() { _run = false; Interlocked.Exchange(ref _thread, null); }

    private void OnLine(TalkLine l)
    {
        if (l.Kind == TalkLineKind.Joke) _cam.MarkHighlight(HighlightReason.Laughter, l.Speaker, l.TimeMs, 2000, l.DurationMs + 1500);
    }

    /// <summary>러너 1틱(테스트/수동 구동 가능): 발화 → 오디오 → 탐지 → 컷 → 합성.</summary>
    public void Step(double dtSec, double nowMs)
    {
        if (DemoAudience && nowMs >= _nextDemoChat)
        {
            var c = InteractiveAudienceAgendaInterventionEngine.DemoChat[_demoIdx++ % InteractiveAudienceAgendaInterventionEngine.DemoChat.Length];
            _audience.Ingest(c.Viewer, c.Text, nowMs);
            _nextDemoChat = nowMs + DemoChatEverySec * 1000;
        }
        _audience.Tick(nowMs);
        _talk.Tick(nowMs);
        var n = Math.Min(MaxBlock, Math.Max(32, (int)(AutonomousPodcastVideoMultiCamEngine.SampleRate * dtSec)));
        var block = _pcm.AsSpan(0, n);
        var speaker = _talk.SpeakerAt(nowMs);
        var cur = _talk.Current;
        var prog = _talk.LineProgress(nowMs);
        for (var s = 0; s < _talk.AgentCount; s++)
        {
            float amp = 0;
            if (s == speaker) amp = cur is { Kind: TalkLineKind.Rebuttal or TalkLineKind.Joke } ? 0.42f : 0.26f;
            else if (cur is not null && s == cur.Addressee && speaker >= 0 && prog < 0.12 && cur.Kind is TalkLineKind.Rebuttal) amp = 0.2f;     // 반박 초반 크로스토크
            else if (cur is not null && speaker >= 0 && cur.Kind == TalkLineKind.Joke && prog > 0.75 && s != speaker && s > 0) amp = 0.12f;     // 웃음 리액션
            Synthesize(block, s, amp, nowMs);
            _cam.Analyze(s, block, nowMs);
        }
        _cam.Direct(nowMs);
        lock (_frameGate) _cam.Render(_frame, FrameW, FrameH, FrameW * 4, nowMs);
        Interlocked.Increment(ref _frames);
    }

    /// <summary>좌석 음성 합성: 기본 주파수 배음 4개 × 음절 엔벨로프(≈4.5Hz) + 저레벨 잡음. 무할당.</summary>
    private void Synthesize(Span<float> dst, int seat, float amp, double nowMs)
    {
        var f0 = _talk.Agent(seat).PitchHz;
        var ph = _phase[seat];
        var step = 2 * Math.PI * f0 / AutonomousPodcastVideoMultiCamEngine.SampleRate;
        var t0 = nowMs / 1000;
        for (var i = 0; i < dst.Length; i++)
        {
            _noise ^= _noise << 13; _noise ^= _noise >> 17; _noise ^= _noise << 5;
            var nz = ((_noise & 0xFFFF) / 32768f - 1f) * 0.0015f;
            if (amp <= 0) { dst[i] = nz; continue; }
            var t = t0 + i / (double)AutonomousPodcastVideoMultiCamEngine.SampleRate;
            var env = 0.55f + 0.45f * MathF.Sin((float)(2 * Math.PI * 4.5 * t + seat));
            var p = ph + i * step;
            var v = MathF.Sin((float)p) + 0.5f * MathF.Sin((float)(2 * p)) + 0.3f * MathF.Sin((float)(3 * p)) + 0.15f * MathF.Sin((float)(4 * p));
            dst[i] = amp * env * v * 0.6f + nz;
        }
        _phase[seat] = (ph + dst.Length * step) % (2 * Math.PI);
    }

    private void Loop()
    {
        var period = 1000.0 / Fps;
        var last = NowMs;
        while (_run)
        {
            var now = NowMs;
            try
            {
                var probe = Guard?.Begin(GuardSite);
                Step((now - last) / 1000, now);
                if (probe is { } pr) Guard!.End(pr);
                OnFrame?.Invoke();
            }
            catch (Exception ex) { PromptFlowStudio.Program.Log("Podcast.Tick", ex); }
            last = now;
            var wait = period - (NowMs - now);
            if (wait > 1) Thread.Sleep((int)wait);
        }
    }

    // ══════════════════════ 세션 내보내기 ══════════════════════

    public static string Timecode(double ms, char sep = ',')
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return ((int)t.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":" + t.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + t.Seconds.ToString("00", CultureInfo.InvariantCulture) + sep + t.Milliseconds.ToString("000", CultureInfo.InvariantCulture);
    }

    /// <summary>세션 패키지(podcast_session.json · podcast.srt) 기록. 반환 = JSON 경로.</summary>
    public string ExportSession(string dir, double originMs)
    {
        Directory.CreateDirectory(dir);
        var lines = _talk.History;
        var clips = _cam.Clips;
        Span<CamCut> cutBuf = new CamCut[AutonomousPodcastVideoMultiCamEngine.CutCap];
        var nc = _cam.CopyCuts(cutBuf);
        var json = Path.Combine(dir, "podcast_session.json");
        using (var fs = File.Create(json))
        using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("version", "16.49.0");
            w.WriteString("topic", _talk.Topic);
            w.WriteString("format", _talk.Format.ToString());
            w.WriteStartArray("agenda"); foreach (var a in _talk.Agenda) w.WriteStringValue(a); w.WriteEndArray();
            w.WriteStartArray("lineup");
            for (var i = 0; i < _talk.AgentCount; i++)
            {
                var a = _talk.Agent(i);
                w.WriteStartObject();
                w.WriteString("name", a.Name); w.WriteString("title", a.Title);
                w.WriteString("persona", MultiAgentTalkshowConversationOrchestrator.PersonaLabel(a.Persona)); w.WriteString("stance", MultiAgentTalkshowConversationOrchestrator.StanceLabel(a.Stance));
                w.WriteNumber("turns", a.TurnsTaken); w.WriteNumber("spokenSec", Math.Round(a.SpokenSeconds, 2));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("transcript");
            foreach (var l in lines)
            {
                w.WriteStartObject();
                w.WriteString("tc", Timecode(l.TimeMs - originMs, '.')); w.WriteString("kind", l.Kind.ToString());
                w.WriteString("speaker", _talk.Agent(l.Speaker).Name); w.WriteString("text", l.Text); w.WriteString("source", l.Source);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("cuts");
            for (var i = nc - 1; i >= 0; i--)
            {
                var c = cutBuf[i];
                w.WriteStartObject(); w.WriteString("tc", Timecode(c.TimeMs - originMs, '.')); w.WriteString("shot", c.Shot.ToString()); w.WriteNumber("a", c.A); w.WriteNumber("b", c.B); w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("highlights");
            foreach (var c in clips)
            {
                w.WriteStartObject();
                w.WriteString("in", Timecode(c.StartMs - originMs, '.')); w.WriteString("out", Timecode(c.EndMs - originMs, '.'));
                w.WriteString("reason", c.Reason.ToString()); w.WriteNumber("speaker", c.Speaker); w.WriteNumber("score", Math.Round(c.Score, 3));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        var srt = new StringBuilder();
        var k = 0;
        foreach (var l in lines)
        {
            srt.Append((++k).ToString(CultureInfo.InvariantCulture)).Append('\n')
               .Append(Timecode(l.TimeMs - originMs)).Append(" --> ").Append(Timecode(l.TimeMs + l.DurationMs - originMs)).Append('\n')
               .Append(_talk.Agent(l.Speaker).Name).Append(": ").Append(l.Text).Append("\n\n");
        }
        File.WriteAllText(Path.Combine(dir, "podcast.srt"), srt.ToString(), new UTF8Encoding(false));
        LastExport = json;
        return json;
    }

    public void Dispose()
    {
        Stop();
        _talk.OnLine -= OnLine;
        _talk.OnEnded -= OnEnded;
    }
}
