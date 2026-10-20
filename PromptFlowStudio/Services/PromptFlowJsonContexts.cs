using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using PromptFlowStudio.Services.Account;
using PromptFlowStudio.Services.Agents;
using PromptFlowStudio.Services.AI.Wan;
using PromptFlowStudio.Services.Flow;
using PromptFlowStudio.Services.Integration;
using PromptFlowStudio.Services.Marketing;
using PromptFlowStudio.Services.Mobile;
using PromptFlowStudio.Services.Operations;
using PromptFlowStudio.Services.Security;
using PromptFlowStudio.Services.Storyboard;
using PromptFlowStudio.Services.Studio;
using PromptFlowStudio.Services.Trade;
using PromptFlowStudio.Services.Ui;
using PromptFlowStudio.Services.Wan;

// xUnit(PromptFlowStudio.Tests)이 internal 소스생성 컨텍스트/DTO 를 화이트박스 테스트할 수 있도록 허용(공개 API 는 그대로 internal 유지).
[assembly: InternalsVisibleTo("PromptFlowStudio.Tests")]

namespace PromptFlowStudio.Services;

/// <summary>
/// [v10.8.0-NativeAOT-Prep] 로컬 파일 영속 DTO 전용 소스 생성 컨텍스트(PascalCase 기본) — 리플렉션 없이
///   JsonTypeInfo&lt;T&gt; 를 컴파일 타임 생성 → IL2026/IL3050(트리밍/AOT) 경고 원천 제거.
///   ⚠ 기존 파일 포맷과 100% 호환(WriteIndented 은 공백만 영향 · 값 포맷 불변) — 마이그레이션 없이 그대로 교체 가능.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<SerialLicense>), TypeInfoPropertyName = "SerialLicenseList")]
[JsonSerializable(typeof(List<RecentProject>), TypeInfoPropertyName = "RecentProjectList")]
[JsonSerializable(typeof(StoryboardProject))]
[JsonSerializable(typeof(List<SyncItem>), TypeInfoPropertyName = "SyncItemList")]
[JsonSerializable(typeof(C2paClaim))]
[JsonSerializable(typeof(DisasterSnapshot))]
[JsonSerializable(typeof(CrossDeviceDelta))]
[JsonSerializable(typeof(List<string>), TypeInfoPropertyName = "StringList")]
[JsonSerializable(typeof(Dictionary<string, JsonElement>), TypeInfoPropertyName = "StringJsonElementDictionary")]
[JsonSerializable(typeof(BenchmarkReportEnvelope))]
[JsonSerializable(typeof(List<InstallInfo>), TypeInfoPropertyName = "InstallInfoList")]
[JsonSerializable(typeof(List<LicenseRecord>), TypeInfoPropertyName = "LicenseRecordList")]
[JsonSerializable(typeof(LicenseService.Payload), TypeInfoPropertyName = "LicenseServicePayload")]
[JsonSerializable(typeof(MemberDirectoryEntry))]
[JsonSerializable(typeof(MemberRegistryService.MemberEntity), TypeInfoPropertyName = "MemberEntity")]
[JsonSerializable(typeof(List<MemberRegistryService.MemberEntity>), TypeInfoPropertyName = "MemberEntityList")]
[JsonSerializable(typeof(SmtpConfigService.Envelope), TypeInfoPropertyName = "SmtpEnvelope")]
[JsonSerializable(typeof(SmtpConfig))]
[JsonSerializable(typeof(SwarmDeltaEnvelope))]
[JsonSerializable(typeof(Integration.AuditEntry), TypeInfoPropertyName = "ZeroTrustAuditEntry")]
[JsonSerializable(typeof(List<Integration.AuditEntry>), TypeInfoPropertyName = "ZeroTrustAuditEntryList")]
[JsonSerializable(typeof(Integration.AuditReport), TypeInfoPropertyName = "ZeroTrustAuditReport")]
[JsonSerializable(typeof(Security.AuditEntry), TypeInfoPropertyName = "SecurityAuditEntry")]
[JsonSerializable(typeof(List<Security.AuditEntry>), TypeInfoPropertyName = "SecurityAuditEntryList")]
[JsonSerializable(typeof(MarketingPayload))]
[JsonSerializable(typeof(List<WebhookTarget>), TypeInfoPropertyName = "WebhookTargetList")]
[JsonSerializable(typeof(OfflineSyncPipeline.SyncEnvelope), TypeInfoPropertyName = "OfflineSyncEnvelope")]
[JsonSerializable(typeof(RenderJobEnvelope))]
[JsonSerializable(typeof(EncryptedPayload))]
[JsonSerializable(typeof(RemoteRenderClient.SubmitAccept), TypeInfoPropertyName = "RemoteSubmitAccept")]
[JsonSerializable(typeof(RemoteRenderClient.WsFrame), TypeInfoPropertyName = "RemoteWsFrame")]
[JsonSerializable(typeof(RemoteRenderClient.ResultManifest), TypeInfoPropertyName = "RemoteResultManifest")]
[JsonSerializable(typeof(TelemetryPipelineWiring.AnomalyRecord), TypeInfoPropertyName = "TelemetryAnomalyRecord")]
[JsonSerializable(typeof(HotfixManifest))]
[JsonSerializable(typeof(DeploymentBlueprint))]
[JsonSerializable(typeof(Studio.LicensePayload), TypeInfoPropertyName = "QuotaLicensePayload")]
[JsonSerializable(typeof(LicenseQuotaGuard.QuotaState), TypeInfoPropertyName = "QuotaState")]
[JsonSerializable(typeof(List<CustomPreset>), TypeInfoPropertyName = "CustomPresetList")]
[JsonSerializable(typeof(List<ClientBrandProfile>), TypeInfoPropertyName = "ClientBrandProfileList")]
[JsonSerializable(typeof(ThemeService.ThemeDto), TypeInfoPropertyName = "ThemeDto")]
[JsonSerializable(typeof(ThemeService.StoreDto), TypeInfoPropertyName = "ThemeStoreDto")]
internal sealed partial class PromptFlowJsonContext : JsonSerializerContext
{
}

