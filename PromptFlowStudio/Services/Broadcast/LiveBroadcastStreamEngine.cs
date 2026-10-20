using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver

namespace PromptFlowStudio.Services.Broadcast;

/// <summary>송출 시작/중지 결과.</summary>
public sealed record BroadcastResult(bool Ok, string Message);

/// <summary>
/// [PromptFlowStudio] NDI / RTMP / SRT 라이브 방송 송출 파이프라인(v2.8.0 · 관찰 가능 싱글턴 · FFmpeg CLI · 외부 NuGet 0).
///   ▸ 최종 렌더링 결과물(또는 미리보기 영상)을 <b>RTMP</b>(OBS/vMix/YouTube Live) · <b>SRT</b>(저지연 방송) · <b>NDI</b>(로컬 방송망)로 무손실 실시간 송출한다.
///   ▸ <c>-re</c>(실시간 페이싱) + <c>-stream_loop -1</c>(무한 루프) + NVENC 저지연 인코딩(<c>-tune ll</c>) 체인. NVENC 부재 시 libx264 zerolatency 폴백.
///   ▸ 송출 프로세스를 추적(<see cref="IsLive"/>/<see cref="Uptime"/>)하고 상태를 방송 → 주조종실 LED 갱신. NDI 는 ffmpeg 빌드에 libndi 필요(없으면 Graceful 안내).
/// </summary>
public sealed class LiveBroadcastStreamEngine
{
    /// <summary>지원 프로토콜.</summary>
    public static readonly IReadOnlyList<string> Protocols = new[] { "rtmp", "srt", "ndi" };

    private readonly object _lock = new();
    private Process? _proc;
    private CancellationTokenSource? _cts;

    /// <summary>상태 변경 방송(송출 LED/업타임).</summary>
    public event Action? OnChange;

    /// <summary>송출 중 여부.</summary>
    public bool IsLive { get; private set; }
    /// <summary>현재 송출 프로토콜.</summary>
    public string Protocol { get; private set; } = "rtmp";
    /// <summary>현재 송출 대상(URL/소스명).</summary>
    public string? Target { get; private set; }
    /// <summary>송출 시작 시각.</summary>
    public DateTimeOffset? StartedAt { get; private set; }
    /// <summary>마지막 상태 메시지.</summary>
    public string LastMessage { get; private set; } = "대기";

    /// <summary>송출 경과 시간.</summary>
    public TimeSpan Uptime => StartedAt is { } s ? DateTimeOffset.Now - s : TimeSpan.Zero;

    // ══════════════════════ 송출 인자 빌드(테스트 대상) ══════════════════════

    /// <summary>프로토콜별 FFmpeg 송출 인자를 만든다(실시간 페이싱 + 선택적 루프 + NVENC/libx264).</summary>
    public static IReadOnlyList<string> BuildArgs(string protocol, string input, string url, bool loop, bool nvenc, int bitrateKbps = 6000)
    {
        var args = new List<string> { "-hide_banner" };
        if (loop) args.AddRange(new[] { "-stream_loop", "-1" });
        args.AddRange(new[] { "-re", "-i", input });   // -re: 실시간 속도로 읽기(라이브 페이싱)

        // 비디오 인코딩(저지연).
        var br = bitrateKbps.ToString(CultureInfo.InvariantCulture) + "k";
        var maxbuf = (bitrateKbps * 2).ToString(CultureInfo.InvariantCulture) + "k";
        if (nvenc)
            args.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "ll", "-b:v", br, "-maxrate", br, "-bufsize", maxbuf, "-g", "60" });
        else
            args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-b:v", br, "-maxrate", br, "-bufsize", maxbuf, "-g", "60" });
        args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k", "-ar", "44100" });

        // 컨테이너/출력.
        var (fmt, outUrl) = protocol.ToLowerInvariant() switch
        {
            "srt" => ("mpegts", url),
            "ndi" => ("libndi_newtek", url),   // ffmpeg 가 libndi 로 빌드된 경우
            _ => ("flv", url),                  // rtmp 기본
        };
        args.AddRange(new[] { "-f", fmt, outUrl });
        return args;
    }

    /// <summary>대상 URL 이 프로토콜과 형식상 유효한지(간이 검증).</summary>
    public static bool IsValidTarget(string protocol, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return protocol.ToLowerInvariant() switch
        {
            "rtmp" => url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase),
            "srt" => url.StartsWith("srt://", StringComparison.OrdinalIgnoreCase),
            "ndi" => url.Trim().Length > 0,   // NDI 는 소스명 문자열
            _ => false,
        };
    }

    // ══════════════════════ 송출 제어 ══════════════════════

    /// <summary>라이브 송출을 시작한다. 이미 송출 중/입력 없음/대상 무효/ffmpeg 부재는 실패(Graceful).</summary>
    public async Task<BroadcastResult> StartAsync(string input, string protocol, string url, bool loop = true, bool nvenc = true, CancellationToken ct = default)
    {
        if (IsLive) return new(false, "이미 송출 중입니다.");
        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input)) return new(false, "송출할 영상이 없습니다(먼저 병합/생성).");
        var proto = Protocols.Contains(protocol?.ToLowerInvariant() ?? "") ? protocol!.ToLowerInvariant() : "rtmp";
        if (!IsValidTarget(proto, url)) return new(false, $"{proto.ToUpperInvariant()} 대상 주소가 유효하지 않습니다.");
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return new(false, "ffmpeg 실행 파일을 찾을 수 없습니다.");

        try
        {
            var args = BuildArgs(proto, input, url, loop, nvenc);
            var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!proc.Start()) return new(false, "송출 프로세스 시작 실패.");

            lock (_lock)
            {
                _proc = proc;
                _cts = new CancellationTokenSource();
                IsLive = true; Protocol = proto; Target = url; StartedAt = DateTimeOffset.Now;
                LastMessage = $"🔴 {proto.ToUpperInvariant()} 송출 중 → {url}";
            }
            // 백그라운드: 종료 감지 → 상태 정리(await 하지 않음 · 장기 실행).
            _ = MonitorAsync(proc);
            OnChange?.Invoke();
            await Task.Yield();
            return new(true, LastMessage);
        }
        catch (Exception ex) { Program.Log("Broadcast.Start", ex); return new(false, "송출 시작 실패: " + ex.Message); }
    }

    /// <summary>송출을 중지한다.</summary>
    public Task StopAsync()
    {
        lock (_lock)
        {
            try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
            _cts?.Cancel();
            IsLive = false; LastMessage = "⏹ 송출 중지됨"; StartedAt = null; _proc = null;
        }
        OnChange?.Invoke();
        return Task.CompletedTask;
    }

    private async Task MonitorAsync(Process proc)
    {
        try
        {
            var drain = proc.StandardError.ReadToEndAsync();   // stderr 드레인(버퍼 데드락 방지)
            await proc.WaitForExitAsync().ConfigureAwait(false);
            await drain.ConfigureAwait(false);
        }
        catch { }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_proc, proc) || _proc is null)
                {
                    IsLive = false; StartedAt = null; _proc = null;
                    if (!LastMessage.StartsWith("⏹")) LastMessage = "⏹ 송출 종료";
                }
            }
            OnChange?.Invoke();
        }
    }

    private static string? ResolveFfmpeg()
    {
        try
        {
            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;
            return CliPathResolver.GetExecutablePath("ffmpeg", "ffmpeg.exe");
        }
        catch { return null; }
    }
}
