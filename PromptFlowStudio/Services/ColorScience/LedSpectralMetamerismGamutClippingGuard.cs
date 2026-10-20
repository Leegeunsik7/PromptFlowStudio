namespace PromptFlowStudio.Services.ColorScience;

/// <summary>[PromptFlow Studio v17.10.0] ACES RGC(Reference Gamut Compression) 파라미터 — 시안/마젠타/옐로 임계 · 한계 · 지수.</summary>
public struct GamutCompressionParams
{
    public float ThrC, ThrM, ThrY, LimC, LimM, LimY, Power;
    public float ScaleC, ScaleM, ScaleY;

    public static GamutCompressionParams Aces(float thresholdScale = 1f, float minLimC = 0f, float minLimM = 0f, float minLimY = 0f)
    {
        var p = new GamutCompressionParams
        {
            ThrC = 0.815f * thresholdScale, ThrM = 0.803f * thresholdScale, ThrY = 0.880f * thresholdScale,
            LimC = MathF.Max(1.147f, minLimC), LimM = MathF.Max(1.264f, minLimM), LimY = MathF.Max(1.312f, minLimY), Power = 1.2f,
        };
        p.Prepare();
        return p;
    }

    /// <summary>한계 거리(lim)가 정확히 1(색역 경계)로 사상되도록 스케일 계산.</summary>
    public void Prepare()
    {
        ScaleC = Scale(ThrC, LimC, Power); ScaleM = Scale(ThrM, LimM, Power); ScaleY = Scale(ThrY, LimY, Power);
    }

    private static float Scale(float thr, float lim, float pwr) => (lim - thr) / MathF.Pow(MathF.Pow((1f - thr) / (lim - thr), -pwr) - 1f, 1f / pwr);
}

/// <summary>
/// [PromptFlow Studio v17.10.0] 💡 LED 스펙트럼 메타메리즘 &amp; 색역 클리핑 가드(380–780 nm · 5 nm · 81 샘플 · 무할당 핫패스).
/// · 관측자: CIE 1931 2° 등색 함수 다중 로브 해석 근사(Wyman · Sloan · Shirley 2013). 카메라: 가우시안 분광 감도(R 600 · G 535 · B 460 nm).
/// · 조명: 키/필 = 플랑크 흑체(CCT) · LED 월 = 협대역 R 630 / G 525 / B 465 nm 가우시안 합(구동값 d).
/// · 스펙트럼 역산: 카메라 응답 행렬 A(채널 × LED 원색)로 d = A⁻¹·c(키 화이트) → 카메라가 보는 월 화이트 = 키 화이트(눈 기준 XYZ 매칭은 카메라에 틀어짐).
/// · 메타메리즘: 반사 패치 18종이 LED 스필 아래 카메라 RGB 가 키 조명 대비 어긋남 → IDT(카메라→ACEScg 최소제곱) 기준 ΔE*ab 측정 →
///   보정 K = argmin Σ‖K·c_led − c_ref‖²(3×3 정규방정식) → ACEScg 공간 K_ap1 = IDT·K·IDT⁻¹ 로 실시간 SIMD 적용.
/// · 색역 가드: 모니터/LED 월 원색(709/2020)으로 변환 시 음수(색역 밖) 성분을 ACES RGC 거리 압축 롤오프(thr → lim ↦ 1)로 경계 안에 수렴 · 클리핑 화소 계수.
///   한계 lim 은 ACES 기본값과 "AP1 색역 경계를 대상 원색으로 옮겼을 때의 최대 거리" 중 큰 값 → AP1 안의 모든 색이 대상 색역 안으로 사상.
/// </summary>
public sealed class LedSpectralMetamerismGamutClippingGuard
{
    public const int Samples = 81, Patches = 18;
    public const float LambdaMin = 380f, LambdaStep = 5f;

