using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Media;

/// <summary>v16.24.0 원본 나레이터 음색 프로필 — 중앙 F0(Hz) · F0 분산(사분위/중앙) · 밝기(영교차율) · RMS · 유성 비율 · 남성 추정.</summary>
public readonly record struct VoiceTimbreProfile(double MedianF0, double F0Spread, double Brightness, double Rms, double VoicedRatio, bool IsMale)
{
    public bool IsValid => MedianF0 > 0;
}

/// <summary>v16.24.0 다국어 더빙 요청.</summary>
public sealed record DubbingRequest(
    IReadOnlyList<string> Locales, bool CulturalAdapt = true, bool UseAi = true, bool TimbreMatch = true, bool CloneVoice = false,
    bool LipSync = false, bool Subtitles = true, bool BurnIn = true, NarrationTone Tone = NarrationTone.FairyTale, double Speed = 1.0,
    double OriginalVolume = 0.25, SafePlatform Platform = SafePlatform.YouTubeShorts, bool Zip = false, string? OutputRoot = null);

/// <summary>v16.24.0 로캘 1건 결과.</summary>
public sealed record DubbingLocaleResult(string Code, bool Ok, string? VideoPath, int VoicedLines, int StretchedLines, int SubtitleBlocks,
    bool AiLocalized, int Conversions, string Message);

/// <summary>v16.24.0 일괄 결과(1건 이상 성공 → Ok · [부분 구제]).</summary>
public sealed record DubbingBatchResult(bool Ok, string? OutputPath, IReadOnlyList<DubbingLocaleResult> Items, VoiceTimbreProfile? Timbre, string Message);

/// <summary>
/// [PromptFlowStudio] v16.24.0 🌐 다국어 AI 음성 복제 &amp; 더빙 합성 서비스 — 원본 나레이션 음색 분석 → 34개 로캘 감정 더빙 트랙 → 립싱크/자막 재정렬 → 로캘별 MP4 패키지.
///   ▸ 음색 분석: 완성 영상 오디오 30초를 ffmpeg 16k mono WAV 추출 → 40ms 창 · 20ms 홉 정규화 자기상관 F0(70~400Hz · ArrayPool · 무할당 루프)
///     + 영교차율(밝기) · RMS · 유성 비율 → 성별 · 피치 오프셋으로 대상 언어 Edge 음성(여/남) 매칭.
///   ▸ 음성 복제(선택 · 명시 동의): ElevenLabs Instant Voice Clone(<c>/v1/voices/add</c> · 키는 환경변수 · 타임아웃 60초 · 429/5xx 지수 백오프 · voice_id 즉시 검증)
///     → multilingual_v2 로 동일 음색 다국어 합성. 실패/미설정 → 음색 매칭 Edge 음성 폴백.
///   ▸ 감정 유지: 컷 감정(FacialEmotion × 강도) → 피치/속도/ElevenLabs style 가감.
///   ▸ 파이프라인(로캘별 순차): 현지화(<see cref="CulturalLocalizationTranspiler"/>) → 줄별 TTS → 길이 측정 → 타임 스트레칭 정렬(<see cref="MultilingualLipSyncAndSubtitleAligner"/>)
///     → 자막 SRT/ASS → 원본 오디오 덕킹(보이스오버 방식) + 더빙 믹스 + 번인 → (선택) viseme 타임라인 재정렬 저장 · AI 립싱크.
///   ▸ 출력: <c>DUB_yyyyMMdd_HHmmss/{code}/{code}.mp4 · .srt/.ass · localization.txt</c> + <c>dub_manifest.json</c> · (선택) ZIP. 로캘 실패는 건너뛰고 계속 · 동시 실행 1건 · 예외 0.
/// </summary>
public sealed class MultilingualVoiceCloningDubbingService : IDisposable
{
    public const int AnalysisSampleRate = 16000;
    public const int WindowSamples = 640;    // 40ms
    public const int HopSamples = 320;       // 20ms
    public const double MinF0 = 70, MaxF0 = 400;
    public const double FemaleBaseF0 = 205, MaleBaseF0 = 115, MaleThresholdF0 = 165;
    public const string CloneApi = "https://api.elevenlabs.io/v1/voices/add";
    public const int MaxCloneAttempts = 3;
    public const int MaxLagBuffer = 1024;
    public static readonly TimeSpan CloneTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<string?> _ffmpeg;
    private readonly AiVoiceoverTtsService? _tts;
    private readonly CulturalLocalizationTranspiler _loc;
    private readonly FfmpegAudioMixerService? _audio;
    private readonly AiAudioDrivenLipSyncService? _lip;
    private readonly Func<string?> _apiKey;
    private readonly HttpClient _http;
    private int _busy;
    private int _dubbed;
    private int _failed;
    private string? _cloneKey;
    private string? _cloneVoiceId;

