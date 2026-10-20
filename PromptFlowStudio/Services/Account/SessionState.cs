using System;

namespace PromptFlowStudio.Services.Account;

/// <summary>
/// 회원 로그인 세션 상태(단일 사용자 데스크톱 · 싱글턴). 회원 원장(MemberRegistryService) 인증 성공 시 SignIn 으로 채워지고,
///   MainLayout 로그인 게이트·관리자 페이지(RBAC)·사이드바에서 참조한다. 인메모리(쿠키 없음) — 프로세스 수명 유지(앱 재시작 시 재로그인).
///   ※ BlazorWebView(단일 인터랙티브 컨텍스트)라 게이트·회로 스코프 구분이 없어 싱글턴이 자연스럽다.
/// </summary>
public sealed class SessionState
{
    public bool IsAuthenticated { get; private set; }
    public string User { get; private set; } = "";
    public string Display { get; private set; } = "";
    public string Role { get; private set; } = "Viewer";
    public bool IsAdmin => Role == "Admin";
    public bool CanOperate => Role is "Admin" or "Operator";
    public event Action? OnChange;

    public void SignIn(string user, string? role, string? display = null)
    {
        IsAuthenticated = true;
        User = user ?? "";
        Display = string.IsNullOrWhiteSpace(display) ? (user ?? "") : display!;
        Role = string.IsNullOrWhiteSpace(role) ? "Viewer" : role!;
        OnChange?.Invoke();
    }

    public void SetRole(string role)
    {
        if (Role == role) return;
        Role = string.IsNullOrWhiteSpace(role) ? "Viewer" : role;
        OnChange?.Invoke();
    }

    public void SignOut()
    {
        IsAuthenticated = false; User = ""; Display = ""; Role = "Viewer";
        OnChange?.Invoke();
    }
}
