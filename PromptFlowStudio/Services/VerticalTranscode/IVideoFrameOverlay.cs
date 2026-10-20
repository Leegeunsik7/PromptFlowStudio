namespace PromptFlowStudio.Services.VerticalTranscode;

/// <summary>v17.52 — 인코딩 직전 NV12 프레임 오버레이(자막 · 진행률 · 스티커). 구현은 무할당이어야 한다.</summary>
/// <summary>v17.53 — 인코딩 오디오 믹스 스테이지(보컬/폴리 스템 → BGM 더킹 · 정규화 · 리미터 · 제자리 l/r). 구현은 무할당이어야 한다.</summary>
public interface IAudioMixStage
{
    void Mix(ReadOnlySpan<float> voice, ReadOnlySpan<float> foley, Span<float> l, Span<float> r);
}

public interface IVideoFrameOverlay
{
    void Apply(Span<byte> y, Span<byte> uv, int width, int height, long frameIndex, int fps);
}

/// <summary>v17.62 — 인코더 출력 탭(Annex-B 접근 단위 · 원시 AAC 프레임 · 세션 시작). 구현은 무할당이어야 한다.</summary>
public interface IEncodedStreamTap
{
    void OnSessionStart(int width, int height, int fps);
    void OnVideo(ReadOnlySpan<byte> annexB, bool key);
    void OnAudio(ReadOnlySpan<byte> aacRaw);
}
