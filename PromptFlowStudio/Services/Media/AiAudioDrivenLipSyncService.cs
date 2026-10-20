using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Media;

/// <summary>v16.20.0 입모양 클래스(viseme) — 한국어 모음/자음 그룹 기준 7종.</summary>
public enum Viseme : byte { Rest = 0, A = 1, E = 2, I = 3, O = 4, U = 5, Closed = 6 }

/// <summary>v16.20.0 음소 타임라인 1구간 — 시작/끝(초) · 입모양 · 개구도(0~1).</summary>
public readonly record struct VisemeCue(double Start, double End, Viseme Shape, double Openness);

/// <summary>v16.20.0 오디오 분석 — 길이 · 첫 발화 시각 · 유성 구간 비율 · 20ms 프레임 수.</summary>
public readonly record struct VoiceActivity(double DurationSeconds, double OnsetSeconds, double VoicedRatio, int Frames);

/// <summary>v16.20.0 립싱크 백엔드.</summary>
public enum LipSyncBackend { None, Wav2LipApi, MuseTalkApi, LatentSyncLocal, FfmpegRealign }

/// <summary>v16.20.0 원격 립싱크 API 설정(환경변수 · 시크릿 하드코딩 금지).</summary>
public sealed record LipSyncApiConfig(string? Endpoint, string? ApiKey, LipSyncBackend Model)
{
    public const string EndpointVar = "PFS_LIPSYNC_ENDPOINT", KeyVar = "PFS_LIPSYNC_API_KEY", ModelVar = "PFS_LIPSYNC_MODEL";

    public bool IsConfigured => Uri.TryCreate(Endpoint, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.IsLoopback);

    public static LipSyncApiConfig FromEnvironment()
    {
        var model = Environment.GetEnvironmentVariable(ModelVar);
        return new LipSyncApiConfig(Environment.GetEnvironmentVariable(EndpointVar), Environment.GetEnvironmentVariable(KeyVar),
            string.Equals(model, "musetalk", StringComparison.OrdinalIgnoreCase) ? LipSyncBackend.MuseTalkApi : LipSyncBackend.Wav2LipApi);
    }
}

/// <summary>v16.20.0 립싱크 요청 — 영상 · 나레이션(WAV/MP3) · 출력 · 대사(음소 분배용) · 감정(입모양 진폭).</summary>
public sealed record AudioLipSyncRequest(
    string VideoPath, string AudioPath, string OutputPath, string? Transcript = null,
    FacialEmotion Emotion = FacialEmotion.Neutral, double EmotionIntensity = FacialEmotionPuppeteerEngine.DefaultIntensity, bool AllowRemote = true);

/// <summary>v16.20.0 립싱크 결과 — 사용 백엔드 · 음소 타임라인 · 오디오 분석 · 폴백 사유.</summary>
public sealed record AudioLipSyncResult(
    bool Ok, string? OutputPath, LipSyncBackend Backend, IReadOnlyList<VisemeCue> Visemes, VoiceActivity Activity, string Message);

/// <summary>
/// [PromptFlowStudio] v16.20.0 👄 음성 기반 정밀 AI 립싱크 오케스트레이터 — 나레이션(v16.14.0 TTS WAV/MP3) → 음소(viseme) 타임라인 → 입모양 정합.
///   ▸ 분석: WAV PCM(16-bit · 다채널 평균) 20ms RMS 엔벨로프(ArrayPool · 무할당 루프) → 적응 임계(노이즈 플로어 ×2.5 + 히스테리시스) 유성 구간.
///     MP3/기타는 ffmpeg 로 16kHz mono WAV 변환 후 동일 경로.
///   ▸ 음소: 대사(한글 음절 → 중성 모음/종성 양순음, 라틴 모음) 순서를 유성 구간 길이에 비례 분배 · 개구도 = 프레임 에너지 × 감정 입모양 배율.
///     대사가 없으면 에너지 기반(A/E/O/Closed) 추정.
///   ▸ 실행 순서(폴백 체인): 원격 Wav2Lip/MuseTalk API(제출→폴링→다운로드 · 타임아웃 · 지수 백오프 · 응답 스키마 즉시 검증)
///     → 로컬 LatentSync(<see cref="ILipSyncService"/>) → 로컬 FFmpeg 프레임 보정(첫 발화 정렬 · 30fps CFR · 길이 차 ≤20% 리타임 · 부족분 마지막 프레임 복제 · 오디오 먹싱).
///     FFmpeg 보정은 입모양 자체를 바꾸지 않음(메시지에 명시).
///   ▸ 실패는 결과 record(예외 0) · 시크릿은 환경변수 · 동시 원격 제출 1건.
/// </summary>
public sealed class AiAudioDrivenLipSyncService : IDisposable
{
    public const int FrameMs = 20;
    public const int TargetFps = 30;
    public const double MaxRetimeRatio = 0.2;
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    public const int MaxPolls = 200, MaxSubmitAttempts = 3;
    public const long MaxUploadBytes = 200L * 1024 * 1024;

