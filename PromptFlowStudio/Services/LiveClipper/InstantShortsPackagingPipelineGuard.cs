using System.Diagnostics;
using PromptFlowStudio.Services.KineticBRoll;
using PromptFlowStudio.Services.LiveRestream;
using PromptFlowStudio.Services.SmartPublishing;
using PromptFlowStudio.Services.VisualHooks;

namespace PromptFlowStudio.Services.LiveClipper;

public enum ShortStatus : byte { Queued, Packaging, Ready, Dispatched, Expired, Failed }

/// <summary>v17.84.0 패키징된 9:16 숏폼(메타데이터 · 클립 바이트는 워커 소유 버퍼).</summary>
public struct PackagedShort
{
    public long Id, ClipId, SpikeUs, StartUs, EndUs, Bytes, YtAtUnix, IgAtUnix;
    public ulong Hash;
    public float Score, SubjectCx, ImpactPeak, PackMs, BounceScale;
    public int VideoFrames, AudioFrames, CropX, Words, Cues, Caption;
    public bool Manual, Decodable;
    public HookBox CaptionBox;
    public ReframeVerdict Verdict;
    public HormoziPreset Preset;
    public ShortStatus Status;
    public readonly float Seconds => (EndUs - StartUs) / 1e6f;
}

/// <summary>
/// [PromptFlow Studio v17.84.0] 📦 인스턴트 숏폼 패키징 파이프라인 가드 — 라이브 60 fps 핫패스와 완전 격리.
///   ▸ 핫패스 측 <see cref="TryEnqueue"/> = 락 1 회 구조체 복사 + 세마포어 신호(무할당 · 비차단 · 큐 포화 시 드롭).
///   ▸ 전용 BelowNormal 워커 스레드: 링 프레임 단위 짧은 락 복사(대형 memmove 로 송출 락 점유 금지) · 64 프레임마다 양보 →
///     디코더 시작 무결성(SPS/PPS → IDR) · v17.77 9:16 세이프존 리프레이밍(1080 px 크롭 + 자막 박스 클램프) ·
///     v17.78 호르모지 키네틱 자막(3 단어 창 · 1.15× 스프링 바운스) · v17.76 휘쉬/임팩트/팝 효과음 큐 합성 → Ready.
///   ▸ <see cref="Dispatch"/>: v17.76 쿼터 가드 최초 적합 시각 예약 → v17.79 업로더 / v17.81 매트릭스 배포 큐 원클릭 이관.
/// </summary>
public sealed class InstantShortsPackagingPipelineGuard : IDisposable
{
    public const int QueueCap = 16, ResultCap = 24, SrcW = 1920, SrcH = 1080, CropW = SrcH * 9 / 16, MixBlock = 4096;
    public const double EnqueueBudgetUs = 500;
    public static readonly string[] Captions =
    {
        "역대급 반응 터졌다 실화냐",
        "채팅창 완전 폭발한 순간",
        "후원 들어오자마자 리액션 미쳤다",
        "이 장면 놓치면 후회함",
    };

    private readonly object _gate = new();
    private readonly object _work = new();
    private readonly RollingCircularVideoBufferEngine _ring;
    private readonly (ClimaxClip Clip, ClipSlice Slice, float SubjectCx)[] _queue = new (ClimaxClip, ClipSlice, float)[QueueCap];
    private readonly PackagedShort[] _done = new PackagedShort[ResultCap];
    private byte[] _clip = new byte[64 * 1024];
    private readonly float[] _mix = new float[MixBlock];
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private Thread? _worker;
    private volatile bool _stop;
    private int _qHead, _qN, _doneHead, _doneN, _captionRot;
    private long _nextId = 1;

    public InstantShortsPackagingPipelineGuard(RollingCircularVideoBufferEngine ring) => _ring = ring;

    public ReelsUiSafeZoneReframingGuard SafeZone { get; } = new(ReelsPlatforms.All);
    public ProceduralViralSoundFxSynthesizer Sfx { get; } = new();
    public MultiChannelQuotaDistributionGuard Quota { get; } = new();
    public HormoziPreset Preset { get; set; } = HormoziPreset.HormoziYellow;
    public bool WorkerRunning => _worker is { IsAlive: true };
    public int Queued { get { lock (_gate) return _qN; } }
    public int ResultCount { get { lock (_gate) return _doneN; } }
    public long Enqueued { get; private set; }
    public long Dropped { get; private set; }
    public long Packaged { get; private set; }
    public long Expired { get; private set; }
    public long Failed { get; private set; }
    public long Dispatched { get; private set; }
    public long HotPathInterference { get; private set; }
    public double LastEnqueueUs { get; private set; }
    public double MaxEnqueueUs { get; private set; }
    public double LastPackMs { get; private set; }
    public ReadOnlySpan<byte> LastClipBytes => _clip.AsSpan(0, (int)Math.Min(_clip.Length, LastBytes));
    public long LastBytes { get; private set; }