    // 패치 반사율: 기저 + 가우시안 2개(진폭 · 중심 · 폭) · 마지막 6개는 무채색.
    private static readonly float[] PatchDef =
    {
        0.10f, 0.30f, 610f, 70f, 0.05f, 480f, 40f,    // 어두운 피부
        0.18f, 0.45f, 620f, 80f, 0.08f, 500f, 50f,    // 밝은 피부
        0.06f, 0.30f, 470f, 35f, 0.05f, 560f, 60f,    // 하늘
        0.04f, 0.18f, 550f, 30f, 0.30f, 750f, 30f,    // 잎
        0.05f, 0.40f, 450f, 30f, 0.10f, 520f, 30f,    // 블루 플라워
        0.08f, 0.40f, 500f, 40f, 0.00f, 600f, 10f,    // 청록
        0.03f, 0.75f, 640f, 40f, 0.00f, 500f, 10f,    // 레드
        0.05f, 0.55f, 540f, 35f, 0.00f, 500f, 10f,    // 그린
        0.04f, 0.55f, 455f, 25f, 0.00f, 500f, 10f,    // 블루
        0.06f, 0.80f, 620f, 75f, 0.10f, 450f, 20f,    // 옐로/오렌지
        0.05f, 0.55f, 650f, 45f, 0.45f, 440f, 30f,    // 마젠타
        0.05f, 0.65f, 490f, 40f, 0.20f, 545f, 30f,    // 시안
        0.90f, 0f, 500f, 10f, 0f, 500f, 10f,
        0.59f, 0f, 500f, 10f, 0f, 500f, 10f,
        0.36f, 0f, 500f, 10f, 0f, 500f, 10f,
        0.19f, 0f, 500f, 10f, 0f, 500f, 10f,
        0.09f, 0f, 500f, 10f, 0f, 500f, 10f,
        0.03f, 0f, 500f, 10f, 0f, 500f, 10f,
    };

    private readonly double[] _cmf = new double[Samples * 3], _ssf = new double[Samples * 3], _led = new double[Samples * 3], _key = new double[Samples], _refl = new double[Samples * Patches];
    private readonly double[] _cRef = new double[Patches * 3], _cLed = new double[Patches * 3];
    private readonly float[] _deBefore = new float[Patches], _deAfter = new float[Patches];
    private readonly float[] _apRef = new float[Patches * 3], _apLed = new float[Patches * 3], _apCor = new float[Patches * 3];
    private GamutCompressionParams _params = GamutCompressionParams.Aces();
    private float _thresholdScale = 1f;

    public LedSpectralMetamerismGamutClippingGuard()
    {
        for (var i = 0; i < Samples; i++)
        {
            var l = LambdaMin + i * LambdaStep;
            Cmf(l, out var x, out var y, out var z);
            _cmf[i * 3] = x; _cmf[i * 3 + 1] = y; _cmf[i * 3 + 2] = z;
            _ssf[i * 3] = G(l, 600, 32) + 0.06 * G(l, 450, 20); _ssf[i * 3 + 1] = G(l, 535, 38); _ssf[i * 3 + 2] = G(l, 460, 28);
            for (var p = 0; p < Patches; p++)
            {
                var o = p * 7;
                _refl[p * Samples + i] = Math.Clamp(PatchDef[o] + PatchDef[o + 1] * G(l, PatchDef[o + 2], PatchDef[o + 3]) + PatchDef[o + 4] * G(l, PatchDef[o + 5], PatchDef[o + 6]), 0.0, 1.0);
            }
        }
        Recompute();
        ConfigureTarget(AcesColorMath.LinearMatrix(AcesSpace.AcesCg, AcesSpace.Rec709));
    }

    public bool CorrectionEnabled { get; set; } = true;
    public bool GuardEnabled { get; set; } = true;
    /// <summary>true = 카메라 분광 역산 구동 · false = 눈(XYZ) 기준 구동.</summary>
    public bool CameraMatchedDrive { get; private set; } = true;
    public float KeyCct { get; private set; } = 5600f;
    public float LedPeakR { get; private set; } = 630f;
    public float LedPeakG { get; private set; } = 525f;
    public float LedPeakB { get; private set; } = 465f;
    public float ThresholdScale => _thresholdScale;
    public ref readonly GamutCompressionParams Params => ref _params;

