using System.Buffers.Text;
using System.Numerics;
namespace PromptFlowStudio.Services.ViralReels;

/// <summary>릴스 메타데이터 1건(값 형식) — 조회 · 저장 · 공유 · 경과 시간 · 카테고리.</summary>
public struct ReelMeta
{
    public int Id;
    public long Views;
    public int Saves, Shares, AgeHours, Category;
    public float Velocity, Score;
}

/// <summary>3초 훅 분해 결과 1 프레임 — 링버퍼 슬롯.</summary>
public struct ViralReelDeconstructSpan
{
    public int Frame;
    public float TimeMs, Flow, Rms, Hook;
    public bool Beat;
}

/// <summary>
/// [PromptFlow Studio v17.47.0] 📱 실시간 바이럴 릴스 인제스천 &amp; 3초 훅 분해 엔진(<see cref="IngestBatch"/> · <see cref="Deconstruct"/> 60 Hz 무할당).
/// <b>인제스천</b>: 트렌딩 피드(시뮬레이션 — 실 스크래핑 없음)의 UTF-8 레코드 <c>id=;v=;s=;sh=;h=;c=</c> 를 <see cref="Utf8Parser"/> 로 틱당 256 줄씩
/// 파싱해 확산 속도(조회/시간) × (1 + 20·참여율) 점수로 상위 K 를 삽입 정렬, 1 패스 완료 시 랭킹을 확정하고 상위 0.1% 를 표시한다.
/// <b>분해</b>: 선택 릴스의 0~3초(180 프레임) 9:16 루마 · 48 kHz 오디오를 프레임마다 SIMD 로 스캔 — 프레임 차분 절대 평균(옵티컬 플로우 근사) ·
/// RMS · EWMA 기준 대비 서지 온셋(비트 타임코드) → 훅 점수 최대 프레임을 핵심 후킹 프레임으로 기록한다.
/// </summary>
public sealed class RealtimeViralReelsIngestionEngine
{
    public const int W = 72, H = 128, Px = W * H, Fps = 60, WindowFrames = 3 * Fps, SampleRate = 48000, Spf = SampleRate / Fps;
    public const int FeedSize = 4096, Batch = 256, TopK = 8, SpanCap = 256, RecordBytes = 64;
    private static readonly float[] PadHz = { 330f, 494f, 659f, 880f, 1319f, 1760f, 2637f, 3520f }, PadRate = { 0.7f, 1.3f, 1.9f, 2.3f, 2.9f, 3.7f, 4.3f, 5.1f };
    public static readonly string[] Categories = { "댄스", "푸드", "여행", "피트니스", "코미디" };
    private readonly byte[] _feed = new byte[FeedSize * RecordBytes];
    private readonly int[] _lineLen = new int[FeedSize];
    private readonly ReelMeta[] _work = new ReelMeta[TopK], _top = new ReelMeta[TopK];
    private readonly float[] _luma = new float[Px], _prev = new float[Px], _audio = new float[Spf];
    private readonly ViralReelDeconstructSpan[] _spans = new ViralReelDeconstructSpan[SpanCap];
    private int _cursor, _workN, _topN, _spanHead, _spanCount;
    private float _rmsBase, _flowBase, _prevRms, _hookBest;
    private bool _havePrev;

    public RealtimeViralReelsIngestionEngine() => BuildFeed(7);

    public bool UseSimd { get; set; } = true;
    public int TopCount => _topN;
    public ref readonly ReelMeta Top(int i) => ref _top[i];
    public static int Top01Count => (FeedSize + 999) / 1000;
    public long Passes { get; private set; }
    public long Parsed { get; private set; }
    public long ParseErrors { get; private set; }
    public double LastIngestUs { get; private set; }
    public double LastDeconUs { get; private set; }
    public int ReelId { get; private set; }
    public int PlantedHookFrame { get; private set; }
    public float Bpm { get; private set; }
    public int HookFrame { get; private set; } = -1;
    public int BeatCount { get; private set; }
    public float Flow { get; private set; }
    public float Rms { get; private set; }
    public ReadOnlySpan<float> Luma => _luma;
    public ReadOnlySpan<float> Audio => _audio;
    public int SpanCount => _spanCount;
    /// <summary>i = 0 이 최신.</summary>
    public ref readonly ViralReelDeconstructSpan Span(int i) => ref _spans[(_spanHead - 1 - i + 2 * SpanCap) % SpanCap];

