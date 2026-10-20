using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;

namespace PromptFlowStudio.Services.ContentSecurity;

/// <summary>
/// [PromptFlow Studio v16.90.0] 🛡️ AI 콘텐츠 보안 &amp; 디지털 저작권 스튜디오 러너(60 Hz · 비디오 30 fps 256×144 · 오디오 7.1.4 1024/틱 · <see cref="TickOnce"/> 무할당).
/// 장면 렌더 → 워터마크(시청자 세션 ID) → 봉인 → 배포 경로 시나리오(원본 · 재압축 · 노이즈 · 얼굴 교체 · 스플라이스 · 무단 재촬영 · 재압축+교체) →
/// 진위 검증(판정 · 레이더 · 타일 오버레이) → 유출 ID 역추적 → 원장 차단 · 재생/판매 이벤트 → 1 s 배치 정산(해시 체인).
/// </summary>
public sealed class ContentSecurityStudioRunner : IDisposable
{
    public const int Hz = 60, W = 256, H = 144, AudioN = NeuralInvisibleWatermarkEncoderEngine.AudioN, Scenarios = 7, ScenarioFrames = 60;
    public const string DefaultContract =
        "자산: 밤의 추적자 | PFS-A-1690\n지분: 스튜디오=40, 감독=20, 작가=15, 배우 미나=15, 음악=10\n회수: 스튜디오=2000000\n" +
        "요율: 스트리밍=12.5 /재생, 다운로드=990 /건, 극장=55%, 방송=2000000 /회\n지역: KR, US, JP, FR, BR";

    private readonly object _gate = new();
    private readonly NeuralInvisibleWatermarkEncoderEngine _wm;
    private readonly DeepfakeProtectionContentAuthenticityGuard _guard;
    private readonly EnterpriseDigitalRightsMonetizationSynthesizer _rights;
    public readonly float[] Original = new float[W * H * 3], Marked = new float[W * H * 3], Received = new float[W * H * 3];
    public readonly byte[] OriginalBytes = new byte[W * H * 3], ReceivedBytes = new byte[W * H * 3], DiffBytes = new byte[W * H * 3];
    private readonly float[] _other = new float[W * H * 3], _bed = new float[12 * AudioN], _rx = new float[12 * AudioN];
    private readonly FrameManifest _manifest = new();
    private readonly GaussianScene _splats = new(512);
    private readonly AttackKind[] _plan = { AttackKind.None, AttackKind.Recompress, AttackKind.Noise, AttackKind.FaceSwap, AttackKind.Splice, AttackKind.None, AttackKind.Recompress };
    private CancellationTokenSource? _cts;
    private long _frame, _videoFrame, _audioFrame;
    private uint _rng = 0x1690u;
    private int _scenario, _audioWindow;
    private bool _leakScenario;

    public ContentSecurityStudioRunner(IServiceProvider sp)
        : this((NeuralInvisibleWatermarkEncoderEngine?)sp.GetService(typeof(NeuralInvisibleWatermarkEncoderEngine)) ?? new NeuralInvisibleWatermarkEncoderEngine(),
               (EnterpriseDigitalRightsMonetizationSynthesizer?)sp.GetService(typeof(EnterpriseDigitalRightsMonetizationSynthesizer)) ?? new EnterpriseDigitalRightsMonetizationSynthesizer(),
               (DeepfakeProtectionContentAuthenticityGuard?)sp.GetService(typeof(DeepfakeProtectionContentAuthenticityGuard))) { }

    public ContentSecurityStudioRunner(NeuralInvisibleWatermarkEncoderEngine wm, EnterpriseDigitalRightsMonetizationSynthesizer rights, DeepfakeProtectionContentAuthenticityGuard? guard = null)
    {
        _wm = wm; _rights = rights; _guard = guard ?? new DeepfakeProtectionContentAuthenticityGuard(wm);
        ContractText = DefaultContract;
        _rights.Load(DefaultContract);
        for (var i = 0; i < 400; i++)
            _splats.Add(new System.Numerics.Vector3(i % 20 * 0.1f, i / 20 * 0.1f, 0f), System.Numerics.Quaternion.Identity, new System.Numerics.Vector3(0.02f), 0.9f, new System.Numerics.Vector3(0.5f + 0.3f * MathF.Sin(i)));
        NewSession();
    }

    public event Action? OnChange;

