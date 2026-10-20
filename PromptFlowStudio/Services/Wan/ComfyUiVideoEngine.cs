using System;
using System.Collections.Generic;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // EngineConfigService · ProcessRunner

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// [PromptFlowStudio] 로컬 비디오 생성 엔진 플러그인 — ComfyUI/CogVideoX 등 로컬 CLI 를 <see cref="IVideoGenerationEngine"/> 로 감싼다(외부 NuGet 0).
///   ▸ 설정: EngineConfigService <c>VideoCli</c>(Exe/Args). Args 템플릿의 {prompt}{size}{out} 을 치환해 CLI 를 실행, <b>로컬 mp4</b> 를 만든다.
///   ▸ 결과의 VideoUrl 은 로컬 파일 경로 — LocalMediaService.DownloadAsync 가 로컬 경로를 인식해 미디어 루트로 복사한다.
///   ▸ Exe 미설정이면 <see cref="Enabled"/>=false(선택돼도 JobManager 가 건너뜀). 실행 실패는 Graceful(Ok=false).
///   ▸ v16.0.0 Titan 로컬 I2V: <see cref="WanVideoRequest.ImagePath"/>(로컬) 또는 http(s) <see cref="WanVideoRequest.ImageUrl"/> 를
///     임시 폴더로 스트리밍 다운로드(풀 버퍼 · 전체 로드 없음)해 {image} 자리표시자(ComfyUI LoadImage 노드 입력)로 주입 — 템플릿에 없으면 <c>--image</c> 로 덧붙인다.
///   ⚠ 업로드 API 는 미지원(로컬 엔진은 참조 이미지를 경로로 받음) — <see cref="UploadImageAsync"/> 는 Ok=false.
/// </summary>
public sealed class ComfyUiVideoEngine(EngineConfigService engines) : IVideoGenerationEngine
{
    public string Key => "ComfyUI";
    public string DisplayName => "로컬 CLI (ComfyUI/CogVideoX)";

    /// <summary>참조 이미지 다운로드 상한(32MB) — 초과 응답은 i2v 를 포기하고 t2v 로 진행.</summary>
    public const long MaxImageBytes = 32L * 1024 * 1024;
    private const int DownloadAttempts = 3;
    private const string ImageVar = "{image}";

    // 다운로드 전용(요청 타임아웃 30s · 지수 백오프 재시도는 PrepareI2vImageAsync 가 담당).
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private string? Exe => engines.Resolve(EngineKeys.VideoCli);
    public bool Enabled => Exe is not null;

    public async Task<WanGenerationResult> GenerateAsync(WanVideoRequest request, IProgress<WanProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var exe = Exe;
        if (exe is null)
            return new(false, WanTaskStatus.Failed, null, "로컬 비디오 CLI 가 설정되지 않았습니다(관리자 설정 › AI 엔진 › 로컬 비디오 CLI).", null, sw.Elapsed);

        var prompt = (request.Prompt ?? "").Trim();
        if (prompt.Length == 0)
            return new(false, WanTaskStatus.Failed, null, "프롬프트가 비어 있습니다.", null, sw.Elapsed);
        request = ContextAwarePromptGuard.Process(request);   // v16.9.0: Artifact Guard(로컬 엔진 동일 적용)
        prompt = request.Prompt.Trim();

        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "promptflow-videocli");
            Directory.CreateDirectory(dir);
            var outPath = Path.Combine(dir, $"gen_{Guid.NewGuid():N}.mp4");

