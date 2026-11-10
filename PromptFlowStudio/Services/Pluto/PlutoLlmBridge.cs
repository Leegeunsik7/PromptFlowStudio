using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🧠 Pluto LLM 결선점 — Gemini(<see cref="PromptFlowStudio.Services.AI.GeminiPromptService"/>) 등 JSON 응답 LLM 추상화.</summary>
public interface IPlutoLlmClient
{
    /// <summary>키 설정 등 호출 가능 여부(false면 즉시 로컬 폴백).</summary>
    bool IsAvailable { get; }

    /// <summary>시스템/사용자 지시 → JSON 텍스트. 실패는 null(취소 외 throw 금지 권장 — 브리지가 격리).</summary>
    Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct);
}

/// <summary>
/// 🔁 Gemini(클라우드) + 커스텀 로컬 AI 게이트웨이 페일오버 묶음 — DI에서 Pluto 서비스에 <see cref="IPlutoLlmClient"/>로 주입.
/// <see cref="PlutoLlmBridge.TryCompleteAsync(IPlutoLlmClient?, string, string, double, CancellationToken, TimeSpan?)"/>가 인식해 단계별 예산으로 체인 실행.
/// </summary>
public sealed class PlutoLlmFailoverClient : IPlutoLlmClient
{
    public IPlutoLlmClient? Primary { get; }
    public PlutoCustomLocalGateway? Gateway { get; }

    public PlutoLlmFailoverClient(IPlutoLlmClient? primary, PlutoCustomLocalGateway? gateway)
    {
        Primary = primary is PlutoLlmFailoverClient nested ? nested.Primary : primary;   // 중첩 방지
        Gateway = gateway;
    }

    public bool IsAvailable => Probe(Primary) || Probe(Gateway);

    /// <summary>브리지 경유 체인(직접 호출 대비) — 실패 시 null.</summary>
    public Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct)
        => PlutoLlmBridge.TryCompleteAsync(Primary, Gateway, system, user, temperature, ct);

    private static bool Probe(IPlutoLlmClient? c)
    {
        if (c is null) return false;
        try { return c.IsAvailable; } catch { return false; }
    }
}

/// <summary>
/// 🧠 Zero-Downtime LLM 브리지 — 3.5초 예산 · 게이트웨이 단계는 컴팩트 프롬프트 + JSON 자가 치유(토큰 무시 클라이언트도 WhenAny로 차단) · 예외 격리 · 무응답/미설정 → null(호출측이 로컬 폴백).
/// 호출자 취소만 전파.
/// </summary>
public static class PlutoLlmBridge
{
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(3500);

    public static Task<string?> TryCompleteAsync(IPlutoLlmClient? llm, string system, string user, double temperature, CancellationToken ct, TimeSpan? budget = null)
        => llm switch
        {
            PlutoLlmFailoverClient chain => TryCompleteAsync(chain.Primary, chain.Gateway, system, user, temperature, ct, budget),
            PlutoCustomLocalGateway gw => TryGatewayAsync(gw, system, user, temperature, ct, budget),
            _ => TryCompleteSingleAsync(llm, system, user, temperature, ct, budget),
        };

    /// <summary>
    /// 🔁 우선순위 체인 — ① Gemini(클라우드 · 3.5초) → ② 커스텀 로컬 AI 게이트웨이(REST · 활성 시 · 게이트웨이 타임아웃) → ③ null(호출측 결정론 템플릿).
    /// 단계별 예산 독립(<paramref name="budget"/> 지정 시 양 단계 공통). 호출자 취소만 전파.
    /// </summary>
    public static async Task<string?> TryCompleteAsync(IPlutoLlmClient? primary, PlutoCustomLocalGateway? gateway, string system, string user, double temperature, CancellationToken ct, TimeSpan? budget = null)
    {
        var text = await TryCompleteSingleAsync(primary, system, user, temperature, ct, budget).ConfigureAwait(false);
        if (text is not null || gateway is null || ReferenceEquals(primary, gateway)) return text;
        bool enabled;
        try { enabled = gateway.Options.IsEnabled; } catch { return null; }
        if (!enabled) return null;
        return await TryGatewayAsync(gateway, system, user, temperature, ct, budget ?? gateway.Options.EffectiveTimeout).ConfigureAwait(false);
    }

    /// <summary>
    /// 🔌 로컬 게이트웨이 단계 — 🗜 컴팩트 불변 접두 프롬프트(<see cref="PlutoPromptCompressor"/> · KV-캐시 재사용) 전송 →
    /// 🩹 응답 자가 치유(<see cref="PlutoJsonSanitizer"/>). Gemini는 원본 서술형 지시문 유지.
    /// </summary>
    private static async Task<string?> TryGatewayAsync(PlutoCustomLocalGateway gateway, string system, string user, double temperature, CancellationToken ct, TimeSpan? budget)
    {
        var p = PlutoPromptCompressor.Compose(system, user);
        var text = await TryCompleteSingleAsync(gateway, p.System, p.User, temperature, ct, budget).ConfigureAwait(false);
        if (text is null) return null;
        var healed = PlutoJsonSanitizer.Sanitize(text);
        return healed.Length == 0 ? null : healed;
    }

    private static async Task<string?> TryCompleteSingleAsync(IPlutoLlmClient? llm, string system, string user, double temperature, CancellationToken ct, TimeSpan? budget)
    {
        if (llm is null) return null;
        bool available;
        try { available = llm.IsAvailable; } catch { return null; }
        if (!available) return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget ?? Budget);
        try
        {
            Task<string?> call;
            try { call = llm.CompleteJsonAsync(system, user, temperature, cts.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log(ex); return null; }
            if (call is null) return null;

            var timeout = Task.Delay(Timeout.Infinite, cts.Token);
            var done = await Task.WhenAny(call, timeout).ConfigureAwait(false);
            if (done != call)
            {
                _ = call.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);   // 관측되지 않은 예외 방지
                ct.ThrowIfCancellationRequested();
                return null;   // ⏱ 예산 초과 → 로컬 폴백
            }
            var text = await call.ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log(ex); return null; }
        finally
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }   // 대기 Delay 해제
        }
    }

    private static void Log(Exception ex) => System.Diagnostics.Debug.WriteLine("[Pluto.LLM] fallback: " + ex.Message);

    // ── JSON 헬퍼(스키마 즉시 검증용) ──

    internal static JsonDocument? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var s = PlutoJsonSanitizer.Sanitize(json);   // 🩹 펜스·서두/후미·후행 쉼표·잘린 괄호 자가 치유(정상 JSON은 무변경)
        if (s.Length == 0) return null;
        try { return JsonDocument.Parse(s); }
        catch (JsonException) { return null; }
    }

    internal static string Str(JsonElement e, string name, int max)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return "";
        var s = (v.GetString() ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return s.Length > max ? s[..max].TrimEnd() : s;
    }

    /// <summary>해시태그 정규화 — '#' 보정 · 공백 제거 · 대소문자 무시 중복 제거 · 상한.</summary>
    internal static void AddTag(List<string> tags, HashSet<string> seen, string? raw, int max)
    {
        if (tags.Count >= max || string.IsNullOrWhiteSpace(raw)) return;
        var x = raw.Replace(" ", "", StringComparison.Ordinal).Trim();
        if (x.Length == 0) return;
        if (x[0] != '#') x = "#" + x;
        if (x.Length < 2 || x.Length > 40) return;
        if (seen.Add(x)) tags.Add(x);
    }
}
