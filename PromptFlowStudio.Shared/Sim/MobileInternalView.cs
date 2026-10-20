namespace PromptFlowStudio.Services.Mobile;

/// <summary>
/// [PromptFlow Studio v5.7.4] 모바일 시뮬레이터 내부 독립 화면 상태(PC NavigationManager 미사용 · 시뮬레이터 안에서만 전환).
///   ▸ MobileStudioDeck 이 라우팅하고 MobileNavDrawer 가 활성 하이라이트 동기화에 참조하므로 <b>공용 public</b> 타입으로 둔다.
/// </summary>
public enum MobileInternalView
{
    Workspace,
    Storyboard,
    Prompt,
    Flow,
    Gallery,
    Settings,
    Batch,   // v5.8.0: B2B 대량 양산 팩토리
    Gyro,    // v5.8.0: 3D 자이로 모션 라이브 뷰어
    NodeGrid,   // v6.0.1-b: 원격 GPU 워커 분산 렌더 노드 HUD 모니터링
    AudioSync,  // v6.0.2: Audio-to-Video 보컬 립싱크 파형 스튜디오
    WebhookLog, // v6.1.0: B2B Webhook 발송 로그 & 대상 관리
    LiveStream, // v6.1.0: WebRTC 초저지연 라이브 프리뷰
}