            // v16.0.0: 참조 이미지(I2V) 확보 — 실패 시 t2v 로 Graceful 진행.
            var wantsI2v = !string.IsNullOrWhiteSpace(request.ImagePath) || !string.IsNullOrWhiteSpace(request.ImageUrl);
            if (wantsI2v) progress?.Report(new(WanTaskStatus.Running, "참조 이미지 준비 중(로컬 I2V)…", 10, 0, sw.Elapsed, null));
            var (imagePath, ownsImage) = wantsI2v ? await PrepareI2vImageAsync(request, s_http, dir, ct).ConfigureAwait(false) : (null, false);
            try
            {
            if (ct.IsCancellationRequested)
                return new(false, WanTaskStatus.Canceled, null, "취소됨", null, sw.Elapsed);
            progress?.Report(new(WanTaskStatus.Running,
                imagePath is not null ? "로컬 엔진 생성 중(I2V)…" : wantsI2v ? "⚠️ 참조 이미지 확보 실패 → 로컬 T2V 생성 중…" : "로컬 엔진 생성 중…", 20, 0, sw.Elapsed, null));

            var template = BuildI2vTemplate(engines.Args(EngineKeys.VideoCli), imagePath);
            var vars = new Dictionary<string, string>
            {
                ["prompt"] = prompt,
                ["size"] = string.IsNullOrWhiteSpace(request.Size) ? "1280*720" : request.Size!,
                ["out"] = outPath,
            };
            // v15.4.0: 튜너 값은 템플릿이 자리표시자를 쓸 때만 반영({steps} {cfg} {motion} {seed} {negative} {face_sharpen} {interp60}).
            if (request.Tuning is WanTuning t)
            {
                vars["steps"] = t.Steps.ToString(System.Globalization.CultureInfo.InvariantCulture);
                vars["cfg"] = t.CfgScale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                vars["motion"] = t.MotionBucket.ToString(System.Globalization.CultureInfo.InvariantCulture);
                vars["face_sharpen"] = t.FaceSharpen ? "1" : "0";
                vars["interp60"] = t.Interpolate60 ? "1" : "0";
            }
            if (request.Seed is int seed) vars["seed"] = seed.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(request.NegativePrompt)) vars["negative"] = request.NegativePrompt!.Trim();
            if (imagePath is not null) vars["image"] = imagePath;
            vars["mode"] = imagePath is not null ? "i2v" : "t2v";
            var args = ProcessRunner.Substitute(template, vars);

