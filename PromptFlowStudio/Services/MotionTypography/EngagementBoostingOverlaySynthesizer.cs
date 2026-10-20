namespace PromptFlowStudio.Services.MotionTypography;

/// <summary>
/// [PromptFlow Studio v17.52.0] 📊 시청 유지율 부스팅 오버레이 합성기(<see cref="Render"/> 무할당 · Z 순서: 전환 0 → 진행률 1 → 투표 2 → 배지 3 → (자막 4)).
/// <b>진행률 바</b>: 세이프 존 상단 10 px · 반투명 트랙 + 노랑→주황 채움. <b>투표 스티커</b>: 중앙 둥근 패널(질문 · A/B 반쪽 · 득표율 막대 easeOut 상승 · 280 ms 팝인).
/// <b>"WAIT FOR IT…" 배지</b>: 0~2.5 초 펄스(±6%) 알약. <b>전환 인디케이터</b>: 컷 시각 ±200 ms 동안 세이프 존 하단 와이프 바 + 결정적 해시 글리치 블록.
/// 모든 요소 박스는 <see cref="CrossPlatformUiSafeZoneGuard.Fit"/> 로 데드존 밖에 배치되고 그린 경계를 감사한다.
/// </summary>
public sealed class EngagementBoostingOverlaySynthesizer
{
    public const int MaxCuts = 16;
    private readonly float[] _cuts = new float[MaxCuts];
    private readonly char[] _q = new char[64], _a = new char[24], _b = new char[24];
    private int _cutN, _qn, _an, _bn;

    public EngagementBoostingOverlaySynthesizer(SdfGlyphAtlas atlas) { Atlas = atlas; SetVote("다음 편은?", "A 도시", "B 바다", 0.62f); }

    public SdfGlyphAtlas Atlas { get; }
    public bool Progress { get; set; } = true;
    public bool Vote { get; set; } = true;
    public bool Badge { get; set; } = true;
    public bool Transitions { get; set; } = true;
    public float VoteStartMs { get; set; } = 3000;
    public float VoteEndMs { get; set; } = 7000;
    public float BadgeEndMs { get; set; } = 2500;
    public float VoteShareA { get; private set; } = 0.62f;
    public int Elements { get; private set; }
    public double LastRenderUs { get; private set; }
    public PxRect LastProgress { get; private set; }
    public PxRect LastVote { get; private set; }
    public PxRect LastBadge { get; private set; }
    public PxRect LastWipe { get; private set; }

    /// <summary>투표 문구 · A 득표율(비핫패스 · 글리프 준비).</summary>
    public void SetVote(ReadOnlySpan<char> question, ReadOnlySpan<char> a, ReadOnlySpan<char> b, float shareA)
    {
        _qn = Math.Min(question.Length, _q.Length); question[.._qn].CopyTo(_q);
        _an = Math.Min(a.Length, _a.Length); a[.._an].CopyTo(_a);
        _bn = Math.Min(b.Length, _b.Length); b[.._bn].CopyTo(_b);
        VoteShareA = Math.Clamp(shareA, 0f, 1f);
        Atlas.Ensure(question); Atlas.Ensure(a); Atlas.Ensure(b); Atlas.Ensure("WAIT FOR IT…0123456789%");
    }

    public void SetCuts(ReadOnlySpan<float> cutsMs) { _cutN = Math.Min(cutsMs.Length, MaxCuts); cutsMs[.._cutN].CopyTo(_cuts); }

    private static float EaseOut(float u) { u = Math.Clamp(u, 0f, 1f); var v = 1f - u; return 1f - v * v * v; }

