namespace PromptFlowStudio.Services.Ui;

/// <summary>
/// [PromptFlow Studio v17.50.1] 사이드바 → 스토리보드 컨텍스트 스튜디오 디스패처.
/// 프로젝트(<c>_project</c>) 매개변수가 필요한 모달은 스토리보드 페이지가 소유하므로, 사이드바는 키를 보류(Pending)해 두고 스토리보드로 이동한다.
/// 스토리보드는 초기화 시 <see cref="Take"/> 로 보류 키를 소비하고, 이미 열려 있으면 <see cref="Requested"/> 이벤트로 즉시 연다.
/// </summary>
public sealed class StudioNavigationService
{
    private string? _pending;
    public event Action<string>? Requested;

    public void Request(string key)
    {
        _pending = key;
        try { Requested?.Invoke(key); } catch (Exception ex) { PromptFlowStudio.Program.Log("StudioNav.Requested", ex); }
    }

    /// <summary>보류 키 1회 소비(없으면 null).</summary>
    public string? Take() => Interlocked.Exchange(ref _pending, null);
}
