namespace PromptFlowStudio.Services.Ui;

/// <summary>
/// [PromptFlow Studio v18.0.0] 🏭 숏폼 AI 팩토리 4 단계 탭 공유 컨텍스트 — 워크스페이스 모달 1 개가 인스턴스 1 개를 소유하고
/// 탭 전환 시 같은 필드를 그대로 이어 쓴다(필드 갱신만 · 무할당). 상류 단계 값이 바뀌면 하류 확정 비트를 해제해 낡은 결과 인출을 막는다.
/// </summary>
public sealed class ShortsFactoryWorkspaceContext
{
    public const int StageCount = 4;
    /// <summary>v17.84 오토 클리퍼 인메모리 링 길이(120 s).</summary>
    public const int SourceMs = 120_000;
    public const int MinClipMs = 3_000, MaxClipMs = 60_000;
    public const int MaxZoomPunches = 8;
    public const int LangEn = 1, LangEs = 2, LangJa = 4;
    /// <summary>인출 추정 비트레이트(9:16 1080×1920 · 6 Mbps → 750 B/ms).</summary>
    public const int ExportBytesPerMs = 750;

    public int ClipInMs { get; private set; } = 12_000;
    public int ClipOutMs { get; private set; } = 40_000;
    /// <summary>9:16 리프레임 피사체 중심(0~1000 ‰ · 500 = 중앙).</summary>
    public int ReframeCenterPermille { get; private set; } = 500;
    /// <summary>0 옐로우 하이라이트 · 1 네온 사이버 · 2 미니멀 화이트(v17.78 키네틱 프리셋).</summary>
    public int CaptionPreset { get; private set; }
    public int DubLanguageMask { get; private set; } = LangEn;
    public int ZoomPunchCount { get; private set; } = 3;
    public bool BgmSafe { get; private set; } = true;
    /// <summary>썸네일 프레임 위치(클립 구간 내 ‰).</summary>
    public int ThumbnailPermille { get; private set; } = 300;
    public long ExportBytes { get; private set; }
    public int CompletedMask { get; private set; }
    /// <summary>값이 바뀔 때마다 증가 — 탭 UI 가 같은 컨텍스트를 보고 있는지 확인용.</summary>
    public int Revision { get; private set; }

    public int DurationMs => ClipOutMs - ClipInMs;
    public int ThumbnailAtMs => ClipInMs + (int)((long)DurationMs * ThumbnailPermille / 1000);
    public bool IsCommitted(int stage) => (uint)stage < StageCount && (CompletedMask & (1 << stage)) != 0;
    /// <summary>앞 단계가 모두 확정돼야 해당 단계 확정 가능(① 은 항상 가능).</summary>
    public bool CanCommit(int stage) => (uint)stage < StageCount && (CompletedMask & ((1 << stage) - 1)) == (1 << stage) - 1;
    public int CompletedCount => System.Numerics.BitOperations.PopCount((uint)CompletedMask);
    public bool HasLanguage(int lang) => (DubLanguageMask & lang) != 0;

    // ── ① 절삭 & 리프레임 ──
    public void SetClip(int inMs, int lengthMs)
    {
        var len = Math.Clamp(lengthMs, MinClipMs, MaxClipMs);
        var start = Math.Clamp(inMs, 0, SourceMs - len);
        if (start == ClipInMs && start + len == ClipOutMs) return;
        ClipInMs = start;
        ClipOutMs = start + len;
        Touch(0);
    }

    public void SetReframeCenter(int permille)
    {
        var v = Math.Clamp(permille, 0, 1000);
        if (v == ReframeCenterPermille) return;
        ReframeCenterPermille = v;
        Touch(0);
    }

    // ── ② 자막 & 더빙 ──
    public void SetCaptionPreset(int preset)
    {
        var v = Math.Clamp(preset, 0, 2);
        if (v == CaptionPreset) return;
        CaptionPreset = v;
        Touch(1);
    }

    /// <summary>더빙 언어 토글 — 원본 KO 만 남기는 것(마스크 0)도 허용.</summary>
    public void ToggleLanguage(int lang)
    {
        if (lang is not (LangEn or LangEs or LangJa)) return;
        DubLanguageMask ^= lang;
        Touch(1);
    }

    // ── ③ 리텐션 & 사운드 ──
    public void SetZoomPunches(int count)
    {
        var v = Math.Clamp(count, 0, MaxZoomPunches);
        if (v == ZoomPunchCount) return;
        ZoomPunchCount = v;
        Touch(2);
    }

    public void SetBgmSafe(bool on)
    {
        if (on == BgmSafe) return;
        BgmSafe = on;
        Touch(2);
    }

    // ── ④ 인출 & 썸네일 ──
    public void SetThumbnail(int permille)
    {
        var v = Math.Clamp(permille, 0, 1000);
        if (v == ThumbnailPermille) return;
        ThumbnailPermille = v;
        Touch(3);
    }

    /// <summary>단계 확정 — 앞 단계 미확정이면 false. ④ 확정 시 인출 크기를 산출한다.</summary>
    public bool Commit(int stage)
    {
        if (!CanCommit(stage)) return false;
        CompletedMask |= 1 << stage;
        if (stage == StageCount - 1) ExportBytes = (long)DurationMs * ExportBytesPerMs;
        Revision++;
        return true;
    }

    public void Reset()
    {
        ClipInMs = 12_000; ClipOutMs = 40_000; ReframeCenterPermille = 500;
        CaptionPreset = 0; DubLanguageMask = LangEn;
        ZoomPunchCount = 3; BgmSafe = true;
        ThumbnailPermille = 300; ExportBytes = 0;
        CompletedMask = 0;
        Revision++;
    }

    /// <summary>단계 값 변경 → 그 단계와 하류 확정 해제(상류 변경은 하류 결과를 무효화).</summary>
    private void Touch(int stage)
    {
        CompletedMask &= (1 << stage) - 1;
        ExportBytes = 0;
        Revision++;
    }
}