    public float DriveR { get; private set; }
    public float DriveG { get; private set; }
    public float DriveB { get; private set; }
    /// <summary>카메라가 본 LED 월 화이트 vs 키 화이트 ΔE*ab.</summary>
    public float WallWhiteDeltaE { get; private set; }
    public float DeltaEBefore { get; private set; }
    public float DeltaEAfter { get; private set; }
    public float DeltaEMaxBefore { get; private set; }
    public float DeltaEMaxAfter { get; private set; }
    public float PatchDeltaEBefore(int p) => _deBefore[p];
    public float PatchDeltaEAfter(int p) => _deAfter[p];
    /// <summary>ACEScg: 키 조명(기준) · LED 스필(원본) · LED 스필(보정).</summary>
    public float PatchRef(int p, int c) => _apRef[p * 3 + c];
    public float PatchLed(int p, int c) => _apLed[p * 3 + c];
    public float PatchCorrected(int p, int c) => _apCor[p * 3 + c];
    /// <summary>카메라 정규화 RGB → ACEScg(IDT · 최소제곱).</summary>
    public ColorMatrix3 Idt { get; private set; } = ColorMatrix3.Identity;
    /// <summary>카메라 RGB 공간 보정 K · ACEScg 공간 K_ap1.</summary>
    public ColorMatrix3 CameraCorrection { get; private set; } = ColorMatrix3.Identity;
    public ColorMatrix3 AcesCorrection { get; private set; } = ColorMatrix3.Identity;
    public double LastSolveUs { get; private set; }

    public long PixelsProcessed { get; private set; }
    public long OutOfGamutPixels { get; private set; }
    public long CompressedPixels { get; private set; }
    public long ResidualClipPixels { get; private set; }
    public double LastGuardUs { get; private set; }

    // ── 분광 데이터(UI) ─────────────────────────────────────

    public static float Lambda(int i) => LambdaMin + i * LambdaStep;
    public float CmfValue(int c, int i) => (float)_cmf[i * 3 + c];
    public float CameraSsf(int c, int i) => (float)_ssf[i * 3 + c];
    public float LedSpectrum(int j, int i) => (float)_led[i * 3 + j];
    public float KeySpectrum(int i) => (float)_key[i];
    public float Reflectance(int p, int i) => (float)_refl[p * Samples + i];

    /// <summary>스펙트럼 xy 색도(CIE 1931 · UI 궤적용).</summary>
    public void LocusXy(int i, out float x, out float y)
    {
        double X = _cmf[i * 3], Y = _cmf[i * 3 + 1], Z = _cmf[i * 3 + 2], s = X + Y + Z;
        x = s > 0 ? (float)(X / s) : 0f; y = s > 0 ? (float)(Y / s) : 0f;
    }

    /// <summary>LED 원색 j 의 xy 색도.</summary>
    public void LedXy(int j, out float x, out float y)
    {
        double X = 0, Y = 0, Z = 0;
        for (var i = 0; i < Samples; i++) { var e = _led[i * 3 + j]; X += e * _cmf[i * 3]; Y += e * _cmf[i * 3 + 1]; Z += e * _cmf[i * 3 + 2]; }
        var s = X + Y + Z;
        x = (float)(X / s); y = (float)(Y / s);
    }

    /// <summary>LED 원색 j(구동 1) 의 ACEScg 선형(키 화이트 Y = 1 기준).</summary>
    public void LedPrimaryAcesCg(int j, out float r, out float g, out float b)
    {
        double X = 0, Y = 0, Z = 0, Yw = 0;
        for (var i = 0; i < Samples; i++) { var e = _led[i * 3 + j]; X += e * _cmf[i * 3]; Y += e * _cmf[i * 3 + 1]; Z += e * _cmf[i * 3 + 2]; Yw += _key[i] * _cmf[i * 3 + 1]; }
        var k = 1.0 / Yw;
        r = (float)(X * k); g = (float)(Y * k); b = (float)(Z * k);
        XyzToAp1.Transform(ref r, ref g, ref b);
    }

