using System.Diagnostics;
using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;

namespace PromptFlowStudio.Services.Editorial;

public enum ShotSize : byte { Wide = 0, Medium, Close, OverShoulder }

/// <summary>앵글 메타: 카메라 위치/전방 · 프레이밍 대상 인물(−1 = 투샷/와이드) · 샷 크기.</summary>
public struct CameraAngle
{
    public Vector3 Position, Forward;
    public int Subject;
    public ShotSize Size;
    public string Name;
}

public readonly record struct EditCut(int Frame, int From, int To, CutReason Reason, float Margin);

/// <summary>
/// [PromptFlow Studio v16.79.0] 🎥 실시간 자율 멀티카메라 교차 편집 엔진.
/// 앵글별 특징(무할당 SIMD): 루마 프레임 절대차 합(Vector&lt;byte&gt; max−min → 확장 누적) = 모션 템포 · 3DGS 위치 평균 변위(Vector&lt;float&gt;) · 오디오 블록 피크/RMS → 온셋비.
/// 인물 시선: 화자 시선 방향 · 카메라 방향 내적(정면성) + 프레이밍 대상 일치. 점수 = 화자 · 시선 · 모션 · 오디오 · 신선도 · 샷 크기 교대 − 점프 컷(동일 크기 30° 미만) − 180° 규칙 위반.
/// 컷 판정: 최소/최대 샷 길이(리듬 조율기 목표 0.5~2×) · 히스테리시스 · 컷 기회 = 비트 프레임 또는 모션 온셋(컷 온 액션) · 최대 길이 1.25× 강제 → 타임라인 V1 기록.
/// </summary>
public sealed class AutonomousMultiCameraEditorialEngine
{
    public const int MaxAngles = 8, MaxSubjects = 4, MaxCutLog = 512;

    private readonly object _gate = new();
    private readonly CameraAngle[] _cam = new CameraAngle[MaxAngles];
    private readonly byte[][] _prevLuma = new byte[MaxAngles][];
    private readonly bool[] _hasPrev = new bool[MaxAngles];
    private readonly float[][] _prevSplat = new float[MaxAngles][];
    private readonly float[] _motion = new float[MaxAngles], _motionEma = new float[MaxAngles], _motionSlow = new float[MaxAngles];
    private readonly float[] _peak = new float[MaxAngles], _rms = new float[MaxAngles], _rmsSlow = new float[MaxAngles];
    private readonly float[] _score = new float[MaxAngles], _gaze = new float[MaxAngles], _side = new float[MaxAngles];
    private readonly int[] _lastUsed = new int[MaxAngles];
    private readonly Vector3[] _subPos = new Vector3[MaxSubjects], _subGaze = new Vector3[MaxSubjects];
    private readonly EditCut[] _cuts = new EditCut[MaxCutLog];
    private int _angles, _w, _h, _subjects = 2, _speaker = -1, _current, _shotStart, _frame, _cutCount, _track;
    private bool _started;

    public AutonomousMultiCameraEditorialEngine(NeuralRhythmMontageSynthesizer? rhythm = null) => Rhythm = rhythm;

    public NeuralRhythmMontageSynthesizer? Rhythm { get; set; }
    public EditTimeline? Timeline { get; private set; }
    public int Fps { get; private set; } = 30;
    public int AngleCount => _angles;
    public int Width => _w;
    public int Height => _h;
    public int Current => _current;
    public int Frame => _frame;
    public int ShotStart => _shotStart;
    public int Speaker => _speaker;
    public int CutCount => _cutCount;
    public EditCut Cut(int k) => _cuts[(_cutCount - 1 - k) % MaxCutLog];
    public ref readonly CameraAngle Angle(int i) => ref _cam[i];
    public float Score(int i) => _score[i];
    public float Motion(int i) => _motionEma[i];
    public float Peak(int i) => _peak[i];
    public float GazeScore(int i) => _gaze[i];
    public float Side(int i) => _side[i];
    public Vector3 SubjectPosition(int s) => _subPos[s];
    public int SubjectCount => _subjects;

    public bool BeatSync { get; set; } = true;
    public bool CutOnAction { get; set; } = true;
    public bool Enforce180 { get; set; } = true;
    public float Hysteresis { get; set; } = 0.12f;
    public int DefaultShotFrames { get; set; } = 90;
    public long Violations180 { get; private set; }
    public long JumpCuts { get; private set; }
    public long BeatCuts { get; private set; }
    public long ActionCuts { get; private set; }
    public float LastStepMicros { get; private set; }
    public string? LastAction { get; private set; }

