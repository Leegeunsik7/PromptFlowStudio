using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using PromptFlowStudio.Services.Account;    // LicenseService.DataAreaRoot()
using PromptFlowStudio.Services.Security;    // LocalCipher(머신바운드 AES-256-GCM)

namespace PromptFlowStudio.Services.Wan;

/// <summary>Gemini API 키 검증(핑) 결과. Ok=true 면 유효한 키(모델 목록 조회 성공).</summary>
public sealed record GeminiPing(bool Ok, string Message);

/// <summary>
/// [PromptFlowStudio] Gemini 동적 설정 저장소 — 관리자 화면에서 등록한 API 키를 <c>%LocalAppData%\PromptFlowStudio\appsettings.override.json</c> 에 적재.
///   ▸ <b>재시작 불필요</b>: 현재값을 인메모리 <see cref="Snapshot"/> 으로 들고 저장 시 참조를 원자적 교체 → <see cref="GeminiClient"/> 등 모든 Gemini 서비스가 즉시 새 키 사용.
///   ▸ <b>키 검증(Ping)</b>: 입력 키로 <c>v1beta/models</c>(모델 목록) 엔드포인트에 가벼운 GET → 200 이면 유효(즉시 검증).
///   ▸ <b>보안</b>: 키는 LocalCipher(AES-256-GCM · 머신바운드)로 봉인해 <c>ApiKeyEnc</c> 로 저장(복호화 실패 시 빈 키 → Graceful). Model 은 평문.
///   ▸ 조회 우선순위: override.json(사용자 저장) → appsettings <c>Gemini:*</c>(개발 시드). WanConfigService/EngineConfigService 의 동적 설정 패턴과 동일 계열.
///   ⚠ 키 미등록/복호화 실패면 <see cref="Enabled"/>=false → 7대 Gemini 기능이 모두 조용히 꺼지고 로컬 팩토리 파이프라인 100% 정상(Graceful).
/// </summary>
public sealed class GeminiConfigService
{
    private const string DefaultModel = "gemini-2.5-flash";
    private const string KeyPurpose   = "MS2E::PromptFlowStudio::GEMINI::v1";

    // 키 검증용 짧은 타임아웃 HttpClient(핑 전용).
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly object _gate = new();
    private readonly string _path;
    private volatile Snapshot _s;   // 원자적 교체(읽기 lock-free)

    public event Action? OnConfigChanged;
    public string FilePath => _path;
    public DateTime? SavedAt { get; private set; }

    public GeminiConfigService(IConfiguration cfg)
    {
        _path = Path.Combine(LicenseService.DataAreaRoot(), "appsettings.override.json");
        _s = LoadInitial(cfg);
    }

