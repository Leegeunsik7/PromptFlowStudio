using System.Buffers.Binary;
using PromptFlowStudio.Services.KineticBRoll;
using PromptFlowStudio.Services.LiveClipper;
using PromptFlowStudio.Services.LiveRestream;
using PromptFlowStudio.Services.RetentionHeatmap;
using PromptFlowStudio.Services.ViralExport;
using PromptFlowStudio.Services.VisualPolish;

namespace PromptFlowStudio.Services.Ui;

public enum OneClickRenderStage : byte { Idle, Trim, Caption, Retention, Export, Done, Failed, Cancelled }

/// <summary>
/// [PromptFlow Studio v18.0.2] ⚡ 숏폼 팩토리 원클릭 풀체인 렌더 — <see cref="ShortsFactoryPipelineContext"/> 스냅샷 1 개로 ①~④ 를 단일 비동기 패스로 실행한다.
///   ① 소스 링(120 s · H.264 Annex-B AU + ADTS + 정렬 PCM) → <see cref="ShortsFactoryRetentionRealSliceBinder.Slice"/> 절삭 · 9:16 박스(CropX).
///   ② 절삭 구간 대본 → 타임코드 동기 키네틱 자막 레이어(3GPP 타임드 텍스트 · 첫 단어 = 출력 시간 사상).
///   ③ <see cref="ShortsFactoryRetentionRealSliceBinder.ApplyRetention"/> — 펀치 GOP SPS 크롭 리먹스 · 안전 BGM −30 dB 덕킹 · 템포 WSOLA.
///   ④ <see cref="FastShortsMuxingHardwareExporterEngine"/> fMP4 패키징 + 썸네일 피크 AU 기반 9:16 PNG 동시 추출.
///   ▸ 진행률(단계 0~100 · 전체 0~100)은 필드 기록 + <see cref="OnChange"/> 통지(5 % 단위 · 구독자 예외 격리 · 호출 스레드 비차단).
///   ▸ 소스 = <see cref="AttachCapture"/> 로 연결된 실 캡처 · 미연결 시 첫 렌더에 120 s 리허설 캡처(규격 SPS · AUD · IDR/P · ADTS · 음성형 PCM)를 링에 1 회 적재.
/// </summary>
public sealed class ShortsFactoryOneClickRenderOrchestrator
{
    public const int SourceFps = 30, SourceGop = SourceFps * 2, SourceKbps = 256, ThumbW = 108, ThumbH = 192, BgmLoopSec = 4;
    public const long SourceFrameUs = 1_000_000 / SourceFps, AacFrameUs = ShortsFactoryRetentionRealSliceBinder.AacFrameSamples * 1_000_000L / ShortsFactoryRetentionRealSliceBinder.Fs;
    public const float BounceScale = 1.15f;

    private readonly object _gate = new();
    private readonly int[] _punch = new int[ShortsFactoryWorkspaceContext.MaxZoomPunches];
    private readonly byte[] _rgb = new byte[ThumbW * ThumbH * 3];
    private readonly MemoryStream _png = new(16 * 1024);
    private ShortsFactoryRetentionRealSliceBinder? _binder;
    private short[] _pcm = Array.Empty<short>(), _bgm = Array.Empty<short>();
    private long _originUs;
    private int _busy, _stagePct, _totalPct, _pngLen;
    private OneClickRenderStage _stage;

    public event Action? OnChange;

