using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using PromptFlowStudio.Services.Account;    // LicenseService.DataAreaRoot()
using PromptFlowStudio.Services.Security;    // LocalCipher(머신바운드 AES-256-GCM)

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// [PromptFlowStudio] Wan 동적 설정 저장소 — 관리자 화면에서 편집한 ApiKey/BaseUrl 을 <c>%LocalAppData%\PromptFlowStudio\config.json</c> 에 적재.
///   ▸ 구조 보존 저장: <see cref="JsonNode"/> 로 기존 config.json 을 읽어 <c>Wan.ApiKey</c>/<c>Wan.BaseUrl</c> 만 덮어쓴다(다른 섹션 보존).
///   ▸ 재시작 불필요: 현재값을 인메모리 <see cref="Snapshot"/> 으로 들고, 저장 시 참조를 원자적 교체 → 다른 서비스가 즉시 새 값을 읽음.
///   ▸ thread-safe: 쓰기는 lock 으로 직렬화(+파일 원자적 교체), 읽기는 volatile 스냅샷으로 lock-free.
///   조회 우선순위: config.json(사용자 저장) → appsettings Wan:*(개발 시드). SmtpConfigService 의 '동적 설정' 패턴과 동일 계열.
///   ※ 키는 사용자 프로필(%LocalAppData%)의 평문 JSON 에 저장된다(단일 사용자 데스크톱). 더 강한 보호가 필요하면
///     SmtpConfigService 의 AES-256-GCM(머신 바운드) 방식으로 봉인하는 것을 권장.
/// </summary>
public sealed class WanConfigService : IWanConfigService
{
    private const string DefaultBaseUrl = "https://dashscope-intl.aliyuncs.com";
    private const string DefaultModel   = "wan2.1-t2v-turbo";
    private const string DefaultSize    = "1280*720";
    private const string KeyPurpose     = "MS2E::PromptFlowStudio::WAN::v1";   // API 키 암호화 용도(머신바운드)

    private readonly object _gate = new();
    private readonly string _path;
    private volatile Snapshot _s;   // 원자적 교체 대상(참조 대입은 원자적) → 읽기 lock-free

    public event Action? OnConfigChanged;
    public string FilePath => _path;
    public DateTime? SavedAt { get; private set; }

    public WanConfigService(IConfiguration cfg)
    {
        _path = Path.Combine(LicenseService.DataAreaRoot(), "config.json");
        _s = LoadInitial(cfg);
    }

