using System;
using System.Linq;

namespace PromptFlowStudio.Services.Platform;

/// <summary>사용자 친화적으로 변환된 오류(아이콘/제목/설명/해결 힌트).</summary>
public sealed record HumanError(string Icon, string Title, string Message, string? Hint);

/// <summary>
/// [PromptFlowStudio] 오류 인간화 + 전역 표출(외부 NuGet 0). 개발자용 StackTrace 대신 원인·해결 가이드 문구로 변환.
///   ▸ <see cref="Humanize"/>: 원문 키워드(API 키/엔진 미설치/디스크/네트워크/권한/ffmpeg)를 매핑해 <see cref="HumanError"/> 반환(순수 함수).
///   ▸ 관찰가능: <see cref="Show(string)"/> → <see cref="Current"/> 세팅 + <see cref="OnChange"/> 방송 → MainLayout 의 ErrorModal 표출.
///   ▸ Graceful: 매핑 실패해도 원문 첫 줄(스택트레이스 제거)만 깔끔히 보여준다.
///   ▸ v16.3.1: 원문의 XML/HTML 마크업은 제거(<see cref="CleanMessage"/>) · 업로드/네트워크 실패는 전용 안내 + [다시 시도](<see cref="RetryAction"/>).
/// </summary>
public sealed class ErrorDialogService
{
    private readonly object _gate = new();
    public event Action? OnChange;

    /// <summary>현재 표출 중인 오류(없으면 null).</summary>
    public HumanError? Current { get; private set; }

    /// <summary>v16.3.1: [다시 시도] 동작(없으면 버튼 미표시).</summary>
    public Action? RetryAction { get; private set; }

    /// <summary>업로드/네트워크 실패 공통 안내 제목.</summary>
    public const string UploadNetworkTitle = "네트워크 연결이 불안정하거나 이미지 업로드에 실패했습니다.";

    /// <summary>오류를 인간화해 전역 표출한다.</summary>
    public void Show(string? raw) => Show(raw, null);

    /// <summary>오류를 인간화해 전역 표출 — <paramref name="retry"/> 가 있으면 [다시 시도] 버튼을 함께 표시.</summary>
    public void Show(string? raw, Action? retry)
    {
        var e = Humanize(raw);
        lock (_gate) { Current = e; RetryAction = retry; }
        OnChange?.Invoke();
    }

    /// <summary>[다시 시도] — 모달을 닫고 재시도 동작 실행(예외 격리).</summary>
    public void Retry()
    {
        Action? retry;
        lock (_gate) { retry = RetryAction; Current = null; RetryAction = null; }
        OnChange?.Invoke();
        try { retry?.Invoke(); }
        catch (Exception ex) { Program.Log("ErrorDialog.Retry", ex); }
    }

    /// <summary>예외를 인간화해 전역 표출한다.</summary>
    public void Show(Exception ex) => Show(ex?.Message);

    /// <summary>표출 해제.</summary>
    public void Dismiss()
    {
        lock (_gate) { Current = null; RetryAction = null; }
        OnChange?.Invoke();
    }

