using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace PromptFlowStudio.Services.Account;

/// <summary>도로명주소 검색 API 결과 1건.</summary>
public sealed record JusoResult(string Zip, string Road, string Jibun, string Building);

/// <summary>도로명주소 검색 응답(성공여부·메시지·총건수·목록).</summary>
public sealed record JusoResponse(bool Ok, string Message, int TotalCount, int CurrentPage, List<JusoResult> Results);

/// <summary>
/// [PromptFlowStudio] 행정안전부 도로명주소 '검색 API'(business.juso.go.kr) 서버측 연동 — 전국 실주소 실시간 검색.
///   BCL <see cref="HttpClient"/> 만 사용(외부 NuGet 0). 승인키는 appsettings <c>Juso:SearchKey</c>.
///   미구성(키 없음) 시 <see cref="Enabled"/>=false → 호출부(주소검색 모달)가 내장 모의 DB 로 폴백.
///   모니터링 오토메이션(SoundStack) JusoAddressService 이식본 — 거버넌스 로그 의존 제거.
/// </summary>
public sealed class JusoAddressService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private const string Endpoint = "https://business.juso.go.kr/addrlink/addrLinkApi.do";

    private readonly IConfiguration _cfg;

    public JusoAddressService(IConfiguration cfg) => _cfg = cfg;

    /// <summary>도로명주소 검색 API 승인키 구성 여부. false 면 모의 DB 폴백.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(_cfg["Juso:SearchKey"]);

    /// <summary>키워드로 도로명주소 검색(페이지당 20건).</summary>
    public async Task<JusoResponse> SearchAsync(string keyword, int page = 1)
    {
        var key = _cfg["Juso:SearchKey"] ?? "";
        if (string.IsNullOrWhiteSpace(key)) return new(false, "주소 API 미구성", 0, page, new());
        var kw = (keyword ?? "").Trim();
        if (kw.Length < 1) return new(false, "검색어를 입력하세요", 0, page, new());

        try
        {
            var url = Endpoint
                + "?confmKey=" + Uri.EscapeDataString(key)
                + "&currentPage=" + page
                + "&countPerPage=20"
                + "&resultType=json"
                + "&keyword=" + Uri.EscapeDataString(kw);

            var body = await Http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var results = doc.RootElement.GetProperty("results");
            var common = results.GetProperty("common");
            var errCode = common.GetProperty("errorCode").GetString() ?? "";
            if (errCode != "0")
            {
                var errMsg = common.TryGetProperty("errorMessage", out var em) ? em.GetString() : "오류";
                return new(false, $"주소 API 오류({errCode}): {errMsg}", 0, page, new());
            }

            int total = int.TryParse(common.TryGetProperty("totalCount", out var tc) ? tc.GetString() : "0", out var t) ? t : 0;
            var list = new List<JusoResult>();
            if (results.TryGetProperty("juso", out var juso) && juso.ValueKind == JsonValueKind.Array)
                foreach (var j in juso.EnumerateArray())
                    list.Add(new JusoResult(Str(j, "zipNo"), Str(j, "roadAddr"), Str(j, "jibunAddr"), Str(j, "bdNm")));

            return new(true, $"{total}건", total, page, list);
        }
        catch (Exception ex)
        {
            return new(false, "주소 검색 실패(네트워크): " + ex.Message, 0, page, new());
        }
    }

    private static string Str(JsonElement e, string prop) => e.TryGetProperty(prop, out var v) ? (v.GetString() ?? "") : "";
}
