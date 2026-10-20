using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Broadcast;

/// <summary>v16.25.0 아바타 발화 큐 1건 — 자막 텍스트 · 감정(강도) · TTS 오디오 경로(없으면 텍스트 길이 기반 가상 발화) · 출처 라벨.</summary>
public sealed record AvatarCue(string Text, FacialEmotion Emotion, double Intensity = 0.6, string? AudioPath = null, string Source = "chat");

/// <summary>v16.25.0 아바타 현재 프레임 — 감정 · 입모양 · 벌림(0~1) · 발화 중 · 시퀀스.</summary>
public readonly record struct AvatarFrame(FacialEmotion Emotion, Viseme Mouth, float Openness, bool Speaking, long Seq);

/// <summary>
/// [PromptFlowStudio] v16.25.0 🎭 실시간 아바타 음성/표정 퍼페티어 — 채팅 응답 TTS 를 라이브 송출에 실시간 주입하고 방송 오버레이 아바타의 립싱크/표정을 구동.
///   ▸ 오디오: 응답 TTS(mp3/wav)를 ffmpeg 로 24kHz mono s16le PCM 디코드(ArrayPool · 상한 30초) → 발화 큐(상한 8 · 초과 시 신규 드롭).
///   ▸ 펌프 스레드(20ms 청크 · Stopwatch 누적 페이싱 · 드리프트 없음): 현재 클립 슬라이스 또는 무음을 <see cref="AttachSink"/> 된 송출 ffmpeg stdin 에 기록 → 플레이리스트 음성과 사이드체인 덕킹 믹스.
///   ▸ 립싱크: 청크 RMS → 벌림 EWMA(어택/릴리즈) · 대사 viseme 시퀀스(<see cref="AiAudioDrivenLipSyncService.TranscriptToVisemes"/>)를 발화 진행률로 인덱싱. 오디오 없는 큐는 viseme 기반 가상 벌림.
///   ▸ 표정: 큐 감정 → 발화 종료 후 2초 여운 → Neutral. 오버레이는 drawtext <c>reload=1</c> 용 avatar.txt/caption.txt 를 100ms 주기·변경 시에만 원자 교체.
///   ▸ 핫패스(<see cref="FillChunk"/>)는 락 1개 + Span 연산만 — 할당 0(테스트 고정). 예외는 펌프 루프 최상위에서 격리.
/// </summary>
public sealed class RealtimeAvatarPuppeteerService : IDisposable
{
    public const int SampleRate = 24000;
    public const int ChunkMs = 20;
    public const int ChunkBytes = SampleRate / 1000 * ChunkMs * 2;   // 960B (mono s16le)
    public const int MaxQueue = 8;
    public const int MaxClipSeconds = 30;
    public const int MaxClipBytes = SampleRate * 2 * MaxClipSeconds;
    public const int MaxVisemes = 512;
    public const int AfterglowMs = 2000;
    public const int CaptionHoldMs = 3000;
    public const int MaxCatchUpChunks = 10;
    public const int CaptionLineChars = 22;
    public static readonly TimeSpan DecodeTimeout = TimeSpan.FromSeconds(15);

    private sealed class Clip
    {
        public byte[]? Pcm;
        public int Length;
        public Viseme[] Visemes = Array.Empty<Viseme>();
        public int VisemeCount;
        public int Pos;
        public AvatarCue Cue = null!;
    }

    private static readonly string[] FaceTable = BuildFaceTable();

    private readonly Func<string?> _ffmpeg;
    private readonly object _gate = new();
    private readonly Queue<Clip> _queue = new(MaxQueue);
    private readonly byte[] _chunk = new byte[ChunkBytes];
    private readonly string _overlayDir;
    private Clip? _current;
    private Stream? _sink;
    private Thread? _pump;
    private volatile bool _armed;
    private bool _disposed;

    // 프레임 상태(펌프 스레드 기록 · UI/HUD 조회).
    private FacialEmotion _emotion = FacialEmotion.Neutral;
    private Viseme _mouth = Viseme.Rest;
    private float _open;
    private bool _speaking;
    private long _seq;
    private long _idleSinceMs;
    private string _caption = "";
    private int _lastFace = -1;
    private string _lastCaption = "";

