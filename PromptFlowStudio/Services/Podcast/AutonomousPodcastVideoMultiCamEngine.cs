using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.Podcast;

public enum PodcastShot : byte { Wide, Single, TwoShot, Reaction }

public enum HighlightReason : byte { HeatedExchange, PeakEnergy, Intervention, Laughter }

/// <summary>카메라 컷(시각 · 샷 · 주 피사체 A/B).</summary>
public readonly record struct CamCut(double TimeMs, PodcastShot Shot, int A, int B);

/// <summary>하이라이트 클립 구간(시작/끝 · 사유 · 화자 · 점수).</summary>
public readonly record struct HighlightClip(double StartMs, double EndMs, HighlightReason Reason, int Speaker, float Score);

/// <summary>
/// [PromptFlow Studio v16.49.0] 🎥 AI 팟캐스트 비디오 자율 스위칭 &amp; 멀티캠 편집기.
///   ▸ 액티브 스피커 탐지: 좌석별 PCM 블록 RMS(Vector&lt;float&gt; SIMD) · 영교차율(음성 대역 판정) · 적응형 노이즈 플로어(무음 구간만 상승) · 어택/릴리즈 레벨 · 행오버 · 온셋 디바운스(짧은 추임새 무시).
///   ▸ 자동 스위칭: 1인 → 단독 샷 · 2인 동시(크로스토크) → 2인 대립 샷 · 3인+/무음 지속 → 와이드 풀샷 · 장기 독백 → 리액션 샷 후 복귀 · 화자 전환/와이드 홀드 분리.
///   ▸ 하이라이트: 단독 샷 화자 교대 10초 내 4회+ = 설전 · 피크 에너지 · 외부 마킹(시청자 개입 · 웃음) · 상한 링.
///   ▸ 비디오 레이어링: 스튜디오 그라디언트 · 좌석 아바타(발화 글로우 링 · 레벨 연동 입 모양) · 로어서드 바 — Analyze/Direct/Render 무할당.
/// </summary>
public sealed class AutonomousPodcastVideoMultiCamEngine
{
    public const int MaxSeats = MultiAgentTalkshowConversationOrchestrator.MaxAgents, SampleRate = 16000, CutCap = 128, ClipCap = 32, SwitchRing = 16;
    public const float VoiceRatio = 2.8f, MinRms = 0.008f, MinZcr = 0.004f, MaxZcr = 0.30f;

    private readonly object _gate = new();
    private readonly float[] _level = new float[MaxSeats], _floor = new float[MaxSeats], _zcr = new float[MaxSeats];
    private readonly bool[] _voice = new bool[MaxSeats];
    private readonly double[] _lastVoice = new double[MaxSeats], _voiceSince = new double[MaxSeats];
    private readonly uint[] _colors = new uint[MaxSeats];
    private readonly CamCut[] _cuts = new CamCut[CutCap];
    private readonly double[] _switches = new double[SwitchRing];
    private readonly List<HighlightClip> _clips = new(ClipCap + 1);
    private int _seats, _a = -1, _b = -1, _cutHead, _cutCount, _swHead, _prevSpeaker = -1, _lastSingle = -1, _reactionReturn = -1;
    private PodcastShot _shot = PodcastShot.Wide;
    private double _lastCutMs = double.NegativeInfinity, _lastAnyVoice = double.NegativeInfinity, _singleSince, _lastClipEnd = double.NegativeInfinity;
    private long _cutsTotal, _blocks;

    public AutonomousPodcastVideoMultiCamEngine() => SetSeats(stackalloc uint[] { 0xA855F7, 0x0EA5E9, 0xEF4444, 0xF59E0B });

    public event Action? OnChange;
    public event Action<CamCut>? OnCut;

    public bool AutoSwitch { get; set; } = true;
    public double MinHoldMs { get; set; } = 1800;
    public double SpeakerChangeHoldMs { get; set; } = 700;
    public double SilenceWideMs { get; set; } = 1500;
    public double HangoverMs { get; set; } = 300;
    public double OnsetMs { get; set; } = 200;
    public double LiveWindowMs { get; set; } = 120;
    public double MonologueMs { get; set; } = 12000;
    public double ReactionMs { get; set; } = 1800;
    public float PeakLevel { get; set; } = 0.18f;
    public int HeatedSwitches { get; set; } = 4;
    public double HeatedWindowMs { get; set; } = 10000;
    public int Seats => _seats;
    public PodcastShot Shot => _shot;
    public int SubjectA => _a;
    public int SubjectB => _b;
    public long CutsTotal => Interlocked.Read(ref _cutsTotal);
    public long Blocks => Interlocked.Read(ref _blocks);
    public float Level(int seat) => _level[seat];
    public float NoiseFloor(int seat) => _floor[seat];
    public bool IsVoice(int seat) => _voice[seat];
    public int ClipCount { get { lock (_gate) return _clips.Count; } }
    public IReadOnlyList<HighlightClip> Clips { get { lock (_gate) return _clips.ToArray(); } }
    public string? LastAction { get; private set; }