    private readonly ILipSyncService? _local;
    private readonly Func<string?> _ffmpeg;
    private readonly Func<LipSyncApiConfig> _config;
    private readonly HttpClient _http;
    private readonly TimeSpan _poll;
    private int _remoteBusy;

    public AiAudioDrivenLipSyncService(IVideoMergeService merge, ILipSyncService? local = null)
        : this(() => merge.ResolvedFfmpegPath, local, LipSyncApiConfig.FromEnvironment, null, PollInterval) { }

    /// <summary>테스트 주입 생성자.</summary>
    public AiAudioDrivenLipSyncService(Func<string?> ffmpeg, ILipSyncService? local, Func<LipSyncApiConfig> config, HttpMessageHandler? handler, TimeSpan poll)
    {
        _ffmpeg = ffmpeg;
        _local = local;
        _config = config;
        _poll = poll;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = HttpTimeout;
    }

    public bool RemoteConfigured => _config().IsConfigured;
    public bool LocalModelAvailable => _local is { IsAvailable: true };
    public bool FfmpegAvailable => _ffmpeg() is not null;
    /// <summary>입모양을 실제로 바꾸는 AI 백엔드(원격/로컬)가 있는지.</summary>
    public bool AiAvailable => RemoteConfigured || LocalModelAvailable;
    public bool IsAvailable => AiAvailable || FfmpegAvailable;

    public LipSyncBackend PreferredBackend => RemoteConfigured ? _config().Model : LocalModelAvailable ? LipSyncBackend.LatentSyncLocal
        : FfmpegAvailable ? LipSyncBackend.FfmpegRealign : LipSyncBackend.None;

    public static string LabelOf(LipSyncBackend b) => b switch
    {
        LipSyncBackend.Wav2LipApi => "Wav2Lip API",
        LipSyncBackend.MuseTalkApi => "MuseTalk API",
        LipSyncBackend.LatentSyncLocal => "LatentSync(로컬)",
        LipSyncBackend.FfmpegRealign => "FFmpeg 프레임 보정",
        _ => "없음",
    };

    // ── 순수 함수: PCM 분석 ─────────────────────────────────────────────────

