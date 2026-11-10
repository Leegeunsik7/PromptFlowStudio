using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🌊 Zero-Alloc SSE 파이프 소비기 + In-Flight 증분 JSON 어셈블러 + 페일오버/오케스트레이터 스트리밍 결선.</summary>
public sealed class PlutoStreamingPipelineTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions RawUtf8 = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ── 하네스 ──

    /// <summary>테스트가 청크 도착 시점을 통제하는 스트림(고정 지연 없음) — 종료/소켓 절단 모사.</summary>
    private sealed class GatedStream : Stream
    {
        private readonly Channel<byte[]> _ch = Channel.CreateUnbounded<byte[]>();
        private byte[]? _cur;
        private int _pos;
        private volatile bool _fail;
        public int Disposed;

        public void Push(byte[] chunk) => _ch.Writer.TryWrite(chunk);
        public void Push(string text) => Push(Encoding.UTF8.GetBytes(text));
        public void End() => _ch.Writer.TryComplete();
        public void Cut() { _fail = true; _ch.Writer.TryComplete(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_cur is null || _pos >= _cur.Length)
            {
                try { _cur = await _ch.Reader.ReadAsync(ct).ConfigureAwait(false); _pos = 0; }
                catch (ChannelClosedException)
                {
                    if (_fail) throw new IOException("connection reset by peer");
                    return 0;
                }
            }
            var n = Math.Min(buffer.Length, _cur.Length - _pos);
            _cur.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        protected override void Dispose(bool disposing) { Interlocked.Exchange(ref Disposed, 1); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CollectSink : IPlutoStreamDeltaSink
    {
        public readonly MemoryStream Bytes = new();
        public int Calls;
        public void OnDelta(ReadOnlySpan<byte> utf8) { Bytes.Write(utf8); Calls++; }
    }

    private sealed class CountSink : IPlutoStreamDeltaSink
    {
        public long Total;
        public void OnDelta(ReadOnlySpan<byte> utf8) => Total += utf8.Length;
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex) { Memory = memory; RunningIndex = runningIndex; }
        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }

    private sealed class StreamHandler : HttpMessageHandler
    {
        public Func<string, HttpResponseMessage> Respond { get; init; } = static _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public readonly List<(string Path, string Body)> Requests = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var path = request.RequestUri!.AbsolutePath;
            lock (Requests) Requests.Add((path, body));
            return Respond(path);
        }
    }

    private static string OpenAiFrame(string delta, bool rawUtf8 = false, string boundary = "\n\n")
        => "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":"
           + JsonSerializer.Serialize(delta, rawUtf8 ? RawUtf8 : null) + "},\"finish_reason\":null}]}" + boundary;

    private static string OllamaLine(string delta)
        => "{\"model\":\"m\",\"created_at\":\"2026-11-11T00:00:00Z\",\"message\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(delta, RawUtf8) + "},\"done\":false}\n";

    /// <summary>본문을 1~3 룬 단위 델타로 쪼개기(서로게이트 쌍 보존).</summary>
    private static List<string> Deltas(string content)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        var take = 1;
        var n = 0;
        foreach (var rune in content.EnumerateRunes())
        {
            sb.Append(rune.ToString());
            if (++n < take) continue;
            list.Add(sb.ToString());
            sb.Clear();
            n = 0;
            take = take % 3 + 1;
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    /// <summary>OpenAI SSE(이스케이프/원시 UTF-8 · LF/CRLF 경계) + Ollama NDJSON + 주석/이벤트 줄 혼합 와이어.</summary>
    private static byte[] MixedWire(string content, bool terminate = true)
    {
        var sb = new StringBuilder();
        sb.Append(": keep-alive\n\n");
        var i = 0;
        foreach (var d in Deltas(content))
        {
            switch (i++ % 4)
            {
                case 0: sb.Append(OpenAiFrame(d)); break;                                   // \uXXXX 이스케이프
                case 1: sb.Append(OpenAiFrame(d, rawUtf8: true, boundary: "\r\n\r\n")); break; // 원시 멀티바이트 + CRLF 경계
                case 2: sb.Append("event: message\n").Append(OpenAiFrame(d, rawUtf8: true)); break;
                default: sb.Append(OllamaLine(d)); break;
            }
        }
        if (terminate) sb.Append("data: [DONE]\n\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static GatedStream Fragmented(byte[] wire, int[] sizes)
    {
        var s = new GatedStream();
        var pos = 0;
        var k = 0;
        while (pos < wire.Length)
        {
            var n = Math.Min(sizes[k++ % sizes.Length], wire.Length - pos);
            s.Push(wire.AsSpan(pos, n).ToArray());
            pos += n;
        }
        s.End();
        return s;
    }

    private static long MeasureProcessBufferAllocations(ReadOnlySequence<byte> wire, CountSink sink, int iterations)
    {
        for (var i = 0; i < 8; i++) PlutoStreamingPipeConsumer.ProcessBuffer(wire, true, sink, out _);   // 워밍업(JIT · 풀)
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++) PlutoStreamingPipeConsumer.ProcessBuffer(wire, true, sink, out _);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static bool ParsePair(ReadOnlyMemory<byte> utf8, int ordinal, out (int Act, string Ko) item)
    {
        using var doc = JsonDocument.Parse(utf8);
        var r = doc.RootElement;
        item = (r.TryGetProperty("act", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : ordinal + 1,
                r.TryGetProperty("ko", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "");
        return true;
    }

    private static PlutoLocalGatewayOptions Enabled() => new() { BaseUrl = "http://localhost:8080", ModelName = "local-slm", IsEnabled = true, TimeoutMs = 5000 };

    private static HttpResponseMessage SseResponse(Stream body)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        resp.Content.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");
        return resp;
    }

    // ── 1. 파이프 소비기 ──

    [Fact]
    public async Task PipeConsumer_ExtractsDeltaTokens_ZeroAlloc_FromMixedSseChunks()
    {
        const string content = "[{\"act\":1,\"ko\":\"안녕, 우리 아기 🍼 \\\"첫 뒤집기\\\" 성공!\",\"en\":\"Hello — naïve café ☀️\\n\"},{\"act\":2,\"ko\":\"사랑해 💜\",\"en\":\"path C:\\\\tmp [x] {y}\"}]";
        var expected = Encoding.UTF8.GetBytes(content);
        var wire = MixedWire(content);

        // 임의 크기 조각(멀티바이트 UTF-8 중간 절단 포함) · 1바이트 조각 · 통짜 — 모두 바이트 동일 재구성
        foreach (var sizes in new[] { new[] { 1, 2, 3, 1, 5, 7, 11, 1, 64, 4096 }, new[] { 1 }, new[] { wire.Length } })
        {
            var sink = new CollectSink();
            var end = await PlutoStreamingPipeConsumer.ConsumeAsync(Fragmented(wire, sizes), sink).AsTask().WaitAsync(Wait);
            Assert.Equal(PlutoStreamEnd.Done, end);
            Assert.Equal(expected, sink.Bytes.ToArray());
            Assert.True(sink.Calls > 1);
        }

        // [DONE] 없이 본문 종료 · 마지막 NDJSON 줄에 개행 없음
        var tail = Encoding.UTF8.GetBytes(OllamaLine("끝🍼").TrimEnd('\n'));
        var noDone = new CollectSink();
        Assert.Equal(PlutoStreamEnd.EndOfStream, await PlutoStreamingPipeConsumer.ConsumeAsync(Fragmented(tail, new[] { 3 }), noDone).AsTask().WaitAsync(Wait));
        Assert.Equal("끝🍼", Encoding.UTF8.GetString(noDone.Bytes.ToArray()));

        // Ollama "done":true 종결 · 깨진 프레임/비 JSON 줄은 건너뜀(예외 없음)
        var ollama = Encoding.UTF8.GetBytes("data: {broken\n\nnot-a-frame\n" + OllamaLine("A") + "{\"message\":{\"content\":\"B\"},\"done\":true}\n" + OllamaLine("무시"));
        var od = new CollectSink();
        Assert.Equal(PlutoStreamEnd.Done, await PlutoStreamingPipeConsumer.ConsumeAsync(Fragmented(ollama, new[] { 5 }), od).AsTask().WaitAsync(Wait));
        Assert.Equal("AB", Encoding.UTF8.GetString(od.Bytes.ToArray()));

        // 소켓 절단 → 예외 대신 Truncated · 스트림 소유권(leaveOpen:false) 해제
        var cut = new GatedStream();
        cut.Push(OpenAiFrame("부분"));
        cut.Cut();
        var cs = new CollectSink();
        Assert.Equal(PlutoStreamEnd.Truncated, await PlutoStreamingPipeConsumer.ConsumeAsync(cut, cs).AsTask().WaitAsync(Wait));
        Assert.Equal("부분", Encoding.UTF8.GetString(cs.Bytes.ToArray()));
        Assert.Equal(1, cut.Disposed);

        // Zero-Alloc: 줄 프레이밍 + 스택 Utf8JsonReader 델타 추출(단일 세그먼트 · 7바이트 다중 세그먼트)
        var count = new CountSink();
        Assert.Equal(0L, MeasureProcessBufferAllocations(new ReadOnlySequence<byte>(wire), count, 50));
        var head = new Segment(wire.AsMemory(0, Math.Min(7, wire.Length)), 0);
        var last = head;
        for (var p = 7; p < wire.Length; p += 7) last = last.Append(wire.AsMemory(p, Math.Min(7, wire.Length - p)));
        var multi = new ReadOnlySequence<byte>(head, 0, last, last.Memory.Length);
        count.Total = 0;
        PlutoStreamingPipeConsumer.ProcessBuffer(multi, true, count, out var done);
        Assert.True(done);
        Assert.Equal((long)expected.Length, count.Total);
        Assert.Equal(0L, MeasureProcessBufferAllocations(multi, count, 50));
    }

    // ── 2. 요소 종료 즉시 방출 ──

    [Fact]
    public async Task IncrementalAssembler_EmitsObjectsImmediately_WhenArrayElementCloses()
    {
        const string item1 = "[{\"act\":1,\"ko\":\"처음 만난 날 [기억] {그대로}\",\"en\":\"The day we met\"}";
        const string item2Head = ",\n {\"act\":2,\"ko\":\"웃음이 번지던";
        const string item2Tail = " 오후\",\"en\":\"An afternoon of smiles\"}";
        var stream = new GatedStream();
        var report = new PlutoStreamReport();
        await using var e = PlutoStreamingPipeConsumer.StreamArrayAsync<PlutoSubtitleAct>(stream, PlutoSubtitleDirector.TryParseStreamedAct, report).GetAsyncEnumerator();

        // t1: 1막 토큰만 도착(글자 단위 스트리밍) → 2막을 기다리지 않고 즉시 방출
        foreach (var d in Deltas(item1)) stream.Push(OpenAiFrame(d, rawUtf8: true));
        Assert.True(await e.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.Equal(1, e.Current.Act);
        Assert.Equal("처음 만난 날 [기억] {그대로}", e.Current.KoreanText);
        Assert.Equal((1, 4), (e.Current.StartSec, e.Current.EndSec));
        Assert.Equal(1, report.Elements);
        Assert.True(report.FirstElementMs >= 0);
        Assert.Equal(PlutoStreamEnd.None, report.End);   // 스트림은 아직 진행 중

        // t2: 2막이 절반만 도착 — 미완성 요소는 방출 금지
        var second = e.MoveNextAsync().AsTask();
        foreach (var d in Deltas(item2Head)) stream.Push(OpenAiFrame(d));
        await Task.Delay(60);
        Assert.False(second.IsCompleted);

        // t3: 2막 종료 → 방출 · 배열 종료(]) 시 [DONE]/본문 끝을 기다리지 않고 완료
        foreach (var d in Deltas(item2Tail)) stream.Push(OpenAiFrame(d));
        Assert.True(await second.WaitAsync(Wait));
        Assert.Equal(2, e.Current.Act);
        Assert.Equal("웃음이 번지던 오후", e.Current.KoreanText);
        stream.Push(OpenAiFrame("]\n이상입니다."));
        Assert.False(await e.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.Equal(PlutoStreamEnd.Done, report.End);
        Assert.Equal(2, report.Elements);
        Assert.Equal(0, report.Healed);
        Assert.True(report.Streamed);
    }

    [Fact]
    public void IncrementalAssembler_Streaming_TracksStructure_AcrossPreludeWrappersAndStrings()
    {
        // <think> 블록 · 산문 속 대괄호 · 코드펜스 · 루트 객체 래퍼 · 문자열 내부 괄호/이스케이프 · 중첩 컨테이너
        const string text = "<think>plan: return [4] scenes as {json}</think>\nHere are [4] scenes:\n```json\n{\"meta\":{\"tags\":[\"a\"]},\"scenes\":[" +
                            "{\"act\":1,\"ko\":\"a ] } \\\" [ {\",\"x\":{\"y\":[1,{\"z\":2}]}}, 7, [\"skip\"], {\"act\":2,\"ko\":\"b\\\\\"}],\"after\":[{\"act\":9,\"ko\":\"무시\"}]}\n```";
        using var asm = new PlutoIncrementalJsonAssembler<(int Act, string Ko)>(ParsePair);
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var i = 0; i < bytes.Length; i++) asm.OnDelta(bytes.AsSpan(i, 1));   // 1바이트씩
        Assert.True(asm.IsArrayClosed);
        asm.Complete();
        Assert.True(asm.TryDequeue(out var first));
        Assert.Equal((1, "a ] } \" [ {"), first);
        Assert.True(asm.TryDequeue(out var second));
        Assert.Equal((2, "b\\"), second);
        Assert.False(asm.TryDequeue(out _));
        Assert.Equal(2, asm.ElementsEmitted);
        Assert.Equal(0, asm.ElementsHealed);

        // 배치 재생(ParseAll)도 동일 결과 · 파서 예외/거부 요소는 격리
        Assert.Equal(2, PlutoIncrementalJsonAssembler<(int Act, string Ko)>.ParseAll(text, ParsePair).Count);
        var mixed = PlutoIncrementalJsonAssembler<PlutoSubtitleAct>.ParseAll("[{\"act\":1,\"ko\":\"가\",\"en\":\"a\"},{\"act\":7,\"ko\":\"범위 밖\",\"en\":\"x\"},{\"act\":2,\"ko\":\"\",\"en\":\"b\"},{\"act\":3,\"ko\":\"다\",\"en\":\"c\"}]", PlutoSubtitleDirector.TryParseStreamedAct);
        Assert.Equal(new[] { 1, 3 }, mixed.ConvertAll(a => a.Act));
        Assert.Empty(PlutoIncrementalJsonAssembler<PlutoSubtitleAct>.ParseAll("no json here ] } \"", PlutoSubtitleDirector.TryParseStreamedAct));
    }

    // ── 3. 절단 자가 치유 ──

    [Fact]
    public async Task IncrementalAssembler_HealsTruncatedStream_GracefullyFlushesValidItems()
    {
        // (a) 문자열 값 중간 + 멀티바이트 UTF-8 중간에서 절단 → 봉합 구제
        var full = Encoding.UTF8.GetBytes("[{\"act\":1,\"ko\":\"가\"},{\"act\":2,\"ko\":\"잘린 문장");
        using (var asm = new PlutoIncrementalJsonAssembler<(int Act, string Ko)>(ParsePair))
        {
            asm.OnDelta(full.AsSpan(0, full.Length - 1));   // '장'의 마지막 바이트 유실
            Assert.Equal(1, asm.PendingCount);              // 1막은 절단 전에 이미 완성
            asm.Complete();
            asm.Complete();                                 // 멱등
            Assert.True(asm.TryDequeue(out var a1));
            Assert.Equal((1, "가"), a1);
            Assert.True(asm.TryDequeue(out var a2));
            Assert.Equal((2, "잘린 문"), a2);
            Assert.False(asm.TryDequeue(out _));
            Assert.Equal(1, asm.ElementsHealed);
        }

        // (b) 절단 지점별 — 어떤 경우에도 예외 없음 · 완성 요소는 항상 보존
        var cases = new (string Text, int Expected, string? LastKo)[]
        {
            ("[{\"act\":1,\"ko\":\"가\"},{\"act\":2,\"ko\":\"끝\\", 2, "끝"),               // 홀로 남은 역슬래시
            ("[{\"act\":1,\"ko\":\"가\"},{\"act\":2,\"ko\":\"끝\\u4", 2, "끝"),             // 미완성 \uXXXX
            ("[{\"act\":1,\"ko\":\"가\"},{\"act\":2,\"ko\":\"나\", ", 2, "나"),             // 후행 쉼표
            ("[{\"act\":1,\"ko\":\"가\"},{\"act\":2,\"ko\":", 2, ""),                      // 값 없는 콜론 → null
            ("[{\"act\":1,\"ko\":\"가\"},{\"act\":2,\"ko\":\"나\",\"tags\":[\"x\",{\"d\":[1", 2, "나"),   // 중첩 컨테이너 스택 봉합
            ("[{\"act\":1,\"ko\":\"가\"},{\"act\":2,\"ko\":\"나\",\"e", 2, "나"),           // 키 중간(새니타이저 2차 구제)
            ("{\"scenes\":[{\"act\":1,\"ko\":\"가\"},", 1, "가"),                          // 요소 사이 절단
            ("[{\"act\":1,\"ko\":\"가\"}", 1, "가"),                                       // 배열 미종료
            ("[", 0, null),
            ("", 0, null),
        };
        foreach (var (text, expected, lastKo) in cases)
        {
            var items = PlutoIncrementalJsonAssembler<(int Act, string Ko)>.ParseAll(text, ParsePair);
            Assert.True(items.Count == expected || (expected == 2 && items.Count == 1), text);   // 2차 구제는 최선 노력 — 최소 완성분 보존
            if (items.Count == expected && lastKo is not null) Assert.Equal(lastKo, items[^1].Ko);
            if (items.Count > 0) Assert.Equal((1, "가"), items[0]);
        }
        // 스키마 필수 필드가 잘린 요소는 구제하지 않음(검증 통과분만)
        var strict = PlutoIncrementalJsonAssembler<PlutoSubtitleAct>.ParseAll("[{\"act\":1,\"ko\":\"가\",\"en\":\"one\"},{\"act\":2,\"ko\":\"나\",\"e", PlutoSubtitleDirector.TryParseStreamedAct);
        Assert.Single(strict);
        var healedEn = PlutoIncrementalJsonAssembler<PlutoSubtitleAct>.ParseAll("[{\"act\":1,\"ko\":\"가\",\"en\":\"one\"},{\"act\":2,\"ko\":\"나\",\"en\":\"Second li", PlutoSubtitleDirector.TryParseStreamedAct);
        Assert.Equal(2, healedEn.Count);
        Assert.Equal("Second li", healedEn[1].EnglishText);

        // (c) 파이프라인: 소켓이 2막 도중 끊김(IOException) → 1막 + 봉합된 2막 방출 후 정상 종료
        var stream = new GatedStream();
        foreach (var d in Deltas("[{\"act\":1,\"ko\":\"하나\",\"en\":\"One\"},{\"act\":2,\"ko\":\"둘\",\"en\":\"Two and a ha")) stream.Push(OpenAiFrame(d, rawUtf8: true));
        stream.Push("data: {\"choices\":[{\"delta\":{\"cont");   // 프레임 중간 절단
        stream.Cut();
        var report = new PlutoStreamReport();
        var acts = new List<PlutoSubtitleAct>();
        await foreach (var act in PlutoStreamingPipeConsumer.StreamArrayAsync<PlutoSubtitleAct>(stream, PlutoSubtitleDirector.TryParseStreamedAct, report).WithCancellation(new CancellationTokenSource(Wait).Token))
            acts.Add(act);
        Assert.Equal(2, acts.Count);
        Assert.Equal("One", acts[0].EnglishText);
        Assert.Equal("Two and a ha", acts[1].EnglishText);
        Assert.Equal(PlutoStreamEnd.Truncated, report.End);
        Assert.Equal(1, report.Healed);
        Assert.Equal(1, stream.Disposed);
    }

    // ── 페일오버 클라이언트 · 게이트웨이 · 오케스트레이터 결선 ──

    [Fact]
    public async Task Streaming_FailoverClient_FallsBackToOllamaNdjson_AndRequestsStreamTrue()
    {
        var body = new GatedStream();
        foreach (var d in Deltas("```json\n[{\"act\":1,\"ko\":\"하나\",\"en\":\"One\"},{\"act\":2,\"ko\":\"둘\",\"en\":\"Two\"}]\n```")) body.Push(OllamaLine(d));
        body.Push("{\"message\":{\"content\":\"\"},\"done\":true}\n");
        var handler = new StreamHandler { Respond = path => path.EndsWith(PlutoCustomLocalGateway.OllamaChatPath, StringComparison.Ordinal) ? SseResponse(body) : new HttpResponseMessage(HttpStatusCode.NotFound) };
        using var gw = new PlutoCustomLocalGateway(Enabled(), handler);
        var chain = new PlutoLlmFailoverClient(null, gw);

        var acts = new List<PlutoSubtitleAct>();
        await foreach (var act in chain.StreamArrayAsync<PlutoSubtitleAct>("자막 3막", PlutoSubtitleDirector.TryParseStreamedAct, new CancellationTokenSource(Wait).Token)) acts.Add(act);

        Assert.Equal(new[] { 1, 2 }, acts.ConvertAll(a => a.Act));
        Assert.True(gw.PrefersOllama);
        Assert.False(gw.IsCircuitOpen);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("\"stream\":true", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"stream\":true", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"stream\":false", PlutoCustomLocalGateway.BuildOllamaPayload("m", "s", "u", 0.7), StringComparison.Ordinal);   // 배치 페이로드 불변
        Assert.DoesNotContain("stream", PlutoCustomLocalGateway.BuildOpenAiPayload("m", "s", "u", 0.7), StringComparison.Ordinal);
    }

    private sealed class BatchLlm : IPlutoLlmClient
    {
        public string? Text { get; init; }
        public int Calls;
        public bool IsAvailable => true;
        public Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct) { Interlocked.Increment(ref Calls); return Task.FromResult(Text); }
    }

    [Fact]
    public async Task Streaming_FailoverClient_PrimaryBatchReplay_ThenGateway_ThenEmpty()
    {
        // ① Primary(Gemini) 배치 응답은 요소 단위 재생 — 게이트웨이 미호출
        var handler = new StreamHandler();
        using var gw = new PlutoCustomLocalGateway(Enabled(), handler);
        var primary = new BatchLlm { Text = "{\"acts\":[{\"act\":1,\"ko\":\"가\",\"en\":\"a\"},{\"act\":3,\"ko\":\"다\",\"en\":\"c\"}]}" };
        var report = new PlutoStreamReport();
        var acts = new List<int>();
        await foreach (var a in new PlutoLlmFailoverClient(primary, gw).StreamArrayAsync<PlutoSubtitleAct>("s", "u", 0.5, PlutoSubtitleDirector.TryParseStreamedAct, report)) acts.Add(a.Act);
        Assert.Equal(new[] { 1, 3 }, acts);
        Assert.Empty(handler.Requests);
        Assert.False(report.Streamed);

        // ② Primary 무효 응답 → 게이트웨이 스트림 · ③ 게이트웨이도 실패 → 0건(예외 없음 · 서킷 집계)
        var dead = new PlutoLlmFailoverClient(new BatchLlm { Text = "죄송합니다" }, gw);
        var none = 0;
        await foreach (var _ in dead.StreamArrayAsync<PlutoSubtitleAct>("s", "u", 0.5, PlutoSubtitleDirector.TryParseStreamedAct)) none++;
        Assert.Equal(0, none);
        Assert.Equal(2, handler.Requests.Count);   // OpenAI → Ollama 대체 시도
        Assert.True(gw.IsCircuitOpen);

        // 일반 IPlutoLlmClient(체인 아님)도 브리지 경유 재생
        var plain = new List<int>();
        await foreach (var a in PlutoLlmBridge.StreamArrayAsync<PlutoSubtitleAct>(primary, "s", "u", 0.5, PlutoSubtitleDirector.TryParseStreamedAct)) plain.Add(a.Act);
        Assert.Equal(new[] { 1, 3 }, plain);
        var nothing = 0;
        await foreach (var _ in PlutoLlmBridge.StreamArrayAsync<PlutoSubtitleAct>(null, "s", "u", 0.5, PlutoSubtitleDirector.TryParseStreamedAct)) nothing++;
        Assert.Equal(0, nothing);
    }

    [Fact]
    public async Task Streaming_Gateway_CallerCancel_Propagates_WithoutTrippingCircuit()
    {
        var body = new GatedStream();
        var handler = new StreamHandler { Respond = _ => SseResponse(body) };
        using var gw = new PlutoCustomLocalGateway(Enabled(), handler);
        using var cts = new CancellationTokenSource();
        var report = new PlutoStreamReport();
        await using var e = gw.StreamArrayAsync<PlutoSubtitleAct>("s", "u", 0.7, PlutoSubtitleDirector.TryParseStreamedAct, report, cts.Token).GetAsyncEnumerator();

        foreach (var d in Deltas("[{\"act\":1,\"ko\":\"하나\",\"en\":\"One\"},")) body.Push(OpenAiFrame(d));
        Assert.True(await e.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.Equal(1, e.Current.Act);

        var pending = e.MoveNextAsync().AsTask();
        cts.Cancel();   // 사용자 취소 — 진행 중 소켓 읽기 즉시 중단
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Wait));
        Assert.Equal(PlutoStreamEnd.Canceled, report.End);
        Assert.False(gw.IsCircuitOpen);
        Assert.Equal(1, body.Disposed);
    }

    [Fact]
    public async Task Streaming_MilestoneOrchestrator_PaintsSceneOne_WhileLaterScenesStillGenerating()
    {
        var body = new GatedStream();
        var handler = new StreamHandler { Respond = _ => SseResponse(body) };
        using var gw = new PlutoCustomLocalGateway(Enabled(), handler);
        var orch = new PlutoMilestoneOrchestrator(new PlutoLlmFailoverClient(null, gw));
        await using var e = orch.OrchestrateFourCutPlanStreamAsync("Day100", "첫 뒤집기").GetAsyncEnumerator();

        // 0) 템플릿 4컷 즉시(네트워크 이전)
        Assert.True(await e.MoveNextAsync().AsTask().WaitAsync(Wait));
        var local = e.Current;
        Assert.Equal(PlutoMilestoneOrchestrator.SceneCount, local.Scenes.Count);
        Assert.False(local.UsedLlm);
        Assert.Equal(0, local.StreamedSlotMask);

        // 1) 1컷만 도착 → 2~4컷 생성 중에도 1컷 스냅샷 방출
        foreach (var d in Deltas("{\"scenes\":[{\"slot\":1,\"title\":\"아침 햇살 인사\",\"prompt\":\"soft morning light, baby yawning in a crib\",\"camera\":\"slow push-in\",\"bgm\":\"music box\"},")) body.Push(OpenAiFrame(d));
        Assert.True(await e.MoveNextAsync().AsTask().WaitAsync(Wait));
        var s1 = e.Current;
        Assert.True(s1.UsedLlm);
        Assert.Equal(0b0001, s1.StreamedSlotMask);
        Assert.Equal("아침 햇살 인사", s1.Scenes[0].SceneTitle);
        Assert.Contains("baby yawning in a crib", s1.Scenes[0].Prompt, StringComparison.Ordinal);
        Assert.Equal(local.Scenes[1].Prompt, s1.Scenes[1].Prompt);   // 미도착 슬롯은 템플릿 유지
        var shots = s1.ToDirectorShots();
        Assert.Equal(4, shots.Length);

        // 2) 2컷 도중 컨텍스트 한도로 스트림 종료 → 봉합된 2컷까지 반영 · 3~4컷은 템플릿
        foreach (var d in Deltas("{\"slot\":2,\"title\":\"까르르 터미타임\",\"prompt\":\"baby doing tummy time on a play m")) body.Push(OpenAiFrame(d));
        body.End();
        Assert.True(await e.MoveNextAsync().AsTask().WaitAsync(Wait));
        var s2 = e.Current;
        Assert.Equal(0b0011, s2.StreamedSlotMask);
        Assert.Equal("까르르 터미타임", s2.Scenes[1].SceneTitle);
        Assert.Contains("tummy time on a play m", s2.Scenes[1].Prompt, StringComparison.Ordinal);
        Assert.Equal(local.Scenes[2].Prompt, s2.Scenes[2].Prompt);
        Assert.Equal(local.Scenes[3].Prompt, s2.Scenes[3].Prompt);
        Assert.False(await e.MoveNextAsync().AsTask().WaitAsync(Wait));
        Assert.Contains("\"stream\":true", handler.Requests[0].Body, StringComparison.Ordinal);
    }
}
