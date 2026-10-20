using System.Runtime.CompilerServices;

namespace PromptFlowStudio.Tests;

/// <summary>
/// v17.63.1 고부하 병렬 일괄 실행 하드닝 — 463 건 동시 실행 시 동기 블로킹 테스트(Thread.Join · 스핀 · 60 Hz 러너)가 스레드풀을 점유해
/// 소켓/HttpListener 비동기 완료와 CancelAfter 타이머 콜백이 기아(주입 지연 ~500 ms/스레드)로 수 초씩 밀렸다.
/// 이 때문에 WebSocket 재접속 · VLM 타임아웃 · 업로드 테스트가 10 s 안전망에 걸려 간헐 실패했다 — 최소 워커/IOCP 를 코어 × 8 로 선확보.
/// </summary>
internal static class TestAssemblyInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        var want = Math.Max(64, Environment.ProcessorCount * 8);
        ThreadPool.SetMinThreads(Math.Max(worker, want), Math.Max(io, want));
    }
}
