using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Mocap;

public enum CharacterEmotion : byte { Neutral = 0, Joy, Sadness, Anger, Surprise, Fear }

/// <summary>BCI/생체 신호(정서가 −1..1 · 각성 0..1 · 주의 0..1). Valid = false 면 무시.</summary>
public struct BciSignal
{
    public float Valence, Arousal, Attention;
    public bool Valid;
}

/// <summary>
/// [PromptFlow Studio v16.75.0] 🎭 제너레이티브 캐릭터 표정 연기 &amp; 물리 제약 애니메이션 가드.
///   ▸ 표정: 대사 어휘(한/영) 정서가·각성 + BCI 신호 가중 혼합 → 기쁨/슬픔/분노/놀람 템플릿 연속 혼합 블렌드셰이프 16 · 한글 중성/영문 모음 → 비셈(입 벌림·오므림·둥글림) 음절 포락선 ·
///     결정적 눈 깜빡임 · 캡처 신뢰도로 캡처 표정과 생성 표정 보간(캡처 공백 자동 보완).
///   ▸ 제스처: 각성/정서 → 발화 끄덕임 · 슬픔 고개 숙임 · 놀람 어깨 들썩 · 분노 상체 숙임(로컬 가산 회전 → 리타겟 결합).
///   ▸ 물리 가드(무할당): 발 접촉(높이+속도 히스테리시스) → 발목 고정 2-본 IK(뼈 길이·무릎 극 방향 보존) · 해제 블렌드 · 지면 관통 차단(발목 높이/발끝) ·
///     몸통 캡슐 자기 충돌(손목/손 밀어내기 → 팔 IK) · 루트 최대 가속 관성 제한 — 전/후 미끄럼·관통 지표.
/// </summary>
public sealed class GenerativeCharacterPerformanceGuard
{
    private const int MaxSyllables = 512;
    private static readonly (string[] Words, float V, float A)[] Lexicon =
    {
        (new[] { "기뻐", "기쁘", "행복", "좋아", "사랑", "최고", "신나", "고마", "happy", "glad", "love", "great", "awesome", "yay", "thank" }, 0.85f, 0.6f),
        (new[] { "슬퍼", "슬프", "우울", "눈물", "외로", "미안", "그리워", "sad", "sorry", "cry", "lonely", "miss" }, -0.75f, 0.2f),
        (new[] { "화나", "화가", "짜증", "분노", "싫어", "미워", "angry", "hate", "furious", "mad" }, -0.7f, 0.9f),
        (new[] { "놀라", "깜짝", "세상에", "대박", "헐", "wow", "omg", "surprise", "what?" }, 0.1f, 0.95f),
        (new[] { "무서", "두려", "겁나", "scared", "afraid", "fear", "terrified" }, -0.6f, 0.8f),
    };

    private readonly object _gate = new();
    private readonly byte[] _vis = new byte[MaxSyllables];
    private readonly float[] _captured = new float[FaceShapes.Count], _face = new float[FaceShapes.Count], _gen = new float[FaceShapes.Count];
    private int _visN;
    private long _speechStart = long.MinValue;
    private float _textV, _textA = 0.3f, _capConf, _scale = 1f;
    private string _text = "";
    private BciSignal _bci;
    // 물리 상태
    private readonly bool[] _locked = new bool[2];
    private readonly Vector3[] _lockAnkle = new Vector3[2], _prevToeRaw = new Vector3[2], _prevToeOut = new Vector3[2], _relOffset = new Vector3[2];
    private readonly float[] _relBlend = new float[2];
    private Vector3 _prevRoot, _rootVel;
    private bool _hasPrev;
    private float _slideBefore, _slideAfter, _penBefore, _penAfter;
    private long _collisions, _rootClamps, _frames;

    public event Action? OnChange;