    // ── 설정 ────────────────────────────────────────────────

    public void SetKeyCct(float cct) { KeyCct = Math.Clamp(cct, 2500f, 10000f); Recompute(); }
    public void SetDriveMode(bool cameraMatched) { CameraMatchedDrive = cameraMatched; Recompute(); }
    public void SetLedPeaks(float r, float g, float b) { LedPeakR = Math.Clamp(r, 600f, 680f); LedPeakG = Math.Clamp(g, 500f, 560f); LedPeakB = Math.Clamp(b, 440f, 480f); Recompute(); }
    public void SetThresholdScale(float s) { _thresholdScale = Math.Clamp(s, 0.5f, 1.15f); _params = GamutCompressionParams.Aces(_thresholdScale, MaxDistC * 1.002f, MaxDistM * 1.002f, MaxDistY * 1.002f); }

    /// <summary>AP1 경계 최대 거리(대상 원색 · 시안/마젠타/옐로).</summary>
    public float MaxDistC { get; private set; }
    public float MaxDistM { get; private set; }
    public float MaxDistY { get; private set; }

    /// <summary>대상 원색 설정: AP1 경계(한 성분 0 인 면 · 33² × 3) → 대상 공간 무채 거리 최댓값으로 한계 산정(비 핫패스).</summary>
    public void ConfigureTarget(in ColorMatrix3 ap1ToTarget)
    {
        float mc = 0, mm = 0, my = 0;
        for (var face = 0; face < 3; face++)
            for (var i = 0; i <= 32; i++)
                for (var j = 0; j <= 32; j++)
                {
                    float u = i / 32f, v = j / 32f;
                    if (u == 0f && v == 0f) continue;
                    float r = face == 0 ? 0f : u, g = face == 1 ? 0f : face == 0 ? u : v, b = face == 2 ? 0f : v;
                    ap1ToTarget.Transform(ref r, ref g, ref b);
                    var ach = MathF.Max(r, MathF.Max(g, b));
                    if (ach <= 0f) continue;
                    mc = MathF.Max(mc, (ach - r) / ach); mm = MathF.Max(mm, (ach - g) / ach); my = MathF.Max(my, (ach - b) / ach);
                }
        MaxDistC = mc; MaxDistM = mm; MaxDistY = my;
        SetThresholdScale(_thresholdScale);
    }
    public void ResetCounters() { PixelsProcessed = 0; OutOfGamutPixels = 0; CompressedPixels = 0; ResidualClipPixels = 0; }

    // ── 분광 계산 ───────────────────────────────────────────

    private static double G(double l, double mu, double sigma) { var t = (l - mu) / sigma; return Math.Exp(-0.5 * t * t); }
    private static double G2(double l, double mu, double s1, double s2) { var t = (l - mu) / (l < mu ? s1 : s2); return Math.Exp(-0.5 * t * t); }

    private static void Cmf(double l, out double x, out double y, out double z)
    {
        x = 1.056 * G2(l, 599.8, 37.9, 31.0) + 0.362 * G2(l, 442.0, 16.0, 26.7) - 0.065 * G2(l, 501.1, 20.4, 26.2);
        y = 0.821 * G2(l, 568.8, 46.9, 40.5) + 0.286 * G2(l, 530.9, 16.3, 31.1);
        z = 1.217 * G2(l, 437.0, 11.8, 36.0) + 0.681 * G2(l, 459.0, 26.0, 13.8);
    }

    private static double Planck(double lnm, double t)
    {
        var l = lnm * 1e-9;
        return 1.0 / (Math.Pow(l / 560e-9, 5) * (Math.Exp(1.4388e-2 / (l * t)) - 1.0));
    }

    private static readonly ColorMatrix3 XyzToAp1 = AcesColorMath.RgbToXyz(AcesSpace.AcesCg).Inverse();
    private static readonly ColorMatrix3 Ap1ToXyz = AcesColorMath.RgbToXyz(AcesSpace.AcesCg);

