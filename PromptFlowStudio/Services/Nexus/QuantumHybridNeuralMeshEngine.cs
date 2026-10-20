using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.LowSpec;

namespace PromptFlowStudio.Services.Nexus;

/// <summary>넥서스 텐서로 융합되는 입력 모달리티(각 16 레인).</summary>
public enum NexusModality : byte { Scene, Gaussian, Bci, Agent }

/// <summary>시공간 텐서 저랭크 분해 결과.</summary>
public readonly record struct MeshFactorization(int Rank, int Frames, float EnergyCaptured, float RelError, float CompressionRatio, float FlopReduction, int Iterations, double Micros);

/// <summary>양자 영감 어닐링(SQA) 결과.</summary>
public readonly record struct AnnealResult(float Energy, int Qubits, int Sweeps, int Replicas, long Accepted, double Micros);

/// <summary>
/// [PromptFlow Studio v16.70.0] 양자 영감 하이브리드 신경망 메시 엔진.
/// ① 장면 그래프 · 3DGS 입자 · BCI 채널 · 에이전트 텔레메트리를 모달리티별 16레인으로 풀링해 [시간 128 × 특징 64] 시공간 텐서 링에 적재.
/// ② 직교 부분공간 반복(SIMD Gram + Gram–Schmidt)으로 상위 고유기저를 구해 랭크 k 로 압축(에너지 목표 자동 랭크) — 인코드/디코드/잔차 이상 점수.
/// ③ 경로 적분 몬테카를로 기반 시뮬레이티드 양자 어닐링(Trotter 레플리카 · 횡자기장 Γ 감쇠)으로 QUBO 를 풀고, 렌더 잡 → 노드 배치에 사용.
/// 모든 핫패스는 사전 할당 버퍼만 사용(무할당). 실제 양자 하드웨어가 아닌 고전 시뮬레이션이다.
/// </summary>
public sealed class QuantumHybridNeuralMeshEngine
{
    public const int Lanes = 16, Features = Lanes * 4, Window = 128, MaxRank = 16, MaxQubits = 64, Replicas = 8;

    private readonly object _gate = new();
    private readonly float[] _tensor = new float[Window * Features];
    private readonly float[] _mean = new float[Features], _centered = new float[Features], _recon = new float[Features];
    private readonly float[] _gram = new float[Features * Features];
    private readonly float[] _basis = new float[MaxRank * Features], _work = new float[MaxRank * Features];
    private readonly float[] _eig = new float[MaxRank];
    private readonly float[] _sym = new float[MaxQubits * MaxQubits];
    private readonly byte[] _spins = new byte[Replicas * MaxQubits];
    private readonly float[] _field = new float[Replicas * MaxQubits], _energy = new float[Replicas];
    private int _head, _count, _rank;
    private long _frames, _factorizations, _anneals;
    private ulong _rng = 0x9E3779B97F4A7C15UL;
    private float _trace, _lastResidual;
    private MeshFactorization _lastFactor;
    private AnnealResult _lastAnneal;

    public event Action? OnChange;

    public long Frames => Interlocked.Read(ref _frames);
    public long Factorizations => Interlocked.Read(ref _factorizations);
    public long Anneals => Interlocked.Read(ref _anneals);
    public int Rank { get { lock (_gate) return _rank; } }
    public MeshFactorization LastFactorization { get { lock (_gate) return _lastFactor; } }
    public AnnealResult LastAnneal { get { lock (_gate) return _lastAnneal; } }
    public float LastResidual { get { lock (_gate) return _lastResidual; } }
    public string? LastAction { get; private set; }

