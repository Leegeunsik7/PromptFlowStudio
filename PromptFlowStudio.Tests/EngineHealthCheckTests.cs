using System.Net;
using System.Net.Sockets;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Tests;

/// <summary>렌더 엔진 사전 헬스체크 — 실제 루프백 HTTP 응답/거부에 따른 Online/Offline 판정 · 배지 문구 · 변화 통지 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class EngineHealthCheckTests
{
    /// <summary>요청 1건당 200 응답하는 최소 HTTP 서버(루프백 · 임의 포트).</summary>
    private static (TcpListener Listener, Task Loop) StartStubServer(CancellationToken ct)
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync(ct); } catch (Exception) { return; }
                using (c)
                {
                    var s = c.GetStream();
                    var buf = new byte[4096];
                    try
                    {
                        await s.ReadAsync(buf, ct);
                        var body = "{\"system\":{}}"u8.ToArray();
                        var head = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                        await s.WriteAsync(head, ct);
                        await s.WriteAsync(body, ct);
                    }
                    catch (Exception) { }
                }
            }
        }, ct);
        return (l, loop);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task EngineHealthCheck_DetectsOnlineAndOfflineStates()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (listener, loop) = StartStubServer(cts.Token);
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var online = new EngineHealthCheckService(null, null, "http://127.0.0.1:" + port);
            Assert.False(online.Latest.IsChecked);
            Assert.Contains("점검 중", online.Latest.ComfyLabel);

            var s = await online.CheckNowAsync(cts.Token);
            Assert.True(s.ComfyOnline);
            Assert.Equal("ComfyUI: 🟢 Online (" + port + ")", s.ComfyLabel);
            Assert.False(s.CloudConfigured);                     // 선택기 미등록
            Assert.Equal("Cloud: 🔴 API 키 미설정", s.CloudLabel);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await loop; } catch (Exception) { }
        }

        // 리스너 없는 포트 → 연결 거부 → Offline(예외 0)
        using var offline = new EngineHealthCheckService(null, null, "http://127.0.0.1:" + FreePort());
        var o = await offline.CheckNowAsync();
        Assert.False(o.ComfyOnline);
        Assert.Equal("ComfyUI: 🔴 Offline (시뮬레이션 모드로 동작)", o.ComfyLabel);
    }

    [Fact]
    public async Task EngineHealthCheck_RaisesChanged_OnlyOnTransition_AndIsolatesPingErrors()
    {
        var up = false;
        var throwNext = false;
        using var svc = new EngineHealthCheckService(null, (url, ct) =>
        {
            if (throwNext) throw new HttpRequestException("boom");
            return Task.FromResult(Volatile.Read(ref up));
        }, "http://127.0.0.1:8188");
        var changes = 0;
        svc.Changed += () => Interlocked.Increment(ref changes);

        await svc.CheckNowAsync();   // 최초 판정(Offline) → 통지
        await svc.CheckNowAsync();   // 변화 없음
        Assert.Equal(1, changes);
        Assert.Equal("8188", EngineHealthCheckService.PortOf(svc.Latest.ComfyUrl));

        Volatile.Write(ref up, true);
        Assert.True((await svc.CheckNowAsync()).ComfyOnline);
        Assert.Equal(2, changes);

        throwNext = true;
        Assert.False((await svc.CheckNowAsync()).ComfyOnline);   // 예외 → Offline 격리
        Assert.Equal(3, changes);

        // 타임아웃(1초) 초과 핑 → Offline
        using var slow = new EngineHealthCheckService(null, async (url, ct) => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return true; }, "http://127.0.0.1:8188");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False((await slow.CheckNowAsync()).ComfyOnline);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task EngineHealthCheck_StartStop_PollsInBackground_WithRefCount()
    {
        var pings = 0;
        using var svc = new EngineHealthCheckService(null, (url, ct) => { Interlocked.Increment(ref pings); return Task.FromResult(true); }, "http://127.0.0.1:8188");
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.Changed += () => first.TrySetResult();

        svc.Start();
        svc.Start();                                   // 참조 카운트 2 — 타이머 1개
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(svc.Latest.ComfyOnline);
        svc.Stop();
        svc.Stop();
        svc.Stop();                                    // 초과 해제 무해
        var after = Volatile.Read(ref pings);
        await Task.Delay(200);
        Assert.Equal(after, Volatile.Read(ref pings));
    }
}
