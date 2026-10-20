using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#if ANDROID
using Android.Content;
using Android.Speech;
using Microsoft.Maui.ApplicationModel;
#endif
#if IOS
using AVFoundation;
using Foundation;
using Speech;
#endif

namespace PromptFlowStudio.Services.Mobile;

// ════════════════════════════════════════════════════════════════════════════
//  [PromptFlow Studio v5.4 · Task3] 공간 음성-비전 앵커링 프롬프터 시임(SpatialVoicePrompter)
//   ▸ 스마트폰 카메라로 현장을 비추며 음성 명령 → 카메라 인식 좌표(앵커)에 시네마틱 프롬프트를 3D 공간에 앵커링.
//   ▸ 음성 인식(플랫폼 STT) + 위치/방위(Compass·Sensors)를 결합해 자연어를 구조화된 Wan 워크플로우 노드로 변환.
//   ▸ 데스크톱은 <see cref="DesktopSpatialVoicePrompter"/>(마이크 STT 미지원 → 데모 문장 구조화로 UI 흐름 검증).
//     모바일 MAUI 헤드는 <c>#if ANDROID/IOS</c> 네이티브(Android.Speech / iOS Speech)로 대체(DI override).
//   ⚠ v4.4.4 하드닝: null/빈 입력 방어 · CancellationToken 이식 · IDisposable 멱등 해제(마이크/세션 릭 방어).
//   ⚠ 순수 계약/DTO/구조화기는 100% BCL — 데스크톱(net8.0-windows) 컴파일 정합(외부 SDK 0).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 공간 앵커 — 카메라 프리뷰 상의 정규화 좌표(0~1)와 단말 방위/피치(도). 사물 인식 좌표에 프롬프트를 고정한다.
/// </summary>
public sealed record SpatialAnchor(string Id, double ScreenX, double ScreenY, double HeadingDeg, double PitchDeg, string? Label = null)
{
    /// <summary>화면 정중앙·정북(0°) 기본 앵커.</summary>
    public static SpatialAnchor Center => new("anchor-center", 0.5, 0.5, 0, 0);

    /// <summary>정규화 좌표를 0~1 로 클램프한 안전 사본(이상값 유입 방어).</summary>
    public SpatialAnchor Clamped() => this with
    {
        ScreenX = Math.Clamp(double.IsFinite(ScreenX) ? ScreenX : 0.5, 0, 1),
        ScreenY = Math.Clamp(double.IsFinite(ScreenY) ? ScreenY : 0.5, 0, 1),
        HeadingDeg = Norm360(double.IsFinite(HeadingDeg) ? HeadingDeg : 0),
        PitchDeg = Math.Clamp(double.IsFinite(PitchDeg) ? PitchDeg : 0, -90, 90),
    };

    private static double Norm360(double d) { d %= 360; return d < 0 ? d + 360 : d; }
}

/// <summary>음성 → 구조화된 공간 프롬프트 노드(주체·분위기·조명·카메라·동작 + 합성 Wan 프롬프트 + 태그).</summary>
public sealed record SpatialPromptNode(
    string Subject, string Atmosphere, string Lighting, string Camera, string Action,
    string WanPrompt, IReadOnlyList<string> Tags, SpatialAnchor Anchor);

/// <summary>공간 음성 캡처 결과 — 성공여부/원문/구조화 노드/메시지.</summary>
public sealed record SpatialVoiceResult(bool Ok, string Transcript, SpatialPromptNode? Node, string Message)
{
    public static SpatialVoiceResult Fail(string msg) => new(false, "", null, msg);
}

/// <summary>
/// 공간 음성-비전 앵커링 프롬프터 계약 — 마이크 STT 로 앵커에 시네마틱 프롬프트를 생성한다.
/// </summary>
public interface ISpatialVoicePrompter : IDisposable
{
    /// <summary>이 호스트에서 실시간 음성 인식 지원 여부(데스크톱 false).</summary>
    bool IsSupported { get; }
    /// <summary>현재 청취(녹음) 중인지.</summary>
    bool IsListening { get; }
    /// <summary>부분 인식 방송(실시간 자막 UX). 구독은 항상 허용.</summary>
    event Action<string>? OnPartial;
    /// <summary>앵커를 대상으로 1회 음성 캡처 → 구조화 노드 반환(취소/실패는 Fail).</summary>
    Task<SpatialVoiceResult> CaptureAsync(SpatialAnchor anchor, CancellationToken ct = default);
    /// <summary>진행 중인 캡처 취소(멱등 · 마이크 세션 해제).</summary>
    Task CancelAsync();
}

