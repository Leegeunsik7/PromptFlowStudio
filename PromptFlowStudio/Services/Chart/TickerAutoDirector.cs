using System;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.AI;

namespace PromptFlowStudio.Services.Chart;

/// <summary>🎬 티커 올인원 결과 — 단계별 성공 여부([부분 구제]: 차트 실패해도 프롬프트 보존).</summary>
public sealed record TickerDirectorResult(string Ticker, bool ChartOk, string? ChartPath, bool FrameBound, string? Prompt, bool PromptFallback, string Message)
{
    public bool Ok => ChartOk && FrameBound && !string.IsNullOrWhiteSpace(Prompt);
}

/// <summary>
/// 🎬 티커 올인원 I2V 오토-디렉터 — 종목 1클릭/IPC 수신 → ① OLED 차트 스틸 PNG 생성 → ② START FRAME · STILL 바인딩 → ③ 산업 메타포 Wan 2.1 프롬프트 확장.
/// 단계별 예외 격리(취소만 전파) · 동시 실행 1건(재진입 = 최신 요청 대기 없이 거절).
/// </summary>
public sealed class TickerAutoDirector
{
    private readonly Func<string, CancellationToken, Task<ChartStillResult>> _chart;
    private readonly Func<string, string?, string?, CancellationToken, Task<GeminiEnhanceOutcome>> _prompt;
    private readonly Func<string, CancellationToken, Task>? _bindFrame;
    private int _running;

    public TickerAutoDirector(
        Func<string, CancellationToken, Task<ChartStillResult>> chart,
        Func<string, string?, string?, CancellationToken, Task<GeminiEnhanceOutcome>> prompt,
        Func<string, CancellationToken, Task>? bindFrame = null)
    {
        _chart = chart ?? throw new ArgumentNullException(nameof(chart));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _bindFrame = bindFrame;
    }

    /// <summary>표준 결선 — 차트 스틸 서비스 + Gemini 티커 프롬프트.</summary>
    public static TickerAutoDirector Create(ChartStillGeneratorService chart, GeminiPromptService gemini, string outputDirectory, Func<string, CancellationToken, Task>? bindFrame = null, ChartStillOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(gemini);
        var o = options ?? new ChartStillOptions { Width = 1920, Height = 1080 };
        return new TickerAutoDirector(
            (t, ct) => chart.GenerateAsync(t, outputDirectory, o, ct),
            gemini.GenerateTickerPromptAsync,
            bindFrame);
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public async Task<TickerDirectorResult> RunAsync(string? ticker, string? name = null, string? sector = null, CancellationToken ct = default)
    {
        var t = (ticker ?? "").Trim();
        if (t.Length == 0) return new("", false, null, false, null, true, "종목 티커가 비었습니다.");
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return new(t, false, null, false, null, true, "⏳ 이전 종목 준비 중 — 잠시 후 다시 시도하세요.");
        try
        {
            // ① 차트 스틸
            ChartStillResult? chart = null;
            try { chart = await _chart(t, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Program.Log("TickerDirector.Chart", ex); }
            var chartOk = chart is { Ok: true, Path.Length: > 0 };

            // ② START FRAME 바인딩
            var bound = false;
            if (chartOk)
            {
                if (_bindFrame is null) bound = true;
                else
                {
                    try { await _bindFrame(chart!.Path!, ct).ConfigureAwait(false); bound = true; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Program.Log("TickerDirector.Bind", ex); }
                }
            }

            // ③ 프롬프트 확장
            GeminiEnhanceOutcome? p = null;
            try { p = await _prompt(t, name, sector, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Program.Log("TickerDirector.Prompt", ex); }
            var prompt = string.IsNullOrWhiteSpace(p?.Prompt) ? null : p!.Prompt;

            var label = string.IsNullOrWhiteSpace(name) ? t.ToUpperInvariant() : t.ToUpperInvariant() + " " + name;
            string msg;
            if (chartOk && bound && prompt is not null) msg = "🎬 " + label + " 올인원 준비 완료 — 차트 스틸 · I2V 시작 프레임 · 시네마틱 프롬프트" + (p!.UsedFallback ? "(로컬)" : "") + " → [생성]만 누르세요";
            else if (prompt is not null) msg = "💡 " + label + " 프롬프트만 준비됨 — 차트 스틸 " + (chartOk ? "바인딩" : "생성") + " 실패" + (chart?.Message is { Length: > 0 } cm ? "(" + cm + ")" : "");
            else if (chartOk && bound) msg = "💡 " + label + " 시작 프레임만 준비됨 — 프롬프트 확장 실패";
            else msg = "⚠ " + label + " 준비 실패 — 다시 시도하세요.";
            return new(t, chartOk, chart?.Path, bound, prompt, p?.UsedFallback ?? true, msg);
        }
        finally { Volatile.Write(ref _running, 0); }
    }
}
