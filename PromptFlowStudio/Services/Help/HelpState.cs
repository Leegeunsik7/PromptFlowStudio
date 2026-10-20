namespace PromptFlowStudio.Services.Help;

/// <summary>
/// 도움말 드로어의 열림 상태를 앱 전체가 공유한다.
///
/// <para>
/// ━━━ 왜 서비스인가 (@ref 가 아니라) ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
/// </para>
///
/// <para>
/// 처음에는 MainLayout 이 <c>@ref</c> 로 드로어를 잡고 있었다. 그런데 각
/// 페이지의 제목 옆 미니 아이콘은 MainLayout 의 자식이 아니라 <c>@Body</c>
/// 안쪽이라 그 참조에 닿지 못한다. CascadingValue 로 내려보내는 방법도 있지만,
/// <c>@ref</c> 는 첫 렌더가 끝난 뒤에야 채워져서 첫 화면에서는 null 이 내려간다.
/// </para>
///
/// <para>
/// 상태를 서비스로 빼면 누가 먼저 그려지든 상관이 없다. 드로어는 상태를
/// 구독하고, 버튼들은 상태를 바꾸기만 한다.
/// </para>
///
/// <para>
/// ⚠ 이 앱은 단일 창 데스크톱(BlazorWebView)이라 Singleton 으로 등록한다.
///   웹으로 옮길 일이 생기면 반드시 Scoped 로 바꿔야 한다 —
///   Singleton 이면 모든 접속자가 같은 드로어를 열고 닫게 된다.
/// </para>
/// </summary>
public sealed class HelpState
{
    private bool _open;

    public bool IsOpen => _open;

    /// <summary>열림 상태가 바뀌면 알린다. 구독자는 UI 스레드로 올려서 처리할 것.</summary>
    public event Action? OnChange;

    public void Open()
    {
        if (_open) return;
        _open = true;
        OnChange?.Invoke();
    }

    public void Close()
    {
        if (!_open) return;
        _open = false;
        OnChange?.Invoke();
    }

    public void Toggle()
    {
        _open = !_open;
        OnChange?.Invoke();
    }
}