    public MultilingualVoiceCloningDubbingService(IVideoMergeService merge, CulturalLocalizationTranspiler loc, AiVoiceoverTtsService? tts = null,
        FfmpegAudioMixerService? audio = null, AiAudioDrivenLipSyncService? lip = null)
        : this(() => merge.ResolvedFfmpegPath, loc, tts, audio, lip) { }

    /// <param name="ffmpegPath">ffmpeg 경로 공급자(테스트 주입).</param>
    public MultilingualVoiceCloningDubbingService(Func<string?> ffmpegPath, CulturalLocalizationTranspiler loc, AiVoiceoverTtsService? tts,
        FfmpegAudioMixerService? audio, AiAudioDrivenLipSyncService? lip, HttpMessageHandler? handler = null, Func<string?>? apiKey = null)
    {
        _ffmpeg = ffmpegPath;
        _loc = loc;
        _tts = tts;
        _audio = audio;
        _lip = lip;
        _apiKey = apiKey ?? (() => Environment.GetEnvironmentVariable(AiVoiceoverTtsService.ApiKeyEnv));
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = CloneTimeout;
    }

    public bool IsAvailable => _ffmpeg() is not null;
    public bool CanSpeak => _tts is { IsAvailable: true };
    public bool CanClone => !string.IsNullOrWhiteSpace(_apiKey());
    public bool CanLipSync => _lip is { AiAvailable: true };
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public int DubbedCount => Volatile.Read(ref _dubbed);
    public int FailedCount => Volatile.Read(ref _failed);
    public VoiceTimbreProfile? LastTimbre { get; private set; }
    public string? ClonedVoiceId => _cloneVoiceId;
    public string? LastAction { get; private set; }
    public CulturalLocalizationTranspiler Localizer => _loc;

    public event Action? OnChange;

    // ── 음색 분석(무할당 핫 루프) ────────────────────────────────────────────

    /// <summary>
    /// PCM16 → 음색 프로필. f0Scratch 는 프레임 수 이상(부족하면 앞부분만 사용). 무할당.
    /// </summary>
    public static VoiceTimbreProfile AnalyzeTimbre(ReadOnlySpan<short> pcm, int channels, int sampleRate, Span<float> f0Scratch)
    {
        var ch = Math.Max(1, channels);
        var frames = pcm.Length / ch;
        if (sampleRate <= 0 || frames < WindowSamples || f0Scratch.IsEmpty) return default;
        // 샘플레이트 비례 창/홉 · 지연 범위
        var win = (int)Math.Round(WindowSamples * (sampleRate / (double)AnalysisSampleRate));
        var hop = Math.Max(1, win / 2);
        var minLag = Math.Max(2, (int)(sampleRate / MaxF0));
        var maxLag = Math.Min(Math.Min(win - 1, (int)(sampleRate / MinF0)), MaxLagBuffer - 2);
        if (maxLag <= minLag) return default;
        Span<double> corr = stackalloc double[MaxLagBuffer];

        // 1) 전체 RMS · 노이즈 플로어(프레임 RMS 최소 근사) → 유성 게이트
        double sumSq = 0;
        for (var i = 0; i < frames; i++) { double s = pcm[i * ch]; sumSq += s * s; }
        var rms = Math.Sqrt(sumSq / frames) / 32768.0;
        if (rms < 1e-4) return new(0, 0, 0, rms, 0, false);
        var gate = rms * 0.5;

        int voiced = 0, total = 0, f0n = 0;
        long zc = 0, zcSamples = 0;
        for (var start = 0; start + win <= frames; start += hop)
        {
            total++;
            double e = 0;
            for (var k = 0; k < win; k++) { double s = pcm[(start + k) * ch]; e += s * s; }
            var frms = Math.Sqrt(e / win) / 32768.0;
            if (frms < gate) continue;
            // 영교차
            for (var k = 1; k < win; k++)
                if ((pcm[(start + k - 1) * ch] >= 0) != (pcm[(start + k) * ch] >= 0)) zc++;
            zcSamples += win;
            // 정규화 자기상관 → 전역 최대의 90% 이상인 첫 국소 피크(옥타브 낮춤 오류 방지)
            var best = 0.0;
            for (var lag = minLag - 1; lag <= maxLag + 1; lag++)
            {
                double num = 0, d0 = 0, d1 = 0;
                for (var k = 0; k + lag < win; k++)
                {
                    double a = pcm[(start + k) * ch], b = pcm[(start + k + lag) * ch];
                    num += a * b; d0 += a * a; d1 += b * b;
                }
                var r = d0 > 0 && d1 > 0 ? num / Math.Sqrt(d0 * d1) : 0;
                corr[lag] = r;
                if (lag >= minLag && lag <= maxLag && r > best) best = r;
            }
            if (best < 0.5) continue;
            var bestLag = 0;
            for (var lag = minLag; lag <= maxLag; lag++)
                if (corr[lag] >= best * 0.9 && corr[lag] >= corr[lag - 1] && corr[lag] >= corr[lag + 1]) { bestLag = lag; break; }
            if (bestLag == 0) continue;
            voiced++;
            if (f0n < f0Scratch.Length) f0Scratch[f0n++] = (float)(sampleRate / (double)bestLag);
        }
        if (f0n == 0) return new(0, 0, zcSamples > 0 ? zc / (double)zcSamples : 0, rms, 0, false);
        var f0 = f0Scratch[..f0n];
        f0.Sort();
        var median = f0[f0n / 2];
        var spread = (f0[Math.Min(f0n - 1, f0n * 3 / 4)] - f0[f0n / 4]) / Math.Max(1, median);
        var bright = zcSamples > 0 ? zc / (double)zcSamples : 0;
        return new(Math.Round(median, 1), Math.Round(spread, 3), Math.Round(bright, 4), Math.Round(rms, 4),
            Math.Round(voiced / (double)Math.Max(1, total), 3), median < MaleThresholdF0);
    }

