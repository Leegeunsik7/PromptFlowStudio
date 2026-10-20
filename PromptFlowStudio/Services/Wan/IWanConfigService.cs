using System;

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// Wan(통의만상) 동적 설정 공급자 — 실행 중 편집 가능한 값(ApiKey/BaseUrl)과 appsettings 시드값(Model/Size/Poll/MaxWait)을
///   단일 소스로 노출한다. 저장 즉시 인메모리 스냅샷이 교체돼 <see cref="WanApiService"/>·<see cref="WanJobState"/> 가
///   <b>재시작 없이</b> 새 값을 읽는다. 변경은 <see cref="OnConfigChanged"/> 로 브로드캐스트.
///   구현은 <see cref="WanConfigService"/>(config.json · BCL System.Text.Json · 외부 NuGet 0).
/// </summary>
public interface IWanConfigService
{
    /// <summary>현재 유효 API 키(관리자 저장본 우선 → appsettings Wan:ApiKey 시드).</summary>
    string ApiKey { get; }
    /// <summary>현재 유효 엔드포인트(정규화 · 끝 '/' 제거). 국제/중국본토 전환값.</summary>
    string BaseUrl { get; }
    /// <summary>기본 모델(appsettings Wan:Model).</summary>
    string Model { get; }
    /// <summary>기본 해상도(appsettings Wan:Size).</summary>
    string Size { get; }
    /// <summary>폴링 주기(초).</summary>
    int PollIntervalSeconds { get; }
    /// <summary>최대 대기(초).</summary>
    int MaxWaitSeconds { get; }

    /// <summary>API 키 구성 여부(=키가 비어있지 않음). <see cref="WanApiService.Enabled"/> 의 실체.</summary>
    bool IsConfigured { get; }

    /// <summary>설정 저장 파일 경로(화면 표시용).</summary>
    string FilePath { get; }
    /// <summary>마지막 저장 시각(화면 표시용). 저장본이 없으면 null.</summary>
    DateTime? SavedAt { get; }

    /// <summary>설정 변경 브로드캐스트 — 저장 성공 시 발행(구독: 서비스/컴포넌트가 실시간 감지).</summary>
    event Action? OnConfigChanged;

    /// <summary>
    /// ApiKey·BaseUrl 을 config.json 에 <b>구조 보존</b>으로 덮어쓰고 인메모리 스냅샷을 원자적 교체 후 <see cref="OnConfigChanged"/> 발행.
    /// </summary>
    /// <returns>디스크 저장 성공 여부(실패해도 인메모리 반영은 시도하지 않음 — 파일과 상태 일관성 유지).</returns>
    bool Save(string apiKey, string baseUrl);
}
