using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Media;

namespace PromptFlowStudio.Services.Broadcast;

/// <summary>v16.25.0 송출 플랫폼.</summary>
public enum LivePlatform { YouTube, Chzzk, TikTok, Twitch, CustomRtmp, Srt }

/// <summary>v16.25.0 송출 세션 상태.</summary>
public enum LiveLoopState { Idle, Starting, Live, Reconnecting, Stopped, Failed }

/// <summary>v16.25.0 플랫폼 프리셋 — 기본 인제스트 서버(사용자 수정 가능 · TikTok 은 계정별 서버라 공란).</summary>
public sealed record LivePlatformPreset(LivePlatform Platform, string Icon, string Label, string DefaultServer, bool KeyRequired);

/// <summary>v16.25.0 송출 대상 1건(서버 + 스트림 키 · 키는 메모리 전용 · 로그/상태에는 마스킹).</summary>
public sealed record LiveTarget(LivePlatform Platform, string Server, string StreamKey);

/// <summary>v16.25.0 송출 설정.</summary>
public sealed record LiveStreamConfig(
    IReadOnlyList<LiveTarget> Targets,
    int Width = 1920, int Height = 1080, int Fps = 30, int VideoKbps = 4500, int AudioKbps = 160,
    bool Nvenc = true, bool AvatarOverlay = true, bool VoiceMix = true);

/// <summary>v16.25.0 ffmpeg -progress 집계(값 형식).</summary>
public readonly record struct LiveProgress(long Frame, double Fps, double BitrateKbps, double Speed, long DroppedFrames);

/// <summary>v16.25.0 시작 결과.</summary>
public sealed record LiveStartResult(bool Ok, string Message);

/// <summary>
/// [PromptFlowStudio] v16.25.0 📺 24시간 심리스 루프 스트리밍 엔진 — 비디오 플레이리스트를 YouTube Live · 치지직 · TikTok · Twitch · RTMP/SRT 로 무한 연속 송출.
///   ▸ 심리스: ffconcat 플레이리스트 + <c>-stream_loop -1</c> 단일 프로세스(파일 경계 재접속 0) · scale/pad/fps 정규화로 해상도 혼재 허용 · CBR + 고정 GOP(2초).
///   ▸ 동시 송출: 대상 2개 이상이면 tee 먹서(<c>onfail=ignore</c> — 한 플랫폼 장애가 다른 송출을 끊지 않음).
///   ▸ AI 아바타: <see cref="RealtimeAvatarPuppeteerService"/> PCM 을 stdin(pipe:0)으로 받아 플레이리스트 음성과 사이드체인 덕킹 믹스 · drawtext <c>reload=1</c> 로 아바타 얼굴/자막 오버레이.
///   ▸ 24/7 워치독: 비정상 종료 시 지수 백오프(1→60초) 자동 재시작 · 5분 이상 안정 송출하면 백오프 리셋 · 연속 20회 실패 시 중단 · NVENC 초기화 실패 감지 시 libx264 폴백.
///   ▸ 스트림 키는 하드코딩/저장 없이 세션 메모리에만 보관, 상태·로그·예외 메시지에는 <see cref="MaskUrl"/> 적용.
/// </summary>
public sealed class AiLiveLoopStreamerService : IDisposable
{
    public const int MaxPlaylist = 200;
    public const int MaxConsecutiveFailures = 20;
    public const int MaxBackoffSeconds = 60;
    public static readonly TimeSpan StableResetAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan NvencProbeWindow = TimeSpan.FromSeconds(8);

    public static readonly IReadOnlyList<LivePlatformPreset> Presets = new[]
    {
        new LivePlatformPreset(LivePlatform.YouTube, "▶️", "YouTube Live", "rtmp://a.rtmp.youtube.com/live2", true),
        new LivePlatformPreset(LivePlatform.Chzzk, "🟢", "치지직(CHZZK)", "rtmp://global-rtmp.lip2.navercorp.com:8080/relay", true),
        new LivePlatformPreset(LivePlatform.TikTok, "🎵", "TikTok LIVE", "", true),
        new LivePlatformPreset(LivePlatform.Twitch, "🟣", "Twitch", "rtmp://live.twitch.tv/app", true),
        new LivePlatformPreset(LivePlatform.CustomRtmp, "📡", "사용자 RTMP", "", false),
        new LivePlatformPreset(LivePlatform.Srt, "⚡", "SRT", "", false),
    };