    /// <summary>WAV 바이트 → 음색 프로필(ArrayPool 스크래치 · 실패 default).</summary>
    public static VoiceTimbreProfile AnalyzeWav(ReadOnlySpan<byte> wav)
    {
        if (!AiAudioDrivenLipSyncService.TryParseWav(wav, out var off, out var len, out var channels, out var rate, out var bits) || bits != 16) return default;
        var data = wav.Slice(off, Math.Min(len, wav.Length - off) & ~1);
        var pcm = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(data);
        if (!BitConverter.IsLittleEndian) return default;
        var frames = pcm.Length / Math.Max(1, channels);
        var maxFrames = Math.Max(1, frames / Math.Max(1, (int)(rate * 0.02)) + 1);
        var scratch = ArrayPool<float>.Shared.Rent(maxFrames);
        try { return AnalyzeTimbre(pcm, channels, rate, scratch.AsSpan(0, maxFrames)); }
        finally { ArrayPool<float>.Shared.Return(scratch); }
    }

    /// <summary>음색 + 로캘 + 감정 → 운율(Edge 음성 성별 · 피치 오프셋 · 속도 · ElevenLabs style). 순수.</summary>
    public static NarrationProsody MatchVoice(VoiceTimbreProfile? timbre, LocaleProfile locale, FacialEmotion emotion = FacialEmotion.Neutral,
        double intensity = 0.5, NarrationTone tone = NarrationTone.FairyTale, double speed = 1.0)
    {
        var baseP = AiVoiceoverTtsService.Prosody(tone, speed);
        var male = timbre is { IsValid: true, IsMale: true };
        var voice = male ? locale.EdgeMale : locale.EdgeFemale;
        var pitchHz = 0.0;
        if (timbre is { IsValid: true } t) pitchHz = (t.MedianF0 - (male ? MaleBaseF0 : FemaleBaseF0)) * 0.6;
        else pitchHz = ParseSigned(baseP.Pitch);
        var k = Math.Clamp(double.IsFinite(intensity) ? intensity : 0.5, 0, 1);
        var (ep, er, es) = emotion switch
        {
            FacialEmotion.HappySmile => (6.0, 5.0, 0.15),
            FacialEmotion.Giggle => (8.0, 8.0, 0.25),
            FacialEmotion.Surprised => (10.0, 6.0, 0.20),
            FacialEmotion.Curious => (4.0, -2.0, 0.10),
            FacialEmotion.Pout => (-4.0, -6.0, 0.10),
            _ => (0.0, 0.0, 0.0),
        };
        pitchHz = Math.Clamp(Math.Round(pitchHz + ep * k), -40, 40);
        var ratePct = Math.Clamp((int)Math.Round(ParseSigned(baseP.Rate) + er * k), -30, 30);
        var pitch = (pitchHz >= 0 ? "+" : "") + ((int)pitchHz).ToString(CultureInfo.InvariantCulture) + "Hz";
        var rate = (ratePct >= 0 ? "+" : "") + ratePct.ToString(CultureInfo.InvariantCulture) + "%";
        var stability = Math.Clamp(baseP.Stability - es * k, 0.2, 0.9);
        var style = Math.Clamp(baseP.Style + es * k, 0, 1);
        return new NarrationProsody(voice, rate, pitch, Math.Round(Math.Clamp(1 + ratePct / 100.0, 0.7, 1.2), 2), Math.Round(stability, 2), 0.85, Math.Round(style, 2));
    }

