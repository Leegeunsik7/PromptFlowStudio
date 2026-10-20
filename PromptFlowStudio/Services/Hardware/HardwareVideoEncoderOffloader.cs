using System.Diagnostics;
using System.Globalization;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Hardware;

public enum HwVendor : byte { Auto, Nvenc, Amf, QuickSync, Software, Null }
public enum HwCodec : byte { H264, Hevc, Av1 }

/// <summary>인코더 가용성 — 목록 존재(ffmpeg -encoders) · 실검증(3프레임 테스트 인코드 성공).</summary>
public sealed record EncoderCapability(HwVendor Vendor, HwCodec Codec, string FfmpegName, bool Listed, bool Verified, string? Error);

public sealed record EncoderSessionOptions(int Width, int Height, int Fps = 30, HwVendor Vendor = HwVendor.Auto, HwCodec Codec = HwCodec.H264, int BitrateKbps = 0,
    DirectPathFormat Input = DirectPathFormat.Rgb24, string? OutputPath = null);

public sealed record EncodeReport(HwVendor Vendor, HwCodec Codec, string Encoder, int Width, int Height, long Frames, long Dropped, double Seconds, long OutputBytes, int ExitCode,
    double EncoderCpuMs, double ConvertMsPerFrame, string? Note);

/// <summary>
/// [PromptFlow Studio v16.63.0] 🎞 하드웨어 비디오 인코더 오프로더(NVENC · AMF · QuickSync · 소프트웨어 · Null).
///   ▸ 탐지: ffmpeg -encoders 목록 + 실제 3프레임 테스트 인코드(드라이버/세션 가용 검증) — Auto = 검증된 NVENC → AMF → QSV → x264 → Null.
///   ▸ 세션: rawvideo 파이프 입력(NV12 · BGRA는 GPU 색변환 경로) → 벤더별 초저지연 프리셋(B프레임 0 · CBR · zerolatency) → 파일/널 싱크. 8K(&gt;4096폭)는 HEVC 자동 승격.
///   ▸ 핫패스: 렌더 스레드는 링 슬롯 확보 + (RGB→NV12 BT.709 정수 변환 | DirectPath 슬롯 핸드오프) 만 수행 · 파이프 쓰기는 전용 스레드 · 링 가득 시 드롭(백프레셔) · 무할당.
///   ▸ <see cref="SubmitSlot"/>: 공유 메모리 슬롯을 그대로 파이프로 기록(입력 포맷 일치 시 복사 0) 후 슬롯 반납.
///   ▸ 장애: 하드웨어 세션 초기 실패(파이프 끊김) → 소프트웨어 인코더로 1회 자동 폴백 · ffmpeg 부재 → Null 싱크.
/// </summary>
public sealed class HardwareVideoEncoderOffloader : IDisposable
{
    public const int RingSize = 3, MaxWidth = 7680, MaxHeight = 4320, H264MaxWidth = 4096;

    private readonly Func<string?> _ffmpeg;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private readonly byte[][] _ring = new byte[RingSize][];
    private readonly int[] _ringLen = new int[RingSize];
    private readonly DirectPathArena?[] _ringArena = new DirectPathArena?[RingSize];
    private readonly int[] _ringSlot = new int[RingSize];
    private readonly Queue<string> _log = new();
    private byte[] _scratch = Array.Empty<byte>();
    private IReadOnlyList<EncoderCapability>? _caps;
    private Process? _proc;
    private Stream? _stdin;
    private Thread? _writer;
    private SemaphoreSlim? _filled, _free;
    private EncoderSessionOptions? _opt;
    private DirectPathFormat _pipeFmt;
    private volatile bool _running;
    private int _head, _tail, _fallbackDone;
    private long _submitted, _written, _dropped, _bytes, _convTicks, _zeroCopyFrames;
    private double _convEwmaUs, _writeEwmaUs;
    private Stopwatch _clock = new();
    private string _encoder = "";
    private HwVendor _vendor;
    private HwCodec _codec;
    private string? _note;

    public HardwareVideoEncoderOffloader(Func<string?> ffmpeg) => _ffmpeg = ffmpeg;
    public HardwareVideoEncoderOffloader(IServiceProvider sp)
        : this(() => (sp.GetService(typeof(PromptFlowStudio.Services.Media.IVideoMergeService)) as PromptFlowStudio.Services.Media.IVideoMergeService)?.ResolvedFfmpegPath) { }

    public event Action? OnChange;

