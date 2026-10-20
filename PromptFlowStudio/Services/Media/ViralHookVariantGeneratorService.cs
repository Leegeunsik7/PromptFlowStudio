using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Media;

/// <summary>v16.23.0 첫 3초 바이럴 후킹 스타일 — A 충격 질문형 · B 익스트림 클로즈업형 · C 파이어링 카피형.</summary>
public enum ViralHookStyle { ShockQuestion, ExtremeCloseUp, FiringCopy }

/// <summary>v16.23.0 스타일 표시 규격(문자 · 라벨 · 아이콘 · 설명).</summary>
public sealed record ViralHookSpec(ViralHookStyle Style, char Letter, string Label, string Icon, string Description);

/// <summary>v16.23.0 오프닝(첫 3초) 분석 — 후킹 점수 0~100 · 첫 컷 길이 · 질문/클로즈업/모션 신호 · 추천 스타일.</summary>
public readonly record struct HookOpeningAnalysis(int Score, double FirstCutSeconds, bool HasQuestion, bool HasCloseUp, bool HasMotion, ViralHookStyle Recommended);

/// <summary>v16.23.0 변주 1건 렌더 결과.</summary>
public sealed record ViralHookRenderResult(ViralHookStyle Style, bool Ok, string? VideoPath, string Copy, string Message);

/// <summary>v16.23.0 A/B/C 일괄 렌더 결과(1건 이상 성공이면 Ok · [부분 구제]).</summary>
public sealed record ViralHookBatchResult(bool Ok, string? OutputDir, IReadOnlyList<ViralHookRenderResult> Items, string Message);

/// <summary>
/// [PromptFlowStudio] v16.23.0 🎬 첫 3초 바이럴 후킹 오토 젠 — 완성 영상(1080x1920) → A/B/C 후킹 변주 컷(첫 3초 오버레이 · ffmpeg · 순수 BCL).
///   ▸ A 충격 질문형: 화면 30% 높이 대형 질문 카피(반투명 박스) · B 익스트림 클로즈업형: zoompan 1.6x→1.0x 줌아웃(출력 프레임 기준 3초) + 하단 카피
///     · C 파이어링 카피형: 상단 경고 밴드 + 노란 카피 0.25초 점멸. 오버레이는 enable='lt(t,3)' 로 첫 3초에만 적용(이후 원본 그대로).
///   ▸ 카피는 임시 UTF-8 textfile(한글 인라인 이스케이프 회피) · 경로는 FontResolver.EscapeDrawPath · 수치 InvariantCulture.
///   ▸ 출력: 원본 옆 <c>HOOK_yyyyMMdd_HHmmss/hook_{A|B|C}.mp4</c> · 스타일 실패는 건너뛰고 계속 · 동시 실행 1건 · 예외 0(결과 record).
/// </summary>
public sealed class ViralHookVariantGeneratorService
{
    public const int Width = 1080;
    public const int Height = 1920;
    public const double HookSeconds = 3.0;
    public const int Fps = 30;
    public const int MaxTopicChars = 18;

    public static readonly IReadOnlyList<ViralHookSpec> Specs = new[]
    {
        new ViralHookSpec(ViralHookStyle.ShockQuestion,  'A', "충격 질문형",       "❓", "첫 화면 대형 질문으로 궁금증 유발"),
        new ViralHookSpec(ViralHookStyle.ExtremeCloseUp, 'B', "익스트림 클로즈업형", "🔍", "1.6x 클로즈업 → 줌아웃 리빌"),
        new ViralHookSpec(ViralHookStyle.FiringCopy,     'C', "파이어링 카피형",    "🔥", "상단 경고 밴드 + 점멸 카피"),
    };

    public static ViralHookSpec SpecOf(ViralHookStyle s)
    {
        foreach (var x in Specs) if (x.Style == s) return x;
        return Specs[0];
    }

    // 오프닝 신호 키워드(소문자 비교 · 한/영)
    private static readonly string[] CloseUpWords = { "close-up", "closeup", "close up", "macro", "extreme close", "클로즈업", "접사", "얼굴", "눈동자" };
    private static readonly string[] MotionWords = { "run", "jump", "fly", "spin", "dash", "explode", "fast", "zoom", "달리", "뛰", "날아", "회전", "폭발", "빠르", "질주" };

    private readonly Func<string?> _ffmpeg;
    private int _busy;
    private int _rendered;
    private int _failed;

    public ViralHookVariantGeneratorService(IVideoMergeService merge) : this(() => merge.ResolvedFfmpegPath) { }