    /// <summary>제어 평면: 앵글 수 · 루마 해상도 · fps · 타임라인 트랙(V1).</summary>
    public void Configure(int angles, int width, int height, int fps, EditTimeline timeline, int track)
    {
        lock (_gate)
        {
            _angles = Math.Clamp(angles, 1, MaxAngles); _w = width; _h = height; Fps = fps; Timeline = timeline; _track = track;
            for (var i = 0; i < _angles; i++)
            {
                if (_prevLuma[i] is null || _prevLuma[i].Length != width * height) _prevLuma[i] = new byte[width * height];
                _hasPrev[i] = false; _motion[i] = _motionEma[i] = _motionSlow[i] = _peak[i] = _rms[i] = _rmsSlow[i] = 0; _lastUsed[i] = -100000;
            }
            _frame = 0; _cutCount = 0; _started = false; _current = 0; _shotStart = 0;
            Violations180 = JumpCuts = BeatCuts = ActionCuts = 0;
        }
    }

    public void SetCamera(int i, Vector3 pos, Vector3 target, int subject, ShotSize size, string name)
    {
        lock (_gate)
        {
            _cam[i] = new CameraAngle { Position = pos, Forward = Vector3.Normalize(target - pos), Subject = subject, Size = size, Name = name };
        }
    }

    /// <summary>인물 상태(위치 · 시선 방향). 0/1 번 인물이 액션 라인(180° 규칙)을 정의.</summary>
    public void SetSubject(int s, Vector3 head, Vector3 gaze)
    {
        lock (_gate) { if ((uint)s < MaxSubjects) { _subPos[s] = head; _subGaze[s] = gaze.LengthSquared() > 1e-8f ? Vector3.Normalize(gaze) : -Vector3.UnitZ; _subjects = Math.Max(_subjects, s + 1); } }
    }

    public void SetSpeaker(int s) { lock (_gate) _speaker = s; }

    // ══════════════════════ SIMD 특징 ══════════════════════

