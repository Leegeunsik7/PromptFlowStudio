using System.Numerics;
namespace PromptFlowStudio.Services.GenlockFrustum;

/// <summary>
/// v17.55 — 방향 기반 외부 장면 샘플러(인너 프러스텀 · 세트 확장 텍스처 공급). 무할당 · 다른 스레드에서 호출될 수 있다.
/// 반환 = 커버리지(0..1) · <paramref name="rgb"/> = 커버리지 사전 곱 색(호출자는 rgb + fallback·(1 − 커버리지)로 합성).
/// </summary>
public interface IDirectionalSceneSampler
{
    float Sample(Vector3 eye, Vector3 dir, out Vector3 rgb);
}