    /// <summary>ElevenLabs 복제 응답 검증(순수) — voice_id 영숫자 8~64자.</summary>
    public static string? ParseCloneResponse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("voice_id", out var v) || v.ValueKind != JsonValueKind.String) return null;
            var id = v.GetString();
            if (id is null || id.Length is < 8 or > 64) return null;
            foreach (var c in id) if (!char.IsAsciiLetterOrDigit(c)) return null;
            return id;
        }
        catch (JsonException) { return null; }
    }

    // ── 실행 ────────────────────────────────────────────────────────────────

    /// <summary>원본 영상/오디오 → 음색 프로필(앞 30초 · 실패 null · 예외 0).</summary>
    public async Task<VoiceTimbreProfile?> AnalyzeSourceAsync(string sourcePath, CancellationToken ct = default)
    {
        var wav = await ExtractSampleAsync(sourcePath, 30, ct).ConfigureAwait(false);
        if (wav is null) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(wav, ct).ConfigureAwait(false);
            var p = AnalyzeWav(bytes);
            if (!p.IsValid) return null;
            LastTimbre = p;
            Raise();
            return p;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Program.Log("Dubbing.Analyze", ex); return null; }
        finally { TryDelete(wav); }
    }

    /// <summary>ElevenLabs Instant Voice Clone(명시 동의 시에만 호출 · 같은 샘플은 재사용 · 실패 null).</summary>
    public async Task<string?> CloneVoiceAsync(string sourcePath, string name, CancellationToken ct = default)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key)) return null;
        var sample = await ExtractSampleAsync(sourcePath, 60, ct).ConfigureAwait(false);
        if (sample is null) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(sample, ct).ConfigureAwait(false);
            var cacheKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            if (_cloneKey == cacheKey && _cloneVoiceId is not null) return _cloneVoiceId;
            for (var attempt = 0; attempt < MaxCloneAttempts; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromMilliseconds(500 << (attempt - 1)), ct).ConfigureAwait(false);
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, CloneApi);
                    req.Headers.TryAddWithoutValidation("xi-api-key", key);
                    using var form = new MultipartFormDataContent();
                    form.Add(new StringContent(string.IsNullOrWhiteSpace(name) ? "PromptFlow Narrator" : name.Trim()), "name");
                    var file = new ByteArrayContent(bytes);
                    file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
                    form.Add(file, "files", "narrator.wav");
                    req.Content = form;
                    using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
                    if (res.StatusCode == HttpStatusCode.TooManyRequests || (int)res.StatusCode >= 500) continue;
                    if (!res.IsSuccessStatusCode) return null;
                    var id = ParseCloneResponse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    if (id is null) return null;
                    _cloneKey = cacheKey;
                    _cloneVoiceId = id;
                    Raise();
                    return id;
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { /* 타임아웃 → 재시도 */ }
                catch (HttpRequestException) { /* 네트워크 → 재시도 */ }
            }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Program.Log("Dubbing.Clone", ex); return null; }
        finally { TryDelete(sample); }
    }

    /// <summary>로캘 음성 미리듣기(샘플 문장 · 음색 매칭 · 복제 음성 우선 · 실패 null).</summary>
    public async Task<string?> PreviewAsync(LocaleProfile locale, CancellationToken ct = default)
    {
        if (_tts is null || !_tts.IsAvailable) return null;
        var p = MatchVoice(LastTimbre, locale);
        return await _tts.SynthesizeVoiceAsync(locale.Sample, p, _cloneVoiceId, TtsProvider.Auto, ct).ConfigureAwait(false);
    }

    /// <summary>다국어 더빙 일괄 내보내기(예외 0).</summary>
    public async Task<DubbingBatchResult> ExportAsync(StoryboardProject project, string masterVideo, DubbingRequest req,
        IProgress<PostProductionProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(req);
        if (Interlocked.Exchange(ref _busy, 1) != 0) return Done(Fail("이미 다국어 더빙이 진행 중입니다."));
        var work = Path.Combine(Path.GetTempPath(), "promptflow-dub", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var exe = _ffmpeg();
            if (exe is null) return Done(Fail("ffmpeg.exe 를 찾을 수 없어 다국어 더빙을 할 수 없습니다."));
            if (string.IsNullOrWhiteSpace(masterVideo) || !File.Exists(masterVideo)) return Done(Fail("원본 완성 영상이 없습니다 — 먼저 최종 영상을 내보내세요."));
            var locales = new List<LocaleProfile>(req.Locales.Count);
            foreach (var c in req.Locales)
                if (CulturalLocalizationTranspiler.LocaleOf(c) is { } l && l.Code != "ko-KR" && !locales.Contains(l)) locales.Add(l);
            if (locales.Count == 0) return Done(Fail("더빙할 언어를 1개 이상 선택하세요."));

            var probe = await ProcessRunner.RunAsync(exe, new List<string> { "-hide_banner", "-nostdin", "-i", masterVideo }, ct: ct).ConfigureAwait(false);
            var total = AiAudioDrivenLipSyncService.ParseDuration(probe.StdErr);
            if (total <= 0) return Done(Fail("원본 영상 길이를 읽지 못했습니다."));
            var (w, h) = StoryboardProject.ParseResolution(project.ExportResolution);
            var src = SourceLines(project, total);
            if (src.Lines.Count == 0) return Done(Fail("나레이션 대본(VoiceText)이 있는 컷이 없습니다."));

            progress?.Report(new(2, "🎙️ 원본 나레이터 음색 분석 중…"));
            VoiceTimbreProfile? timbre = req.TimbreMatch ? await AnalyzeSourceAsync(masterVideo, ct).ConfigureAwait(false) : null;
            string? cloneId = null;
            if (req.CloneVoice && CanClone)
            {
                progress?.Report(new(5, "🧬 ElevenLabs 음성 복제 중…"));
                cloneId = await CloneVoiceAsync(masterVideo, project.Title + " narrator", ct).ConfigureAwait(false);
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var root = req.OutputRoot ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(masterVideo))!, "DUB_" + stamp);
            Directory.CreateDirectory(root);
            var items = new List<DubbingLocaleResult>(locales.Count);
            var ok = 0;
            for (var i = 0; i < locales.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var loc = locales[i];
                var from = 8 + 88 * i / locales.Count;
                var to = 8 + 88 * (i + 1) / locales.Count;
                var r = await DubLocaleAsync(exe, project, masterVideo, total, w, h, src, loc, req, timbre, cloneId, root, work, from, to, progress, ct).ConfigureAwait(false);
                items.Add(r);
                if (r.Ok) { ok++; Interlocked.Increment(ref _dubbed); }
                else Interlocked.Increment(ref _failed);
                Raise();
            }
            if (ok == 0) return Done(new(false, null, items, timbre, "모든 언어 더빙 실패: " + (items.Count > 0 ? items[0].Message : "")));

            await WriteManifestAsync(root, project, req, items, timbre, cloneId is not null, ct).ConfigureAwait(false);
            var output = root;
            if (req.Zip)
            {
                progress?.Report(new(97, "🗜️ ZIP 패키징 중…"));
                output = root + ".zip";
                if (File.Exists(output)) File.Delete(output);
                ZipFile.CreateFromDirectory(root, output, CompressionLevel.Fastest, includeBaseDirectory: true);
                try { Directory.Delete(root, true); } catch { /* 폴더 정리 실패 무시(ZIP 완성) */ }
            }
            progress?.Report(new(100, "✅ 다국어 더빙 패키지 완성"));
            var voiceNote = cloneId is not null ? "복제 음성" : timbre is { } tp ? "음색 매칭(F0 " + tp.MedianF0.ToString("0", CultureInfo.InvariantCulture) + "Hz)" : "기본 음성";
            return Done(new(true, output, items, timbre, ok.ToString(CultureInfo.InvariantCulture) + "/" + locales.Count.ToString(CultureInfo.InvariantCulture) + " 언어 · " + voiceNote));
        }
        catch (OperationCanceledException) { return Done(Fail("취소됨")); }
        catch (Exception ex) { Program.Log("Dubbing.Export", ex); return Done(Fail("다국어 더빙 실패: " + ex.Message)); }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { /* 임시 정리 실패 무시 */ }
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>원본 대사 · 발화 시작(초) · 감정 — 완료 컷 타임라인(크로스페이드)을 실제 길이로 비례 보정. 완료 컷이 없으면 전 컷 균등 분배.</summary>
    public static (List<string> Lines, double[] Starts, FacialEmotion[] Emotions, double[] Intensities) SourceLines(StoryboardProject project, double totalSeconds)
    {
        var scenes = PostProductionEngine.ReadyScenes(project);
        if (scenes.Count == 0) scenes = project.Scenes;
        var cutStarts = new double[scenes.Count];
        if (scenes.Count > 0 && scenes[0].LocalPath is not null)
        {
            var inputs = new List<string>(scenes.Count);
            foreach (var s in scenes) inputs.Add(s.LocalPath ?? "");
            var tl = FfmpegVideoMixerService.Timeline(new VideoMixPlan(inputs, "", 1280, 720));
            var k = tl.TotalSeconds > 0 ? totalSeconds / tl.TotalSeconds : 1;
            for (var i = 0; i < cutStarts.Length && i < tl.CutStarts.Count; i++) cutStarts[i] = tl.CutStarts[i] * k;
        }
        else for (var i = 0; i < cutStarts.Length; i++) cutStarts[i] = totalSeconds * i / Math.Max(1, cutStarts.Length);

        var lines = new List<string>(scenes.Count);
        var starts = new List<double>(scenes.Count);
        var emo = new List<FacialEmotion>(scenes.Count);
        var inten = new List<double>(scenes.Count);
        for (var i = 0; i < scenes.Count; i++)
        {
            var text = scenes[i].VoiceText;
            if (string.IsNullOrWhiteSpace(text)) continue;
            var at = cutStarts[i] + AiVoiceoverTtsService.LeadInSeconds;
            if (at >= totalSeconds) continue;
            lines.Add(text.Trim());
            starts.Add(Math.Round(at, 3));
            emo.Add(scenes[i].Emotion);
            inten.Add(scenes[i].EmotionIntensity);
        }
        return (lines, starts.ToArray(), emo.ToArray(), inten.ToArray());
    }

    private async Task<DubbingLocaleResult> DubLocaleAsync(string exe, StoryboardProject project, string master, double total, int w, int h,
        (List<string> Lines, double[] Starts, FacialEmotion[] Emotions, double[] Intensities) src, LocaleProfile loc, DubbingRequest req,
        VoiceTimbreProfile? timbre, string? cloneId, string root, string work, int from, int to, IProgress<PostProductionProgress>? progress, CancellationToken ct)
    {
        var label = loc.Flag + " " + loc.Native;
        try
        {
            progress?.Report(new(from, label + " 현지화·각색 중…"));
            var cta = string.IsNullOrWhiteSpace(project.CoverTitle) ? project.Title : project.CoverTitle;
            var lz = await _loc.LocalizeAsync(src.Lines, cta, loc, req.CulturalAdapt, req.UseAi, null, ct).ConfigureAwait(false);
            var n = Math.Min(src.Lines.Count, lz.Lines.Count);

            // 줄별 합성 · 길이 측정
            var paths = new string?[n];
            var dubSec = new double[n];
            var voiced = 0;
            if (_tts is { IsAvailable: true })
            {
                for (var i = 0; i < n; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new(from + (to - from) * (i + 1) / (2 * Math.Max(1, n)), label + " 더빙 합성 " + (i + 1).ToString(CultureInfo.InvariantCulture) + "/" + n.ToString(CultureInfo.InvariantCulture)));
                    var p = MatchVoice(timbre, loc, src.Emotions[i], src.Intensities[i], req.Tone, req.Speed);
                    var path = await _tts.SynthesizeVoiceAsync(lz.Lines[i], p, cloneId, TtsProvider.Auto, ct).ConfigureAwait(false);
                    if (path is null) { dubSec[i] = MultilingualLipSyncAndSubtitleAligner.EstimateSeconds(lz.Lines[i], loc); continue; }
                    paths[i] = path;
                    voiced++;
                    dubSec[i] = AiVoiceoverTtsService.ProbeSeconds(path) ?? await ProbeAsync(exe, path, ct).ConfigureAwait(false);
                    if (dubSec[i] <= 0) dubSec[i] = MultilingualLipSyncAndSubtitleAligner.EstimateSeconds(lz.Lines[i], loc);
                }
            }
            else for (var i = 0; i < n; i++) dubSec[i] = MultilingualLipSyncAndSubtitleAligner.EstimateSeconds(lz.Lines[i], loc);

            // 정렬
            var timings = new DubTiming[n];
            MultilingualLipSyncAndSubtitleAligner.BuildTimings(src.Starts.AsSpan(0, n), dubSec, total, timings);
            var aligned = new AlignedDubCue[n];
            MultilingualLipSyncAndSubtitleAligner.Align(timings, aligned, total);
            var stretched = 0;
            foreach (var a in aligned) if (a.Tempo > 1.001) stretched++;

            var dir = Path.Combine(root, loc.Code);
            Directory.CreateDirectory(dir);
            var output = Path.Combine(dir, loc.Code + ".mp4");
            var cues = voiced > 0 ? await MultilingualLipSyncAndSubtitleAligner.ApplyAsync(exe, paths, lz.Lines, aligned, Path.Combine(work, loc.Code), ct).ConfigureAwait(false) : new List<VoiceCue>(0);

            // 자막
            (string AssPath, int Count)? sub = null;
            if (req.Subtitles)
                sub = await MultilingualLipSyncAndSubtitleAligner.WriteSubtitlesAsync(output, MultilingualLipSyncAndSubtitleAligner.ToCaptionTiming(aligned, lz.Lines), loc, req.Platform, w, h, ct).ConfigureAwait(false);
            var burn = sub is not null && req.BurnIn ? sub.Value.AssPath : null;
            var fonts = FontsDirOf(FontResolver.FindKoreanFont(true));

            // 믹스: 원본 오디오(덕킹 보이스오버 베드) + 더빙 + 번인
            progress?.Report(new(from + (to - from) * 3 / 4, label + " 믹싱·자막 번인 중…"));
            if (_audio is null) return new(loc.Code, false, null, voiced, stretched, sub?.Count ?? 0, lz.AiUsed, lz.Changes.Count, "오디오 믹서 미등록");
            var plan = cues.Count > 0
                ? new AudioMixPlan(master, output, total, master, Array.Empty<SfxCue>(), BgmVolume: Math.Clamp(req.OriginalVolume, 0, 1), BgmFadeInSeconds: 0, BgmFadeOutSeconds: 0,
                    Voice: cues, DuckRatio: 12, SubtitlePath: burn, FontsDir: fonts)
                : new AudioMixPlan(master, output, total, null, Array.Empty<SfxCue>(), KeepOriginalAudio: true, SubtitlePath: burn, FontsDir: fonts);
            var mr = await _audio.MixAsync(plan, null, ct).ConfigureAwait(false);
            if (!mr.Ok && !ct.IsCancellationRequested && plan.SubtitlePath is not null) mr = await _audio.MixAsync(plan with { SubtitlePath = null }, null, ct).ConfigureAwait(false);   // [부분 구제] 번인 실패
            if (!mr.Ok && !ct.IsCancellationRequested && cues.Count > 0) mr = await _audio.MixAsync(plan with { BgmPath = null, SubtitlePath = null }, null, ct).ConfigureAwait(false);   // 원본 무음 영상
            if (!mr.Ok) return new(loc.Code, false, null, voiced, stretched, sub?.Count ?? 0, lz.AiUsed, lz.Changes.Count, mr.Message);

            var lipNote = "";
            if (req.LipSync && cues.Count > 0)
                lipNote = await LipSyncAsync(exe, output, cues, aligned, lz.Lines, dir, loc, ct).ConfigureAwait(false);

            await File.WriteAllTextAsync(Path.Combine(dir, "localization.txt"), LocalizationText(lz, aligned), new UTF8Encoding(true), ct).ConfigureAwait(false);
            var msg = (voiced > 0 ? "더빙 " + voiced.ToString(CultureInfo.InvariantCulture) + "/" + n.ToString(CultureInfo.InvariantCulture) + "줄" : "TTS 없음 → 자막판")
                      + (stretched > 0 ? " · 스트레칭 " + stretched.ToString(CultureInfo.InvariantCulture) : "") + " · " + lz.Message + lipNote;
            return new(loc.Code, true, output, voiced, stretched, sub?.Count ?? 0, lz.AiUsed, lz.Changes.Count, msg);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Program.Log("Dubbing.Locale", ex); return new(loc.Code, false, null, 0, 0, 0, false, 0, "실패: " + ex.Message); }
    }

    // viseme 타임라인 재정렬 저장 + (AI 백엔드 있을 때만) 립싱크 — FFmpeg 보정 폴백은 전체 오디오를 이동시키므로 채택하지 않음.
    private async Task<string> LipSyncAsync(string exe, string output, List<VoiceCue> cues, AlignedDubCue[] aligned, IReadOnlyList<string> lines,
        string dir, LocaleProfile loc, CancellationToken ct)
    {
        if (_lip is null) return " · 립싱크 엔진 없음";
        var all = new List<VisemeCue>(cues.Count * 16);
        foreach (var c in cues)
        {
            var (_, vis) = await _lip.AnalyzeAudioAsync(c.Path, c.Text, 1.0, ct).ConfigureAwait(false);
            if (vis.Count == 0) continue;
            var arr = vis.ToArray();
            var tempo = 1.0;
            foreach (var a in aligned) if (Math.Abs(a.At - c.AtSeconds) < 1e-3) { tempo = a.Tempo; break; }
            MultilingualLipSyncAndSubtitleAligner.RetimeVisemes(arr, c.AtSeconds, c.Path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && tempo > 1.001 ? 1.0 : tempo);
            all.AddRange(arr);
        }
        var json = new StringBuilder(64 + all.Count * 48).Append("{\"locale\":\"").Append(loc.Code).Append("\",\"visemes\":[");
        for (var i = 0; i < all.Count; i++)
        {
            if (i > 0) json.Append(',');
            json.Append("{\"s\":").Append(FfmpegVideoMixerService.Inv(all[i].Start)).Append(",\"e\":").Append(FfmpegVideoMixerService.Inv(all[i].End))
                .Append(",\"v\":\"").Append(all[i].Shape.ToString()).Append("\",\"o\":").Append(FfmpegVideoMixerService.Inv(all[i].Openness)).Append('}');
        }
        json.Append("]}");
        await File.WriteAllTextAsync(Path.Combine(dir, loc.Code + "_visemes.json"), json.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        if (!_lip.AiAvailable) return " · viseme " + all.Count.ToString(CultureInfo.InvariantCulture) + "개(AI 립싱크 미설정)";

        var wav = Path.Combine(dir, loc.Code + "_dub.wav");
        var ex = await FfmpegVideoMixerService.RunAsync(exe, new List<string> { "-y", "-hide_banner", "-nostdin", "-i", output, "-vn", "-ac", "1", "-ar", "16000", wav }, wav, 0, null, ct).ConfigureAwait(false);
        if (!ex.Ok) return " · 립싱크 오디오 추출 실패";
        var lipOut = Path.Combine(dir, loc.Code + "_lipsync.mp4");
        var r = await _lip.SyncAsync(new AudioLipSyncRequest(output, wav, lipOut, string.Join(' ', lines)), null, ct).ConfigureAwait(false);
        TryDelete(wav);
        if (r.Ok && r.Backend != LipSyncBackend.FfmpegRealign && r.OutputPath is not null && File.Exists(r.OutputPath))
        {
            File.Move(r.OutputPath, output, overwrite: true);
            return " · 립싱크 " + AiAudioDrivenLipSyncService.LabelOf(r.Backend);
        }
        if (r.OutputPath is not null) TryDelete(r.OutputPath);
        return " · 립싱크 실패(원본 입모양 유지)";
    }

    /// <summary>로캘 메타데이터 텍스트(순수).</summary>
    public static string LocalizationText(LocalizationResult lz, ReadOnlySpan<AlignedDubCue> aligned)
    {
        var sb = new StringBuilder(512);
        sb.Append(lz.Locale.Flag).Append(' ').Append(lz.Locale.Name).Append(" (").Append(lz.Locale.Code).Append(") · ").Append(lz.Message).Append("\n\n");
        sb.Append("CTA: ").Append(lz.Cta).Append("\n\n[Lines]\n");
        for (var i = 0; i < lz.Lines.Count; i++)
        {
            sb.Append(i + 1).Append(". ");
            if (i < aligned.Length)
                sb.Append('[').Append(FfmpegVideoMixerService.Inv(aligned[i].At)).Append("s ×").Append(FfmpegVideoMixerService.Inv(aligned[i].Tempo)).Append("] ");
            sb.Append(lz.Lines[i]).Append('\n');
        }
        if (lz.Changes.Count > 0)
        {
            sb.Append("\n[Conversions]\n");
            foreach (var c in lz.Changes) sb.Append("- ").Append(c.Kind).Append(": ").Append(c.From).Append(" → ").Append(c.To).Append('\n');
        }
        if (lz.Notes.Count > 0)
        {
            sb.Append("\n[Cultural notes]\n");
            foreach (var n in lz.Notes) sb.Append("- ").Append(n).Append('\n');
        }
        return sb.ToString();
    }

    private async Task<string?> ExtractSampleAsync(string source, int seconds, CancellationToken ct)
    {
        var exe = _ffmpeg();
        if (exe is null || string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return null;
        var dir = Path.Combine(Path.GetTempPath(), "promptflow-dub");
        Directory.CreateDirectory(dir);
        var wav = Path.Combine(dir, "sample_" + Guid.NewGuid().ToString("N") + ".wav");
        var args = new List<string>(16) { "-y", "-hide_banner", "-nostdin", "-i", source, "-t", seconds.ToString(CultureInfo.InvariantCulture), "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav };
        var r = await FfmpegVideoMixerService.RunAsync(exe, args, wav, 0, null, ct).ConfigureAwait(false);
        return r.Ok ? wav : null;
    }

    private static async Task<double> ProbeAsync(string exe, string path, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(exe, new List<string> { "-hide_banner", "-nostdin", "-i", path }, ct: ct).ConfigureAwait(false);
        return AiAudioDrivenLipSyncService.ParseDuration(r.StdErr);
    }

    private static async Task WriteManifestAsync(string root, StoryboardProject project, DubbingRequest req, List<DubbingLocaleResult> items,
        VoiceTimbreProfile? timbre, bool cloned, CancellationToken ct)
    {
        var entries = new List<object>(items.Count);
        foreach (var it in items)
        {
            entries.Add(new
            {
                locale = it.Code, ok = it.Ok, video = it.VideoPath is null ? null : Path.GetRelativePath(root, it.VideoPath),
                voicedLines = it.VoicedLines, stretchedLines = it.StretchedLines, subtitleBlocks = it.SubtitleBlocks,
                aiLocalized = it.AiLocalized, conversions = it.Conversions, message = it.Message,
            });
        }
        var json = JsonSerializer.Serialize(new
        {
            version = "16.24.0", project = project.Title, culturalAdapt = req.CulturalAdapt, voiceClone = cloned,
            timbre = timbre is { } t ? new { medianF0 = t.MedianF0, spread = t.F0Spread, brightness = t.Brightness, male = t.IsMale } : null,
            createdAt = DateTime.Now.ToString("O", CultureInfo.InvariantCulture), locales = entries,
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        await File.WriteAllTextAsync(Path.Combine(root, "dub_manifest.json"), json, new UTF8Encoding(false), ct).ConfigureAwait(false);
    }

    private DubbingBatchResult Done(DubbingBatchResult r)
    {
        LastAction = (r.Ok ? "✅ " : "⚠ ") + r.Message;
        Raise();
        return r;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("Dubbing.OnChange", ex); }
    }

    private static DubbingBatchResult Fail(string msg) => new(false, null, Array.Empty<DubbingLocaleResult>(), null, msg);

    private static double ParseSigned(string s)
    {
        var span = s.AsSpan().Trim();
        var end = span.Length;
        while (end > 0 && !char.IsDigit(span[end - 1])) end--;
        return double.TryParse(span[..end], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static string? FontsDirOf(string? fontFile)
    {
        if (string.IsNullOrWhiteSpace(fontFile)) return null;
        try { return Path.GetDirectoryName(Path.GetFullPath(fontFile)); } catch { return null; }
    }

    private static void TryDelete(string? path)
    {
        try { if (path is not null && File.Exists(path)) File.Delete(path); } catch { /* 정리 실패 무시 */ }
    }

    public void Dispose() => _http.Dispose();
}
