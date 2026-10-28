using System;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Security;
using PromptFlowStudio.Services.Telemetry;
using PromptFlowStudio.Services.Ui;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Tests;

[Collection("ProcessGcMode")]   // EventListener 활성화는 프로세스 전역 + 스레드 할당 단언 → 직렬화
[Trait("Category", "Integration")]
public class LtsSealTests
{
    [Fact]
    public async Task LTS_Integrity_And_ETW_Check()
    {
        // ══ ① AppIntegrityGuard — RSA 서명 · 검증 · 변조 감지 ══
        var dir = Path.Combine(Path.GetTempPath(), "pfs-lts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var rsa = RSA.Create(2048);
            var priv = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
            var key = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
            File.WriteAllBytes(Path.Combine(dir, "A.dll"), RandomNumberGenerator.GetBytes(4096));
            File.WriteAllBytes(Path.Combine(dir, "B.dll"), RandomNumberGenerator.GetBytes(100_000));

            Assert.Equal(IntegrityVerdict.Unsealed, AppIntegrityGuard.Verify(dir, key).Verdict);   // 매니페스트 부재 → 종료 안 함
            Assert.False(AppIntegrityGuard.Verify(dir, key).ShouldShutdown);

            AppIntegrityGuard.SignManifest(priv, dir, new[] { "B.dll", "A.dll" });
            var ok = AppIntegrityGuard.Verify(dir, key);
            Assert.Equal(IntegrityVerdict.Verified, ok.Verdict);
            Assert.Equal(2, ok.Checked);
            Assert.Equal(0, ok.Failed);

            // 다른 키 → 봉인 불일치
            using (var other = RSA.Create(2048))
                Assert.Equal(IntegrityVerdict.Tampered, AppIntegrityGuard.Verify(dir, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())).Verdict);

            // 바이너리 1바이트 변조 → 해당 파일 지목 + 종료 트리거
            var bPath = Path.Combine(dir, "B.dll");
            var bytes = File.ReadAllBytes(bPath);
            bytes[50_000] ^= 0x01;
            File.WriteAllBytes(bPath, bytes);
            var bad = AppIntegrityGuard.Verify(dir, key);
            Assert.Equal(IntegrityVerdict.Tampered, bad.Verdict);
            Assert.True(bad.ShouldShutdown);
            Assert.Equal(1, bad.Failed);
            Assert.Equal("B.dll", bad.FirstFailure);
            bytes[50_000] ^= 0x01;
            File.WriteAllBytes(bPath, bytes);
            Assert.Equal(IntegrityVerdict.Verified, AppIntegrityGuard.Verify(dir, key).Verdict);

            // 파일 삭제 → Tampered
            var aPath = Path.Combine(dir, "A.dll");
            var aBytes = File.ReadAllBytes(aPath);
            File.Delete(aPath);
            Assert.Equal("A.dll", AppIntegrityGuard.Verify(dir, key).FirstFailure);
            File.WriteAllBytes(aPath, aBytes);

            // 매니페스트 항목 제거(집계 필드 위조 포함) → 봉인 불일치
            var mPath = Path.Combine(dir, AppIntegrityGuard.ManifestFileName);
            var lines = File.ReadAllLines(mPath);
            File.WriteAllLines(mPath, new[] { "PFI1 1", lines[1], lines[3] });
            Assert.Equal(IntegrityVerdict.Tampered, AppIntegrityGuard.Verify(dir, key).Verdict);
            File.WriteAllText(mPath, "garbage");
            Assert.Equal(IntegrityVerdict.Tampered, AppIntegrityGuard.Verify(dir, key).Verdict);

            // 경로 탈출 · 빈 키 거부
            Assert.Throws<ArgumentException>(() => AppIntegrityGuard.SignManifest(priv, dir, new[] { "..\\x.dll" }));
            Assert.Throws<ArgumentException>(() => AppIntegrityGuard.SignManifest("", dir, new[] { "A.dll" }));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }

