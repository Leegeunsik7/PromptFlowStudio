namespace PromptFlowStudio.Services.LiveClipper;

public enum AvatarEmotionTag : byte { Neutral, Happy, Surprise, Excited, Sad }

/// <summary>v17.84.0 1 초 버킷 채점 결과(4 신호 + 복합).</summary>
public struct SpikeSample
{
    public long AtUs;
    public float Chat, Donation, Audio, Emotion, Score, ChatRatio, RmsDb, HfRatio;
    public int ChatCount;
    public long DonationKrw;
}

/// <summary>v17.84.0 판정된 바이럴 클립 구간(스파이크 −20 s ~ +15 s).</summary>
public struct ClimaxClip
{
    public long Id, SpikeUs, StartUs, EndUs;
    public float Score, Chat, Donation, Audio, Emotion;
    public bool Manual;
}

/// <summary>
/// [PromptFlow Studio v17.84.0] 📈 멀티모달 클라이맥스 스파이크 감지기 — 1 초 슬라이딩 버킷 4 신호 동시 채점(무할당).
///   ① 채팅 가속도: 직전 30 s 평균 대비 유속 비(300 % = 1.0) · ② 슈퍼챗/도네이션 가중(₩50,000 = 1.0) ·
///   ③ 음향 RMS 에너지 비 + 2.4 kHz 1 차 HPF 고역(웃음/환호) 비 · ④ 아바타 감정 급변(Surprise/Excited 진입).
///   ▸ 복합 = 0.35·채팅 + 0.30·음향 + 0.20·후원 + 0.15·감정 (+3 신호 이상 ≥ 0.5 동시 발화 시 +0.10) ≥ <see cref="Threshold"/> →
///     직전 <see cref="PreSec"/> s + 직후 <see cref="PostSec"/> s 클립 판정 · 후속 <see cref="RefractorySec"/> s 중복 억제.
/// </summary>
public sealed class MultimodalClimaxSpikeDetector
{
    public const float Threshold = 0.85f, VelocityBoom = 3f, DonationFullKrw = 50_000f, HpCutHz = 2_400f;
    public const float WChat = 0.35f, WAudio = 0.30f, WDonation = 0.20f, WEmotion = 0.15f, Synergy = 0.10f;
    public const int PreSec = 20, PostSec = 15, ClipSec = PreSec + PostSec, BaselineSec = 30, WarmupSec = 5, RefractorySec = 20, Hist = 120, ClipCap = 32;
    public const long BucketUs = 1_000_000;

    private readonly object _gate = new();
    private readonly int[] _chatHist = new int[BaselineSec];
    private readonly float[] _rmsHist = new float[BaselineSec];
    private readonly SpikeSample[] _hist = new SpikeSample[Hist];
    private readonly ClimaxClip[] _clips = new ClimaxClip[ClipCap];
    private int _baseHead, _baseN, _histHead, _histN, _clipN;
    private long _bucketStart = -1, _lastTriggerUs = long.MinValue / 2, _nextId = 1;
    // 현재 버킷 누적
    private int _chat;
    private long _krw;
    private double _sumSq, _hpSumSq;
    private long _samples;
    private float _hp, _xPrev;
    private AvatarEmotionTag _emo;
    private bool _emoJump;

    public bool Enabled { get; set; } = true;
    public SpikeSample Last { get; private set; }
    public long Buckets { get; private set; }
    public long Triggers { get; private set; }
    public long Suppressed { get; private set; }
    public float PeakScore { get; private set; }
    public AvatarEmotionTag Emotion => _emo;
    public int HistCount => _histN;
    public SpikeSample HistAt(int i) { lock (_gate) return _hist[(_histHead - _histN + i + Hist) % Hist]; }
    public int PendingCount { get { lock (_gate) return _clipN; } }

