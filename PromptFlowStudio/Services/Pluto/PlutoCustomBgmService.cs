using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Pluto.Dsp;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🎵 사용자 업로드 BGM → 15초 하이라이트 계약.</summary>
public interface IPlutoCustomBgmService
{
    /// <summary>
    /// 업로드 오디오(.wav/.mp3/.m4a/.aac/.ogg) → PCM WAV(비 WAV 는 FFmpeg 48kHz 16bit 스테레오 디코드) → 하이라이트 추출.
    /// <paramref name="manualStartSec"/> 지정 시 해당 시작점(경계 클램프) · null → 자동 클라이맥스 탐색.
    /// 실패 → <see cref="InvalidOperationException"/>(사용자 표시용 한국어 메시지) · 취소는 전파.
    /// </summary>
    Task<PlutoHighlightClipResult> LoadAndExtractHighlightAsync(string fileName, ReadOnlyMemory<byte> fileBytes, double? manualStartSec = null, CancellationToken ct = default);
}

/// <summary>
/// 🎵 커스텀 BGM 디코드 · 하이라이트 서비스 — RIFF WAV 는 C# 직통, 압축 포맷은 FFmpeg 디코드(임시 파일 finally 정리 · 60s 타임아웃 · 최대 8분).
/// 같은 파일 재요청(±1s 넛지)은 디코드 결과 단일 캐시 재사용(FFmpeg 재실행 없음).
/// </summary>
public sealed class PlutoCustomBgmService : IPlutoCustomBgmService
{
    public const long MaxUploadBytes = 25L * 1024 * 1024;
    public const string AcceptAttribute = ".mp3,.wav,.m4a,.aac,.ogg";
    public const int DecodeTimeoutSec = 60;
    /// <summary>FFmpeg 디코드 상한(초) — 장시간 음원의 메모리 폭주 방지.</summary>
    public const int MaxDecodeSec = 480;
    private static readonly string[] s_extensions = { ".wav", ".mp3", ".m4a", ".aac", ".ogg" };

    private readonly IPlutoAudioHighlightExtractor _extractor;
    private readonly Func<string?> _ffmpegResolver;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> _runner;
    private readonly string _tempDir;
    private readonly object _gate = new();
    private string? _cacheKey;
    private byte[]? _cacheWav;

    public PlutoCustomBgmService() : this(null, null, null, null) { }

    public PlutoCustomBgmService(IPlutoAudioHighlightExtractor? extractor, Func<string?>? ffmpegResolver = null,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<ProcessResult>>? runner = null, string? tempDir = null)
    {
        _extractor = extractor ?? PlutoAudioHighlightExtractor.Shared;
        _ffmpegResolver = ffmpegResolver ?? ResolveFfmpeg;
        _runner = runner ?? (static (exe, args, ct) => ProcessRunner.RunAsync(exe, args, null, null, ct));
        _tempDir = string.IsNullOrWhiteSpace(tempDir) ? Path.Combine(Path.GetTempPath(), "PromptFlowStudio", "custombgm") : tempDir;
    }

    public static bool IsSupportedExtension(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "");
        for (var i = 0; i < s_extensions.Length; i++)
            if (string.Equals(ext, s_extensions[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public async Task<PlutoHighlightClipResult> LoadAndExtractHighlightAsync(string fileName, ReadOnlyMemory<byte> fileBytes, double? manualStartSec = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (fileBytes.IsEmpty) throw new InvalidOperationException("빈 오디오 파일입니다.");
        if (fileBytes.Length > MaxUploadBytes) throw new InvalidOperationException("BGM 파일은 최대 25MB 까지 업로드할 수 있습니다.");
        var isWav = PlutoPcmWav.TryReadHeader(fileBytes.Span, out _);
        if (!isWav && !IsSupportedExtension(fileName)) throw new InvalidOperationException("지원 형식: .mp3 · .wav · .m4a · .aac · .ogg");

        var wav = isWav ? fileBytes : await DecodeCachedAsync(fileName, fileBytes, ct).ConfigureAwait(false);
        var extractor = _extractor;
        try
        {
            return await Task.Run(() => manualStartSec is { } s
                ? extractor.ExtractWindowAt(wav.Span, s)
                : extractor.ExtractBestHighlight(wav.Span), ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            Program.Log("Pluto.CustomBgm.Extract", ex);
            throw new InvalidOperationException("오디오를 분석할 수 없습니다: " + ex.Message, ex);
        }
    }

    private async Task<ReadOnlyMemory<byte>> DecodeCachedAsync(string fileName, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var key = fileName + "|" + bytes.Length.ToString(CultureInfo.InvariantCulture) + "|" + Convert.ToHexString(SHA256.HashData(bytes.Span));
        lock (_gate)
        {
            if (_cacheWav is not null && string.Equals(_cacheKey, key, StringComparison.Ordinal)) return _cacheWav;
        }
        var wav = await DecodeWithFfmpegAsync(fileName, bytes, ct).ConfigureAwait(false);
        lock (_gate) { _cacheKey = key; _cacheWav = wav; }
        return wav;
    }

    /// <summary>FFmpeg → 48kHz 16bit 스테레오 PCM WAV(<c>-y -i in -ar 48000 -ac 2 -c:a pcm_s16le out</c>). 임시 파일은 finally 정리.</summary>
    private async Task<byte[]> DecodeWithFfmpegAsync(string fileName, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var ffmpeg = _ffmpegResolver();
        if (string.IsNullOrWhiteSpace(ffmpeg))
            throw new InvalidOperationException("FFmpeg 를 찾을 수 없어 압축 오디오를 디코드할 수 없습니다 — WAV 파일을 업로드하거나 FFmpeg 를 설치해 주세요.");
        var ext = Path.GetExtension(fileName ?? "");
        if (!IsSupportedExtension(ext)) ext = ".bin";
        var id = Guid.NewGuid().ToString("N");
        var input = Path.Combine(_tempDir, "in_" + id + ext.ToLowerInvariant());
        var output = Path.Combine(_tempDir, "out_" + id + ".wav");
        try
        {
            Directory.CreateDirectory(_tempDir);
            await using (var fs = new FileStream(input, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
            string[] args =
            [
                "-y", "-hide_banner", "-loglevel", "error", "-i", input, "-vn",
                "-t", MaxDecodeSec.ToString(CultureInfo.InvariantCulture),
                "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le", output,
            ];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(DecodeTimeoutSec));
            ProcessResult res;
            try { res = await _runner(ffmpeg, args, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException("오디오 디코드 시간이 초과되었습니다."); }
            ct.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) throw new InvalidOperationException("오디오 디코드 시간이 초과되었습니다.");
            if (!res.Ok || !File.Exists(output))
                throw new InvalidOperationException("오디오 디코드 실패: " + Tail(res.StdErr));
            var wav = await File.ReadAllBytesAsync(output, ct).ConfigureAwait(false);
            if (!PlutoPcmWav.TryReadHeader(wav, out _)) throw new InvalidOperationException("디코드된 오디오가 올바른 WAV 가 아닙니다.");
            return wav;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("Pluto.CustomBgm.Decode", ex);
            throw new InvalidOperationException("오디오 임시 파일 처리 실패: " + ex.Message, ex);
        }
        finally
        {
            TryDelete(input);
            TryDelete(output);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return null; }
    }

    private static string Tail(string? s, int n = 300)
        => string.IsNullOrEmpty(s) ? "(stderr 없음)" : (s.Length <= n ? s.Trim() : "…" + s[^n..].Trim());

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("Pluto.CustomBgm.Cleanup", ex); }
    }
}