    /// <summary>조명 e(LED 구동 또는 키) × 반사 p → 카메라 RGB · XYZ.</summary>
    private void Integrate(bool led, double d0, double d1, double d2, int p, Span<double> cam, Span<double> xyz)
    {
        cam.Clear(); xyz.Clear();
        for (var i = 0; i < Samples; i++)
        {
            var e = led ? d0 * _led[i * 3] + d1 * _led[i * 3 + 1] + d2 * _led[i * 3 + 2] : _key[i];
            var s = e * (p < 0 ? 1.0 : _refl[p * Samples + i]);
            cam[0] += s * _ssf[i * 3]; cam[1] += s * _ssf[i * 3 + 1]; cam[2] += s * _ssf[i * 3 + 2];
            xyz[0] += s * _cmf[i * 3]; xyz[1] += s * _cmf[i * 3 + 1]; xyz[2] += s * _cmf[i * 3 + 2];
        }
    }

    /// <summary>스펙트럼 역산 · IDT · 보정 행렬 · ΔE 재계산(파라미터 변경 시 · 무할당).</summary>
    public void Recompute()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        double kmax = 0;
        for (var i = 0; i < Samples; i++)
        {
            var l = LambdaMin + i * LambdaStep;
            _key[i] = Planck(l, KeyCct); kmax = Math.Max(kmax, _key[i]);
            _led[i * 3] = G(l, LedPeakR, 7.6); _led[i * 3 + 1] = G(l, LedPeakG, 13.6); _led[i * 3 + 2] = G(l, LedPeakB, 9.3);
        }
        for (var i = 0; i < Samples; i++) _key[i] /= kmax;

        Span<double> cam = stackalloc double[3], xyz = stackalloc double[3], camW = stackalloc double[3], xyzW = stackalloc double[3];
        Span<double> a = stackalloc double[9], inv = stackalloc double[9];
        Integrate(false, 0, 0, 0, -1, camW, xyzW);
        var yw = xyzW[1];

        // 구동 역산: 카메라 응답(또는 XYZ) 행렬 A[c, j] · d = A⁻¹ · 화이트.
        for (var j = 0; j < 3; j++)
        {
            Integrate(true, j == 0 ? 1 : 0, j == 1 ? 1 : 0, j == 2 ? 1 : 0, -1, cam, xyz);
            for (var c = 0; c < 3; c++) a[c * 3 + j] = CameraMatchedDrive ? cam[c] : xyz[c];
        }
        Invert(a, inv);
        var tw = CameraMatchedDrive ? camW : xyzW;
        double d0 = inv[0] * tw[0] + inv[1] * tw[1] + inv[2] * tw[2], d1 = inv[3] * tw[0] + inv[4] * tw[1] + inv[5] * tw[2], d2 = inv[6] * tw[0] + inv[7] * tw[1] + inv[8] * tw[2];
        DriveR = (float)d0; DriveG = (float)d1; DriveB = (float)d2;

        // 카메라 정규화(키 화이트 → 1,1,1) · 패치 응답.
        for (var p = 0; p < Patches; p++)
        {
            Integrate(false, 0, 0, 0, p, cam, xyz);
            for (var c = 0; c < 3; c++) _cRef[p * 3 + c] = cam[c] / camW[c];
            Integrate(true, d0, d1, d2, p, cam, xyz);
            for (var c = 0; c < 3; c++) _cLed[p * 3 + c] = cam[c] / camW[c];
        }

        // IDT: 정규화 카메라 RGB → ACEScg(키 화이트 → D60 Bradford) 최소제곱.
        WhiteXy(xyzW, out var wx, out var wy);
        var cat = AcesColorMath.Bradford(wx, wy, 0.32168, 0.33767);
        Span<double> tgt = stackalloc double[Patches * 3];
        for (var p = 0; p < Patches; p++)
        {
            Integrate(false, 0, 0, 0, p, cam, xyz);
            float X = (float)(xyz[0] / yw), Y = (float)(xyz[1] / yw), Z = (float)(xyz[2] / yw);
            cat.Transform(ref X, ref Y, ref Z);
            XyzToAp1.Transform(ref X, ref Y, ref Z);
            tgt[p * 3] = X; tgt[p * 3 + 1] = Y; tgt[p * 3 + 2] = Z;
        }
        Idt = Fit(_cRef, tgt);
        CameraCorrection = Fit(_cLed, _cRef);
        AcesCorrection = Idt * (CameraCorrection * Idt.Inverse());

