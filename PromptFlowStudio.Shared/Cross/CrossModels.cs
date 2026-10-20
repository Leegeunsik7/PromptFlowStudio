using System;
using System.Collections.Generic;

namespace PromptFlowStudio.Shared.Cross;

/// <summary>실행 플랫폼.</summary>
public enum DevicePlatform { Windows, Android, iOS, Web }

/// <summary>기기 폼팩터.</summary>
public enum DeviceForm { Phone, Tablet, Desktop }

/// <summary>지원 언어(ko/en/de/zh/ja).</summary>
public enum AppLanguage { Ko, En, De, Zh, Ja }

/// <summary>호스트가 노출하는 기능 스냅샷 — PC 기능 누락 검증의 정량 근거.</summary>
public sealed record FeatureParitySnapshot(
    DevicePlatform Platform,
    int ThemeCount,
    bool CadExplodedView,
    bool TelemetryHud,
    bool WanHybrid,
    bool EmotionalVoice,
    bool SpatialAudio,
    bool RemoteRender,
    bool GoldKeepsake,
    int SchemaFieldCount);

/// <summary>QA 개별 항목.</summary>
public sealed record QaCheck(string Feature, string Expected, string Actual, bool Pass);

/// <summary>QA 리포트 — 총/통과/통과율(0~1)/항목/요약.</summary>
public sealed record QaReport(int Total, int Passed, double PassRate, IReadOnlyList<QaCheck> Checks, string Summary)
{
    public bool AllPass => Total > 0 && Passed == Total;
    public int PassPercent => (int)Math.Round(PassRate * 100);
}

/// <summary>화면비 프로파일.</summary>
public sealed record AspectProfile(string Key, string Name, int W, int H)
{
    public double Ratio => H == 0 ? 0 : (double)W / H;
}

/// <summary>기기 프로파일 — 플랫폼/폼팩터/픽셀 해상도/화면비 키.</summary>
public sealed record DeviceProfile(string Key, string Name, DevicePlatform Platform, DeviceForm Form, int PxW, int PxH, string AspectKey);

/// <summary>콘텐츠를 캔버스에 맞춘 박스(레터/필러박스) — X/Y/W/H + 모드.</summary>
public sealed record FitBox(int X, int Y, int W, int H, string Mode);

/// <summary>PWA 웹 폴백 계획.</summary>
public sealed record PwaFallbackPlan(bool Enabled, string ManifestPath, string ServiceWorkerPath, string EntryUrl, string Note);

/// <summary>시뮬레이션 리포트 1행.</summary>
public sealed record SimReportRow(string Label, string Value);
