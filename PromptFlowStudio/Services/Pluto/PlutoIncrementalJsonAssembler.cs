using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🌊 SSE 델타 수신점 — <see cref="PlutoStreamingPipeConsumer"/>가 디코딩한 UTF-8 조각을 동기 전달(스팬은 호출 중에만 유효).</summary>
public interface IPlutoStreamDeltaSink
{
    void OnDelta(ReadOnlySpan<byte> utf8);
}

/// <summary>
/// 🧩 닫힌 배열 요소 1개(<c>{…}</c> UTF-8) → 도메인 모델. 메모리는 호출 중에만 유효(풀 버퍼) — 보관 금지.
/// 스키마 불일치는 false · 깨진 JSON은 <see cref="JsonException"/> 허용(어셈블러가 격리).
/// </summary>
public delegate bool PlutoJsonElementParser<T>(ReadOnlyMemory<byte> elementUtf8, int ordinal, out T item);

/// <summary>
/// 🧩 In-Flight 증분 JSON 구조 어셈블러 — 로컬 SLM이 글자 단위로 흘리는 토큰(<c>[</c> · <c>{"act":</c> · <c>1,</c> …)을 바이트 상태 기계로 추적.
///   ▸ 대상 배열: 루트 배열 <c>[…]</c> 또는 루트 객체의 첫 배열 속성(<c>{"scenes":[…]}</c>). 객체 요소가 없는 배열(산문 속 <c>[4]</c>)은 버리고 재탐색.
///   ▸ 요소 객체가 닫히는 순간(깊이 배열+1 → 배열) 해당 바이트 구간만 풀 버퍼에서 파싱해 즉시 큐에 적재 — 후속 요소를 기다리지 않음.
///   ▸ 대괄호/중괄호 비트 스택 · 문자열/이스케이프 상태 → 문자열 내부 괄호 오인 없음. 서두 산문 · <c>&lt;think&gt;</c> 블록 · 코드펜스 무시.
///   ▸ 🩹 <see cref="Complete"/>: 스트림 절단 시 미닫힘 문자열/괄호를 즉석 봉합(실패 시 <see cref="PlutoJsonSanitizer"/>)해 마지막 요소 구제 — 예외 없음.
/// 핫패스(<see cref="OnDelta"/>)는 요소 버퍼 성장 외 할당 0. 단일 스레드 전용.
/// </summary>
public sealed class PlutoIncrementalJsonAssembler<T> : IPlutoStreamDeltaSink, IDisposable
{
    public const int MaxElementBytes = 256 * 1024;
    public const int MaxDepth = 64;
    private const int InitialBufferBytes = 1024;

    private readonly PlutoJsonElementParser<T> _parser;
    private readonly Queue<T> _ready = new(4);
    private byte[] _buf = Array.Empty<byte>();
    private int _len;
    private ulong _stack;          // 깊이별 컨테이너 종류(비트 1 = 배열 · 0 = 객체) — 봉합용
    private int _depth;
    private int _arrayDepth = -1;  // 대상 배열 내부 깊이(-1 = 탐색 중)
    private int _think;            // <think> 매칭 진행(0..7) · 블록 내부에선 </think> 진행(0..8)
    private bool _inThink, _inString, _escape, _capturing, _overflow, _arrayClosed, _completed, _poisoned, _disposed;

    public PlutoIncrementalJsonAssembler(PlutoJsonElementParser<T> parser) => _parser = parser ?? throw new ArgumentNullException(nameof(parser));

    /// <summary>닫힌(또는 봉합된) 요소 객체 수 — 파서 거부 포함.</summary>
    public int ElementsClosed { get; private set; }
    /// <summary>파서가 채택한 요소 수.</summary>
    public int ElementsEmitted { get; private set; }
    /// <summary>절단 후 봉합으로 구제된 요소 수(0 또는 1).</summary>
    public int ElementsHealed { get; private set; }
    /// <summary>대상 배열이 정상 종료(<c>]</c>)됐는지 — true면 이후 입력 무시(조기 종료 신호).</summary>
    public bool IsArrayClosed => _arrayClosed;
    /// <summary>아직 꺼내지 않은 완성 요소 수.</summary>
    public int PendingCount => _ready.Count;

    public bool TryDequeue(out T item) => _ready.TryDequeue(out item!);

