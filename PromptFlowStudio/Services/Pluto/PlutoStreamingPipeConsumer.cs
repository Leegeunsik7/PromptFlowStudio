using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🌊 스트림 종료 사유.</summary>
public enum PlutoStreamEnd : byte
{
    /// <summary>진행 중/미시작.</summary>
    None = 0,
    /// <summary><c>data: [DONE]</c> · Ollama <c>"done":true</c> · <c>finish_reason</c> 또는 대상 배열 종료.</summary>
    Done = 1,
    /// <summary>종결 표식 없이 본문 끝.</summary>
    EndOfStream = 2,
    /// <summary>소켓 오류 · 무응답(유휴 타임아웃) · 총 예산/크기 상한 초과 — 봉합 후 구제.</summary>
    Truncated = 3,
    /// <summary>호출자 취소.</summary>
    Canceled = 4,
}

/// <summary>🌊 스트림 1회 진단(호출자 소유 · 단일 스레드 기록) — TTFT · 요소 수 · 종료 사유.</summary>
public sealed class PlutoStreamReport
{
    public PlutoStreamEnd End { get; internal set; }
    /// <summary>소비한 원시 바이트(SSE 프레임 포함).</summary>
    public long Bytes { get; internal set; }
    /// <summary>채택 요소 수.</summary>
    public int Elements { get; internal set; }
    /// <summary>절단 봉합으로 구제된 요소 수.</summary>
    public int Healed { get; internal set; }
    /// <summary>첫 요소 방출까지 경과(ms) — 없으면 -1.</summary>
    public long FirstElementMs { get; internal set; } = -1;
    /// <summary>실시간 SSE/NDJSON 스트림 경유 여부(false = 배치 응답 재생).</summary>
    public bool Streamed { get; internal set; }
}

/// <summary>
/// 🌊 Zero-Alloc SSE 파이프 소비기 — <see cref="PipeReader"/>(4KiB 풀 세그먼트) + <see cref="SequenceReader{T}"/>로 소켓 바이트를 줄 단위 프레이밍.
///   ▸ SSE <c>data: {…}</c>(이벤트 경계 <c>\n\n</c>·<c>\r\n\r\n</c> — 빈 줄은 건너뜀) 와 Ollama NDJSON(<c>{…}\n</c>) 동시 지원 · <c>:</c> 주석/<c>event:</c>/<c>id:</c> 무시.
///   ▸ 줄이 완성된 뒤에만 파싱 → 네트워크 청크가 UTF-8 멀티바이트 중간을 갈라도 안전(재조립은 파이프가 담당).
///   ▸ 스택 <see cref="Utf8JsonReader"/>로 OpenAI <c>choices[0].delta.content</c> · Ollama <c>message.content</c>만 추출 → 싱크에 UTF-8 스팬 직접 전달(문자열 0).
///   ▸ 종결: <c>data: [DONE]</c> · <c>"done":true</c> · <c>finish_reason</c>.
/// 줄 파싱 경로(<see cref="ProcessBuffer"/>)는 할당 0(이스케이프 포함 512B 초과 델타만 풀 대여).
/// </summary>
public static class PlutoStreamingPipeConsumer
{
    public const int BufferSize = 4096;
    /// <summary>단일 SSE 줄 상한 — 초과 시 절단 처리.</summary>
    public const int MaxLineBytes = 256 * 1024;
    /// <summary>스트림 원시 바이트 상한(토큰당 프레임 오버헤드 포함).</summary>
    public const long MaxStreamBytes = 32L * 1024 * 1024;
    private const int StackDeltaBytes = 512;

    private static readonly StreamPipeReaderOptions ReaderOptions = new(bufferSize: BufferSize, leaveOpen: false);

    /// <summary>
    /// 완성된 줄을 모두 소비해 델타를 싱크로 전달. <paramref name="isCompleted"/>면 개행 없는 마지막 줄도 처리.
    /// </summary>
    /// <returns>소비 위치(이후는 미완성 줄 — 다음 읽기에서 재검사).</returns>
    public static SequencePosition ProcessBuffer(in ReadOnlySequence<byte> buffer, bool isCompleted, IPlutoStreamDeltaSink sink, out bool done)
    {
        done = false;
        var reader = new SequenceReader<byte>(buffer);
        while (!done && reader.TryReadTo(out ReadOnlySequence<byte> line, (byte)'\n', advancePastDelimiter: true))
            done = ProcessLine(line, sink);
        if (!done && isCompleted && !reader.End)
        {
            done = ProcessLine(buffer.Slice(reader.Position), sink);
            return buffer.End;
        }
        return reader.Position;
    }