        // ══ ② ZenithEventSource — 싱글톤 · 매니페스트 유효 ══
        var src = ZenithEventSource.Log;
        Assert.Same(src, ZenithEventSource.Log);
        Assert.Equal(ZenithEventSource.SourceName, src.Name);
        Assert.Null(src.ConstructionException);
        Assert.False(string.IsNullOrEmpty(EventSource.GenerateManifest(typeof(ZenithEventSource), "PromptFlowStudio.dll")));
        Assert.Equal(1, ZenithEventSource.ProviderCode(WanTokenVault.DashScope));
        Assert.Equal(0, ZenithEventSource.ProviderCode(null));

        // ══ ③ 리스너 미구독 → 결선 경로 0B ══
        var prompt = "a cat walking, " + WanCameraStyleStudio.Cameras[1].Snippet + " end";
        for (int w = 0; w < 3; w++)
        {
            src.OnKeyRotated(WanTokenVault.DashScope);
            src.OnVaultSignal(VaultSignalKind.Persisted, null, true);
            WanCameraStyleStudio.ActiveIndex(prompt, WanCameraStyleStudio.Cameras);
        }
        Assert.False(src.SimdTracing);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            src.OnKeyRotated(WanTokenVault.DashScope);
            src.OnVaultSignal(VaultSignalKind.KeysChanged, WanTokenVault.SiliconFlow, false);
            src.OnSimdScan(123, 45);
            WanCameraStyleStudio.ActiveIndex(prompt, WanCameraStyleStudio.Cameras);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        // ══ ④ 리스너 구독 → 실제 결선 지점(로테이션 · SIMD 스캔 · Vault)에서 이벤트 방출 ══
        using var listener = new CaptureListener();
        listener.EnableEvents(src, EventLevel.Verbose, EventKeywords.All);
        Assert.True(src.SimdTracing);

        long rotationsBefore = src.RotationCount;
        var vault = new WanTokenVault(null);
        vault.UpdateProviderKeys(WanTokenVault.DashScope, new[] { "sk-dead", "sk-live" });
        using (var http = new HttpClient(new QuotaHandler("sk-dead")))
        {
            var (resp, used) = await WanApiService.SendWithKeyRotationAsync(http, vault, WanTokenVault.DashScope, "sk-dead", 3,
                k => new HttpRequestMessage(HttpMethod.Get, "http://localhost/x") { Headers = { { "X-Key", k } } }, CancellationToken.None);
            using (resp)
            {
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.Equal("sk-live", used);
            }
        }
        Assert.Equal(rotationsBefore + 1, src.RotationCount);
        Assert.Equal(1, WanCameraStyleStudio.ActiveIndex(prompt, WanCameraStyleStudio.Cameras));

        Assert.True(listener.TryFind(ZenithEventSource.KeyRotatedId, out var rot));
        Assert.Equal(1L, (long)rot.Payload![0]!);
        Assert.Equal(rotationsBefore + 1, (long)rot.Payload[1]!);
        Assert.True(listener.TryFind(ZenithEventSource.SimdScanId, out var scan));
        Assert.True((long)scan.Payload![0]! >= 0);
        Assert.Equal((long)prompt.Length, (long)scan.Payload[1]!);
        Assert.True(listener.TryFind(ZenithEventSource.VaultStateId, out var vs));
        Assert.Equal((int)VaultSignalKind.KeysChanged, (int)vs.Payload![0]!);
        Assert.Equal(1, (int)vs.Payload[1]!);
        Assert.Empty(listener.Errors);
    }

    private sealed class QuotaHandler(string deadKey) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var dead = request.Headers.TryGetValues("X-Key", out var v) && string.Equals(System.Linq.Enumerable.First(v), deadKey, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(dead ? HttpStatusCode.PaymentRequired : HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private sealed class CaptureListener : EventListener
    {
        private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();
        public readonly ConcurrentQueue<string> Errors = new();

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventSource.Name != ZenithEventSource.SourceName) return;
            if (e.EventId <= 0) { Errors.Enqueue(e.Message ?? "?"); return; }   // 자체 진단(매니페스트/페이로드 오류) 이벤트
            _events.Enqueue(e);
        }

        public bool TryFind(int id, out EventWrittenEventArgs found)
        {
            foreach (var e in _events)
                if (e.EventId == id) { found = e; return true; }
            found = null!;
            return false;
        }
    }
}