    /// <summary>원문 오류 메시지를 사용자 친화적 <see cref="HumanError"/> 로 변환(순수 함수). 스택트레이스는 제거.</summary>
    public static HumanError Humanize(string? raw)
    {
        var clean = FirstLine(raw);
        var low = clean.ToLowerInvariant();

        // ── v16.3.1 업로드/일시 네트워크 실패(가장 먼저 — 업로드 메시지의 HTTP 403 등이 API 키로 오분류되지 않게) ──
        if (IsUploadOrNetworkFailure(clean))
            return new("📡", UploadNetworkTitle,
                clean.Length > 0 ? clean : "참조 이미지를 업로드하지 못했습니다.",
                "[다시 시도]를 눌러 주세요. 계속 실패하면 인터넷 연결, 관리자 설정의 Base URL(지역), 이미지 형식(JPG·PNG·WEBP)과 크기(최대 32MB)를 확인하세요.");

        // ── API 키 ──
        if (Has(low, "api 키", "apikey", "invalidapikey", "구성되지 않", "unauthorized", "인증 실패", "401", "403"))
            return new("🔑", "Wan API 키가 필요해요",
                "영상 생성을 위한 Wan 2.1 API 키가 없거나 올바르지 않습니다.",
                "관리자 설정 › 🎬 영상(Wan) 탭에서 API 키를 등록하고 [연결 테스트]로 확인하세요.");

        // ── ffmpeg ──
        if (Has(low, "ffmpeg"))
            return new("🎬", "FFmpeg가 필요해요",
                "영상 병합·썸네일·오디오 처리에 FFmpeg가 필요하지만 찾지 못했습니다.",
                "ffmpeg.exe 를 앱 실행 파일과 같은 폴더에 두거나 시스템 PATH에 추가하세요.");

        // ── 로컬 AI 엔진 미설치 ──
        if (Has(low, "설정되지 않", "미설치", "실행 파일을 찾을 수 없", "cli 가 설정", "engines"))
            return new("🤖", "AI 엔진이 설치되지 않았어요",
                "선택한 로컬 AI 엔진(업스케일/립싱크/TTS/STT)의 실행 파일 경로가 지정되지 않았습니다.",
                "관리자 설정 › 🤖 AI 엔진 탭에서 해당 엔진의 실행 파일(.exe) 경로를 지정하세요. (엔진 없이도 기본 기능은 동작합니다)");

        // ── 디스크 용량 ──
        if (Has(low, "공간", "disk full", "not enough space", "no space", "디스크"))
            return new("💾", "저장 공간이 부족해요",
                "영상을 저장할 디스크 여유 공간이 부족합니다.",
                "🖼️ 갤러리에서 오래된 영상을 삭제하거나 다른 드라이브를 확보한 뒤 다시 시도하세요.");

        // ── 네트워크 ──
        if (Has(low, "network", "네트워크", "timeout", "시간 초과", "연결할 수 없", "no such host", "getaddrinfo", "connection"))
            return new("🌐", "네트워크 연결을 확인해 주세요",
                "서버에 연결하지 못했습니다(인터넷 또는 지역 엔드포인트 문제).",
                "인터넷 연결을 확인하고, 관리자 설정의 Base URL(지역)이 API 키와 같은 지역인지 확인하세요.");

        // ── 파일 권한/잠금 ──
        if (Has(low, "access", "권한", "denied", "unauthorizedaccess", "사용 중", "being used", "lock"))
            return new("🔒", "파일 접근에 문제가 있어요",
                "파일이 다른 프로그램에서 사용 중이거나 접근 권한이 없습니다.",
                "해당 파일을 연 다른 프로그램을 닫거나, 앱을 관리자 권한으로 실행해 보세요.");

        // ── 이미지/i2v ──
        if (Has(low, "이미지", "image", "업로드"))
            return new("🖼️", "이미지 처리에 문제가 있어요",
                clean.Length > 0 ? clean : "참조 이미지를 업로드하거나 해석하지 못했습니다.",
                "이미지 형식(PNG/JPG/WEBP)과 크기(최대 32MB)를 확인하고 다시 시도하세요.");

        // ── 기본 ──
        return new("⚠️", "문제가 발생했어요",
            clean.Length > 0 ? clean : "알 수 없는 오류가 발생했습니다.",
            "잠시 후 다시 시도해 주세요. 문제가 계속되면 관리자 설정에서 관련 구성을 확인하세요.");
    }

    /// <summary>
    /// 업로드/일시 네트워크 실패 여부(재시도 안내 대상). 이미지 형식·용량 문제(재시도 무의미)는 제외.
    /// </summary>
    public static bool IsUploadOrNetworkFailure(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var low = raw.ToLowerInvariant();
        if (Has(low, "너무 큽니다", "허용 용량", "지원하지 않는 이미지", "빈 파일", "찾을 수 없습니다")) return false;
        return Has(low, "이미지 업로드 실패", "업로드 정책", "업로드 서버", "업로드 시간", "malformedpostrequest",
            "network", "네트워크", "timeout", "timed out", "시간 초과", "연결할 수 없", "no such host", "getaddrinfo", "connection");
    }

    /// <summary>
    /// UI 표시용 정화 — XML 선언·태그(&lt;…&gt;) 제거, 공백 축약, 200자 제한. 마크업만 있던 원문은 일반 안내로 대체.
    /// (Blazor 는 텍스트를 이스케이프하지만 Raw XML/HTML 오류 문서가 화면에 그대로 노출되는 것을 차단)
    /// </summary>
    public static string CleanMessage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (raw.IndexOf('<') < 0) return raw.Length <= 300 ? raw : raw[..300] + "…";
        var sb = new System.Text.StringBuilder(Math.Min(raw.Length, 300));
        bool inTag = false, space = false;
        foreach (var c in raw)
        {
            if (c == '<') { inTag = true; continue; }
            if (inTag) { if (c == '>') { inTag = false; space = true; } continue; }
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
            if (sb.Length >= 200) { sb.Append('…'); break; }
        }
        var t = sb.ToString().Trim();
        return t.Length > 0 ? t : "서버가 오류 문서를 반환했습니다.";
    }

    private static bool Has(string s, params string[] keys) => keys.Any(k => s.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>스택트레이스 프레임(  at …)을 잘라내고 첫 의미 있는 줄만.</summary>
    private static string FirstLine(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (raw.IndexOf('<') >= 0) raw = CleanMessage(raw);   // 마크업 원문 → 텍스트만
        foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("at ", StringComparison.OrdinalIgnoreCase) || t.StartsWith("위치:", StringComparison.Ordinal)) break;
            return t.Length <= 200 ? t : t[..200] + "…";
        }
        return "";
    }
}