/// <summary>
/// [순수 BCL] 자연어(한/영) → 구조화된 공간 프롬프트 노드 변환기 — 외부 NLU/SDK 0(오프라인 · 테스트 대상).
///   키워드 사전(분위기·조명·위치·카메라·주체)을 스캔해 시네마틱 Wan 프롬프트를 조립한다.
/// </summary>
public static class SpatialPromptStructurer
{
    private sealed record Kw(string En, params string[] Keys);

    // 분위기(안개/연기/비/눈/먼지/증기) — 한국어 + 영어 동의어.
    private static readonly Kw[] Atmos =
    {
        new("volumetric fog", "안개", "fog", "mist", "물안개"),
        new("drifting smoke", "연기", "smoke", "매연"),
        new("heavy rain", "비", "빗", "rain", "폭우", "장대비"),
        new("falling snow", "눈", "snow", "함박눈", "눈보라"),
        new("floating dust", "먼지", "dust", "분진"),
        new("rising steam", "수증기", "증기", "steam", "김"),
        new("glowing embers", "불티", "불꽃", "ember", "spark"),
    };

    // 분위기 동작(피어오름/흩날림/자욱).
    private static readonly Kw[] AtmosMotion =
    {
        new("slowly rising", "피어오", "솟아", "올라", "rising", "rise", "상승"),
        new("drifting down", "흩날", "내리", "falling", "떨어", "drift"),
        new("swirling", "휘몰", "소용돌", "swirl", "휘감"),
        new("settling thick", "자욱", "짙게", "깔리", "thick"),
    };

    // 조명(붉은/푸른/노을/네온/역광/저조도).
    private static readonly Kw[] Lights =
    {
        new("dramatic red lighting", "붉은", "빨간", "적색", "red"),
        new("cold blue lighting", "푸른", "파란", "청색", "blue"),
        new("golden hour lighting", "노을", "골든", "황금", "주황", "golden", "amber", "sunset"),
        new("vivid neon lighting", "네온", "neon"),
        new("hard backlight", "역광", "backlit", "backlight", "실루엣", "silhouette"),
        new("soft white lighting", "하얀", "백색", "white", "밝은"),
        new("low-key moody lighting", "어두", "어둠", "dark", "저조도", "그림자"),
        new("flickering firelight", "불빛", "화염", "firelight", "촛불"),
    };

    // 위치(앵커 상대) — 주체 대비 프롬프트 배치.
    private static readonly Kw[] Positions =
    {
        new("behind", "뒤", "후방", "behind", "back of"),
        new("in front of", "앞", "전방", "front", "before"),
        new("above", "위", "상단", "above", "over", "top of"),
        new("below", "아래", "하단", "below", "under"),
        new("beside", "옆", "측면", "beside", "next to", "side of"),
        new("around", "주위", "둘레", "around", "surrounding"),
    };

    // 카메라 이동.
    private static readonly Kw[] Cameras =
    {
        new("camera slowly pushing in", "접근", "다가", "줌인", "push in", "zoom in", "다가가", "밀고"),
        new("camera pulling out", "후퇴", "멀어", "줌아웃", "pull out", "zoom out", "빠지"),
        new("camera orbiting the subject", "회전", "돌면서", "돌며", "orbit", "선회", "공전"),
        new("camera panning", "팬", "훑", "pan", "좌우로"),
        new("camera craning up", "상승샷", "크레인", "치솟", "crane", "부감"),
        new("locked-off static camera", "고정", "정지샷", "static", "고정샷"),
    };

    // 주체 명사(우선순위 스캔).
    private static readonly Kw[] Subjects =
    {
        new("the machine", "기계", "장비", "설비", "machine", "equipment"),
        new("the robot", "로봇", "robot", "암", "arm"),
        new("the vehicle", "자동차", "차량", "차", "car", "vehicle", "truck"),
        new("the building", "건물", "사옥", "빌딩", "building", "tower"),
        new("the product", "제품", "상품", "product"),
        new("the person", "사람", "인물", "person", "man", "woman", "작업자", "operator"),
        new("the landscape", "풍경", "전경", "landscape", "scenery", "계곡", "valley"),
    };