    private int _spoken, _dropped, _underruns, _sinkErrors, _decodeFails;
    private long _chunksWritten;

    public RealtimeAvatarPuppeteerService(IVideoMergeService merge) : this(() => merge.ResolvedFfmpegPath, null) { }

    /// <param name="ffmpeg">ffmpeg 경로 공급자(테스트 주입 · null 반환 시 오디오 없는 가상 발화).</param>
    /// <param name="overlayDir">오버레이 텍스트 폴더(null = %TEMP%/PromptFlowStudio/live).</param>
    public RealtimeAvatarPuppeteerService(Func<string?> ffmpeg, string? overlayDir)
    {
        _ffmpeg = ffmpeg;
        _overlayDir = overlayDir ?? Path.Combine(Path.GetTempPath(), "PromptFlowStudio", "live");
    }

    /// <summary>발화 시작/종료 · 큐 변경 방송(저빈도 · 20ms 틱마다 아님).</summary>
    public event Action? OnChange;

    public string AvatarTextPath => Path.Combine(_overlayDir, "avatar.txt");
    public string CaptionTextPath => Path.Combine(_overlayDir, "caption.txt");
    public bool IsArmed => _armed;
    public bool SinkAttached => Volatile.Read(ref _sink) is not null;
    public int QueueDepth { get { lock (_gate) return _queue.Count + (_current is null ? 0 : 1); } }
    public int SpokenCount => Volatile.Read(ref _spoken);
    public int DroppedCount => Volatile.Read(ref _dropped);
    public int UnderrunCount => Volatile.Read(ref _underruns);
    public int SinkErrorCount => Volatile.Read(ref _sinkErrors);
    public int DecodeFailCount => Volatile.Read(ref _decodeFails);
    public long ChunksWritten => Interlocked.Read(ref _chunksWritten);
    public string Caption { get { lock (_gate) return _caption; } }
    public string? LastAction { get; private set; }

    /// <summary>현재 프레임 스냅샷(값 형식 · 무할당).</summary>
    public AvatarFrame Current { get { lock (_gate) return new(_emotion, _mouth, _open, _speaking, _seq); } }

    /// <summary>현재 표정의 텍스트 얼굴(정적 캐시 참조 · 무할당).</summary>
    public string CurrentFace { get { lock (_gate) return FaceText(_emotion, _mouth); } }

    // ══════════════════════ 순수 헬퍼(테스트 대상) ══════════════════════

    /// <summary>감정 × 입모양 → 텍스트 얼굴(정적 테이블 참조 반환 · 0B). drawtext 기본 폰트에서도 깨지지 않는 ASCII 위주.</summary>
    public static string FaceText(FacialEmotion e, Viseme m)
    {
        var ei = (uint)e < 6u ? (int)e : 0;
        var mi = (uint)m < 7u ? (int)m : 0;
        return FaceTable[ei * 7 + mi];
    }

    private static string[] BuildFaceTable()
    {
        // 눈(감정) · 볼 장식 · 입(viseme: Rest A E I O U Closed)
        var eyes = new[] { "oo", "^^", "><", "OO", "o?", "--" };   // Neutral HappySmile Giggle Surprised Curious Pout
        var mouthRest = new[] { "_", "u", "w", "o", "~", "^" };
        var t = new string[6 * 7];
        for (var e = 0; e < 6; e++)
        {
            var l = e == 1 || e == 2 ? "(*" : "(";
            var r = e == 1 || e == 2 ? "*)" : ")";
            for (var m = 0; m < 7; m++)
            {
                var mouth = (Viseme)m switch
                {
                    Viseme.A => "D", Viseme.E => "e", Viseme.I => "=", Viseme.O => "O", Viseme.U => "o", Viseme.Closed => "-",
                    _ => mouthRest[e],
                };
                var eye = eyes[e];
                t[e * 7 + m] = l + " " + eye[0] + " " + mouth + " " + eye[eye.Length - 1] + " " + r;
            }
        }
        return t;
    }