    private static readonly string[] VideoExts = { ".mp4", ".mov", ".mkv", ".webm", ".m4v", ".ts", ".flv" };

    private readonly Func<string?> _ffmpeg;
    private readonly RealtimeAvatarPuppeteerService? _avatar;
    private readonly string _workDir;
    private readonly object _gate = new();
    private readonly List<string> _playlist = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Process? _proc;
    private LiveStreamConfig? _config;
    private bool _nvencFailed;
    private long _lastRaiseTicks;
    private bool _disposed;

    // progress 누적(파서 상태 · stdout 리더 스레드 전용).
    private long _pFrame, _pDrop;
    private double _pFps, _pKbps, _pSpeed;

    public AiLiveLoopStreamerService(IVideoMergeService merge, RealtimeAvatarPuppeteerService avatar)
        : this(() => merge.ResolvedFfmpegPath, avatar, null) { }

    /// <param name="ffmpeg">ffmpeg 경로 공급자(테스트 주입).</param>
    /// <param name="workDir">ffconcat 목록 폴더(null = %TEMP%/PromptFlowStudio/live).</param>
    public AiLiveLoopStreamerService(Func<string?> ffmpeg, RealtimeAvatarPuppeteerService? avatar, string? workDir)
    {
        _ffmpeg = ffmpeg;
        _avatar = avatar;
        _workDir = workDir ?? Path.Combine(Path.GetTempPath(), "PromptFlowStudio", "live");
    }

    /// <summary>상태/플레이리스트/진행(1초 스로틀) 변경 방송.</summary>
    public event Action? OnChange;