    /// <summary>자연어 + 앵커 → 구조화 노드. null/빈 문자열도 안전(기본 노드 반환).</summary>
    public static SpatialPromptNode Structure(string? transcript, SpatialAnchor anchor)
    {
        var a = (anchor ?? SpatialAnchor.Center).Clamped();
        var text = (transcript ?? "").Trim();
        var lower = text.ToLowerInvariant();

        var subject = Match(lower, Subjects) ?? "the subject";
        var position = Match(lower, Positions);
        var atmos = Match(lower, Atmos);
        var atmosMotion = Match(lower, AtmosMotion);
        var light = Match(lower, Lights);
        var camera = Match(lower, Cameras);

        // 비디오 의도 감지 → 카메라 미지정 시 기본 시네마틱 무브 부여.
        var wantsVideo = ContainsAny(lower, "비디오", "영상", "동영상", "video", "clip", "촬영", "생성", "만들");
        if (camera is null && wantsVideo) camera = "camera slowly pushing in";

        // 조립 — 시네마틱 Wan 프롬프트(영문).
        var subjectPhrase = position is null ? subject : $"{position} {subject}";
        var atmosPhrase = atmos is null ? "" : (atmosMotion is null ? atmos : $"{atmos} {atmosMotion}");

        var parts = new List<string> { "cinematic video" };
        if (light is not null) parts.Add($"{light} illuminating {subjectPhrase}");
        else parts.Add($"featuring {subjectPhrase}");
        if (atmosPhrase.Length > 0) parts.Add(atmosPhrase);
        if (camera is not null) parts.Add(camera);
        // 앵커 방위 → 공간 시점 힌트(현장 방향 보존).
        parts.Add($"anchored at screen ({a.ScreenX:0.00},{a.ScreenY:0.00}), facing {Bearing(a.HeadingDeg)}");
        parts.Add("volumetric lighting, ultra-detailed, sharp focus, 8k");

        var wan = string.Join(", ", parts);

        var tags = new List<string>();
        if (atmos is not null) tags.Add(atmos.Split(' ').Last());
        if (light is not null) tags.Add(light.Split(' ')[light.Split(' ').Length >= 2 ? 1 : 0]);
        if (camera is not null) tags.Add("motion");
        tags.Add(Bearing(a.HeadingDeg));

        return new SpatialPromptNode(
            Subject: subjectPhrase,
            Atmosphere: atmosPhrase.Length > 0 ? atmosPhrase : "(없음)",
            Lighting: light ?? "(기본광)",
            Camera: camera ?? "(고정)",
            Action: wantsVideo ? "video generation" : "still composition",
            WanPrompt: wan,
            Tags: tags,
            Anchor: a);
    }

    private static string? Match(string lower, Kw[] table)
    {
        if (lower.Length == 0) return null;
        foreach (var k in table)
            foreach (var key in k.Keys)
                if (lower.Contains(key, StringComparison.OrdinalIgnoreCase)) return k.En;
        return null;
    }