    public float Ground { get; set; }
    public bool FootLock { get; set; } = true;
    public bool GroundClamp { get; set; } = true;
    public bool SelfCollision { get; set; } = true;
    public bool Inertia { get; set; } = true;
    public float BciWeight { get; set; } = 0.5f;
    public float SyllablesPerSecond { get; set; } = 6f;
    public float MaxRootAccel { get; set; } = 30f;
    public int BlendFrames { get; set; } = 6;
    public float AnkleHeight => 0.06f * _scale;
    public float TorsoRadius => 0.12f * _scale;

    public string Dialogue => _text;
    public float TextValence => _textV;
    public float TextArousal => _textA;
    public BciSignal Bci => _bci;
    public bool IsSpeaking(long nowMicros) => _visN > 0 && nowMicros >= _speechStart && (nowMicros - _speechStart) * SyllablesPerSecond / 1e6f < _visN;
    public bool IsLocked(int side) => _locked[side];
    /// <summary>접촉 중인 발끝의 프레임당 수평 이동 최댓값(mm · EMA) — 가드 전/후.</summary>
    public float SlideBeforeMm => _slideBefore * 1000f;
    public float SlideAfterMm => _slideAfter * 1000f;
    public float PenetrationBeforeMm => _penBefore * 1000f;
    public float PenetrationAfterMm => _penAfter * 1000f;
    public long Collisions => Interlocked.Read(ref _collisions);
    public long RootClamps => Interlocked.Read(ref _rootClamps);
    public ReadOnlySpan<float> Face => _face;
    public string? LastAction { get; private set; }

    /// <summary>캐릭터 키 → 접촉/충돌 임계 스케일.</summary>
    public void SetCharacterHeight(float height) => _scale = MathF.Max(0.2f, height / MocapSkeleton.ReferenceHeight);

    // ══════════════════════ 대사 · BCI · 캡처(제어 평면) ══════════════════════