        // 월 화이트(직시) ΔE: 카메라가 본 LED 화이트 vs 키 화이트.
        Integrate(true, d0, d1, d2, -1, cam, xyz);
        float wr = (float)(cam[0] / camW[0]), wg = (float)(cam[1] / camW[1]), wb = (float)(cam[2] / camW[2]);
        Idt.Transform(ref wr, ref wg, ref wb);
        float er = 1f, eg = 1f, eb = 1f;
        Idt.Transform(ref er, ref eg, ref eb);
        WallWhiteDeltaE = DeltaE(wr, wg, wb, er, eg, eb);

        double sb = 0, sa = 0; float mb = 0, ma = 0;
        var k = CameraCorrection;
        for (var p = 0; p < Patches; p++)
        {
            float rr = (float)_cRef[p * 3], rg = (float)_cRef[p * 3 + 1], rb = (float)_cRef[p * 3 + 2];
            float lr = (float)_cLed[p * 3], lg = (float)_cLed[p * 3 + 1], lb = (float)_cLed[p * 3 + 2];
            float cr = lr, cg = lg, cb = lb;
            k.Transform(ref cr, ref cg, ref cb);
            Idt.Transform(ref rr, ref rg, ref rb); Idt.Transform(ref lr, ref lg, ref lb); Idt.Transform(ref cr, ref cg, ref cb);
            _apRef[p * 3] = rr; _apRef[p * 3 + 1] = rg; _apRef[p * 3 + 2] = rb;
            _apLed[p * 3] = lr; _apLed[p * 3 + 1] = lg; _apLed[p * 3 + 2] = lb;
            _apCor[p * 3] = cr; _apCor[p * 3 + 1] = cg; _apCor[p * 3 + 2] = cb;
            var b0 = DeltaE(lr, lg, lb, rr, rg, rb); var a0 = DeltaE(cr, cg, cb, rr, rg, rb);
            _deBefore[p] = b0; _deAfter[p] = a0;
            sb += b0; sa += a0; mb = MathF.Max(mb, b0); ma = MathF.Max(ma, a0);
        }
        DeltaEBefore = (float)(sb / Patches); DeltaEAfter = (float)(sa / Patches); DeltaEMaxBefore = mb; DeltaEMaxAfter = ma;
        LastSolveUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static void WhiteXy(ReadOnlySpan<double> xyz, out double x, out double y)
    {
        var s = xyz[0] + xyz[1] + xyz[2];
        x = xyz[0] / s; y = xyz[1] / s;
    }

