using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PromptFlowStudio.Shared.Store;

// PromptFlow Studio v4.4.0 · CI/CD 게이트키퍼 — 검수(기본) 또는 스토어 업로드(--upload-*) 디스패치.
string? Val(string key)
{
    var i = Array.IndexOf(args, key);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
bool Has(string key) => Array.IndexOf(args, key) >= 0;

if (Has("--upload-aab") || Has("--upload-ipa"))
    return await UploadAsync();

// 기본: AAB/IPA 실파일 검수 게이트(결함 시 Exit 1)
return CiCdPipelineRunner.Run(args);

async Task<int> UploadAsync()
{
    var gw = new StoreUploadRestGateway();
    var now = DateTimeOffset.UtcNow;

    if (Val("--upload-aab") is { } aab)
    {
        var saJson = Val("--google-sa") ?? Environment.GetEnvironmentVariable("GOOGLE_SA_JSON") ?? "";
        try
        {
            using var doc = JsonDocument.Parse(saJson);
            var email = doc.RootElement.GetProperty("client_email").GetString()!;
            var pem = doc.RootElement.GetProperty("private_key").GetString()!;
            var bearer = StoreUploadRestGateway.BuildGoogleJwtBearer(email, pem, now);
            var token = await gw.AuthorizeGoogleAsync(bearer);
            if (token is null) { Console.WriteLine("[CI] ❌ Google OAuth 실패"); return 1; }
            var r = await gw.UploadAabAsync(Val("--package") ?? "com.angtec.promptflowstudio", token, aab);
            Console.WriteLine("[CI] " + r.Message);
            return r.Ok ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine("[CI] ❌ Google 업로드 오류: " + ex.Message); return 1; }
    }

    if (Val("--upload-ipa") is { } ipa)
    {
        try
        {
            var jwt = StoreUploadRestGateway.BuildAppleJwt(Val("--asc-key-id") ?? "", Val("--asc-issuer") ?? "", Val("--asc-key") ?? "", now);
            var r = await gw.UploadIpaAsync(jwt, ipa);
            Console.WriteLine("[CI] " + r.Message);
            return r.Ok ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine("[CI] ❌ Apple 업로드 오류: " + ex.Message); return 1; }
    }
    return 0;
}