    // ══════════════════════ 피드(시뮬레이션) ══════════════════════
    private void BuildFeed(int seed)
    {
        var rng = new Random(seed);
        for (var i = 0; i < FeedSize; i++)
        {
            var id = 100000 + i;
            long views = 2000 + rng.Next(0, 400000);
            var age = 1 + rng.Next(0, 96);
            if (i % 997 == 13) { views = 8_000_000 + 1_500_000L * (i / 997); age = 3 + i / 997; }
            var saves = (int)(views * (0.004 + 0.02 * rng.NextDouble()));
            var shares = (int)(views * (0.002 + 0.01 * rng.NextDouble()));
            var line = _feed.AsSpan(i * RecordBytes, RecordBytes);
            var n = 0;
            n += Put(line[n..], "id="u8); n += Num(line[n..], id);
            n += Put(line[n..], ";v="u8); n += Num(line[n..], views);
            n += Put(line[n..], ";s="u8); n += Num(line[n..], saves);
            n += Put(line[n..], ";sh="u8); n += Num(line[n..], shares);
            n += Put(line[n..], ";h="u8); n += Num(line[n..], age);
            n += Put(line[n..], ";c="u8); n += Num(line[n..], rng.Next(0, Categories.Length));
            _lineLen[i] = n;
        }
    }

    private static int Put(Span<byte> d, ReadOnlySpan<byte> s) { s.CopyTo(d); return s.Length; }
    private static int Num(Span<byte> d, long v) { Utf8Formatter.TryFormat(v, d, out var w); return w; }

    /// <summary>레코드 1줄 파싱(무할당) — 필수 키 누락/숫자 오류 시 false.</summary>
    public static bool ParseLine(ReadOnlySpan<byte> line, out ReelMeta m)
    {
        m = default;
        var seen = 0;
        while (!line.IsEmpty)
        {
            var semi = line.IndexOf((byte)';');
            var kv = semi < 0 ? line : line[..semi];
            line = semi < 0 ? default : line[(semi + 1)..];
            var eq = kv.IndexOf((byte)'=');
            if (eq <= 0) return false;
            var key = kv[..eq]; var val = kv[(eq + 1)..];
            if (!Utf8Parser.TryParse(val, out long v, out var used) || used != val.Length || v < 0) return false;
            if (key.SequenceEqual("id"u8)) { m.Id = (int)v; seen |= 1; }
            else if (key.SequenceEqual("v"u8)) { m.Views = v; seen |= 2; }
            else if (key.SequenceEqual("s"u8)) m.Saves = (int)v;
            else if (key.SequenceEqual("sh"u8)) m.Shares = (int)v;
            else if (key.SequenceEqual("h"u8)) { m.AgeHours = (int)Math.Max(v, 1); seen |= 4; }
            else if (key.SequenceEqual("c"u8)) m.Category = (int)Math.Min(v, Categories.Length - 1);
        }
        if (seen != 7) return false;
        m.Velocity = (float)m.Views / m.AgeHours;
        var engage = m.Views > 0 ? (m.Saves + 2f * m.Shares) / m.Views : 0f;
        m.Score = m.Velocity * (1f + 20f * engage);
        return true;
    }