    private static bool ContainsAny(string lower, params string[] keys)
    {
        foreach (var k in keys) if (lower.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>방위각(도) → 8방위 영문(N/NE/E…).</summary>
    public static string Bearing(double headingDeg)
    {
        var d = headingDeg % 360; if (d < 0) d += 360;
        string[] names = { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };
        var idx = (int)Math.Round(d / 45.0) % 8;
        return names[idx];
    }
}

/// <summary>
/// 데스크톱 폴백 — 마이크 STT 미지원(<c>IsSupported=false</c>). CaptureAsync 는 데모 문장을 구조화해
///   UI 흐름(앵커→노드 변환)을 데스크톱에서도 검증 가능하게 한다(Graceful). 실제 인식은 모바일 헤드가 담당.
/// </summary>
public sealed class DesktopSpatialVoicePrompter : ISpatialVoicePrompter
{
    private bool _disposed;
    public bool IsSupported => false;
    public bool IsListening => false;
    public event Action<string>? OnPartial { add { } remove { } }

    public Task<SpatialVoiceResult> CaptureAsync(SpatialAnchor anchor, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return Task.FromResult(SpatialVoiceResult.Fail("취소되었습니다."));
        const string demo = "이 기계 뒤에서 안개가 피어오르고 붉은 조명이 비추는 비디오 생성";
        var node = SpatialPromptStructurer.Structure(demo, anchor);
        return Task.FromResult(new SpatialVoiceResult(true, demo, node,
            "🖥️ 데스크톱 시뮬레이션(음성 미지원) — 데모 문장을 구조화했습니다. 모바일에서 실제 음성으로 앵커링됩니다."));
    }

    public Task CancelAsync() => Task.CompletedTask;

    public void Dispose()
    {
        if (_disposed) return;   // 멱등
        _disposed = true;
    }
}

#if ANDROID
// ════════════════════════════════════════════════════════════════════════════
//  [MAUI 안드로이드 전용] Android.Speech.SpeechRecognizer(ko-KR · 부분결과 방송) + 마이크 권한.
//   ⚠ SpeechRecognizer 는 메인스레드 생성/사용 필수 → MainThread 마샬링. 세션은 Cancel/Destroy 로 멱등 해제.
// ════════════════════════════════════════════════════════════════════════════

public sealed class MauiSpatialVoicePrompter : ISpatialVoicePrompter
{
    private SpeechRecognizer? _recognizer;
    private bool _disposed;

    public bool IsSupported => SpeechRecognizer.IsRecognitionAvailable(Android.App.Application.Context);
    public bool IsListening { get; private set; }
    public event Action<string>? OnPartial;

    public Task<SpatialVoiceResult> CaptureAsync(SpatialAnchor anchor, CancellationToken ct = default)
    {
        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try
            {
                var mic = await Permissions.RequestAsync<Permissions.Microphone>();
                if (mic != PermissionStatus.Granted) return SpatialVoiceResult.Fail("마이크 권한이 거부되었습니다.");
                if (!IsSupported) return SpatialVoiceResult.Fail("이 기기에서 음성 인식을 사용할 수 없습니다.");

                var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var listener = new SttListener(tcs, t => OnPartial?.Invoke(t));

                _recognizer?.Destroy();
                _recognizer = SpeechRecognizer.CreateSpeechRecognizer(Android.App.Application.Context);
                _recognizer.SetRecognitionListener(listener);

                using var intent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
                intent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
                intent.PutExtra(RecognizerIntent.ExtraLanguage, "ko-KR");
                intent.PutExtra(RecognizerIntent.ExtraPartialResults, true);

                IsListening = true;
                _recognizer.StartListening(intent);

                using var reg = ct.Register(() => { try { _recognizer?.Cancel(); } catch { } tcs.TrySetResult(null); });
                var transcript = await tcs.Task.ConfigureAwait(false);

                IsListening = false;
                try { _recognizer?.Destroy(); } catch { } _recognizer = null;

                if (string.IsNullOrWhiteSpace(transcript)) return SpatialVoiceResult.Fail("음성을 인식하지 못했습니다.");
                var node = SpatialPromptStructurer.Structure(transcript, anchor);
                return new SpatialVoiceResult(true, transcript!, node, "음성 → 공간 프롬프트 노드 구조화 완료.");
            }
            catch (Exception ex) { IsListening = false; return SpatialVoiceResult.Fail("음성 인식 오류: " + ex.Message); }
        });
    }

    public Task CancelAsync()
    {
        return MainThread.InvokeOnMainThreadAsync(() =>
        {
            try { _recognizer?.Cancel(); } catch { }
            IsListening = false;
        });
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;   // 멱등
        try { _recognizer?.Destroy(); } catch { }
        _recognizer = null; OnPartial = null;
    }

    private sealed class SttListener : Java.Lang.Object, IRecognitionListener
    {
        private readonly TaskCompletionSource<string?> _tcs;
        private readonly Action<string> _partial;
        public SttListener(TaskCompletionSource<string?> tcs, Action<string> partial) { _tcs = tcs; _partial = partial; }