    /// <summary>오버레이 렌더(무할당) — 레이어는 호출자가 Begin 한 상태.</summary>
    public void Render(float tMs, float durationMs, RgbaOverlayLayer layer, CrossPlatformUiSafeZoneGuard guard)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var s = guard.Safe;
        Elements = 0;
        LastProgress = LastVote = LastBadge = LastWipe = default;
        // z0 전환 인디케이터
        if (Transitions)
            for (var i = 0; i < _cutN; i++)
            {
                var d = tMs - _cuts[i];
                if (d < -200 || d > 200) continue;
                var u = (d + 200) / 400f;
                var bar = new PxRect(s.X, s.Bottom - 18, (int)(s.W * EaseOut(u)), 12);
                guard.Fit(ref bar);
                layer.FillRoundRect(bar, 6, 1f, 1f, 1f, 0.85f * (1f - MathF.Abs(2 * u - 1)));
                for (var k = 0; k < 6; k++)
                {
                    var h = (uint)((i * 7919 + k * 104729 + (int)(d / 33)) * 2654435761u);
                    var blk = new PxRect(s.X + (int)(h % (uint)Math.Max(s.W - 120, 1)), s.Bottom - 60 - (int)(h >> 20 & 31), 60 + (int)(h >> 8 & 63), 6 + (int)(h >> 16 & 7));
                    guard.Fit(ref blk);
                    layer.FillRoundRect(blk, 0, (h & 1) != 0 ? 1f : 0f, (h & 2) != 0 ? 1f : 0.2f, 1f, 0.55f);
                }
                LastWipe = bar; guard.Audit(bar); Elements++;
                break;
            }
        // z1 진행률 바
        if (Progress && durationMs > 0)
        {
            var track = new PxRect(s.X, s.Y, s.W, 10);
            guard.Fit(ref track);
            layer.FillRoundRect(track, 5, 1f, 1f, 1f, 0.28f);
            var fill = track; fill.W = (int)(track.W * Math.Clamp(tMs / durationMs, 0f, 1f));
            layer.FillRoundRect(fill, 5, 1f, 0.78f, 0.05f, 1f);
            LastProgress = track; guard.Audit(track); Elements++;
        }
        // z2 투표 스티커
        if (Vote && tMs >= VoteStartMs && tMs <= VoteEndMs)
        {
            var pop = EaseOut((tMs - VoteStartMs) / 280f);
            var w = (int)(Math.Min(680, s.W) * (0.85f + 0.15f * pop)); var h = (int)(250 * (0.85f + 0.15f * pop));
            var box = new PxRect((layer.Width - w) / 2, (int)(layer.Height * 0.40f) - h / 2, w, h);
            var k = guard.Fit(ref box);
            layer.FillRoundRect(box, 28 * k, 1f, 1f, 1f, 0.94f * pop);
            var qpx = 50f * k;
            var qw = SdfText.MeasureEm(Atlas, _q.AsSpan(0, _qn)) * qpx;
            SdfText.Draw(Atlas, layer, _q.AsSpan(0, _qn), box.X + (box.W - qw) / 2, box.Y + 22 * k, qpx, 0.08f, 0.08f, 0.1f, int.MaxValue, 0.04f);
            var grow = EaseOut((tMs - VoteStartMs - 280) / 900f);
            var half = (box.W - (int)(60 * k)) / 2;
            var ra = new PxRect(box.X + (int)(20 * k), box.Bottom - (int)(118 * k), half, (int)(96 * k));
            var rb = new PxRect(ra.Right + (int)(20 * k), ra.Y, half, ra.H);
            layer.FillRoundRect(ra, 18 * k, 0.15f, 0.45f, 1f, 0.95f * pop);
            layer.FillRoundRect(rb, 18 * k, 1f, 0.2f, 0.55f, 0.95f * pop);
            var fa = ra; fa.H = (int)(10 * k); fa.Y = ra.Bottom - fa.H - (int)(8 * k); fa.X += (int)(10 * k); fa.W = (int)((ra.W - 20 * k) * VoteShareA * grow);
            var fb = rb; fb.H = fa.H; fb.Y = fa.Y; fb.X += (int)(10 * k); fb.W = (int)((rb.W - 20 * k) * (1f - VoteShareA) * grow);
            layer.FillRoundRect(fa, 5 * k, 1f, 1f, 1f, 0.9f);
            layer.FillRoundRect(fb, 5 * k, 1f, 1f, 1f, 0.9f);
            var opx = 44f * k;
            SdfText.Draw(Atlas, layer, _a.AsSpan(0, _an), ra.X + (ra.W - SdfText.MeasureEm(Atlas, _a.AsSpan(0, _an)) * opx) / 2, ra.Y + 14 * k, opx, 1f, 1f, 1f, int.MaxValue, 0.06f);
            SdfText.Draw(Atlas, layer, _b.AsSpan(0, _bn), rb.X + (rb.W - SdfText.MeasureEm(Atlas, _b.AsSpan(0, _bn)) * opx) / 2, rb.Y + 14 * k, opx, 1f, 1f, 1f, int.MaxValue, 0.06f);
            LastVote = box; guard.Audit(box); Elements++;
        }
        // z3 Wait for it 배지
        if (Badge && tMs < BadgeEndMs)
        {
            var pulse = 1f + 0.06f * MathF.Sin(2f * MathF.PI * tMs / 600f);
            const string txt = "WAIT FOR IT…";
            var bpx = 46f * pulse;
            var tw = SdfText.MeasureEm(Atlas, txt) * bpx;
            var box = new PxRect((int)((layer.Width - tw) / 2 - 30), s.Y + 40, (int)(tw + 60), (int)(bpx * 1.45f));
            var k = guard.Fit(ref box);
            layer.FillRoundRect(box, box.H * 0.5f, 0.93f, 0.13f, 0.2f, 0.95f);
            SdfText.Draw(Atlas, layer, txt, box.X + 30 * k, box.Y + (box.H - bpx * k) / 2, bpx * k, 1f, 1f, 1f, int.MaxValue, 0.05f);
            LastBadge = box; guard.Audit(box); Elements++;
        }
        LastRenderUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }
}