    public LiveLoopState State { get; private set; } = LiveLoopState.Idle;
    public bool IsLive => State is LiveLoopState.Live or LiveLoopState.Starting or LiveLoopState.Reconnecting;
    public DateTimeOffset? SessionStartedAt { get; private set; }
    public DateTimeOffset? ProcessStartedAt { get; private set; }
    public TimeSpan Uptime => SessionStartedAt is { } s && IsLive ? DateTimeOffset.Now - s : TimeSpan.Zero;
    public int Restarts { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public LiveProgress Progress { get; private set; }
    public string LastMessage { get; private set; } = "대기";
    public string? LastError { get; private set; }
    public string TargetsLabel { get; private set; } = "-";
    public bool UsingNvenc { get; private set; }
    public int TotalSessions { get; private set; }

    public IReadOnlyList<string> Playlist { get { lock (_gate) return _playlist.ToArray(); } }
    public int PlaylistCount { get { lock (_gate) return _playlist.Count; } }

    // ══════════════════════ 순수 헬퍼(테스트 대상) ══════════════════════

    public static LivePlatformPreset PresetOf(LivePlatform p)
    {
        foreach (var x in Presets) if (x.Platform == p) return x;
        return Presets[0];
    }

    /// <summary>서버 + 키 → 최종 URL. RTMP 는 <c>server/key</c>, SRT 는 <c>streamid=key</c> 쿼리 부착(이미 있으면 유지).</summary>
    public static string ComposeUrl(LiveTarget t)
    {
        var server = (t.Server ?? "").Trim();
        var key = (t.StreamKey ?? "").Trim();
        if (t.Platform == LivePlatform.Srt || server.StartsWith("srt://", StringComparison.OrdinalIgnoreCase))
        {
            if (key.Length == 0 || server.Contains("streamid=", StringComparison.OrdinalIgnoreCase)) return server;
            return server + (server.Contains('?') ? "&" : "?") + "streamid=" + Uri.EscapeDataString(key);
        }
        if (key.Length == 0) return server;
        return server.TrimEnd('/') + "/" + key.TrimStart('/');
    }

    /// <summary>대상 형식 검증(프로토콜 접두 · 필수 키 · 금지 문자 — tee 구분자 '|' 및 공백/제어문자).</summary>
    public static bool IsValidTarget(LiveTarget t, out string reason)
    {
        var url = ComposeUrl(t);
        var proto = url.StartsWith("srt://", StringComparison.OrdinalIgnoreCase) ? "srt" : "rtmp";
        if (!LiveBroadcastStreamEngine.IsValidTarget(proto, url)) { reason = PresetOf(t.Platform).Label + " 서버 주소가 유효하지 않습니다(rtmp(s):// 또는 srt://)."; return false; }
        if (PresetOf(t.Platform).KeyRequired && string.IsNullOrWhiteSpace(t.StreamKey)) { reason = PresetOf(t.Platform).Label + " 스트림 키가 필요합니다."; return false; }
        foreach (var ch in url)
            if (ch == '|' || ch == '[' || ch == ']' || char.IsWhiteSpace(ch) || char.IsControl(ch)) { reason = "주소/키에 허용되지 않는 문자가 있습니다."; return false; }
        reason = "";
        return true;
    }

    /// <summary>URL 의 스트림 키/streamid 를 마스킹(로그·UI 노출용).</summary>
    public static string MaskUrl(string? url)
    {
        var u = url ?? "";
        var sid = u.IndexOf("streamid=", StringComparison.OrdinalIgnoreCase);
        if (sid >= 0) return u.Substring(0, sid + 9) + "****";
        var scheme = u.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return u.Length == 0 ? "" : "****";
        var slash = u.LastIndexOf('/');
        var firstPath = u.IndexOf('/', scheme + 3);
        if (slash <= scheme + 2 || slash == firstPath) return u;   // 키 경로 없음
        return u.Substring(0, slash + 1) + "****";
    }

    /// <summary>지수 백오프(초) — 1,2,4,…, 상한 60.</summary>
    public static int BackoffSeconds(int attempt) => attempt <= 0 ? 1 : (int)Math.Min(MaxBackoffSeconds, 1L << Math.Min(attempt, 6));

    /// <summary>ffconcat 목록(작은따옴표 이스케이프 · 슬래시 정규화).</summary>
    public static string BuildConcatList(IReadOnlyList<string> files)
    {
        var sb = new StringBuilder("ffconcat version 1.0\n");
        foreach (var f in files)
        {
            var p = Path.GetFullPath(f).Replace('\\', '/').Replace("'", "'\\''");
            sb.Append("file '").Append(p).Append("'\n");
        }
        return sb.ToString();
    }

    /// <summary>ffmpeg 송출 인자(순수) — 입력(ffconcat 루프 + 선택 PCM pipe) · 필터 그래프 · 인코딩 · 단일/tee 출력.</summary>
    public static List<string> BuildArgs(LiveStreamConfig cfg, string concatPath, bool nvenc, string? avatarText, string? captionText, string? fontPath)
    {
        var inv = CultureInfo.InvariantCulture;
        var w = Math.Clamp(cfg.Width, 320, 3840) & ~1;
        var h = Math.Clamp(cfg.Height, 240, 2160) & ~1;
        var fps = Math.Clamp(cfg.Fps, 15, 60);
        var vk = Math.Clamp(cfg.VideoKbps, 500, 20000);
        var ak = Math.Clamp(cfg.AudioKbps, 64, 320);
        var gop = (fps * 2).ToString(inv);

        var a = new List<string>(64) { "-hide_banner", "-loglevel", "warning", "-nostats", "-progress", "pipe:1", "-stats_period", "1",
            "-thread_queue_size", "1024", "-re", "-stream_loop", "-1", "-f", "concat", "-safe", "0", "-i", concatPath };
        if (!cfg.VoiceMix) a.Insert(1, "-nostdin");
        if (cfg.VoiceMix)
            a.AddRange(new[] { "-thread_queue_size", "1024", "-f", "s16le", "-ar", RealtimeAvatarPuppeteerService.SampleRate.ToString(inv), "-ac", "1", "-i", "pipe:0" });

        var v = new StringBuilder(384);
        v.Append("[0:v]scale=").Append(w.ToString(inv)).Append(':').Append(h.ToString(inv)).Append(":force_original_aspect_ratio=decrease,pad=")
         .Append(w.ToString(inv)).Append(':').Append(h.ToString(inv)).Append(":(ow-iw)/2:(oh-ih)/2:color=black,fps=").Append(fps.ToString(inv)).Append(",format=yuv420p");
        if (cfg.AvatarOverlay && avatarText is not null && captionText is not null)
        {
            var font = fontPath is null ? "" : "fontfile='" + FontResolver.EscapeDrawPath(fontPath) + "':";
            var big = Math.Max(24, h / 12).ToString(inv);
            var small = Math.Max(16, h / 28).ToString(inv);
            v.Append(",drawtext=").Append(font).Append("textfile='").Append(FontResolver.EscapeDrawPath(avatarText)).Append("':reload=1:expansion=none:fontsize=").Append(big)
             .Append(":fontcolor=white:box=1:boxcolor=black@0.45:boxborderw=18:x=w-tw-40:y=h-th-").Append((h / 6).ToString(inv));
            v.Append(",drawtext=").Append(font).Append("textfile='").Append(FontResolver.EscapeDrawPath(captionText)).Append("':reload=1:expansion=none:fontsize=").Append(small)
             .Append(":fontcolor=white:line_spacing=6:box=1:boxcolor=black@0.55:boxborderw=14:x=w-tw-40:y=h-th-40");
        }
        v.Append("[vout]");
        if (cfg.VoiceMix)
            v.Append(";[0:a]aresample=44100:async=1,aformat=channel_layouts=stereo[pa];[1:a]aresample=44100,aformat=channel_layouts=stereo,asplit=2[va][vsc];")
             .Append("[pa][vsc]sidechaincompress=threshold=0.03:ratio=8:attack=20:release=400[duck];[duck][va]amix=inputs=2:duration=first:normalize=0[aout]");
        else
            v.Append(";[0:a]aresample=44100:async=1,aformat=channel_layouts=stereo[aout]");
        a.AddRange(new[] { "-filter_complex", v.ToString(), "-map", "[vout]", "-map", "[aout]" });

        var br = vk.ToString(inv) + "k";
        var buf = (vk * 2).ToString(inv) + "k";
        if (nvenc)
            a.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "ll", "-rc", "cbr", "-b:v", br, "-maxrate", br, "-bufsize", buf, "-g", gop, "-bf", "0" });
        else
            a.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-b:v", br, "-maxrate", br, "-bufsize", buf, "-g", gop, "-keyint_min", gop, "-sc_threshold", "0" });
        a.AddRange(new[] { "-c:a", "aac", "-b:a", ak.ToString(inv) + "k", "-ar", "44100", "-ac", "2" });