            var r = await ProcessRunner.RunAsync(exe, args, workingDir: Path.GetDirectoryName(exe), ct: ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                return new(false, WanTaskStatus.Canceled, null, "취소됨", null, sw.Elapsed);
            if (!r.Ok || !File.Exists(outPath) || new FileInfo(outPath).Length == 0)
                return new(false, WanTaskStatus.Failed, null, "로컬 엔진 생성 실패: " + Tail(r.StdErr), null, sw.Elapsed);

            progress?.Report(new(WanTaskStatus.Succeeded, "완성!", 100, 0, sw.Elapsed, null));
            // ⚠ VideoUrl 에 로컬 경로 반환 — DownloadAsync 가 로컬 파일을 인식해 미디어 루트로 복사한다.
            return new(true, WanTaskStatus.Succeeded, outPath, imagePath is not null ? "완료(로컬 엔진 · I2V)" : "완료(로컬 엔진)", null, sw.Elapsed);
            }
            finally
            {
                if (ownsImage && imagePath is not null) TryDelete(imagePath);   // 다운로드 임시 이미지 정리
            }
        }
        catch (OperationCanceledException) { return new(false, WanTaskStatus.Canceled, null, "취소됨", null, sw.Elapsed); }
        catch (Exception ex) { Program.Log("ComfyUiVideoEngine.Generate", ex); return new(false, WanTaskStatus.Failed, null, "로컬 엔진 실패: " + ex.Message, null, sw.Elapsed); }
    }

    /// <summary>로컬 엔진은 i2v 참조 이미지 업로드를 지원하지 않는다(Graceful).</summary>
    public Task<WanUploadResult> UploadImageAsync(string localImagePath, string? model = null, CancellationToken ct = default)
        => Task.FromResult(new WanUploadResult(false, null, "로컬 엔진은 이미지 업로드를 지원하지 않습니다."));

    /// <summary>
    /// I2V 참조 이미지를 로컬 경로로 확보. 우선순위: 존재하는 <see cref="WanVideoRequest.ImagePath"/> → 로컬 경로형 ImageUrl → http(s) 스트리밍 다운로드.
    /// oss:// 등 클라우드 전용 URL·4xx·상한 초과·재시도 소진은 (null,false). <c>Owned</c>=true 면 호출자가 임시 파일을 삭제한다.
    /// </summary>
    public static async Task<(string? Path, bool Owned)> PrepareI2vImageAsync(WanVideoRequest request, HttpClient http, string dir, CancellationToken ct)
    {
        var local = request.ImagePath?.Trim();
        if (!string.IsNullOrEmpty(local) && File.Exists(local)) return (Path.GetFullPath(local), false);

        var url = request.ImageUrl?.Trim();
        if (string.IsNullOrEmpty(url)) return (null, false);
        if (!url.Contains("://", StringComparison.Ordinal) && File.Exists(url)) return (Path.GetFullPath(url), false);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return (null, false);   // oss:// 는 DashScope 내부 전용 — 로컬에서 받을 수 없음

        var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp")) ext = ".png";
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "i2v_" + Guid.NewGuid().ToString("N") + ext);

        for (int attempt = 0; attempt < DownloadAttempts; attempt++)
        {
            if (attempt > 0) await Task.Delay(250 << attempt, ct).ConfigureAwait(false);   // 지수 백오프 500ms · 1s
            try
            {
                using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    if ((int)resp.StatusCode >= 500 || resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout) continue;
                    return (null, false);
                }
                if (resp.Content.Headers.ContentLength is long len && len > MaxImageBytes) return (null, false);

                if (await StreamToFileAsync(resp, path, ct).ConfigureAwait(false) > 0) return (path, true);
                TryDelete(path);
                return (null, false);
            }
            catch (HttpRequestException) { TryDelete(path); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { TryDelete(path); }   // HttpClient 타임아웃
            catch (InvalidDataException) { TryDelete(path); return (null, false); }                       // 상한 초과(Content-Length 미제공)
        }
        TryDelete(path);
        return (null, false);
    }

    /// <summary>
    /// 인자 템플릿에 I2V 이미지 결선. 이미지 있음: {image} 자리표시자가 없으면 <c>--image {image}</c> 를 덧붙임.
    /// 이미지 없음: {image} 요소와 바로 앞의 플래그(<c>-</c> 시작)를 제거해 t2v 인자를 유지.
    /// </summary>
    public static IReadOnlyList<string> BuildI2vTemplate(IReadOnlyList<string> template, string? imagePath)
    {
        int idx = -1;
        for (int i = 0; i < template.Count; i++)
            if (template[i].Contains(ImageVar, StringComparison.Ordinal)) { idx = i; break; }

        if (imagePath is not null)
        {
            if (idx >= 0) return template;
            var withImage = new List<string>(template.Count + 2);
            withImage.AddRange(template);
            withImage.Add("--image");
            withImage.Add(ImageVar);
            return withImage;
        }
        if (idx < 0) return template;
        var stripped = new List<string>(template.Count);
        for (int i = 0; i < template.Count; i++)
        {
            if (template[i].Contains(ImageVar, StringComparison.Ordinal)) continue;
            if (i + 1 < template.Count && template[i].StartsWith('-') && template[i + 1].Contains(ImageVar, StringComparison.Ordinal)) continue;
            stripped.Add(template[i]);
        }
        return stripped;
    }

    // 풀 버퍼(81920B) 청크 복사 — 응답 본문을 메모리에 올리지 않는다. 상한 초과 시 InvalidDataException.
    private static async Task<long> StreamToFileAsync(HttpResponseMessage resp, string path, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long total = 0;
            int n;
            while ((n = await src.ReadAsync(buffer.AsMemory(0, 81920), ct).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > MaxImageBytes) throw new InvalidDataException("참조 이미지가 상한을 초과했습니다.");
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            }
            return total;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Tail(string s, int n = 300) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s.Trim() : "…" + s[^n..].Trim());
}
