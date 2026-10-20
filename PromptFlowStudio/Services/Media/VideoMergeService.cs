using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace PromptFlowStudio.Services.Media;

/// <summary>비디오 병합 결과. 실패 시 Ok=false + Message(사람이 읽는 원인).</summary>
public sealed record VideoMergeResult(bool Ok, string? OutputPath, string Message);

/// <summary>
/// 여러 로컬 MP4 를 하나로 병합하는 서비스 계약(ffmpeg 외부 프로세스).
///   구현은 <see cref="VideoMergeService"/>(BCL <see cref="Process"/> 만 사용 · 외부 NuGet 0).
/// </summary>
public interface IVideoMergeService
{
    /// <summary>ffmpeg.exe 를 찾을 수 있는지(exe 옆 또는 PATH). false 면 병합 불가.</summary>
    bool IsAvailable { get; }

    /// <summary>발견된 ffmpeg 경로(화면 표시용). 없으면 null.</summary>
    string? ResolvedFfmpegPath { get; }

    /// <summary>
    /// 입력 영상들을 순서대로 병합해 <paramref name="outputPath"/> 에 저장한다.
    ///   해상도가 서로 달라도 안전하도록 공통 해상도로 scale+pad 정규화 후 concat, H.264(libx264) 재인코딩한다.
    /// </summary>
    /// <param name="inputPaths">병합할 로컬 파일들(순서 = 영상 순서).</param>
    /// <param name="outputPath">출력 MP4 경로.</param>
    /// <param name="width">최종 가로 해상도(px). 세로 쇼츠면 height 가 더 크다.</param>
    /// <param name="height">최종 세로 해상도(px).</param>
    /// <param name="bgmPath">선택적 배경음악 로컬 경로. 지정 시 오디오 트랙 합성(-shortest · aac 128k · 짧으면 반복).</param>
    /// <param name="fade">true 면 컷마다 0.5초 페이드 인/아웃(dip-to-black) + BGM 마지막 1초 afade out.</param>
    /// <param name="progress">병합 진행률(0–100). 전체 길이는 입력 Duration 합(폴백: 컷수×5초)로 계산.</param>
    /// <param name="log">ffmpeg 원시 로그(stderr) 스트림(선택).</param>
    /// <param name="speed">템포 배속(1.0=원본). 영상 setpts + 오디오 atempo 로 적용(0.5~2.0 클램프).</param>
    /// <param name="narrations">컷별 나레이션 오디오 경로(없으면 null · 컷과 1:1). 하나라도 있으면 오디오 트랙 구성.</param>
    /// <param name="subtitles">컷별 자막 문구(없으면 null · 컷과 1:1). 있으면 drawtext 로 번인.</param>
    /// <param name="ducking">오디오 덕킹 — true 면 나레이션+BGM 동시 존재 시 나레이션 구간에 BGM 자동 감쇄(sidechaincompress). 기본 true.</param>
    /// <param name="bgmVolume">나레이션과 믹스될 때의 BGM 기준 음량(0.0~1.0). 기본 0.3. BGM 단독 재생 시엔 미적용(원음).</param>
    /// <param name="watermarkPath">브랜드 워터마크(로고) 이미지 경로. 지정 시 최종 영상 우측 상단에 오버레이(알파 유지). null/부재면 미적용.</param>
    /// <param name="smartSfx">스마트 SFX — true 면 <paramref name="kineticSfx"/> 가 없을 때 컷 경계마다 기본 효과음(whoosh)을 자동 믹싱.</param>
    /// <param name="colorGrades">컷별 AI 컬러 그레이딩 ffmpeg 필터 체인(예: <c>eq=...,vignette=PI/4</c>). ⚠ <see cref="FfmpegFilterSafety"/>로 재검증 후 삽입 — 위험/부재면 무시(원본 톤).</param>
    /// <param name="kineticWords">컷별 키네틱 자막 단어 목록(밀리초 타임코드). 있으면 정적 자막 대신 단어 단위 색상 전환 drawtext 번인.</param>
    /// <param name="kineticSfx">컷별 정밀 타임코드 SFX 트리거. 있으면 컷-로컬 시점에 지정 효과음을 밀리초 정확도로 믹싱(smartSfx 경계음보다 우선).</param>
    /// <param name="emotion">감정선 기반 동적 BGM 오케스트레이션 플랜(컷별 Tension→BGM 볼륨 엔벨로프/덕킹 강도/오디오 페이드). null 이면 기존 고정 믹싱.</param>
    /// <param name="transitions">컷 경계별 스마트 트랜지션(xfade). 있으면 concat 대신 xfade(+오디오 acrossfade)로 컷을 크로스페이드. null/부재면 기존 concat(동작 불변).</param>
    /// <param name="foley">컷별 생성형 폴리 큐(해결된 효과음 + 컷-로컬 ms + 공간 팬 -1~+1). 있으면 <c>pan</c> 필터로 좌/우 배치해 SFX 믹싱에 추가.</param>
    /// <param name="visualBadges">컷별 음소거 대응 시각 효과음 뱃지(이모지 결합). 있으면 컷-로컬 시점에 화면 상단 중앙 팝업 drawtext(박스)로 번인.</param>
    /// <param name="safePlatform">자막/뱃지 세이프존 대상 플랫폼(기본 None=기존 위치). 숏츠/릴스/틱톡 선택 시 UI 가림 영역을 피해 Y 오프셋 자동 보정.</param>
    Task<VideoMergeResult> MergeAsync(
        IReadOnlyList<string> inputPaths, string outputPath, int width, int height,
        string? bgmPath = null, bool fade = false, IProgress<double>? progress = null, IProgress<string>? log = null,
        CancellationToken ct = default,
        double speed = 1.0, IReadOnlyList<string?>? narrations = null, IReadOnlyList<string?>? subtitles = null,
        bool ducking = true, double bgmVolume = 0.3,
        string? watermarkPath = null, bool smartSfx = false,
        IReadOnlyList<string?>? colorGrades = null,
        IReadOnlyList<IReadOnlyList<KineticCaptionWord>?>? kineticWords = null,
        IReadOnlyList<IReadOnlyList<KineticSfxCue>?>? kineticSfx = null,
        EmotionPacing? emotion = null,
        IReadOnlyList<VideoTransition?>? transitions = null,
        IReadOnlyList<IReadOnlyList<FoleyCue>?>? foley = null,
        IReadOnlyList<IReadOnlyList<VisualSfxBadge>?>? visualBadges = null,
        SafePlatform safePlatform = SafePlatform.None,
        bool embossWatermark = false);