    /// <summary>델타 조각 투입 — 요소가 닫히면 즉시 파싱되어 <see cref="TryDequeue"/>로 인출 가능.</summary>
    public void OnDelta(ReadOnlySpan<byte> utf8)
    {
        if (_completed || _arrayClosed || _poisoned || _disposed) return;
        var run = _capturing ? 0 : -1;   // 현재 조각에서 요소 버퍼로 복사할 구간 시작
        for (var i = 0; i < utf8.Length; i++)
        {
            var b = utf8[i];
            if (_inString)
            {
                if (_escape) _escape = false;
                else if (b == (byte)'\\') _escape = true;
                else if (b == (byte)'"') _inString = false;
                continue;
            }
            if (_depth == 0 && _arrayDepth < 0 && SkipThink(b)) continue;
            switch (b)
            {
                case (byte)'"':
                    if (_depth > 0) _inString = true;   // 깊이 0의 산문 따옴표는 무시
                    break;
                case (byte)'{':
                case (byte)'[':
                    if (_depth >= MaxDepth) { _poisoned = true; _capturing = false; return; }
                    if (b == (byte)'[') _stack |= 1UL << _depth; else _stack &= ~(1UL << _depth);
                    _depth++;
                    if (_capturing) break;
                    if (_arrayDepth < 0) { if (b == (byte)'[' && _depth <= 2) _arrayDepth = _depth; }
                    else if (b == (byte)'{' && _depth == _arrayDepth + 1) { _capturing = true; _overflow = false; _len = 0; run = i; }
                    break;
                case (byte)'}':
                case (byte)']':
                    if (_depth == 0) break;   // 짝 없는 닫힘(산문) 무시
                    _depth--;
                    if (_capturing)
                    {
                        if (_depth != _arrayDepth) break;
                        AppendBytes(utf8.Slice(run, i + 1 - run));
                        run = -1;
                        _capturing = false;
                        EmitElement(healed: false);
                    }
                    else if (_arrayDepth >= 0 && _depth < _arrayDepth)
                    {
                        if (ElementsClosed == 0) _arrayDepth = -1;   // 요소 없는 배열 → 재탐색
                        else { _arrayClosed = true; return; }
                    }
                    break;
            }
        }
        if (_capturing && run >= 0) AppendBytes(utf8.Slice(run));
    }

    /// <summary>🩹 스트림 종료 — 진행 중 요소가 있으면 미닫힘 문자열/괄호를 봉합해 구제 시도(실패 시 조용히 폐기). 멱등 · 예외 없음.</summary>
    public void Complete()
    {
        if (_completed || _disposed) return;
        _completed = true;
        if (!_capturing || _overflow || _poisoned || _len == 0) return;
        _capturing = false;
        var rawLen = _len;
        Seal();
        if (EmitElement(healed: true)) return;
        // 2차: 키 중간 절단 · 리터럴 절단 등 → 검증된 문자열 새니타이저(콜드 패스)
        try
        {
            var healed = PlutoJsonSanitizer.Sanitize(Encoding.UTF8.GetString(_buf, 0, TrimPartialUtf8(_buf, rawLen)));
            if (healed.Length == 0 || healed[0] != '{') return;
            var n = Encoding.UTF8.GetByteCount(healed);
            if (n > MaxElementBytes) return;
            EnsureCapacity(n, preserve: false);
            _len = Encoding.UTF8.GetBytes(healed, _buf);
            ElementsClosed--;   // 같은 요소 재시도 — 서수 유지
            EmitElement(healed: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Stream] heal fallback: " + ex.Message);
        }
    }

    /// <summary>완성 텍스트(배치 응답) 일괄 투입 → 채택 요소 목록. 절단 텍스트도 봉합.</summary>
    public static List<T> ParseAll(string? text, PlutoJsonElementParser<T> parser)
    {
        var result = new List<T>(4);
        if (string.IsNullOrEmpty(text)) return result;
        var rented = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(text.Length));
        try
        {
            var n = Encoding.UTF8.GetBytes(text, rented);
            using var asm = new PlutoIncrementalJsonAssembler<T>(parser);
            asm.OnDelta(rented.AsSpan(0, n));
            asm.Complete();
            while (asm.TryDequeue(out var item)) result.Add(item);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var buf = _buf;
        _buf = Array.Empty<byte>();
        _len = 0;
        if (buf.Length > 0) ArrayPool<byte>.Shared.Return(buf);
    }

    // ── 내부 ──

