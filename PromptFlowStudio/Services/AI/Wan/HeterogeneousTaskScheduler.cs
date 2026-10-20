using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace PromptFlowStudio.Services.AI.Wan;

public enum DeviceRole { Primary, Secondary }
public enum DeviceClass { DiscreteGpu, IntegratedGpu, SoftwareOrUnknown }

/// <summary>탐지된 그래픽 어댑터 1건.</summary>
public sealed record GpuDeviceInfo(int Index, string Description, DeviceClass Class);

/// <summary>스케줄러 관제 스냅샷.</summary>
public sealed record SchedulerReport(int DetectedGpus, bool DualGpuMode, long PrimaryTasks, long SecondaryTasks, long Migrations, double LastMigrationMs);

/// <summary>
/// [PromptFlow Studio v10.6.0-F2] 이종 하드웨어 태스크 스케줄러 - Wan 2.1 파이프라인의 메인 추론(ComfyUi/DirectML
///   커넥터)과 후처리(WanPostProcessingEngine/WanProgressivePreviewer)를 별도 디바이스로 분산 라우팅한다.
///   GPU 탐지: 원 지시서는 WMI(ManagementObjectSearcher) 또는 DXGI P/Invoke를 제안했으나, 둘 다 이 요구에 비해
///     리스크가 크다 - WMI(System.Management)는 .NET 8에서 NuGet 패키지 참조가 필요해 이 프로젝트의 제로-NuGet
///     원칙과 정면으로 충돌하고, DXGI COM v테이블 P/Invoke는 선언을 한 글자라도 잘못하면 관리되지 않는 접근 위반
///     (크래시)으로 이어져 try/catch 로도 못 막는다. 대신 Windows 레지스트리의 디스플레이 어댑터 클래스를 순수
///     BCL(Microsoft.Win32.Registry, 이미 OS 에 설치된 드라이버가 실제로 채워둔 값)로 열거해 동일한 목적(다중
///     GPU 존재 여부/설명 문자열)을 제로-NuGet, 크래시 없이 달성한다.
///   실행 라우팅은 Task.Run(스레드풀) 기반 - "Primary/Secondary 디바이스"는 실제 별도 GPU 컨텍스트가 아니라
///     논리적 라우팅 레이블이다(순수 BCL이라 실제 디바이스 바인딩 API 없음, 스텁 결선점). 단일 GPU/탐지 실패 시
///     Secondary 요청도 Primary 로 합류해 동일한 스레드풀 비동기 파이프라인으로 Graceful 폴백한다.
///   디바이스 간 메모리 마이그레이션은 SimulateMigration(MMF 고정 아레나 재사용, 안정상태 무할당)로 대용한다.
///   v10.7.0-Wan22: 결과 반환이 필요한 작업(오디오 FFT/특징 추출 등)을 위해 제네릭 오버로드
///     (RunOnPrimaryAsync/RunOnSecondaryAsync)를 추가 - 무반환 오버로드와 동일한 라우팅/카운터/이벤트 경로를
///     공유하는 단일 제네릭 내부 헬퍼로 구현(중복 로직 없음).
/// </summary>
public sealed class HeterogeneousTaskScheduler : IDisposable
{
    private const int MigrationBytes = 64 * 1024;

    private readonly object _gate = new();
    private readonly byte[] _migrationScratch = new byte[MigrationBytes];
    private MemoryMappedFile? _migMmf;
    private MemoryMappedViewAccessor? _migAcc;
    private byte[]? _migPool;
    private IReadOnlyList<GpuDeviceInfo> _devices = Array.Empty<GpuDeviceInfo>();
    private bool _dualGpu;
    private long _primaryTasks, _secondaryTasks, _migrations;
    private double _lastMigrationMs;
    private bool _disposed;

    public event Action? OnChange;
    public string? LastAction { get; private set; }
    public IReadOnlyList<GpuDeviceInfo> Devices { get { lock (_gate) return _devices; } }
    public bool DualGpuMode { get { lock (_gate) return _dualGpu; } }
    public long PrimaryTasks => Interlocked.Read(ref _primaryTasks);
    public long SecondaryTasks => Interlocked.Read(ref _secondaryTasks);
    public long Migrations => Interlocked.Read(ref _migrations);
    public SchedulerReport Report { get { lock (_gate) return new SchedulerReport(_devices.Count, _dualGpu, PrimaryTasks, SecondaryTasks, Migrations, _lastMigrationMs); } }

    public HeterogeneousTaskScheduler()
    {
        InitMigrationArena();
        DetectDevices();
    }

    private void InitMigrationArena()
    {
        try
        {
            _migMmf = MemoryMappedFile.CreateNew(Guid.NewGuid().ToString("N"), MigrationBytes);
            _migAcc = _migMmf.CreateViewAccessor(0, MigrationBytes);
        }
        catch (Exception) { _migPool = new byte[MigrationBytes]; }
    }

