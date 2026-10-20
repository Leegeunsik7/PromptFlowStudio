using System;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.Inference;

/// <summary>
/// [PromptFlow Studio v7.1.0] DirectML/D3D12 하드웨어 가속 경량 프로브(순수 BCL P/Invoke · 외부 대형 DLL 0).
///   ▸ Windows 시스템 DLL <c>d3d12.dll</c> 의 <c>D3D12CreateDevice</c> 를 <b>디바이스 생성 없이</b>(ppDevice=null) 호출해
///     DirectX 12 / DirectML 가용 GPU 상주 여부만 초경량 판정한다(S_OK/S_FALSE=가용).
///   ▸ OS 종속 API 이므로 <c>#if WINDOWS</c> 로 격리하고, 비-Windows(모바일 소스링크)·DLL 부재·예외는 false 로 폴백(제2헌장).
/// </summary>
internal static class DirectMlNativeAdapter
{
#if WINDOWS
    // ID3D12Device IID.
    private static readonly Guid IID_ID3D12Device = new("189819f1-1db6-4b57-be54-1821339b85f7");
    private const int D3D_FEATURE_LEVEL_11_0 = 0xb000;   // DirectML 최소 요건

    [DllImport("d3d12.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true, SetLastError = false)]
    private static extern int D3D12CreateDevice(IntPtr pAdapter, int MinimumFeatureLevel, in Guid riid, IntPtr ppDevice);

    /// <summary>D3D12(=DirectML 가속) 가용 GPU 존재 여부. 디바이스를 실제로 만들지 않는 초경량 프로브.</summary>
    public static bool IsDirectMlCapable()
    {
        try
        {
            // ppDevice = IntPtr.Zero → 생성 없이 가용성만 확인. S_OK(0)/S_FALSE(1) = 가용, 음수(E_FAIL 등) = 불가.
            var hr = D3D12CreateDevice(IntPtr.Zero, D3D_FEATURE_LEVEL_11_0, in IID_ID3D12Device, IntPtr.Zero);
            return hr >= 0;
        }
        catch (DllNotFoundException) { return false; }   // d3d12.dll 부재(Server Core 등)
        catch (EntryPointNotFoundException) { return false; }
        catch (Exception ex) { Program.Log("DirectMl.Probe", ex); return false; }
    }
#else
    /// <summary>비-Windows(모바일 등)에서는 항상 false — 상위가 WebGPU 시그널 또는 BCL 로 폴백.</summary>
    public static bool IsDirectMlCapable() => false;
#endif
}