    public static (float Valence, float Arousal) AnalyzeEmotion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (0, 0.3f);
        float v = 0, a = 0; var n = 0;
        foreach (var (words, wv, wa) in Lexicon)
            foreach (var w in words)
                if (text.Contains(w, StringComparison.OrdinalIgnoreCase)) { v += wv; a += wa; n++; break; }
        if (n == 0) { v = 0; a = 0.3f; } else { v /= n; a /= n; }
        var bang = 0;
        foreach (var ch in text) if (ch == '!') bang++;
        a = MathF.Min(1, a + 0.1f * Math.Min(3, bang));
        return (Math.Clamp(v, -1f, 1f), a);
    }

    public static CharacterEmotion Classify(float v, float a)
    {
        if (MathF.Abs(v) < 0.2f && a < 0.6f) return CharacterEmotion.Neutral;
        if (v >= 0.2f) return a > 0.85f && v < 0.4f ? CharacterEmotion.Surprise : CharacterEmotion.Joy;
        if (v <= -0.2f) return a > 0.7f ? CharacterEmotion.Anger : a > 0.6f ? CharacterEmotion.Fear : CharacterEmotion.Sadness;
        return CharacterEmotion.Surprise;
    }

    /// <summary>대사 설정 → 정서 분석 · 비셈 타임라인(음절 단위) · 발화 시작 시각.</summary>
    public void SetDialogue(string text, long startMicros)
    {
        lock (_gate)
        {
            _text = text ?? "";
            (_textV, _textA) = AnalyzeEmotion(_text);
            _visN = 0;
            foreach (var ch in _text)
            {
                if (_visN >= MaxSyllables) break;
                var cls = VisemeOf(ch);
                if (cls == 255) continue;
                _vis[_visN++] = cls;
            }
            _speechStart = startMicros;
        }
        LastAction = "🗣 대사 · " + Classify(_textV, _textA) + " · 음절 " + _visN.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Raise();
    }

    /// <summary>문자 → 비셈(0 쉼 · 1 아 · 2 에 · 3 이 · 4 오 · 5 우 · 255 무시).</summary>
    public static byte VisemeOf(char ch)
    {
        if (ch >= 0xAC00 && ch <= 0xD7A3)
        {
            var jung = (ch - 0xAC00) % 588 / 28;
            return jung switch
            {
                0 or 1 or 2 or 3 or 9 or 10 => 1,
                4 or 5 or 6 or 7 or 15 => 2,
                18 or 19 or 20 => 3,
                8 or 11 or 12 or 14 => 4,
                _ => 5,
            };
        }
        return char.ToLowerInvariant(ch) switch
        {
            'a' => 1, 'e' => 2, 'i' or 'y' => 3, 'o' => 4, 'u' or 'w' => 5,
            ' ' or ',' or '.' or '!' or '?' or '~' => 0,
            _ => 255,
        };
    }

    public void SetBci(in BciSignal s) { lock (_gate) _bci = s; }

    public void SetCapturedFace(ReadOnlySpan<float> weights, float confidence)
    {
        lock (_gate)
        {
            weights[..FaceShapes.Count].CopyTo(_captured);
            _capConf = Math.Clamp(confidence, 0f, 1f);
        }
    }

    public (float Valence, float Arousal) Mood
    {
        get
        {
            var v = _textV; var a = _textA;
            if (_bci.Valid) { v += (_bci.Valence - v) * BciWeight; a += (_bci.Arousal - a) * BciWeight; }
            return (v, a);
        }
    }

    // ══════════════════════ 표정 · 제스처(무할당) ══════════════════════

    private static float Hash01(long x)
    {
        var z = (ulong)x * 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z ^= z >> 27;
        return (z >> 40) * (1f / (1 << 24));
    }

    /// <summary>생성 + 캡처 표정 → 최종 블렌드셰이프 16(<see cref="Face"/>).</summary>
    public void EvaluateFace(long nowMicros, Span<float> outWeights)
    {
        lock (_gate)
        {
            var (v, a) = Mood;
            var joy = MathF.Max(0, v);
            var neg = MathF.Max(0, -v);
            var sad = neg * (1 - a);
            var anger = neg * a;
            var surprise = a * (1 - MathF.Abs(v)) * (a > 0.5f ? 1 : 0);
            var g = _gen;
            g.AsSpan().Clear();
            g[FaceShapes.MouthSmile] += 0.9f * joy; g[FaceShapes.EyeSquint] += 0.4f * joy; g[FaceShapes.BrowOuterUp] += 0.2f * joy;
            g[FaceShapes.MouthFrown] += 0.85f * sad; g[FaceShapes.BrowInnerUp] += 0.7f * sad; g[FaceShapes.EyeBlinkL] += 0.25f * sad; g[FaceShapes.EyeBlinkR] += 0.25f * sad;
            g[FaceShapes.BrowDownL] += 0.9f * anger; g[FaceShapes.BrowDownR] += 0.9f * anger; g[FaceShapes.NoseSneer] += 0.5f * anger; g[FaceShapes.MouthStretch] += 0.3f * anger;
            g[FaceShapes.BrowInnerUp] += 0.8f * surprise; g[FaceShapes.BrowOuterUp] += 0.8f * surprise; g[FaceShapes.EyeWide] += 0.9f * surprise; g[FaceShapes.JawOpen] += 0.35f * surprise;
            if (_bci.Valid) g[FaceShapes.EyeWide] += 0.3f * MathF.Max(0, _bci.Attention - 0.5f);
            // 눈 깜빡임(결정적 · 2.5~5.5 s 간격 · 120 ms)
            var tSec = nowMicros / 1e6f;
            var slot = (long)(tSec / 4f);
            var blinkAt = slot * 4f + Hash01(slot) * 3f;
            var bt = tSec - blinkAt;
            if (bt >= 0 && bt < 0.12f) { var bw = MathF.Sin(bt / 0.12f * MathF.PI); g[FaceShapes.EyeBlinkL] += bw; g[FaceShapes.EyeBlinkR] += bw; }
            // 캡처 보간
            var c = _capConf;
            for (var i = 0; i < FaceShapes.Count; i++) _face[i] = Math.Clamp(_captured[i] * c + g[i] * (1 - c), 0f, 1f);
            // 비셈(발화 중 입 모양은 최댓값 결합)
            if (IsSpeaking(nowMicros))
            {
                var pos = (nowMicros - _speechStart) * SyllablesPerSecond / 1e6f;
                var idx = (int)pos; var env = MathF.Sin((pos - idx) * MathF.PI);
                var cls = _vis[Math.Min(idx, _visN - 1)];
                float jaw = 0, funnel = 0, pucker = 0, stretch = 0, smile = 0;
                switch (cls)
                {
                    case 1: jaw = 0.7f; break;
                    case 2: jaw = 0.4f; stretch = 0.3f; break;
                    case 3: jaw = 0.15f; smile = 0.4f; break;
                    case 4: jaw = 0.5f; funnel = 0.6f; break;
                    case 5: jaw = 0.2f; pucker = 0.8f; break;
                }
                _face[FaceShapes.JawOpen] = MathF.Max(_face[FaceShapes.JawOpen], jaw * env);
                _face[FaceShapes.MouthFunnel] = MathF.Max(_face[FaceShapes.MouthFunnel], funnel * env);
                _face[FaceShapes.MouthPucker] = MathF.Max(_face[FaceShapes.MouthPucker], pucker * env);
                _face[FaceShapes.MouthStretch] = MathF.Max(_face[FaceShapes.MouthStretch], stretch * env);
                _face[FaceShapes.MouthSmile] = MathF.Max(_face[FaceShapes.MouthSmile], smile * env);
            }
            _face.AsSpan().CopyTo(outWeights);
        }
    }

    /// <summary>제스처 로컬 가산 회전(24 · 항등 기본).</summary>
    public void Gesture(long nowMicros, Span<Quaternion> add)
    {
        lock (_gate)
        {
            for (var j = 0; j < MocapSkeleton.Body; j++) add[j] = Quaternion.Identity;
            var (v, a) = Mood;
            var t = nowMicros / 1e6f;
            const float D = MathF.PI / 180f;
            var nod = IsSpeaking(nowMicros) ? 6f * a * MathF.Sin(t * MathF.Tau * SyllablesPerSecond * 0.25f) : 0f;
            var sad = MathF.Max(0, -v) * (1 - a);
            var anger = MathF.Max(0, -v) * a;
            var surprise = a * (1 - MathF.Abs(v)) * (a > 0.5f ? 1 : 0);
            add[MocapSkeleton.Neck] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (nod + 14f * sad) * D) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 4f * MathF.Max(0, v) * MathF.Sin(t * 0.8f) * D);
            add[MocapSkeleton.Spine2] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (6f * anger + 4f * sad) * D);
            add[MocapSkeleton.LCollar] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 10f * surprise * D);
            add[MocapSkeleton.RCollar] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -10f * surprise * D);
        }
    }

    // ══════════════════════ 물리 가드(무할당) ══════════════════════

    /// <summary>2-본 해석 IK: 뼈 길이 보존 · 무릎/팔꿈치 극 방향 유지. 도달 불가면 최대 신장.</summary>
    public static void TwoBoneIk(Vector3 a, ref Vector3 b, ref Vector3 c, Vector3 target)
    {
        var l1 = Vector3.Distance(a, b); var l2 = Vector3.Distance(b, c);
        var at = target - a; var dl = at.Length();
        if (dl < 1e-6f || l1 < 1e-6f || l2 < 1e-6f) return;
        var dir = at / dl;
        var d = Math.Clamp(dl, MathF.Abs(l1 - l2) + 1e-4f, l1 + l2 - 1e-4f);
        var pole = b - a - dir * Vector3.Dot(b - a, dir);
        if (pole.LengthSquared() < 1e-10f)
        {
            pole = Vector3.Cross(dir, Vector3.UnitX);
            if (pole.LengthSquared() < 1e-6f) pole = Vector3.Cross(dir, Vector3.UnitZ);
        }
        pole = Vector3.Normalize(pole);
        var cosA = Math.Clamp((l1 * l1 + d * d - l2 * l2) / (2 * l1 * d), -1f, 1f);
        var sinA = MathF.Sqrt(1 - cosA * cosA);
        b = a + dir * (cosA * l1) + pole * (sinA * l1);
        c = a + dir * d;
    }

    private static float Horizontal(Vector3 a, Vector3 b) { var d = a - b; d.Y = 0; return d.Length(); }

    /// <summary>24관절 포즈에 물리 제약 적용(제자리 수정). dt = 프레임 간격(s).</summary>
    public void Apply(Span<Vector3> p, float dt)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Mocap.Guard");
            dt = MathF.Max(dt, 1e-4f);
            // 1) 루트 관성
            var root = p[MocapSkeleton.Pelvis];
            if (Inertia && _hasPrev)
            {
                var vel = (root - _prevRoot) / dt;
                var dv = vel - _rootVel;
                var max = MaxRootAccel * _scale * dt;
                if (dv.Length() > max)
                {
                    vel = _rootVel + dv * (max / dv.Length());
                    var shift = _prevRoot + vel * dt - root;
                    for (var j = 0; j < MocapSkeleton.Body; j++) p[j] += shift;
                    root += shift;
                    _rootClamps++;
                }
                _rootVel = vel;
            }
            else if (_hasPrev) _rootVel = (root - _prevRoot) / dt;
            _prevRoot = root;

            // 2) 발 접촉 · 고정 · 지면
            float penB = 0, penA = 0, slideB = 0, slideA = 0;
            var anyLocked = false;
            var ankleH = AnkleHeight;
            for (var s = 0; s < 2; s++)
            {
                int hip = s == 0 ? MocapSkeleton.LHip : MocapSkeleton.RHip, knee = s == 0 ? MocapSkeleton.LKnee : MocapSkeleton.RKnee,
                    ankle = s == 0 ? MocapSkeleton.LAnkle : MocapSkeleton.RAnkle, toe = s == 0 ? MocapSkeleton.LFoot : MocapSkeleton.RFoot;
                var rawToe = p[toe]; var rawAnkle = p[ankle];
                var speed = _hasPrev ? Horizontal(rawToe, _prevToeRaw[s]) / dt : 0f;
                var height = MathF.Min(rawAnkle.Y - Ground - ankleH, rawToe.Y - Ground);
                penB = MathF.Max(penB, MathF.Max(Ground - rawToe.Y, Ground + ankleH - rawAnkle.Y));
                if (_locked[s] && _hasPrev) { slideB = MathF.Max(slideB, Horizontal(rawToe, _prevToeRaw[s])); anyLocked = true; }
                if (!_locked[s] && height < 0.05f * _scale && speed < 0.35f * _scale)
                {
                    _locked[s] = true;
                    _lockAnkle[s] = new Vector3(rawAnkle.X, MathF.Max(rawAnkle.Y, Ground + ankleH), rawAnkle.Z);
                }
                else if (_locked[s] && (height > 0.09f * _scale || speed > 0.8f * _scale))
                {
                    _locked[s] = false;
                    _relOffset[s] = _lockAnkle[s] - rawAnkle; _relBlend[s] = 1f;
                }
                var target = rawAnkle;
                if (FootLock && _locked[s]) target = _lockAnkle[s];
                else if (_relBlend[s] > 0) { target = rawAnkle + _relOffset[s] * _relBlend[s]; _relBlend[s] = MathF.Max(0, _relBlend[s] - 1f / Math.Max(1, BlendFrames)); }
                if (GroundClamp) target.Y = MathF.Max(target.Y, Ground + ankleH);
                if (Vector3.DistanceSquared(target, rawAnkle) > 1e-12f)
                {
                    var footLen = Vector3.Distance(rawToe, rawAnkle);
                    var kn = p[knee]; var an = p[ankle];
                    TwoBoneIk(p[hip], ref kn, ref an, target);
                    p[knee] = kn; p[ankle] = an;
                    p[toe] = rawToe + (an - rawAnkle);
                    if (GroundClamp && p[toe].Y < Ground && footLen > 1e-4f)
                    {
                        var t = p[toe]; t.Y = Ground;
                        p[toe] = an + Vector3.Normalize(t - an) * footLen;
                    }
                }
                else if (GroundClamp && p[toe].Y < Ground)
                {
                    var footLen = Vector3.Distance(rawToe, rawAnkle);
                    var t = p[toe]; t.Y = Ground;
                    p[toe] = p[ankle] + Vector3.Normalize(t - p[ankle]) * footLen;
                }
                if (_locked[s] && _hasPrev) slideA = MathF.Max(slideA, Horizontal(p[toe], _prevToeOut[s]));
                penA = MathF.Max(penA, MathF.Max(Ground - p[toe].Y, Ground + ankleH - p[ankle].Y - 1e-4f));
                _prevToeRaw[s] = rawToe; _prevToeOut[s] = p[toe];
            }
            _penBefore = penB; _penAfter = MathF.Max(0, penA);
            if (anyLocked) { _slideBefore += (slideB - _slideBefore) * 0.1f; _slideAfter += (slideA - _slideAfter) * 0.1f; }

            // 3) 몸통 캡슐 자기 충돌(손목/손)
            if (SelfCollision)
            {
                var ca = p[MocapSkeleton.Pelvis]; var cb = p[MocapSkeleton.Neck];
                var r = TorsoRadius;
                var fwd = Vector3.Cross(p[MocapSkeleton.LHip] - p[MocapSkeleton.RHip], cb - ca);
                fwd = fwd.LengthSquared() > 1e-8f ? Vector3.Normalize(fwd) : Vector3.UnitZ;
                for (var s = 0; s < 2; s++)
                {
                    int sh = s == 0 ? MocapSkeleton.LShoulder : MocapSkeleton.RShoulder, el = s == 0 ? MocapSkeleton.LElbow : MocapSkeleton.RElbow,
                        wr = s == 0 ? MocapSkeleton.LWrist : MocapSkeleton.RWrist, hd = s == 0 ? MocapSkeleton.LHand : MocapSkeleton.RHand;
                    var push = Vector3.Zero;
                    for (var m = 0; m < 2; m++)
                    {
                        var q = p[m == 0 ? wr : hd];
                        var ab = cb - ca; var t = Math.Clamp(Vector3.Dot(q - ca, ab) / MathF.Max(ab.LengthSquared(), 1e-8f), 0f, 1f);
                        var cp = ca + ab * t;
                        var d = q - cp; var dl = d.Length();
                        if (dl >= r) continue;
                        var n = dl > 1e-5f ? d / dl : fwd;
                        var need = cp + n * r - q;
                        if (need.LengthSquared() > push.LengthSquared()) push = need;
                    }
                    if (push.LengthSquared() <= 1e-12f) continue;
                    var oldWrist = p[wr];
                    var e = p[el]; var w = p[wr];
                    TwoBoneIk(p[sh], ref e, ref w, oldWrist + push);
                    p[el] = e; p[wr] = w;
                    p[hd] += w - oldWrist;
                    _collisions++;
                }
            }
            _hasPrev = true;
            _frames++;
        }
    }

    public void ResetPhysics()
    {
        lock (_gate) { _hasPrev = false; _locked[0] = _locked[1] = false; _relBlend[0] = _relBlend[1] = 0; _slideBefore = _slideAfter = 0; }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Mocap.Guard.OnChange", ex); }
    }
}