    public void Reset()
    {
        lock (_gate)
        {
            _baseHead = _baseN = _histHead = _histN = _clipN = 0;
            _bucketStart = -1; _lastTriggerUs = long.MinValue / 2;
            _chat = 0; _krw = 0; _sumSq = _hpSumSq = 0; _samples = 0; _hp = _xPrev = 0;
            _emo = AvatarEmotionTag.Neutral; _emoJump = false;
            Last = default; Buckets = Triggers = Suppressed = 0; PeakScore = 0;
        }
    }

    // ══════════════════════ 신호 입력(무할당 핫패스) ══════════════════════

    public void OnChat(int count = 1) { if (count > 0) lock (_gate) _chat += count; }

    public void OnDonation(long krw) { if (krw > 0) lock (_gate) _krw += krw; }

    public void OnEmotion(AvatarEmotionTag tag)
    {
        lock (_gate)
        {
            if (tag == _emo) return;
            if (tag is AvatarEmotionTag.Surprise or AvatarEmotionTag.Excited && _emo is not (AvatarEmotionTag.Surprise or AvatarEmotionTag.Excited)) _emoJump = true;
            _emo = tag;
        }
    }

    /// <summary>모노 PCM 블록(−1..1) — RMS · 1 차 HPF 고역 에너지 누적.</summary>
    public void OnAudio(ReadOnlySpan<float> pcm, int sampleRate)
    {
        if (pcm.IsEmpty || sampleRate <= 0) return;
        var rc = 1f / (2f * MathF.PI * HpCutHz);
        var a = rc / (rc + 1f / sampleRate);
        lock (_gate)
        {
            double s = 0, h = 0;
            var hp = _hp; var xp = _xPrev;
            for (var i = 0; i < pcm.Length; i++)
            {
                var x = pcm[i];
                hp = a * (hp + x - xp); xp = x;
                s += x * x; h += hp * hp;
            }
            _hp = hp; _xPrev = xp;
            _sumSq += s; _hpSumSq += h; _samples += pcm.Length;
        }
    }

    /// <summary>시각 전진 — 1 s 경계마다 버킷 확정 · 채점 · 판정. 반환 = 이번 호출에서 새로 판정된 클립 수.</summary>
    public int Advance(long nowUs)
    {
        lock (_gate)
        {
            if (_bucketStart < 0) { _bucketStart = nowUs; return 0; }
            var made = 0;
            while (nowUs - _bucketStart >= BucketUs)
            {
                _bucketStart += BucketUs;
                if (Close(_bucketStart)) made++;
            }
            return made;
        }
    }