    public RollingCircularVideoBufferEngine Source { get; } = new(SourceKbps);
    public FastShortsMuxingHardwareExporterEngine Exporter { get; } = new();
    /// <summary>슬라이스 바인더(첫 렌더 시 버퍼 할당 · 이후 재사용).</summary>
    public ShortsFactoryRetentionRealSliceBinder Binder => _binder ??= new ShortsFactoryRetentionRealSliceBinder();
    public bool SourceReady { get; private set; }
    public bool RehearsalSource { get; private set; }
    public bool Busy => Volatile.Read(ref _busy) != 0;
    public OneClickRenderStage Stage => _stage;
    public int StagePercent => Volatile.Read(ref _stagePct);
    public int TotalPercent => Volatile.Read(ref _totalPct);
    public long Renders { get; private set; }
    public long Failures { get; private set; }
    public double LastRenderMs { get; private set; }
    public int LastManifestRevision { get; private set; } = -1;
    public ExportResult LastExport => Exporter.Last;
    public int CaptionWords { get; private set; }
    public int CaptionStartOutMs { get; private set; }
    public string? LastError { get; private set; }
    public ReadOnlyMemory<byte> Output => Exporter.Output;
    /// <summary>소스 PCM(48 kHz 모노 · 0 = 링 원점) · 안전 BGM 베드.</summary>
    public ReadOnlyMemory<short> SourcePcm => _pcm;
    public ReadOnlyMemory<short> BgmBed => _bgm;
    public long SourceOriginUs => _originUs;
    public ReadOnlyMemory<byte> ThumbnailPng => _png.GetBuffer().AsMemory(0, _pngLen);
    /// <summary>UI 미리보기(렌더 완료 시 1 회 생성).</summary>
    public string? ThumbnailDataUrl { get; private set; }

    public static string StageName(OneClickRenderStage s) => s switch
    {
        OneClickRenderStage.Trim => "① 절삭·리프레임",
        OneClickRenderStage.Caption => "② 키네틱 자막",
        OneClickRenderStage.Retention => "③ 줌 펀치·BGM 덕킹",
        OneClickRenderStage.Export => "④ fMP4·썸네일",
        OneClickRenderStage.Done => "✔ 완료",
        OneClickRenderStage.Failed => "🛑 실패",
        OneClickRenderStage.Cancelled => "⏹ 취소",
        _ => "대기",
    };

    /// <summary>실 캡처 연결 — <see cref="Source"/> 링에 기록된 캡처의 PCM(48 kHz 모노 · 0 = 링 원점) · BGM 베드 지정(비핫패스 · 렌더 중 거부).</summary>
    public bool AttachCapture(short[] pcm, short[] bgm, long ringOriginUs)
    {
        lock (_gate)
        {
            if (Busy) return false;
            _pcm = pcm; _bgm = bgm; _originUs = ringOriginUs;
            SourceReady = true; RehearsalSource = false;
            return true;
        }
    }

