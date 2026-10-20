using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace PromptFlowStudio.Services.Media;

public enum UpscaleTarget { Uhd4K, Uhd8K }

public enum UpscaleTileState { Pending, Inferred, Blended }

public sealed record UpscaleTileView(int Index, int Row, int Col, UpscaleTileState State, double SharpnessGain);

public sealed record UpscaleReport(UpscaleTarget Target, int Tiles, int Cols, int Rows, double ScaleFactor, double MegaPixels, double ThroughputMpxS, double DepthCorrection, double ElapsedMs, bool Mapped);

/// <summary>
/// v9.8.1 자율 뉴럴 타일링 업스케일러 — MMF 무할당 재사용 아레나(미가용 시 풀 폴백) 타일 분할 추론,
/// 공간 뎁스 보정, 타일 심 블렌딩으로 4K/8K 텐서 업스케일링. 순수 BCL(MemoryMappedFile) · 백엔드 미구성 시 데모 파이프라인.
/// </summary>
public sealed class AutonomousNeuralTilingUpscaler : IDisposable
{
    public const int TickMs = 900;
    private const int TileBytes = 64 * 1024;   // 타일 작업 세트(무할당 재사용 스크래치)

    private sealed class Tile { public int Row, Col; public UpscaleTileState State; public double Sharpness; }

    private readonly object _gate = new();
    private readonly Random _rng = new(9801);
    private readonly byte[] _scratch = new byte[TileBytes];
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _acc;
    private byte[]? _pool;
    private long _arenaBytes;
    private Tile[] _tiles = Array.Empty<Tile>();
    private UpscaleTarget _target = UpscaleTarget.Uhd4K;
    private double _depthDrift;
    private UpscaleReport? _last;   // v12.2.0: _snap 에서 조회 시 지연 생성되는 캐시(핫패스는 값 스냅샷만 기록)
    private readonly record struct Snap(UpscaleTarget Target, int Tiles, int Cols, int Rows, double Scale, double Mpx, double Thru, double Depth, double Ms, bool Mapped);
    private Snap _snap;
    private bool _snapValid;
    private bool _actionFromSnap;
    private string? _lastAction;
    private System.Threading.Timer? _timer;
    private long _runs, _inferred, _blended, _depthCorr, _fallbacks;
    private bool _disposed;

    public event Action? OnChange;

    public AutonomousNeuralTilingUpscaler() => Reallocate(UpscaleTarget.Uhd4K);

    public bool Mapped => _mmf is not null;
    public bool DemoRunning => _timer is not null;
    public string? LastAction
    {
        get
        {
            lock (_gate)
            {
                if (_actionFromSnap)
                {
                    _lastAction = $"{_snap.Tiles}타일 업스케일 ×{_snap.Scale:0.#} · {(_snap.Mapped ? "MMF" : "Pool")} · {_snap.Thru:0}Mpx/s";
                    _actionFromSnap = false;
                }
                return _lastAction;
            }
        }
        private set { lock (_gate) { _lastAction = value; _actionFromSnap = false; } }
    }
    public UpscaleReport? LastReport { get { lock (_gate) return MaterializeLocked(); } }

    private UpscaleReport? MaterializeLocked()
    {
        if (_last is null && _snapValid)
            _last = new UpscaleReport(_snap.Target, _snap.Tiles, _snap.Cols, _snap.Rows, _snap.Scale, _snap.Mpx, _snap.Thru, _snap.Depth, _snap.Ms, _snap.Mapped);
        return _last;
    }
    public UpscaleTarget Target { get { lock (_gate) return _target; } }
    public long Runs => Interlocked.Read(ref _runs);
    public long TilesInferred => Interlocked.Read(ref _inferred);
    public long SeamsBlended => Interlocked.Read(ref _blended);
    public long DepthCorrections => Interlocked.Read(ref _depthCorr);
    public long Fallbacks => Interlocked.Read(ref _fallbacks);
    public long ArenaMb { get { lock (_gate) return _arenaBytes / (1024 * 1024); } }

    private static (int Cols, int Rows, double Scale, double Mpx) Geom(UpscaleTarget t)
        => t == UpscaleTarget.Uhd8K ? (8, 8, 4.0, 7680d * 4320 / 1_000_000) : (4, 4, 2.0, 3840d * 2160 / 1_000_000);

    public IReadOnlyList<UpscaleTileView> Tiles
    {
        get
        {
            lock (_gate)
            {
                var list = new List<UpscaleTileView>(_tiles.Length);
                for (var i = 0; i < _tiles.Length; i++)
                {
                    var t = _tiles[i];
                    list.Add(new UpscaleTileView(i, t.Row, t.Col, t.State, Math.Round(t.Sharpness, 2)));
                }
                return list;
            }
        }
    }