    /// <param name="ffmpegPath">ffmpeg 경로 공급자(테스트 주입용).</param>
    public ViralHookVariantGeneratorService(Func<string?> ffmpegPath) => _ffmpeg = ffmpegPath;

    public bool IsAvailable => _ffmpeg() is not null;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public int RenderedCount => Volatile.Read(ref _rendered);
    public int FailedCount => Volatile.Read(ref _failed);
    public string? LastAction { get; private set; }
    public HookOpeningAnalysis? LastAnalysis { get; private set; }

    public event Action? OnChange;

    /// <summary>첫 3초(첫 컷 프롬프트·나레이션) 후킹 신호 분석 → 점수 · 추천 스타일(부족한 신호를 보강하는 변주).</summary>
    public HookOpeningAnalysis AnalyzeOpening(StoryboardProject project, double cutSeconds = RetentionHeatmapPredictorEngine.DefaultCutSeconds)
    {
        var a = Analyze(project, cutSeconds);
        LastAnalysis = a;
        return a;
    }

    /// <summary>순수 분석(상태 미기록).</summary>
    public static HookOpeningAnalysis Analyze(StoryboardProject project, double cutSeconds = RetentionHeatmapPredictorEngine.DefaultCutSeconds)
    {
        ArgumentNullException.ThrowIfNull(project);
        StoryboardScene? first = null;
        foreach (var s in project.Scenes)
            if (!string.IsNullOrWhiteSpace(s.Prompt) || !string.IsNullOrWhiteSpace(s.VoiceText)) { first = s; break; }
        if (first is null) return new(0, 0, false, false, false, ViralHookStyle.ShockQuestion);

        var prompt = first.Prompt ?? "";
        var voice = first.VoiceText ?? "";
        var question = voice.Contains('?') || prompt.Contains('?') || ContainsAny(voice, "왜", "어떻게", "진짜", "정말");
        var closeUp = ContainsAny(prompt, CloseUpWords);
        var motion = ContainsAny(prompt, MotionWords);
        var cut = Math.Max(0.5, cutSeconds);
        // 첫 3초 안에 컷 전환이 있으면 가점, 나레이션이 첫 1초 안에 시작(짧은 문장)이면 가점
        var score = 20 + (question ? 25 : 0) + (closeUp ? 20 : 0) + (motion ? 15 : 0)
                    + (cut <= HookSeconds ? 10 : 0) + (voice.Length is > 0 and <= 40 ? 10 : 0);
        var rec = !question ? ViralHookStyle.ShockQuestion : !closeUp ? ViralHookStyle.ExtremeCloseUp : ViralHookStyle.FiringCopy;
        return new(Math.Clamp(score, 0, 100), cut, question, closeUp, motion, rec);
    }

    /// <summary>후킹 주제(제목 → 첫 나레이션 → 첫 프롬프트) · 최대 <see cref="MaxTopicChars"/>자.</summary>
    public static string TopicOf(StoryboardProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var t = project.CoverTitle;
        if (string.IsNullOrWhiteSpace(t) || t == "새 스토리보드") t = project.Title;
        if (string.IsNullOrWhiteSpace(t) || t == "새 스토리보드")
        {
            foreach (var s in project.Scenes)
            {
                if (!string.IsNullOrWhiteSpace(s.VoiceText)) { t = s.VoiceText; break; }
                if (!string.IsNullOrWhiteSpace(s.Prompt)) { t = s.Prompt; break; }
            }
        }
        return Trim(t ?? "", MaxTopicChars);
    }

    /// <summary>스타일별 후킹 카피(순수 함수 · 2줄 이내).</summary>
    public static string BuildCopy(ViralHookStyle style, string? topic)
    {
        var t = Trim(topic ?? "", MaxTopicChars);
        if (t.Length == 0) t = "이 영상";
        return style switch
        {
            ViralHookStyle.ShockQuestion => t + ",\n진짜 알고 있었나요?",
            ViralHookStyle.ExtremeCloseUp => "잠깐! 여기 보세요\n" + t,
            _ => "3초 만에 빠져드는\n" + t,
        };
    }

