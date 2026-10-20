namespace PromptFlowStudio.Services.SetExtension;

/// <summary>
/// [PromptFlow Studio v17.46.0] 🏛️ 세트 경계 찢어짐 &amp; 크로마 블리딩 방어 가드(<see cref="Push"/> · <see cref="Resolve"/> · <see cref="UpdateSpill"/> 무할당).
/// 세트 확장 렌더러는 L 프레임 전 발행된 포즈로 그린 결과를 지금 내놓으므로, 카메라 급회전 시 LED(물리 · 즉시) 와 확장 레이어 사이에
/// ω·L·dt 각도만큼 접합부가 미끄러진다(Seam Tearing). <b>에지 록</b>: 32 슬롯 포즈 링에서 팬/틸트/롤을 2차(속도 · 가속도) 외삽해
/// 확장 렌더 완료 시점 포즈로 선발행하고, 합성 시 실제 카메라 광선을 확장 렌더 포즈로 회전 재투영(레이트 래치 · 쌍선형 보간)해 경계를 LED 에 고정한다(UI 시프트 = Δpan·f, −Δtilt·f).
/// 각속도 1.5 rad/s 이상에서는 페더를 최대 2 배로 넓혀 잔여 틈을 마스킹한다. <b>블리딩</b>: 배우 윤곽의 그린 잔량(G − max(R,B))을
/// 목표 0.002 로 P 제어해 디스필 강도를 자동 상향한다.
/// </summary>
public sealed class BoundarySeamTearChromaBleedGuard
{
    public const int Cap = 32;
    public const float FastRate = 1.5f, SpillTarget = 0.002f, SpillGain = 6f;
    private readonly double[] _t = new double[Cap];
    private readonly float[] _pan = new float[Cap], _tilt = new float[Cap], _roll = new float[Cap];
    private readonly SetCameraPose[] _issued = new SetCameraPose[Cap];
    private int _head, _count;

    public bool EdgeLock { get; set; } = true;
    public bool AutoDespill { get; set; } = true;
    /// <summary>확장 렌더 파이프라인 지연(프레임).</summary>
    public int ExtLatencyFrames { get; set; } = 2;
    public float AngularRate { get; private set; }
    public float PoseErrPx { get; private set; }
    public float RawTearPx { get; private set; }
    public float ShiftX { get; private set; }
    public float ShiftY { get; private set; }
    public float FeatherScale { get; private set; } = 1f;
    public float MaxRawTearPx { get; private set; }
    public float MaxResidualPx { get; private set; }
    public long FastFrames { get; private set; }
    public long DespillBoosts { get; private set; }

    public void ResetMetrics() { MaxRawTearPx = 0; MaxResidualPx = 0; FastFrames = 0; DespillBoosts = 0; }

    /// <summary>실제 포즈 적재 · 확장 렌더러로 발행할 포즈(에지 록 = 2차 외삽 / OFF = 원시) 기록.</summary>
    public void Push(double t, in SetCameraPose pose)
    {
        _t[_head] = t; _pan[_head] = pose.Pan; _tilt[_head] = pose.Tilt; _roll[_head] = pose.Roll;
        var issued = pose;
        if (_count >= 2)
        {
            int k1 = (_head - 1 + Cap) % Cap, k2 = (_head - 2 + Cap) % Cap;
            var dt0 = (float)(t - _t[k1]); var dt1 = (float)(_t[k1] - _t[k2]);
            if (dt0 > 1e-6f && dt1 > 1e-6f)
            {
                var vp = Wrap(pose.Pan - _pan[k1]) / dt0; var vp1 = Wrap(_pan[k1] - _pan[k2]) / dt1;
                var vt = (pose.Tilt - _tilt[k1]) / dt0; var vt1 = (_tilt[k1] - _tilt[k2]) / dt1;
                var vr = (pose.Roll - _roll[k1]) / dt0;
                var ap = (vp - vp1) / dt0; var at = (vt - vt1) / dt0;
                AngularRate = MathF.Sqrt(vp * vp + vt * vt);
                if (EdgeLock)
                {
                    var h = ExtLatencyFrames * dt0;
                    issued.Pan = Wrap(pose.Pan + vp * h + 0.5f * ap * h * h);
                    issued.Tilt = pose.Tilt + vt * h + 0.5f * at * h * h;
                    issued.Roll = pose.Roll + vr * h;
                }
            }
        }
        _issued[_head] = issued;
        _head = (_head + 1) % Cap;
        if (_count < Cap) _count++;
    }

    /// <summary>지금 도착한 확장 렌더의 포즈(L 프레임 전 발행분) · 실제 포즈 대비 래치 시프트/페더 배율.</summary>
    public void Resolve(in SetCameraPose truth, out SetCameraPose extPose)
    {
        var l = Math.Clamp(ExtLatencyFrames, 0, Cap - 1);
        extPose = _count > l ? _issued[(_head - 1 - l + 2 * Cap) % Cap] : truth;
        var f = RealtimeVirtualSetExtensionEngine.FocalPx;
        var dp = Wrap(truth.Pan - extPose.Pan); var dtl = truth.Tilt - extPose.Tilt;
        RawTearPx = MathF.Sqrt(dp * dp + dtl * dtl) * f;
        if (RawTearPx > MaxRawTearPx) MaxRawTearPx = RawTearPx;
        if (EdgeLock)
        {
            ShiftX = dp * f; ShiftY = -dtl * f;
            PoseErrPx = RawTearPx;
            if (RawTearPx > MaxResidualPx) MaxResidualPx = RawTearPx;
            var fast = AngularRate > FastRate;
            if (fast) FastFrames++;
            FeatherScale = 1f + Math.Clamp((AngularRate - FastRate) / FastRate, 0f, 1f);
        }
        else
        {
            ShiftX = 0f; ShiftY = 0f; PoseErrPx = RawTearPx; FeatherScale = 1f;
        }
    }

    /// <summary>배우 윤곽 그린 잔량 → 디스필 강도 P 제어(자동 모드).</summary>
    public float UpdateSpill(double spill, float strength)
    {
        if (!AutoDespill) return strength;
        var e = (float)spill - SpillTarget;
        if (e <= 0f) return strength;
        DespillBoosts++;
        return Math.Clamp(strength + SpillGain * e, 0f, 1f);
    }

    public static float Wrap(float a) => a - 2f * MathF.PI * MathF.Round(a / (2f * MathF.PI));
}
