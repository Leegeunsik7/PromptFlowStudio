using System;
using System.Globalization;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Ui;

/// <summary>
/// [PromptFlowStudio] v15.4.0 🎬 라이브 비디오 캔버스 & ⚙️ Wan 2.1 튜너 상태 — Workspace 인스턴스당 1개.
///   모든 setter 는 범위 클램프 후 값 필드만 갱신(0B). 요청 결선은 값 형식 <see cref="WanTuning"/> 스냅숏 복사(0B),
///   텔레메트리 요약 문자열은 값이 바뀐 뒤 첫 조회 때만 1회 생성(렌더마다 포맷 0). 순수 BCL · 모바일 링크.
/// </summary>
public sealed class LiveVideoCanvasStudio
{
    public const int MinSteps = 15, MaxSteps = 50, DefaultSteps = 30;
    public const float MinCfg = 1.0f, MaxCfg = 15.0f, DefaultCfg = 6.0f;
    public const int MinMotion = 1, MaxMotion = 255, DefaultMotion = 127;

    private int _steps = DefaultSteps, _motion = DefaultMotion, _seed;
    private float _cfg = DefaultCfg;
    private bool _seedPinned, _faceSharpen, _interpolate60;
    private string? _summary, _cfgLabel;

    /// <summary>추론 스텝(15~50).</summary>
    public int Steps { get => _steps; set => Set(ref _steps, Math.Clamp(value, MinSteps, MaxSteps)); }

    /// <summary>CFG 스케일(1.0~15.0, 0.5 단위 스냅). NaN 은 기본값.</summary>
    public float CfgScale
    {
        get => _cfg;
        set
        {
            var v = float.IsNaN(value) ? DefaultCfg : MathF.Round(Math.Clamp(value, MinCfg, MaxCfg) * 2f) / 2f;
            if (v != _cfg) { _cfg = v; _summary = null; _cfgLabel = null; }
        }
    }

    /// <summary>CFG 표시 라벨("6.0") — 값 변경 후 첫 조회 때만 생성.</summary>
    public string CfgLabel => _cfgLabel ??= _cfg.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>모션 버킷(1~255, 클수록 움직임 큼).</summary>
    public int MotionBucket { get => _motion; set => Set(ref _motion, Math.Clamp(value, MinMotion, MaxMotion)); }

    /// <summary>시드 값(0~int.MaxValue). 핀이 꺼져 있으면 요청에 실리지 않는다(서버 랜덤).</summary>
    public int Seed { get => _seed; set => Set(ref _seed, Math.Max(0, value)); }

    /// <summary>📌 시드 고정 — 켜면 같은 시드로 재현 생성.</summary>
    public bool SeedPinned { get => _seedPinned; set => Set(ref _seedPinned, value); }

    /// <summary>🍼 아기 얼굴 디테일 보정(Baby Face Sharpener).</summary>
    public bool FaceSharpen { get => _faceSharpen; set => Set(ref _faceSharpen, value); }

    /// <summary>60FPS 모션 보간.</summary>
    public bool Interpolate60 { get => _interpolate60; set => Set(ref _interpolate60, value); }

    /// <summary>요청에 실을 시드 — 핀 해제 시 null.</summary>
    public int? EffectiveSeed => _seedPinned ? _seed : null;

    /// <summary>요청 결선용 값 스냅숏(0B).</summary>
    public WanTuning Snapshot() => new(_steps, _cfg, _motion, _faceSharpen, _interpolate60);

    /// <summary>튜너를 기본값으로 되돌린다(시드 값은 유지, 핀만 해제).</summary>
    public void Reset()
    {
        Steps = DefaultSteps; CfgScale = DefaultCfg; MotionBucket = DefaultMotion;
        SeedPinned = false; FaceSharpen = false; Interpolate60 = false;
    }

    /// <summary>📊 미니 텔레메트리 바 요약 — 값 변경 후 첫 조회 때만 생성, 이후 같은 참조 재사용.</summary>
    public string Summary => _summary ??= string.Create(CultureInfo.InvariantCulture,
        $"{_steps} steps · CFG {_cfg:0.0} · Motion {_motion} · Seed {(_seedPinned ? _seed.ToString(CultureInfo.InvariantCulture) : "랜덤")}{(_faceSharpen ? " · 👶보정" : "")}{(_interpolate60 ? " · 60FPS" : "")}");

    private void Set<T>(ref T field, T value) where T : IEquatable<T>
    {
        if (field.Equals(value)) return;
        field = value;
        _summary = null;
    }
}
