using Microsoft.AspNetCore.Components.Forms;   // IBrowserFile

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>즉시 생성 사전 점검 결과 동작 — 바로 진행 / 사진 등록 제안 / 엔진 미구성.</summary>
public enum InstantAction { Proceed, OfferPhoto, NoEngine }

/// <summary>즉시 생성 준비 상태 — 다음 동작 + 안내 문구.</summary>
public sealed record InstantReadiness(InstantAction Action, bool HasAnchor, bool EngineReady, string Headline, string Detail, string PrimaryCta);

/// <summary>테마 즉시 생성 요청 — 테마 키/포맷 + (선택) 새로 고른 아기 사진 + T2V 허용 여부.</summary>
public sealed record ThemeInstantRequest(string Key, VideoFormat Format, IBrowserFile? Photo, bool AllowT2v);

/// <summary>
/// [PromptFlowStudio] 테마 즉시 생성 오케스트레이션 헬퍼 — 순수 BCL(외부 NuGet 0 · 테스트 가능).
///   ▸ [🚀 이 테마로 즉시 생성 시작] 클릭 시 <b>레퍼런스 사진 유무 · 엔진 준비 상태</b>를 점검해 다음 동작(<see cref="InstantAction"/>)을 결정한다.
///     ①앵커 있음 → 즉시 진행(전 컷 I2V) ②앵커 없음 → 사진 등록 제안(또는 T2V 바로 시작) ③엔진 미구성 → 안내.
///   ▸ 실제 등록/테마 적용/생성 시작(IO)은 호출부(Storyboard)가 수행하고, 이 서비스는 <b>결정 로직 + 안내 문구</b>만 담당(단일 책임·테스트 용이).
///   ▸ 레퍼런스 부재 시 T2V Graceful 폴백을 보장(<see cref="ThemeInstantRequest.AllowT2v"/>).
/// </summary>
public sealed class ThemeInstantGeneratorService(ICharacterAnchorService anchor)
{
    /// <summary>프로젝트 앵커 + 엔진 준비 상태로 즉시 생성 준비도를 평가한다.</summary>
    public InstantReadiness Evaluate(StoryboardProject? project, bool engineReady)
        => Evaluate(project is not null && anchor.HasAnchor(project), engineReady);

    /// <summary>불리언 상태로 직접 평가(모달/테스트 공용).</summary>
    public InstantReadiness Evaluate(bool hasAnchor, bool engineReady)
    {
        if (!engineReady)
            return new(InstantAction.NoEngine, hasAnchor, false,
                "엔진을 먼저 구성하세요",
                "관리자 설정 › 영상(Wan)에서 API 키를 등록하면 즉시 생성이 시작됩니다.",
                "⚙️ 관리자 설정");

        if (!hasAnchor)
            return new(InstantAction.OfferPhoto, false, true,
                "아기 사진을 등록할까요?",
                "사진을 넣으면 그 아기 얼굴을 유지한 채(전 컷 I2V) 생성합니다. 없이도 텍스트(T2V)로 바로 시작할 수 있어요.",
                "📁 사진 선택 후 시작");

        return new(InstantAction.Proceed, true, true,
            "즉시 생성 준비 완료",
            "등록된 아기 얼굴 레퍼런스를 유지한 채 컷 생성 → 오디오 → 세이프존 → 최종 렌더링까지 원스톱으로 실행합니다.",
            "🚀 지금 시작");
    }
}