    /// <summary>16-bit PCM → 프레임 RMS(0~1) 엔벨로프. 반환: 기록한 프레임 수(dst 길이 상한).</summary>
    public static int ComputeEnvelope(ReadOnlySpan<byte> pcm16, int channels, int sampleRate, Span<float> dst)
    {
        if (channels <= 0 || sampleRate <= 0) return 0;
        var frameSamples = Math.Max(1, sampleRate * FrameMs / 1000);
        var stride = channels * 2;
        var total = pcm16.Length / stride;
        var frames = Math.Min(dst.Length, (total + frameSamples - 1) / frameSamples);
        for (var f = 0; f < frames; f++)
        {
            var from = f * frameSamples;
            var to = Math.Min(total, from + frameSamples);
            double acc = 0;
            for (var s = from; s < to; s++)
            {
                var o = s * stride;
                var sum = 0;
                for (var c = 0; c < channels; c++) sum += BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(o + c * 2, 2));
                var v = sum / (double)channels / 32768.0;
                acc += v * v;
            }
            dst[f] = to > from ? (float)Math.Sqrt(acc / (to - from)) : 0f;
        }
        return frames;
    }

    /// <summary>엔벨로프 → 유성 플래그(제자리 voiced) · 적응 임계 + 히스테리시스 + 60ms 미만 무음 갭 메움. 반환: 임계값.</summary>
    public static float DetectVoiced(ReadOnlySpan<float> env, Span<bool> voiced)
    {
        var n = Math.Min(env.Length, voiced.Length);
        if (n == 0) return 0;
        // 노이즈 플로어 = 하위 10% 근사(최소값과 평균 사이 1/10 지점 대신 정렬 없는 2-패스 추정)
        float min = float.MaxValue, max = 0;
        double mean = 0;
        for (var i = 0; i < n; i++) { var v = env[i]; if (v < min) min = v; if (v > max) max = v; mean += v; }
        mean /= n;
        var floor = min + (float)((mean - min) * 0.2);
        var on = Math.Max(0.01f, Math.Max(floor * 2.5f, max * 0.12f));
        var off = on * 0.6f;
        var state = false;
        for (var i = 0; i < n; i++)
        {
            state = state ? env[i] >= off : env[i] >= on;
            voiced[i] = state;
        }
        const int gap = 60 / FrameMs;
        var last = -1;
        for (var i = 0; i < n; i++)
        {
            if (!voiced[i]) continue;
            if (last >= 0 && i - last - 1 > 0 && i - last - 1 < gap)
                for (var k = last + 1; k < i; k++) voiced[k] = true;
            last = i;
        }
        return on;
    }

    /// <summary>대사 → 입모양 시퀀스(한글 음절 중성/종성 · 라틴 모음 · 공백/구두점은 Rest). 반환: 기록 수.</summary>
    public static int TranscriptToVisemes(ReadOnlySpan<char> text, Span<Viseme> dst)
    {
        var n = 0;
        foreach (var ch in text)
        {
            if (n >= dst.Length) break;
            if (ch >= '가' && ch <= '힣')
            {
                var idx = ch - 0xAC00;
                var jung = idx % 588 / 28;
                var jong = idx % 28;
                dst[n++] = VowelOf(jung);
                // 종성 ㅁ(16)·ㅂ(17)·ㅍ(26) — 양순음 닫힘
                if ((jong == 16 || jong == 17 || jong == 26) && n < dst.Length) dst[n++] = Viseme.Closed;
                continue;
            }
            var c = char.ToLowerInvariant(ch);
            switch (c)
            {
                case 'a': dst[n++] = Viseme.A; break;
                case 'e': dst[n++] = Viseme.E; break;
                case 'i': case 'y': dst[n++] = Viseme.I; break;
                case 'o': dst[n++] = Viseme.O; break;
                case 'u': case 'w': dst[n++] = Viseme.U; break;
                case 'm': case 'b': case 'p': dst[n++] = Viseme.Closed; break;
                case ' ': case ',': case '.': case '!': case '?': case '\n':
                    if (n > 0 && dst[n - 1] != Viseme.Rest) dst[n++] = Viseme.Rest;
                    break;
            }
        }
        return n;
    }

    // 중성 인덱스(ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ) → 입모양
    private static Viseme VowelOf(int jung) => jung switch
    {
        0 or 2 or 9 => Viseme.A,
        1 or 3 or 5 or 7 or 10 or 15 => Viseme.E,
        4 or 6 => Viseme.O,
        8 or 11 or 12 or 14 => Viseme.O,
        13 or 16 or 17 => Viseme.U,
        18 => Viseme.I,
        19 or 20 => Viseme.I,
        _ => Viseme.A,
    };

    /// <summary>
    /// 음소 타임라인 생성(순수) — 유성 구간에 대사 입모양을 길이 비례로 분배, 무음은 Rest. mouthScale 은 개구도 배율.
    /// </summary>
    public static List<VisemeCue> BuildTimeline(ReadOnlySpan<float> env, ReadOnlySpan<bool> voiced, ReadOnlySpan<Viseme> script, double mouthScale = 1.0)
    {
        var n = Math.Min(env.Length, voiced.Length);
        var cues = new List<VisemeCue>(Math.Max(4, n / 4));
        if (n == 0) return cues;
        var voicedFrames = 0;
        float peak = 0;
        for (var i = 0; i < n; i++) { if (voiced[i]) voicedFrames++; if (env[i] > peak) peak = env[i]; }
        var dt = FrameMs / 1000.0;
        var scale = Math.Clamp(mouthScale, 0.5, 1.5);
        var vIndex = 0;
        var i0 = 0;
        while (i0 < n)
        {
            var isV = voiced[i0];
            var i1 = i0;
            while (i1 < n && voiced[i1] == isV) i1++;
            if (!isV) { cues.Add(new VisemeCue(Math.Round(i0 * dt, 3), Math.Round(i1 * dt, 3), Viseme.Rest, 0)); i0 = i1; continue; }
            // 유성 구간: 대사 입모양을 프레임 순서로 분배(대사 없으면 에너지 기반)
            var segStart = i0;
            while (segStart < i1)
            {
                Viseme shape;
                int segEnd;
                if (script.Length > 0)
                {
                    var si = (int)((long)vIndex * script.Length / Math.Max(1, voicedFrames));
                    shape = script[Math.Min(script.Length - 1, si)];
                    if (shape == Viseme.Rest) shape = Viseme.Closed;
                    segEnd = segStart + 1;
                    while (segEnd < i1)
                    {
                        var sj = (int)((long)(vIndex + segEnd - segStart) * script.Length / Math.Max(1, voicedFrames));
                        var nxt = script[Math.Min(script.Length - 1, sj)];
                        if (nxt == Viseme.Rest) nxt = Viseme.Closed;
                        if (nxt != shape) break;
                        segEnd++;
                    }
                }
                else
                {
                    var r = peak > 0 ? env[segStart] / peak : 0;
                    shape = r > 0.66f ? Viseme.A : r > 0.4f ? Viseme.E : r > 0.2f ? Viseme.O : Viseme.Closed;
                    segEnd = Math.Min(i1, segStart + 3);
                }
                double e = 0;
                for (var k = segStart; k < segEnd; k++) e += env[k];
                e /= segEnd - segStart;
                var open = shape == Viseme.Closed ? 0.05 : Math.Clamp((peak > 0 ? e / peak : 0) * scale * BaseOpen(shape), 0.05, 1);
                cues.Add(new VisemeCue(Math.Round(segStart * dt, 3), Math.Round(segEnd * dt, 3), shape, Math.Round(open, 3)));
                vIndex += segEnd - segStart;
                segStart = segEnd;
            }
            i0 = i1;
        }
        return cues;
    }

    private static double BaseOpen(Viseme v) => v switch
    {
        Viseme.A => 1.0, Viseme.E => 0.7, Viseme.O => 0.8, Viseme.I => 0.45, Viseme.U => 0.4, _ => 0.1,
    };

    /// <summary>WAV 헤더 파싱 — data 오프셋/길이 · 채널 · 샘플레이트 · 비트 수. 실패 false.</summary>
    public static bool TryParseWav(ReadOnlySpan<byte> wav, out int dataOffset, out int dataLength, out int channels, out int sampleRate, out int bits)
    {
        dataOffset = dataLength = channels = sampleRate = bits = 0;
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8)) return false;
        var p = 12;
        var fmt = false;
        while (p + 8 <= wav.Length)
        {
            var size = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(p + 4, 4)));
            var body = p + 8;
            if (wav.Slice(p, 4).SequenceEqual("fmt "u8) && size >= 16 && body + 16 <= wav.Length)
            {
                if (BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body, 2)) != 1) return false;   // PCM 만
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 2, 2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 14, 2));
                fmt = true;
            }
            else if (wav.Slice(p, 4).SequenceEqual("data"u8))
            {
                dataOffset = body;
                dataLength = Math.Min(size, wav.Length - body);
                return fmt && channels > 0 && sampleRate > 0 && bits == 16;
            }
            p = body + size + (size & 1);
        }
        return false;
    }

    /// <summary>WAV 바이트 → 음성 활동 + 음소 타임라인(순수 · 스크래치는 ArrayPool).</summary>
    public static (VoiceActivity Activity, List<VisemeCue> Cues) Analyze(ReadOnlySpan<byte> wav, string? transcript, double mouthScale = 1.0)
    {
        if (!TryParseWav(wav, out var off, out var len, out var ch, out var sr, out _)) return (default, new List<VisemeCue>());
        var pcm = wav.Slice(off, len);
        var frameSamples = Math.Max(1, sr * FrameMs / 1000);
        var frames = (pcm.Length / (ch * 2) + frameSamples - 1) / frameSamples;
        if (frames == 0) return (default, new List<VisemeCue>());
        var envArr = ArrayPool<float>.Shared.Rent(frames);
        var vArr = ArrayPool<bool>.Shared.Rent(frames);
        var tLen = Math.Min(4096, (transcript?.Length ?? 0) * 2);
        var sArr = ArrayPool<Viseme>.Shared.Rent(Math.Max(1, tLen));
        try
        {
            var env = envArr.AsSpan(0, frames);
            var voiced = vArr.AsSpan(0, frames);
            frames = ComputeEnvelope(pcm, ch, sr, env);
            DetectVoiced(env, voiced);
            var sn = transcript is null ? 0 : TranscriptToVisemes(transcript.AsSpan(), sArr.AsSpan(0, tLen));
            var cues = BuildTimeline(env, voiced, sArr.AsSpan(0, sn), mouthScale);
            int onset = -1, vc = 0;
            for (var i = 0; i < frames; i++) if (voiced[i]) { vc++; if (onset < 0) onset = i; }
            var dur = pcm.Length / (double)(ch * 2) / sr;
            return (new VoiceActivity(Math.Round(dur, 3), onset < 0 ? 0 : Math.Round(onset * FrameMs / 1000.0, 3), frames > 0 ? (double)vc / frames : 0, frames), cues);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(envArr);
            ArrayPool<bool>.Shared.Return(vArr);
            ArrayPool<Viseme>.Shared.Return(sArr);
        }
    }

    // ── 순수 함수: FFmpeg 프레임 보정 ────────────────────────────────────────

    /// <summary>
    /// 프레임 보정 인자(순수) — 오디오 첫 발화 정렬(atrim) · 30fps CFR · 길이 차 ≤20% 는 setpts 리타임, 초과 부족분은 tpad 마지막 프레임 복제 · 오디오 먹싱.
    /// </summary>
    public static List<string> BuildRealignArgs(string video, string audio, string output, double videoSeconds, double audioSeconds, double onsetSeconds, double leadIn = 0.1)
    {
        var trim = Math.Max(0, onsetSeconds - Math.Max(0, leadIn));
        var aLen = Math.Max(0.1, audioSeconds - trim);
        var vLen = Math.Max(0.1, videoSeconds);
        var vf = new StringBuilder(160);
        var ratio = aLen / vLen;
        if (Math.Abs(ratio - 1) <= MaxRetimeRatio) vf.Append("setpts=").Append(Inv(ratio)).Append("*PTS,");
        else if (ratio > 1) vf.Append("tpad=stop_mode=clone:stop_duration=").Append(Inv(aLen - vLen)).Append(',');
        vf.Append("fps=").Append(TargetFps).Append(",format=yuv420p");
        var af = "atrim=start=" + Inv(trim) + ",asetpts=PTS-STARTPTS";
        return new List<string>(32)
        {
            "-y", "-hide_banner", "-nostdin", "-i", video, "-i", audio,
            "-filter_complex", "[0:v]" + vf + "[v];[1:a]" + af + "[a]",
            "-map", "[v]", "-map", "[a]", "-t", Inv(aLen),
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-c:a", "aac", "-b:a", "192k", "-ar", "48000",
            "-movflags", "+faststart", "-progress", "pipe:1", output,
        };
    }

    // ── 실행 ────────────────────────────────────────────────────────────────

    /// <summary>오디오 분석(WAV 직접 · 그 외 ffmpeg 16k mono 변환). 실패 시 빈 결과.</summary>
    public async Task<(VoiceActivity Activity, List<VisemeCue> Cues)> AnalyzeAudioAsync(string audioPath, string? transcript, double mouthScale, CancellationToken ct)
    {
        string? tmp = null;
        try
        {
            var path = audioPath;
            if (AiVoiceoverTtsService.ProbeSeconds(audioPath) is null || !await IsPcm16Async(audioPath, ct).ConfigureAwait(false))
            {
                var exe = _ffmpeg();
                if (exe is null) return (default, new List<VisemeCue>());
                tmp = Path.Combine(Path.GetTempPath(), "pfs_lip_" + Guid.NewGuid().ToString("N") + ".wav");
                var r = await ProcessRunner.RunAsync(exe, new List<string> { "-y", "-hide_banner", "-nostdin", "-i", audioPath, "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", tmp }, ct: ct).ConfigureAwait(false);
                if (!r.Ok || !File.Exists(tmp)) return (default, new List<VisemeCue>());
                path = tmp;
            }
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            return Analyze(bytes, transcript, mouthScale);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Program.Log("AiLipSync.Analyze", ex); return (default, new List<VisemeCue>()); }
        finally { try { if (tmp is not null) File.Delete(tmp); } catch { /* 무시 */ } }
    }

    private static async Task<bool> IsPcm16Async(string path, CancellationToken ct)
    {
        var head = new byte[64];
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var n = await fs.ReadAsync(head, ct).ConfigureAwait(false);
        return IsPcm16Header(head.AsSpan(0, n));
    }

    private static bool IsPcm16Header(ReadOnlySpan<byte> s)
        => s.Length >= 36 && BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(20, 2)) == 1 && BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(34, 2)) == 16;

    /// <summary>
    /// 립싱크 실행(폴백 체인 · 예외 0). 원격 API → LatentSync → FFmpeg 프레임 보정.
    /// </summary>
    public async Task<AudioLipSyncResult> SyncAsync(AudioLipSyncRequest req, IProgress<string>? status = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (string.IsNullOrWhiteSpace(req.VideoPath) || !File.Exists(req.VideoPath)) return Fail("입력 영상을 찾을 수 없습니다.");
        if (string.IsNullOrWhiteSpace(req.AudioPath) || !File.Exists(req.AudioPath)) return Fail("나레이션 오디오를 찾을 수 없습니다.");
        try
        {
            status?.Report("🎧 음소 타임라인 분석 중…");
            var baby = FacialEmotionPuppeteerEngine.IsBabySubject(req.Transcript);
            var mouth = FacialEmotionPuppeteerEngine.MouthScaleOf(req.Emotion, req.EmotionIntensity, baby);
            var (act, cues) = await AnalyzeAudioAsync(req.AudioPath, req.Transcript, mouth, ct).ConfigureAwait(false);
            var notes = new StringBuilder(96);

            if (req.AllowRemote && RemoteConfigured)
            {
                var cfg = _config();
                status?.Report("☁️ " + LabelOf(cfg.Model) + " 제출 중…");
                var (ok, msg) = await RunRemoteAsync(cfg, req, mouth, cues, status, ct).ConfigureAwait(false);
                if (ok) return new(true, req.OutputPath, cfg.Model, cues, act, LabelOf(cfg.Model) + " 립싱크 완료");
                notes.Append(LabelOf(cfg.Model)).Append(" 실패(").Append(msg).Append(") → ");
            }
            ct.ThrowIfCancellationRequested();
            if (_local is { IsAvailable: true })
            {
                var lr = await _local.ApplyAsync(req.VideoPath, req.AudioPath, req.OutputPath, status, ct).ConfigureAwait(false);
                if (lr.Ok) return new(true, lr.OutputPath, LipSyncBackend.LatentSyncLocal, cues, act, notes + "LatentSync 립싱크 완료");
                notes.Append("LatentSync 실패 → ");
            }
            ct.ThrowIfCancellationRequested();
            var rr = await RealignAsync(req.VideoPath, req.AudioPath, req.OutputPath, act, status, ct).ConfigureAwait(false);
            return rr.Ok
                ? new(true, rr.OutputPath, LipSyncBackend.FfmpegRealign, cues, act, notes + "FFmpeg 프레임 보정(오디오 정렬 · 입모양 변형 없음)")
                : new(false, null, LipSyncBackend.None, cues, act, notes + rr.Message);
        }
        catch (OperationCanceledException) { return Fail("립싱크가 취소되었습니다."); }
        catch (Exception ex) { Program.Log("AiLipSync.Sync", ex); return Fail("립싱크 실패: " + ex.Message); }
    }

    /// <summary>1-Click 오디오-립싱크 재정렬(FFmpeg 프레임 보정 전용 · 예외 0).</summary>
    public async Task<MixResult> RealignAsync(string video, string audio, string output, VoiceActivity? activity = null,
        IProgress<string>? status = null, CancellationToken ct = default)
    {
        var exe = _ffmpeg();
        if (exe is null) return new(false, null, "ffmpeg.exe 를 찾을 수 없습니다.");
        if (!File.Exists(video) || !File.Exists(audio)) return new(false, null, "영상 또는 오디오가 없습니다.");
        try
        {
            var act = activity is { DurationSeconds: > 0 } a ? a : (await AnalyzeAudioAsync(audio, null, 1.0, ct).ConfigureAwait(false)).Activity;
            var vSec = await ProbeDurationAsync(exe, video, ct).ConfigureAwait(false);
            var aSec = act.DurationSeconds > 0 ? act.DurationSeconds : await ProbeDurationAsync(exe, audio, ct).ConfigureAwait(false);
            if (vSec <= 0 || aSec <= 0) return new(false, null, "영상/오디오 길이를 읽지 못했습니다.");
            status?.Report("🎞️ 프레임 보정 · 오디오 정렬 중…");
            return await FfmpegVideoMixerService.RunAsync(exe, BuildRealignArgs(video, audio, output, vSec, aSec, act.OnsetSeconds), output, aSec, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new(false, null, "취소됨"); }
        catch (Exception ex) { Program.Log("AiLipSync.Realign", ex); return new(false, null, "재정렬 실패: " + ex.Message); }
    }

    /// <summary>ffmpeg stderr "Duration: HH:MM:SS.xx" 파싱(순수).</summary>
    public static double ParseDuration(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return 0;
        var s = stderr.AsSpan();
        var i = s.IndexOf("Duration: ", StringComparison.Ordinal);
        if (i < 0 || i + 21 > s.Length) return 0;
        var t = s.Slice(i + 10, 11);
        if (!int.TryParse(t[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var h)) return 0;
        if (!int.TryParse(t.Slice(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m)) return 0;
        if (!double.TryParse(t.Slice(6, 5), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)) return 0;
        return h * 3600 + m * 60 + sec;
    }

    private static async Task<double> ProbeDurationAsync(string exe, string path, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(exe, new List<string> { "-hide_banner", "-nostdin", "-i", path }, ct: ct).ConfigureAwait(false);
        return ParseDuration(r.StdErr);
    }

    // ── 원격 API ────────────────────────────────────────────────────────────

    /// <summary>원격 제출 응답 검증 — {"id":"…"} 또는 {"job_id":"…"}(순수).</summary>
    public static string? ParseSubmit(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            foreach (var k in IdKeys)
                if (r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and <= 128 } id && IsSafeId(id)) return id;
            return null;
        }
        catch (JsonException) { return null; }
    }

    private static readonly string[] IdKeys = { "id", "job_id" };

    /// <summary>원격 상태 응답 검증 — status(queued/running/succeeded/failed) · output_url(https)(순수).</summary>
    public static (string Status, string? OutputUrl, string? Error) ParseStatus(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("status", out var st) || st.ValueKind != JsonValueKind.String) return ("invalid", null, "스키마 불일치");
            var status = (st.GetString() ?? "").ToLowerInvariant();
            string? url = null, err = null;
            if (r.TryGetProperty("output_url", out var u) && u.ValueKind == JsonValueKind.String
                && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback)) url = uri.AbsoluteUri;
            if (r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) err = e.GetString();
            status = status switch
            {
                "succeeded" or "success" or "completed" or "done" => "succeeded",
                "failed" or "error" or "canceled" or "cancelled" => "failed",
                "queued" or "pending" or "running" or "processing" or "in_progress" => "running",
                _ => "invalid",
            };
            if (status == "succeeded" && url is null) return ("invalid", null, "output_url 누락");
            return (status, url, err);
        }
        catch (JsonException) { return ("invalid", null, "JSON 파싱 실패"); }
    }

    private static bool IsSafeId(string id)
    {
        foreach (var c in id) if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')) return false;
        return true;
    }

    private async Task<(bool Ok, string Message)> RunRemoteAsync(LipSyncApiConfig cfg, AudioLipSyncRequest req, double mouth, List<VisemeCue> cues,
        IProgress<string>? status, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _remoteBusy, 1) != 0) return (false, "다른 원격 립싱크 진행 중");
        try
        {
            if (new FileInfo(req.VideoPath).Length > MaxUploadBytes) return (false, "영상이 업로드 상한(200MB) 초과");
            var baseUri = cfg.Endpoint!.TrimEnd('/');
            string? id = null;
            var delay = TimeSpan.FromSeconds(1);
            for (var attempt = 1; attempt <= MaxSubmitAttempts && id is null; attempt++)
            {
                using var form = new MultipartFormDataContent();
                await using var v = File.OpenRead(req.VideoPath);
                await using var a = File.OpenRead(req.AudioPath);
                form.Add(new StreamContent(v), "video", Path.GetFileName(req.VideoPath));
                form.Add(new StreamContent(a), "audio", Path.GetFileName(req.AudioPath));
                form.Add(new StringContent(cfg.Model == LipSyncBackend.MuseTalkApi ? "musetalk" : "wav2lip"), "model");
                form.Add(new StringContent(mouth.ToString("0.###", CultureInfo.InvariantCulture)), "mouth_scale");
                form.Add(new StringContent(TargetFps.ToString(CultureInfo.InvariantCulture)), "fps");
                form.Add(new StringContent(VisemesJson(cues)), "visemes");
                using var msg = new HttpRequestMessage(HttpMethod.Post, baseUri + "/lipsync") { Content = form };
                Auth(msg, cfg);
                using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) { id = ParseSubmit(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)); if (id is null) return (false, "제출 응답 스키마 불일치"); break; }
                if (!IsTransient(resp.StatusCode) || attempt == MaxSubmitAttempts) return (false, "HTTP " + ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture));
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay += delay;
            }
            if (id is null) return (false, "제출 실패");

            for (var i = 0; i < MaxPolls; i++)
            {
                await Task.Delay(_poll, ct).ConfigureAwait(false);
                using var msg = new HttpRequestMessage(HttpMethod.Get, baseUri + "/lipsync/" + id);
                Auth(msg, cfg);
                using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) { if (IsTransient(resp.StatusCode)) continue; return (false, "상태 HTTP " + ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture)); }
                var (st, url, err) = ParseStatus(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (st == "running") { status?.Report("☁️ 원격 립싱크 처리 중… (" + (i + 1).ToString(CultureInfo.InvariantCulture) + ")"); continue; }
                if (st != "succeeded") return (false, err ?? st);
                status?.Report("⬇️ 결과 다운로드 중…");
                var dir = Path.GetDirectoryName(Path.GetFullPath(req.OutputPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using var dl = await _http.GetAsync(url!, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!dl.IsSuccessStatusCode) return (false, "다운로드 HTTP " + ((int)dl.StatusCode).ToString(CultureInfo.InvariantCulture));
                await using (var src = await dl.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var dst = new FileStream(req.OutputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                return new FileInfo(req.OutputPath).Length > 0 ? (true, "ok") : (false, "빈 결과 파일");
            }
            return (false, "원격 처리 시간 초과");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Program.Log("AiLipSync.Remote", ex); return (false, ex.GetType().Name); }
        finally { Volatile.Write(ref _remoteBusy, 0); }
    }

    private static void Auth(HttpRequestMessage msg, LipSyncApiConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey)) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
    }

    private static bool IsTransient(HttpStatusCode c) => c is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)c >= 500;

    /// <summary>음소 타임라인 JSON(원격 가이드 · 순수).</summary>
    public static string VisemesJson(IReadOnlyList<VisemeCue> cues)
    {
        var sb = new StringBuilder(cues.Count * 40 + 2).Append('[');
        for (var i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"s\":").Append(Inv(c.Start)).Append(",\"e\":").Append(Inv(c.End)).Append(",\"v\":\"").Append(c.Shape.ToString())
              .Append("\",\"o\":").Append(Inv(c.Openness)).Append('}');
        }
        return sb.Append(']').ToString();
    }

    private static AudioLipSyncResult Fail(string msg) => new(false, null, LipSyncBackend.None, Array.Empty<VisemeCue>(), default, msg);
    private static string Inv(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public void Dispose() => _http.Dispose();
}