    // ── 현재 스냅샷 노출(읽기 lock-free) ──
    public string ApiKey => _s.ApiKey;
    public string BaseUrl => _s.BaseUrl;
    public string Model => _s.Model;
    public string Size => _s.Size;
    public int PollIntervalSeconds => _s.Poll;
    public int MaxWaitSeconds => _s.MaxWait;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_s.ApiKey);

    /// <inheritdoc/>
    public bool Save(string apiKey, string baseUrl)
    {
        var key = (apiKey ?? "").Trim();
        var url = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim().TrimEnd('/');

        lock (_gate)
        {
            try
            {
                PersistToDisk(key, url);                 // 먼저 디스크(원자적) — 성공해야 상태 교체
                _s = _s with { ApiKey = key, BaseUrl = url };
                SavedAt = DateTime.Now;
            }
            catch (Exception ex)
            {
                Program.Log("WanConfigService.Save", ex);
                return false;
            }
        }

        OnConfigChanged?.Invoke();   // ⚠ 락 밖에서 브로드캐스트(구독자 재진입/데드락 방지)
        return true;
    }

    /// <summary>config.json 구조를 보존하며 Wan.ApiKey / Wan.BaseUrl 만 덮어쓴다(.tmp → 교체, 원자적).</summary>
    private void PersistToDisk(string apiKey, string baseUrl)
    {
        var root = ReadRoot();
        var wan  = root["Wan"] as JsonObject ?? new JsonObject();
        wan.Remove("ApiKey");   // 과거 평문 키가 있었다면 제거(평문 잔존 방지)
        wan["ApiKeyEnc"] = string.IsNullOrEmpty(apiKey) ? "" : LocalCipher.Protect(apiKey, KeyPurpose);   // AES-256-GCM(머신바운드)
        wan["BaseUrl"]   = baseUrl;   // 엔드포인트는 비밀이 아니라 평문
        root["Wan"] = wan;

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Copy(tmp, _path, overwrite: true);
        try { File.Delete(tmp); } catch { /* 임시파일 삭제 실패 무시 */ }
    }

    /// <summary>기존 config.json 을 JsonObject 로 읽는다(없거나 손상 시 빈 객체 → 구조는 새로 만든다).</summary>
    private JsonObject ReadRoot()
    {
        try
        {
            if (File.Exists(_path))
                return JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex) { Program.Log("WanConfigService.ReadRoot", ex); }
        return new JsonObject();
    }

    /// <summary>초기 스냅샷 — config.json(사용자 저장) 우선, 없으면 appsettings Wan:* 시드. Model/Size/Poll/MaxWait 는 appsettings 에서.</summary>
    private Snapshot LoadInitial(IConfiguration cfg)
    {
        var apiKey  = cfg["Wan:ApiKey"] ?? "";                       // appsettings 시드(개발용)
        var baseUrl = string.IsNullOrWhiteSpace(cfg["Wan:BaseUrl"]) ? DefaultBaseUrl : cfg["Wan:BaseUrl"]!;
        var legacyPlain = false;

        try
        {
            if (File.Exists(_path))
            {
                var wan = (JsonNode.Parse(File.ReadAllText(_path)) as JsonObject)?["Wan"] as JsonObject;
                var savedUrl = Str(wan, "BaseUrl");
                if (!string.IsNullOrWhiteSpace(savedUrl)) baseUrl = savedUrl!;

                var enc = Str(wan, "ApiKeyEnc");
                if (!string.IsNullOrEmpty(enc))
                {
                    // 복호화 실패(머신 변경·OS 재설치·손상)면 빈 문자열 → 키 없음으로 Graceful 처리(크래시 없음).
                    var dec = LocalCipher.Unprotect(enc, KeyPurpose);
                    if (!string.IsNullOrWhiteSpace(dec)) apiKey = dec;
                    else { apiKey = ""; Program.Log("WanConfigService.LoadInitial",
                        new Exception("API 키 복호화 실패 — 다른 PC/OS 이거나 파일 손상. 관리자 설정에서 재입력이 필요합니다.")); }
                }
                else
                {
                    var legacy = Str(wan, "ApiKey");   // 과거 평문 저장본(이전 버전) → 마이그레이션 대상
                    if (!string.IsNullOrWhiteSpace(legacy)) { apiKey = legacy!; legacyPlain = true; }
                }
                try { SavedAt = File.GetLastWriteTime(_path); } catch { /* 시각 근사 실패 무시 */ }
            }
        }
        catch (Exception ex) { Program.Log("WanConfigService.LoadInitial", ex); }

        var model = string.IsNullOrWhiteSpace(cfg["Wan:Model"]) ? DefaultModel : cfg["Wan:Model"]!;
        var size  = string.IsNullOrWhiteSpace(cfg["Wan:Size"])  ? DefaultSize  : cfg["Wan:Size"]!;
        var poll  = int.TryParse(cfg["Wan:PollIntervalSeconds"], out var pv) && pv > 0 ? pv : 3;
        var maxw  = int.TryParse(cfg["Wan:MaxWaitSeconds"],      out var mv) && mv > 0 ? mv : 600;

        var snap = new Snapshot(apiKey.Trim(), baseUrl.TrimEnd('/'), model, size, poll, maxw);

        // 평문 레거시 키를 발견하면 즉시 암호문으로 승격(평문 잔존 제거 · best-effort).
        if (legacyPlain)
            try { PersistToDisk(snap.ApiKey, snap.BaseUrl); }
            catch (Exception ex) { Program.Log("WanConfigService.Migrate", ex); }

        return snap;
    }

    /// <summary>JsonObject 에서 문자열 값을 안전하게(없거나 문자열 아니면 null).</summary>
    private static string? Str(JsonObject? o, string key)
        => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>불변 설정 스냅샷 — 원자적 교체 단위.</summary>
    private sealed record Snapshot(string ApiKey, string BaseUrl, string Model, string Size, int Poll, int MaxWait);
}