    /// <summary>고유값 스펙트럼(내림차순 · 정규화 전) 복사.</summary>
    public int CopySpectrum(Span<float> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, MaxRank);
            _eig.AsSpan(0, n).CopyTo(dst);
            return n;
        }
    }

    public float Trace { get { lock (_gate) return _trace; } }

    /// <summary>한 시점 프레임을 텐서 링에 적재(모달리티별 구간 평균 풀링 · 무할당).</summary>
    public void PushFrame(ReadOnlySpan<float> scene, ReadOnlySpan<float> gaussians, ReadOnlySpan<float> bci, ReadOnlySpan<float> agents)
    {
        lock (_gate)
        {
            var row = _tensor.AsSpan(_head * Features, Features);
            Pool(scene, row.Slice((int)NexusModality.Scene * Lanes, Lanes));
            Pool(gaussians, row.Slice((int)NexusModality.Gaussian * Lanes, Lanes));
            Pool(bci, row.Slice((int)NexusModality.Bci * Lanes, Lanes));
            Pool(agents, row.Slice((int)NexusModality.Agent * Lanes, Lanes));
            _head = (_head + 1) % Window;
            if (_count < Window) _count++;
            Interlocked.Increment(ref _frames);
        }
    }

    /// <summary>가장 최근 프레임 복사.</summary>
    public void CopyLatest(Span<float> dst)
    {
        lock (_gate) _tensor.AsSpan(((_head + Window - 1) % Window) * Features, Features).CopyTo(dst);
    }

    /// <summary>입력을 <paramref name="dst"/> 레인 수로 구간 평균(SIMD 합산). 입력이 짧으면 최근접 복제.</summary>
    public static void Pool(ReadOnlySpan<float> src, Span<float> dst)
    {
        if (src.IsEmpty) { dst.Clear(); return; }
        for (var l = 0; l < dst.Length; l++)
        {
            var a = (int)((long)l * src.Length / dst.Length);
            var b = (int)((long)(l + 1) * src.Length / dst.Length);
            if (b <= a) { dst[l] = src[Math.Min(a, src.Length - 1)]; continue; }
            dst[l] = Sum(src[a..b]) / (b - a);
        }
    }

    public static float Sum(ReadOnlySpan<float> s)
    {
        var i = 0; var sum = 0f;
        if (Vector.IsHardwareAccelerated && s.Length >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            var v = MemoryMarshal.Cast<float, Vector<float>>(s);
            for (var k = 0; k < v.Length; k++) acc += v[k];
            sum = Vector.Sum(acc);
            i = v.Length * Vector<float>.Count;
        }
        for (; i < s.Length; i++) sum += s[i];
        return sum;
    }

    /// <summary><c>y += a·x</c> (SIMD).</summary>
    public static void Axpy(float a, ReadOnlySpan<float> x, Span<float> y)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated && x.Length >= Vector<float>.Count)
        {
            var va = new Vector<float>(a);
            var vx = MemoryMarshal.Cast<float, Vector<float>>(x);
            var vy = MemoryMarshal.Cast<float, Vector<float>>(y);
            for (var k = 0; k < vx.Length; k++) vy[k] += va * vx[k];
            i = vx.Length * Vector<float>.Count;
        }
        for (; i < x.Length; i++) y[i] += a * x[i];
    }

    /// <summary>
    /// 중심화 Gram 행렬 → 직교 부분공간 반복으로 상위 <see cref="MaxRank"/> 고유쌍 → 누적 에너지 ≥ <paramref name="targetEnergy"/> 인 최소 랭크 선택(무할당).
    /// </summary>
    public MeshFactorization Factorize(float targetEnergy = 0.95f, int maxIterations = 60)
    {
        var t0 = Stopwatch.GetTimestamp();
        MeshFactorization f;
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Nexus.Mesh.Factorize");
            var n = _count;
            if (n < 2) return _lastFactor;
            _mean.AsSpan().Clear();
            for (var r = 0; r < n; r++) Axpy(1f / n, _tensor.AsSpan(r * Features, Features), _mean);
            _gram.AsSpan().Clear();
            for (var r = 0; r < n; r++)
            {
                var row = _tensor.AsSpan(r * Features, Features);
                for (var j = 0; j < Features; j++) _centered[j] = row[j] - _mean[j];
                for (var i = 0; i < Features; i++)
                    if (_centered[i] != 0) Axpy(_centered[i], _centered, _gram.AsSpan(i * Features, Features));
            }
            _trace = 0;
            for (var i = 0; i < Features; i++) _trace += _gram[i * Features + i];

            // 결정적 초기 기저(대각 우세 열 + 섭동) → 직교화.
            for (var k = 0; k < MaxRank; k++)
                for (var j = 0; j < Features; j++)
                    _basis[k * Features + j] = (j == k * (Features / MaxRank) ? 1f : 0f) + 0.01f * MathF.Sin(1.3f * (k + 1) * (j + 1));
            Orthonormalize(_basis);
            var it = 0;
            var prev = 0f;
            for (; it < maxIterations; it++)
            {
                for (var k = 0; k < MaxRank; k++) MatVec(_basis.AsSpan(k * Features, Features), _work.AsSpan(k * Features, Features));
                var energy = 0f;
                for (var k = 0; k < MaxRank; k++) energy += DynamicLowBitQuantizationFallbackRouter.DotF32(_basis.AsSpan(k * Features, Features), _work.AsSpan(k * Features, Features));
                _work.AsSpan().CopyTo(_basis);
                Orthonormalize(_basis);
                if (it > 3 && MathF.Abs(energy - prev) <= 1e-6f * MathF.Max(1e-12f, MathF.Abs(energy))) { it++; break; }
                prev = energy;
            }
            // Rayleigh 몫(고유값) 및 내림차순 정렬(기저 행 동반 교환).
            for (var k = 0; k < MaxRank; k++)
            {
                MatVec(_basis.AsSpan(k * Features, Features), _work.AsSpan(0, Features));
                _eig[k] = MathF.Max(0, DynamicLowBitQuantizationFallbackRouter.DotF32(_basis.AsSpan(k * Features, Features), _work.AsSpan(0, Features)));
            }
            for (var a = 1; a < MaxRank; a++)
                for (var b = a; b > 0 && _eig[b] > _eig[b - 1]; b--)
                {
                    (_eig[b], _eig[b - 1]) = (_eig[b - 1], _eig[b]);
                    var rb = _basis.AsSpan(b * Features, Features); var ra = _basis.AsSpan((b - 1) * Features, Features);
                    for (var j = 0; j < Features; j++) (rb[j], ra[j]) = (ra[j], rb[j]);
                }
            var rank = MaxRank; var cum = 0f;
            var total = MathF.Max(1e-20f, _trace);
            for (var k = 0; k < MaxRank; k++)
            {
                cum += _eig[k];
                if (cum / total >= targetEnergy) { rank = k + 1; break; }
            }
            cum = 0;
            for (var k = 0; k < rank; k++) cum += _eig[k];
            var captured = Math.Clamp(cum / total, 0f, 1f);
            _rank = rank;
            f = new MeshFactorization(rank, n, captured, MathF.Sqrt(1 - captured),
                (float)Window * Features / (Window * rank + Features * rank + Features), Features / (2f * rank), it,
                (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
            _lastFactor = f;
        }
        Interlocked.Increment(ref _factorizations);
        return f;
    }

    private void MatVec(ReadOnlySpan<float> x, Span<float> y)
    {
        for (var i = 0; i < Features; i++) y[i] = DynamicLowBitQuantizationFallbackRouter.DotF32(_gram.AsSpan(i * Features, Features), x);
    }

    /// <summary>행 단위 수정 Gram–Schmidt(퇴화 행은 표준 기저로 재시드).</summary>
    private static void Orthonormalize(Span<float> rows)
    {
        for (var k = 0; k < MaxRank; k++)
        {
            var rk = rows.Slice(k * Features, Features);
            for (var pass = 0; pass < 2; pass++)
                for (var p = 0; p < k; p++)
                {
                    var rp = rows.Slice(p * Features, Features);
                    Axpy(-DynamicLowBitQuantizationFallbackRouter.DotF32(rk, rp), rp, rk);
                }
            var norm = MathF.Sqrt(DynamicLowBitQuantizationFallbackRouter.DotF32(rk, rk));
            if (norm < 1e-12f)
            {
                rk.Clear(); rk[(k * 7 + 3) % Features] = 1;
                for (var p = 0; p < k; p++) { var rp = rows.Slice(p * Features, Features); Axpy(-DynamicLowBitQuantizationFallbackRouter.DotF32(rk, rp), rp, rk); }
                norm = MathF.Max(1e-12f, MathF.Sqrt(DynamicLowBitQuantizationFallbackRouter.DotF32(rk, rk)));
            }
            var inv = 1f / norm;
            for (var j = 0; j < Features; j++) rk[j] *= inv;
        }
    }

    /// <summary>프레임 → 랭크 k 계수(무할당). 반환: 사용 랭크.</summary>
    public int Encode(ReadOnlySpan<float> frame, Span<float> coeffs)
    {
        lock (_gate)
        {
            for (var j = 0; j < Features; j++) _centered[j] = frame[j] - _mean[j];
            var k = Math.Min(_rank, coeffs.Length);
            for (var r = 0; r < k; r++) coeffs[r] = DynamicLowBitQuantizationFallbackRouter.DotF32(_basis.AsSpan(r * Features, Features), _centered);
            return k;
        }
    }

    /// <summary>계수 → 프레임 복원(평균 + Σ c·b).</summary>
    public void Decode(ReadOnlySpan<float> coeffs, Span<float> frame)
    {
        lock (_gate)
        {
            _mean.AsSpan().CopyTo(frame);
            var k = Math.Min(_rank, coeffs.Length);
            for (var r = 0; r < k; r++) Axpy(coeffs[r], _basis.AsSpan(r * Features, Features), frame);
        }
    }

    /// <summary>부분공간 밖 잔차 비율 ‖x − x̂‖ / ‖x − μ‖ — 텔레메트리 이상 점수(무할당).</summary>
    public float Residual(ReadOnlySpan<float> frame)
    {
        lock (_gate)
        {
            for (var j = 0; j < Features; j++) _centered[j] = frame[j] - _mean[j];
            var norm = DynamicLowBitQuantizationFallbackRouter.DotF32(_centered, _centered);
            _centered.AsSpan().CopyTo(_recon);
            for (var r = 0; r < _rank; r++)
            {
                var b = _basis.AsSpan(r * Features, Features);
                Axpy(-DynamicLowBitQuantizationFallbackRouter.DotF32(b, _centered), b, _recon);
            }
            var res = DynamicLowBitQuantizationFallbackRouter.DotF32(_recon, _recon);
            _lastResidual = norm > 1e-12f ? MathF.Sqrt(res / norm) : 0;
            return _lastResidual;
        }
    }

    /// <summary>QUBO 에너지 Σᵢⱼ Qᵢⱼ xᵢ xⱼ.</summary>
    public static float QuboEnergy(ReadOnlySpan<float> q, int n, ReadOnlySpan<byte> x)
    {
        var e = 0f;
        for (var i = 0; i < n; i++)
        {
            if (x[i] == 0) continue;
            for (var j = 0; j < n; j++) if (x[j] != 0) e += q[i * n + j];
        }
        return e;
    }

    /// <summary>
    /// 시뮬레이티드 양자 어닐링(경로 적분 · P 레플리카). 레플리카 간 결합 J⊥ = −(PT/2)·ln tanh(Γ/PT), Γ 를 선형 감쇠.
    /// 종료 후 최우수 레플리카에 단일 비트 하강 연마. <paramref name="best"/> 에 해 기록(무할당).
    /// </summary>
    public AnnealResult SolveQubo(ReadOnlySpan<float> q, int n, Span<byte> best, int sweeps = 600, ulong seed = 0)
    {
        if (n <= 0 || n > MaxQubits) throw new ArgumentOutOfRangeException(nameof(n));
        var t0 = Stopwatch.GetTimestamp();
        AnnealResult res;
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Nexus.Mesh.Anneal");
            if (seed != 0) _rng = seed;
            var scale = 0f;
            for (var i = 0; i < n; i++)
                for (var j = 0; j < n; j++)
                {
                    var s = i == j ? q[i * n + i] : q[i * n + j] + q[j * n + i];
                    _sym[i * n + j] = s;
                    scale = MathF.Max(scale, MathF.Abs(s));
                }
            scale = MathF.Max(scale, 1e-6f);
            const int P = Replicas;
            var temp = 0.05f * scale;
            var gamma0 = 2.5f * scale;
            for (var p = 0; p < P; p++)
            {
                var x = _spins.AsSpan(p * MaxQubits, n);
                for (var i = 0; i < n; i++) x[i] = (byte)(NextU() & 1);
                InitField(p, n);
            }
            var bestE = float.MaxValue;
            long accepted = 0;
            for (var s = 0; s < sweeps; s++)
            {
                var gamma = MathF.Max(1e-4f * scale, gamma0 * (1 - (float)s / sweeps));
                var jPerp = -0.5f * P * temp * MathF.Log(MathF.Tanh(gamma / (P * temp)));
                for (var p = 0; p < P; p++)
                {
                    var up = ((p + 1) % P) * MaxQubits; var dn = ((p + P - 1) % P) * MaxQubits;
                    var x = _spins.AsSpan(p * MaxQubits, n);
                    var h = _field.AsSpan(p * MaxQubits, n);
                    for (var i = 0; i < n; i++)
                    {
                        var dE = x[i] == 0 ? h[i] : -h[i];
                        var si = x[i] * 2 - 1;
                        var nb = (_spins[up + i] * 2 - 1) + (_spins[dn + i] * 2 - 1);
                        var dH = dE / P + 2f * jPerp * si * nb;
                        if (dH <= 0 || NextF() < MathF.Exp(-dH / temp))
                        {
                            Flip(p, i, n);
                            _energy[p] += dE;
                            accepted++;
                        }
                    }
                    if (_energy[p] < bestE)
                    {
                        bestE = _energy[p];
                        _spins.AsSpan(p * MaxQubits, n).CopyTo(best);
                    }
                }
            }
            // 단일 비트 하강 연마(레플리카 0 슬롯 재사용).
            best[..n].CopyTo(_spins.AsSpan(0, n));
            InitField(0, n);
            for (var improved = true; improved;)
            {
                improved = false;
                for (var i = 0; i < n; i++)
                {
                    var dE = _spins[i] == 0 ? _field[i] : -_field[i];
                    if (dE < -1e-7f * scale) { Flip(0, i, n); _energy[0] += dE; improved = true; }
                }
            }
            if (_energy[0] <= bestE) { bestE = _energy[0]; _spins.AsSpan(0, n).CopyTo(best); }
            res = new AnnealResult(bestE, n, sweeps, P, accepted, (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
            _lastAnneal = res;
        }
        Interlocked.Increment(ref _anneals);
        return res;
    }

    private void InitField(int p, int n)
    {
        var x = _spins.AsSpan(p * MaxQubits, n);
        var h = _field.AsSpan(p * MaxQubits, n);
        var e = 0f;
        for (var i = 0; i < n; i++)
        {
            var v = _sym[i * n + i];
            for (var j = 0; j < n; j++) if (j != i && x[j] != 0) v += _sym[i * n + j];
            h[i] = v;
        }
        for (var i = 0; i < n; i++)
        {
            if (x[i] == 0) continue;
            e += _sym[i * n + i];
            for (var j = i + 1; j < n; j++) if (x[j] != 0) e += _sym[i * n + j];
        }
        _energy[p] = e;
    }

    private void Flip(int p, int i, int n)
    {
        var x = _spins.AsSpan(p * MaxQubits, n);
        var h = _field.AsSpan(p * MaxQubits, n);
        var d = x[i] == 0 ? 1f : -1f;
        x[i] ^= 1;
        for (var j = 0; j < n; j++) if (j != i) h[j] += d * _sym[i * n + j];
    }

    private ulong NextU()
    {
        var x = _rng;
        x ^= x << 13; x ^= x >> 7; x ^= x << 17;
        _rng = x;
        return x * 0x2545F4914F6CDD1DUL;
    }

    private float NextF() => (NextU() >> 40) * (1f / (1 << 24));

    /// <summary>잡 배치 목적함수: Σₘ(부하ₘ/용량ₘ)² + λ·Σ 비용·지연. 할당 공간에서 평가.</summary>
    public static float PlacementCost(ReadOnlySpan<float> cost, ReadOnlySpan<float> capacity, ReadOnlySpan<float> latency, float latencyWeight, ReadOnlySpan<int> assign)
    {
        Span<float> load = stackalloc float[capacity.Length];
        load.Clear();
        var lat = 0f;
        for (var j = 0; j < cost.Length; j++) { load[assign[j]] += cost[j]; lat += cost[j] * latency[assign[j]]; }
        var e = 0f;
        for (var m = 0; m < capacity.Length; m++) { var u = load[m] / capacity[m]; e += u * u; }
        return e + latencyWeight * lat;
    }

    /// <summary>
    /// 렌더 잡 → 노드 배치를 원-핫 QUBO 로 인코딩해 SQA 로 풀고, 원-핫 위반 복구 + 1-이동/교환 국소 탐색으로 마무리.
    /// 반환: 목적함수 값. 큐비트 = 잡 × 노드 ≤ <see cref="MaxQubits"/>.
    /// </summary>
    public float AssignJobs(ReadOnlySpan<float> cost, ReadOnlySpan<float> capacity, ReadOnlySpan<float> latency, float latencyWeight, Span<int> assign, ulong seed = 0)
    {
        int J = cost.Length, M = capacity.Length, n = J * M;
        if (n > MaxQubits || M == 0) throw new ArgumentOutOfRangeException(nameof(cost));
        Span<float> q = stackalloc float[n * n];
        q.Clear();
        var penalty = 0f;
        for (var j = 0; j < J; j++)
            for (var m = 0; m < M; m++)
            {
                var i = j * M + m;
                var inv = 1f / (capacity[m] * capacity[m]);
                for (var j2 = 0; j2 < J; j2++) q[i * n + j2 * M + m] += cost[j] * cost[j2] * inv;
                q[i * n + i] += latencyWeight * cost[j] * latency[m];
                penalty = MathF.Max(penalty, 2 * cost[j] * Sum(cost) * inv + latencyWeight * cost[j] * latency[m]);
            }
        penalty = penalty * 2 + 1e-3f;
        for (var j = 0; j < J; j++)
            for (var m = 0; m < M; m++)
            {
                var i = j * M + m;
                q[i * n + i] -= penalty;
                for (var m2 = m + 1; m2 < M; m2++) q[i * n + j * M + m2] += 2 * penalty;
            }
        Span<byte> x = stackalloc byte[n];
        SolveQubo(q, n, x, seed: seed);
        for (var j = 0; j < J; j++)
        {
            var pick = -1;
            for (var m = 0; m < M; m++) if (x[j * M + m] != 0) { pick = m; break; }
            if (pick < 0)
            {
                pick = 0;
                for (var m = 1; m < M; m++) if (capacity[m] > capacity[pick]) pick = m;
            }
            assign[j] = pick;
        }
        var cur = PlacementCost(cost, capacity, latency, latencyWeight, assign);
        for (var improved = true; improved;)
        {
            improved = false;
            for (var j = 0; j < J; j++)
            {
                var keep = assign[j];
                for (var m = 0; m < M; m++)
                {
                    if (m == keep) continue;
                    assign[j] = m;
                    var c = PlacementCost(cost, capacity, latency, latencyWeight, assign);
                    if (c < cur - 1e-6f) { cur = c; keep = m; improved = true; }
                }
                assign[j] = keep;
                for (var j2 = j + 1; j2 < J; j2++)
                {
                    if (assign[j2] == assign[j]) continue;
                    (assign[j], assign[j2]) = (assign[j2], assign[j]);
                    var c = PlacementCost(cost, capacity, latency, latencyWeight, assign);
                    if (c < cur - 1e-6f) { cur = c; improved = true; }
                    else (assign[j], assign[j2]) = (assign[j2], assign[j]);
                }
            }
        }
        return cur;
    }

    public void Announce(string action)
    {
        LastAction = action;
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Nexus.Mesh.OnChange", ex); }
    }
}