    /// <summary>영상 1초 지점 포스터(썸네일)를 <c>{videoPath}.jpg</c> 로 추출한다. ffmpeg 부재/실패 시 false(Graceful).</summary>
    Task<bool> ExtractPosterAsync(string videoPath, CancellationToken ct = default);

    /// <summary>영상 <paramref name="atSeconds"/> 지점의 프레임 1장을 <paramref name="outputJpgPath"/> 로 추출(트랜지션 분석용 엔딩/시작 프레임). 실패 시 null(Graceful).</summary>
    Task<string?> ExtractFrameAsync(string videoPath, double atSeconds, string outputJpgPath, CancellationToken ct = default);
}

/// <summary>
/// [PromptFlowStudio] ffmpeg 기반 비디오 병합 — 순수 BCL <see cref="Process"/> 로 <c>ffmpeg.exe</c> 를 호출(외부 NuGet 0).
///   ▸ 위치 가정: exe 와 같은 폴더의 ffmpeg.exe → 없으면 시스템 PATH.
///   ▸ 안전 병합: T2V/I2V 결과 해상도가 달라도 <c>-c copy</c> concat 은 재생오류가 나므로, filter_complex 로
///     각 입력을 공통 해상도(scale + pad 레터박스, setsar=1, fps 통일)로 맞춘 뒤 concat → <c>libx264</c> 재인코딩(yuv420p, +faststart).
///   ▸ Graceful: ffmpeg 부재/실행 실패/비정상 종료 시 예외를 던지지 않고 결과 record 로 원인을 돌려준다(호출부가 UI 표시).
///   ▸ 인자는 <see cref="ProcessStartInfo.ArgumentList"/> 로 전달 — 공백 포함 경로("PromptFlow Studio") 따옴표 문제 원천 차단.
///   출력 해상도(가로 HD/FHD·세로 쇼츠)는 <see cref="MergeAsync"/> 파라미터로 받는다 — 세로 포맷도 scale+pad 로 비율 유지·레터박스. fps 는 <c>Merge:Fps</c>(기본 30).
/// </summary>
public sealed class VideoMergeService(IConfiguration cfg) : IVideoMergeService
{
    private int Fps => int.TryParse(cfg["Merge:Fps"], out var v) && v > 0 ? v : 30;

    private const double ClipSeconds = 5.0;   // Wan 컷 길이(고정) — 페이드아웃 시점/BGM afade 계산용
    private const double FadeDur     = 0.5;   // 컷 페이드 인/아웃 길이(초)

    /// <inheritdoc/>
    public bool IsAvailable => ResolvedFfmpegPath is not null;

    /// <inheritdoc/>
    public string? ResolvedFfmpegPath => ResolveExisting();