    /// <summary>스타일별 필터 그래프(입력 [0:v] → 출력 [v] · 순수 함수). fontFile 없으면 ffmpeg 기본 폰트.</summary>
    public static string BuildFilter(ViralHookStyle style, string textFile, string? fontFile, int width = Width, int height = Height)
    {
        var w = Even(width);
        var h = Even(height);
        var hookFrames = (int)(HookSeconds * Fps);
        var sb = new StringBuilder(640);
        sb.Append("[0:v]scale=").Append(w).Append(':').Append(h).Append(":force_original_aspect_ratio=increase,crop=")
          .Append(w).Append(':').Append(h).Append(",setsar=1,fps=").Append(Fps);
        if (style == ViralHookStyle.ExtremeCloseUp)
        {
            // 출력 프레임 번호(on) 기준 1.6x → 1.0x 선형 줌아웃 · 중앙 고정 · 3초 이후 1.0
            sb.Append(",zoompan=z='if(lt(on,").Append(hookFrames).Append("),1.6-0.6*on/").Append(hookFrames)
              .Append(",1)':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d=1:s=").Append(w).Append('x').Append(h)
              .Append(":fps=").Append(Fps);
        }
        else if (style == ViralHookStyle.FiringCopy)
        {
            sb.Append(",drawbox=x=0:y=0:w=iw:h=ih*0.16:color=red@0.55:t=fill:enable='lt(t,").Append(Inv(HookSeconds)).Append(")'");
        }
        sb.Append(",drawtext=");
        if (!string.IsNullOrWhiteSpace(fontFile)) sb.Append("fontfile=").Append(FontResolver.EscapeDrawPath(fontFile)).Append(':');
        sb.Append("textfile=").Append(FontResolver.EscapeDrawPath(textFile)).Append(':');
        switch (style)
        {
            case ViralHookStyle.ShockQuestion:
                sb.Append("fontcolor=white:fontsize=").Append(h / 20).Append(":borderw=6:bordercolor=black:box=1:boxcolor=black@0.45:boxborderw=28")
                  .Append(":line_spacing=16:x=(w-text_w)/2:y=h*0.30");
                break;
            case ViralHookStyle.ExtremeCloseUp:
                sb.Append("fontcolor=white:fontsize=").Append(h / 26).Append(":borderw=5:bordercolor=black")
                  .Append(":line_spacing=12:x=(w-text_w)/2:y=h*0.72");
                break;
            default:
                sb.Append("fontcolor=yellow:fontsize=").Append(h / 24).Append(":borderw=6:bordercolor=black")
                  .Append(":line_spacing=12:x=(w-text_w)/2:y=h*0.03:alpha='if(lt(mod(t,0.5),0.25),1,0.6)'");
                break;
        }
        sb.Append(":enable='lt(t,").Append(Inv(HookSeconds)).Append(")',format=yuv420p[v]");
        return sb.ToString();
    }