    /// <summary>깊이 0 서두의 <c>&lt;think&gt;…&lt;/think&gt;</c> 건너뛰기 — 소비했으면 true.</summary>
    private bool SkipThink(byte b)
    {
        if (_inThink)
        {
            var close = "</think>"u8;
            _think = b == close[_think] ? _think + 1 : (b == (byte)'<' ? 1 : 0);
            if (_think == close.Length) { _inThink = false; _think = 0; }
            return true;
        }
        var open = "<think>"u8;
        if (b == open[_think])
        {
            if (++_think == open.Length) { _inThink = true; _think = 0; }
            return true;
        }
        _think = b == (byte)'<' ? 1 : 0;
        return _think == 1;
    }

    private void AppendBytes(ReadOnlySpan<byte> src)
    {
        if (_overflow || src.IsEmpty) return;
        if (_len + src.Length > MaxElementBytes) { _overflow = true; return; }
        EnsureCapacity(_len + src.Length, preserve: true);
        src.CopyTo(_buf.AsSpan(_len));
        _len += src.Length;
    }

    private void EnsureCapacity(int min, bool preserve)
    {
        if (_buf.Length >= min) return;
        var next = ArrayPool<byte>.Shared.Rent(Math.Max(min, Math.Max(InitialBufferBytes, _buf.Length * 2)));
        if (preserve && _len > 0) _buf.AsSpan(0, _len).CopyTo(next);
        if (_buf.Length > 0) ArrayPool<byte>.Shared.Return(_buf);
        _buf = next;
    }

    /// <summary>현재 버퍼 구간 파싱 → 채택 시 큐 적재. 파서 예외는 격리(요소 폐기).</summary>
    private bool EmitElement(bool healed)
    {
        var ordinal = ElementsClosed++;
        if (_overflow) return false;
        try
        {
            if (!_parser(new ReadOnlyMemory<byte>(_buf, 0, _len), ordinal, out var item)) return false;
            _ready.Enqueue(item);
            ElementsEmitted++;
            if (healed) ElementsHealed++;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Stream] element rejected: " + ex.Message);
            return false;
        }
    }

    /// <summary>🩹 즉석 봉합 — 미닫힘 문자열 종료 · 후행 쉼표 제거 · 값 없는 <c>:</c> → null · 스택 역순으로 괄호 닫기.</summary>
    private void Seal()
    {
        var len = _len;
        if (_inString)
        {
            if (_escape) len--;                       // 홀로 남은 역슬래시
            else len = TrimPartialUnicodeEscape(_buf, len);
            len = TrimPartialUtf8(_buf, len);
        }
        var open = _depth - _arrayDepth;              // 요소 객체 포함 미닫힘 컨테이너 수
        _len = len;
        EnsureCapacity(len + open + 8, preserve: true);
        var buf = _buf;
        if (_inString) buf[len++] = (byte)'"';
        else
        {
            while (len > 0 && (buf[len - 1] == 0x20 || buf[len - 1] == 0x0A || buf[len - 1] == 0x0D || buf[len - 1] == 0x09)) len--;
            if (len > 0 && buf[len - 1] == (byte)',') len--;
        }
        if (len > 0 && buf[len - 1] == (byte)':') { "null"u8.CopyTo(buf.AsSpan(len)); len += 4; }
        for (var d = _depth - 1; d >= _arrayDepth && d >= 0; d--)
            buf[len++] = (_stack & (1UL << d)) != 0 ? (byte)']' : (byte)'}';
        _len = len;
        _inString = _escape = false;
        _depth = _arrayDepth;
    }

    /// <summary>문자열 끝의 미완성 <c>\uXXXX</c>(16진 4자리 미만) 제거.</summary>
    private static int TrimPartialUnicodeEscape(byte[] buf, int len)
    {
        for (var back = 1; back <= 5 && back < len; back++)
        {
            var i = len - back;
            if (buf[i] != (byte)'u' || buf[i - 1] != (byte)'\\') continue;
            var slashes = 0;
            for (var j = i - 1; j >= 0 && buf[j] == (byte)'\\'; j--) slashes++;
            return (slashes & 1) == 1 && back - 1 < 4 ? i - 1 : len;   // 홀수 개 역슬래시 = 실제 이스케이프
        }
        return len;
    }

    /// <summary>끝의 미완성 UTF-8 멀티바이트 시퀀스 제거.</summary>
    private static int TrimPartialUtf8(byte[] buf, int len)
    {
        var i = len - 1;
        var cont = 0;
        while (i >= 0 && cont < 3 && (buf[i] & 0xC0) == 0x80) { i--; cont++; }
        if (i < 0) return len;
        var lead = buf[i];
        var need = lead >= 0xF0 ? 3 : lead >= 0xE0 ? 2 : lead >= 0xC0 ? 1 : 0;
        return need > cont ? i : len;
    }
}
