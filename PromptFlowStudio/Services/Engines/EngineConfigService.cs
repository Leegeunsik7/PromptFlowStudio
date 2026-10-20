using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot()

namespace PromptFlowStudio.Services.Engines;

/// <summary>엔진 연결 테스트 결과(성공 여부 + 사람이 읽는 메시지).</summary>
public sealed record EngineTestResult(bool Ok, string Message);

/// <summary>UI 카드 구성을 위한 엔진 기술서(키·표시명·분류·경로 힌트·기본 인자).</summary>
public sealed record EngineDescriptor(string Key, string Name, string Category, string Icon,
    IReadOnlyList<string> PathHints, IReadOnlyList<string> DefaultArgs, string Note);

/// <summary>엔진 설정 키(appsettings <c>Engines:{Key}</c> 와 일치).</summary>
public static class EngineKeys
{
    public const string Kokoro = "Kokoro";
    public const string F5Tts = "F5Tts";
    public const string RealEsrgan = "RealEsrgan";
    public const string RealCugan = "RealCugan";
    public const string FasterWhisper = "FasterWhisper";
    public const string LatentSync = "LatentSync";
    public const string VideoCli = "VideoCli";   // 로컬 비디오 생성 CLI(ComfyUI/CogVideoX 래퍼 등) — 플러그인 엔진
    public const string FoleyAudio = "FoleyAudio";   // 생성형 폴리(텍스트→효과음) CLI — 미설정 시 내장 Sfx 라이브러리로 폴백
}

/// <summary>
/// [PromptFlowStudio] AI 엔진 동적 설정 저장소 — 관리자 UI 에서 편집한 각 엔진의 Exe 경로/Args 를 재시작 없이 적용(외부 NuGet 0).
///   ▸ 권위 저장소: <c>%LocalAppData%\PromptFlowStudio\engines.json</c>(항상 쓰기 가능 · 앱 업데이트에도 보존) — 구조보존 <see cref="JsonNode"/>.
///     추가로 앱 폴더 <c>appsettings.json</c> 의 <c>Engines</c> 도 best-effort 미러링(쓰기 가능할 때 · 실패해도 engines.json 이 권위).
///   ▸ 초기 스냅샷: appsettings <c>Engines:*</c>(개발 시드) → engines.json 오버레이(사용자 저장이 우선).
///   ▸ 재시작 불필요: 현재값을 volatile <see cref="_s"/> 스냅샷으로 들고, 저장 시 원자적 교체 + <see cref="OnChange"/> 방송 → 4대 엔진 서비스가 즉시 새 값을 읽음.
///   ▸ thread-safe: 쓰기 lock 직렬화(+파일 원자 교체), 읽기 lock-free. WanConfigService 의 동적 설정 패턴과 동일 계열.
/// </summary>
public sealed class EngineConfigService
{
    /// <summary>UI 카드로 노출할 엔진 목록(순서 = 표시 순서).</summary>
    public static readonly IReadOnlyList<EngineDescriptor> Descriptors = new[]
    {
        new EngineDescriptor(EngineKeys.Kokoro, "Kokoro-82M", "오프라인 TTS", "🗣️",
            new[] { "kokoro-tts", "kokoro" },
            new[] { "{textfile}", "{out}", "--voice", "{voice}" },
            "초경량 오프라인 음성합성(Apache 2.0). 나레이션 1순위. 예: kokoro-tts.exe"),
        new EngineDescriptor(EngineKeys.F5Tts, "F5-TTS", "오프라인 TTS(대체)", "🎙️",
            Array.Empty<string>(),
            new[] { "-m", "f5_tts.infer.infer_cli", "--gen_text_file", "{textfile}", "--output_file", "{out}" },
            "Kokoro 미설정 시 폴백(MIT). Exe=python.exe, Args 에 추론 모듈 지정."),
        new EngineDescriptor(EngineKeys.RealEsrgan, "Real-ESRGAN", "4K 업스케일(실사)", "🖼️",
            new[] { "realesrgan-ncnn-vulkan" },
            new[] { "-i", "{in}", "-o", "{out}", "-s", "{scale}", "-n", "{model}", "-f", "png" },
            "실사 영상 4K 업스케일(BSD 3-Clause). realesrgan-ncnn-vulkan.exe 권장(무파이썬)."),
        new EngineDescriptor(EngineKeys.RealCugan, "Real-CUGAN", "4K 업스케일(2D/애니)", "🎨",
            new[] { "realcugan-ncnn-vulkan" },
            new[] { "-i", "{in}", "-o", "{out}", "-s", "{scale}", "-f", "png" },
            "2D/애니 업스케일(MIT). realcugan-ncnn-vulkan.exe."),
        new EngineDescriptor(EngineKeys.FasterWhisper, "Faster-Whisper", "오프라인 STT(자막)", "📝",
            new[] { "whisper-faster", "faster-whisper", "whisper-ctranslate2" },
            new[] { "{in}", "--model", "{model}", "--language", "{lang}", "--output_format", "srt", "--output_dir", "{outdir}" },
            "오디오→.srt 자막 추출(MIT). Purfview whisper-faster.exe 권장."),
        new EngineDescriptor(EngineKeys.LatentSync, "LatentSync", "립싱크", "👄",
            new[] { "latentsync" },
            new[] { "-m", "scripts.inference", "--video_path", "{video}", "--audio_path", "{audio}", "--video_out_path", "{out}" },
            "인물 입모양 동기화(Apache 2.0). Exe=python.exe, Args 에 추론 스크립트/체크포인트 지정."),
        new EngineDescriptor(EngineKeys.VideoCli, "로컬 비디오 CLI", "비디오 생성 엔진(플러그인)", "🎬",
            Array.Empty<string>(),
            new[] { "--prompt", "{prompt}", "--size", "{size}", "--output", "{out}" },
            "Wan 대신 쓸 로컬 비디오 생성 CLI(ComfyUI/CogVideoX 래퍼 등). Exe/Args 지정 후 [🎬 비디오 엔진]에서 선택. {prompt}{size}{out} 치환."),
        new EngineDescriptor(EngineKeys.FoleyAudio, "생성형 폴리(Gen-Foley)", "효과음 생성", "🔊",
            new[] { "audiogen", "stable-audio" },
            new[] { "--prompt", "{prompt}", "--output", "{out}", "--seconds", "{seconds}" },
            "텍스트→효과음 생성 CLI(AudioGen/Stable-Audio 래퍼 등). {prompt}{out}{seconds} 치환. 미설정 시 내장 Sfx 라이브러리로 폴백."),
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 한글 주석/값을 \uXXXX 로 깨지 않게(가독성)
    };