    /// <summary>ffmpeg 인자(순수 함수 · 파일 접근 0).</summary>
    public static List<string> BuildArgs(string input, string output, ViralHookStyle style, string textFile, string? fontFile)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("입력 영상이 비었습니다.", nameof(input));
        return new List<string>(32)
        {
            "-y", "-hide_banner", "-nostdin", "-i", input,
            "-filter_complex", BuildFilter(style, textFile, fontFile),
            "-map", "[v]", "-map", "0:a?",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000",
            "-movflags", "+faststart", "-progress", "pipe:1", output,
        };
    }

    public static string FileNameOf(ViralHookStyle s) => "hook_" + SpecOf(s).Letter + ".mp4";

    /// <summary>A/B/C 변주 일괄 렌더(예외 0). copies 미지정 스타일은 <see cref="BuildCopy"/> 기본 카피.</summary>
    public async Task<ViralHookBatchResult> RenderVariantsAsync(string masterVideo, string topic, IReadOnlyList<ViralHookStyle> styles,
        IReadOnlyDictionary<ViralHookStyle, string>? copies = null, string? outputDir = null, double durationSeconds = 0,
        IProgress<PostProductionProgress>? progress = null, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return Fail("이미 후킹 변주 렌더가 진행 중입니다.");
        var tmpDir = Path.Combine(Path.GetTempPath(), "promptflow-hook");
        var tmpFiles = new List<string>(3);
        try
        {
            var exe = _ffmpeg();
            if (exe is null) return Done(Fail("ffmpeg.exe 를 찾을 수 없어 후킹 변주를 렌더할 수 없습니다."));
            if (string.IsNullOrWhiteSpace(masterVideo) || !File.Exists(masterVideo)) return Done(Fail("원본 완성 영상이 없습니다 — 먼저 최종 영상을 내보내세요."));
            var list = Distinct(styles);
            if (list.Count == 0) return Done(Fail("후킹 스타일을 1개 이상 선택하세요."));

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var dir = outputDir ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(masterVideo))!, "HOOK_" + stamp);
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(tmpDir);
            var font = FontResolver.FindKoreanFont(bold: true);
            var items = new List<ViralHookRenderResult>(list.Count);
            var ok = 0;
            for (var i = 0; i < list.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var style = list[i];
                var spec = SpecOf(style);
                var copy = copies is not null && copies.TryGetValue(style, out var c) && !string.IsNullOrWhiteSpace(c) ? c.Trim() : BuildCopy(style, topic);
                var from = 100 * i / list.Count;
                var to = 100 * (i + 1) / list.Count;
                var stage = spec.Icon + " " + spec.Letter + " " + spec.Label + " 렌더 중…";
                progress?.Report(new(from, stage));

                var txt = Path.Combine(tmpDir, "hook_" + Guid.NewGuid().ToString("N") + ".txt");
                tmpFiles.Add(txt);
                await File.WriteAllTextAsync(txt, copy, new UTF8Encoding(false), ct).ConfigureAwait(false);
                var output = Path.Combine(dir, FileNameOf(style));
                var r = await FfmpegVideoMixerService.RunAsync(exe, BuildArgs(masterVideo, output, style, txt, font), output, durationSeconds,
                    Map(progress, from, to, stage), ct).ConfigureAwait(false);
                if (!r.Ok)
                {
                    if (ct.IsCancellationRequested) break;
                    Interlocked.Increment(ref _failed);
                    items.Add(new ViralHookRenderResult(style, false, null, copy, r.Message));
                    continue;
                }
                Interlocked.Increment(ref _rendered);
                items.Add(new ViralHookRenderResult(style, true, output, copy, "완료"));
                ok++;
            }
            if (ct.IsCancellationRequested) return Done(new(false, null, items, "취소됨"));
            if (ok == 0) return Done(new(false, null, items, "모든 후킹 변주 렌더 실패: " + (items.Count > 0 ? items[0].Message : "")));
            progress?.Report(new(100, "✅ 후킹 변주 완성"));
            return Done(new(true, dir, items, ok.ToString(CultureInfo.InvariantCulture) + "/" + list.Count.ToString(CultureInfo.InvariantCulture) + " 변주 렌더 완료"));
        }
        catch (OperationCanceledException) { return Done(Fail("취소됨")); }
        catch (Exception ex) { Program.Log("ViralHook.Render", ex); return Done(Fail("후킹 변주 렌더 실패: " + ex.Message)); }
        finally
        {
            foreach (var f in tmpFiles) { try { if (File.Exists(f)) File.Delete(f); } catch { /* 정리 실패 무시 */ } }
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>썸네일 1장 추출(0.5초 지점 · 실패 시 null · 예외 0).</summary>
    public async Task<string?> ExtractThumbnailAsync(string video, string output, CancellationToken ct = default)
    {
        var exe = _ffmpeg();
        if (exe is null || string.IsNullOrWhiteSpace(video) || !File.Exists(video)) return null;
        var args = new List<string>(16) { "-y", "-hide_banner", "-nostdin", "-ss", "0.5", "-i", video, "-frames:v", "1", "-q:v", "3", output };
        var r = await FfmpegVideoMixerService.RunAsync(exe, args, output, 0, null, ct).ConfigureAwait(false);
        return r.Ok ? output : null;
    }

    private ViralHookBatchResult Done(ViralHookBatchResult r)
    {
        LastAction = (r.Ok ? "✅ " : "⚠ ") + r.Message;
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("ViralHook.OnChange", ex); }
        return r;
    }

    private static ViralHookBatchResult Fail(string msg) => new(false, null, Array.Empty<ViralHookRenderResult>(), msg);

    private static List<ViralHookStyle> Distinct(IReadOnlyList<ViralHookStyle>? src)
    {
        var list = new List<ViralHookStyle>(3);
        if (src is null) return list;
        foreach (var s in src) if (!list.Contains(s)) list.Add(s);
        return list;
    }

    private static bool ContainsAny(string text, params string[] words)
    {
        foreach (var w in words) if (text.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string Trim(string s, int max)
    {
        var t = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length <= max ? t : t[..max].TrimEnd() + "…";
    }

    private static int Even(int v) => Math.Max(2, v & ~1);
    private static string Inv(double v) => FfmpegVideoMixerService.Inv(v);

    private static IProgress<double>? Map(IProgress<PostProductionProgress>? p, int from, int to, string stage)
        => p is null ? null : new Progress<double>(v => p.Report(new(from + (int)Math.Round(Math.Clamp(v, 0, 1) * (to - from)), stage)));
}