    /// <summary>피드 256 줄 파싱 · 상위 K 삽입 정렬 — 1 패스 완료 시 랭킹 확정(true).</summary>
    public bool IngestBatch()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var end = Math.Min(_cursor + Batch, FeedSize);
        for (var i = _cursor; i < end; i++)
        {
            if (!ParseLine(_feed.AsSpan(i * RecordBytes, _lineLen[i]), out var m)) { ParseErrors++; continue; }
            Parsed++;
            Insert(in m);
        }
        _cursor = end;
        var done = false;
        if (_cursor >= FeedSize)
        {
            Array.Copy(_work, _top, _workN);
            _topN = _workN; _workN = 0; _cursor = 0;
            Passes++;
            done = true;
        }
        LastIngestUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return done;
    }

    private void Insert(in ReelMeta m)
    {
        var pos = _workN;
        while (pos > 0 && _work[pos - 1].Score < m.Score) pos--;
        if (pos >= TopK) return;
        var last = Math.Min(_workN, TopK - 1);
        for (var k = last; k > pos; k--) _work[k] = _work[k - 1];
        _work[pos] = m;
        if (_workN < TopK) _workN++;
    }

    // ══════════════════════ 3초 훅 분해 ══════════════════════
    /// <summary>분해 대상 릴스 교체 — 시드 = id(훅 컷 프레임 · BPM 결정).</summary>
    public void BeginReel(int id)
    {
        ReelId = id;
        PlantedHookFrame = 12 + (int)((uint)(id * 2654435761u) % 28u);
        Bpm = 100f + (id % 41);
        ResetWindow();
        _spanCount = 0; _spanHead = 0;
    }

    public void ResetWindow()
    {
        _havePrev = false; _rmsBase = 0f; _flowBase = 0f; _prevRms = 0f; _hookBest = 0f;
        HookFrame = -1; BeatCount = 0;
    }

    public float BeatPeriodFrames => 60f * Fps / Bpm;
    public bool IsPlantedBeat(int frame) { var p = BeatPeriodFrames; var k = MathF.Round((frame - 4) / p); return k >= 0 && (int)MathF.Round(4 + k * p) == frame; }

    /// <summary>원본 릴스 프레임 디코드(시뮬레이션) — 9:16 루마 + 48 kHz 오디오(보이스 · 8 파셜 패드 · 비트 킥). 훅 프레임에서 장면 컷.</summary>
    public void RenderSource(int frame, Span<float> luma, Span<float> audio)
    {
        var cut = frame >= PlantedHookFrame;
        var phase = cut ? 2.1f + 0.02f * frame : 0.01f * frame;
        var freq = cut ? 0.55f : 0.3f;
        var sx = 36f + 10f * MathF.Sin(0.05f * frame); var sy = cut ? 58f : 70f; var r = cut ? 20f : 16f;
        for (var y = 0; y < H; y++)
        {
            var row = y * W; var g = 0.25f + 0.2f * y / H;
            for (var x = 0; x < W; x++)
            {
                var dx = x - sx; var dy = y - sy;
                luma[row + x] = dx * dx + 0.6f * dy * dy < r * r ? 0.85f + 0.06f * MathF.Sin(0.4f * y) : g + 0.08f * MathF.Sin(freq * x + 0.21f * y * (cut ? 1f : 0.2f) + phase);
            }
        }
        var p = BeatPeriodFrames;
        var kb = MathF.Floor((frame - 3.5f) / p);
        var beatStart = kb >= 0 ? MathF.Round(4 + kb * p) * Spf : 0f;
        for (var i = 0; i < Spf; i++)
        {
            var s = frame * Spf + i;
            var t = s / (float)SampleRate;
            var env = 0.5f + 0.5f * MathF.Sin(2f * MathF.PI * 3f * t);
            var a = 0.15f * env * MathF.Sin(2f * MathF.PI * 220f * t);
            for (var k = 0; k < PadHz.Length; k++) a += 0.05f * (0.5f + 0.5f * MathF.Sin(2f * MathF.PI * PadRate[k] * t + k)) * MathF.Sin(2f * MathF.PI * PadHz[k] * t);
            var ds = s - beatStart;
            if (kb >= 0 && ds >= 0) a += 0.8f * MathF.Exp(-ds / 2400f) * MathF.Sin(2f * MathF.PI * 60f * ds / SampleRate);
            audio[i] = a;
        }
    }

    /// <summary>프레임 1장 분해(무할당) — 원본을 내부 버퍼로 디코드 후 플로우 · RMS · 비트 온셋 · 훅 점수 기록.</summary>
    public ref readonly ViralReelDeconstructSpan Deconstruct(int frame)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        RenderSource(frame, _luma, _audio);
        var flow = _havePrev ? MeanAbsDiff(_luma, _prev, UseSimd) : 0f;
        var rms = MathF.Sqrt(MeanSquare(_audio, UseSimd));
        var beat = _havePrev && rms > 0.05f && rms > 1.6f * _rmsBase && rms > 1.3f * _prevRms;
        var hook = _havePrev ? flow / (_flowBase + 0.01f) + (beat ? 1f : 0f) : 0f;
        if (beat) BeatCount++;
        if (frame < WindowFrames && hook > _hookBest) { _hookBest = hook; HookFrame = frame; }
        _rmsBase = _havePrev ? 0.9f * _rmsBase + 0.1f * rms : rms;
        _flowBase = _havePrev ? 0.9f * _flowBase + 0.1f * flow : 0f;
        _prevRms = rms;
        _luma.AsSpan().CopyTo(_prev);
        _havePrev = true;
        Flow = flow; Rms = rms;
        ref var sp = ref _spans[_spanHead];
        sp.Frame = frame; sp.TimeMs = frame * 1000f / Fps; sp.Flow = flow; sp.Rms = rms; sp.Beat = beat; sp.Hook = hook;
        _spanHead = (_spanHead + 1) % SpanCap;
        if (_spanCount < SpanCap) _spanCount++;
        LastDeconUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return ref sp;
    }

    public static float MeanAbsDiff(ReadOnlySpan<float> a, ReadOnlySpan<float> b, bool simd = true)
    {
        var i = 0; var s = 0f;
        if (simd && Vector.IsHardwareAccelerated)
        {
            var n = Vector<float>.Count; var acc = Vector<float>.Zero;
            for (; i <= a.Length - n; i += n) acc += Vector.Abs(new Vector<float>(a.Slice(i, n)) - new Vector<float>(b.Slice(i, n)));
            s = Vector.Sum(acc);
        }
        for (; i < a.Length; i++) s += MathF.Abs(a[i] - b[i]);
        return a.Length > 0 ? s / a.Length : 0f;
    }

    public static float MeanSquare(ReadOnlySpan<float> a, bool simd = true)
    {
        var i = 0; var s = 0f;
        if (simd && Vector.IsHardwareAccelerated)
        {
            var n = Vector<float>.Count; var acc = Vector<float>.Zero;
            for (; i <= a.Length - n; i += n) { var v = new Vector<float>(a.Slice(i, n)); acc += v * v; }
            s = Vector.Sum(acc);
        }
        for (; i < a.Length; i++) s += a[i] * a[i];
        return a.Length > 0 ? s / a.Length : 0f;
    }
}