    private void DetectDevices()
    {
        var list = new List<GpuDeviceInfo>();
#if WINDOWS
        try
        {
            using var displayClass = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (displayClass is not null)
            {
                foreach (var sub in displayClass.GetSubKeyNames())
                {
                    if (!int.TryParse(sub, out _)) continue;
                    using var key = displayClass.OpenSubKey(sub);
                    if (key?.GetValue("DriverDesc") is not string desc || string.IsNullOrWhiteSpace(desc)) continue;
                    list.Add(new GpuDeviceInfo(list.Count, desc, ClassifyDevice(desc)));
                }
            }
        }
        catch (Exception ex) { Program.Log("HeteroScheduler.Detect", ex); }
#endif
        lock (_gate)
        {
            _devices = list;
            _dualGpu = list.Count >= 2;
            LastAction = list.Count == 0
                ? "GPU 탐지 결과 없음 - 단일 스레드풀 폴백 모드"
                : $"{list.Count}개 어댑터 탐지 - {(_dualGpu ? "이종 듀얼GPU 라우팅 모드" : "단일GPU 폴백 모드")}";
        }
        OnChange?.Invoke();
    }

    private static DeviceClass ClassifyDevice(string desc)
    {
        var d = desc.ToUpperInvariant();
        if (d.Contains("MICROSOFT BASIC")) return DeviceClass.SoftwareOrUnknown;
        if (d.Contains("INTEL") && (d.Contains("HD GRAPHICS") || d.Contains("UHD") || d.Contains("IRIS"))) return DeviceClass.IntegratedGpu;
        if (d.Contains("AMD") && d.Contains("RADEON") && d.Contains("GRAPHICS") && !d.Contains(" RX ") && !d.EndsWith(" RX")) return DeviceClass.IntegratedGpu;
        return DeviceClass.DiscreteGpu;
    }

    public DeviceRole SecondaryRouteTarget => DualGpuMode ? DeviceRole.Secondary : DeviceRole.Primary;

    public Task RunOnPrimaryAsync(Func<CancellationToken, Task> work, string kind, CancellationToken ct = default)
        => RunRoutedAsync(DeviceRole.Primary, work, kind, ct);

    public Task RunOnSecondaryAsync(Func<CancellationToken, Task> work, string kind, CancellationToken ct = default)
        => RunRoutedAsync(SecondaryRouteTarget, work, kind, ct);

    public Task<T> RunOnPrimaryAsync<T>(Func<CancellationToken, Task<T>> work, string kind, CancellationToken ct = default)
        => RunRoutedAsync(DeviceRole.Primary, work, kind, ct);

    public Task<T> RunOnSecondaryAsync<T>(Func<CancellationToken, Task<T>> work, string kind, CancellationToken ct = default)
        => RunRoutedAsync(SecondaryRouteTarget, work, kind, ct);

    private Task RunRoutedAsync(DeviceRole role, Func<CancellationToken, Task> work, string kind, CancellationToken ct)
        => RunRoutedAsync<object?>(role, async c => { await work(c).ConfigureAwait(false); return null; }, kind, ct);

    private async Task<T> RunRoutedAsync<T>(DeviceRole role, Func<CancellationToken, Task<T>> work, string kind, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = await Task.Run(() => work(ct), ct).ConfigureAwait(false);
        sw.Stop();
        if (role == DeviceRole.Primary) Interlocked.Increment(ref _primaryTasks); else Interlocked.Increment(ref _secondaryTasks);
        lock (_gate) LastAction = $"{kind} -> {role} 실행 완료 - {sw.Elapsed.TotalMilliseconds:0.##}ms";
        OnChange?.Invoke();
        return result;
    }

    public double SimulateMigration(ReadOnlySpan<byte> data)
    {
        var sw = Stopwatch.StartNew();
        lock (_gate)
        {
            var take = Math.Min(data.Length, MigrationBytes);
            data[..take].CopyTo(_migrationScratch);
            if (take < MigrationBytes) _migrationScratch.AsSpan(take).Clear();
            if (_migAcc is not null) _migAcc.WriteArray(0, _migrationScratch, 0, MigrationBytes);
            else if (_migPool is not null) _migrationScratch.CopyTo(_migPool.AsSpan());
        }
        sw.Stop();
        Interlocked.Increment(ref _migrations);
        lock (_gate) _lastMigrationMs = sw.Elapsed.TotalMilliseconds;
        OnChange?.Invoke();
        return sw.Elapsed.TotalMilliseconds;
    }

    public void ResetAll()
    {
        Interlocked.Exchange(ref _primaryTasks, 0);
        Interlocked.Exchange(ref _secondaryTasks, 0);
        Interlocked.Exchange(ref _migrations, 0);
        lock (_gate) { _lastMigrationMs = 0; LastAction = "스케줄러 초기화 - 재탐지 중"; }
        DetectDevices();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) { _migAcc?.Dispose(); _migMmf?.Dispose(); _migAcc = null; _migMmf = null; }
    }
}