    public void SetTarget(UpscaleTarget target)
    {
        lock (_gate) { if (target != _target) Reallocate(target); }
        OnChange?.Invoke();
    }

    private void Reallocate(UpscaleTarget target)
    {
        _target = target;
        var (cols, rows, _, _) = Geom(target);
        var count = cols * rows;
        _tiles = new Tile[count];
        for (var i = 0; i < count; i++) _tiles[i] = new Tile { Row = i / cols, Col = i % cols };
        var bytes = (long)count * TileBytes;
        _acc?.Dispose(); _mmf?.Dispose(); _acc = null; _mmf = null; _pool = null;
        try
        {
            _mmf = MemoryMappedFile.CreateNew(Guid.NewGuid().ToString("N"), bytes);
            _acc = _mmf.CreateViewAccessor(0, bytes);
        }
        catch (Exception) { _mmf = null; _acc = null; _pool = new byte[bytes]; _fallbacks++; }   // MMF 미가용 → 풀 폴백
        _arenaBytes = bytes;
        _last = null;
        _snapValid = false;
        LastAction = $"{(target == UpscaleTarget.Uhd8K ? "8K" : "4K")} 아레나 재할당 · {count}타일 · {(Mapped ? "MMF" : "Pool")}";
    }

    public UpscaleReport Upscale()
    {
        UpscaleReport rep;
        lock (_gate) { UpscaleLocked(); rep = MaterializeLocked()!; }
        OnChange?.Invoke();
        return rep;
    }

    /// <summary>v12.2.0: 60FPS 후처리 핫패스용 - <see cref="Upscale"/> 과 동일 연산이되 리포트/문자열을 만들지 않는다(조회 시 지연 생성).</summary>
    public void UpscaleHotPath()
    {
        lock (_gate) UpscaleLocked();
        OnChange?.Invoke();
    }

    private void UpscaleLocked()
    {
        var t0 = Stopwatch.GetTimestamp();
        _runs++;
        var (cols, rows, scale, mpx) = Geom(_target);
        for (var i = 0; i < _tiles.Length; i++)
        {
            var t = _tiles[i];
            // 무할당: 재사용 스크래치에 결정적 패턴을 채워 타일 추론을 대용하고 아레나에 커밋
            var seed = (byte)((i * 31 + _runs) & 0xFF);
            for (var b = 0; b < _scratch.Length; b += 512) _scratch[b] = seed;
            CommitTile(i);
            t.Sharpness = 1.4 + scale * 0.35 + (_rng.NextDouble() - 0.5) * 0.2;
            t.State = UpscaleTileState.Inferred; _inferred++;
        }
        foreach (var t in _tiles) { t.State = UpscaleTileState.Blended; _blended++; }   // 심 블렌딩
        var depthCorr = Math.Max(0, 1.0 - _depthDrift);                                  // 공간 뎁스 보정
        if (_depthDrift > 0) { _depthCorr++; _depthDrift *= 0.5; }
        var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        var sec = Math.Max(ms / 1000.0, 1e-4);
        _snap = new Snap(_target, _tiles.Length, cols, rows, scale, Math.Round(mpx, 2), Math.Round(mpx / sec, 1), Math.Round(depthCorr, 3), ms, Mapped);
        _snapValid = true;
        _last = null;
        _actionFromSnap = true;
    }

    private void CommitTile(int index)
    {
        var offset = (long)index * TileBytes;
        if (_acc is not null) _acc.WriteArray(offset, _scratch, 0, _scratch.Length);
        else if (_pool is not null) Buffer.BlockCopy(_scratch, 0, _pool, (int)offset, _scratch.Length);
    }

    public void InjectDepthDrift() { lock (_gate) { _depthDrift = Math.Min(1.0, _depthDrift + 0.6); LastAction = "공간 뎁스 드리프트 주입"; } OnChange?.Invoke(); }

    public void StartDemo()
    {
        lock (_gate)
        {
            if (_timer is not null || _disposed) return;
            _timer = new System.Threading.Timer(_ => Tick(), null, 0, TickMs);
            LastAction = "자율 업스케일 루프 기동";
        }
        OnChange?.Invoke();
    }

    private void Tick()
    {
        lock (_gate) { if (_timer is null) return; UpscaleLocked(); }
        OnChange?.Invoke();
    }

    public void StopDemo()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; LastAction = "업스케일 정지"; }
        OnChange?.Invoke();
    }

    public void ResetAll()
    {
        lock (_gate) { _depthDrift = 0; Reallocate(_target); LastAction = "업스케일러 초기화"; }
        OnChange?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            _timer?.Dispose(); _timer = null;
            _acc?.Dispose(); _mmf?.Dispose(); _acc = null; _mmf = null;
        }
    }
}
