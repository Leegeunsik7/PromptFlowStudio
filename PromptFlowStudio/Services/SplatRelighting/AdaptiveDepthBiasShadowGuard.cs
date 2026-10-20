namespace PromptFlowStudio.Services.SplatRelighting;

/// <summary>
/// [PromptFlow Studio v17.56.0] 🛡 적응형 뎁스 바이어스 &amp; 그림자 연산 레이턴시 세이프티 가드(<see cref="Bias"/> · <see cref="Observe"/> 무할당).
/// <b>바이어스</b>: texel·BiasScale·(상수 0.5 + 기울기 1.0·tanθ·(1 + PCF 반경)) — θ = 수용면 법선과 광선 사이 각(광원 각도가 낮을수록 커짐).
/// 커널 반경만큼 떨어진 이웃 텍셀의 평면 깊이 차까지 덮어 아크네(자기 그림자 얼룩)를 막고, 가드 ON 이면 8 텍셀로 상한을 걸어
/// 접지부 그림자가 발에서 떨어지는 피터 패닝을 차단한다(OFF = 상한 없음 · 비교용).
/// <b>레이턴시</b>: 프레임 그림자 시간(뎁스 맵 + PCF 벽시계)이 3.5 ms 를 넘으면 그 즉시 PCF/블로커 탐색 반경 상한을 2/3 로 축소하고,
/// 1.75 ms 미만이 30 프레임 지속되면 1 씩 사용자 반경까지 복원한다.
/// </summary>
public sealed class AdaptiveDepthBiasShadowGuard
{
    public const float ConstTexels = 0.5f, SlopeTexels = 1f, MaxBiasTexels = 8f, MaxTanTheta = 12f, DefaultBudgetMs = 3.5f;
    public const int MaxRadius = 12;
    private int _radius = 4, _user = 4, _calm;

    public bool Enabled { get; set; } = true;
    /// <summary>사용자 바이어스 배율(1 = 적응형 기본 · 0 = 바이어스 없음).</summary>
    public float BiasScale { get; set; } = 1f;
    public float BudgetMs { get; set; } = DefaultBudgetMs;
    /// <summary>사용자 요청 PCF 반경(텍셀) — 설정 시 현재 상한도 이 값으로 재시작.</summary>
    public int UserRadius { get => _user; set { _user = Math.Clamp(value, 1, MaxRadius); _radius = _user; _calm = 0; } }
    /// <summary>현재 허용 PCF/블로커 탐색 반경 상한(가드 축소 반영).</summary>
    public int RadiusCap => Enabled ? _radius : _user;
    public long Frames { get; private set; }
    public long OverBudgetFrames { get; private set; }
    /// <summary>예산 초과 시 반경 상한이 이미 1 이어서 더 줄일 수 없던 프레임.</summary>
    public long OverBudgetAtFloor { get; private set; }
    public long Downscales { get; private set; }
    public long Restores { get; private set; }
    public long BiasClamps { get; private set; }
    public float LastShadowMs { get; private set; }
    public float PeakShadowMs { get; private set; }

    public void ResetMetrics() { Frames = OverBudgetFrames = OverBudgetAtFloor = Downscales = Restores = BiasClamps = 0; PeakShadowMs = 0; }
    public void Reset() { ResetMetrics(); _radius = _user; _calm = 0; }

    /// <summary>뎁스 바이어스(월드 m) — cosTheta = 수용면 법선 · 광원 방향, texelM = 수용점 깊이의 텍셀 크기, radius = 이번 PCF 반경.</summary>
    public float Bias(float cosTheta, float texelM, int radius)
    {
        var c = Math.Clamp(cosTheta, 0.05f, 1f);
        var tan = MathF.Min(MathF.Sqrt(MathF.Max(1f - c * c, 0f)) / c, MaxTanTheta);
        var texels = MathF.Max(BiasScale, 0f) * (ConstTexels + SlopeTexels * tan * (1 + radius));
        if (Enabled && texels > MaxBiasTexels) { texels = MaxBiasTexels; BiasClamps++; }
        return texels * texelM;
    }

    /// <summary>프레임 그림자 시간 관측 → 반경 상한 축소/복원.</summary>
    public void Observe(float shadowMs)
    {
        Frames++;
        LastShadowMs = shadowMs;
        if (shadowMs > PeakShadowMs) PeakShadowMs = shadowMs;
        if (!Enabled) { _radius = _user; return; }
        if (shadowMs > BudgetMs)
        {
            OverBudgetFrames++; _calm = 0;
            if (_radius > 1) { _radius = Math.Max(1, _radius * 2 / 3); Downscales++; }
            else OverBudgetAtFloor++;
        }
        else if (shadowMs < BudgetMs * 0.5f && _radius < _user) { if (++_calm >= 30) { _radius++; Restores++; _calm = 0; } }
        else _calm = 0;
    }
}
