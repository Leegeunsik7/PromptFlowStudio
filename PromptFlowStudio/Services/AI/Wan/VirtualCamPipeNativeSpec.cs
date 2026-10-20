using System;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>
/// [PromptFlow Studio v11.2.0-OBS-BridgeSpec] LiveStreamVirtualCamPipe 의 MMF 헤더를 C-ABI 로 정식화한
///   언매니지드 구조체 - 외부 OBS 플러그인(C/C++)이 동일 메모리 레이아웃으로 직접 파싱할 수 있게 한다.
///   ▸ [StructLayout(LayoutKind.Sequential, Pack = 1)] - 필드 선언 순서 그대로 패딩 없이 순차 배치(C 의
///     #pragma pack(1) 과 동일 정합) - sizeof(VirtualCamFrameHeaderNative) == LiveStreamVirtualCamPipe.
///     HeaderBytes(36) 를 단위 테스트로 실측 보증해 문서/실제 구현 드리프트를 방지한다.
///   ▸ 엔디안: 전 필드 리틀엔디안(LiveStreamVirtualCamPipe.SubmitFrame 이 BinaryPrimitives.*LittleEndian 으로
///     명시 기록). 이 프로젝트 배포 대상(win-x64/android-arm64/ios-arm64) 전부 리틀엔디안이라 "구조체를 그대로
///     메모리에서 읽기"가 실사용과 100% 일치 - 빅엔디안 호스트는 대상 밖(문서에 명시).
///   ▸ 관리 코드(LiveStreamVirtualCamPipe) 는 여전히 BinaryPrimitives 로 Zero-Alloc 기록 - 이 구조체는 마샬링에
///     쓰이지 않고, C-ABI 계약을 코드로 고정해 회귀 테스트가 드리프트를 잡아내게 하는 명세 전용 타입이다.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VirtualCamFrameHeaderNative
{
    /// <summary>고정 매직 넘버 - <see cref="LiveStreamVirtualCamPipe.MagicValue"/> 와 반드시 일치(리틀엔디안 메모리상 ASCII "PFSV").</summary>
    public uint Magic;
    /// <summary>프레임 너비(픽셀).</summary>
    public int Width;
    /// <summary>프레임 높이(픽셀).</summary>
    public int Height;
    /// <summary>단조증가 프레임 순번(1부터 시작).</summary>
    public long FrameIndex;
    /// <summary>UTC 타임스탬프(.NET Ticks - 100ns 단위, 0001-01-01 00:00:00 UTC 기준).</summary>
    public long Timestamp;
    /// <summary>픽셀 포맷 - 0=Rgba32, 1=Nv12(<see cref="VirtualCamPixelFormat"/> 과 동일 정수값).</summary>
    public int PixelFormat;
    /// <summary>헤더 직후에 이어지는 픽셀 페이로드의 실제 바이트 수.</summary>
    public int PayloadBytes;
}

/// <summary>C-ABI 명세 상수/오프셋 - 이 타입 자체는 순수 정적 데이터라 인스턴스화·부작용 없음.</summary>
public static class VirtualCamPipeNativeSpec
{
    /// <summary>C-ABI 헤더의 실측 매니지드 크기(바이트) - LiveStreamVirtualCamPipe.HeaderBytes 와 반드시 일치(테스트로 보증).</summary>
    public static readonly int HeaderSizeBytes = Marshal.SizeOf<VirtualCamFrameHeaderNative>();

    /// <summary>헤더 필드별 바이트 오프셋(문서/디버깅용) - Pack=1 순차 배치라 값이 컴파일 타임에 고정.</summary>
    public static class Offsets
    {
        public const int Magic = 0;
        public const int Width = 4;
        public const int Height = 8;
        public const int FrameIndex = 12;
        public const int Timestamp = 20;
        public const int PixelFormat = 28;
        public const int PayloadBytes = 32;
        public const int PayloadStart = 36;
    }
}