    private readonly object _gate = new();
    private readonly string _path;             // 권위: 데이터영역 engines.json
    private readonly string _appsettingsPath;  // best-effort 미러: 앱 폴더 appsettings.json
    private volatile IReadOnlyDictionary<string, EngineState> _s;
    private volatile string _videoEngine = "Wan";   // 선택된 비디오 생성 엔진 키(기본 Wan · engines.json 의 "VideoEngine")

    public event Action? OnChange;
    public string FilePath => _path;
    public DateTime? SavedAt { get; private set; }

    public EngineConfigService(IConfiguration cfg)
    {
        _path = Path.Combine(LicenseService.DataAreaRoot(), "engines.json");
        _appsettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        _s = LoadInitial(cfg);
        _videoEngine = LoadVideoEngine(cfg);
    }

    /// <summary>선택된 비디오 생성 엔진 키(기본 "Wan").</summary>
    public string VideoEngineKey => _videoEngine;

    /// <summary>비디오 생성 엔진 선택을 변경·영속(engines.json 의 최상위 "VideoEngine"). OnChange 방송.</summary>
    public void SetVideoEngine(string key)
    {
        var k = string.IsNullOrWhiteSpace(key) ? "Wan" : key.Trim();
        lock (_gate)
        {
            _videoEngine = k;
            try
            {
                var root = ReadRoot(_path);
                root["VideoEngine"] = k;
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, root.ToJsonString(JsonOpts), new UTF8Encoding(false));
                File.Copy(tmp, _path, overwrite: true);
                try { File.Delete(tmp); } catch { /* 무시 */ }
            }
            catch (Exception ex) { Program.Log("EngineConfig.SetVideoEngine", ex); }
        }
        OnChange?.Invoke();
    }

    private string LoadVideoEngine(IConfiguration cfg)
    {
        try
        {
            if (File.Exists(_path))
            {
                var v = (JsonNode.Parse(File.ReadAllText(_path)) as JsonObject)?["VideoEngine"];
                if (v is JsonValue jv && jv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        }
        catch (Exception ex) { Program.Log("EngineConfig.LoadVideoEngine", ex); }
        var seed = cfg["Engines:VideoEngine"];
        return string.IsNullOrWhiteSpace(seed) ? "Wan" : seed.Trim();
    }

    private static EngineDescriptor? Desc(string key) =>
        Descriptors.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    // ── 조회(읽기 lock-free) ──
    /// <summary>설정된 원본 Exe 경로(미해석). 없으면 "".</summary>
    public string Exe(string key) => _s.TryGetValue(key, out var e) ? e.Exe : "";

    /// <summary>실행 인자 템플릿(스냅샷 · 비면 기본값).</summary>
    public IReadOnlyList<string> Args(string key)
    {
        if (_s.TryGetValue(key, out var e) && e.Args.Count > 0) return e.Args;
        return Desc(key)?.DefaultArgs ?? Array.Empty<string>();
    }

    /// <summary>표시/편집용 Args(공백 결합 한 줄).</summary>
    public string ArgsLine(string key) => string.Join(" ", Args(key));

    /// <summary>부가 설정(Voice/Model/Language 등 · appsettings 시드에서 보존). 없으면 fallback.</summary>
    public string? Extra(string key, string extraKey, string? fallback = null)
        => _s.TryGetValue(key, out var e) && e.Extras.TryGetValue(extraKey, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    /// <summary>Exe 를 실제 실행 파일 경로로 해석 — <see cref="CliPathResolver"/> 3단계(사용자설정→앱번들 Tools→로컬앱데이터 Tools→PATH). 없으면 null.</summary>
    public string? Resolve(string key) => ResolveDetailed(key).Path;

    /// <summary>해석 결과 + 출처(뱃지/진단용). <paramref name="exeOverride"/>(편집 중 값)가 있으면 그걸 커스텀 경로로 사용(없으면 저장값).</summary>
    public CliResolution ResolveDetailed(string key, string? exeOverride = null)
        => CliPathResolver.Resolve(key, Desc(key)?.PathHints ?? Array.Empty<string>(), string.IsNullOrWhiteSpace(exeOverride) ? Exe(key) : exeOverride);

    /// <summary>앱번들/로컬앱데이터 <c>Tools\{key}\</c> 에서만 찾은 경로 — UI 의 '기본 번들 경로' 연한 표시(placeholder)용. 없으면 null.</summary>
    public string? BundleHint(string key) => CliPathResolver.GetBundlePath(key, Desc(key)?.PathHints ?? Array.Empty<string>());

    /// <summary>경로가 유효해 실행 파일을 찾을 수 있는지(서비스 IsAvailable 판정 기준). 이제 번들 자동탐색 포함(Zero-Config).</summary>
    public bool IsConfigured(string key) => Resolve(key) is not null;

    /// <summary>저장된 경로로 연결 테스트(<see cref="TestPathAsync"/> 위임).</summary>
    public Task<EngineTestResult> TestConnectionAsync(string key, CancellationToken ct = default)
        => TestPathAsync(key, Exe(key), ct);

    /// <summary>
    /// 엔진 연결 테스트 — <paramref name="exeRaw"/>(편집 중인 경로도 가능) 존재 검증 후 <c>--version</c>/<c>--help</c> 로 CLI 실행 확인(각 20초 타임아웃).
    ///   ExitCode 0 이면 명확히 연결됨. 파일은 실행되나 표준 버전 플래그가 없으면 '실행 확인됨'(경로 유효)으로 판정. 저장 없이 즉시 검증 가능.
    /// </summary>
    public async Task<EngineTestResult> TestPathAsync(string key, string exeRaw, CancellationToken ct = default)
    {
        // ⚠ CliPathResolver 로 해석 — exeRaw 가 비어 있어도 번들(Tools\)에서 자동 탐색되면 테스트 가능(Zero-Config).
        var exe = CliPathResolver.Resolve(key, Desc(key)?.PathHints ?? Array.Empty<string>(), exeRaw).Path;
        if (exe is null)
        {
            return new(false, string.IsNullOrWhiteSpace(exeRaw)
                ? "실행 파일을 찾을 수 없습니다(경로 미지정 · 기본 번들 Tools 폴더에도 없음)."
                : $"실행 파일을 찾을 수 없습니다: {exeRaw}");
        }

        var started = false;
        foreach (var flag in new[] { "--version", "--help", "-h" })
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var r = await ProcessRunner.RunAsync(exe, new[] { flag }, ct: cts.Token).ConfigureAwait(false);
            if (r.Started) started = true; else continue;
            if (r.ExitCode == 0)
            {
                var ver = FirstLine(r.StdOut) ?? FirstLine(r.StdErr);
                return new(true, $"연결됨 · {flag} 확인" + (string.IsNullOrEmpty(ver) ? "" : $" · {Trunc(ver!)}"));
            }
        }
        return started
            ? new(true, "실행 확인됨(경로 유효 · 표준 --version/--help 미지원 엔진일 수 있음).")
            : new(false, "실행 파일은 있으나 프로세스를 시작할 수 없습니다(권한/아키텍처 확인).");
    }

    /// <summary>여러 엔진의 Exe/Args 를 한 번에 저장(원자적) → 스냅샷 교체 → OnChange 방송(재시작 불필요).</summary>
    public bool SaveAll(IEnumerable<(string Key, string Exe, string ArgsLine)> edits)
    {
        var list = edits
            .Select(e => (e.Key, Exe: (e.Exe ?? "").Trim().Trim('"'), Args: SplitArgs(e.ArgsLine)))
            .ToList();

        lock (_gate)
        {
            try
            {
                PersistTo(_path, list);                                   // 권위: engines.json
                try { PersistTo(_appsettingsPath, list); }               // best-effort 미러(읽기전용/부재면 무시)
                catch (Exception ex) { Program.Log("EngineConfig.MirrorAppsettings", ex); }

                var next = new Dictionary<string, EngineState>(_s, StringComparer.OrdinalIgnoreCase);
                foreach (var (key, exe, args) in list)
                {
                    var extras = _s.TryGetValue(key, out var cur) ? cur.Extras : EmptyExtras;
                    next[key] = new EngineState(exe, args.Length > 0 ? args : (Desc(key)?.DefaultArgs.ToArray() ?? Array.Empty<string>()), extras);
                }
                _s = next;
                SavedAt = DateTime.Now;
            }
            catch (Exception ex) { Program.Log("EngineConfig.SaveAll", ex); return false; }
        }

        OnChange?.Invoke();   // ⚠ 락 밖에서 방송(재진입/데드락 방지)
        return true;
    }

    // ── 영속화(구조보존) ──
    private void PersistTo(string path, IEnumerable<(string Key, string Exe, string[] Args)> saves)
    {
        var root = ReadRoot(path);
        var engines = root["Engines"] as JsonObject ?? new JsonObject();
        foreach (var (key, exe, args) in saves)
        {
            var e = engines[key] as JsonObject ?? new JsonObject();
            e["Exe"] = exe;
            var arr = new JsonArray();
            foreach (var a in args) arr.Add((JsonNode)a);
            e["Args"] = arr;
            engines[key] = e;
        }
        root["Engines"] = engines;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(JsonOpts), new UTF8Encoding(false));   // UTF-8(BOM 없음)
        File.Copy(tmp, path, overwrite: true);
        try { File.Delete(tmp); } catch { /* 임시파일 삭제 실패 무시 */ }
    }

    private static JsonObject ReadRoot(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex) { Program.Log("EngineConfig.ReadRoot", ex); }
        return new JsonObject();
    }

    // ── 초기 스냅샷: appsettings 시드 → engines.json 오버레이 ──
    private IReadOnlyDictionary<string, EngineState> LoadInitial(IConfiguration cfg)
    {
        var map = new Dictionary<string, EngineState>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in Descriptors)
        {
            var sec = cfg.GetSection($"Engines:{d.Key}");
            var exe = (sec["Exe"] ?? "").Trim();
            var args = sec.GetSection("Args").GetChildren().Select(c => c.Value ?? "").Where(v => v.Length > 0).ToArray();
            if (args.Length == 0) args = d.DefaultArgs.ToArray();

            var extras = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in sec.GetChildren())
                if (child.Key is not ("Exe" or "Args" or "_comment") && child.Value is { Length: > 0 })
                    extras[child.Key] = child.Value;

            map[d.Key] = new EngineState(exe, args, extras);
        }

        try
        {
            if (File.Exists(_path))
            {
                var eng = (JsonNode.Parse(File.ReadAllText(_path)) as JsonObject)?["Engines"] as JsonObject;
                if (eng is not null)
                {
                    foreach (var d in Descriptors)
                    {
                        if (eng[d.Key] is not JsonObject e) continue;
                        var cur = map[d.Key];
                        var exe = Str(e, "Exe") ?? cur.Exe;
                        var args = (e["Args"] as JsonArray)?
                            .Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToArray();
                        map[d.Key] = new EngineState(exe, args is { Length: > 0 } ? args : cur.Args, cur.Extras);
                    }
                }
                try { SavedAt = File.GetLastWriteTime(_path); } catch { /* 무시 */ }
            }
        }
        catch (Exception ex) { Program.Log("EngineConfig.Overlay", ex); }

        return map;
    }

    private static string[] SplitArgs(string? line)
        => (line ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static string? Str(JsonObject? o, string key)
        => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? FirstLine(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        foreach (var line in s.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) return t;
        }
        return null;
    }

    private static string Trunc(string s, int n = 90) => s.Length <= n ? s : s[..n] + "…";

    private static readonly IReadOnlyDictionary<string, string> EmptyExtras =
        new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);

    /// <summary>불변 엔진 상태(원자적 교체 단위).</summary>
    private sealed record EngineState(string Exe, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Extras);
}