    /// <summary>🌊 스트림 전체를 싱크로 배수 — 예외 없음(호출자 취소 포함 · 사유는 반환값).</summary>
    public static async ValueTask<PlutoStreamEnd> ConsumeAsync(Stream stream, IPlutoStreamDeltaSink sink, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(sink);
        var reader = PipeReader.Create(stream, ReaderOptions);
        try
        {
            long total = 0;
            while (true)
            {
                var (ok, result) = await TryReadAsync(reader, ct).ConfigureAwait(false);
                if (!ok) return ct.IsCancellationRequested ? PlutoStreamEnd.Canceled : PlutoStreamEnd.Truncated;
                var buffer = result.Buffer;
                var consumed = ProcessBuffer(buffer, result.IsCompleted, sink, out var done);
                total += buffer.Slice(0, consumed).Length;
                var pending = buffer.Slice(consumed).Length;
                reader.AdvanceTo(consumed, buffer.End);
                if (done) return PlutoStreamEnd.Done;
                if (result.IsCompleted) return PlutoStreamEnd.EndOfStream;
                if (pending > MaxLineBytes || total > MaxStreamBytes) return PlutoStreamEnd.Truncated;
            }
        }
        finally { await CompleteQuietlyAsync(reader).ConfigureAwait(false); }
    }

    /// <summary>
    /// 🌊 SSE 스트림 → 배열 요소가 닫히는 즉시 도메인 모델 방출(후속 요소 대기 없음). 대상 배열이 닫히면 남은 스트림을 읽지 않고 종료.
    /// 절단(소켓 오류 · 유휴 타임아웃 · 취소) 시 진행 중 요소를 봉합해 구제 후 정상 종료 — 예외 없음(취소 판정은 <see cref="PlutoStreamReport.End"/>/호출자 토큰으로).
    /// </summary>
    /// <param name="watchdog">유휴 감시 CTS(<paramref name="ct"/>의 원천) — 청크 수신마다 <paramref name="idleTimeout"/>으로 재무장.</param>
    /// <param name="totalBudget">전체 상한(유휴 재무장과 무관한 절대 예산).</param>
    public static async IAsyncEnumerable<T> StreamArrayAsync<T>(Stream stream, PlutoJsonElementParser<T> parser, PlutoStreamReport? report = null,
        CancellationTokenSource? watchdog = null, TimeSpan idleTimeout = default, TimeSpan totalBudget = default,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(parser);
        report ??= new PlutoStreamReport();
        report.Streamed = true;
        var t0 = Environment.TickCount64;
        var deadline = totalBudget > TimeSpan.Zero ? t0 + (long)totalBudget.TotalMilliseconds : long.MaxValue;
        var reader = PipeReader.Create(stream, ReaderOptions);
        var asm = new PlutoIncrementalJsonAssembler<T>(parser);
        try
        {
            var end = PlutoStreamEnd.None;
            while (end == PlutoStreamEnd.None)
            {
                var (ok, result) = await TryReadAsync(reader, ct).ConfigureAwait(false);
                if (!ok) { end = PlutoStreamEnd.Truncated; break; }
                var buffer = result.Buffer;
                var consumed = ProcessBuffer(buffer, result.IsCompleted, asm, out var done);
                report.Bytes += buffer.Slice(0, consumed).Length;
                var pending = buffer.Slice(consumed).Length;
                reader.AdvanceTo(consumed, buffer.End);

                while (asm.TryDequeue(out var item))
                {
                    if (report.FirstElementMs < 0) report.FirstElementMs = Environment.TickCount64 - t0;
                    report.Elements++;
                    yield return item;   // ⚡ 요소 종료 즉시 — 다음 청크를 기다리지 않음
                }

                if (done || asm.IsArrayClosed) end = PlutoStreamEnd.Done;
                else if (result.IsCompleted) end = PlutoStreamEnd.EndOfStream;
                else if (pending > MaxLineBytes || report.Bytes > MaxStreamBytes) end = PlutoStreamEnd.Truncated;
                else if (!Rearm(watchdog, idleTimeout, deadline)) end = PlutoStreamEnd.Truncated;
            }

            asm.Complete();   // 🩹 절단 시 미닫힘 괄호 봉합 · 정상 종료면 무동작
            while (asm.TryDequeue(out var item))
            {
                if (report.FirstElementMs < 0) report.FirstElementMs = Environment.TickCount64 - t0;
                report.Elements++;
                yield return item;
            }
            report.Healed = asm.ElementsHealed;
            report.End = end;
        }
        finally
        {
            if (report.End == PlutoStreamEnd.None) report.End = PlutoStreamEnd.Canceled;   // 소비자가 열거 중단
            asm.Dispose();
            await CompleteQuietlyAsync(reader).ConfigureAwait(false);
        }
    }

    // ── 내부 ──