        var urls = new List<string>(cfg.Targets.Count);
        foreach (var t in cfg.Targets) urls.Add(ComposeUrl(t));
        if (urls.Count == 1)
        {
            var srt = urls[0].StartsWith("srt://", StringComparison.OrdinalIgnoreCase);
            if (!srt) a.AddRange(new[] { "-flvflags", "no_duration_filesize" });
            a.AddRange(new[] { "-f", srt ? "mpegts" : "flv", urls[0] });
        }
        else
        {
            var tee = new StringBuilder(256);
            for (var i = 0; i < urls.Count; i++)
            {
                if (i > 0) tee.Append('|');
                var srt = urls[i].StartsWith("srt://", StringComparison.OrdinalIgnoreCase);
                tee.Append(srt ? "[f=mpegts:onfail=ignore]" : "[f=flv:flvflags=no_duration_filesize:onfail=ignore]").Append(urls[i]);
            }
            a.AddRange(new[] { "-f", "tee", tee.ToString() });
        }
        return a;
    }

    /// <summary>
    /// <c>-progress</c> 1줄 파싱(무할당 · span). 진행 블록 종료(<c>progress=</c>) 줄이면 true.
    /// </summary>
    public static bool ParseProgressLine(ReadOnlySpan<char> line, ref long frame, ref double fps, ref double kbps, ref double speed, ref long drop)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0) return false;
        var k = line.Slice(0, eq).Trim();
        var v = line.Slice(eq + 1).Trim();
        var inv = CultureInfo.InvariantCulture;
        if (k.SequenceEqual("frame")) { if (long.TryParse(v, NumberStyles.Integer, inv, out var f)) frame = f; }
        else if (k.SequenceEqual("fps")) { if (double.TryParse(v, NumberStyles.Float, inv, out var d)) fps = d; }
        else if (k.SequenceEqual("bitrate"))
        {
            var u = v.IndexOf("kbits/s", StringComparison.Ordinal);
            if (u > 0 && double.TryParse(v.Slice(0, u), NumberStyles.Float, inv, out var d)) kbps = d;
        }
        else if (k.SequenceEqual("speed"))
        {
            var x = v.IndexOf('x');
            if (x > 0 && double.TryParse(v.Slice(0, x), NumberStyles.Float, inv, out var d)) speed = d;
        }
        else if (k.SequenceEqual("drop_frames")) { if (long.TryParse(v, NumberStyles.Integer, inv, out var dr)) drop = dr; }
        else if (k.SequenceEqual("progress")) return true;
        return false;
    }

    /// <summary>NVENC 초기화 실패 징후(stderr) 판별.</summary>
    public static bool LooksLikeNvencFailure(string? stderr) =>
        stderr is not null && stderr.Contains("nvenc", StringComparison.OrdinalIgnoreCase)
        && (stderr.Contains("Cannot load", StringComparison.OrdinalIgnoreCase) || stderr.Contains("No capable devices", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("OpenEncodeSessionEx failed", StringComparison.OrdinalIgnoreCase) || stderr.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Error while opening encoder", StringComparison.OrdinalIgnoreCase));

    // ══════════════════════ 플레이리스트 ══════════════════════

    /// <summary>영상 추가(존재·확장자·중복·상한 검증). 실패 시 false.</summary>
    public bool AddToPlaylist(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var full = path.Trim().Trim('"');
        if (!File.Exists(full) || Array.IndexOf(VideoExts, Path.GetExtension(full).ToLowerInvariant()) < 0) return false;
        lock (_gate)
        {
            if (_playlist.Count >= MaxPlaylist || _playlist.Contains(full)) return false;
            _playlist.Add(full);
        }
        Raise(true);
        return true;
    }

    /// <summary>폴더의 영상 일괄 추가(이름순) → 추가 수.</summary>
    public int AddFolder(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return 0;
        var n = 0;
        try
        {
            var files = Directory.GetFiles(dir);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) if (AddToPlaylist(f)) n++;
        }
        catch (Exception ex) { Program.Log("LiveLoop.AddFolder", ex); }
        return n;
    }

    public void RemoveAt(int index)
    {
        lock (_gate) { if ((uint)index >= (uint)_playlist.Count) return; _playlist.RemoveAt(index); }
        Raise(true);
    }

    public void Move(int index, int delta)
    {
        lock (_gate)
        {
            var to = index + delta;
            if ((uint)index >= (uint)_playlist.Count || (uint)to >= (uint)_playlist.Count) return;
            (_playlist[index], _playlist[to]) = (_playlist[to], _playlist[index]);
        }
        Raise(true);
    }

    public void ClearPlaylist()
    {
        lock (_gate) _playlist.Clear();
        Raise(true);
    }

    // ══════════════════════ 세션 제어 ══════════════════════

    /// <summary>24/7 송출 시작(검증 실패/ffmpeg 부재는 Graceful 실패 · 예외 0).</summary>
    public LiveStartResult Start(LiveStreamConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (_disposed) return new(false, "서비스가 종료되었습니다.");
        if (IsLive) return new(false, "이미 송출 중입니다.");
        if (cfg.Targets is null || cfg.Targets.Count == 0) return new(false, "송출 대상(플랫폼)을 1개 이상 선택하세요.");
        foreach (var t in cfg.Targets)
            if (!IsValidTarget(t, out var why)) return new(false, why);
        if (PlaylistCount == 0) return new(false, "플레이리스트가 비어 있습니다.");
        if (_ffmpeg() is null) return new(false, "ffmpeg 실행 파일을 찾을 수 없습니다.");

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _config = cfg;
            _nvencFailed = false;
            var old = Interlocked.Exchange(ref _cts, cts);
            if (old is not null) { try { old.Cancel(); old.Dispose(); } catch { } }
            Restarts = 0; ConsecutiveFailures = 0; LastError = null; Progress = default;
            SessionStartedAt = DateTimeOffset.Now;
            TotalSessions++;
            var labels = new StringBuilder();
            foreach (var t in cfg.Targets) { if (labels.Length > 0) labels.Append(" · "); labels.Append(PresetOf(t.Platform).Label); }
            TargetsLabel = labels.ToString();
            State = LiveLoopState.Starting;
            LastMessage = "⏳ 송출 준비 → " + TargetsLabel;
        }
        _loop = Task.Run(() => SessionLoopAsync(cfg, cts));
        Raise(true);
        return new(true, LastMessage);
    }

    /// <summary>송출 중지(프로세스 종료 · 워치독 정지 · 아바타 싱크 해제).</summary>
    public async Task StopAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        KillProcess();
        var loop = _loop;
        if (loop is not null) { try { await Task.WhenAny(loop, Task.Delay(5000)).ConfigureAwait(false); } catch { } }
        try { cts.Dispose(); } catch { }
        _avatar?.AttachSink(null);
        lock (_gate) { State = LiveLoopState.Stopped; LastMessage = "⏹ 송출 중지됨"; SessionStartedAt = null; ProcessStartedAt = null; }
        Raise(true);
    }

    /// <summary>플레이리스트 변경 즉시 반영 — 현재 프로세스만 재시작(백오프 없음 · 세션 유지).</summary>
    public bool ApplyPlaylistNow()
    {
        if (!IsLive || PlaylistCount == 0) return false;
        Volatile.Write(ref _applyRequested, 1);
        KillProcess();
        return true;
    }
    private int _applyRequested;

    private async Task SessionLoopAsync(LiveStreamConfig cfg, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var started = DateTimeOffset.Now;
            var (exitCode, stderr) = await RunOnceAsync(cfg, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) break;

            var ran = DateTimeOffset.Now - started;
            var applied = Interlocked.Exchange(ref _applyRequested, 0) == 1;
            if (!_nvencFailed && UsingNvenc && ran < NvencProbeWindow && LooksLikeNvencFailure(stderr))
            {
                _nvencFailed = true;
                LastMessage = "⚠️ NVENC 사용 불가 → libx264 폴백 재시작";
                Raise(true);
                continue;
            }
            if (applied) { attempt = 0; LastMessage = "🔁 플레이리스트 반영 재시작"; Raise(true); continue; }

            if (ran >= StableResetAfter) attempt = 0;
            attempt++;
            lock (_gate)
            {
                Restarts++;
                ConsecutiveFailures = attempt;
                LastError = "ffmpeg 종료(코드 " + exitCode.ToString(CultureInfo.InvariantCulture) + ") " + MaskUrl(LastLine(stderr));
            }
            if (attempt > MaxConsecutiveFailures)
            {
                lock (_gate) { State = LiveLoopState.Failed; LastMessage = "❌ 연속 " + MaxConsecutiveFailures.ToString(CultureInfo.InvariantCulture) + "회 실패 — 송출 중단"; SessionStartedAt = null; }
                _avatar?.AttachSink(null);
                if (ReferenceEquals(Interlocked.CompareExchange(ref _cts, null, cts), cts)) { try { cts.Dispose(); } catch { } }
                Raise(true);
                return;
            }
            var wait = BackoffSeconds(attempt - 1);
            lock (_gate) { State = LiveLoopState.Reconnecting; LastMessage = "🔄 재연결 대기 " + wait.ToString(CultureInfo.InvariantCulture) + "초 (" + attempt.ToString(CultureInfo.InvariantCulture) + "회차)"; }
            Raise(true);
            try { await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<(int ExitCode, string Stderr)> RunOnceAsync(LiveStreamConfig cfg, CancellationToken ct)
    {
        string[] files;
        lock (_gate) files = _playlist.ToArray();
        var existing = new List<string>(files.Length);
        foreach (var f in files) if (File.Exists(f)) existing.Add(f);
        if (existing.Count == 0) return (-1, "플레이리스트의 영상 파일이 모두 사라졌습니다.");

        var ffmpeg = _ffmpeg();
        if (ffmpeg is null) return (-1, "ffmpeg 실행 파일을 찾을 수 없습니다.");
        Process? proc = null;
        try
        {
            Directory.CreateDirectory(_workDir);
            var list = Path.Combine(_workDir, "playlist.ffconcat");
            await File.WriteAllTextAsync(list, BuildConcatList(existing), new UTF8Encoding(false), ct).ConfigureAwait(false);

            var nvenc = cfg.Nvenc && !_nvencFailed;
            var overlay = cfg.AvatarOverlay && _avatar is not null;
            if (overlay) _avatar!.Arm();   // 오버레이 텍스트 파일 선생성
            var args = BuildArgs(cfg with { VoiceMix = cfg.VoiceMix && _avatar is not null }, list, nvenc,
                overlay ? _avatar!.AvatarTextPath : null, overlay ? _avatar!.CaptionTextPath : null, overlay ? FontResolver.FindKoreanFont(true) : null);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            proc = new Process { StartInfo = psi };
            if (!proc.StartCompat()) return (-1, "송출 프로세스 시작 실패");

            lock (_gate)
            {
                _proc = proc;
                UsingNvenc = nvenc;
                ProcessStartedAt = DateTimeOffset.Now;
                State = LiveLoopState.Live;
                LastMessage = "🔴 LIVE · " + TargetsLabel + " · " + existing.Count.ToString(CultureInfo.InvariantCulture) + "편 루프" + (nvenc ? " · NVENC" : " · x264");
            }
            if (cfg.VoiceMix && _avatar is not null) _avatar.AttachSink(proc.StandardInput.BaseStream);
            else { try { proc.StandardInput.Close(); } catch { } }
            Raise(true);

            var errTask = DrainStderrAsync(proc);
            var outTask = ReadProgressAsync(proc);
            try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { KillProcess(); }
            _avatar?.AttachSink(null);
            try { await outTask.ConfigureAwait(false); } catch { }
            var err = "";
            try { err = await errTask.ConfigureAwait(false); } catch { }
            return (proc.HasExited ? proc.ExitCode : -1, err);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Program.Log("LiveLoop.Run", ex);
            return (-1, ex.Message);
        }
        catch (OperationCanceledException) { return (-1, ""); }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_proc, proc)) _proc = null; }
            try { proc?.Dispose(); } catch { }
        }
    }

    private static async Task<string> DrainStderrAsync(Process proc)
    {
        // 최근 4KB 만 보존(24시간 누적 방지).
        var sb = new StringBuilder(4096);
        var r = proc.StandardError;
        string? line;
        while ((line = await r.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (sb.Length + line.Length > 4096) sb.Remove(0, Math.Min(sb.Length, line.Length + 1024));
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    private async Task ReadProgressAsync(Process proc)
    {
        var r = proc.StandardOutput;
        string? line;
        while ((line = await r.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (!ParseProgressLine(line.AsSpan(), ref _pFrame, ref _pFps, ref _pKbps, ref _pSpeed, ref _pDrop)) continue;
            Progress = new(_pFrame, _pFps, _pKbps, _pSpeed, _pDrop);
            Raise(false);
        }
    }

    private void KillProcess()
    {
        Process? p;
        lock (_gate) p = _proc;
        try { if (p is { HasExited: false }) p.KillCompat(true); } catch { }
    }

    private static string LastLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = s.TrimEnd();
        var i = t.LastIndexOf('\n');
        var l = i >= 0 ? t.Substring(i + 1) : t;
        return l.Length > 160 ? l.Substring(0, 160) : l;
    }

    /// <summary>OnChange 방송(진행 갱신은 1초 스로틀 · 구독자 예외 격리).</summary>
    private void Raise(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastRaiseTicks) < 1000) return;
        Interlocked.Exchange(ref _lastRaiseTicks, now);
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("LiveLoop.OnChange", ex); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var cts = Interlocked.Exchange(ref _cts, null);
        try { cts?.Cancel(); } catch { }
        KillProcess();
        try { cts?.Dispose(); } catch { }
        OnChange = null;
    }
}