    /// <summary>원클릭 렌더 — 호출 스레드에서 매니페스트 · 펀치 · 대본을 스냅샷 후 스레드 풀에서 ①~④ 실행. 이미 실행 중이면 false.</summary>
    public async Task<bool> RenderAsync(ShortsFactoryPipelineContext p, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
        try
        {
            var m = p.Manifest;
            var punches = Snapshot(p);
            var script = p.BuildSlicedScript();
            return await Task.Run(() => RenderCore(m, punches, script, ct), CancellationToken.None).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref _busy, 0); Notify(); }
    }

    /// <summary>동기 렌더(테스트 · 배치) — <see cref="RenderAsync"/> 와 같은 경로.</summary>
    public bool Render(ShortsFactoryPipelineContext p, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
        try { return RenderCore(p.Manifest, Snapshot(p), p.BuildSlicedScript(), ct); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private int Snapshot(ShortsFactoryPipelineContext p)
    {
        var src = p.PunchTimecodesMs;
        var n = Math.Min(src.Length, _punch.Length);
        src[..n].CopyTo(_punch);
        return n;
    }

    private bool RenderCore(ExportManifest m, int punches, string script, CancellationToken ct)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastError = null;
        try
        {
            // ① 절삭 & 리프레임 — 소스 링 직전 IDR 슬라이스
            Report(OneClickRenderStage.Trim, 0);
            EnsureSource();
            ct.ThrowIfCancellationRequested();
            var b = Binder;
            if (b.Slice(Source, m.Trim, _originUs) != SliceBindStatus.Bound) return Fail("① 슬라이스 실패: " + b.Status.ToString());
            Report(OneClickRenderStage.Trim, 100);

            // ② 자막 — 절삭 구간 대본 · 첫 단어 원본 시각(클립 원점)
            ct.ThrowIfCancellationRequested();
            Report(OneClickRenderStage.Caption, 0);
            var inMs = (int)(m.Trim.Start.Ticks / TimeSpan.TicksPerMillisecond);
            CaptionWords = FastShortsMuxingHardwareExporterEngine.CountWords(script);
            var capSrcMs = Math.Max(0, ShortsFactoryPipelineContext.WordStartMs(m.Caption.WordStart) - inMs);
            var ov = new ShortsOverlay
            {
                CropX = m.Reframe.BoxX, Caption = 0, Preset = m.Caption.Preset, BounceScale = BounceScale,
                Script = script.Length > 0 ? script : null,
            };
            Report(OneClickRenderStage.Caption, 100);

            // ③ 리텐션 — 펀치 GOP SPS 크롭 리먹스 · −30 dB BGM · 템포
            ct.ThrowIfCancellationRequested();
            Report(OneClickRenderStage.Retention, 0);
            if (b.ApplyRetention(_pcm, _bgm, _punch.AsSpan(0, punches), m.Retention.BgmSafe) != SliceBindStatus.Bound) return Fail("③ 리먹스 실패: " + b.Status.ToString());
            CaptionStartOutMs = b.ReEditor.OutMsAt(capSrcMs);
            ov.SpikeMs = CaptionStartOutMs + 1000;   // 익스포터 자막 시작 = SpikeMs − 1 s
            Report(OneClickRenderStage.Retention, 100);

            // ④ fMP4 + 썸네일
            ct.ThrowIfCancellationRequested();
            Report(OneClickRenderStage.Export, 0);
            var mux = b.Mux;
            var es = b.RemuxedAnnexB;
            Exporter.BeginClip();
            for (var i = 0; i < mux.OutCount; i++)
            {
                ref readonly var au = ref mux.OutAu(i);
                Exporter.AddVideo(es.Slice(au.Offset, au.Length), ShortsFactoryRetentionRealSliceBinder.TicksToUs(au.Pts));
                if ((i & 63) == 63) { Report(OneClickRenderStage.Export, 70 * i / mux.OutCount); ct.ThrowIfCancellationRequested(); }
            }
            for (var i = 0; i < b.KeptAudioFrames; i++) Exporter.AddAudio(b.AudioFrame(i), b.AudioOutPtsUs(i));
            if (Exporter.FinishClip(ov, m.Revision) != ExportStatus.Ready) return Fail("④ fMP4 패키징 실패");
            Report(OneClickRenderStage.Export, 85);
            WriteThumbnail(m, b, inMs);
            LastManifestRevision = m.Revision;
            Renders++;
            LastRenderMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            Report(OneClickRenderStage.Done, 100);
            return true;
        }
        catch (OperationCanceledException)
        {
            Report(OneClickRenderStage.Cancelled, 0);
            return false;
        }
        catch (Exception ex)
        {
            return Fail(ex.GetType().Name + ": " + ex.Message);
        }
    }

    private bool Fail(string why)
    {
        LastError = why;
        Failures++;
        Report(OneClickRenderStage.Failed, 0);
        return false;
    }

    private void Report(OneClickRenderStage s, int pct)
    {
        var total = s switch
        {
            OneClickRenderStage.Done => 100,
            OneClickRenderStage.Failed or OneClickRenderStage.Cancelled => Volatile.Read(ref _totalPct),
            _ => ((int)s - 1) * 25 + pct / 4,
        };
        var changed = s != _stage || pct - Volatile.Read(ref _stagePct) >= 5 || pct < Volatile.Read(ref _stagePct);
        _stage = s;
        Volatile.Write(ref _stagePct, pct);
        Volatile.Write(ref _totalPct, total);
        if (changed) Notify();
    }

    private void Notify()
    {
        try { OnChange?.Invoke(); }
        catch (Exception) { /* 구독자 예외 격리 — 렌더 상태는 이미 기록 */ }
    }

    // ══════════════════════ ④ 썸네일(피크 AU · 비핫패스) ══════════════════════

    /// <summary>썸네일 피크 시각의 출력 AU 바이트(FNV)로 배경 톤 · 펀치 GOP 이면 1.15× 비네트 · 프리셋 색 자막 바 → 108×192 PNG.</summary>
    private void WriteThumbnail(in ExportManifest m, ShortsFactoryRetentionRealSliceBinder b, int inMs)
    {
        var mux = b.Mux;
        var peakTicks = (long)b.ReEditor.OutMsAt(Math.Max(0, m.ThumbnailPeakMs - inMs)) * NaluSmartReMuxer.Clock / 1000;
        var best = 0;
        for (var i = 1; i < mux.OutCount; i++)
            if (Math.Abs(mux.OutAu(i).Pts - peakTicks) < Math.Abs(mux.OutAu(best).Pts - peakTicks)) best = i;
        ref readonly var au = ref mux.OutAu(best);
        var h = (uint)RtmpWire.Fnv(b.RemuxedAnnexB.Slice(au.Offset, au.Length));
        var punched = false;
        for (var i = 0; i < b.ReEditor.PunchCount; i++)
            if (Math.Abs(b.ReEditor.Punch(i).AtMs - (m.ThumbnailPeakMs - inMs)) < 2_000) { punched = true; break; }
        var accent = WordByWordKineticSubtitleEngine.PresetColors[(int)m.Caption.Preset][0];
        int r0 = (int)(h & 0x7F) + 40, g0 = (int)((h >> 8) & 0x7F) + 40, b0 = (int)((h >> 16) & 0x7F) + 60;
        for (var y = 0; y < ThumbH; y++)
        {
            for (var x = 0; x < ThumbW; x++)
            {
                var o = (y * ThumbW + x) * 3;
                var dx = (x - ThumbW / 2) / (float)ThumbW; var dy = (y - ThumbH / 2) / (float)ThumbH;
                var v = 1.25f - (dx * dx + dy * dy) * (punched ? 3.2f : 2f);   // 고CTR 대비 + 펀치 비네트
                var shade = 0.55f + 0.45f * y / ThumbH;
                _rgb[o] = (byte)Math.Clamp(r0 * v * shade, 0f, 255f);
                _rgb[o + 1] = (byte)Math.Clamp(g0 * v * shade, 0f, 255f);
                _rgb[o + 2] = (byte)Math.Clamp(b0 * v * (1.2f - shade * 0.4f), 0f, 255f);
                if (y >= ThumbH * 62 / 100 && y < ThumbH * 70 / 100 && x >= 10 && x < ThumbW - 10)
                {
                    _rgb[o] = (byte)(accent >> 16); _rgb[o + 1] = (byte)(accent >> 8); _rgb[o + 2] = (byte)accent;
                }
            }
        }
        _png.SetLength(0);
        PeakEmotion3dgsThumbnailSynthesizer.EncodePng(_rgb, ThumbW, ThumbH, _png);
        _pngLen = (int)_png.Length;
        ThumbnailDataUrl = "data:image/png;base64," + Convert.ToBase64String(_png.GetBuffer(), 0, _pngLen);
    }

    // ══════════════════════ 소스 링(비핫패스 · 1 회) ══════════════════════

    private void EnsureSource()
    {
        lock (_gate)
        {
            if (SourceReady) return;
            PrimeRehearsalSource();
            SourceReady = true; RehearsalSource = true;
        }
    }

    /// <summary>120 s 리허설 캡처 → 링(비디오 30 fps · GOP 2 s · IDR 마다 SPS/PPS · ADTS 48 kHz) + 음성형 PCM(문장 사이 데드에어) + 4 s BGM 베드.</summary>
    private void PrimeRehearsalSource()
    {
        const int Sec = ShortsFactoryWorkspaceContext.SourceMs / 1000, Fs = ShortsFactoryRetentionRealSliceBinder.Fs;
        Source.Reset();
        _originUs = 0;
        var sps = SyntheticAnnexBStream.BuildH264Sps(SourceFps);
        var au = new byte[2048];
        var adts = new byte[96];
        uint seed = 0x5F1C3D7Bu;
        long vUs = 0, aUs = 0;
        int frame = 0;
        while (vUs < Sec * 1_000_000L || aUs < Sec * 1_000_000L)
        {
            if (vUs <= aUs && vUs < Sec * 1_000_000L)
            {
                var n = EmitAccessUnit(au, sps, frame % SourceGop == 0, ref seed);
                Source.WriteVideo(au.AsSpan(0, n), vUs);
                frame++; vUs = frame * SourceFrameUs;
            }
            else
            {
                const int raw = 48;
                var len = 7 + raw;
                adts[0] = 0xFF; adts[1] = 0xF1; adts[2] = 0x4C; adts[3] = (byte)(0x80 | ((len >> 11) & 3)); adts[4] = (byte)(len >> 3); adts[5] = (byte)(((len & 7) << 5) | 0x1F); adts[6] = 0xFC;
                for (var i = 7; i < len; i++) { var v = Next(ref seed); adts[i] = v == 0 ? (byte)0x21 : v; }
                Source.WriteAudio(adts.AsSpan(0, len), aUs);
                aUs += AacFrameUs;
            }
        }

        // 음성형 PCM: 음절(170 ms 유성 · 70 ms 간격) · 7~9 s 마다 1.4 s 데드에어 · 180~260 Hz 기본 주파수
        var pcm = new short[Sec * Fs];
        for (var i = 0; i < pcm.Length; i++)
        {
            var ms = i / (Fs / 1000);
            var sentence = ms % 8_200;
            var dead = sentence >= 6_800;
            var syl = ms % 240 < 170;
            if (dead || !syl) continue;
            var f0 = 180f + 80f * ((ms / 240) % 5) / 4f;
            var env = MathF.Sin(MathF.PI * (ms % 240) / 170f);
            pcm[i] = (short)(9_000f * env * (MathF.Sin(2f * MathF.PI * f0 * i / Fs) + 0.35f * MathF.Sin(4f * MathF.PI * f0 * i / Fs)));
        }
        var bgm = new short[BgmLoopSec * Fs];
        for (var i = 0; i < bgm.Length; i++)
        {
            var t = i / (float)Fs;
            bgm[i] = (short)(8_000f * (MathF.Sin(2f * MathF.PI * 220f * t) + MathF.Sin(2f * MathF.PI * 277.18f * t) + MathF.Sin(2f * MathF.PI * 329.63f * t)) / 3f);
        }
        _pcm = pcm; _bgm = bgm;
    }

    /// <summary>H.264 AU — AUD · (IDR: SPS/PPS) · 슬라이스(first_mb = 0 · 페이로드 0 바이트 없음 = 에뮬레이션 방지 불요 · RBSP 정지 비트).</summary>
    private static int EmitAccessUnit(byte[] a, byte[] sps, bool idr, ref uint seed)
    {
        var p = Nal(a, 0, 0x09); a[p++] = 0xF0;
        if (idr)
        {
            p = Nal(a, p, 0x67); sps.CopyTo(a, p); p += sps.Length;
            p = Nal(a, p, 0x68); a[p++] = 0xEE; a[p++] = 0x3C; a[p++] = 0x80;
        }
        p = Nal(a, p, idr ? (byte)0x65 : (byte)0x41);
        var size = idr ? 900 : 160 + (int)(Next(ref seed) % 80);
        a[p++] = (byte)(0x80 | (Next(ref seed) & 0x7F));
        for (var i = 1; i < size - 1; i++) { var v = Next(ref seed); a[p++] = v == 0 ? (byte)0x55 : v; }
        a[p++] = 0x80;
        return p;
    }

    private static int Nal(byte[] a, int p, byte header)
    {
        BinaryPrimitives.WriteUInt32BigEndian(a.AsSpan(p), 1u);
        a[p + 4] = header;
        return p + 5;
    }

    private static byte Next(ref uint r) { r ^= r << 13; r ^= r >> 17; r ^= r << 5; return (byte)(r >> 24); }
}