    public NeuralInvisibleWatermarkEncoderEngine Watermark => _wm;
    public DeepfakeProtectionContentAuthenticityGuard Guard => _guard;
    public EnterpriseDigitalRightsMonetizationSynthesizer Rights => _rights;
    public FrameManifest Manifest => _manifest;
    public GaussianScene Splats => _splats;
    public bool IsRunning => _cts is not null;
    public long Frame { get { lock (_gate) return _frame; } }
    public long VideoFrames => _videoFrame;
    public bool AutoScenario { get; set; } = true;
    public AttackKind ManualAttack { get; set; } = AttackKind.None;
    public int RecompressQuality { get; set; } = 70;
    public AttackKind CurrentAttack { get; private set; }
    public bool LeakCopy => _leakScenario;
    public string ContractText { get; private set; }
    public WatermarkDetection AudioDetection { get; private set; }
    public WatermarkDetection SplatDetection { get; private set; }
    public WatermarkPayload Viewer { get; private set; }
    public WatermarkPayload LastTraced { get; private set; }
    public long LeaksTraced { get; private set; }
    public long Tampers { get; private set; }
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }

    public bool LoadContract(string? text)
    {
        var ok = _rights.Load(text);
        if (ok) ContractText = text!;
        LastAction = _rights.ParseMessage;
        Raise();
        return ok;
    }

    private uint Rand() { _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5; return _rng; }

    /// <summary>새 시청 세션(제어 평면): 디바이스/사용자/세션 ID 갱신 → 페이로드 · 3DGS 재내장.</summary>
    public void NewSession()
    {
        lock (_gate)
        {
            Viewer = new WatermarkPayload(0x100000 + Rand() % 0xEFFFF, 0x200000 + Rand() % 0xDFFFF, (ushort)Rand());
            _wm.SetPayload(Viewer);
            _wm.EncodeSplats(_splats);
            SplatDetection = _wm.DetectSplats(_splats, Viewer);
            _wm.ResetAudioDetect(); _audioWindow = 0;
        }
        LastAction = "🔑 새 시청 세션 — 디바이스 " + Viewer.Device.ToString("X6", System.Globalization.CultureInfo.InvariantCulture) + " · 사용자 " + Viewer.User.ToString("X6", System.Globalization.CultureInfo.InvariantCulture);
        Raise();
    }

    /// <summary>장면(무할당): 배경 그라데이션 + 질감 + 이동 인물(얼굴 타원) + 자막 판.</summary>
    private static void RenderScene(float[] dst, float t, int seed)
    {
        var cx = 128f + 50f * MathF.Sin(0.4f * t);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var h = (uint)((x * 73856093) ^ (y * 19349663) ^ seed) * 2654435761u;
                var n = ((h >> 8) & 0xFF) / 255f - 0.5f;
                var v = 0.35f + 0.25f * y / H + 0.12f * MathF.Sin((x + t * 30f) * 0.06f) * MathF.Cos(y * 0.07f) + 0.05f * n;
                float r = v * 0.8f, g = v * 0.9f, b = v + 0.08f;
                var dx = (x - cx) / 26f; var dy = (y - 62f) / 34f;
                if (dx * dx + dy * dy < 1f) { var s = 0.75f - 0.1f * dy + 0.03f * n; r = s; g = s * 0.78f; b = s * 0.66f; }
                var bx = (x - cx) / 40f; var by = (y - 120f) / 30f;
                if (bx * bx + by * by < 1f && y > 96) { r = 0.15f + 0.05f * n; g = 0.18f; b = 0.32f; }
                var i = (y * W + x) * 3; dst[i] = Math.Clamp(r, 0f, 1f); dst[i + 1] = Math.Clamp(g, 0f, 1f); dst[i + 2] = Math.Clamp(b, 0f, 1f);
            }
    }

    private void RenderAudio(long frame)
    {
        for (var c = 0; c < 12; c++)
        {
            var o = c * AudioN;
            for (var n = 0; n < AudioN; n++)
            {
                var t = (frame * AudioN + n) / 48000f;
                _bed[o + n] = c == 3 ? 0.2f * MathF.Sin(MathF.Tau * 50f * t)
                    : 0.12f * MathF.Sin(MathF.Tau * (196f + 24f * c) * t) + 0.06f * MathF.Sin(MathF.Tau * (1960f + 37f * c) * t + c) + 0.03f * MathF.Sin(MathF.Tau * 3100f * t * (1f + 0.01f * c));
            }
        }
    }

    /// <summary>1틱(무할당): 오디오 매 틱 · 비디오 2틱 · 정산 60틱.</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("ContentSecurity.Tick");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            // ── 오디오: 위상 내장 → 배포 열화(이득 · 노이즈) → 6 프레임 창 검출 ──
            RenderAudio(_audioFrame);
            _wm.EncodeAudio(_bed, AudioN, _audioFrame);
            for (var i = 0; i < _rx.Length; i++) { var r = Rand(); _rx[i] = _bed[i] * 0.8f + ((r & 0xFFFF) / 65535f - 0.5f) * 0.004f; }
            _wm.AccumulateAudio(_rx, AudioN, _audioFrame);
            if (++_audioWindow >= 6) { AudioDetection = _wm.DecideAudio(Viewer); _wm.ResetAudioDetect(); _audioWindow = 0; }
            _audioFrame++;
            // ── 비디오 ──
            if ((_frame & 1) == 0) VideoStep();
            // ── 저작권 이벤트 · 정산 ──
            RightsStep();
            if (_frame % Hz == Hz - 1) _rights.Settle(_frame * 1000 / Hz);
            _frame++;
            LastTickMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    private void VideoStep()
    {
        var t = _videoFrame / 30f;
        _scenario = (int)(_videoFrame / ScenarioFrames % Scenarios);
        CurrentAttack = AutoScenario ? _plan[_scenario] : ManualAttack;
        _leakScenario = AutoScenario && _scenario == 5;
        RenderScene(Original, t, 1);
        Original.AsSpan().CopyTo(Marked);
        _wm.EncodeFrame(Marked, W, H);
        _guard.Seal(Marked, W, H, _videoFrame, _manifest);
        Marked.AsSpan().CopyTo(Received);
        switch (CurrentAttack)
        {
            case AttackKind.Recompress: ContentAttackSimulator.Recompress(Received, W, H, RecompressQuality); break;
            case AttackKind.Noise: ContentAttackSimulator.Noise(Received, 0.012f, (uint)_videoFrame + 7); break;
            case AttackKind.Brightness: ContentAttackSimulator.Brightness(Received, 1.08f, 0.02f); break;
            case AttackKind.FaceSwap:
                {
                    var cx = (int)(128f + 50f * MathF.Sin(0.4f * t)) / 32 * 32;
                    ContentAttackSimulator.FaceSwap(Received, W, Math.Clamp(cx - 32, 0, W - 64), 24, 64, 72);
                    break;
                }
            case AttackKind.Splice:
                RenderScene(_other, t + 7f, 99);
                ContentAttackSimulator.Splice(Received, _other, W, 0, 0, 192, 96, 64, 48);
                break;
        }
        if (_leakScenario) ContentAttackSimulator.Recompress(Received, W, H, 80); // 유출 사본(재인코딩 배포)
        var v = _guard.Verify(Received, W, H, _manifest);
        if (v == AuthVerdict.Tampered) Tampers++;
        var det = _guard.Detection;
        if (_leakScenario && det.Valid)
        {
            LastTraced = det.Payload;
            if (_rights.BlockDevice(det.Payload.Id)) { LeaksTraced++; LastAction = "🚨 유출 역추적 — 디바이스 " + det.Payload.Device.ToString("X6", System.Globalization.CultureInfo.InvariantCulture) + " 차단"; }
        }
        // 미리보기 바이트(오버레이 포함)
        for (var i = 0; i < Original.Length; i++)
        {
            OriginalBytes[i] = (byte)(Original[i] * 255f + 0.5f);
            DiffBytes[i] = (byte)Math.Clamp(128f + (Marked[i] - Original[i]) * 255f * 12f, 0f, 255f);
        }
        _guard.Overlay(Received, W, H);
        for (var i = 0; i < Received.Length; i++) ReceivedBytes[i] = (byte)(Math.Clamp(Received[i], 0f, 1f) * 255f + 0.5f);
        _videoFrame++;
    }

    private void RightsStep()
    {
        if (!_rights.Valid) return;
        var nT = _rights.TerritoryCount;
        // 스트리밍 재생 3~8건/틱(정상 디바이스) · 차단 디바이스 재생(유출본 시청)
        var plays = 3 + (int)(Rand() % 6);
        for (var i = 0; i < plays; i++) _rights.RecordPlay(0, (int)(Rand() % (uint)nT), 0x1000 + Rand() % 5000);
        if (LeaksTraced > 0) _rights.RecordPlay(0, (int)(Rand() % (uint)nT), LastTraced.Id, 2);
        if (Rand() % 20 == 0) _rights.RecordPlay(1, (int)(Rand() % (uint)nT), 0x2000 + Rand() % 100);
        if (Rand() % 30 == 0 && _rights.ChannelCount > 2) _rights.RecordSale(2, (int)(Rand() % (uint)nT), (12000 + Rand() % 4000) * EnterpriseDigitalRightsMonetizationSynthesizer.Milli);
        if (_frame % (Hz * 10) == 0 && _rights.ChannelCount > 3) _rights.RecordPlay(3, 0, 0x3000);
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 보안 관제 가동";
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
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("ContentSecurity.Tick", ex); }
                if ((Frame & 7) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("ContentSecurity.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
