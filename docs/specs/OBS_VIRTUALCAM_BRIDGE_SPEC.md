# PromptFlow Studio VirtualCam 공유메모리 브리지 명세 (v11.2.0)

warning **정직한 스코프 고지**: 이 문서는 "NDI" 프로토콜이 아닙니다(NDI는 NewTek/Vizrt 소유의 독점 네트워크 프로토콜로, 별도 SDK 없이는 재구현할 수 없습니다). 이 명세는 PromptFlow Studio 프로세스가 로컬 머신에 만드는 **Win32 Named Shared Memory(MemoryMappedFile) + Named Mutex** 만을 다룹니다. OBS 쪽에서 이 메모리를 읽어 비디오 소스로 노출하려면, 이 문서의 프로토콜을 구현하는 **별도의 OBS 소스 플러그인**(C/C++, `obs-source_info` 등록)이 필요합니다 - 그 플러그인 자체는 이 저장소의 범위 밖입니다.

## 1. 참조 구현(C# 측 정본)

- 관리 코드 정본: `PromptFlowStudio/Services/AI/Wan/LiveStreamVirtualCamPipe.cs`
- C-ABI 구조체 정본: `PromptFlowStudio/Services/AI/Wan/VirtualCamPipeNativeSpec.cs`
- 회귀 테스트(구조체 크기 + 실제 와이어 바이트 일치 검증): `PromptFlowStudio.Tests/LiveStreamVirtualCamPipeTests.cs`의 `VirtualCamPipeNativeSpecTests` 클래스. 이 테스트가 통과하는 한, 아래 명세는 실제 구현과 어긋나지 않습니다.

## 2. Named 커널 객체

| 이름 | 종류 | Win32 API |
|---|---|---|
| `Local\PromptFlowStudio_VirtualCam_Frame_v1` | File Mapping | `CreateFileMappingW` / `OpenFileMappingW` |
| `Local\PromptFlowStudio_VirtualCam_Mutex_v1` | Mutex | `CreateMutexW` / `OpenMutexW` |

- `Local\` 접두사 - 현재 사용자 세션 스코프(관리자 권한 불필요).
- 매핑 전체 크기 = 헤더(36바이트) + 페이로드 상한(1920x1080x4 = 8,294,400바이트) = **8,294,436바이트**. OBS 플러그인은 이 고정 크기로 `MapViewOfFile` 하면 됩니다(가변 크기 재매핑 불필요).
- **쓰기는 PromptFlow Studio만** 합니다. OBS 플러그인은 항상 `FILE_MAP_READ`(읽기 전용)로 열어야 합니다.

## 3. 헤더 레이아웃(36바이트, `#pragma pack(1)`, 리틀엔디안)

| 오프셋 | 필드 | 타입(C) | 설명 |
|---|---|---|---|
| 0 | `magic` | `uint32_t` | 고정값 `0x56534650`(메모리상 ASCII `"PFSV"`) - 파싱 전 포맷 식별/검증 |
| 4 | `width` | `int32_t` | 프레임 너비(픽셀) |
| 8 | `height` | `int32_t` | 프레임 높이(픽셀) |
| 12 | `frameIndex` | `int64_t` | 단조증가 프레임 순번(1부터) |
| 20 | `timestamp` | `int64_t` | UTC, **.NET Ticks**(100ns 단위, 0001-01-01 00:00:00 UTC 기준 - Unix epoch 아님. 유닉스 초 변환: `(ticks - 621355968000000000) / 10000000`) |
| 28 | `pixelFormat` | `int32_t` | `0` = RGBA32, `1` = NV12 |
| 32 | `payloadBytes` | `int32_t` | 헤더 직후 픽셀 데이터의 실제 바이트 수(<= 8,294,400) |
| 36 | *(페이로드 시작)* | `uint8_t[payloadBytes]` | 픽셀 데이터(타이트 패킹, 스트라이드 패딩 없음) |

**엔디안**: 모든 필드는 리틀엔디안입니다. 이 프로젝트가 실제로 배포하는 아키텍처(x64, ARM64)는 전부 리틀엔디안이므로, 구조체를 메모리에서 그대로 캐스팅해 읽으면 됩니다. 빅엔디안 호스트는 이 명세의 대상이 아닙니다.

## 4. 동기화 프로토콜

1. Mutex를 `WaitForSingleObject(hMutex, timeoutMs)`로 획득(권장 타임아웃 50ms - 라이터도 동일 타임아웃을 씁니다).
2. `WAIT_ABANDONED`도 `WAIT_OBJECT_0`와 동일하게 "획득 성공"으로 취급하십시오(라이터가 비정상 종료했던 경우 - 자가치유).
3. 헤더를 읽고 `magic`이 `0x56534650`와 다르면 **아직 유효한 프레임이 없거나 손상**된 것이므로 그 회차는 버리십시오(메모리 매핑 직후 전부 0으로 시작하므로 `magic == 0`은 정상적인 "아직 쓰기 없음" 상태입니다).
4. `payloadBytes`가 0 이하이거나 상한을 넘으면 마찬가지로 버리십시오.
5. 페이로드까지 다 읽은 뒤 `ReleaseMutex`.
6. 매 프레임 재확인이 필요합니다 - 이 매핑은 "최신 프레임 1장"만 보관하는 슬롯이며 링버퍼가 아닙니다. `frameIndex`가 이전에 읽은 값과 같으면 새 프레임이 아직 없는 것이므로 스킵하십시오.

