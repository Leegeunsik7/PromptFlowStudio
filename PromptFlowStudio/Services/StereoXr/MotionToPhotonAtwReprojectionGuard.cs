using System.Numerics;

namespace PromptFlowStudio.Services.StereoXr;

/// <summary>ATW 동작: 끔 · 항상 · 자동(M2P 스파이크 · 프레임 반복 · 회전 편차 시에만).</summary>
public enum AtwMode : byte { Off, Always, Auto }

/// <summary>
/// [PromptFlow Studio v17.22.0] ⏱ M2P(Motion-to-Photon) 지연 · 비동기 타임워프(ATW) 가드(무할당 · 결정적).
///   ▸ M2P 원지연 = 표시 시각 − 프레임이 렌더된 헤드 포즈의 샘플 시각(업링크 + 렌더 + 부호화 + 다운링크 + 복호 + vsync 대기 + 반복 표시).
///   ▸ HMD IMU(1 kHz) 최신 두 샘플의 각속도로 표시 시각까지 회전을 예측하고, 표시 눈 카메라 → 렌더 눈 카메라 회전 호모그래피
///     (화면 tan 공간에서 선형 · 픽셀당 나눗셈 1 회)로 직전 프레임을 역투영한다. 렌더 프러스텀 밖은 가장자리 클램프(비율 계측).
///   ▸ 자동 모드: 원지연 &gt; 예산(20 ms) · 새 프레임 미도착(반복) · 회전 편차 &gt; 임계 시에만 워프하고 그 외엔 패스스루(memmove).
///   ▸ 워프 후 실효 M2P = IMU 샘플 → 광자(AtwLeadMs) — 진동/멀미를 유발하는 회전 잔차를 계측(시뮬레이션 진값 대비).
/// </summary>
public sealed class MotionToPhotonAtwReprojectionGuard
{
    public const int EW = StereoscopicFoveatedRenderingEngine.EW, EH = StereoscopicFoveatedRenderingEngine.EH, EN = StereoscopicFoveatedRenderingEngine.EN;
    public const float BudgetMs = 20f;

    private readonly uint[] _out = new uint[2 * EN];
    private double _imuT0 = double.NaN, _imuT1 = double.NaN;
    private Quaternion _imuQ0 = Quaternion.Identity, _imuQ1 = Quaternion.Identity;

    public AtwMode Mode { get; set; } = AtwMode.Auto;
    public bool Prediction { get; set; } = true;
    public float AtwLeadMs { get; set; } = 3f;
    public float EngageDeg { get; set; } = 0.25f;
    public ReadOnlySpan<uint> Output => _out;
    public bool Engaged { get; private set; }
    public bool Repeat { get; private set; }
    public float RawM2pMs { get; private set; }
    public float EffectiveM2pMs { get; private set; }
    public float MaxRawM2pMs { get; private set; }
    public float ResidualDeg { get; private set; }
    public float UncorrectedDeg { get; private set; }
    public float EdgeFill { get; private set; }
    public double LastWarpUs { get; private set; }
    public long Frames { get; private set; }
    public long Warps { get; private set; }
    public long Repeats { get; private set; }
    public long SpikeFrames { get; private set; }
    public long Defended { get; private set; }
    public long ComfortViolations { get; private set; }
    public Quaternion DisplayOrientation { get; private set; } = Quaternion.Identity;

