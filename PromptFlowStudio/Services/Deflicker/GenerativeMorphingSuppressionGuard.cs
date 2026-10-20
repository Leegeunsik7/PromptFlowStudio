namespace PromptFlowStudio.Services.Deflicker;

/// <summary>
/// [PromptFlow Studio v17.07.0] 🛡 생성형 모핑 억제 &amp; 프레임 롤백 가드.
/// 현재 입력과 흐름 보상된 직전 출력의 저역 휘도(반해상도)에서 소벨 윤곽을 뽑고, 3-4 챔퍼 거리 변환으로 타일(8×6)별 양방향 평균 윤곽 거리(px)를 계산 —
/// 허용 오차 초과 타일 = 형태 환각(모자 윤곽 · 눈 모양 임의 변형)으로 보고 해당 영역 가중치를 억제(직전 출력으로 롤백 마스크),
/// 윤곽 타일 중 다수가 변형이면 프레임 전체 롤백. 연속 한도 초과 시 의도된 변화로 수용(재고정). 장면 전환(신뢰도 붕괴)은 감시 제외. 전 경로 무할당.
/// </summary>
public sealed class GenerativeMorphingSuppressionGuard
{
    public const int TilesX = 8, TilesY = 6, DistCap = 24, Margin = 3;

    private readonly int _w, _h;
    private readonly byte[] _eCur, _ePrev, _wCur, _wPrev;
    private readonly int[] _dCur, _dPrev;
    private readonly float[] _mask, _tmp, _soft, _maskFull, _q, _sc, _sp;
    private readonly float[] _score = new float[TilesX * TilesY];
    private readonly bool[] _morph = new bool[TilesX * TilesY];
    private int _consecutive;

    public GenerativeMorphingSuppressionGuard(int halfWidth = 80, int halfHeight = 48)
    {
        _w = halfWidth; _h = halfHeight;
        var n = halfWidth * halfHeight;
        _eCur = new byte[n]; _ePrev = new byte[n]; _wCur = new byte[n]; _wPrev = new byte[n]; _dCur = new int[n]; _dPrev = new int[n];
        _mask = new float[n]; _tmp = new float[n]; _soft = new float[n]; _maskFull = new float[n * 4];
        _q = new float[n / 4]; _sc = new float[n]; _sp = new float[n];
    }

    public bool Enabled { get; set; } = true;
    /// <summary>허용 형태 오차(반해상도 px · 평균 윤곽 거리).</summary>
    public float Tolerance { get; set; } = 0.9f;
    public float EdgeThreshold { get; set; } = 0.035f;
    public float RollbackFraction { get; set; } = 0.5f;
    /// <summary>흐름 신뢰 하한(기본 0 = 미사용 — 모핑 영역은 광도 잔차로 신뢰 0이 되므로 필터하면 환각을 가림 · 장면 전환은 별도 제외).</summary>
    public float MinConfidence { get; set; }
    public int MaxConsecutive { get; set; } = 8;
    public long Frames { get; private set; }
    public long MorphFrames { get; private set; }
    public long MorphTiles { get; private set; }
    public long FrameRollbacks { get; private set; }
    public long Accepted { get; private set; }
    public float MaxScore { get; private set; }
    /// <summary>사라진 윤곽 최대 거리(px · 진단용).</summary>
    public float MaxLost { get; private set; }
    public float Suppression { get; private set; }
    public int LastMorphTiles { get; private set; }
    public int LastEdgeTiles { get; private set; }
    public bool LastRollback { get; private set; }
    public double LastUs { get; private set; }
    public float TileScore(int t) => _score[t];
    public bool TileMorph(int t) => _morph[t];
    public ReadOnlySpan<float> MaskHalf => _mask;
    public ReadOnlySpan<byte> EdgesCurrent => _eCur;
    public ReadOnlySpan<byte> EdgesReference => _ePrev;
    public ReadOnlySpan<byte> WeakCurrent => _wCur;
    public ReadOnlySpan<byte> WeakReference => _wPrev;
    public ReadOnlySpan<float> MaskFull => _maskFull;

    public void Reset() { _consecutive = 0; Array.Clear(_mask); Array.Clear(_maskFull); Array.Clear(_morph); Array.Clear(_score); }