## 5. C++ 샘플 코드(Win32, OBS 소스 플러그인 골격)

> 지시서의 "OpenConfigFile"은 실제 Win32 API가 아닙니다 - 기존 공유메모리를 여는 올바른 API는 `OpenFileMappingW`이며, 아래 샘플은 그것을 사용합니다.

```cpp
#include <windows.h>
#include <cstdint>
#include <vector>

#pragma pack(push, 1)
struct PfsVirtualCamFrameHeader {
    uint32_t magic;
    int32_t  width;
    int32_t  height;
    int64_t  frameIndex;
    int64_t  timestampTicks;   // .NET Ticks(UTC)
    int32_t  pixelFormat;      // 0=RGBA32, 1=NV12
    int32_t  payloadBytes;
};
#pragma pack(pop)

static constexpr uint32_t PFS_MAGIC = 0x56534650u;
static constexpr size_t   PFS_MAX_PAYLOAD = 1920ull * 1080ull * 4ull;
static constexpr size_t   PFS_TOTAL_BYTES = sizeof(PfsVirtualCamFrameHeader) + PFS_MAX_PAYLOAD;
static const wchar_t*     PFS_MAP_NAME   = L"Local\\PromptFlowStudio_VirtualCam_Frame_v1";
static const wchar_t*     PFS_MUTEX_NAME = L"Local\\PromptFlowStudio_VirtualCam_Mutex_v1";

class PfsVirtualCamReader {
public:
    bool Open() {
        hMutex_ = OpenMutexW(SYNCHRONIZE, FALSE, PFS_MUTEX_NAME);
        if (!hMutex_) return false;   // PromptFlow Studio 가 아직 안 떠 있음

        hMap_ = OpenFileMappingW(FILE_MAP_READ, FALSE, PFS_MAP_NAME);
        if (!hMap_) { CloseHandle(hMutex_); hMutex_ = nullptr; return false; }

        view_ = MapViewOfFile(hMap_, FILE_MAP_READ, 0, 0, PFS_TOTAL_BYTES);
        if (!view_) { Close(); return false; }
        return true;
    }

    // 성공 시 true, outPixels 를 채움. 새 프레임이 없으면(frameIndex 불변) false.
    bool TryReadNextFrame(std::vector<uint8_t>& outPixels, PfsVirtualCamFrameHeader& outHeader) {
        if (!view_) return false;

        DWORD wait = WaitForSingleObject(hMutex_, 50);
        if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED) return false;

        bool ok = false;
        auto* hdr = reinterpret_cast<const PfsVirtualCamFrameHeader*>(view_);
        if (hdr->magic == PFS_MAGIC
            && hdr->payloadBytes > 0
            && static_cast<size_t>(hdr->payloadBytes) <= PFS_MAX_PAYLOAD
            && hdr->frameIndex != lastFrameIndex_) {
            const uint8_t* pixels = reinterpret_cast<const uint8_t*>(view_) + sizeof(PfsVirtualCamFrameHeader);
            outPixels.assign(pixels, pixels + hdr->payloadBytes);
            outHeader = *hdr;
            lastFrameIndex_ = hdr->frameIndex;
            ok = true;
        }

        ReleaseMutex(hMutex_);
        return ok;
    }

    void Close() {
        if (view_)   { UnmapViewOfFile(view_); view_ = nullptr; }
        if (hMap_)   { CloseHandle(hMap_);     hMap_ = nullptr; }
        if (hMutex_) { CloseHandle(hMutex_);   hMutex_ = nullptr; }
    }

    ~PfsVirtualCamReader() { Close(); }

private:
    HANDLE hMutex_ = nullptr;
    HANDLE hMap_   = nullptr;
    LPVOID view_   = nullptr;
    int64_t lastFrameIndex_ = 0;
};
```

OBS 소스 플러그인의 `video_tick` 콜백에서 매 틱마다 `TryReadNextFrame`을 호출하고, `pixelFormat`에 따라 `obs_source_frame`의 `format`(`VIDEO_FORMAT_RGBA` 또는 `VIDEO_FORMAT_NV12`)을 설정한 뒤 `obs_source_output_video`로 넘기면 됩니다. 이 매핑/연동 코드 자체는 이 저장소 범위 밖(별도 OBS 플러그인 프로젝트)입니다.

## 6. 알려진 제약

- 슬롯 1개(최신 프레임만) - 프레임 드롭이 발생해도 재전송하지 않습니다(라이브 스트리밍 특성상 최신 프레임만 중요하다는 전제).
- `payloadBytes`가 실제 `width x height x bytesPerPixel(pixelFormat)`과 일치하는지는 라이터가 보장하지만, 리더 쪽에서도 방어적으로 재검증하는 것을 권장합니다.
- 이 문서 버전(v11.2.0) 기준 페이로드 상한은 1080p RGBA 고정입니다. 더 큰 해상도가 필요하면 `LiveStreamVirtualCamPipe.MaxPayloadBytes`와 이 문서를 함께 갱신해야 합니다.