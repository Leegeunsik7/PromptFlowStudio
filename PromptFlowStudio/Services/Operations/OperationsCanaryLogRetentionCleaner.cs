using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace PromptFlowStudio.Services.Operations;

/// <summary>세척 실행 1회 결과 - HUD/감사용 불변 레코드.</summary>
public sealed record RetentionCleanupResult(DateTimeOffset At, int FilesScanned, int FilesDeleted, long BytesFreed, IReadOnlyList<string> Errors);

/// <summary>
/// [PromptFlow Studio v11.0.1-LTS-AuditPatch] 카나리/OTLP 로그 보존 정책 세척기 — %LocalAppData%\PromptFlowStudio\logs
///   내 지정 패턴(기본 <c>*.otlp.log</c>·<c>canary-*.log</c>)에 매칭되는 파일 중 보존기간(기본 7일)을 초과한
///   것만 원자적으로(<see cref="File.Delete(string)"/>, 파일 시스템 단일 연산) 삭제한다. 패턴 밖 파일(예: 기존
///   shell.log)은 절대 건드리지 않는다.
///   ⚠ 실측 고지: 이 시점 OperationsCanaryHealthChecker/OtlpTelemetryVisualizerProvider 는 모두 순수 인메모리
///   (CircularBuffer)라 위 패턴에 매칭되는 실 로그 파일이 아직 존재하지 않는다 — 이 클리너는 향후 파일 기반
///   로깅이 추가될 때를 대비한 인프라이며, 현재는 합성 픽스처로만 로직이 검증된다(대상 없으면 Graceful 0건).
///   ▸ MMF 고정 링(최근 32회 세척 이력, 슬롯당 고정폭) — 프로세스 재기동 후에도 최근 세척 이력이 파일 시스템에
///     남아 진단 가능(이 프로젝트의 CircularBuffer&lt;T&gt; 관례를 MMF 백엔드로 구현).
/// </summary>
public sealed class OperationsCanaryLogRetentionCleaner : IDisposable
{
    public static string LogsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "logs");
    public static readonly IReadOnlyList<string> DefaultPatterns = new[] { "*.otlp.log", "canary-*.log" };
    public const int RetentionDays = 7;
    private const int RingSlots = 32;
    private const int SlotBytes = 32;   // timestampMs(8)+filesScanned(4)+filesDeleted(4)+bytesFreed(8)+errorCount(4)+여유(4)

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewStream _stream;
    private readonly object _gate = new();
    private int _ringHead;
    private long _totalRuns, _totalDeleted, _totalBytesFreed;
    private RetentionCleanupResult? _lastResult;
    private bool _disposed;

    public event Action? OnChange;
    public string? LastAction { get; private set; }
    public long TotalRuns => Interlocked.Read(ref _totalRuns);
    public long TotalDeleted => Interlocked.Read(ref _totalDeleted);
    public long TotalBytesFreed => Interlocked.Read(ref _totalBytesFreed);
    public RetentionCleanupResult? LastResult { get { lock (_gate) return _lastResult; } }

    public OperationsCanaryLogRetentionCleaner(string? mmfName = null)
    {
        // v12.2.0: 뷰 생성 실패 시 앞서 연 매핑 핸들 즉시 반환(이전엔 필드 대입 전 예외로 종료자까지 잔류).
        var mmf = OperatingSystem.IsWindows()
            ? MemoryMappedFile.CreateOrOpen(mmfName ?? "Local\\PromptFlowStudio_CanaryRetention_Ring_v1", (long)SlotBytes * RingSlots, MemoryMappedFileAccess.ReadWrite)
            : MemoryMappedFile.CreateNew(null, (long)SlotBytes * RingSlots, MemoryMappedFileAccess.ReadWrite);   // v17.63.2: 비-Windows 는 명명 매핑 미지원 → 익명(프로세스 내) 매핑 폴백
        try { _stream = mmf.CreateViewStream(0, (long)SlotBytes * RingSlots, MemoryMappedFileAccess.ReadWrite); }
        catch { mmf.Dispose(); throw; }
        _mmf = mmf;
    }

    /// <summary>보존기간 초과 파일을 세척(개별 파일 실패는 격리 - 나머지 계속 진행). 폴더 없으면 0건으로 Graceful 반환.
    ///   <paramref name="logsRootOverride"/> 는 테스트 전용(운영 경로는 <see cref="LogsRoot"/> 고정).</summary>
    public RetentionCleanupResult RunCleanup(string? logsRootOverride = null, IReadOnlyList<string>? patterns = null,
        int retentionDays = RetentionDays, DateTimeOffset? nowOverride = null)
    {
        var root = logsRootOverride ?? LogsRoot;
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        var cutoffUtc = now.UtcDateTime - TimeSpan.FromDays(retentionDays);
        var pats = patterns ?? DefaultPatterns;

        var scanned = 0; var deleted = 0; long freed = 0;
        var errors = new List<string>();

        if (Directory.Exists(root))
        {
            foreach (var pattern in pats)
            {
                // v12.2.0: 열거 자체의 예외(폴더 동시 삭제·권한 변경·잘못된 패턴)는 이전엔 try 밖이라 세척 전체가 예외로 중단됐다 - 패턴 단위로 격리.
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, pattern); }
                catch (Exception ex) { errors.Add($"[{pattern}] 열거 실패: {ex.GetType().Name}"); continue; }
                using var e = files.GetEnumerator();
                while (true)
                {
                    string file;
                    try { if (!e.MoveNext()) break; file = e.Current; }
                    catch (Exception ex) { errors.Add($"[{pattern}] 열거 중단: {ex.GetType().Name}"); break; }
                    scanned++;
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.LastWriteTimeUtc >= cutoffUtc) continue;   // 보존기간 이내 - 유지
                        var bytes = info.Length;
                        File.Delete(file);
                        deleted++;
                        freed += bytes;
                    }
                    catch (Exception ex) { errors.Add($"{Path.GetFileName(file)}: {ex.GetType().Name}"); }
                }
            }
        }

        var result = new RetentionCleanupResult(now, scanned, deleted, freed, errors);
        Interlocked.Increment(ref _totalRuns);
        Interlocked.Add(ref _totalDeleted, deleted);
        Interlocked.Add(ref _totalBytesFreed, freed);
        lock (_gate)
        {
            _lastResult = result;
            LastAction = $"세척 완료 - 스캔 {scanned} · 삭제 {deleted} · 확보 {freed:N0}B" + (errors.Count > 0 ? $" · 오류 {errors.Count}" : "");
        }
        WriteRingSlotLocked(result);
        OnChange?.Invoke();
        return result;
    }

    // MMF 고정 링 - 최근 세척 이력을 슬롯당 고정폭으로 기록(재기동 후에도 최근 이력 진단 가능).
    private void WriteRingSlotLocked(RetentionCleanupResult r)
    {
        lock (_gate)
        {
            if (_disposed) return;   // v12.2.0: Dispose 이후 세척 호출 시 해제된 뷰 접근(ObjectDisposedException) 차단 - 결과는 정상 반환
            _stream.Position = _ringHead * SlotBytes;
            Span<byte> buf = stackalloc byte[SlotBytes];
            BinaryPrimitives.WriteInt64LittleEndian(buf[0..8], r.At.ToUnixTimeMilliseconds());
            BinaryPrimitives.WriteInt32LittleEndian(buf[8..12], r.FilesScanned);
            BinaryPrimitives.WriteInt32LittleEndian(buf[12..16], r.FilesDeleted);
            BinaryPrimitives.WriteInt64LittleEndian(buf[16..24], r.BytesFreed);
            BinaryPrimitives.WriteInt32LittleEndian(buf[24..28], r.Errors.Count);
            _stream.Write(buf);
            _stream.Flush();
            _ringHead = (_ringHead + 1) % RingSlots;
        }
    }

    public void ResetAll()
    {
        lock (_gate) { _lastResult = null; _ringHead = 0; LastAction = "세척기 초기화"; }
        Interlocked.Exchange(ref _totalRuns, 0);
        Interlocked.Exchange(ref _totalDeleted, 0);
        Interlocked.Exchange(ref _totalBytesFreed, 0);
        OnChange?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)   // v12.2.0: 링 기록과 해제를 같은 락으로 직렬화 - 기록 도중 뷰가 해제되는 경합 차단
        {
            if (_disposed) return;
            _disposed = true;
            _stream.Dispose();
            _mmf.Dispose();
        }
    }
}