    public static float AngleDeg(Quaternion a, Quaternion b) => 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(Quaternion.Dot(a, b)))) * (180f / MathF.PI);

    /// <summary>IMU 회전 샘플 입력(시각 ms).</summary>
    public void FeedImu(double tMs, Quaternion q)
    {
        _imuT0 = _imuT1; _imuQ0 = _imuQ1;
        _imuT1 = tMs; _imuQ1 = Quaternion.Normalize(q);
    }

    /// <summary>최신 두 IMU 샘플의 월드 각속도로 tMs 까지 외삽.</summary>
    public Quaternion Predict(double tMs)
    {
        if (double.IsNaN(_imuT1)) return Quaternion.Identity;
        if (!Prediction || double.IsNaN(_imuT0) || _imuT1 <= _imuT0) return _imuQ1;
        var dq = _imuQ1 * Quaternion.Conjugate(_imuQ0);
        if (dq.W < 0f) dq = -dq;
        var angle = 2f * MathF.Acos(MathF.Min(1f, dq.W));
        if (angle < 1e-7f) return _imuQ1;
        var axis = Vector3.Normalize(new Vector3(dq.X, dq.Y, dq.Z));
        var k = (float)((tMs - _imuT1) / (_imuT1 - _imuT0));
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis, angle * k) * _imuQ1);
    }

    /// <summary>
    /// 표시 1 회(무할당): 원지연 계측 → 워프 여부 결정 → 양안 역투영 또는 패스스루. truthQ 는 계측 전용(시뮬레이션 진값).
    /// </summary>
    public void Process(ReadOnlySpan<uint> src, Vector3 renderPos, Quaternion renderQ, double poseTimeMs, bool newFrame, double displayMs,
                        float ipdMm, float fovHDeg, float convergenceM, Quaternion truthQ)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Frames++;
        Repeat = !newFrame;
        if (Repeat) Repeats++;
        RawM2pMs = (float)(displayMs - poseTimeMs);
        if (RawM2pMs > MaxRawM2pMs) MaxRawM2pMs = RawM2pMs;
        var spike = RawM2pMs > BudgetMs;
        if (spike) SpikeFrames++;
        var pred = Predict(displayMs);
        Engaged = Mode switch
        {
            AtwMode.Always => true,
            AtwMode.Auto => spike || Repeat || AngleDeg(renderQ, pred) > EngageDeg,
            _ => false,
        };
        UncorrectedDeg = AngleDeg(renderQ, truthQ);
        if (Engaged)
        {
            StereoscopicFoveatedRenderingEngine.ComputeEyes(renderPos, renderQ, ipdMm, fovHDeg, convergenceM, out var rl, out var rr);
            StereoscopicFoveatedRenderingEngine.ComputeEyes(renderPos, pred, ipdMm, fovHDeg, convergenceM, out var dl, out var dr);
            var edge = Reproject(src[..EN], rl, dl, _out.AsSpan(0, EN)) + Reproject(src[EN..], rr, dr, _out.AsSpan(EN, EN));
            EdgeFill = edge / (2f * EN);
            DisplayOrientation = pred;
            EffectiveM2pMs = AtwLeadMs;
            Warps++;
        }
        else
        {
            src.CopyTo(_out);
            EdgeFill = 0f;
            DisplayOrientation = renderQ;
            EffectiveM2pMs = RawM2pMs;
        }
        ResidualDeg = AngleDeg(DisplayOrientation, truthQ);
        if (spike && EffectiveM2pMs <= BudgetMs) Defended++;
        if (EffectiveM2pMs > BudgetMs) ComfortViolations++;
        LastWarpUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>
    /// 회전 호모그래피 역투영(최근접 · 무할당). 표시 눈 화면 tan (tx, ty) → 월드 방향 = F + R·tx + U·ty →
    /// 렌더 눈 기저 내적(선형) → 원근 나눗셈. 반환 = 프러스텀 밖(가장자리 클램프) 픽셀 수. 동일 포즈면 비트 동일 항등.
    /// </summary>
    public static int Reproject(ReadOnlySpan<uint> src, in XrEyeCamera render, in XrEyeCamera display, Span<uint> dst)
    {
        // 표시 기저 → 렌더 기저 3×3(행 = 렌더 R/U/F, 열 = 표시 F/R/U)
        float xf = Vector3.Dot(display.Forward, render.Right), xr = Vector3.Dot(display.Right, render.Right), xu = Vector3.Dot(display.Up, render.Right);
        float yf = Vector3.Dot(display.Forward, render.Up), yr = Vector3.Dot(display.Right, render.Up), yu = Vector3.Dot(display.Up, render.Up);
        float zf = Vector3.Dot(display.Forward, render.Forward), zr = Vector3.Dot(display.Right, render.Forward), zu = Vector3.Dot(display.Up, render.Forward);
        float dW = display.TanR - display.TanL, rW = render.TanR - render.TanL;
        var edge = 0;
        for (var y = 0; y < EH; y++)
        {
            var ty = (1f - 2f * ((y + 0.5f) / EH)) * display.TanV;
            float bx = xf + xu * ty, by = yf + yu * ty, bz = zf + zu * ty;
            var row = y * EW;
            for (var x = 0; x < EW; x++)
            {
                var tx = display.TanL + (x + 0.5f) / EW * dW;
                var wz = bz + zr * tx;
                int ix, iy;
                if (wz <= 1e-6f) { ix = -1; iy = -1; }
                else
                {
                    var inv = 1f / wz;
                    var u = ((bx + xr * tx) * inv - render.TanL) / rW;
                    var v = 0.5f - (by + yr * tx) * inv / (2f * render.TanV);
                    ix = (int)MathF.Floor(u * EW); iy = (int)MathF.Floor(v * EH);
                }
                if ((uint)ix >= EW || (uint)iy >= EH)
                {
                    edge++;
                    ix = Math.Clamp(ix, 0, EW - 1); iy = Math.Clamp(iy, 0, EH - 1);
                }
                dst[row + x] = src[iy * EW + ix];
            }
        }
        return edge;
    }
}