    /// <inheritdoc/>
    public async Task<VideoMergeResult> MergeAsync(
        IReadOnlyList<string> inputPaths, string outputPath, int width, int height,
        string? bgmPath = null, bool fade = false, IProgress<double>? progress = null, IProgress<string>? log = null,
        CancellationToken ct = default,
        double speed = 1.0, IReadOnlyList<string?>? narrations = null, IReadOnlyList<string?>? subtitles = null,
        bool ducking = true, double bgmVolume = 0.3,
        string? watermarkPath = null, bool smartSfx = false,
        IReadOnlyList<string?>? colorGrades = null,
        IReadOnlyList<IReadOnlyList<KineticCaptionWord>?>? kineticWords = null,
        IReadOnlyList<IReadOnlyList<KineticSfxCue>?>? kineticSfx = null,
        EmotionPacing? emotion = null,
        IReadOnlyList<VideoTransition?>? transitions = null,
        IReadOnlyList<IReadOnlyList<FoleyCue>?>? foley = null,
        IReadOnlyList<IReadOnlyList<VisualSfxBadge>?>? visualBadges = null,
        SafePlatform safePlatform = SafePlatform.None,
        bool embossWatermark = false)
    {
        if (inputPaths is null || inputPaths.Count == 0)
            return new(false, null, "병합할 입력 영상이 없습니다.");
        foreach (var p in inputPaths)
            if (!File.Exists(p)) return new(false, null, "입력 파일을 찾을 수 없습니다: " + p);

        // 해상도 방어 — 잘못된 값이면 기본(1280×720). H.264 는 짝수 크기 필요 → 홀수면 -1 내림.
        var w = (width  > 0 ? width  : 1280) & ~1;
        var h = (height > 0 ? height : 720)  & ~1;

        var ffmpeg = ResolveExisting();
        if (ffmpeg is null)
            return new(false, null, "ffmpeg.exe 를 찾을 수 없습니다(exe 옆 또는 PATH 에 두세요).");

        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var hasBgm = !string.IsNullOrWhiteSpace(bgmPath) && File.Exists(bgmPath);
            var n = inputPaths.Count;
            var sp = Math.Clamp(speed <= 0 ? 1.0 : speed, 0.5, 2.0);   // atempo 유효범위
            var speedOn = sp > 1.001;

            // 나레이션(컷별 TTS) — 하나라도 실제 파일이 있으면 오디오 트랙 구성.
            var hasNarr = narrations is { Count: > 0 }
                          && Enumerable.Range(0, n).Any(i => i < narrations.Count && !string.IsNullOrEmpty(narrations[i]) && File.Exists(narrations[i]!));

            var bgmVol = Math.Clamp(bgmVolume <= 0 ? 0.3 : bgmVolume, 0.0, 1.0);   // 나레이션 믹스 시 BGM 기준 음량
            var duck = ducking && hasNarr && hasBgm;   // 덕킹은 '나레이션+BGM 동시' 일 때만 — 단독은 기존 흐름

            // 컷별 자막을 임시 텍스트 파일로 기록(drawtext textfile — 한글/특수문자 인라인 이스케이프 회피).
            var subFiles = new string?[n];
            if (subtitles is { Count: > 0 })
            {
                var subDir = Path.Combine(Path.GetTempPath(), "promptflow-subs");
                Directory.CreateDirectory(subDir);
                for (var i = 0; i < n; i++)
                {
                    var s = i < subtitles.Count ? subtitles[i] : null;
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    var f = Path.Combine(subDir, $"sub_{Guid.NewGuid():N}.txt");
                    File.WriteAllText(f, s!.Trim(), new UTF8Encoding(false));
                    subFiles[i] = f;
                }
            }
            var fontFile = FindKoreanFont();

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg, RedirectStandardError = true, RedirectStandardOutput = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-y");
            foreach (var inp in inputPaths) { psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(inp); }   // 0..n-1 = 비디오

            // 나레이션/무음 입력(나레이션 활성 시 컷마다 1개 — 컷과 1:1 정렬).
            var narrIdx = new int[n];
            if (hasNarr)
            {
                var idx = n;
                for (var i = 0; i < n; i++)
                {
                    var na = (narrations != null && i < narrations.Count) ? narrations[i] : null;
                    if (!string.IsNullOrEmpty(na) && File.Exists(na!))
                    {
                        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(na!);
                    }
                    else   // 나레이션 없는 컷은 컷 길이만큼 무음
                    {
                        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
                        psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(Inv(ClipSeconds));
                        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("anullsrc=r=44100:cl=stereo");
                    }
                    narrIdx[i] = idx++;
                }
            }

            var bgmIndex = -1;
            if (hasBgm)
            {
                psi.ArgumentList.Add("-stream_loop"); psi.ArgumentList.Add("-1");
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(bgmPath!);
                bgmIndex = n + (hasNarr ? n : 0);
            }

            // ── 워터마크(브랜드 로고) · 스마트 SFX 입력 ──
            var nextIdx = n + (hasNarr ? n : 0) + (hasBgm ? 1 : 0);
            var hasWm = !string.IsNullOrWhiteSpace(watermarkPath) && File.Exists(watermarkPath);
            var wmIndex = -1;
            if (hasWm) { psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(watermarkPath!); wmIndex = nextIdx++; }

            var hasAudioBase = hasNarr || hasBgm;                     // 기존 오디오 트랙 존재 여부
            var clipMs = (int)(ClipSeconds * 1000);

            // ── 스마트 트랜지션(xfade/acrossfade) — 컷 경계별 크로스페이드 길이/전환명. 없으면 0(=기존 concat 타임라인 · 동작 불변) ──
            var transDur = new double[Math.Max(0, n - 1)];
            var transName = new string[Math.Max(0, n - 1)];
            var transOn = false;
            if (transitions is not null && n >= 2)
            {
                for (var i = 0; i < n - 1; i++)
                {
                    var tr = i < transitions.Count ? transitions[i] : null;
                    transName[i] = "fade";
                    if (tr is null) continue;
                    var d = Math.Clamp(tr.DurationMs / 1000.0, 0.2, Math.Min(1.8, ClipSeconds * 0.6));   // 두 컷보다 짧게(xfade 요건)
                    transDur[i] = d; transName[i] = TransitionSafety.SafeXfade(tr.XfadeName);            // 화이트리스트 강제
                    if (d > 0.01) transOn = true;
                }
            }
            // 컷 시작 오프셋(초·배속 이전). concat=i*Clip, 트랜지션=크로스페이드만큼 압축. 모든 오디오/SFX 타이밍의 단일 기준.
            var cutStart = new double[n];
            for (var i = 1; i < n; i++) cutStart[i] = cutStart[i - 1] + ClipSeconds - transDur[i - 1];
            var totalPre = n > 0 ? cutStart[n - 1] + ClipSeconds : 0;   // 배속 이전 총 길이
            var totalDur = totalPre / sp;                              // 배속 반영 총 길이

            // ── SFX 큐 구성: 키네틱 정밀 트리거(밀리초) 우선, 없으면 smartSfx 컷 경계 whoosh + 생성형 폴리(공간 팬) ──
            //   각 큐 = (효과음 파일, 배속 반영 지연 ms, 팬 -1~+1). 파일 못 찾으면 스킵(Graceful).
            var sfxCues = new List<(string File, int DelayMs, double Pan)>();
            if (kineticSfx is not null)
            {
                for (var i = 0; i < n; i++)
                {
                    var cues = (i < kineticSfx.Count) ? kineticSfx[i] : null;
                    if (cues is null) continue;
                    foreach (var c in cues)
                    {
                        if (c is null) continue;
                        var f = SfxResolver.FindByType(c.SfxType);   // 타입(whoosh/pop 등) 우선, 없으면 기본 효과음
                        if (f is null) continue;
                        var local = Math.Clamp(c.TimestampMs, 0, clipMs) / 1000.0;             // 컷-로컬 초
                        var ms = (int)Math.Round((cutStart[i] + local) / sp * 1000.0);         // 최종 타임라인 ms(트랜지션 압축·배속 반영)
                        sfxCues.Add((f, Math.Max(0, ms), 0.0));
                    }
                }
            }
            if (sfxCues.Count == 0 && smartSfx && n > 1)   // 폴백: 컷 경계 whoosh
            {
                var f = FindSfx();
                if (f is not null)
                    for (var i = 1; i < n; i++)
                        sfxCues.Add((f, (int)Math.Round(cutStart[i] / sp * 1000.0), 0.0));   // 트랜지션 압축된 컷 경계
            }
            // 생성형 폴리 — 컷별 해결된 효과음을 컷-로컬 시점에 공간 팬(pan)으로 추가(기존 SFX 와 공존).
            if (foley is not null)
            {
                for (var i = 0; i < n; i++)
                {
                    var cues = (i < foley.Count) ? foley[i] : null;
                    if (cues is null) continue;
                    foreach (var c in cues)
                    {
                        if (c is null || string.IsNullOrWhiteSpace(c.AudioPath) || !File.Exists(c.AudioPath)) continue;
                        var local = Math.Clamp(c.TimestampMs, 0, clipMs) / 1000.0;
                        var ms = (int)Math.Round((cutStart[i] + local) / sp * 1000.0);
                        sfxCues.Add((c.AudioPath, Math.Max(0, ms), Math.Clamp(c.Pan, -1.0, 1.0)));
                    }
                }
            }
            var sfxOn = sfxCues.Count > 0;

            var silentIdx = -1;
            if (sfxOn && !hasAudioBase)                              // SFX 만 있고 다른 오디오 없으면 무음 베이스 필요
            {
                psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
                psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(Inv(totalDur));
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("anullsrc=r=44100:cl=stereo");
                silentIdx = nextIdx++;
            }
            var sfxInputIdx = new int[sfxCues.Count];
            for (var k = 0; k < sfxCues.Count; k++) { psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(sfxCues[k].File); sfxInputIdx[k] = nextIdx++; }

            // ── filter_complex: 비디오(scale/pad/fade/자막)→concat→배속, 오디오(나레이션/무음 concat + BGM 믹스)→배속 ──
            var fc = new StringBuilder();
            // ⚠ 트랜지션(xfade) 활성 시 컷별 dip-to-black 은 끈다(xfade 가 경계 전환을 담당 · 이중 페이드 방지).
            var fadeChain = (fade && !transOn) ? $",fade=t=in:st=0:d={Inv(FadeDur)},fade=t=out:st={Inv(ClipSeconds - FadeDur)}:d={Inv(FadeDur)}" : "";
            var fontSize = Math.Max(22, w / 22);
            // 멀티 플랫폼 세이프존 — 자막(하단 앵커)/뱃지(상단 앵커) Y 를 플랫폼 안전 밴드로 자동 보정(None=기존 0.13/0.10 · 바이트 동일).
            var yMargin = (int)(h * MultiPlatformSafeZoneService.CaptionBottomFraction(safePlatform));
            var wordSize = Math.Max(30, w / 14);          // 키네틱 단어(일반)
            var hlSize   = Math.Max(38, w / 11);          // 키네틱 단어(강조)
            var badgeSize = Math.Max(26, w / 18);         // 음소거 대응 시각 SFX 뱃지
            var badgeY = (int)(h * MultiPlatformSafeZoneService.BadgeTopFraction(safePlatform));   // 상단 중앙 팝업 위치(세이프존 보정)
            var wordFiles = new List<string>();           // 키네틱 단어/뱃지 임시 textfile(인코딩 후 정리)
            for (var i = 0; i < n; i++)
            {
                fc.Append($"[{i}:v]scale={w}:{h}:force_original_aspect_ratio=decrease,")
                  .Append($"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps={Fps}{fadeChain}");

                // AI 시네마틱 컬러 그레이딩(안전 검증된 vf 체인만) — 자막 '이전'에 적용(자막은 그레이딩 위에 선명).
                var grade = SafeGrade(colorGrades, i);
                if (grade is not null) fc.Append(',').Append(grade);

                var kw = (kineticWords is not null && i < kineticWords.Count) ? kineticWords[i] : null;
                if (kw is { Count: > 0 })
                {
                    // 키네틱 하이라이트 자막 — 단어별 drawtext(enable 로 컷-로컬 시간창에만 표시 · 강조어는 강조색/확대).
                    foreach (var word in kw)
                    {
                        if (word is null || string.IsNullOrWhiteSpace(word.Text)) continue;
                        var wf = WriteWordFile(word.Text);
                        if (wf is null) continue;
                        wordFiles.Add(wf);
                        var color = word.Highlight ? "0xFFE24D" : "white";   // 강조어=골드(0x 형식 · ffmpeg 전 버전 호환)
                        var wsz = word.Highlight ? hlSize : wordSize;
                        var st = Inv(Math.Max(0, word.StartMs) / 1000.0);
                        var en = Inv(Math.Max(word.StartMs + 1, word.EndMs) / 1000.0);
                        fc.Append(",drawtext=");
                        if (fontFile is not null) fc.Append($"fontfile={EscDrawPath(fontFile)}:");
                        fc.Append($"textfile={EscDrawPath(wf)}:fontcolor={color}:fontsize={wsz}:")
                          .Append($"borderw=3:bordercolor=black:x=(w-text_w)/2:y=h-text_h-{yMargin}:")
                          .Append($"enable='between(t,{st},{en})'");
                    }
                }
                else if (subFiles[i] is not null)   // 정적 숏츠 자막(노란 글자·검은 테두리 2px·중앙 하단)
                {
                    fc.Append(",drawtext=");
                    if (fontFile is not null) fc.Append($"fontfile={EscDrawPath(fontFile)}:");
                    fc.Append($"textfile={EscDrawPath(subFiles[i]!)}:fontcolor=yellow:fontsize={fontSize}:")
                      .Append($"borderw=2:bordercolor=black:x=(w-text_w)/2:y=h-text_h-{yMargin}:line_spacing=8");
                }

                // 음소거 시청 대응 시각 SFX 뱃지 — 상단 중앙 팝업(반투명 박스). 자막과 별개 레이어로 공존.
                //   ⚠ 이모지 색상 렌더는 ffmpeg/폰트 빌드 의존 — 미지원이면 텍스트만 표시(Graceful).
                var vb = (visualBadges is not null && i < visualBadges.Count) ? visualBadges[i] : null;
                if (vb is { Count: > 0 })
                {
                    foreach (var b in vb)
                    {
                        if (b is null || string.IsNullOrWhiteSpace(b.Text)) continue;
                        var bf = WriteWordFile(b.Label);
                        if (bf is null) continue;
                        wordFiles.Add(bf);
                        var bs = Inv(Math.Max(0, b.StartMs) / 1000.0);
                        var be = Inv(Math.Max(b.StartMs + 1, b.EndMs) / 1000.0);
                        fc.Append(",drawtext=");
                        if (fontFile is not null) fc.Append($"fontfile={EscDrawPath(fontFile)}:");
                        fc.Append($"textfile={EscDrawPath(bf)}:fontcolor=white:fontsize={badgeSize}:")
                          .Append($"box=1:boxcolor=black@0.58:boxborderw=16:borderw=2:bordercolor=black:")
                          .Append($"x=(w-text_w)/2:y={badgeY}:enable='between(t,{bs},{be})'");
                    }
                }
                fc.Append($"[v{i}];");
            }
            if (!transOn)
            {
                for (var i = 0; i < n; i++) fc.Append($"[v{i}]");
                fc.Append($"concat=n={n}:v=1:a=0[vc];");
            }
            else
            {
                // 스마트 트랜지션 xfade 체인 — 컷을 크로스페이드로 이어붙임(offset=압축된 컷 시작 · duration=경계 크로스페이드).
                var prev = "v0";
                for (var i = 1; i < n; i++)
                {
                    var outLbl = i == n - 1 ? "vc" : $"xf{i}";
                    fc.Append($"[{prev}][v{i}]xfade=transition={transName[i - 1]}:duration={Inv(transDur[i - 1])}:offset={Inv(cutStart[i])}[{outLbl}];");
                    prev = outLbl;
                }
            }
            fc.Append(speedOn ? $"[vc]setpts=PTS/{Inv(sp)}[outv];" : "[vc]null[outv];");

            // 브랜드 워터마크 오버레이(우측 상단 · 출력 폭의 ~14% · 알파 유지). ⚠ 자막/업스케일 이후이므로 로고는 선명.
            var vmap = "outv";
            if (hasWm)
            {
                if (embossWatermark)
                {
                    // B2B 3D 엠보싱 워터마크 — convolution 양각 커널 + 우측 상단 인라인 합성(EmbossWatermarkPipeline 결선).
                    fc.Append(EmbossWatermarkPipeline.BuildOverlay(wmIndex, w, "outv", "outw")).Append(';');
                }
                else
                {
                    var wmW = Math.Max(48, w / 7);
                    fc.Append($"[{wmIndex}:v]scale={wmW}:-1[wm];[outv][wm]overlay=W-w-24:24[outw];");
                }
                vmap = "outw";
            }

            // ── 감정선 동적 BGM 오케스트레이션(emotion) — 컷별 볼륨 엔벨로프 + 덕킹 강도(없으면 기존 고정값) ──
            var bgmEnv = new StringBuilder();
            var duckTh = 0.05; var duckRatio = 8.0;   // 기본(감정 데이터 없으면 기존 값 그대로)
            if (emotion is not null && emotion.Tensions.Count > 0)
            {
                for (var i = 0; i < n; i++)
                {
                    var tension = Math.Clamp(i < emotion.Tensions.Count ? emotion.Tensions[i] : 50, 0, 100);
                    var g = 0.8 + tension / 100.0 * 0.7;                   // 긴장↑ → BGM 고조(0.8~1.5배)
                    var ws = cutStart[i];
                    var we = i < n - 1 ? cutStart[i + 1] : totalPre;       // 비겹침 컷 창(배속 이전 시간축)
                    bgmEnv.Append($",volume={Inv(Math.Round(g, 3))}:enable='between(t,{Inv(Math.Round(ws, 3))},{Inv(Math.Round(we, 3))})'");
                }
                var dd = Math.Clamp(emotion.DuckingDepth, 0.0, 1.0);
                duckTh = Math.Round(0.03 + (1 - dd) * 0.09, 4);            // 깊을수록 낮은 threshold(잘 눌림)
                duckRatio = Math.Round(4 + dd * 6, 2);                     // 깊을수록 높은 ratio
            }
            var bgmEnvStr = bgmEnv.ToString();

            string? apre = null;
            if (hasNarr)
            {
                // 컷별 나레이션/무음을 44100·스테레오로 정규화(concat·sidechaincompress 포맷 일치) 후 컷 길이로 pad/trim.
                for (var i = 0; i < n; i++)
                    fc.Append($"[{narrIdx[i]}:a]aresample=44100,aformat=channel_layouts=stereo,apad,atrim=0:{Inv(ClipSeconds)},asetpts=N/SR/TB[na{i}];");
                if (!transOn)
                {
                    for (var i = 0; i < n; i++) fc.Append($"[na{i}]");
                    fc.Append($"concat=n={n}:v=0:a=1[nac];");
                }
                else
                {
                    // acrossfade 체인 — xfade 와 '동일 duration' 으로 오디오도 같은 만큼 압축 → 영상과 자동 동기.
                    var prev = "na0";
                    for (var i = 1; i < n; i++)
                    {
                        var outLbl = i == n - 1 ? "nac" : $"nx{i}";
                        fc.Append($"[{prev}][na{i}]acrossfade=d={Inv(transDur[i - 1])}[{outLbl}];");
                        prev = outLbl;
                    }
                }
                var narr = "nac";
                if (speedOn) { fc.Append($"[nac]atempo={Inv(sp)}[nas];"); narr = "nas"; }

                if (hasBgm)
                {
                    // BGM: 44100·스테레오 정규화 + 기준 음량(+감정 엔벨로프) + (배속). 나레이션과 시간축·포맷을 맞춘다.
                    var bgmSpeed = speedOn ? $",atempo={Inv(sp)}" : "";
                    fc.Append($"[{bgmIndex}:a]aresample=44100,aformat=channel_layouts=stereo,volume={Inv(bgmVol)}{bgmEnvStr}{bgmSpeed}[bg];");

                    if (duck)
                    {
                        // 오디오 덕킹: 나레이션을 [믹스용][사이드체인용] 2개로 분기 → 나레이션 신호로 BGM 감쇄(sidechaincompress) → 믹스.
                        //   감정(Tension)이 높은 씬일수록 duckTh↑/ratio↓ 로 덜 눌러 BGM 이 고조되게(요구사항). amix normalize=0.
                        fc.Append($"[{narr}]asplit=2[nam][nsc];");
                        fc.Append($"[bg][nsc]sidechaincompress=threshold={Inv(duckTh)}:ratio={Inv(duckRatio)}:attack=15:release=350:makeup=1:level_sc=1[bgd];");
                        fc.Append($"[nam][bgd]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[amx];");
                    }
                    else
                    {
                        // 덕킹 OFF: 고정 음량 BGM 과 단순 믹스(기존 흐름).
                        fc.Append($"[{narr}][bg]amix=inputs=2:duration=first:dropout_transition=0[amx];");
                    }
                    apre = "amx";
                }
                else apre = narr;   // 나레이션 단독
            }
            else if (hasBgm)
            {
                // BGM 단독 — 감정 엔벨로프 있으면 볼륨 고조 반영, 없으면 원음 유지(기존 흐름). 배속만 반영.
                if (bgmEnvStr.Length > 0)
                    fc.Append($"[{bgmIndex}:a]aresample=44100,aformat=channel_layouts=stereo,volume=1.0{bgmEnvStr}{(speedOn ? $",atempo={Inv(sp)}" : "")}[bgo];");
                else
                    fc.Append(speedOn ? $"[{bgmIndex}:a]atempo={Inv(sp)}[bgo];" : $"[{bgmIndex}:a]anull[bgo];");
                apre = "bgo";
            }

            if (apre is not null)
            {
                // 감정 아크 기반 페이드(있으면) → 없으면 fade 플래그의 1초 아웃 → 둘 다 없으면 무처리(기존 동작 보존).
                var fadeInS = emotion is not null ? Math.Clamp(emotion.FadeInMs / 1000.0, 0, totalDur / 2) : 0.0;
                var fadeOutS = emotion is not null ? Math.Clamp(emotion.FadeOutMs / 1000.0, 0.1, totalDur / 2)
                                                   : (fade ? 1.0 : 0.0);
                if (fadeInS > 0.01 || fadeOutS > 0.01)
                {
                    var af = new StringBuilder($"[{apre}]");
                    if (fadeInS > 0.01) af.Append($"afade=t=in:st=0:d={Inv(Math.Round(fadeInS, 3))},");
                    if (fadeOutS > 0.01) { var outAt = Math.Max(0, totalDur - fadeOutS); af.Append($"afade=t=out:st={Inv(Math.Round(outAt, 3))}:d={Inv(Math.Round(fadeOutS, 3))},"); }
                    fc.Append(af.ToString().TrimEnd(',')).Append("[outa];");
                }
                else fc.Append($"[{apre}]anull[outa];");
            }

            // ── SFX 믹싱: 키네틱 정밀 트리거 또는 컷 경계 whoosh(sfxCues)를 지연 배치해 믹싱 ──
            //   기존 오디오([outa])가 있으면 그 위에, 없으면 무음 베이스 위에 얹는다. adelay 는 배속 반영한 컷-로컬 시각(ms).
            string? amap = apre is not null ? "outa" : null;
            if (sfxOn)
            {
                var baseLbl = hasAudioBase ? "outa" : "sil";
                if (!hasAudioBase) fc.Append($"[{silentIdx}:a]anull[sil];");
                for (var k = 0; k < sfxCues.Count; k++)
                {
                    var ms = sfxCues[k].DelayMs;
                    var pan = sfxCues[k].Pan;
                    // 3D 공간 팬(-1 좌 ~ +1 우) — 좌/우 채널 게인으로 위치. 0(중앙)이면 팬 필터 생략.
                    var panSeg = "";
                    if (Math.Abs(pan) > 0.01)
                    {
                        var gl = Inv(Math.Round(pan <= 0 ? 1.0 : 1.0 - pan, 3));
                        var gr = Inv(Math.Round(pan >= 0 ? 1.0 : 1.0 + pan, 3));
                        panSeg = $"pan=stereo|c0={gl}*c0|c1={gr}*c1,";
                    }
                    fc.Append($"[{sfxInputIdx[k]}:a]aresample=44100,aformat=channel_layouts=stereo,volume=0.55,{panSeg}adelay={ms}|{ms}[sfx{k}];");
                }
                fc.Append($"[{baseLbl}]");
                for (var k = 0; k < sfxCues.Count; k++) fc.Append($"[sfx{k}]");
                fc.Append($"amix=inputs={sfxCues.Count + 1}:duration=first:dropout_transition=0:normalize=0[outaS];");
                amap = "outaS";
            }

            var filter = fc.ToString().TrimEnd(';');   // ⚠ 트레일링 ';' 는 빈 필터체인 오류 유발
            var hasAudioOut = amap is not null;

            psi.ArgumentList.Add("-filter_complex"); psi.ArgumentList.Add(filter);
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add($"[{vmap}]");
            psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
            psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
            psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("veryfast");
            psi.ArgumentList.Add("-crf"); psi.ArgumentList.Add("20");
            psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
            if (hasAudioOut)
            {
                psi.ArgumentList.Add("-map"); psi.ArgumentList.Add($"[{amap}]");
                psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
                psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("192k");
                psi.ArgumentList.Add("-shortest");
            }
            else psi.ArgumentList.Add("-an");
            psi.ArgumentList.Add("-progress"); psi.ArgumentList.Add("pipe:1");
            psi.ArgumentList.Add(outputPath);

            // ── 진행률: stderr 헤더의 Duration 합(앞쪽 비디오 입력 수만큼) = 전체 길이, stdout -progress 의 out_time 으로 현재 위치 ──
            var videoCount = inputPaths.Count;
            var fallbackTotal = videoCount * 5.0;   // Wan 컷당 5초 고정(폴백)
            double durSum = 0; int durCount = 0; int lastPct = -1;

            void ReportPct(double curSeconds)
            {
                if (progress is null) return;
                // 배속 적용 시 출력 길이는 원본/sp 이므로 진행률 분모도 나눠 준다.
                var total = ((durCount >= videoCount && durSum > 0) ? durSum : fallbackTotal) / sp;
                if (total <= 0) return;
                var pct = (int)Math.Clamp(curSeconds / total * 100.0, 0, 100);
                if (pct != lastPct) { lastPct = pct; progress.Report(pct); }
            }

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var err = new StringBuilder();

            proc.ErrorDataReceived += (_, e) =>
            {
                var line = e.Data;
                if (line is null) return;

                err.AppendLine(line);
                if (err.Length > 16000) err.Remove(0, err.Length - 12000);   // tail 만 유지(장시간 인코딩 메모리 누수 방지)
                log?.Report(line);

                // 입력별 Duration 은 앞쪽 videoCount 개만 합산(BGM Duration 은 제외).
                if (durCount < videoCount)
                {
                    var dm = DurationRx.Match(line);
                    if (dm.Success && TryHms(dm, out var d)) { durSum += d; durCount++; }
                }
            };

            proc.OutputDataReceived += (_, e) =>
            {
                var line = e.Data;
                if (line is null || progress is null) return;

                // -progress 출력: out_time_us=microsec (우선) 또는 out_time=HH:MM:SS.us
                var um = ProgUsRx.Match(line);
                if (um.Success && long.TryParse(um.Groups[1].Value, out var us)) { ReportPct(us / 1_000_000.0); return; }
                var om = ProgTimeRx.Match(line);
                if (om.Success && TryHms(om, out var s)) ReportPct(s);
            };

            if (!proc.StartCompat())
                return new(false, null, "ffmpeg 프로세스를 시작하지 못했습니다.");
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.KillCompat(entireProcessTree: true); } catch { /* 무시 */ }
                return new(false, null, "병합이 취소되었습니다.");
            }

            // 자막 임시파일 정리(인코딩 종료 후 불필요).
            foreach (var sf in subFiles) { try { if (sf is not null && File.Exists(sf)) File.Delete(sf); } catch { /* 무시 */ } }
            foreach (var wf in wordFiles) { try { if (File.Exists(wf)) File.Delete(wf); } catch { /* 무시 */ } }

            if (proc.ExitCode != 0)
                return new(false, null, $"ffmpeg 실패(종료코드 {proc.ExitCode}). {Tail(err.ToString())}");
            if (!File.Exists(outputPath))
                return new(false, null, "ffmpeg 가 출력 파일을 생성하지 못했습니다.");

            try { await ExtractPosterAsync(outputPath, ct).ConfigureAwait(false); } catch { /* 썸네일 실패는 병합 성공에 영향 없음 */ }
            progress?.Report(100);
            return new(true, outputPath, "병합 완료");
        }
        catch (System.ComponentModel.Win32Exception ex)   // ffmpeg 실행 불가(PATH/권한/아키텍처)
        {
            return new(false, null, "ffmpeg 실행 실패: " + ex.Message);
        }
        catch (Exception ex)
        {
            Program.Log("VideoMergeService.MergeAsync", ex);
            return new(false, null, "병합 실패: " + ex.Message);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ExtractPosterAsync(string videoPath, CancellationToken ct = default)
    {
        var ffmpeg = ResolveExisting();
        if (ffmpeg is null || string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) return false;
        var jpg = videoPath + ".jpg";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg, RedirectStandardError = true, RedirectStandardOutput = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add("00:00:01");   // 1초 지점(빠른 seek)
            psi.ArgumentList.Add("-i");  psi.ArgumentList.Add(videoPath);
            psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-q:v"); psi.ArgumentList.Add("2");
            psi.ArgumentList.Add(jpg);

            using var proc = new Process { StartInfo = psi };
            proc.ErrorDataReceived += (_, __) => { };   // 버퍼 드레인(데드락 방지)
            proc.OutputDataReceived += (_, __) => { };
            if (!proc.StartCompat()) return false;
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
            try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { if (!proc.HasExited) proc.KillCompat(entireProcessTree: true); } catch { } return false; }

            return proc.ExitCode == 0 && File.Exists(jpg);
        }
        catch (Exception ex) { Program.Log("VideoMergeService.ExtractPoster", ex); return false; }
    }

    /// <inheritdoc/>
    public async Task<string?> ExtractFrameAsync(string videoPath, double atSeconds, string outputJpgPath, CancellationToken ct = default)
    {
        var ffmpeg = ResolveExisting();
        if (ffmpeg is null || string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) return null;
        try
        {
            var dir = Path.GetDirectoryName(outputJpgPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg, RedirectStandardError = true, RedirectStandardOutput = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(Inv(Math.Max(0, atSeconds)));   // 지정 지점(빠른 seek)
            psi.ArgumentList.Add("-i");  psi.ArgumentList.Add(videoPath);
            psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-q:v"); psi.ArgumentList.Add("3");
            psi.ArgumentList.Add(outputJpgPath);

            using var proc = new Process { StartInfo = psi };
            proc.ErrorDataReceived += (_, __) => { };   // 버퍼 드레인(데드락 방지)
            proc.OutputDataReceived += (_, __) => { };
            if (!proc.StartCompat()) return null;
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
            try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { if (!proc.HasExited) proc.KillCompat(entireProcessTree: true); } catch { } return null; }

            return proc.ExitCode == 0 && File.Exists(outputJpgPath) ? outputJpgPath : null;
        }
        catch (Exception ex) { Program.Log("VideoMergeService.ExtractFrame", ex); return null; }
    }

    /// <summary>소수점을 항상 '.'(InvariantCulture)로 — ffmpeg 인자용(로캘 무관).</summary>
    private static string Inv(double d) => d.ToString(CultureInfo.InvariantCulture);

    /// <summary>drawtext 경로 이스케이프 — 공유 <see cref="FontResolver"/>(역슬래시→'/', 콜론→'\:' · 로캘 무관).</summary>
    private static string EscDrawPath(string p) => FontResolver.EscapeDrawPath(p);

    /// <summary>한글 번인 폰트 탐색 — 공유 <see cref="FontResolver"/>(데이터폴더 시딩 → 앱번들 → 시스템 → null).</summary>
    private static string? FindKoreanFont() => FontResolver.FindKoreanFont(bold: false);

    /// <summary>컷 전환 효과음 탐색 — 공유 <see cref="SfxResolver"/>(데이터폴더 시딩 → 앱번들 → null). 없으면 SFX 믹싱 생략(Graceful).</summary>
    private static string? FindSfx() => SfxResolver.FindTransitionSfx();

    /// <summary>컷 i 의 컬러 그레이딩 필터를 <see cref="FfmpegFilterSafety"/>로 재검증(방어 심층화). 없거나 위험하면 null → 그레이딩 생략.</summary>
    private static string? SafeGrade(IReadOnlyList<string?>? grades, int i)
    {
        if (grades is null || i >= grades.Count) return null;
        return FfmpegFilterSafety.SanitizeColorGrade(grades[i]);
    }

    /// <summary>키네틱 단어 1개를 임시 UTF-8 textfile 로 기록(인라인 이스케이프 회피). 실패 시 null(해당 단어 생략).</summary>
    private static string? WriteWordFile(string text)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "promptflow-kinetic");
            Directory.CreateDirectory(dir);
            var f = Path.Combine(dir, $"kw_{Guid.NewGuid():N}.txt");
            File.WriteAllText(f, text.Trim(), new UTF8Encoding(false));
            return f;
        }
        catch { return null; }
    }

    /// <summary>ffmpeg 실행 파일 위치 결정 — exe 옆(기존 배포 관례) 우선 → <see cref="CliPathResolver"/> 3단계(Tools\ffmpeg\ 번들 → 로컬앱데이터 → PATH). 못 찾으면 null.</summary>
    private static string? ResolveExisting()
    {
        try
        {
            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;

            // Zero-Config 자동 탐색: Tools\ffmpeg\ffmpeg.exe(앱번들/로컬앱데이터) → 시스템 PATH.
            var resolved = PromptFlowStudio.Services.Engines.CliPathResolver.GetExecutablePath("ffmpeg", "ffmpeg.exe");
            if (resolved is not null) return resolved;
        }
        catch { /* 무시 → null */ }
        return null;
    }

    /// <summary>ffmpeg stderr 의 마지막 부분만(오류 메시지용).</summary>
    private static string Tail(string s, int n = 600) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s.Trim() : "…" + s[^n..].Trim());

    // ── 진행률 파싱 정규식 ──
    private static readonly Regex DurationRx = new(@"Duration:\s*(\d+):(\d{2}):(\d{2})(?:\.(\d+))?", RegexOptions.Compiled);
    private static readonly Regex ProgUsRx   = new(@"out_time_us=(\d+)", RegexOptions.Compiled);
    private static readonly Regex ProgTimeRx = new(@"out_time=(\d+):(\d{2}):(\d{2})(?:\.(\d+))?", RegexOptions.Compiled);

    /// <summary>HH:MM:SS(.frac) 매치(그룹 1~4)를 초 단위로. 실패 시 false.</summary>
    private static bool TryHms(Match m, out double seconds)
    {
        seconds = 0;
        if (!int.TryParse(m.Groups[1].Value, out var hh)) return false;
        if (!int.TryParse(m.Groups[2].Value, out var mm)) return false;
        if (!int.TryParse(m.Groups[3].Value, out var ss)) return false;
        double frac = 0;
        if (m.Groups[4].Success) double.TryParse("0." + m.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out frac);
        seconds = hh * 3600 + mm * 60 + ss + frac;
        return true;
    }
}