    /// <summary>최신순 결과(0 = 가장 최근).</summary>
    public PackagedShort Result(int i) { lock (_gate) return _done[(_doneHead - 1 - i + ResultCap * 2) % ResultCap]; }

    // ══════════════════════ 핫패스 측(무할당 · 비차단) ══════════════════════

    public bool TryEnqueue(in ClimaxClip clip, in ClipSlice slice, float subjectCx)
    {
        var t0 = Stopwatch.GetTimestamp();
        bool ok;
        lock (_gate)
        {
            ok = _qN < QueueCap;
            if (ok)
            {
                _queue[(_qHead + _qN) % QueueCap] = (clip, slice, subjectCx);
                _qN++; Enqueued++;
            }
            else Dropped++;
        }
        if (ok && _worker is not null) _signal.Release();
        var us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        LastEnqueueUs = us; if (us > MaxEnqueueUs) MaxEnqueueUs = us;
        if (us > EnqueueBudgetUs) HotPathInterference++;
        return ok;
    }

    // ══════════════════════ 워커(저우선순위 백그라운드) ══════════════════════

    public void StartWorker()
    {
        if (WorkerRunning) return;
        _stop = false;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "PFS.ShortsPackager" };
        _worker.Start();
    }

    public void StopWorker()
    {
        var w = _worker;
        if (w is null) return;
        _stop = true;
        _signal.Release();
        w.Join(1000);
        _worker = null;
    }

    private void WorkerLoop()
    {
        while (!_stop)
        {
            try
            {
                _signal.Wait(250);
                while (!_stop && ProcessOne()) { }
            }
            catch (Exception ex) { PromptFlowStudio.Program.Log("LiveClipper.Packager", ex); }
        }
    }

    /// <summary>대기 작업 1 건 패키징(워커 · 수동 공용 — 작업 버퍼 직렬화). 반환 = 처리 여부.</summary>
    public bool ProcessOne()
    {
        lock (_work)
        {
            ClimaxClip clip; ClipSlice slice; float cx;
            lock (_gate)
            {
                if (_qN == 0) return false;
                (clip, slice, cx) = _queue[_qHead];
                _qHead = (_qHead + 1) % QueueCap; _qN--;
            }
            var t0 = Stopwatch.GetTimestamp();
            var r = new PackagedShort
            {
                ClipId = clip.Id, SpikeUs = clip.SpikeUs, StartUs = slice.StartPtsUs, EndUs = slice.EndPtsUs, Score = clip.Score,
                Manual = clip.Manual, SubjectCx = cx, VideoFrames = slice.VideoFrames, AudioFrames = slice.AudioFrames, Status = ShortStatus.Packaging,
            };

            // ① 링 → 작업 버퍼(프레임 단위 짧은 락)
            var n = slice.PrependParams ? _ring.CopyParams(_clip) : 0;
            var status = ShortStatus.Ready;
            if (slice.Bytes > _clip.Length)
            {
                var grown = new byte[slice.Bytes + slice.Bytes / 4];   // 워커 측 1 회 확장(핫패스 무관)
                _clip.AsSpan(0, n).CopyTo(grown);
                _clip = grown;
            }
            {
                for (var q = slice.FirstSeq; q <= slice.LastSeq; q++)
                {
                    var w = _ring.CopyFrame(q, _clip.AsSpan(n));
                    if (w < 0) { status = ShortStatus.Expired; break; }
                    n += w;
                    if (((q - slice.FirstSeq) & 63) == 63) Thread.Yield();
                }
            }
            r.Bytes = n;
            if (status == ShortStatus.Ready)
            {
                var bytes = _clip.AsSpan(0, n);
                r.Decodable = StartsDecodable(bytes);
                r.Hash = RtmpWire.Fnv(bytes);
                if (!r.Decodable) status = ShortStatus.Failed;
            }

            if (status == ShortStatus.Ready)
            {
                // ② v17.77 9:16 리프레이밍 — 피사체 중심 1080 × 1920 크롭 + 하단 호르모지 자막 박스 세이프존 클램프
                r.CropX = Math.Clamp((int)(Math.Clamp(cx, 0f, 1f) * SrcW) - CropW / 2, 0, SrcW - CropW);
                var box = HookBox.Centered(0.5f, 0.86f, 0.94f, 0.14f);
                r.Verdict = SafeZone.Clamp(ref box);
                r.CaptionBox = box;

                // ③ v17.78 호르모지 키네틱 자막 — 3 단어 창 · 스파이크 1 s 전부터 단어당 400 ms
                r.Caption = clip.Manual ? 3 : _captionRot++ % 3;
                r.Words = WordCount(Captions[r.Caption]);
                r.Preset = Preset;
                r.BounceScale = WordByWordKineticSubtitleEngine.ActiveScaleAt(60f);

                // ④ v17.76 효과음 — 휘쉬(선행 120 ms) · 임팩트(스파이크) · 단어 팝
                var spikeMs = Math.Max(0, (clip.SpikeUs - slice.StartPtsUs) / 1000.0);
                Sfx.ClearCues();
                Sfx.AddCue(Math.Max(0, spikeMs - ProceduralViralSoundFxSynthesizer.WhooshLeadMs), ViralSfxKind.Whoosh, 0.8f);
                Sfx.AddCue(spikeMs, ViralSfxKind.Impact, 1.2f);
                var wordMs = Math.Max(0, spikeMs - 1000);
                for (var i = 0; i < r.Words; i++) Sfx.AddCue(wordMs + i * 400.0, ViralSfxKind.Pop, i == 0 ? 1f : 0.55f);
                r.Cues = Sfx.CueCount;
                Array.Clear(_mix);
                Sfx.Mix(_mix, 1, ProceduralViralSoundFxSynthesizer.StartSample(spikeMs));
                var peak = 0f;
                for (var i = 0; i < _mix.Length; i++) { var v = MathF.Abs(_mix[i]); if (v > peak) peak = v; }
                r.ImpactPeak = peak;
            }

            r.Status = status;
            r.PackMs = (float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            lock (_gate)
            {
                r.Id = _nextId++;
                _done[_doneHead] = r; _doneHead = (_doneHead + 1) % ResultCap; if (_doneN < ResultCap) _doneN++;
                switch (status)
                {
                    case ShortStatus.Ready: Packaged++; break;
                    case ShortStatus.Expired: Expired++; break;
                    default: Failed++; break;
                }
                LastPackMs = r.PackMs; LastBytes = n;
            }
            return true;
        }
    }

    /// <summary>패키징 완료본 → v17.76 쿼터 가드 최초 적합 시각 예약(YouTube Shorts · Instagram Reels) → 배포 큐 이관.</summary>
    public bool Dispatch(long id, long nowUnix)
    {
        lock (_gate)
        {
            for (var i = 0; i < _doneN; i++)
            {
                ref var r = ref _done[(_doneHead - 1 - i + ResultCap * 2) % ResultCap];
                if (r.Id != id) continue;
                if (r.Status != ShortStatus.Ready) return false;
                var horizon = nowUnix + 7 * MultiChannelQuotaDistributionGuard.Day;
                var yt = Quota.EarliestFit(PublishPlatforms.YouTube, nowUnix, horizon);
                var ig = Quota.EarliestFit(PublishPlatforms.Instagram, nowUnix, horizon);
                if (yt == long.MaxValue || ig == long.MaxValue || !Quota.Reserve(PublishPlatforms.YouTube, yt) || !Quota.Reserve(PublishPlatforms.Instagram, ig)) return false;
                r.YtAtUnix = yt; r.IgAtUnix = ig; r.Status = ShortStatus.Dispatched;
                Dispatched++;
                return true;
            }
            return false;
        }
    }

    /// <summary>Ready 전량 원클릭 이관. 반환 = 이관 수.</summary>
    public int DispatchAll(long nowUnix)
    {
        var ids = 0;
        for (var i = ResultCap - 1; i >= 0; i--)
        {
            long id;
            lock (_gate)
            {
                if (i >= _doneN) continue;
                ref readonly var r = ref _done[(_doneHead - 1 - i + ResultCap * 2) % ResultCap];
                if (r.Status != ShortStatus.Ready) continue;
                id = r.Id;
            }
            if (Dispatch(id, nowUnix)) ids++;
        }
        return ids;
    }

    /// <summary>클립 시작이 디코더 진입 가능한지 — 첫 VCL 이 IDR 이고 앞에 SPS · PPS 존재.</summary>
    public static bool StartsDecodable(ReadOnlySpan<byte> annexB)
    {
        bool sps = false, pps = false;
        var pos = 0;
        while (RtmpWire.NextNal(annexB, ref pos, out var nal))
        {
            if (nal.Length == 0) continue;
            var t = nal[0] & 0x1F;
            if (t == 7) sps = true;
            else if (t == 8) pps = true;
            else if (t == 5) return sps && pps;
            else if (t == 1) return false;
        }
        return false;
    }

    private static int WordCount(string s)
    {
        var n = 0; var inWord = false;
        for (var i = 0; i < s.Length; i++)
        {
            var w = !char.IsWhiteSpace(s[i]);
            if (w && !inWord) n++;
            inWord = w;
        }
        return n;
    }

    public void Reset()
    {
        lock (_work)
        lock (_gate)
        {
            _qHead = _qN = _doneHead = _doneN = 0;
            Enqueued = Dropped = Packaged = Expired = Failed = Dispatched = HotPathInterference = 0;
            LastEnqueueUs = MaxEnqueueUs = LastPackMs = 0; LastBytes = 0;
            Quota.ClearReservations();
        }
    }

    public static string StatusName(ShortStatus s) => s switch
    {
        ShortStatus.Queued => "대기", ShortStatus.Packaging => "패키징", ShortStatus.Ready => "배포 준비", ShortStatus.Dispatched => "배포 큐 이관",
        ShortStatus.Expired => "링 만료", _ => "실패",
    };

    public void Dispose()
    {
        StopWorker();
        _signal.Dispose();
    }
}