    public bool IsRunning => _running;
    public bool FfmpegAvailable => _ffmpeg() is not null;
    public IReadOnlyList<EncoderCapability>? Capabilities => _caps;
    public HwVendor ActiveVendor => _vendor;
    public HwCodec ActiveCodec => _codec;
    public string ActiveEncoder => _encoder;
    public long Submitted => Interlocked.Read(ref _submitted);
    public long Written => Interlocked.Read(ref _written);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long BytesPiped => Interlocked.Read(ref _bytes);
    public long ZeroCopyFrames => Interlocked.Read(ref _zeroCopyFrames);
    public double ConvertMicros => Volatile.Read(ref _convEwmaUs);
    public double WriteMicros => Volatile.Read(ref _writeEwmaUs);
    public double FpsAchieved => _clock.Elapsed.TotalSeconds > 0.2 ? Written / _clock.Elapsed.TotalSeconds : 0;
    public EncodeReport? LastReport { get; private set; }
    public string? LastAction { get; private set; }
    public string[] RecentLog() { lock (_log) return _log.ToArray(); }

    public static string Label(HwVendor v) => v switch
    {
        HwVendor.Nvenc => "NVIDIA NVENC", HwVendor.Amf => "AMD AMF", HwVendor.QuickSync => "Intel QuickSync", HwVendor.Software => "소프트웨어(x264/x265/SVT)", HwVendor.Null => "Null(측정)", _ => "자동",
    };

    public static string? EncoderName(HwVendor v, HwCodec c) => (v, c) switch
    {
        (HwVendor.Nvenc, HwCodec.H264) => "h264_nvenc", (HwVendor.Nvenc, HwCodec.Hevc) => "hevc_nvenc", (HwVendor.Nvenc, HwCodec.Av1) => "av1_nvenc",
        (HwVendor.Amf, HwCodec.H264) => "h264_amf", (HwVendor.Amf, HwCodec.Hevc) => "hevc_amf", (HwVendor.Amf, HwCodec.Av1) => "av1_amf",
        (HwVendor.QuickSync, HwCodec.H264) => "h264_qsv", (HwVendor.QuickSync, HwCodec.Hevc) => "hevc_qsv", (HwVendor.QuickSync, HwCodec.Av1) => "av1_qsv",
        (HwVendor.Software, HwCodec.H264) => "libx264", (HwVendor.Software, HwCodec.Hevc) => "libx265", (HwVendor.Software, HwCodec.Av1) => "libsvtav1",
        _ => null,
    };

    /// <summary>해상도 제약 보정 — 짝수 정렬 · H.264 폭 &gt; 4096 → HEVC 승격.</summary>
    public static (int W, int H, HwCodec Codec, string? Note) Normalize(int w, int h, HwCodec c)
    {
        w = Math.Clamp(w & ~1, 16, MaxWidth); h = Math.Clamp(h & ~1, 16, MaxHeight);
        if (c == HwCodec.H264 && w > H264MaxWidth) return (w, h, HwCodec.Hevc, "H.264 폭 4096 초과 → HEVC 승격");
        return (w, h, c, null);
    }