    /// <summary>유휴 감시 재무장 — 총 예산 소진/감시 CTS 폐기 시 false.</summary>
    private static bool Rearm(CancellationTokenSource? watchdog, TimeSpan idle, long deadline)
    {
        var remaining = deadline == long.MaxValue ? long.MaxValue : deadline - Environment.TickCount64;
        if (remaining <= 0) return false;
        if (watchdog is null || idle <= TimeSpan.Zero) return true;
        var ms = Math.Min((long)idle.TotalMilliseconds, remaining);
        try { watchdog.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, ms))); return true; }
        catch (ObjectDisposedException) { return false; }
    }

    private static async ValueTask<(bool Ok, ReadResult Result)> TryReadAsync(PipeReader reader, CancellationToken ct)
    {
        try { return (true, await reader.ReadAsync(ct).ConfigureAwait(false)); }
        catch (OperationCanceledException) { return (false, default); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Stream] read cut: " + ex.Message);
            return (false, default);
        }
    }

    private static async ValueTask CompleteQuietlyAsync(PipeReader reader)
    {
        try { await reader.CompleteAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Stream] complete: " + ex.Message);
        }
    }

    /// <summary>줄 1개 처리 — 종결 표식이면 true.</summary>
    private static bool ProcessLine(ReadOnlySequence<byte> line, IPlutoStreamDeltaSink sink)
    {
        var r = new SequenceReader<byte>(line);
        ReadOnlySequence<byte> payload;
        if (r.IsNext("data:"u8, advancePast: true))
        {
            r.AdvancePast((byte)' ');
            payload = line.Slice(r.Position);
        }
        else if (r.IsNext((byte)'{')) payload = line;   // Ollama NDJSON
        else return false;                              // 빈 줄(이벤트 경계) · ':' 주석 · event:/id:/retry:

        if (payload.Length <= 16)
        {
            Span<byte> tmp = stackalloc byte[16];
            payload.CopyTo(tmp);
            if (tmp.Slice(0, (int)payload.Length).Trim((byte)' ').TrimEnd((byte)'\r').SequenceEqual("[DONE]"u8)) return true;
        }
        return ExtractDelta(payload, sink);
    }

    /// <summary>프레임 JSON에서 델타 본문만 추출해 싱크 전달 — 종결 신호면 true. 깨진 프레임은 건너뜀.</summary>
    private static bool ExtractDelta(ReadOnlySequence<byte> payload, IPlutoStreamDeltaSink sink)
    {
        var reader = new Utf8JsonReader(payload, isFinalBlock: true, state: default);
        var done = false;
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("choices"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); continue; }
                    var first = true;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (first && reader.TokenType == JsonTokenType.StartObject) done |= ReadChoice(ref reader, sink);
                        else reader.Skip();
                        first = false;
                    }
                }
                else if (reader.ValueTextEquals("message"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.StartObject) ReadContentObject(ref reader, sink);
                    else reader.Skip();
                }
                else if (reader.ValueTextEquals("done"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.True) done = true;
                    else reader.Skip();
                }
                else { reader.Read(); reader.Skip(); }
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return done;
    }

    /// <summary><c>choices[0]</c> 객체 — <c>delta.content</c>(스트림) · <c>message.content</c>(비스트림 호환) · <c>finish_reason</c>.</summary>
    private static bool ReadChoice(ref Utf8JsonReader reader, IPlutoStreamDeltaSink sink)
    {
        var done = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("delta"u8) || reader.ValueTextEquals("message"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartObject) ReadContentObject(ref reader, sink);
                else reader.Skip();
            }
            else if (reader.ValueTextEquals("finish_reason"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String) done = true;
                else reader.Skip();
            }
            else { reader.Read(); reader.Skip(); }
        }
        return done;
    }

    private static void ReadContentObject(ref Utf8JsonReader reader, IPlutoStreamDeltaSink sink)
    {
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isContent = reader.ValueTextEquals("content"u8);
            reader.Read();
            if (isContent && reader.TokenType == JsonTokenType.String) EmitString(ref reader, sink);
            else reader.Skip();
        }
    }

    /// <summary>JSON 문자열 값 → 언이스케이프 UTF-8 스팬. 이스케이프 없는 단일 세그먼트는 무복사.</summary>
    private static void EmitString(ref Utf8JsonReader reader, IPlutoStreamDeltaSink sink)
    {
        if (!reader.HasValueSequence && !reader.ValueIsEscaped)
        {
            if (!reader.ValueSpan.IsEmpty) sink.OnDelta(reader.ValueSpan);
            return;
        }
        var max = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
        if (max == 0) return;
        byte[]? rented = null;
        Span<byte> tmp = max <= StackDeltaBytes ? stackalloc byte[StackDeltaBytes] : (rented = ArrayPool<byte>.Shared.Rent(max));
        try
        {
            var n = reader.CopyString(tmp);
            if (n > 0) sink.OnDelta(tmp.Slice(0, n));
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