        public void OnResults(Android.OS.Bundle? results)
        {
            var list = results?.GetStringArrayList(SpeechRecognizer.ResultsRecognition);
            _tcs.TrySetResult(list is { Count: > 0 } ? list[0] : null);
        }
        public void OnPartialResults(Android.OS.Bundle? partial)
        {
            var list = partial?.GetStringArrayList(SpeechRecognizer.ResultsRecognition);
            if (list is { Count: > 0 } && list[0] is { } p) _partial(p);
        }
        public void OnError(SpeechRecognizerError error) => _tcs.TrySetResult(null);
        public void OnReadyForSpeech(Android.OS.Bundle? p) { }
        public void OnBeginningOfSpeech() { }
        public void OnRmsChanged(float rms) { }
        public void OnBufferReceived(byte[]? buffer) { }
        public void OnEndOfSpeech() { }
        public void OnEvent(int eventType, Android.OS.Bundle? p) { }
    }
}
#elif IOS
// ════════════════════════════════════════════════════════════════════════════
//  [MAUI iOS 전용] Speech.SFSpeechRecognizer + AVFoundation 오디오 엔진(ko-KR).
//   ⚠ Info.plist: NSSpeechRecognitionUsageDescription / NSMicrophoneUsageDescription 필요.
// ════════════════════════════════════════════════════════════════════════════

public sealed class MauiSpatialVoicePrompter : ISpatialVoicePrompter
{
    private AVAudioEngine? _engine;
    private SFSpeechRecognizer? _recognizer;
    private SFSpeechAudioBufferRecognitionRequest? _request;
    private SFSpeechRecognitionTask? _task;
    private bool _disposed;

    public bool IsSupported => new SFSpeechRecognizer(NSLocale.FromLocaleIdentifier("ko-KR"))?.Available ?? false;
    public bool IsListening { get; private set; }
    public event Action<string>? OnPartial;

    public Task<SpatialVoiceResult> CaptureAsync(SpatialAnchor anchor, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<SpatialVoiceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        SFSpeechRecognizer.RequestAuthorization(status =>
        {
            if (status != SFSpeechRecognizerAuthorizationStatus.Authorized)
            { tcs.TrySetResult(SpatialVoiceResult.Fail("음성 인식 권한이 거부되었습니다.")); return; }
            try
            {
                _recognizer = new SFSpeechRecognizer(NSLocale.FromLocaleIdentifier("ko-KR"));
                if (_recognizer is null || !_recognizer.Available)
                { tcs.TrySetResult(SpatialVoiceResult.Fail("이 기기에서 음성 인식을 사용할 수 없습니다.")); return; }

                _engine = new AVAudioEngine();
                _request = new SFSpeechAudioBufferRecognitionRequest { ShouldReportPartialResults = true };

                var audioSession = AVAudioSession.SharedInstance();
                audioSession.SetCategory(AVAudioSessionCategory.Record);
                audioSession.SetActive(true, out _);

                var node = _engine.InputNode;
                var format = node.GetBusOutputFormat(0);
                node.InstallTapOnBus(0, 1024, format, (buffer, when) => _request?.Append(buffer));
                _engine.Prepare();
                _engine.StartAndReturnError(out _);
                IsListening = true;

                _task = _recognizer.GetRecognitionTask(_request, (result, error) =>
                {
                    if (result is not null)
                    {
                        var best = result.BestTranscription?.FormattedString ?? "";
                        OnPartial?.Invoke(best);
                        if (result.Final)
                        {
                            StopEngine();
                            var sn = SpatialPromptStructurer.Structure(best, anchor);
                            tcs.TrySetResult(new SpatialVoiceResult(true, best, sn, "음성 → 공간 프롬프트 노드 구조화 완료."));
                        }
                    }
                    if (error is not null) { StopEngine(); tcs.TrySetResult(SpatialVoiceResult.Fail("음성 인식 오류: " + error.LocalizedDescription)); }
                });

                ct.Register(() => { StopEngine(); tcs.TrySetResult(SpatialVoiceResult.Fail("취소되었습니다.")); });
            }
            catch (Exception ex) { StopEngine(); tcs.TrySetResult(SpatialVoiceResult.Fail("음성 인식 초기화 오류: " + ex.Message)); }
        });
        return tcs.Task;
    }

    private void StopEngine()
    {
        try { _engine?.InputNode?.RemoveTapOnBus(0); } catch { }
        try { _engine?.Stop(); } catch { }
        try { _request?.EndAudio(); } catch { }
        try { _task?.Cancel(); } catch { }
        IsListening = false;
    }

    public Task CancelAsync() { StopEngine(); return Task.CompletedTask; }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;   // 멱등
        StopEngine();
        _engine?.Dispose(); _request?.Dispose(); _task?.Dispose(); _recognizer?.Dispose();
        _engine = null; _request = null; _task = null; _recognizer = null; OnPartial = null;
    }
}
#endif