    /// <summary>ffmpeg 인자(내부 상수 조합 · 셸 미경유) — 파이프 rawvideo → 벤더별 초저지연 프리셋 → 출력(null = 널 먹스).</summary>
    public static List<string> BuildArgs(HwVendor v, HwCodec c, int w, int h, int fps, int kbps, DirectPathFormat input, string? output)
    {
        var enc = EncoderName(v, c) ?? throw new ArgumentException("인코더 없음: " + v);
        var inv = CultureInfo.InvariantCulture;
        var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-y", "-f", "rawvideo", "-pix_fmt", input == DirectPathFormat.Bgra32 ? "bgra" : "nv12",
            "-s", w.ToString(inv) + "x" + h.ToString(inv), "-r", fps.ToString(inv), "-i", "-", "-an", "-c:v", enc };
        if (kbps <= 0) kbps = (int)Math.Clamp(w * (long)h * fps / 10_000, 2_000, 200_000);
        switch (v)
        {
            case HwVendor.Nvenc: a.AddRange(new[] { "-preset", "p1", "-tune", "ull", "-zerolatency", "1", "-rc", "cbr", "-delay", "0", "-bf", "0" }); break;
            case HwVendor.Amf: a.AddRange(new[] { "-usage", "ultralowlatency", "-quality", "speed", "-rc", "cbr", "-bf", "0" }); break;
            case HwVendor.QuickSync: a.AddRange(new[] { "-preset", "veryfast", "-look_ahead", "0", "-async_depth", "1", "-bf", "0" }); break;
            default:
                if (c == HwCodec.Av1) a.AddRange(new[] { "-preset", "12" });
                else a.AddRange(new[] { "-preset", "ultrafast", "-tune", "zerolatency", "-bf", "0" });
                break;
        }
        a.AddRange(new[] { "-b:v", kbps.ToString(inv) + "k", "-maxrate", kbps.ToString(inv) + "k", "-bufsize", (kbps / 2).ToString(inv) + "k", "-g", (fps * 2).ToString(inv) });
        // NVENC/AMF: NV12·BGRA 원본 입력(BGRA 색변환은 GPU) · QSV: NV12 · 소프트웨어: yuv420p
        if (v == HwVendor.Software) a.AddRange(new[] { "-pix_fmt", "yuv420p" });
        else if (v == HwVendor.QuickSync) a.AddRange(new[] { "-pix_fmt", "nv12" });
        if (output is null) a.AddRange(new[] { "-f", "null", "-" });
        else a.Add(output);
        return a;
    }

    /// <summary>하드웨어 인코더 탐지(목록 + 테스트 인코드 · 결과 캐시 · force 재탐지).</summary>
    public async Task<IReadOnlyList<EncoderCapability>> ProbeAsync(bool force = false, CancellationToken ct = default)
    {
        if (_caps is not null && !force) return _caps;
        await _probeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_caps is not null && !force) return _caps;
            var list = new List<EncoderCapability>();
            var ff = _ffmpeg();
            string listing = "";
            if (ff is not null && OperatingSystem.IsWindows())
            {
                using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
                to.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    var r = await PromptFlowStudio.Services.Engines.ProcessRunner.RunAsync(ff, new[] { "-hide_banner", "-encoders" }, null, null, to.Token).ConfigureAwait(false);
                    listing = r.StdOut + r.StdErr;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            foreach (var v in new[] { HwVendor.Nvenc, HwVendor.Amf, HwVendor.QuickSync, HwVendor.Software })
                foreach (var c in new[] { HwCodec.H264, HwCodec.Hevc, HwCodec.Av1 })
                {
                    var name = EncoderName(v, c)!;
                    var listed = listing.Contains(" " + name + " ", StringComparison.Ordinal);
                    bool ok = false; string? err = ff is null ? "ffmpeg 미탐지" : listed ? null : "ffmpeg 빌드에 없음";
                    if (listed && ff is not null)
                    {
                        using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        to.CancelAfter(TimeSpan.FromSeconds(8));
                        try
                        {
                            var r = await PromptFlowStudio.Services.Engines.ProcessRunner.RunAsync(ff, new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=gray:s=320x180:r=30",
                                "-frames:v", "3", "-c:v", name, "-f", "null", "-" }, null, null, to.Token).ConfigureAwait(false);
                            ok = r.Ok;
                            if (!ok) err = FirstLine(r.StdErr) ?? "테스트 인코드 실패(" + r.ExitCode.ToString(CultureInfo.InvariantCulture) + ")";
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { err = "테스트 인코드 시간 초과"; }
                    }
                    list.Add(new EncoderCapability(v, c, name, listed, ok, err));
                }
            _caps = list;
            var hw = 0; foreach (var e in list) if (e.Verified && e.Vendor != HwVendor.Software) hw++;
            LastAction = "🔍 인코더 탐지 · 하드웨어 " + hw.ToString(CultureInfo.InvariantCulture) + "종 검증" + (ff is null ? " · ffmpeg 없음(Null 싱크)" : "");
            Raise();
            return list;
        }
        finally { _probeLock.Release(); }
    }

    private static string? FirstLine(string s)
    {
        foreach (var l in s.Split('\n')) { var t = l.Trim(); if (t.Length > 0) return t.Length > 160 ? t[..160] : t; }
        return null;
    }

    /// <summary>Auto/요청 벤더 → 실제 사용 벤더(검증 우선 · 없으면 소프트웨어 · ffmpeg 없으면 Null).</summary>
    public HwVendor Resolve(HwVendor want, HwCodec codec)
    {
        if (want == HwVendor.Null || _ffmpeg() is null || !OperatingSystem.IsWindows()) return HwVendor.Null;
        var caps = _caps;
        bool Ok(HwVendor v) { if (caps is null) return v == HwVendor.Software; foreach (var c in caps) if (c.Vendor == v && c.Codec == codec && c.Verified) return true; return false; }
        if (want != HwVendor.Auto) return Ok(want) || caps is null ? want : (Ok(HwVendor.Software) ? HwVendor.Software : HwVendor.Null);
        foreach (var v in new[] { HwVendor.Nvenc, HwVendor.Amf, HwVendor.QuickSync, HwVendor.Software }) if (Ok(v)) return v;
        return HwVendor.Null;
    }

    /// <summary>세션 시작(구성 시에만 할당) — 실제 사용 벤더 반환.</summary>
    public HwVendor Start(EncoderSessionOptions o)
    {
        Stop();
        lock (_gate)
        {
            var (w, h, codec, note) = Normalize(o.Width, o.Height, o.Codec);
            _opt = o with { Width = w, Height = h, Codec = codec };
            _codec = codec; _note = note;
            _pipeFmt = o.Input == DirectPathFormat.Rgb24 ? DirectPathFormat.Nv12 : o.Input;
            var len = DirectPathMeta.FrameBytes(_pipeFmt, w, h);
            for (var i = 0; i < RingSize; i++) { if (_ring[i] is null || _ring[i].Length < len) _ring[i] = new byte[len]; _ringArena[i] = null; }
            if (_scratch.Length < len) _scratch = new byte[len];
            _head = _tail = 0; _fallbackDone = 0;
            _submitted = _written = _dropped = _bytes = _convTicks = _zeroCopyFrames = 0;
            _convEwmaUs = _writeEwmaUs = 0;
            _filled = new SemaphoreSlim(0, RingSize);
            _free = new SemaphoreSlim(RingSize, RingSize);
            lock (_log) _log.Clear();
            var v = Resolve(o.Vendor, codec);
            if (v == HwVendor.QuickSync && _pipeFmt == DirectPathFormat.Bgra32) v = Resolve(HwVendor.Software, codec);
            if (!Launch(v)) { v = HwVendor.Null; Launch(v); }
            _running = true;
            _clock = Stopwatch.StartNew();
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "HwEnc.Writer" };
            _writer.Start();
            LastAction = "⏺ 인코딩 시작 · " + Label(_vendor) + " · " + (_encoder.Length > 0 ? _encoder : "null") + " · " + w.ToString(CultureInfo.InvariantCulture) + "×" + h.ToString(CultureInfo.InvariantCulture)
                + (note is null ? "" : " · " + note);
        }
        Raise();
        return _vendor;
    }

    private bool Launch(HwVendor v)
    {
        _vendor = v;
        _encoder = v == HwVendor.Null ? "" : EncoderName(v, _codec) ?? "";
        if (v == HwVendor.Null) { _proc = null; _stdin = null; return true; }
        var o = _opt!;
        var ff = _ffmpeg();
        if (ff is null) return false;
        try
        {
            if (o.OutputPath is { } op) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(op))!);
            var psi = new ProcessStartInfo(ff) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            foreach (var a in BuildArgs(v, _codec, o.Width, o.Height, o.Fps, o.BitrateKbps, _pipeFmt, o.OutputPath)) psi.ArgumentList.Add(a);
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } d) lock (_log) { _log.Enqueue(d); while (_log.Count > 12) _log.Dequeue(); } };
            p.OutputDataReceived += (_, _) => { };
            if (!p.StartCompat()) { p.Dispose(); return false; }
            p.BeginErrorReadLine(); p.BeginOutputReadLine();
            _proc = p;
            _stdin = p.StandardInput.BaseStream;
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            PromptFlowStudio.Program.Log("HwEnc.Launch", ex);
            return false;
        }
    }

    /// <summary>프레임 제출(무할당 · 렌더 스레드) — RGB24 는 NV12 로 변환, NV12/BGRA 는 그대로. 링 가득 시 드롭(false).</summary>
    public bool Submit(ReadOnlySpan<byte> frame)
    {
        if (!_running) return false;
        using var guard = ZeroGcRegionScope.Enter("HwEnc.Submit");
        Interlocked.Increment(ref _submitted);
        var free = _free!;
        if (!free.Wait(0)) { Interlocked.Increment(ref _dropped); return false; }
        var i = _head; _head = (_head + 1) % RingSize;
        var o = _opt!;
        var t0 = Stopwatch.GetTimestamp();
        int len;
        if (o.Input == DirectPathFormat.Rgb24) len = RgbToNv12(frame, o.Width, o.Height, o.Width * 3, _ring[i]);
        else { len = Math.Min(frame.Length, DirectPathMeta.FrameBytes(_pipeFmt, o.Width, o.Height)); frame[..len].CopyTo(_ring[i]); }
        ObserveConvert(t0);
        _ringLen[i] = len; _ringArena[i] = null;
        _filled!.Release();
        return true;
    }

    /// <summary>DirectPath 읽기 슬롯 핸드오프 — 입력 포맷이 파이프 포맷과 같으면 공유 메모리에서 파이프로 직접 기록(복사 0). 슬롯은 기록 후 반납.</summary>
    public bool SubmitSlot(DirectPathArena arena, int slot)
    {
        if (!_running) { arena.Release(slot); return false; }
        Interlocked.Increment(ref _submitted);
        if (!_free!.Wait(0)) { Interlocked.Increment(ref _dropped); arena.Release(slot); return false; }
        var i = _head; _head = (_head + 1) % RingSize;
        _ringArena[i] = arena; _ringSlot[i] = slot; _ringLen[i] = arena.LengthOf(slot);
        _filled!.Release();
        return true;
    }

    private void ObserveConvert(long t0)
    {
        var dt = Stopwatch.GetTimestamp() - t0;
        Interlocked.Add(ref _convTicks, dt);
        var us = dt * 1e6 / Stopwatch.Frequency;
        Volatile.Write(ref _convEwmaUs, _convEwmaUs <= 0 ? us : _convEwmaUs * 0.9 + us * 0.1);
    }

    private void WriterLoop()
    {
        var filled = _filled!; var free = _free!;
        while (true)
        {
            try { filled.Wait(); } catch (ObjectDisposedException) { return; }
            var i = _tail; _tail = (_tail + 1) % RingSize;
            if (_ringLen[i] < 0) { free.Release(); return; } // 종료 마커
            ReadOnlySpan<byte> data;
            var arena = _ringArena[i];
            if (arena is not null)
            {
                var slot = _ringSlot[i];
                var o = _opt!;
                if (o.Input == DirectPathFormat.Rgb24)
                {
                    var t0 = Stopwatch.GetTimestamp();
                    var n = RgbToNv12(arena.Payload(slot)[..(o.Width * o.Height * 3)], o.Width, o.Height, o.Width * 3, _scratch);
                    ObserveConvert(t0);
                    arena.Release(slot);
                    data = _scratch.AsSpan(0, n);
                }
                else { data = arena.Payload(slot)[.._ringLen[i]]; Interlocked.Increment(ref _zeroCopyFrames); }
                Pipe(data);
                if (o.Input != DirectPathFormat.Rgb24) arena.Release(slot);
                _ringArena[i] = null;
            }
            else Pipe(_ring[i].AsSpan(0, _ringLen[i]));
            free.Release();
        }
    }

    private void Pipe(ReadOnlySpan<byte> data)
    {
        var t0 = Stopwatch.GetTimestamp();
        var s = _stdin;
        if (s is not null)
        {
            try { s.Write(data); }
            catch (IOException)
            {
                // 하드웨어 세션 초기화 실패 등 → 소프트웨어 1회 폴백(현재 프레임 재전송)
                if (Interlocked.Exchange(ref _fallbackDone, 1) == 0 && _vendor != HwVendor.Software)
                {
                    var failed = _vendor;
                    KillProcess();
                    _note = Label(failed) + " 파이프 실패 → 소프트웨어 폴백";
                    if (Launch(HwVendor.Software) && _stdin is { } s2) { try { s2.Write(data); } catch (IOException) { Interlocked.Increment(ref _dropped); return; } }
                    else { Launch(HwVendor.Null); }
                    Raise();
                }
                else { Interlocked.Increment(ref _dropped); return; }
            }
        }
        Interlocked.Increment(ref _written);
        Interlocked.Add(ref _bytes, data.Length);
        var us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        Volatile.Write(ref _writeEwmaUs, _writeEwmaUs <= 0 ? us : _writeEwmaUs * 0.9 + us * 0.1);
    }

    /// <summary>RGB24 → NV12(BT.709 제한 범위 · 2×2 크로마 평균 · 정수) — 반환 길이. w/h 짝수.</summary>
    public static int RgbToNv12(ReadOnlySpan<byte> rgb, int w, int h, int stride, Span<byte> nv12)
    {
        var ySize = w * h;
        var uv = nv12.Slice(ySize, ySize / 2);
        for (var y = 0; y < h; y += 2)
        {
            var r0 = rgb.Slice(y * stride, w * 3);
            var r1 = rgb.Slice((y + 1) * stride, w * 3);
            var y0 = nv12.Slice(y * w, w);
            var y1 = nv12.Slice((y + 1) * w, w);
            var c = uv.Slice(y / 2 * w, w);
            for (var x = 0; x < w; x += 2)
            {
                var k = x * 3;
                int ra = r0[k], ga = r0[k + 1], ba = r0[k + 2], rb = r0[k + 3], gb = r0[k + 4], bb = r0[k + 5];
                int rc = r1[k], gc = r1[k + 1], bc = r1[k + 2], rd = r1[k + 3], gd = r1[k + 4], bd = r1[k + 5];
                y0[x] = (byte)(((47 * ra + 157 * ga + 16 * ba + 128) >> 8) + 16);
                y0[x + 1] = (byte)(((47 * rb + 157 * gb + 16 * bb + 128) >> 8) + 16);
                y1[x] = (byte)(((47 * rc + 157 * gc + 16 * bc + 128) >> 8) + 16);
                y1[x + 1] = (byte)(((47 * rd + 157 * gd + 16 * bd + 128) >> 8) + 16);
                int r = (ra + rb + rc + rd + 2) >> 2, g = (ga + gb + gc + gd + 2) >> 2, b = (ba + bb + bc + bd + 2) >> 2;
                c[x] = (byte)(((-26 * r - 86 * g + 112 * b + 128) >> 8) + 128);
                c[x + 1] = (byte)(((112 * r - 102 * g - 10 * b + 128) >> 8) + 128);
            }
        }
        return ySize + ySize / 2;
    }

    /// <summary>세션 종료 — 대기 프레임 배출 → stdin 닫기 → 종료 대기(5s) · 보고서.</summary>
    public EncodeReport? Stop()
    {
        EncodeReport rep;
        lock (_gate)
        {
            if (!_running) return null;
            _running = false;
            try
            {
                _free!.Wait(TimeSpan.FromSeconds(5));
                var i = _head; _head = (_head + 1) % RingSize;
                _ringLen[i] = -1; _ringArena[i] = null;
                _filled!.Release();
            }
            catch (ObjectDisposedException) { }
            _writer?.Join(TimeSpan.FromSeconds(10));
            for (var i = 0; i < RingSize; i++) if (_ringArena[i] is { } a) { a.Release(_ringSlot[i]); _ringArena[i] = null; }
            var exit = 0; double cpu = 0;
            if (_proc is { } p)
            {
                try { _stdin?.Dispose(); } catch (IOException) { }
                if (!p.WaitForExit(5000)) { try { p.KillCompat(true); } catch (InvalidOperationException) { } }
                p.WaitForExit(2000);
                try { exit = p.ExitCode; cpu = p.TotalProcessorTimeCompat().TotalMilliseconds; } catch (InvalidOperationException) { }
                p.Dispose();
            }
            _proc = null; _stdin = null;
            _clock.Stop();
            var o = _opt!;
            long size = 0;
            if (o.OutputPath is { } op && File.Exists(op)) size = new FileInfo(op).Length;
            var frames = Written;
            rep = new EncodeReport(_vendor, _codec, _encoder.Length > 0 ? _encoder : "null", o.Width, o.Height, frames, Dropped, _clock.Elapsed.TotalSeconds, size, exit, cpu,
                frames > 0 ? Interlocked.Read(ref _convTicks) * 1000.0 / Stopwatch.Frequency / frames : 0, _note);
            LastReport = rep;
            _filled?.Dispose(); _free?.Dispose(); _filled = _free = null;
            LastAction = "⏹ 인코딩 종료 · " + rep.Encoder + " · " + frames.ToString(CultureInfo.InvariantCulture) + "프레임 · 드롭 " + rep.Dropped.ToString(CultureInfo.InvariantCulture)
                + (size > 0 ? " · " + (size / 1024).ToString(CultureInfo.InvariantCulture) + "KB" : "") + (exit != 0 ? " · 종료코드 " + exit.ToString(CultureInfo.InvariantCulture) : "");
        }
        Raise();
        return rep;
    }

    private void KillProcess()
    {
        var p = _proc;
        _proc = null; _stdin = null;
        if (p is null) return;
        try { if (!p.HasExited) p.KillCompat(true); p.WaitForExit(2000); } catch (InvalidOperationException) { }
        p.Dispose();
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("HwEnc.OnChange", ex); }
    }

    public void Dispose() { Stop(); KillProcess(); _probeLock.Dispose(); }
}
