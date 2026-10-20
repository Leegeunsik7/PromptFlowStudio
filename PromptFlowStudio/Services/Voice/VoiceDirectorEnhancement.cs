using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PromptFlowStudio.Services.Voice;

/// <summary>
/// [PromptFlowStudio] 음성 피드백 오디오 효과음 합성기(v2.9.0 자율 발상 ①) — <b>순수 C# BCL WAV(PCM) 합성</b>(외부 오디오 라이브러리 0).
///   ▸ 음성 명령 수행 시 청각 피드백을 주는 짧은 톤을 실시간 합성한다 — <b>Ack</b>(인식) · <b>Success</b>(적용 성공 · 장3화음) · <b>Error</b>(미인식 · 저음 버즈).
///   ▸ 16-bit 모노 PCM RIFF/WAVE 를 바이트로 직접 생성 → Data URI 로 브라우저 &lt;audio&gt; 즉시 재생 가능(WebView 호환).
/// </summary>
public static class VoiceFeedbackSynthesizer
{
    private const int SampleRate = 44100;

    /// <summary>인식 확인음(맑은 단음).</summary>
    public static byte[] Ack() => BuildChord(new[] { 880.0 }, 90);
    /// <summary>적용 성공음(장3화음 C-E-G).</summary>
    public static byte[] Success() => BuildChord(new[] { 523.25, 659.25, 783.99 }, 200);
    /// <summary>미인식/오류음(저음 불협 버즈).</summary>
    public static byte[] Error() => BuildChord(new[] { 220.0, 175.0 }, 240);

    /// <summary>주파수 집합의 화음 톤을 16-bit 모노 PCM WAV 로 합성한다(클릭 방지 페이드).</summary>
    public static byte[] BuildChord(double[] freqs, int ms, double amp = 0.3)
    {
        var n = Math.Max(1, SampleRate * Math.Clamp(ms, 10, 5000) / 1000);
        var pcm = new short[n];
        var fade = Math.Max(1, SampleRate / 200);   // 5ms 페이드 인/아웃
        for (var i = 0; i < n; i++)
        {
            double s = 0;
            foreach (var f in freqs) s += Math.Sin(2 * Math.PI * f * i / SampleRate);
            s /= Math.Max(1, freqs.Length);
            double env = 1.0;
            if (i < fade) env = i / (double)fade;
            else if (i > n - fade) env = (n - i) / (double)fade;
            pcm[i] = (short)Math.Clamp(s * amp * env * short.MaxValue, short.MinValue, short.MaxValue);
        }
        return Encode(pcm);
    }

    /// <summary>PCM WAV 를 브라우저 재생용 Data URI 로.</summary>
    public static string ToDataUri(byte[] wav) => "data:audio/wav;base64," + Convert.ToBase64String(wav);

    // 16-bit mono PCM → RIFF/WAVE 바이트(순수 BCL).
    private static byte[] Encode(short[] pcm)
    {
        var dataLen = pcm.Length * 2;
        using var ms = new MemoryStream(44 + dataLen);
        using var w = new BinaryWriter(ms, Encoding.ASCII, true);
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataLen); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);   // PCM · mono
        w.Write(SampleRate); w.Write(SampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(dataLen);
        foreach (var s in pcm) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}

/// <summary>음성 편집 이력 1건 — 시각·원문·의도·자동 라벨·메시지·성공.</summary>
public sealed record VoiceEditEntry(DateTimeOffset Time, string Raw, VoiceIntent Intent, string Label, string Message, bool Ok);

/// <summary>
/// [PromptFlowStudio] 음성 생성 컷 편집 이력 자동 라벨링(v2.9.0 자율 발상 ②) — 관찰 가능 싱글턴 · 순수 BCL.
///   ▸ 음성으로 수행된 모든 편집/생성 명령을 <b>사람이 읽기 쉬운 라벨</b>(시각 + 대상 + 동작 + 결과)로 자동 기록한다.
///   ▸ 감사(audit)·되짚기·협업 인수인계에 즉시 활용 가능한 편집 로그 → 음성 연출의 추적성 확보.
/// </summary>
public sealed class VoiceEditHistoryLabeler
{
    private readonly object _lock = new();
    private readonly List<VoiceEditEntry> _entries = new();

    /// <summary>이력 변경 방송(대시보드 이력 패널).</summary>
    public event Action? OnChange;

    /// <summary>전체 이력(최신 우선).</summary>
    public IReadOnlyList<VoiceEditEntry> Entries { get { lock (_lock) return _entries.AsEnumerable().Reverse().ToList(); } }

    /// <summary>최근 n건.</summary>
    public IReadOnlyList<VoiceEditEntry> Recent(int n) => Entries.Take(Math.Max(0, n)).ToList();

    /// <summary>명령 + 적용 결과를 자동 라벨링해 기록한다.</summary>
    public VoiceEditEntry Record(VoiceCommand cmd, VoiceActionResult result)
    {
        var entry = new VoiceEditEntry(DateTimeOffset.Now, cmd.Raw, cmd.Intent, BuildLabel(cmd, result), result.Message, result.Ok);
        lock (_lock) _entries.Add(entry);
        OnChange?.Invoke();
        return entry;
    }

    /// <summary>이력 초기화.</summary>
    public void Clear() { lock (_lock) _entries.Clear(); OnChange?.Invoke(); }

    /// <summary>명령/결과 → 사람이 읽는 라벨(순수 · 테스트 대상). 예: "[14:03:22] 🎥 3번 컷 · 카메라 오르빗 ✓".</summary>
    public static string BuildLabel(VoiceCommand cmd, VoiceActionResult result)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var icon = IconOf(cmd.Intent);
        var target = cmd.TargetCut is int n ? $"{n}번 컷 · " : "";
        var mark = result.Ok ? "✓" : "✗";
        return $"[{ts}] {icon} {target}{result.Label} {mark}";
    }

    private static string IconOf(VoiceIntent i) => i switch
    {
        VoiceIntent.ZoomIn or VoiceIntent.ZoomOut or VoiceIntent.CameraOrbit => "🎥",
        VoiceIntent.PromptBoost => "✨",
        VoiceIntent.BgmDuckDown or VoiceIntent.BgmUp => "🔊",
        VoiceIntent.SpeedUp or VoiceIntent.SpeedDown => "⏩",
        VoiceIntent.SafeZoneOn or VoiceIntent.SafeZoneOff => "🛡️",
        VoiceIntent.EmbossOn => "🏷️",
        VoiceIntent.StartGeneration => "🚀",
        VoiceIntent.StopGeneration => "⏹",
        VoiceIntent.Merge => "🎬",
        VoiceIntent.NextScene => "➡️",
        _ => "🎙️",
    };
}