/// <summary>
/// [v10.8.0-NativeAOT-Prep] 외부 통신 와이어 프로토콜 DTO 전용 소스 생성 컨텍스트(camelCase — 기존
///   Wan/클라우드/웹훅/임베디드 HTTP/협업 WebSocket 프로토콜과 100% 동일 대소문자 규칙 유지).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WanRenderRequest))]
[JsonSerializable(typeof(WanRenderClient.WanRenderResponse), TypeInfoPropertyName = "WanRenderResponse")]
[JsonSerializable(typeof(WanRenderClient.ConcatRequest), TypeInfoPropertyName = "WanConcatRequest")]
[JsonSerializable(typeof(NodeWorkerService.WorkerRenderResponse), TypeInfoPropertyName = "WorkerRenderResponse")]
[JsonSerializable(typeof(CloudProvisionRequest))]
[JsonSerializable(typeof(CloudProvisionResponse))]
[JsonSerializable(typeof(TimelineDelta))]
[JsonSerializable(typeof(RenderBatchRequest))]
[JsonSerializable(typeof(ApiErrorResponsePayload))]
[JsonSerializable(typeof(EdgeHealthPayload))]
[JsonSerializable(typeof(EdgeSimpleStatusPayload))]
[JsonSerializable(typeof(VideoCompletedWebhookPayload))]
[JsonSerializable(typeof(WebRtcSignalMessage))]
[JsonSerializable(typeof(FlowSerializer.GraphDto), TypeInfoPropertyName = "FlowGraphDto")]
internal sealed partial class PromptFlowWireJsonContext : JsonSerializerContext
{
}