    /// <summary>|a − b| 합(바이트 · Vector&lt;byte&gt; max−min → ushort → uint 확장 누적).</summary>
    public static long SumAbsDiff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var n = Math.Min(a.Length, b.Length);
        long sum = 0; var i = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<byte>.Count)
        {
            var acc = Vector<uint>.Zero;
            var chunk = 0;
            for (; i + Vector<byte>.Count <= n; i += Vector<byte>.Count)
            {
                var va = new Vector<byte>(a.Slice(i)); var vb = new Vector<byte>(b.Slice(i));
                var d = Vector.Max(va, vb) - Vector.Min(va, vb);
                Vector.Widen(d, out var lo, out var hi);
                Vector.Widen(lo + hi, out var l2, out var h2);
                acc += l2 + h2;
                if (++chunk == 1 << 20) { sum += Vector.Sum(acc); acc = Vector<uint>.Zero; chunk = 0; }
            }
            sum += Vector.Sum(acc);
        }
        for (; i < n; i++) sum += Math.Abs(a[i] - b[i]);
        return sum;
    }

    /// <summary>최대 절댓값 · 제곱합(Vector&lt;float&gt;).</summary>
    public static (float Peak, double SumSq) PeakAndEnergy(ReadOnlySpan<float> x)
    {
        var i = 0; float pk = 0; double ss = 0;
        if (Vector.IsHardwareAccelerated && x.Length >= Vector<float>.Count)
        {
            var vp = Vector<float>.Zero; var vs = Vector<float>.Zero;
            for (; i + Vector<float>.Count <= x.Length; i += Vector<float>.Count)
            {
                var v = new Vector<float>(x.Slice(i));
                vp = Vector.Max(vp, Vector.Abs(v)); vs += v * v;
            }
            for (var l = 0; l < Vector<float>.Count; l++) { pk = MathF.Max(pk, vp[l]); ss += vs[l]; }
        }
        for (; i < x.Length; i++) { pk = MathF.Max(pk, MathF.Abs(x[i])); ss += x[i] * x[i]; }
        return (pk, ss);
    }

    /// <summary>두 위치 배열 평균 변위(Vector&lt;float&gt; 절댓값 합 / 점 수 · L1 근사).</summary>
    public static float MeanDisplacement(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n < 3) return 0;
        var i = 0; float s = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            for (; i + Vector<float>.Count <= n; i += Vector<float>.Count) acc += Vector.Abs(new Vector<float>(a.Slice(i)) - new Vector<float>(b.Slice(i)));
            s = Vector.Sum(acc);
        }
        for (; i < n; i++) s += MathF.Abs(a[i] - b[i]);
        return s / (n / 3);
    }

    /// <summary>앵글 루마 프레임 제출 → 모션(픽셀당 평균 절대차 0..1).</summary>
    public void SubmitLuma(int angle, ReadOnlySpan<byte> luma)
    {
        lock (_gate)
        {
            var prev = _prevLuma[angle];
            if (_hasPrev[angle]) _motion[angle] = SumAbsDiff(luma, prev) / (255f * Math.Max(1, prev.Length));
            luma[..Math.Min(luma.Length, prev.Length)].CopyTo(prev);
            _hasPrev[angle] = true;
        }
    }

    /// <summary>3DGS 스트림 앵글 → 모션(평균 변위 m → 0.05 m = 1). 최초 1회 버퍼 할당.</summary>
    public void SubmitSplats(int angle, GaussianScene scene)
    {
        lock (_gate)
        {
            var n = scene.Count * 3;
            var prev = _prevSplat[angle];
            if (prev is null || prev.Length < n) { prev = _prevSplat[angle] = new float[Math.Max(n, 64)]; _hasPrev[angle] = false; }
            if (_hasPrev[angle]) _motion[angle] = Math.Clamp(MeanDisplacement(scene.Pos.AsSpan(0, n), prev.AsSpan(0, n)) / 0.05f, 0f, 1f) * 0.1f;
            scene.Pos.AsSpan(0, n).CopyTo(prev);
            _hasPrev[angle] = true;
        }
    }

    public void SubmitAudio(int angle, ReadOnlySpan<float> block)
    {
        lock (_gate)
        {
            var (pk, ss) = PeakAndEnergy(block);
            _peak[angle] = pk;
            _rms[angle] = block.Length > 0 ? (float)Math.Sqrt(ss / block.Length) : 0;
        }
    }

    // ══════════════════════ 결정 ══════════════════════

    /// <summary>1프레임 결정(무할당). 반환 = 프로그램 앵글.</summary>
    public int Step()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Editorial.Step");
            var t0 = Stopwatch.GetTimestamp();
            var tl = Timeline;
            UpdateFeatures();
            if (!_started)
            {
                _current = BestAngle(-1, false);
                _shotStart = _frame; _lastUsed[_current] = _frame; _started = true;
                tl?.Add(_track, _current, _frame, 1, CutReason.Start);
                LogCut(-1, _current, CutReason.Start, 0);
            }
            else
            {
                var len = _frame - _shotStart;
                var target = Rhythm is not null && Rhythm.TempoConfidence > 0.05f ? Rhythm.TargetShotFrames(_frame / (float)Fps, Fps) : DefaultShotFrames;
                var minLen = Math.Max(Fps / 3, target / 2); var maxLen = Math.Max(minLen + 1, target * 2);
                var beat = BeatSync && Rhythm is not null && Rhythm.IsBeatFrame(_frame, Fps);
                var action = CutOnAction && _motionEma[_current] > _motionSlow[_current] * 1.6f && _motionEma[_current] > 0.01f;
                var opportunity = BeatSync && Rhythm is not null && Rhythm.TempoConfidence > 0.05f ? beat : (beat || action || !CutOnAction);
                if (len >= minLen && (opportunity || len >= maxLen * 5 / 4))
                {
                    var best = BestAngle(_current, true);
                    var margin = best >= 0 ? _score[best] - _score[_current] : 0f;
                    CutReason reason;
                    if (best >= 0 && margin > Hysteresis) reason = beat ? CutReason.Beat : action ? CutReason.Action : _speaker >= 0 && _cam[best].Subject == _speaker ? CutReason.Speaker : CutReason.Reaction;
                    else if (len >= maxLen && best >= 0) reason = CutReason.MaxLength;
                    else reason = CutReason.Start;
                    if (reason != CutReason.Start && best != _current) DoCut(best, reason, margin);
                }
            }
            tl?.ExtendLast(_track, _frame + 1);
            _frame++;
            LastStepMicros = (float)(Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1000.0);
            return _current;
        }
    }

    private void DoCut(int to, CutReason reason, float margin)
    {
        var from = _current;
        if (_side[from] * _side[to] < -0.01f) Violations180++;
        if (IsJump(from, to)) JumpCuts++;
        if (reason == CutReason.Beat) BeatCuts++;
        if (reason == CutReason.Action) ActionCuts++;
        _current = to; _shotStart = _frame; _lastUsed[to] = _frame;
        Timeline?.Add(_track, to, _frame, 1, reason);
        LogCut(from, to, reason, margin);
        LastAction = "✂ " + _cam[from].Name + " → " + _cam[to].Name;
    }

    private void LogCut(int from, int to, CutReason r, float m)
    {
        _cuts[_cutCount % MaxCutLog] = new EditCut(_frame, from, to, r, m);
        _cutCount++;
    }

    private bool IsJump(int a, int b)
    {
        if (_cam[a].Size != _cam[b].Size) return false;
        var c = Vector3.Dot(_cam[a].Forward, _cam[b].Forward);
        return c > MathF.Cos(30f * MathF.PI / 180f);
    }

    private void UpdateFeatures()
    {
        // 액션 라인(인물 0 → 1) · 카메라 측면(+/−, |값| 작음 = 라인 위 중립)
        var a = _subPos[0]; var b = _subPos[Math.Min(1, _subjects - 1)];
        var line = b - a; line.Y = 0;
        var hasLine = line.LengthSquared() > 1e-4f;
        if (hasLine) line = Vector3.Normalize(line);
        var mid = 0.5f * (a + b);
        float maxM = 1e-4f, maxR = 1e-5f;
        for (var i = 0; i < _angles; i++)
        {
            _motionEma[i] += (_motion[i] - _motionEma[i]) * 0.35f;
            _motionSlow[i] += (_motion[i] - _motionSlow[i]) * 0.03f;
            _rmsSlow[i] += (_rms[i] - _rmsSlow[i]) * 0.05f;
            maxM = MathF.Max(maxM, _motionEma[i]); maxR = MathF.Max(maxR, _rms[i]);
            var rel = _cam[i].Position - mid; rel.Y = 0;
            var side = hasLine ? line.X * rel.Z - line.Z * rel.X : 0f;
            var dist = MathF.Max(0.1f, rel.Length());
            _side[i] = MathF.Abs(side) / dist < 0.15f ? 0f : MathF.Sign(side);
        }
        for (var i = 0; i < _angles; i++)
        {
            ref readonly var c = ref _cam[i];
            // 시선/정면성: 프레이밍 대상(없으면 화자) 시선 · 카메라 방향
            var subj = c.Subject >= 0 ? c.Subject : _speaker;
            float gaze = 0.3f;
            if (subj >= 0)
            {
                var toCam = c.Position - _subPos[subj];
                gaze = toCam.LengthSquared() > 1e-6f ? Math.Clamp(Vector3.Dot(_subGaze[subj], Vector3.Normalize(toCam)), 0f, 1f) : 0f;
            }
            _gaze[i] = gaze;
            var speakerFit = _speaker < 0 ? (c.Subject < 0 ? 0.6f : 0.3f) : c.Subject == _speaker ? 1f : c.Subject < 0 ? 0.45f : 0.2f;
            var motion = _motionEma[i] / maxM;
            var audio = _rms[i] / maxR;
            var onset = _rmsSlow[i] > 1e-6f ? Math.Clamp(_rms[i] / _rmsSlow[i] - 1f, 0f, 1f) : 0f;
            var idle = Math.Clamp((_frame - _lastUsed[i]) / (float)(Fps * 8), 0f, 1f);
            var intensity = Rhythm?.Intensity(_frame / (float)Fps) ?? 0.5f;
            var sizeFit = c.Size == ShotSize.Wide ? 1f - intensity : c.Size == ShotSize.Close ? intensity : 0.5f;
            _score[i] = 0.9f * speakerFit + 0.6f * gaze + (0.3f + 0.6f * intensity) * motion + 0.35f * audio + 0.2f * onset + 0.25f * idle + 0.3f * sizeFit;
        }
    }

    /// <summary>최고 점수 앵글(exclude 제외 · checkRules = 180°/점프 컷 금지).</summary>
    private int BestAngle(int exclude, bool checkRules)
    {
        var best = -1; var bs = float.MinValue;
        for (var i = 0; i < _angles; i++)
        {
            if (i == exclude) continue;
            if (checkRules && exclude >= 0)
            {
                if (Enforce180 && _side[exclude] * _side[i] < -0.01f) continue;
                if (IsJump(exclude, i)) continue;
            }
            if (_score[i] > bs) { bs = _score[i]; best = i; }
        }
        return best < 0 ? Math.Max(0, exclude) : best;
    }
}