    private bool Close(long atUs)
    {
        Buckets++;
        var s = new SpikeSample { AtUs = atUs, ChatCount = _chat, DonationKrw = _krw };

        // ① 채팅 가속도
        float baseChat = 0, baseRms = 0;
        for (var i = 0; i < _baseN; i++) { baseChat += _chatHist[i]; baseRms += _rmsHist[i]; }
        if (_baseN > 0) { baseChat /= _baseN; baseRms /= _baseN; }
        s.ChatRatio = _chat / MathF.Max(baseChat, 1f);
        s.Chat = _baseN >= WarmupSec ? Math.Clamp((s.ChatRatio - 1f) / (VelocityBoom - 1f), 0f, 1f) : 0f;

        // ② 후원
        s.Donation = _krw <= 0 ? 0f : 0.6f + 0.4f * Math.Clamp(_krw / DonationFullKrw, 0f, 1f);

        // ③ 음향
        var rms = _samples > 0 ? (float)Math.Sqrt(_sumSq / _samples) : 0f;
        s.RmsDb = rms > 1e-6f ? 20f * MathF.Log10(rms) : -120f;
        s.HfRatio = _sumSq > 1e-12 ? (float)(_hpSumSq / _sumSq) : 0f;
        var energy = _baseN >= WarmupSec ? Math.Clamp((rms / MathF.Max(baseRms, 1e-4f) - 1f) / 2f, 0f, 1f) : 0f;
        var hf = Math.Clamp((s.HfRatio - 0.25f) / 0.35f, 0f, 1f);
        s.Audio = Math.Clamp(0.6f * energy + 0.4f * hf * MathF.Min(1f, energy * 2f), 0f, 1f);

        // ④ 감정 급변(진입 = 1.0 · 유지 = 0.4)
        s.Emotion = _emoJump ? 1f : _emo is AvatarEmotionTag.Surprise or AvatarEmotionTag.Excited ? 0.4f : 0f;

        var score = WChat * s.Chat + WAudio * s.Audio + WDonation * s.Donation + WEmotion * s.Emotion;
        var hot = (s.Chat >= 0.5f ? 1 : 0) + (s.Audio >= 0.5f ? 1 : 0) + (s.Donation >= 0.5f ? 1 : 0) + (s.Emotion >= 0.5f ? 1 : 0);
        if (hot >= 3) score += Synergy;
        s.Score = Enabled ? Math.Clamp(score, 0f, 1f) : 0f;
        if (s.Score > PeakScore) PeakScore = s.Score;

        // 기준선 갱신(스파이크 버킷은 기준선 오염 방지 위해 제외)
        if (s.Score < 0.5f)
        {
            _chatHist[_baseHead] = _chat; _rmsHist[_baseHead] = rms;
            _baseHead = (_baseHead + 1) % BaselineSec; if (_baseN < BaselineSec) _baseN++;
        }
        _hist[_histHead] = s; _histHead = (_histHead + 1) % Hist; if (_histN < Hist) _histN++;
        Last = s;
        _chat = 0; _krw = 0; _sumSq = _hpSumSq = 0; _samples = 0; _emoJump = false;

        if (s.Score < Threshold) return false;
        if (atUs - _lastTriggerUs < RefractorySec * BucketUs) { Suppressed++; return false; }
        _lastTriggerUs = atUs;
        Triggers++;
        var spike = atUs - BucketUs / 2;
        return Push(new ClimaxClip
        {
            SpikeUs = spike, StartUs = spike - PreSec * BucketUs, EndUs = spike + PostSec * BucketUs,
            Score = s.Score, Chat = s.Chat, Donation = s.Donation, Audio = s.Audio, Emotion = s.Emotion,
        });
    }

    /// <summary>수동 펀치 인/아웃 구간 등록(감지기와 동일 대기열).</summary>
    public bool AddManual(long startUs, long endUs)
    {
        if (endUs <= startUs) return false;
        lock (_gate) return Push(new ClimaxClip { SpikeUs = endUs, StartUs = startUs, EndUs = endUs, Score = 1f, Manual = true });
    }

    private bool Push(ClimaxClip c)
    {
        if (_clipN == ClipCap) { Suppressed++; return false; }
        c.Id = _nextId++;
        _clips[_clipN++] = c;
        return true;
    }

    /// <summary>EndUs ≤ now 인(직후 15 s 녹화 완료) 가장 오래된 클립 인출.</summary>
    public bool TryDequeueReady(long nowUs, out ClimaxClip clip)
    {
        lock (_gate)
        {
            for (var i = 0; i < _clipN; i++)
            {
                if (_clips[i].EndUs > nowUs) continue;
                clip = _clips[i];
                for (var j = i + 1; j < _clipN; j++) _clips[j - 1] = _clips[j];
                _clipN--;
                return true;
            }
            clip = default;
            return false;
        }
    }

    public ClimaxClip Pending(int i) { lock (_gate) return _clips[i]; }

    public static string EmotionName(AvatarEmotionTag t) => t switch
    {
        AvatarEmotionTag.Happy => "기쁨", AvatarEmotionTag.Surprise => "놀람", AvatarEmotionTag.Excited => "흥분", AvatarEmotionTag.Sad => "슬픔", _ => "평온",
    };

    public static string EmotionIcon(AvatarEmotionTag t) => t switch
    {
        AvatarEmotionTag.Happy => "😊", AvatarEmotionTag.Surprise => "😲", AvatarEmotionTag.Excited => "🤩", AvatarEmotionTag.Sad => "😢", _ => "🙂",
    };
}