    /// <summary>소벨 윤곽: 강(임계) · 약(0.6×임계) 2단 — 비교는 강 윤곽 → 상대 약 윤곽 거리(임계 경계 깜빡임 무시).</summary>
    private void Edges(ReadOnlySpan<float> l, byte[] e, byte[] weak)
    {
        int w = _w, h = _h;
        var th2 = EdgeThreshold * EdgeThreshold; var tw2 = th2 * 0.36f;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                int xm = Math.Max(0, x - 1), xp = Math.Min(w - 1, x + 1), ym = Math.Max(0, y - 1) * w, yp = Math.Min(h - 1, y + 1) * w, yc = y * w;
                var gx = (l[ym + xp] + 2 * l[yc + xp] + l[yp + xp] - l[ym + xm] - 2 * l[yc + xm] - l[yp + xm]) * 0.125f;
                var gy = (l[yp + xm] + 2 * l[yp + x] + l[yp + xp] - l[ym + xm] - 2 * l[ym + x] - l[ym + xp]) * 0.125f;
                var m2 = gx * gx + gy * gy;
                e[yc + x] = (byte)(m2 > th2 ? 1 : 0); weak[yc + x] = (byte)(m2 > tw2 ? 1 : 0);
            }
    }

    /// <summary>3-4 챔퍼 거리 변환(2 패스 · 단위 = px × 3).</summary>
    private void Distance(byte[] e, int[] d)
    {
        int w = _w, h = _h, big = DistCap * 3;
        for (var i = 0; i < d.Length; i++) d[i] = e[i] != 0 ? 0 : big;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x; var v = d[i];
                if (x > 0) v = Math.Min(v, d[i - 1] + 3);
                if (y > 0)
                {
                    v = Math.Min(v, d[i - w] + 3);
                    if (x > 0) v = Math.Min(v, d[i - w - 1] + 4);
                    if (x < w - 1) v = Math.Min(v, d[i - w + 1] + 4);
                }
                d[i] = v;
            }
        for (var y = h - 1; y >= 0; y--)
            for (var x = w - 1; x >= 0; x--)
            {
                var i = y * w + x; var v = d[i];
                if (x < w - 1) v = Math.Min(v, d[i + 1] + 3);
                if (y < h - 1)
                {
                    v = Math.Min(v, d[i + w] + 3);
                    if (x < w - 1) v = Math.Min(v, d[i + w + 1] + 4);
                    if (x > 0) v = Math.Min(v, d[i + w - 1] + 4);
                }
                d[i] = v;
            }
    }

    /// <summary>
    /// 1 프레임 판정: cur = 현재 입력 저역 휘도 · warpedPrev = 흐름 보상 직전 출력 저역 휘도 · conf = 흐름 신뢰도(반해상도).
    /// 반환 = 억제 마스크 평균(0 = 정상 · 1 = 전면 롤백).
    /// </summary>
    public float Analyze(ReadOnlySpan<float> cur, ReadOnlySpan<float> warpedPrev, ReadOnlySpan<float> conf, bool sceneCut)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Frames++;
        Array.Clear(_mask);
        LastRollback = false; LastMorphTiles = 0; LastEdgeTiles = 0; MaxScore = 0; MaxLost = 0;
        if (!Enabled || sceneCut)
        {
            _consecutive = 0; Array.Clear(_morph); Array.Clear(_score); Array.Clear(_maskFull); Suppression = 0;
            LastUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
            return 0;
        }
        // 형태 윤곽만: 한 단계 더 저역화(↓↑)해 레이스/체크 밴드 등 질감 경계 제외.
        FlowPlane.Down(cur, _w, _h, _tmp, _q); FlowPlane.Up(_q, _w / 2, _h / 2, _sc);
        FlowPlane.Down(warpedPrev, _w, _h, _tmp, _q); FlowPlane.Up(_q, _w / 2, _h / 2, _sp);
        Edges(_sc, _eCur, _wCur); Edges(_sp, _ePrev, _wPrev);
        Distance(_wCur, _dCur); Distance(_wPrev, _dPrev);
        int tw = _w / TilesX, th = _h / TilesY;
        for (var ty = 0; ty < TilesY; ty++)
            for (var tx = 0; tx < TilesX; tx++)
            {
                int nc = 0, np = 0; long sc = 0, sp = 0;
                for (var y = ty * th; y < (ty + 1) * th; y++)
                    for (var x = tx * tw; x < (tx + 1) * tw; x++)
                    {
                        var i = y * _w + x;
                        if (conf[i] < MinConfidence || x < Margin || y < Margin || x >= _w - Margin || y >= _h - Margin) continue;   // 가장자리 = 패닝 유입/클램프 영역
                        if (_eCur[i] != 0) { nc++; sc += _dPrev[i]; }
                        if (_ePrev[i] != 0) { np++; sp += _dCur[i]; }
                    }
                var t = ty * TilesX + tx;
                float s = 0;
                if (nc >= 4 || np >= 4)
                {
                    // 각 방향 평균 거리(전역 거리 변환 → 타일 경계 너머 대응 윤곽도 인정) · 표본 부족 방향은 제외.
                    LastEdgeTiles++;
                    // 현재 → 기준 방향만(환각 = 기준에 없던 새 윤곽). 기준 → 현재(사라진 윤곽)는 직전 출력 잔여물에 오탐하므로 보조 지표로만.
                    s = nc >= 4 ? sc / (3f * nc) : 0f;
                    var lost = np >= 4 ? sp / (3f * np) : 0f;
                    if (lost > MaxLost) MaxLost = lost;
                }
                _score[t] = s; _morph[t] = s > Tolerance;
                if (s > MaxScore) MaxScore = s;
                if (_morph[t]) LastMorphTiles++;
            }
        if (LastMorphTiles > 0)
        {
            if (++_consecutive > MaxConsecutive) { Accepted++; _consecutive = 0; Array.Clear(_morph); LastMorphTiles = 0; }   // 지속 변화 = 의도된 변화로 수용
        }
        else _consecutive = 0;
        if (LastMorphTiles > 0)
        {
            MorphFrames++; MorphTiles += LastMorphTiles;
            if (LastMorphTiles >= Math.Max(1f, RollbackFraction * LastEdgeTiles))
            {
                LastRollback = true; FrameRollbacks++;
                Array.Fill(_mask, 1f);
            }
            else
            {
                for (var y = 0; y < _h; y++)
                    for (var x = 0; x < _w; x++)
                        if (_morph[Math.Min(TilesY - 1, y / th) * TilesX + Math.Min(TilesX - 1, x / tw)]) _mask[y * _w + x] = 1f;
                // 부드러운 경계(반경 3 박스 평균 후 팽창 유지).
                FlowPlane.BoxSum(_mask, _w, _h, 3, _tmp, _soft);
                for (var i = 0; i < _mask.Length; i++) _mask[i] = MathF.Max(_mask[i], _soft[i] / 49f);
            }
        }
        FlowPlane.Up(_mask, _w, _h, _maskFull);
        double acc = 0;
        for (var i = 0; i < _mask.Length; i++) acc += _mask[i];
        Suppression = (float)(acc / _mask.Length);
        LastUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return Suppression;
    }
}