    /// <summary>PCM s16le 청크 RMS → 입 벌림 목표값(0~1 · 무할당). 0.12 FS 이상이면 최대.</summary>
    public static float RmsOpenness(ReadOnlySpan<byte> pcm16)
    {
        var n = pcm16.Length / 2;
        if (n == 0) return 0f;
        double acc = 0;
        for (var i = 0; i < n; i++)
        {
            var s = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i * 2, 2)) / 32768.0;
            acc += s * s;
        }
        var rms = Math.Sqrt(acc / n);
        var o = (rms - 0.01) / 0.11;
        return o <= 0 ? 0f : o >= 1 ? 1f : (float)o;
    }

    /// <summary>viseme 기준 가상 벌림(오디오 없는 발화용).</summary>
    public static float VisemeOpenness(Viseme v) => v switch
    {
        Viseme.A => 0.9f, Viseme.O => 0.75f, Viseme.E => 0.55f, Viseme.U => 0.4f, Viseme.I => 0.35f, Viseme.Closed => 0.02f, _ => 0.05f,
    };

    /// <summary>자막 줄바꿈(공백 우선 · 줄당 <paramref name="width"/>자 · 최대 3줄 · 초과 시 말줄임).</summary>
    public static string WrapCaption(string? text, int width = CaptionLineChars)
    {
        var s = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (s.Length <= width || width < 4) return s;
        var sb = new StringBuilder(s.Length + 4);
        var lines = 0;
        var i = 0;
        while (i < s.Length && lines < 3)
        {
            var take = Math.Min(width, s.Length - i);
            if (i + take < s.Length)
            {
                var sp = s.LastIndexOf(' ', i + take, take);
                if (sp > i + width / 2) take = sp - i;
            }
            if (lines > 0) sb.Append('\n');
            var last = lines == 2 && i + take < s.Length;
            sb.Append(s, i, last ? Math.Max(1, take - 1) : take);
            if (last) sb.Append('…');
            i += take;
            while (i < s.Length && s[i] == ' ') i++;
            lines++;
        }
        return sb.ToString();
    }

    /// <summary>오디오 없는 발화의 가상 길이(초) — 글자당 0.11초 · 1.2~10초.</summary>
    public static double VirtualSeconds(string? text) => Math.Clamp((text ?? "").Trim().Length * 0.11, 1.2, 10);

    // ══════════════════════ 큐 ══════════════════════

    /// <summary>발화 큐 등록(TTS 디코드 포함 · 예외 0). 큐 가득/빈 텍스트/폐기 시 false.</summary>
    public async Task<bool> EnqueueAsync(AvatarCue cue, CancellationToken ct = default)
    {
        if (cue is null || string.IsNullOrWhiteSpace(cue.Text) || _disposed) return false;
        if (QueueDepth >= MaxQueue) { Interlocked.Increment(ref _dropped); return false; }

        var clip = new Clip { Cue = cue };
        try
        {
            if (!string.IsNullOrWhiteSpace(cue.AudioPath) && File.Exists(cue.AudioPath))
            {
                var (pcm, len) = await DecodeAsync(cue.AudioPath!, ct).ConfigureAwait(false);
                if (pcm is not null && len > 0) { clip.Pcm = pcm; clip.Length = len; }
                else Interlocked.Increment(ref _decodeFails);
            }
            if (clip.Pcm is null)
                clip.Length = (int)(VirtualSeconds(cue.Text) * SampleRate) * 2 & ~1;

            var vis = ArrayPool<Viseme>.Shared.Rent(MaxVisemes);
            clip.Visemes = vis;
            clip.VisemeCount = AiAudioDrivenLipSyncService.TranscriptToVisemes(cue.Text.AsSpan(), vis.AsSpan(0, MaxVisemes));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { ReturnClip(clip); return false; }
        catch (Exception ex) { Program.Log("AvatarPuppeteer.Enqueue", ex); ReturnClip(clip); return false; }

        lock (_gate)
        {
            if (_disposed || _queue.Count + (_current is null ? 0 : 1) >= MaxQueue)
            {
                Interlocked.Increment(ref _dropped);
                ReturnClip(clip);
                return false;
            }
            _queue.Enqueue(clip);
        }
        LastAction = "🎭 발화 대기 · " + cue.Emotion + " · " + (clip.Pcm is null ? "가상 립싱크" : "TTS 립싱크");
        Raise();
        return true;
    }

    /// <summary>대기 중 발화 전부 취소(현재 발화 포함).</summary>
    public void ClearQueue()
    {
        lock (_gate)
        {
            while (_queue.Count > 0) ReturnClip(_queue.Dequeue());
            if (_current is not null) { ReturnClip(_current); _current = null; }
            _speaking = false; _mouth = Viseme.Rest; _open = 0; _caption = "";
        }
        Raise();
    }

    // ══════════════════════ 핫패스 ══════════════════════

    /// <summary>
    /// 20ms 청크 생성(펌프 스레드 · 무할당) — 현재 클립 PCM 슬라이스(부족분 무음) · 입 벌림/viseme/감정 갱신.
    /// <paramref name="nowMs"/>는 단조 시각(ms · 테스트 주입). 반환: 발화 상태 전이 여부(OnChange 필요).
    /// </summary>
    public bool FillChunk(Span<byte> dst, long nowMs)
    {
        dst.Clear();
        var changed = false;
        lock (_gate)
        {
            if (_current is null && _queue.Count > 0)
            {
                _current = _queue.Dequeue();
                _emotion = _current.Cue.Emotion;
                _caption = _current.Cue.Text;
                _speaking = true;
                changed = true;
            }

            var target = 0f;
            if (_current is { } c)
            {
                var remain = c.Length - c.Pos;
                var take = Math.Min(remain, dst.Length) & ~1;
                if (c.Pcm is not null)
                {
                    c.Pcm.AsSpan(c.Pos, take).CopyTo(dst);
                    target = RmsOpenness(dst.Slice(0, take));
                }
                var vi = c.VisemeCount == 0 ? 0 : (int)((long)c.Pos * c.VisemeCount / Math.Max(1, c.Length));
                var v = c.VisemeCount == 0 ? Viseme.A : c.Visemes[Math.Min(vi, c.VisemeCount - 1)];
                if (c.Pcm is null) target = VisemeOpenness(v);
                _mouth = target < 0.08f ? (v == Viseme.Closed ? Viseme.Closed : Viseme.Rest) : v;
                c.Pos += take;
                if (c.Pos >= c.Length)
                {
                    ReturnClip(c);
                    _current = null;
                    _speaking = false;
                    _idleSinceMs = nowMs;
                    Interlocked.Increment(ref _spoken);
                    changed = true;
                }
            }
            else
            {
                _mouth = Viseme.Rest;
                if (_emotion != FacialEmotion.Neutral && nowMs - _idleSinceMs >= AfterglowMs) { _emotion = FacialEmotion.Neutral; changed = true; }
                if (_caption.Length > 0 && nowMs - _idleSinceMs >= CaptionHoldMs) { _caption = ""; changed = true; }
            }

            // 어택 0.6 · 릴리즈 0.3 EWMA.
            _open += (target - _open) * (target > _open ? 0.6f : 0.3f);
            if (_open < 0.001f) _open = 0f;
            _seq++;
        }
        return changed;
    }

    // ══════════════════════ 펌프 / 싱크 ══════════════════════

    /// <summary>송출 ffmpeg 의 stdin(오디오 입력 pipe:0)을 연결/해제(null). 쓰기 실패 시 자동 해제.</summary>
    public void AttachSink(Stream? sink)
    {
        Volatile.Write(ref _sink, sink);
        if (sink is not null) Arm();
        Raise();
    }

    /// <summary>펌프 스레드 기동(멱등) — 송출 연결 전에도 UI 미리보기 립싱크를 위해 가동 가능.</summary>
    public void Arm()
    {
        lock (_gate)
        {
            if (_disposed || _armed) return;
            _armed = true;
            _pump = new Thread(PumpLoop) { IsBackground = true, Name = "LiveAvatar.Pump", Priority = ThreadPriority.AboveNormal };
            _pump.Start();
        }
        WriteOverlay(force: true);
    }

    /// <summary>펌프 정지(싱크 해제 · 큐 유지).</summary>
    public void Disarm()
    {
        Thread? t;
        lock (_gate) { _armed = false; t = _pump; _pump = null; }
        Volatile.Write(ref _sink, null);
        try { if (t is not null && t != Thread.CurrentThread) t.Join(500); } catch { }
        Raise();
    }

    private void PumpLoop()
    {
        GcLatencySession.Acquire();
        try
        {
            var sw = Stopwatch.StartNew();
            long sent = 0;
            long lastOverlay = 0;
            while (_armed)
            {
                try
                {
                    var now = sw.ElapsedMilliseconds;
                    var due = now / ChunkMs;
                    if (due - sent > 50) { Interlocked.Increment(ref _underruns); sent = due - 1; }   // 장기 정지(절전 등) 후 재동기
                    var burst = 0;
                    var changed = false;
                    while (sent < due && burst++ < MaxCatchUpChunks)
                    {
                        changed |= FillChunk(_chunk, now);
                        var sink = Volatile.Read(ref _sink);
                        if (sink is not null)
                        {
                            try { sink.Write(_chunk, 0, ChunkBytes); Interlocked.Increment(ref _chunksWritten); }
                            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                            {
                                Interlocked.CompareExchange(ref _sink, null, sink);
                                Interlocked.Increment(ref _sinkErrors);
                            }
                        }
                        sent++;
                    }
                    if (changed) Raise();
                    if (now - lastOverlay >= 100) { lastOverlay = now; WriteOverlay(force: false); }
                    Thread.Sleep(4);
                }
                catch (Exception ex) { Program.Log("AvatarPuppeteer.Pump", ex); Thread.Sleep(50); }
            }
        }
        finally { GcLatencySession.Release(); }
    }

    /// <summary>오버레이 텍스트 원자 교체(변경 시에만 · 공유 위반은 다음 틱 재시도).</summary>
    private void WriteOverlay(bool force)
    {
        try
        {
            int face; string cap;
            lock (_gate) { face = (int)_emotion * 7 + (int)_mouth; cap = _caption; }
            if (!force && face == _lastFace && ReferenceEquals(cap, _lastCaption)) return;
            Directory.CreateDirectory(_overlayDir);
            if (force || face != _lastFace) { if (Replace(AvatarTextPath, FaceTable[face])) _lastFace = face; }
            if (force || !ReferenceEquals(cap, _lastCaption)) { if (Replace(CaptionTextPath, cap.Length == 0 ? " " : WrapCaption(cap))) _lastCaption = cap; }
        }
        catch (Exception ex) { Program.Log("AvatarPuppeteer.Overlay", ex); }
    }

    private static bool Replace(string path, string text)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // ══════════════════════ 디코드 ══════════════════════

    private async Task<(byte[]? Pcm, int Length)> DecodeAsync(string path, CancellationToken ct)
    {
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null) return (null, 0);
        var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-v", "error", "-i", path, "-f", "s16le", "-acodec", "pcm_s16le", "-ac", "1", "-ar", "24000", "pipe:1" })
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) return (null, 0);
        using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
        d.CancelAfter(DecodeTimeout);
        var buf = ArrayPool<byte>.Shared.Rent(MaxClipBytes);
        var len = 0;
        try
        {
            _ = proc.StandardError.ReadToEndAsync(d.Token);   // stderr 드레인
            var s = proc.StandardOutput.BaseStream;
            while (len < MaxClipBytes)
            {
                var r = await s.ReadAsync(buf.AsMemory(len, MaxClipBytes - len), d.Token).ConfigureAwait(false);
                if (r <= 0) break;
                len += r;
            }
            if (len >= MaxClipBytes) { try { proc.Kill(true); } catch { } }
            else await proc.WaitForExitAsync(d.Token).ConfigureAwait(false);
            len &= ~1;
            if (len == 0) { ArrayPool<byte>.Shared.Return(buf); return (null, 0); }
            return (buf, len);
        }
        catch (Exception ex)
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
            ArrayPool<byte>.Shared.Return(buf);
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            Program.Log("AvatarPuppeteer.Decode", ex);
            return (null, 0);
        }
    }

    private static void ReturnClip(Clip c)
    {
        if (c.Pcm is { } p) { c.Pcm = null; ArrayPool<byte>.Shared.Return(p); }
        if (c.Visemes.Length > 0) { var v = c.Visemes; c.Visemes = Array.Empty<Viseme>(); ArrayPool<Viseme>.Shared.Return(v); }
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("AvatarPuppeteer.OnChange", ex); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Disarm();
        lock (_gate)
        {
            _disposed = true;
            while (_queue.Count > 0) ReturnClip(_queue.Dequeue());
            if (_current is not null) { ReturnClip(_current); _current = null; }
        }
        OnChange = null;
    }
}
