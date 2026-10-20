using System.Numerics;
namespace PromptFlowStudio.Services.SplatEnvironment;

/// <summary>
/// v17.56 — 스플랫 방사휘도 변조기(접지 그림자 · 상호 반사광). 래스터라이저 워커 스레드에서 병렬 호출되므로
/// <see cref="Modulate"/> 는 무할당 · 읽기 전용 공유 상태 + 워커별 슬롯(worker ∈ [0, workers))만 써야 한다.
/// </summary>
public interface ISplatRadianceModulator
{
    void BeginFrame(GaussianSplatScene scene, in SplatCamera cam, int workers);
    Vector3 Modulate(int worker, int splat, Vector3 position, Vector3 color);
    void EndFrame();
}