    public void SetSeats(ReadOnlySpan<uint> colors)
    {
        if (colors.Length is < 1 or > MaxSeats) throw new ArgumentException("좌석은 1~5개여야 합니다.", nameof(colors));
        lock (_gate)
        {
            _seats = colors.Length;
            colors.CopyTo(_colors);
            Reset();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_level); Array.Clear(_zcr); Array.Clear(_voice); Array.Clear(_voiceSince);
            Array.Fill(_floor, 0.002f); Array.Fill(_lastVoice, double.NegativeInfinity);
            _shot = PodcastShot.Wide; _a = _b = -1; _prevSpeaker = -1; _lastSingle = -1; _reactionReturn = -1;
            _lastCutMs = double.NegativeInfinity; _lastAnyVoice = double.NegativeInfinity; _lastClipEnd = double.NegativeInfinity;
            _cutHead = _cutCount = _swHead = 0; Array.Fill(_switches, double.NegativeInfinity);
            _clips.Clear();
        }
    }

    // ══════════════════════ 액티브 스피커 탐지 ══════════════════════

    /// <summary>RMS(Vector&lt;float&gt; 제곱합 + 스칼라 꼬리). 무할당.</summary>
    public static float Rms(ReadOnlySpan<float> pcm)
    {
        if (pcm.IsEmpty) return 0;
        var i = 0; var sum = 0f;
        if (Vector.IsHardwareAccelerated && pcm.Length >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            var vs = MemoryMarshal.Cast<float, Vector<float>>(pcm);
            foreach (var v in vs) acc += v * v;
            sum = Vector.Dot(acc, Vector<float>.One);
            i = vs.Length * Vector<float>.Count;
        }
        for (; i < pcm.Length; i++) sum += pcm[i] * pcm[i];
        return MathF.Sqrt(sum / pcm.Length);
    }

    public static float RmsScalar(ReadOnlySpan<float> pcm)
    {
        if (pcm.IsEmpty) return 0;
        double sum = 0;
        foreach (var s in pcm) sum += s * s;
        return (float)Math.Sqrt(sum / pcm.Length);
    }

    /// <summary>영교차율(부호 변화 / 샘플). 무할당.</summary>
    public static float ZeroCrossingRate(ReadOnlySpan<float> pcm)
    {
        if (pcm.Length < 2) return 0;
        var n = 0;
        for (var i = 1; i < pcm.Length; i++) if (pcm[i - 1] >= 0 != pcm[i] >= 0) n++;
        return n / (float)(pcm.Length - 1);
    }

    /// <summary>좌석 1블록 분석 → 평활 레벨. 음성 = RMS ≥ max(MinRms, 플로어×비) ∧ 영교차율 음성 대역. 무할당.</summary>
    public float Analyze(int seat, ReadOnlySpan<float> pcm, double nowMs)
    {
        if ((uint)seat >= (uint)_seats) return 0;
        var rms = Rms(pcm);
        var zcr = ZeroCrossingRate(pcm);
        var floor = _floor[seat];
        var cand = rms >= MinRms && rms >= floor * VoiceRatio && zcr >= MinZcr && zcr <= MaxZcr;
        if (rms < floor) floor += (rms - floor) * 0.2f;
        else if (!cand) floor += (rms - floor) * 0.02f;
        _floor[seat] = Math.Max(floor, 1e-5f);
        var lv = _level[seat];
        lv += (rms - lv) * (rms > lv ? 0.6f : 0.15f);
        _level[seat] = lv; _zcr[seat] = zcr;
        if (cand)
        {
            if (!_voice[seat]) { _voice[seat] = true; _voiceSince[seat] = nowMs; }
            _lastVoice[seat] = nowMs; _lastAnyVoice = nowMs;
            if (lv >= PeakLevel && nowMs - _lastClipEnd > 5000) AddClip(new HighlightClip(nowMs - 4000, nowMs + 3000, HighlightReason.PeakEnergy, seat, lv));
        }
        else if (_voice[seat] && nowMs - _lastVoice[seat] > HangoverMs) _voice[seat] = false;
        Interlocked.Increment(ref _blocks);
        return lv;
    }

    /// <summary>자동 디렉팅 1회: 원하는 샷 계산 → 홀드 경과 시 컷. 반환 = 컷 발생. 무할당.</summary>
    public bool Direct(double nowMs)
    {
        if (!AutoSwitch) return false;
        int n = 0, hang = 0, s1 = -1, s2 = -1;
        for (var i = 0; i < _seats; i++)
        {
            if (!_voice[i] || _lastVoice[i] - _voiceSince[i] < OnsetMs) continue;
            if (nowMs - _lastVoice[i] > LiveWindowMs) { hang++; continue; }   // 행오버 중(숨 고르기) — 샷 유지용, 동시 발화로 세지 않음
            n++;
            if (s1 < 0 || _level[i] > _level[s1]) { s2 = s1; s1 = i; }
            else if (s2 < 0 || _level[i] > _level[s2]) s2 = i;
        }
        PodcastShot want; int a = -1, b = -1, ret = -1;
        if (n == 0)
        {
            if (hang > 0) return false;
            if (_shot == PodcastShot.Reaction && nowMs - _lastCutMs >= ReactionMs) { want = PodcastShot.Wide; }
            else if (nowMs - _lastAnyVoice < SilenceWideMs) return false;
            else want = PodcastShot.Wide;
        }
        else if (n == 1)
        {
            if (_shot == PodcastShot.Reaction)
            {
                if (s1 != _reactionReturn) { want = PodcastShot.Single; a = s1; }
                else if (nowMs - _lastCutMs < ReactionMs) return false;
                else { want = PodcastShot.Single; a = s1; }
            }
            else if (_shot == PodcastShot.Single && _a == s1)
            {
                if (nowMs - _singleSince < MonologueMs || _prevSpeaker < 0 || _prevSpeaker == s1) return false;
                want = PodcastShot.Reaction; a = _prevSpeaker; ret = s1;
            }
            else { want = PodcastShot.Single; a = s1; }
        }
        else if (n == 2) { want = PodcastShot.TwoShot; a = Math.Min(s1, s2); b = Math.Max(s1, s2); }
        else want = PodcastShot.Wide;
        if (want == _shot && a == _a && b == _b) return false;
        var hold = want is PodcastShot.Wide or PodcastShot.Reaction ? MinHoldMs : SpeakerChangeHoldMs;
        if (nowMs - _lastCutMs < hold) return false;
        Cut(nowMs, want, a, b);
        if (want == PodcastShot.Reaction) _reactionReturn = ret;
        return true;
    }

    /// <summary>수동/자동 컷 기록(링 버퍼 · 화자 교대 → 설전 하이라이트).</summary>
    public void Cut(double nowMs, PodcastShot shot, int a, int b)
    {
        var c = new CamCut(nowMs, shot, a, b);
        lock (_gate)
        {
            _shot = shot; _a = a; _b = b; _lastCutMs = nowMs;
            var switched = false;
            _cuts[_cutHead] = c; _cutHead = (_cutHead + 1) % CutCap; if (_cutCount < CutCap) _cutCount++;
            if (shot == PodcastShot.Single)
            {
                _singleSince = nowMs;
                if (_lastSingle >= 0 && _lastSingle != a) { _prevSpeaker = _lastSingle; switched = true; }
                _lastSingle = a;
                _reactionReturn = -1;
            }
            if (shot == PodcastShot.Single && switched)
            {
                _switches[_swHead] = nowMs; _swHead = (_swHead + 1) % SwitchRing;
                var k = 0;
                foreach (var t in _switches) if (nowMs - t <= HeatedWindowMs) k++;
                if (k >= HeatedSwitches && nowMs > _lastClipEnd)
                {
                    Array.Fill(_switches, double.NegativeInfinity);
                    AddClip(new HighlightClip(nowMs - HeatedWindowMs, nowMs + 2000, HighlightReason.HeatedExchange, a, k / (float)HeatedSwitches));
                }
            }
        }
        Interlocked.Increment(ref _cutsTotal);
        try { OnCut?.Invoke(c); } catch (Exception ex) { PromptFlowStudio.Program.Log("MultiCam.OnCut", ex); }
        Raise();
    }

    /// <summary>최근 컷(최신 우선) 복사. 반환 = 개수.</summary>
    public int CopyCuts(Span<CamCut> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, _cutCount);
            for (var i = 0; i < n; i++) dst[i] = _cuts[(_cutHead - 1 - i + CutCap) % CutCap];
            return n;
        }
    }

    /// <summary>외부 하이라이트 마킹(시청자 개입 · 웃음).</summary>
    public void MarkHighlight(HighlightReason reason, int speaker, double nowMs, double preMs = 3000, double postMs = 6000)
        => AddClip(new HighlightClip(nowMs - preMs, nowMs + postMs, reason, speaker, 1));

    private void AddClip(HighlightClip c)
    {
        lock (_gate)
        {
            _clips.Add(c);
            if (_clips.Count > ClipCap) _clips.RemoveAt(0);
            if (c.EndMs > _lastClipEnd) _lastClipEnd = c.EndMs;
        }
        LastAction = "✂ 하이라이트 · " + ReasonLabel(c.Reason);
        Raise();
    }

    // ══════════════════════ 비디오 레이어링 ══════════════════════

    /// <summary>현재 샷 합성(배경 · 아바타 · 글로우 · 로어서드). 무할당.</summary>
    public void Render(Span<byte> rgba, int w, int h, int stride, double nowMs)
    {
        for (var y = 0; y < h; y++)
        {
            var t = y / (float)h;
            uint c = t > 0.78f ? Pack(38, 30, 52) : Pack((byte)(18 + 30 * t), (byte)(16 + 14 * t), (byte)(44 + 50 * t));
            MemoryMarshal.Cast<byte, uint>(rgba.Slice(y * stride, w * 4)).Fill(c);
        }
        var shot = _shot; int a = _a, b = _b;
        switch (shot)
        {
            case PodcastShot.Single when (uint)a < (uint)_seats:
                DrawAvatar(rgba, w, h, stride, w / 2, (int)(h * 0.56f), (int)(h * 0.36f), a);
                LowerThird(rgba, w, h, stride, a);
                break;
            case PodcastShot.Reaction when (uint)a < (uint)_seats:
                DrawAvatar(rgba, w, h, stride, (int)(w * 0.62f), (int)(h * 0.58f), (int)(h * 0.30f), a);
                break;
            case PodcastShot.TwoShot when (uint)a < (uint)_seats && (uint)b < (uint)_seats:
                DrawAvatar(rgba, w, h, stride, (int)(w * 0.28f), (int)(h * 0.58f), (int)(h * 0.27f), a);
                DrawAvatar(rgba, w, h, stride, (int)(w * 0.72f), (int)(h * 0.58f), (int)(h * 0.27f), b);
                FillRect(rgba, w, h, stride, w / 2 - 1, 0, 2, h, Pack(10, 10, 16));
                LowerThird(rgba, w, h, stride, _level[a] >= _level[b] ? a : b);
                break;
            default:
                var n = _seats;
                var r = (int)(Math.Min(h * 0.17f, w / (2.6f * n)));
                for (var k = 0; k < n; k++)
                {
                    var seat = WideOrder(k, n);
                    DrawAvatar(rgba, w, h, stride, (int)(w * (k + 1) / (float)(n + 1)), (int)(h * 0.6f), r, seat);
                }
                break;
        }
        if (((long)(nowMs / 500) & 1) == 0) FillCircle(rgba, w, h, stride, 14, 14, 5, Pack(239, 68, 68));
    }

    /// <summary>와이드 배치: MC(좌석 0)를 중앙에.</summary>
    public static int WideOrder(int k, int n)
    {
        var mid = (n - 1) / 2;
        if (k == mid) return 0;
        return k < mid ? k + 1 : k;
    }

    private void DrawAvatar(Span<byte> px, int w, int h, int stride, int cx, int cy, int r, int seat)
    {
        var col = _colors[seat];
        byte cr = (byte)(col >> 16), cg = (byte)(col >> 8), cb = (byte)col;
        var lv = Math.Clamp(_level[seat] * 3f, 0, 1);
        if (_voice[seat]) FillRing(px, w, h, stride, cx, cy, (int)(r * 1.02f), (int)(r * (1.08f + 0.08f * lv)), Pack((byte)Math.Min(255, cr + 60), (byte)Math.Min(255, cg + 60), (byte)Math.Min(255, cb + 60)));
        FillCircle(px, w, h, stride, cx, cy, r, Pack((byte)(cr / 3 + 20), (byte)(cg / 3 + 20), (byte)(cb / 3 + 26)));
        var body = Pack(cr, cg, cb);
        var bw = (int)(r * 0.62f);
        FillRect(px, w, h, stride, cx - bw, cy + (int)(r * 0.25f), bw * 2, (int)(r * 0.62f), body);
        FillCircle(px, w, h, stride, cx, cy + (int)(r * 0.25f), bw, body);
        var hr = (int)(r * 0.36f); var hy = cy - (int)(r * 0.28f);
        FillCircle(px, w, h, stride, cx, hy, hr, Pack(242, 205, 170));
        var eye = Math.Max(1, hr / 9);
        FillCircle(px, w, h, stride, cx - hr / 3, hy - hr / 6, eye, Pack(30, 24, 30));
        FillCircle(px, w, h, stride, cx + hr / 3, hy - hr / 6, eye, Pack(30, 24, 30));
        var mw = Math.Max(2, hr / 3); var mh = Math.Max(1, (int)(hr * (0.06f + 0.4f * (_voice[seat] ? lv : 0))));
        FillRect(px, w, h, stride, cx - mw / 2, hy + hr / 3, mw, mh, Pack(120, 30, 40));
    }

    private void LowerThird(Span<byte> px, int w, int h, int stride, int seat)
    {
        var col = _colors[seat];
        var y = (int)(h * 0.84f); var bh = Math.Max(4, h / 12);
        FillRect(px, w, h, stride, (int)(w * 0.05f), y, (int)(w * 0.42f), bh, Pack(15, 15, 22));
        FillRect(px, w, h, stride, (int)(w * 0.05f), y, Math.Max(3, w / 80), bh, Pack((byte)(col >> 16), (byte)(col >> 8), (byte)col));
        FillRect(px, w, h, stride, (int)(w * 0.08f), y + bh / 3, (int)(w * 0.42f * Math.Clamp(_level[seat] * 3f, 0.05f, 1f) * 0.8f), Math.Max(1, bh / 3), Pack((byte)(col >> 16), (byte)(col >> 8), (byte)col));
    }

    public static uint Pack(byte r, byte g, byte b) => (uint)(r | g << 8 | b << 16) | 0xFF000000u;

    private static void FillRect(Span<byte> px, int w, int h, int stride, int x, int y, int rw, int rh, uint c)
    {
        int x0 = Math.Max(0, x), x1 = Math.Min(w, x + rw), y0 = Math.Max(0, y), y1 = Math.Min(h, y + rh);
        if (x1 <= x0) return;
        for (var yy = y0; yy < y1; yy++) MemoryMarshal.Cast<byte, uint>(px.Slice(yy * stride + x0 * 4, (x1 - x0) * 4)).Fill(c);
    }

    private static void FillCircle(Span<byte> px, int w, int h, int stride, int cx, int cy, int r, uint c) => FillRing(px, w, h, stride, cx, cy, -1, r, c);

    /// <summary>링(r0 &lt; d ≤ r1) 채우기 — r0 &lt; 0 이면 원.</summary>
    private static void FillRing(Span<byte> px, int w, int h, int stride, int cx, int cy, int r0, int r1, uint c)
    {
        if (r1 <= 0) return;
        for (var dy = -r1; dy <= r1; dy++)
        {
            var y = cy + dy;
            if ((uint)y >= (uint)h) continue;
            var o = (int)MathF.Sqrt(r1 * r1 - dy * dy);
            var i = r0 > 0 && Math.Abs(dy) < r0 ? (int)MathF.Sqrt(r0 * r0 - dy * dy) : -1;
            if (i < 0) FillRect(px, w, h, stride, cx - o, y, o * 2 + 1, 1, c);
            else { FillRect(px, w, h, stride, cx - o, y, o - i, 1, c); FillRect(px, w, h, stride, cx + i + 1, y, o - i, 1, c); }
        }
    }

    public static string ShotLabel(PodcastShot s) => s switch { PodcastShot.Single => "단독 샷", PodcastShot.TwoShot => "2인 대립 샷", PodcastShot.Reaction => "리액션 샷", _ => "와이드 풀샷" };

    public static string ReasonLabel(HighlightReason r) => r switch { HighlightReason.HeatedExchange => "🔥 설전", HighlightReason.PeakEnergy => "⚡ 피크", HighlightReason.Intervention => "🙋 시청자", _ => "😂 웃음" };

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("MultiCam.OnChange", ex); } }
}
