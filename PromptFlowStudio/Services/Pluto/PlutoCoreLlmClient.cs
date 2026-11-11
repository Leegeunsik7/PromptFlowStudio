using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// 🪐 PlutoCore Named Pipe 허브 LLM 단계 — 요청 1회 = 연결 1회(JSON 1줄 송신 · 1줄 수신).
/// <see cref="IsAvailable"/> = 관리자 상태 생존 + API 키(PLUTOCORE_API_KEY · 하드코딩 금지). 실패/거부/스키마 위반 → null(체인 다음 단계) · 호출자 취소만 전파.
/// </summary>
public sealed class PlutoCoreLlmClient : IPlutoLlmClient
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    public const int DefaultMaxTokens = 768;

    public PlutoCoreProcessManager Manager { get; }

    public PlutoCoreLlmClient(PlutoCoreProcessManager manager) => Manager = manager ?? throw new ArgumentNullException(nameof(manager));

    public TimeSpan RequestTimeout => Manager.Options.RequestTimeout;

    public bool IsAvailable => Manager.IsRunning && !string.IsNullOrWhiteSpace(Manager.Options.ApiKey);

    public async Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct)
    {
        if (!IsAvailable) return null;
        var o = Manager.Options;
        using var pipe = new NamedPipeClientStream(".", o.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(o.ProbeTimeout);
                await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
            }
            using var writer = new StreamWriter(pipe, Utf8NoBom, 4096, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Utf8NoBom, false, 4096, leaveOpen: true);
            var payload = JsonSerializer.Serialize(new PipeRequest(PlutoCoreOptions.AppName, o.ApiKey!, system ?? string.Empty, user ?? string.Empty,
                DefaultMaxTokens, (float)Math.Clamp(double.IsFinite(temperature) ? temperature : 0.7, 0.0, 2.0)));
            await writer.WriteLineAsync(payload.AsMemory(), ct).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            return ParseResponse(line);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException or JsonException or PlatformNotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine("[PlutoCore.LLM] fallback: " + ex.Message);
            return null;
        }
    }

    /// <summary>응답 스키마 즉시 검증 — Success=true + 비어 있지 않은 Content 만 채택.</summary>
    internal static string? ParseResponse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("Success", out var s) || s.ValueKind != JsonValueKind.True) return null;
            if (!root.TryGetProperty("Content", out var c) || c.ValueKind != JsonValueKind.String) return null;
            var text = c.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (JsonException) { return null; }
    }

    private sealed record PipeRequest(string AppName, string ApiKey, string SystemPrompt, string UserPrompt, int MaxTokens, float Temperature);
}
