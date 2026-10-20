using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PromptFlowStudio.KeyGen;

// [PromptFlow Studio] 발급자(벤더) 전용 라이선스 서명 키 제너레이터 — 콘솔 도구.
//   ⚠ 이 도구는 RSA '개인 서명키'를 내장한다. <절대> 고객 배포본에 포함하면 안 된다(개인키 유출).
//     Tools\PromptFlowKeyGen\ 에 소스로만 두고, 배포 패키징(csproj CopyToolsToPublish · 패키저)에서 명시적으로 제외한다.
//   실행:
//     ① 인자 없이 → 발급자 서명키를 이 PC 의 %LocalAppData%\PromptFlowStudio\license_signing.dat 로 provisioning
//        (이후 앱 [관리자 설정 > 라이선스] 에서 직접 서명 발급 가능).
//     ② PromptFlowKeyGen <수령자명> [만료 yyyyMMdd] → 서명된 라이선스 토큰(payload.signature · base64url) 출력.
internal static class Program
{
    private sealed class Payload
    {
        public string? n { get; set; }
        public string? iat { get; set; }
        public string? exp { get; set; }
        public string? id { get; set; }
    }

    // 발급자 RSA 개인키(PKCS#8, base64). 앱의 공개키와 쌍을 이룬다. ⚠ 배포 금지.
    private const string PrivB64 = "MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQDGKlkTa3fIgj4qYweQbsuwQKvs5eLnazfahP4Y7zO0TNeDNdJGg+eewz5TCA5NJgxHmfRe3iNuLfhHJMKFTkB6MF3S5tvIqQiYFkDLKlG9eYetEVWrXnDC/ZV6RfBlQG3v7LkupQJWGD6iOyP4aTMGRK8hV3I3tsnTieFLQJSCIrY5PUTLeSep3QEX/OZJpD05q7uyVXv67aImI7q19tnovNJthv9/RZt/Vdwa5UFVecLgU2tO6b3kMBnTp64Pw6xhqLj9SBhy6/qjvP+3PR/tfXYJnbzAehnlxeCNBX3UaLkzMd7Lf9PYdWml0Fv1K1M50hys5Cmafe5B3JPHoAJZAgMBAAECggEBAKJpuOHatazHWk4BJgTxWc3V41LgwwxqareVQnCLM/Q0+nkWloWmvCH54U3KD3aZlXzlqExMgftef9tHPIpnLR84Ht2oyj92ko2bx3T79Ux2m4Vz7KPsXjzh+y3gvnstyCzY9plNMlH/wu3RzNRe3CvqBSMteXZCQL44hkbX4h+ZBCy4NBNYtcvgHUfwppKo5uGk6AE7v3jMvfQAs8SAzQwT6LV5jFuY6j5mSGhpBrsFklmrfudZZ8E0K3JrB8tArlKHfl7bV5+E49FaO7w3w+NuWhea6q+OjML8lZD6pCw4H77aTCJoen5ivi0QAg09KjQSXo24Y2Z+lsjG58ke8XUCgYEA51/er8rYI1sUyv7XgO3s5qnu820ZfNHud5JjKLSEQGjXgrME7TMzAud/gHhLWO+hUucCbFsMeHPB/YWIuI7yU/GY3LQXRMbLR0SID/G5xFxQlyWF7TWD9zI/CzklrcaB55Tvoh4j9SHXihSDMR1L2AnOg9NAmgLloyDeVYPy2RcCgYEA20GmOMpoCaqyOYUfK0NdMeESSsUDkCVtCiyBp46N1I+zQ5GHNwE3Jdf7O+UDq8hffeYyFc+4xHT0F11dha9e17W876Olugz2W3cfaGh+/whqng211oShiUiczpmbDUepBai3HY5fKLZG9OCrgaWDfTi207S4oFmo68R/3QbfRg8CgYA5hX8SSyai25D2ypZ/CiecTOEICbaNbo+PkIgwZXh/ntD9QE5YPiNPdIghQ0bQLHr55k6W66fVBkpTIfCUmY0rS2QKcodaZOXCqQn+lMTvSVgLy5tZFrXhLc+4lZOFeVVwYnmU31NnLOut+12GR1dEtWNvKITmBRRcEt7TecNVLwKBgHaBbzQ2wjCnYBkSyJv7lv2Epvxeq+RXNi5Pi+xicmo3GS8mEy05rPL5X8MpruhELsi/iMadNUKJ6/xM7GV7OgnhIrNrggGjS+x34DPwduOlSusOgf/mQ+ENrCNGhrWQy9xRKlgBQZGho4xreodiTEV8R+T5Quj4TQLRfEKcmkUlAoGAYYv5FoupGYGqJ6VuM9idGFv8sCFHBZQ0pCI+PifdBwEgnRqU08KeCN0Pr4pIAOZXPnX+K2W0lYADZ4X53CUyaVSdceAccyNv2P4tN4uAuuNg6fu2fxPs40XJFnA6hJaHT9Fs6HKxiNra0aP6YEX/YPzLpmnxH6hnzPk3zKLEviA=";

    private const string Seed = "PROMPTFLOWSTUDIO::SAFERISK::LICENSE-SIGNING::v1-fixed";

    private static string DataRoot()
    {
        var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(lad)) lad = Path.GetTempPath();
        var dir = Path.Combine(lad, "PromptFlowStudio");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static int Main(string[] args)
    {
        var dat = Path.Combine(DataRoot(), "license_signing.dat");
        File.WriteAllText(dat, Protect(PrivB64));
        Console.WriteLine("[provision] 발급자 서명키 설치됨: " + dat);
        Console.WriteLine("            이제 앱 [관리자 설정 > 라이선스] 에서 직접 발급할 수 있습니다.");
        Console.WriteLine();

        if (args.Length >= 1)
        {
            var licensee = args[0];
            DateTime? expiry = null;
            if (args.Length >= 2 && DateTime.TryParseExact(args[1], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
                expiry = result;

            Console.WriteLine("licensee : " + licensee);
            Console.WriteLine("expiry   : " + (expiry?.ToString("yyyy-MM-dd") ?? "(무기한)"));
            Console.WriteLine("license  :");
            Console.WriteLine(Issue(licensee, expiry));
        }
        else
        {
            Console.WriteLine("사용법: PromptFlowKeyGen <수령자명> [만료 yyyyMMdd]");
        }
        return 0;
    }

    private static string Issue(string licensee, DateTime? expiry)
    {
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(PrivB64), out _);
        var array = JsonSerializer.SerializeToUtf8Bytes(new Payload
        {
            n = (licensee ?? "").Trim(),
            iat = DateTime.Now.ToString("yyyyMMdd"),
            exp = expiry?.ToString("yyyyMMdd") ?? "",
            id = Guid.NewGuid().ToString("N").Substring(0, 8),
        });
        var sig = rsa.SignData(array, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return B64Url(array) + "." + B64Url(sig);
    }

    // 서명키를 이 PC 전용(머신·용도 바운드 AES-256-GCM)으로 봉인해 license_signing.dat 로 저장.
    private static string Protect(string plain)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(Seed));
        var bytes = Encoding.UTF8.GetBytes(plain);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[bytes.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, bytes, cipher, tag);
        var blob = new byte[28 + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, 12);
        Buffer.BlockCopy(tag, 0, blob, 12, 16);
        Buffer.BlockCopy(cipher, 0, blob, 28, cipher.Length);
        return Convert.ToBase64String(blob);
    }

    private static string B64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