    /// <summary>최소제곱 3×3: dst ≈ M · src(패치 행 · 3×3 정규방정식).</summary>
    private static ColorMatrix3 Fit(ReadOnlySpan<double> src, ReadOnlySpan<double> dst)
    {
        Span<double> ata = stackalloc double[9], inv = stackalloc double[9], atb = stackalloc double[9];
        ata.Clear(); atb.Clear();
        for (var p = 0; p < Patches; p++)
            for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++) { ata[i * 3 + j] += src[p * 3 + i] * src[p * 3 + j]; atb[i * 3 + j] += src[p * 3 + i] * dst[p * 3 + j]; }
        Invert(ata, inv);
        // X = (AᵀA)⁻¹ AᵀB → M = Xᵀ.
        Span<double> x = stackalloc double[9];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++) x[i * 3 + j] = inv[i * 3] * atb[j] + inv[i * 3 + 1] * atb[3 + j] + inv[i * 3 + 2] * atb[6 + j];
        return new(x[0], x[3], x[6], x[1], x[4], x[7], x[2], x[5], x[8]);
    }

    private static void Invert(ReadOnlySpan<double> m, Span<double> o)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
        var det = a * A + b * B + c * C;
        var k = Math.Abs(det) < 1e-300 ? 0 : 1.0 / det;
        o[0] = A * k; o[1] = -(b * i - c * h) * k; o[2] = (b * f - c * e) * k;
        o[3] = B * k; o[4] = (a * i - c * g) * k; o[5] = -(a * f - c * d) * k;
        o[6] = C * k; o[7] = -(a * h - b * g) * k; o[8] = (a * e - b * d) * k;
    }

    private static float Lab(float t) => t > 0.008856f ? MathF.Cbrt(t) : 7.787f * t + 16f / 116f;

    /// <summary>ACEScg 두 색 ΔE*ab(CIE76 · D60 백색).</summary>
    public static float DeltaE(float r1, float g1, float b1, float r2, float g2, float b2)
    {
        Ap1ToXyz.Transform(ref r1, ref g1, ref b1);
        Ap1ToXyz.Transform(ref r2, ref g2, ref b2);
        const float xn = 0.32168f / 0.33767f, zn = (1f - 0.32168f - 0.33767f) / 0.33767f;
        float fx1 = Lab(r1 / xn), fy1 = Lab(g1), fz1 = Lab(b1 / zn), fx2 = Lab(r2 / xn), fy2 = Lab(g2), fz2 = Lab(b2 / zn);
        float dl = 116f * (fy1 - fy2), da = 500f * ((fx1 - fy1) - (fx2 - fy2)), db = 200f * ((fy1 - fz1) - (fy2 - fz2));
        return MathF.Sqrt(dl * dl + da * da + db * db);
    }

    // ── 색역 압축 가드 ───────────────────────────────────────

    private static float Cmp(float d, float thr, float scale, float pwr)
    {
        var nd = (d - thr) / scale;
        return thr + scale * nd / MathF.Pow(1f + MathF.Pow(nd, pwr), 1f / pwr);
    }

    /// <summary>ACES RGC 1 화소(무채 거리 d = (max − c)/max · d &gt; thr 만 압축 · d = lim → 1 = 경계). 압축 시 true.</summary>
    public static bool Compress(ref float r, ref float g, ref float b, in GamutCompressionParams p)
    {
        var ach = MathF.Max(r, MathF.Max(g, b));
        if (ach <= 0f) return false;
        float dr = (ach - r) / ach, dg = (ach - g) / ach, db = (ach - b) / ach;
        var ch = false;
        if (dr > p.ThrC) { r = ach - Cmp(dr, p.ThrC, p.ScaleC, p.Power) * ach; ch = true; }
        if (dg > p.ThrM) { g = ach - Cmp(dg, p.ThrM, p.ScaleM, p.Power) * ach; ch = true; }
        if (db > p.ThrY) { b = ach - Cmp(db, p.ThrY, p.ScaleY, p.Power) * ach; ch = true; }
        return ch;
    }

    /// <summary>모니터 선형 평면 버퍼 가드(색역 밖 계수 → 압축 → 잔여 음수 계수 · 무할당).</summary>
    public void Process(Span<float> r, Span<float> g, Span<float> b)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        long oog = 0, cmp = 0, res = 0;
        var on = GuardEnabled;
        var p = _params;
        for (var i = 0; i < r.Length; i++)
        {
            float x = r[i], y = g[i], z = b[i];
            if (x < 0f || y < 0f || z < 0f) oog++;
            if (on && Compress(ref x, ref y, ref z, p)) { cmp++; r[i] = x; g[i] = y; b[i] = z; }
            if (x < -1e-6f || y < -1e-6f || z < -1e-6f) res++;
        }
        PixelsProcessed += r.Length; OutOfGamutPixels += oog; CompressedPixels += cmp; ResidualClipPixels += res;
        LastOutOfGamut = (int)oog; LastResidual = (int)res;
        LastGuardUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    public int LastOutOfGamut { get; private set; }
    public int LastResidual { get; private set; }
}
