using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Ui;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Tests;

public class WanEngineTests
{
    [Fact]
    public void MultiCloudVault_DynamicAdd_And_InstantApply_Check()
    {
        var vault = new WanTokenVault(null);   // 인메모리 전용(사용자 vault.enc 미접촉)
        int notified = 0;
        vault.OnKeysChanged += _ => notified++;
        vault.OnKeysChanged += _ => throw new InvalidOperationException("구독자 예외 격리 확인");

        // [+ 키 추가] → 빈 칸은 무시
        var drafts = new List<string> { "" };
        vault.UpdateProviderKeys(WanTokenVault.DashScope, drafts);
        Assert.Equal(0, vault.GetKeyCount(WanTokenVault.DashScope));
        Assert.False(vault.TryGetNextKey(WanTokenVault.DashScope, out _));

        // 입력 즉시 반영 + 동적 추가(공백 트림·중복 제거)
        drafts[0] = "sk-a";
        drafts.Add(" sk-b ");
        drafts.Add("sk-a");
        vault.UpdateProviderKeys(WanTokenVault.DashScope, drafts);
        Assert.Equal(2, vault.GetKeyCount(WanTokenVault.DashScope));

        // 라운드로빈 순환
        Assert.True(vault.TryGetNextKey("dashscope", out var k1));
        Assert.True(vault.TryGetNextKey(WanTokenVault.DashScope, out var k2));
        Assert.True(vault.TryGetNextKey(WanTokenVault.DashScope, out var k3));
        Assert.Equal("sk-a", k1);
        Assert.Equal("sk-b", k2);
        Assert.Equal("sk-a", k3);

        // 프로바이더 격리
        Assert.Equal(0, vault.GetKeyCount(WanTokenVault.SiliconFlow));
        vault.UpdateProviderKeys(WanTokenVault.SiliconFlow, new[] { "sf-1" });
        Assert.True(vault.TryGetNextKey(WanTokenVault.SiliconFlow, out var sf));
        Assert.Equal("sf-1", sf);

        // 삭제 즉시 반영(커서 범위 보정)
        drafts.RemoveAt(1);
        vault.UpdateProviderKeys(WanTokenVault.DashScope, drafts);
        Assert.Equal(1, vault.GetKeyCount(WanTokenVault.DashScope));
        Assert.True(vault.TryGetNextKey(WanTokenVault.DashScope, out var k4));
        Assert.Equal("sk-a", k4);

        var copy = new List<string>();
        vault.CopyKeys(WanTokenVault.DashScope, copy);
        Assert.Equal(new[] { "sk-a" }, copy);
        Assert.Equal(4, notified);
        Assert.Equal(4, vault.Version);

        // 용량 내 갱신/순환 0B(구독자 없음 — 예외 격리용 GetInvocationList 는 할당 대상 아님)
        var warm = new List<string> { "sk-a", "sk-b", "sk-c" };
        var quiet = new WanTokenVault(null);
        quiet.UpdateProviderKeys(WanTokenVault.Custom, warm);
        quiet.TryGetNextKey(WanTokenVault.Custom, out _);
        // 단발 측정은 병렬 테스트 중 티어드 JIT 재컴파일 할당에 오염 → 최소값 반복 측정(AllocationProbe)
        Assert.Equal(0, AllocationProbe.MinAllocated(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                quiet.UpdateProviderKeys(WanTokenVault.Custom, warm);
                quiet.TryGetNextKey(WanTokenVault.Custom, out _);
            }
        }));
    }

    [Fact]
    public async Task SecureVault_Persistence_And_RetryLogic_Check()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pfs-vault-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "Config", "vault.enc");
        try
        {
            // ① 암호화 저장 → 재로드 왕복(평문 미기록)
            var v1 = new WanTokenVault(path);
            v1.UpdateProviderKeys(WanTokenVault.DashScope, new[] { "sk-quota-dead", "sk-alive" });
            v1.UpdateProviderKeys(WanTokenVault.SiliconFlow, new[] { "sf-1" });
            Assert.True(v1.SaveToDisk());
            var raw = File.ReadAllText(path);
            Assert.DoesNotContain("sk-alive", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("DashScope", raw, StringComparison.Ordinal);

            var v2 = new WanTokenVault(path);
            Assert.Equal(2, v2.GetKeyCount(WanTokenVault.DashScope));
            Assert.Equal(1, v2.GetKeyCount(WanTokenVault.SiliconFlow));

            // 손상 파일 → Graceful(false · 예외 없음)
            File.WriteAllText(path + ".bad", "not-a-valid-envelope");
            Assert.False(new WanTokenVault(path + ".bad").LoadFromDisk());

            // ② 429/402 → RotateKey 즉시 재시도
            var handler = new KeyedHandler(new Dictionary<string, HttpStatusCode>
            {
                ["sk-quota-dead"] = HttpStatusCode.TooManyRequests,
                ["sk-alive"] = HttpStatusCode.OK,
            });
            using var http = new HttpClient(handler);
            var (resp, used) = await WanApiService.SendWithKeyRotationAsync(http, v2, WanTokenVault.DashScope,
                "sk-quota-dead", WanApiService.MaxKeyRotations, k => Build(k), CancellationToken.None);
            using (resp)
            {
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.Equal("sk-alive", used);
            }
            Assert.Equal(2, handler.Calls);

            // 모든 키 402 → 한 바퀴 후 중단(무한 루프 없음) · 마지막 응답 반환
            var dead = new KeyedHandler(new Dictionary<string, HttpStatusCode>
            {
                ["sk-quota-dead"] = HttpStatusCode.PaymentRequired,
                ["sk-alive"] = HttpStatusCode.PaymentRequired,
            });
            using var http2 = new HttpClient(dead);
            var (resp2, _) = await WanApiService.SendWithKeyRotationAsync(http2, v2, WanTokenVault.DashScope,
                "sk-quota-dead", WanApiService.MaxKeyRotations, k => Build(k), CancellationToken.None);
            using (resp2) Assert.Equal(HttpStatusCode.PaymentRequired, resp2.StatusCode);
            Assert.Equal(2, dead.Calls);

            // 로테이션 0회(i2v oss 바운드) → 재시도 없음
            var once = new KeyedHandler(new Dictionary<string, HttpStatusCode> { ["sk-quota-dead"] = HttpStatusCode.TooManyRequests });
            using var http3 = new HttpClient(once);
            var (resp3, _) = await WanApiService.SendWithKeyRotationAsync(http3, v2, WanTokenVault.DashScope,
                "sk-quota-dead", 0, k => Build(k), CancellationToken.None);
            resp3.Dispose();
            Assert.Equal(1, once.Calls);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        static HttpRequestMessage Build(string key)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/submit");
            r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            return r;
        }
    }

    [Fact]
    public async Task SecureVault_DPAPI_And_RetryLogic_Check()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pfs-dpapi-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "Config", "vault.enc");
        try
        {
            // ① UI 갱신 → 백그라운드 DPAPI 저장(Fire-and-forget) 완료 대기
            var v1 = new WanTokenVault(path);
            Assert.False(v1.IsPersisted);
            var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            v1.OnPersisted += ok => saved.TrySetResult(ok);
            v1.UpdateProviderKeys(WanTokenVault.DashScope, new[] { "ds-1", "ds-2", "ds-3" });
            Assert.True(await saved.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(v1.IsPersisted);
            Assert.Equal("DPAPI", WanTokenVault.CipherName);

            var raw = File.ReadAllBytes(path);
            Assert.NotEqual((byte)'{', raw[0]);                                         // JSON 평문 아님
            Assert.DoesNotContain("ds-2", System.Text.Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
            Assert.DoesNotContain("ds-2", System.Text.Encoding.ASCII.GetString(raw), StringComparison.Ordinal);

            // ② 초기화 시 복원
            var v2 = new WanTokenVault(path);
            Assert.True(v2.IsPersisted);
            var restored = new List<string>();
            v2.CopyKeys(WanTokenVault.DashScope, restored);
            Assert.Equal(new[] { "ds-1", "ds-2", "ds-3" }, restored);

            // ③ 402 → ds-1 영구 소진 · 429 → ds-2 쿨다운 → 즉시 ds-3 로 전환
            var handler = new KeyedHandler(new Dictionary<string, HttpStatusCode>
            {
                ["ds-1"] = HttpStatusCode.PaymentRequired,
                ["ds-2"] = HttpStatusCode.TooManyRequests,
                ["ds-3"] = HttpStatusCode.OK,
            });
            using var http = new HttpClient(handler);
            Assert.True(v2.TryGetNextKey(WanTokenVault.DashScope, out var first));
            Assert.Equal("ds-1", first);
            var (resp, used) = await WanApiService.SendWithKeyRotationAsync(http, v2, WanTokenVault.DashScope,
                first, WanApiService.MaxKeyRotations, Build, CancellationToken.None);
            using (resp) Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("ds-3", used);
            Assert.Equal(3, handler.Calls);
            Assert.True(v2.IsExhausted(WanTokenVault.DashScope, "ds-1"));
            Assert.True(v2.IsExhausted(WanTokenVault.DashScope, "ds-2"));
            Assert.False(v2.IsExhausted(WanTokenVault.DashScope, "ds-3"));
            Assert.Equal(1, v2.GetActiveKeyCount(WanTokenVault.DashScope));

            // 소진 키는 라운드로빈에서 제외
            for (int i = 0; i < 4; i++)
            {
                Assert.True(v2.TryGetNextKey(WanTokenVault.DashScope, out var k));
                Assert.Equal("ds-3", k);
            }

            // ④ 목록 갱신 시 동일 키 소진 상태 승계 · 신규 키는 활성
            v2.UpdateProviderKeys(WanTokenVault.DashScope, new[] { "ds-3", "ds-1", "ds-4" });
            Assert.True(v2.IsExhausted(WanTokenVault.DashScope, "ds-1"));
            Assert.False(v2.IsExhausted(WanTokenVault.DashScope, "ds-4"));
            Assert.Equal(2, v2.GetActiveKeyCount(WanTokenVault.DashScope));

            // ⑤ 쿨다운 만료(0초) → 자동 복귀
            Assert.True(v2.MarkExhausted(WanTokenVault.DashScope, "ds-4", TimeSpan.Zero));
            Assert.False(v2.IsExhausted(WanTokenVault.DashScope, "ds-4"));

            // ⑥ 전 키 소진 → 로테이션 불가(무한 재시도 없음)
            v2.MarkExhausted(WanTokenVault.DashScope, "ds-3");
            v2.MarkExhausted(WanTokenVault.DashScope, "ds-4");
            Assert.False(v2.TryGetNextKey(WanTokenVault.DashScope, out _));
            Assert.False(v2.RotateKey(WanTokenVault.DashScope, "ds-3", out _));

            // ⑦ 소진 판정/순환 0B
            var quiet = new WanTokenVault(null);
            var warm = new List<string> { "a", "b", "c" };
            quiet.UpdateProviderKeys(WanTokenVault.Custom, warm);
            quiet.MarkExhausted(WanTokenVault.Custom, "b");
            quiet.RotateKey(WanTokenVault.Custom, "a", out _);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                quiet.UpdateProviderKeys(WanTokenVault.Custom, warm);
                quiet.TryGetNextKey(WanTokenVault.Custom, out _);
                quiet.RotateKey(WanTokenVault.Custom, "a", out _);
                quiet.IsExhausted(WanTokenVault.Custom, "b");
            }
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        static HttpRequestMessage Build(string key)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/submit");
            r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            return r;
        }
    }

    [Fact]
    public async Task Wan21_I2V_MultiKey_And_CrossProvider_Check()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pfs-i2v-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var img = Path.Combine(dir, "anchor.png");
        File.WriteAllBytes(img, new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 });
        try
        {
            // ① I2V: ds-a 429 → ds-b 로 로테이션 + 새 계정으로 이미지 재업로드 후 재제출
            var vault = new WanTokenVault(null);
            vault.UpdateProviderKeys(WanTokenVault.DashScope, new[] { "ds-a", "ds-b" });
            var mock = new RouteHandler();
            mock.SubmitStatus["ds-a"] = HttpStatusCode.TooManyRequests;
            var svc = new WanApiService(new FakeWanConfig(), new QuotaRateGuardService(), vault, mock);

            var r1 = await svc.SubmitAsync(new WanVideoRequest { Prompt = "아기 미소", ImagePath = img });
            Assert.True(r1.Ok, r1.Message);
            Assert.Equal("task-ds-b", r1.TaskId);
            Assert.Equal(new[] { "ds-a", "ds-b" }, mock.PolicyKeys);                 // 키마다 재업로드
            Assert.Equal(2, mock.OssUploads);
            Assert.Equal(new[] { "ds-a", "ds-b" }, mock.SubmitKeys);
            Assert.Contains("\"img_url\":\"oss://", mock.LastSubmitBody, StringComparison.Ordinal);
            Assert.True(vault.IsExhausted(WanTokenVault.DashScope, "ds-a"));

            // 제출 키로 조회 고정
            var q1 = await svc.QueryAsync(r1.TaskId!);
            Assert.True(q1.Ok);
            Assert.Equal("ds-b", mock.LastQueryKey);

            // ② DashScope 전멸(402) → SiliconFlow 우회(i2v base64 동봉) → SiliconFlow 상태 조회
            var v2 = new WanTokenVault(null);
            v2.UpdateProviderKeys(WanTokenVault.DashScope, new[] { "ds-x" });
            v2.UpdateProviderKeys(WanTokenVault.SiliconFlow, new[] { "sf-1" });
            v2.UpdateProviderKeys(WanTokenVault.Custom, new[] { "cu-1" });
            var m2 = new RouteHandler();
            m2.SubmitStatus["ds-x"] = HttpStatusCode.PaymentRequired;
            var svc2 = new WanApiService(new FakeWanConfig(), new QuotaRateGuardService(), v2, m2);

            var r2 = await svc2.SubmitAsync(new WanVideoRequest { Prompt = "아기 웃음", ImagePath = img, Size = "720*1280" });
            Assert.True(r2.Ok, r2.Message);
            Assert.Equal("sf-req-1", r2.TaskId);
            Assert.Contains("SiliconFlow", r2.Message, StringComparison.Ordinal);
            Assert.Contains("data:image/png;base64,", m2.LastSiliconBody, StringComparison.Ordinal);
            Assert.Contains("\"image_size\":\"720x1280\"", m2.LastSiliconBody, StringComparison.Ordinal);
            Assert.True(v2.IsExhausted(WanTokenVault.DashScope, "ds-x"));

            var q2 = await svc2.QueryAsync(r2.TaskId!);
            Assert.True(q2.Ok);
            Assert.Equal(WanTaskStatus.Succeeded, q2.Status);
            Assert.Equal("https://sf.example/video.mp4", q2.VideoUrl);

            // ③ SiliconFlow 도 429 → Custom(DashScope 호환) 우회
            m2.SiliconStatus = HttpStatusCode.TooManyRequests;
            var r3 = await svc2.SubmitAsync(new WanVideoRequest { Prompt = "아기 걸음마" });
            Assert.True(r3.Ok, r3.Message);
            Assert.Equal("task-cu-1", r3.TaskId);
            Assert.Contains("Custom", r3.Message, StringComparison.Ordinal);
            Assert.True(v2.IsExhausted(WanTokenVault.SiliconFlow, "sf-1"));

            // ④ 전 프로바이더 소진 → 명확한 실패(무한 재시도 없음)
            m2.SubmitStatus["cu-1"] = HttpStatusCode.PaymentRequired;
            var r4 = await svc2.SubmitAsync(new WanVideoRequest { Prompt = "아기 잠" });
            Assert.False(r4.Ok);
            var r5 = await svc2.SubmitAsync(new WanVideoRequest { Prompt = "아기 잠" });
            Assert.False(r5.Ok);
            Assert.Contains("활성 API 키가 없습니다", r5.Message, StringComparison.Ordinal);

            Assert.Equal("1280x720", WanApiService.SiliconFlowSize("1280*720"));
            Assert.Equal("960x960", WanApiService.SiliconFlowSize("1024*1024"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task SystemAudit_EdgeCases_And_LocalFallback_Check()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pfs-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var img = Path.Combine(dir, "anchor.png");
        var imgBytes = new byte[200_003];   // 3의 배수 아님 → 청크 경계·패딩 검증
        new Random(7).NextBytes(imgBytes);
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(imgBytes, 0);   // v16.3.1 시그니처 사전 검증 통과(PNG 헤더)
        File.WriteAllBytes(img, imgBytes);
        try
        {
            // ① getPolicy 429 → 키 로테이션 후 업로드 계정 키로 제출
            var v1 = new WanTokenVault(null);
            v1.UpdateProviderKeys(WanTokenVault.DashScope, new[] { "ds-a", "ds-b" });
            var m1 = new RouteHandler();
            m1.PolicyStatus["ds-a"] = HttpStatusCode.TooManyRequests;
            var s1 = new WanApiService(new FakeWanConfig(), new QuotaRateGuardService(), v1, m1);
            var r1 = await s1.SubmitAsync(new WanVideoRequest { Prompt = "아기", ImagePath = img });
            Assert.True(r1.Ok, r1.Message);
            Assert.Equal(new[] { "ds-a", "ds-b" }, m1.PolicyKeys);
            Assert.Equal(new[] { "ds-b" }, m1.SubmitKeys);
            Assert.Equal("task-ds-b", r1.TaskId);
            Assert.True(v1.IsExhausted(WanTokenVault.DashScope, "ds-a"));

            // ② SiliconFlow base64 청크 스트리밍 — 정확성 + Content-Length 일치
            var v2 = new WanTokenVault(null);
            v2.UpdateProviderKeys(WanTokenVault.SiliconFlow, new[] { "sf-1" });
            var m2 = new RouteHandler();
            var s2 = new WanApiService(new FakeWanConfig(), new QuotaRateGuardService(), v2, m2);
            var r2 = await s2.SubmitAsync(new WanVideoRequest { Prompt = "아기", ImagePath = img });
            Assert.True(r2.Ok, r2.Message);
            Assert.Contains("data:image/png;base64," + Convert.ToBase64String(imgBytes) + "\"}", m2.LastSiliconBody, StringComparison.Ordinal);
            Assert.Equal(m2.LastSiliconByteCount, m2.LastSiliconContentLength);
            using (var parsed = System.Text.Json.JsonDocument.Parse(m2.LastSiliconBody))
                Assert.Equal("Wan-AI/Wan2.1-I2V-14B-720P", parsed.RootElement.GetProperty("model").GetString());

            // 상한 초과 이미지 → Graceful 거부(파일 미적재)
            var huge = Path.Combine(dir, "huge.png");
            using (var fs = new FileStream(huge, FileMode.Create)) fs.SetLength(WanApiService.MaxInlineImageBytes + 1);
            var r2b = await s2.SubmitAsync(new WanVideoRequest { Prompt = "아기", ImagePath = huge });
            Assert.False(r2b.Ok);
            Assert.Contains("상한 초과", r2b.Message, StringComparison.Ordinal);

            // ③ Custom BaseUrl — 검증·정규화·엔드포인트 사용·DPAPI 영구 저장
            var path = Path.Combine(dir, "Config", "vault.enc");
            var v3 = new WanTokenVault(path);
            Assert.False(v3.SetProviderEndpoint(WanTokenVault.Custom, "ftp://bad.example"));
            Assert.False(v3.SetProviderEndpoint(WanTokenVault.Custom, "not a url"));
            Assert.True(v3.SetProviderEndpoint(WanTokenVault.Custom, " https://custom.mock/ "));
            Assert.Equal("https://custom.mock", v3.GetProviderEndpoint(WanTokenVault.Custom));
            v3.UpdateProviderKeys(WanTokenVault.Custom, new[] { "cu-1" });
            Assert.True(v3.SaveToDisk());
            var v3b = new WanTokenVault(path);
            Assert.Equal("https://custom.mock", v3b.GetProviderEndpoint(WanTokenVault.Custom));
            Assert.Equal(1, v3b.GetKeyCount(WanTokenVault.Custom));

            var m3 = new RouteHandler();
            var s3 = new WanApiService(new FakeWanConfig(), new QuotaRateGuardService(), v3b, m3);
            var r3 = await s3.SubmitAsync(new WanVideoRequest { Prompt = "아기" });
            Assert.True(r3.Ok, r3.Message);
            Assert.StartsWith("https://custom.mock/api/v1/services/", m3.LastSubmitUrl, StringComparison.Ordinal);
            await s3.QueryAsync(r3.TaskId!);
            Assert.StartsWith("https://custom.mock/api/v1/tasks/", m3.LastQueryUrl, StringComparison.Ordinal);

            // ④ 클라우드 전멸 → 로컬 GPU 다이렉트 폴백(제출 단계 402 후 · 시작 시 0키 둘 다)
            var v4 = new WanTokenVault(null);
            v4.UpdateProviderKeys(WanTokenVault.DashScope, new[] { "ds-dead" });
            var m4 = new RouteHandler();
            m4.SubmitStatus["ds-dead"] = HttpStatusCode.PaymentRequired;
            var local = new FakeLocalEngine();
            var s4 = new WanApiService(new FakeWanConfig(), new QuotaRateGuardService(), v4, m4, local);
            Assert.True(s4.HasActiveCloudKey());
            var g1 = await s4.GenerateAsync(new WanVideoRequest { Prompt = "아기" });
            Assert.True(g1.Ok, g1.Message);
            Assert.Equal(@"C:\local\out.mp4", g1.VideoUrl);
            Assert.Equal(1, local.Calls);
            Assert.Equal(new[] { "ds-dead" }, m4.SubmitKeys);
            Assert.False(s4.HasActiveCloudKey());

            var g2 = await s4.GenerateAsync(new WanVideoRequest { Prompt = "아기" });   // 시작 시 전멸 → 클라우드 호출 없이 즉시 로컬
            Assert.True(g2.Ok);
            Assert.Equal(2, local.Calls);
            Assert.Single(m4.SubmitKeys);

            // 로컬 엔진 비활성 → 폴백 안 함(기존 실패 경로)
            var s5 = new WanApiService(new FakeWanConfig(), new QuotaRateGuardService(), v4, m4, new FakeLocalEngine { IsEnabled = false });
            var g3 = await s5.GenerateAsync(new WanVideoRequest { Prompt = "아기" });
            Assert.False(g3.Ok);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Opus_Ascension_ZeroAlloc_Architecture_Check()
    {
        // ① Lock-Free 링: 동시 순환 — CAS 커서로 정확한 라운드로빈(키별 배정 수 동일 · 이중 배정 0)
        var vault = new WanTokenVault(null);
        var keys = new[] { "k-0", "k-1", "k-2", "k-3" };
        vault.UpdateProviderKeys(WanTokenVault.DashScope, keys);
        var hits = new int[keys.Length];
        const int perThread = 5_000, threads = 8;
        var workers = new Task[threads];
        for (int t = 0; t < threads; t++)
            workers[t] = Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    Assert.True(vault.TryGetNextKey(WanTokenVault.DashScope, out var k));
                    Interlocked.Increment(ref hits[k[^1] - '0']);
                }
            });
        await Task.WhenAll(workers);
        foreach (var h in hits) Assert.Equal(perThread * threads / keys.Length, h);

        // 재구성 × 소진 표시 × 조회 경합 — 예외·찢어진 읽기 0, 표시 유실 0(재구성 후에도 소진 유지)
        using (var stop = new CancellationTokenSource())
        {
            var writer = Task.Run(() =>
            {
                var a = new[] { "k-0", "k-1", "k-2", "k-3" };
                var b = new[] { "k-3", "k-2", "k-1", "k-0", "k-4" };
                for (int i = 0; !stop.IsCancellationRequested; i++) vault.UpdateProviderKeys(WanTokenVault.DashScope, (i & 1) == 0 ? a : b);
            });
            var reader = Task.Run(() =>
            {
                var copy = new List<string>();
                while (!stop.IsCancellationRequested)
                {
                    if (vault.TryGetNextKey(WanTokenVault.DashScope, out var k)) Assert.StartsWith("k-", k, StringComparison.Ordinal);
                    vault.CopyKeys(WanTokenVault.DashScope, copy);
                    Assert.True(copy.Count is 4 or 5);
                    foreach (var c in copy) Assert.NotNull(c);
                }
            });
            for (int i = 0; i < 2_000; i++) Assert.True(vault.MarkExhausted(WanTokenVault.DashScope, "k-1"));
            stop.Cancel();
            await Task.WhenAll(writer, reader);
        }
        Assert.True(vault.IsExhausted(WanTokenVault.DashScope, "k-1"));
        vault.UpdateProviderKeys(WanTokenVault.DashScope, keys);
        Assert.True(vault.IsExhausted(WanTokenVault.DashScope, "k-1"));   // 상태 승계
        Assert.Equal(3, vault.GetActiveKeyCount(WanTokenVault.DashScope));

        // ② Pub-Sub: 동기 핸들러가 붙어 있어도 발행 0B(스냅샷 배열) · Channel 비동기 구독 · 취소 시 해제
        var quiet = new WanTokenVault(null);
        var warm = new List<string> { "a", "b", "c" };
        var counter = new int[1];
        Action<string> handler = _ => counter[0]++;
        quiet.OnKeysChanged += handler;
        for (int i = 0; i < 200; i++) quiet.UpdateProviderKeys(WanTokenVault.Custom, warm);   // v16.12.0: 워밍업(JIT 티어링 — 측정 창 내 재컴파일 할당 배제)
        var windows = 0;
        Assert.Equal(0, AllocationProbe.MinAllocated(() => { windows++; for (int i = 0; i < 1000; i++) quiet.UpdateProviderKeys(WanTokenVault.Custom, warm); }));
        Assert.Equal(200 + 1000 * windows, counter[0]);
        var faulty = new WanTokenVault(null);
        faulty.OnKeysChanged += _ => throw new InvalidOperationException("격리");
        faulty.OnKeysChanged += handler;
        faulty.UpdateProviderKeys(WanTokenVault.Custom, warm);   // 앞 구독자 예외에도 뒤 구독자 호출
        Assert.Equal(200 + 1000 * windows + 1, counter[0]);
        Assert.Equal(1, faulty.SubscriberFaults);
        quiet.OnKeysChanged -= handler;
        quiet.UpdateProviderKeys(WanTokenVault.Custom, warm);
        Assert.Equal(200 + 1000 * windows + 1, counter[0]);

        using (var life = new CancellationTokenSource())
        {
            var signals = quiet.Subscribe(life.Token);
            Assert.Equal(1, quiet.ChannelSubscribers);
            quiet.UpdateProviderKeys(WanTokenVault.SiliconFlow, new[] { "sf" });
            var sig = await signals.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new VaultSignal(VaultSignalKind.KeysChanged, WanTokenVault.SiliconFlow, false), sig);
            for (int i = 0; i < 500; i++) quiet.MarkExhausted(WanTokenVault.SiliconFlow, "sf");   // 느린 소비자 → DropOldest(발행자 비차단)
            int drained = 0;
            while (signals.TryRead(out _)) drained++;
            Assert.InRange(drained, 1, 64);
            life.Cancel();
            Assert.Equal(0, quiet.ChannelSubscribers);
            await signals.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }

        // ③ 단일 패스 토큰 스캐너 ≡ 기존 IndexOf 경로(복제 배열 = 폴백 경로) · 0B
        var cams = WanCameraStyleStudio.Cameras;
        var stys = WanCameraStyleStudio.Styles;
        var camsRef = (PromptChip[])cams.Clone();
        var stysRef = (PromptChip[])stys.Clone();
        var rnd = new Random(1515);
        var parts = new List<string>();
        for (int n = 0; n < 400; n++)
        {
            parts.Clear();
            int m = rnd.Next(0, 5);
            for (int j = 0; j < m; j++)
            {
                int pick = rnd.Next(0, 4);
                parts.Add(pick == 0 ? cams[rnd.Next(cams.Length)].Snippet
                        : pick == 1 ? stys[rnd.Next(stys.Length)].Snippet
                        : pick == 2 ? "아기 " + rnd.Next(100).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : "slow smooth sunset");   // 선두 문자 오탐 후보
            }
            var prompt = string.Join(rnd.Next(2) == 0 ? ", " : " ", parts) + (rnd.Next(3) == 0 ? ", " : "");
            Assert.Equal(WanCameraStyleStudio.ActiveIndex(prompt, camsRef), WanCameraStyleStudio.ActiveIndex(prompt, cams));
            Assert.Equal(WanCameraStyleStudio.ActiveIndex(prompt, stysRef), WanCameraStyleStudio.ActiveIndex(prompt, stys));
            int ci = rnd.Next(-1, cams.Length + 1), si = rnd.Next(-1, stys.Length + 1);
            Assert.Equal(WanCameraStyleStudio.ApplyExclusive(prompt, camsRef, ci), WanCameraStyleStudio.ApplyExclusive(prompt, cams, ci));
            Assert.Equal(WanCameraStyleStudio.ApplyExclusive(prompt, stysRef, si), WanCameraStyleStudio.ApplyExclusive(prompt, stys, si));
        }
        var full = "웃는 아기, " + stys[3].Snippet + ", " + cams[5].Snippet;
        for (int i = 0; i < 100; i++) _ = WanCameraStyleStudio.ActiveIndex(full, cams);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            _ = WanCameraStyleStudio.ActiveIndex(full, cams);
            _ = WanCameraStyleStudio.ActiveIndex(full, stys);
            _ = WanCameraStyleStudio.ApplyCamera(full, 5);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        // ④ Utf8JsonReader 스캐너 ≡ JsonDocument 의미(문자열 아닌 값 "" · 중첩 · 이스케이프 · BOM) · 손상 시 JsonException
        var w = WanWire.Parse("{\"output\":{\"task_id\":\"t\\u002d1\",\"task_status\":\"RUNNING\",\"extra\":[1,{\"a\":2}]},\"code\":7,\"message\":\"m\",\"request_id\":\"r\"}"u8);
        Assert.True(w.RootIsObject && w.HasOutput);
        Assert.Equal("t-1", w.TaskId);
        Assert.Equal("RUNNING", w.TaskStatus);
        Assert.Equal("", w.Code);
        Assert.Equal("m", w.Message);
        Assert.Equal("r", w.RequestId);
        var sfw = WanWire.Parse("{\"status\":\"Succeed\",\"results\":{\"seed\":1,\"videos\":[{\"x\":null,\"url\":\"u0\"},{\"url\":\"u1\"}]}}"u8);
        Assert.Equal("Succeed", sfw.Status);
        Assert.Equal("u0", sfw.FirstVideoUrl);
        var pw = WanWire.Parse("{\"data\":{\"upload_host\":\"h\",\"policy\":\"p\",\"x_oss_object_acl\":\"acl\"}}"u8);
        Assert.True(pw.HasData);
        Assert.Equal("h|p|acl|", pw.UploadHost + "|" + pw.Policy + "|" + pw.ObjectAcl + "|" + pw.Signature);
        Assert.False(WanWire.Parse("[1]"u8).RootIsObject);
        Assert.Throws<InvalidOperationException>(() => WanWire.Parse("[1]"u8).RequireObject());
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => WanWire.Parse(""u8));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => WanWire.Parse("{\"code\":"u8));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => WanWire.Parse("{} x"u8));

        var json = "{\"requestId\":\"sf-9\"}"u8.ToArray();
        var bom = new byte[json.Length + 3];
        bom[0] = 0xEF; bom[1] = 0xBB; bom[2] = 0xBF;
        Buffer.BlockCopy(json, 0, bom, 3, json.Length);
        var bomResp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bom) };
        using (var body = await PooledUtf8Body.ReadAsync(bomResp.Content, CancellationToken.None))
        {
            Assert.Equal("sf-9", WanWire.Parse(body.Span).RequestIdCamel);
            Assert.Equal("{\"requestId\":\"sf-9\"}", body.Text);
        }
        bomResp.Dispose();

        // ⑤ IAsyncEnumerable 폴링 스트림 — 결과 순차 방출 · 대기 중 취소는 예외 없이 종료
        var pv = new WanTokenVault(null);
        pv.UpdateProviderKeys(WanTokenVault.DashScope, new[] { "ds-1" });
        var mock = new RouteHandler();
        var svc = new WanApiService(new FakeWanConfig(), new QuotaRateGuardService(), pv, mock);
        var sub = await svc.SubmitAsync(new WanVideoRequest { Prompt = "아기" });
        Assert.True(sub.Ok, sub.Message);
        int polled = 0;
        using (var cts = new CancellationTokenSource())
        {
            await foreach (var q in svc.PollAsync(sub.TaskId!, TimeSpan.FromMilliseconds(1), cts.Token))
            {
                Assert.True(q.Ok);
                Assert.Equal(WanTaskStatus.Running, q.Status);
                if (++polled == 3) cts.Cancel();
            }
        }
        Assert.Equal(3, polled);
        Assert.Equal("ds-1", mock.LastQueryKey);
    }

    [Fact]
    public void NativeAOT_JsonSourceGenerator_And_SIMD_Check()
    {
        // ① WanJsonContext 메타데이터 = 소스 생성(리플렉션 리졸버 아님)
        Assert.NotNull(WanJsonContext.Default.DashScopeSubmitBody.SerializeHandler);
        Assert.NotNull(WanJsonContext.Default.SiliconFlowSubmitBody.SerializeHandler);

        // ② DashScope 본문: null 생략 · snake_case · 조건부 필드 정확
        var ds = new DashScopeSubmitBody
        {
            Model = "wan2.1-i2v-plus",
            Input = new DashScopeInput { Prompt = "p", ImgUrl = "oss://x" },
            Parameters = new DashScopeParameters { Resolution = "720P", PromptExtend = true },
        };
        var dsJson = System.Text.Json.JsonSerializer.Serialize(ds, WanJsonContext.Default.DashScopeSubmitBody);
        Assert.Equal("{\"model\":\"wan2.1-i2v-plus\",\"input\":{\"prompt\":\"p\",\"img_url\":\"oss://x\"},\"parameters\":{\"resolution\":\"720P\",\"prompt_extend\":true}}", dsJson);

        var sf = new SiliconFlowSubmitBody { Model = "m", Prompt = "q", ImageSize = "720x1280", Seed = 7 };
        Assert.Equal("{\"model\":\"m\",\"prompt\":\"q\",\"image_size\":\"720x1280\",\"seed\":7}",
            System.Text.Json.JsonSerializer.Serialize(sf, WanJsonContext.Default.SiliconFlowSubmitBody));
        Assert.Equal("{\"requestId\":\"r-1\"}",
            System.Text.Json.JsonSerializer.Serialize(new SiliconFlowStatusBody("r-1"), WanJsonContext.Default.SiliconFlowStatusBody));

        // ③ WanVideoRequest 왕복(데이터 손실 0)
        var req = new WanVideoRequest { Prompt = "한글 프롬프트", Size = "1280*720", Seed = 42, NegativePrompt = "blur" };
        var back = System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.Serialize(req, WanJsonContext.Default.WanVideoRequest), WanJsonContext.Default.WanVideoRequest);
        Assert.Equal(req, back);

        // ④ VaultFile 신규/레거시 포맷 + 볼트 디스크 왕복(키·엔드포인트 보존)
        var vf = System.Text.Json.JsonSerializer.Deserialize("{\"Keys\":{\"DashScope\":[\"a\",\"b\"]},\"Endpoints\":{\"Custom\":\"https://x.io\"}}"u8, WanJsonContext.Default.VaultFile);
        Assert.Equal(2, vf!.Keys!["DashScope"].Length);
        Assert.Equal("https://x.io", vf.Endpoints!["Custom"]);
        var legacy = System.Text.Json.JsonSerializer.Deserialize("{\"DashScope\":[\"a\"]}"u8, WanJsonContext.Default.VaultFile);
        Assert.Null(legacy!.Keys);
        Assert.Single(System.Text.Json.JsonSerializer.Deserialize("{\"DashScope\":[\"a\"]}"u8, WanJsonContext.Default.KeyMap)!["DashScope"]);

        var dir = Path.Combine(Path.GetTempPath(), "pfs-zenith-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "vault.enc");
        try
        {
            var v1 = new WanTokenVault(path);
            v1.UpdateProviderKeys(WanTokenVault.DashScope, new[] { "sk-1", "sk-2" });
            Assert.True(v1.SetProviderEndpoint(WanTokenVault.Custom, "https://custom.example.com/"));
            Assert.True(v1.SaveToDisk());
            var v2 = new WanTokenVault(path);
            Assert.Equal(2, v2.GetKeyCount(WanTokenVault.DashScope));
            Assert.Equal(v1.GetProviderEndpoint(WanTokenVault.Custom), v2.GetProviderEndpoint(WanTokenVault.Custom));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        // ⑤ SIMD 바이그램 프리필터 ≡ 스칼라 SearchValues ≡ IndexOf(블록 경계·꼬리·대량 텍스트)
        var rng = new Random(1616);
        var groups = new[] { WanCameraStyleStudio.Cameras, WanCameraStyleStudio.Styles };
        Span<int> simd = stackalloc int[16];
        Span<int> scalar = stackalloc int[16];
        var sb = new System.Text.StringBuilder();
        const string noise = "sdcF8w13 smooth slow cinematic, dolly crane ";
        for (var iter = 0; iter < 600; iter++)
        {
            var group = groups[iter & 1];
            sb.Clear();
            var len = rng.Next(0, iter < 300 ? 64 : 4096);
            while (sb.Length < len)
            {
                if (rng.Next(6) == 0) sb.Append(group[rng.Next(group.Length)].Snippet);
                else sb.Append(noise, rng.Next(noise.Length), 1);
            }
            var text = sb.ToString();
            WanCameraStyleStudio.ScanForTest(group, text, simd, simd: true);
            WanCameraStyleStudio.ScanForTest(group, text, scalar, simd: false);
            for (var i = 0; i < group.Length; i++)
            {
                var expected = text.IndexOf(group[i].Snippet, StringComparison.Ordinal);
                Assert.Equal(expected, scalar[i]);
                Assert.Equal(expected, simd[i]);
            }
        }

        // 토큰이 정확히 16자 블록 경계·꼬리에 걸치는 경우
        for (var pad = 0; pad < 40; pad++)
        {
            var text = new string('x', pad) + WanCameraStyleStudio.Styles[4].Snippet;
            WanCameraStyleStudio.ScanForTest(WanCameraStyleStudio.Styles, text, simd, simd: true);
            Assert.Equal(pad, simd[4]);
            Assert.Equal(4, WanCameraStyleStudio.ActiveIndex(text, WanCameraStyleStudio.Styles));
        }
    }

    private sealed class FakeLocalEngine : IVideoGenerationEngine
    {
        public int Calls;
        public bool IsEnabled = true;
        public string Key => "ComfyUI";
        public string DisplayName => "Fake Local GPU";
        public bool Enabled => IsEnabled;
        public Task<WanGenerationResult> GenerateAsync(WanVideoRequest request, IProgress<WanProgress>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new WanGenerationResult(true, WanTaskStatus.Succeeded, @"C:\local\out.mp4", "로컬 완료", null, TimeSpan.Zero));
        }
        public Task<WanUploadResult> UploadImageAsync(string localImagePath, string? model = null, CancellationToken ct = default)
            => Task.FromResult(new WanUploadResult(false, null, "미지원"));
    }

    internal sealed class FakeWanConfig : IWanConfigService
    {
        public string ApiKey => "";
        public string BaseUrl => "https://dashscope.mock";
        public string Model => "wan2.1-t2v-turbo";
        public string Size => "1280*720";
        public int PollIntervalSeconds => 1;
        public int MaxWaitSeconds => 5;
        public bool IsConfigured => false;
        public string FilePath => "";
        public DateTime? SavedAt => null;
#pragma warning disable CS0067
        public event Action? OnConfigChanged;
#pragma warning restore CS0067
        public bool Save(string apiKey, string baseUrl) => false;
    }

    // DashScope(getPolicy · OSS · 제출 · 조회) + SiliconFlow(제출 · 조회) 라우팅 모의.
    internal sealed class RouteHandler : HttpMessageHandler
    {
        public readonly Dictionary<string, HttpStatusCode> SubmitStatus = new();
        public readonly Dictionary<string, HttpStatusCode> PolicyStatus = new();
        public string LastSubmitUrl = "";
        public string LastQueryUrl = "";
        public long LastSiliconByteCount;
        public long? LastSiliconContentLength;
        public HttpStatusCode SiliconStatus = HttpStatusCode.OK;
        public readonly List<string> PolicyKeys = new();
        public readonly List<string> SubmitKeys = new();
        public int OssUploads;
        public string LastSubmitBody = "";
        public string LastSiliconBody = "";
        public string LastQueryKey = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            var key = request.Headers.Authorization?.Parameter ?? "";
            if (url.Contains("/api/v1/uploads", StringComparison.Ordinal))
            {
                PolicyKeys.Add(key);
                if (PolicyStatus.TryGetValue(key, out var ps)) return Json(ps, "{\"code\":\"Throttling\"}");
                return Json(HttpStatusCode.OK, "{\"data\":{\"upload_host\":\"https://oss.mock/\",\"upload_dir\":\"dir-" + key +
                    "\",\"oss_access_key_id\":\"id\",\"signature\":\"sig\",\"policy\":\"pol\"}}");
            }
            if (url.StartsWith("https://oss.mock", StringComparison.Ordinal))
            {
                OssUploads++;
                return Json(HttpStatusCode.OK, "");
            }
            if (url.Contains("/video-synthesis", StringComparison.Ordinal))
            {
                SubmitKeys.Add(key);
                LastSubmitUrl = url;
                LastSubmitBody = await request.Content!.ReadAsStringAsync(ct);
                var sc = SubmitStatus.TryGetValue(key, out var s) ? s : HttpStatusCode.OK;
                return sc == HttpStatusCode.OK
                    ? Json(sc, "{\"output\":{\"task_id\":\"task-" + key + "\",\"task_status\":\"PENDING\"},\"request_id\":\"r\"}")
                    : Json(sc, "{\"code\":\"Throttling\",\"message\":\"quota\"}");
            }
            if (url.Contains("/api/v1/tasks/", StringComparison.Ordinal))
            {
                LastQueryKey = key;
                LastQueryUrl = url;
                return Json(HttpStatusCode.OK, "{\"output\":{\"task_status\":\"RUNNING\"}}");
            }
            if (url.EndsWith("/v1/video/submit", StringComparison.Ordinal))
            {
                var raw = await request.Content!.ReadAsByteArrayAsync(ct);
                LastSiliconByteCount = raw.Length;
                LastSiliconContentLength = request.Content.Headers.ContentLength;
                LastSiliconBody = System.Text.Encoding.UTF8.GetString(raw);
                return SiliconStatus == HttpStatusCode.OK
                    ? Json(HttpStatusCode.OK, "{\"requestId\":\"sf-req-1\"}")
                    : Json(SiliconStatus, "{\"message\":\"quota\"}");
            }
            if (url.EndsWith("/v1/video/status", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"status\":\"Succeed\",\"results\":{\"videos\":[{\"url\":\"https://sf.example/video.mp4\"}]}}");
            return Json(HttpStatusCode.NotFound, "{}");
        }

        private static HttpResponseMessage Json(HttpStatusCode sc, string body)
            => new(sc) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }

    private sealed class KeyedHandler(Dictionary<string, HttpStatusCode> map) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var key = request.Headers.Authorization?.Parameter ?? "";
            var sc = map.TryGetValue(key, out var s) ? s : HttpStatusCode.Unauthorized;
            return Task.FromResult(new HttpResponseMessage(sc) { Content = new StringContent("{}") });
        }
    }
}