    // ── 현재 스냅샷(읽기 lock-free) ──
    public string ApiKey => _s.ApiKey;
    public string Model => _s.Model;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_s.ApiKey);
    /// <summary>Gemini 기능 사용 가능 여부(키 존재). 모든 Gemini 서비스의 Enabled 근원.</summary>
    public bool Enabled => IsConfigured;

    /// <summary>키로 Gemini API(models 목록)에 핑을 보내 유효성 검증. 저장하지 않음(검증만).</summary>
    public async Task<GeminiPing> ValidateAsync(string apiKey, CancellationToken ct = default)
    {
        var key = (apiKey ?? "").Trim();
        if (key.Length == 0) return new(false, "API 키를 입력하세요.");
        try
        {
            var model = _s.Model;
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}?key={Uri.EscapeDataString(key)}";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return new(true, "연동 완료 — 유효한 키입니다.");

            // 모델 단건이 막히면 목록 엔드포인트로 재시도(키 유효성만 확인).
            var listUrl = $"https://generativelanguage.googleapis.com/v1beta/models?key={Uri.EscapeDataString(key)}";
            using var resp2 = await Http.GetAsync(listUrl, ct).ConfigureAwait(false);
            if (resp2.IsSuccessStatusCode) return new(true, "연동 완료 — 유효한 키입니다.");

            var code = (int)resp2.StatusCode;
            return new(false, code is 400 or 401 or 403 ? "키가 유효하지 않습니다(권한/키 확인)." : $"검증 실패(HTTP {code}).");
        }
        catch (OperationCanceledException) { return new(false, "검증이 취소되었습니다."); }
        catch (Exception ex) { Program.Log("GeminiConfig.Validate", ex); return new(false, "네트워크 오류: " + ex.Message); }
    }

    /// <summary>현재 저장된 키로 핑(연결 상태 뱃지용). 키 없으면 Ok=false.</summary>
    public Task<GeminiPing> TestCurrentAsync(CancellationToken ct = default)
        => IsConfigured ? ValidateAsync(_s.ApiKey, ct) : Task.FromResult(new GeminiPing(false, "키가 등록되지 않았습니다."));

    /// <summary>키를 검증한 뒤 <b>유효할 때만</b> override.json 에 영속화하고 런타임 스냅샷을 즉시 교체(재시작 불필요).</summary>
    public async Task<GeminiPing> ValidateAndSaveAsync(string apiKey, CancellationToken ct = default)
    {
        var ping = await ValidateAsync(apiKey, ct).ConfigureAwait(false);
        if (!ping.Ok) return ping;
        return Save((apiKey ?? "").Trim(), _s.Model)
            ? new(true, "연동 완료 — 저장되었습니다(재시작 불필요).")
            : new(false, "검증은 통과했으나 저장에 실패했습니다.");
    }

    /// <summary>키를 검증 없이 저장(스냅샷 교체 + OnChange). 성공 시 true.</summary>
    public bool Save(string apiKey, string? model = null)
    {
        var key = (apiKey ?? "").Trim();
        var mdl = string.IsNullOrWhiteSpace(model) ? _s.Model : model!.Trim();
        lock (_gate)
        {
            try
            {
                PersistToDisk(key, mdl);
                _s = _s with { ApiKey = key, Model = mdl };
                SavedAt = DateTime.Now;
            }
            catch (Exception ex) { Program.Log("GeminiConfig.Save", ex); return false; }
        }
        OnConfigChanged?.Invoke();   // ⚠ 락 밖 브로드캐스트(재진입/데드락 방지)
        return true;
    }

    /// <summary>저장된 키를 제거(연동 해제).</summary>
    public bool Clear() => Save("", _s.Model);

    /// <summary>appsettings.override.json 구조를 보존하며 Gemini.ApiKeyEnc/Model 만 덮어쓴다(.tmp → 교체, 원자적).</summary>
    private void PersistToDisk(string apiKey, string model)
    {
        var root = ReadRoot();
        var gem = root["Gemini"] as JsonObject ?? new JsonObject();
        gem.Remove("ApiKey");   // 과거 평문 키 제거(평문 잔존 방지)
        gem["ApiKeyEnc"] = string.IsNullOrEmpty(apiKey) ? "" : LocalCipher.Protect(apiKey, KeyPurpose);
        gem["Model"] = model;
        root["Gemini"] = gem;

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Copy(tmp, _path, overwrite: true);
        try { File.Delete(tmp); } catch { /* 임시파일 삭제 실패 무시 */ }
    }

    private JsonObject ReadRoot()
    {
        try
        {
            if (File.Exists(_path))
                return JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex) { Program.Log("GeminiConfig.ReadRoot", ex); }
        return new JsonObject();
    }

    /// <summary>초기 스냅샷 — override.json(사용자 저장) 우선, 없으면 appsettings Gemini:* 시드.</summary>
    private Snapshot LoadInitial(IConfiguration cfg)
    {
        var apiKey = cfg["Gemini:ApiKey"] ?? "";                                                       // appsettings 시드(개발용)
        var model  = string.IsNullOrWhiteSpace(cfg["Gemini:Model"]) ? DefaultModel : cfg["Gemini:Model"]!;

        try
        {
            if (File.Exists(_path))
            {
                var gem = (JsonNode.Parse(File.ReadAllText(_path)) as JsonObject)?["Gemini"] as JsonObject;
                var savedModel = Str(gem, "Model");
                if (!string.IsNullOrWhiteSpace(savedModel)) model = savedModel!;

                var enc = Str(gem, "ApiKeyEnc");
                if (!string.IsNullOrEmpty(enc))
                {
                    var dec = LocalCipher.Unprotect(enc, KeyPurpose);   // 실패 시 빈 문자열(머신 변경/손상 → Graceful)
                    apiKey = !string.IsNullOrWhiteSpace(dec) ? dec : "";
                }
                else
                {
                    var legacy = Str(gem, "ApiKey");   // 과거 평문 저장본
                    if (!string.IsNullOrWhiteSpace(legacy)) apiKey = legacy!;
                }
                try { SavedAt = File.GetLastWriteTime(_path); } catch { /* 무시 */ }
            }
        }
        catch (Exception ex) { Program.Log("GeminiConfig.LoadInitial", ex); }

        return new Snapshot(apiKey.Trim(), model.Trim());
    }

    private static string? Str(JsonObject? o, string key)
        => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private sealed record Snapshot(string ApiKey, string Model);
}
