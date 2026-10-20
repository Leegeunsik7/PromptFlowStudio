using System;
using System.Collections.Generic;
using System.Linq;

namespace PromptFlowStudio.Services.Inference;

/// <summary>로컬 추론 백엔드 — WebGPU / DirectML(가속) 또는 BCL(휴리스틱 폴백).</summary>
public enum InferenceBackend { WebGpu, DirectML, Bcl }

/// <summary>로컬 모델 카탈로그 항목(경량 온디바이스 모델 메타 · 실제 가중치 결선 전 스텁).</summary>
public sealed record LocalModelInfo(string Key, string DisplayName, string Purpose, bool Available);

/// <summary>
/// [PromptFlow Studio v7.0.0] 로컬 추론 백엔드/모델 로더(순수 BCL · 외부 NuGet 0).
///   ▸ 단말의 WebGPU/DirectML 가속 가용성을 <b>프로브</b>하고, 결선 전(=이 저장소는 ONNX/WebGPU 네이티브 패키지 미탑재)에는 <see cref="InferenceBackend.Bcl"/> 로 보고한다.
///   ▸ 실제 가속 엔진(ONNX Runtime DirectML / WebGPU)을 붙일 때 <see cref="DetectBackend"/> 만 교체하면 상위 <see cref="WebGpuInferenceEngine"/> 가 그대로 활용한다.
///   ▸ 어떤 경우에도 예외 없이 백엔드를 반환(Graceful) — 미가속이면 상위가 BCL 휴리스틱으로 즉시 폴백.
/// </summary>
public sealed class LocalModelLoader
{
    private InferenceBackend? _cached;
    private bool _webGpuSignal;   // WebView2 WebGPU(navigator.gpu) 감지 결과(JS interop 이 보고) — 기본 미감지.

    /// <summary>가속 백엔드 탐지(1회 캐시). DirectML(D3D12) &gt; WebGPU 시그널 &gt; BCL.</summary>
    public InferenceBackend DetectBackend()
    {
        if (_cached is { } c) return c;
        var backend = Probe();
        _cached = backend;
        return backend;
    }

    /// <summary>WebView2/웹 런타임의 WebGPU 가용 시그널 보고(향후 JS interop 결선). 캐시를 무효화해 다음 탐지에 반영.</summary>
    public void ReportWebGpuSignal(bool available)
    {
        _webGpuSignal = available;
        _cached = null;
    }

    /// <summary>가속(WebGPU/DirectML) 사용 가능 여부(하드웨어 상주).</summary>
    public bool IsAccelerated => DetectBackend() != InferenceBackend.Bcl;

    /// <summary>온디바이스 모델 가중치 로드 여부. ⚠ 외부 NuGet/모델 번들 0 원칙이라 현재는 false → 가속 HW 감지돼도 연산은 BCL 폴백.
    ///   (실제 ONNX 가중치 결선 시 이 값을 true 로 승격하면 상위 엔진이 네이티브 텐서 경로를 가동.)</summary>
    public bool HasLoadedWeights => false;

    /// <summary>백엔드 사람이 읽는 라벨.</summary>
    public string BackendLabel => DetectBackend() switch
    {
        InferenceBackend.WebGpu => "WebGPU",
        InferenceBackend.DirectML => "DirectML",
        _ => "BCL 휴리스틱",
    };

    /// <summary>온디바이스 경량 모델 카탈로그(프롬프트/프레임/오디오). 가중치 로드 시에만 Available(가속 HW 감지≠가중치 탑재).</summary>
    public IReadOnlyList<LocalModelInfo> Models => _models
        .Select(m => m with { Available = HasLoadedWeights })
        .ToList();

    private static readonly LocalModelInfo[] _models =
    {
        new("prompt-boost-mini", "Prompt Booster Mini", "프롬프트 시네마틱 증강", false),
        new("stillcut-lcm",      "StillCut LCM",        "스틸컷 프레임 즉시 생성", false),
        new("audio-rms",         "Audio RMS Analyzer",  "오디오 볼륨 엔벨로프 분석", false),
    };

    /// <summary>
    /// 가속 백엔드 프로브 — Windows 는 D3D12/DirectML(<see cref="DirectMlNativeAdapter"/>) 실측, 그 외는 WebGPU 시그널, 미가용은 BCL.
    ///   ⚠ 1/60초 내 완만 디그레이드: 어떤 예외도 삼키고 BCL 반환.
    /// </summary>
    private InferenceBackend Probe()
    {
        try
        {
            if (DirectMlNativeAdapter.IsDirectMlCapable()) return InferenceBackend.DirectML;   // #if WINDOWS D3D12 실측
            if (_webGpuSignal) return InferenceBackend.WebGpu;                                  // WebView2 navigator.gpu 보고
            return InferenceBackend.Bcl;
        }
        catch { return InferenceBackend.Bcl; }
    }
}
