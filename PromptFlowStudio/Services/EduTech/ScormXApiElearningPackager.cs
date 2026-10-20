using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace PromptFlowStudio.Services.EduTech;

public enum ElearningStandard : byte { Scorm12, Scorm2004, XApi, Lti13 }

public sealed record PackageResult(ElearningStandard Standard, string ZipPath, int Files, long Bytes, string AggregateSha256, IReadOnlyList<string> Entries);

public sealed record PackageValidation(bool Ok, ElearningStandard? Standard, IReadOnlyList<string> Errors);

public sealed record XApiActor(string Name, string Mbox);

public sealed record LrsSendResult(bool Ok, int Accepted, int Attempts, string? Error);

/// <summary>퀴즈 문항 분석(정답률 p-value · 첫 시도 정답률 · 평균 시도).</summary>
public sealed record QuizItemAnalytics(string QuizId, string Prompt, string Concept, int Learners, float PValue, float FirstTryRate, float AvgAttempts);

/// <summary>
/// [PromptFlow Studio v16.46.0] 📦 SCORM 1.2/2004 · xAPI(Tin Can) · LTI 1.3 국제 표준 이러닝 패키저 &amp; 분석 트랜스파일러.
///   ▸ 1-Click 오프라인 .zip: 공통 플레이어(index.html · player.js · 강좌 JSON 인라인 · 영상 사본) + 표준별 매니페스트(imsmanifest.xml SCORM 1.2 / 2004 4th · tincan.xml · LTI 1.3 도구 구성 + Common Cartridge BLTI 링크).
///   ▸ 런타임 어댑터: SCORM API(LMSInitialize/SetValue/Commit/Finish) · API_1484_11(Initialize/SetValue/Commit/Terminate) · xAPI 문장 POST(launch 파라미터) · LTI 독립 실행.
///   ▸ 텔레메트리 트랜스파일: 학습 이벤트 → xAPI 문장(ADL 동사 · 결정적 ID · 결과 점수/성공/응답 · 등록 컨텍스트) · SCORM CMI 데이터 모델 스냅샷(interactions 포함).
///   ▸ 무결성: 파일별 SHA-256 + 집계(파일 수 · 총 바이트 · 집계 해시) package-manifest.json · 패키지 검증기(필수 파일 · XML 파싱 · href 존재 · 해시 대조).
///   ▸ LRS 전송: 타임아웃 · 지수 백오프 3회 · 응답 스키마(ID 배열) 검증 · 자격 증명은 환경변수(PFS_XAPI_LRS_KEY/SECRET).
/// </summary>
public sealed class ScormXApiElearningPackager : IDisposable
{
    public const string ManifestName = "package-manifest.json", XApiVersion = "1.0.3", KeyVar = "PFS_XAPI_LRS_KEY", SecretVar = "PFS_XAPI_LRS_SECRET";
    public const string Scorm12Ns = "http://www.imsproject.org/xsd/imscp_rootv1p1p2", Adlcp12Ns = "http://www.adlnet.org/xsd/adlcp_rootv1p2";
    public const string Imscp2004Ns = "http://www.imsglobal.org/xsd/imscp_v1p1", Adlcp2004Ns = "http://www.adlnet.org/xsd/adlcp_v1p3", AdlseqNs = "http://www.adlnet.org/xsd/adlseq_v1p3", ImsssNs = "http://www.imsglobal.org/xsd/imsss";
    public const string TinCanNs = "http://projecttincan.com/tincan.xsd", CcNs = "http://www.imsglobal.org/xsd/imsccv1p3/imscp_v1p1", BltiNs = "http://www.imsglobal.org/xsd/imsbasiclti_v1p0", LticmNs = "http://www.imsglobal.org/xsd/imslticm_v1p0", LticpNs = "http://www.imsglobal.org/xsd/imslticp_v1p0", CcBltiNs = "http://www.imsglobal.org/xsd/imslticc_v1p0";

    private readonly HttpClient _http;
    private readonly Func<(string? Key, string? Secret)> _cred;
    private long _packages;

    public ScormXApiElearningPackager() : this(null, null) { }

    public ScormXApiElearningPackager(IServiceProvider sp) : this(null, null) { }

    public ScormXApiElearningPackager(HttpMessageHandler? handler, Func<(string? Key, string? Secret)>? credentials)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, false);
        _http.Timeout = TimeSpan.FromSeconds(15);
        _cred = credentials ?? (() => (Environment.GetEnvironmentVariable(KeyVar), Environment.GetEnvironmentVariable(SecretVar)));
    }

    public event Action? OnChange;

    public string OutputRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "ELearning");
    public string BaseIri { get; set; } = "https://promptflow.studio/xapi";
    public string LtiToolUrl { get; set; } = "https://tool.example.edu/lti";
    public long Packages => Interlocked.Read(ref _packages);
    public int LrsTimeoutMs { get; set; } = 8000;
    public IReadOnlyList<PackageResult> LastResults { get; private set; } = Array.Empty<PackageResult>();
    public string? LastAction { get; private set; }

    public static string Label(ElearningStandard s) => s switch { ElearningStandard.Scorm12 => "SCORM 1.2", ElearningStandard.Scorm2004 => "SCORM 2004 4th", ElearningStandard.XApi => "xAPI (Tin Can)", _ => "LTI 1.3" };
    private static string Slug(ElearningStandard s) => s switch { ElearningStandard.Scorm12 => "scorm12", ElearningStandard.Scorm2004 => "scorm2004", ElearningStandard.XApi => "xapi", _ => "lti13" };

    public string CourseIri(EduCourse c) => BaseIri.TrimEnd('/') + "/course/" + Uri.EscapeDataString(c.Id);
    public string QuizIri(EduCourse c, QuizItem q) => CourseIri(c) + "/quiz/" + Uri.EscapeDataString(q.Id);

    // ══════════════════════ 패키지 빌드 ══════════════════════

    public PackageResult Build(EduCourse course, LearnerSession? session, ElearningStandard std, string? outDir = null, XApiActor? actor = null)
    {
        if (course.Quizzes.Count == 0 && course.Chapters.Count == 0) throw new ArgumentException("빈 강좌는 패키징할 수 없습니다.");
        var files = new List<(string Path, byte[] Data)>();
        string? media = null;
        if (course.VideoPath is { Length: > 0 } vp && File.Exists(vp))
        {
            media = "media/" + SafeName(Path.GetFileName(vp));
            files.Add((media, File.ReadAllBytes(vp)));
        }
        files.Add(("index.html", Utf8(PlayerHtml(course, std, media))));
        files.Add(("player.js", Utf8(PlayerJs)));
        files.Add(("course.json", Utf8(CourseJson(course))));
        switch (std)
        {
            case ElearningStandard.Scorm12:
                files.Add(("imsmanifest.xml", Xml(Scorm12Manifest(course, files))));
                break;
            case ElearningStandard.Scorm2004:
                files.Add(("imsmanifest.xml", Xml(Scorm2004Manifest(course, files))));
                break;
            case ElearningStandard.XApi:
                files.Add(("tincan.xml", Xml(TinCanXml(course))));
                break;
            default:
                files.Add(("lti/tool-config.json", Utf8(LtiToolConfig(course))));
                files.Add(("lti/cartridge_basiclti_link.xml", Xml(BltiLink(course))));
                files.Add(("imsmanifest.xml", Xml(CommonCartridgeManifest(course))));
                break;
        }
        if (session is not null)
        {
            if (std is ElearningStandard.Scorm12 or ElearningStandard.Scorm2004) files.Add(("cmi-snapshot.json", Utf8(JsonSerializer.Serialize(ToScormCmi(course, session, std == ElearningStandard.Scorm2004)))));
            else files.Add(("statements.json", Utf8(BuildStatements(course, session, actor ?? new XApiActor(session.LearnerName, "mailto:" + SafeName(session.LearnerId) + "@learner.local")))));
        }
        var (manifest, aggregate) = IntegrityManifest(course, std, files);
        files.Add((ManifestName, manifest));

        var dir = outDir ?? OutputRoot;
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, SafeName(course.Id) + "_" + Slug(std) + ".zip");
        var tmp = zip + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
            foreach (var (p, d) in files)
            {
                var e = za.CreateEntry(p, CompressionLevel.Optimal);
                e.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var s = e.Open();
                s.Write(d);
            }
        File.Move(tmp, zip, true);
        Interlocked.Increment(ref _packages);
        var names = new List<string>(files.Count);
        foreach (var f in files) names.Add(f.Path);
        var res = new PackageResult(std, zip, files.Count, new FileInfo(zip).Length, aggregate, names);
        LastAction = "📦 " + Label(std) + " · " + Path.GetFileName(zip);
        Raise();
        return res;
    }

    /// <summary>1-Click 4종 표준 일괄 내보내기.</summary>
    public IReadOnlyList<PackageResult> ExportAll(EduCourse course, LearnerSession? session, string? outDir = null)
    {
        var list = new List<PackageResult>(4);
        foreach (var s in new[] { ElearningStandard.Scorm12, ElearningStandard.Scorm2004, ElearningStandard.XApi, ElearningStandard.Lti13 }) list.Add(Build(course, session, s, outDir));
        LastResults = list;
        LastAction = "📦 1-Click SCORM/xAPI/LTI 패키지 4종 생성";
        Raise();
        return list;
    }

    public static string SafeName(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        var r = sb.ToString().Trim('.');
        return r.Length == 0 ? "file" : r;
    }

    private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

    private static byte[] Xml(XDocument d)
    {
        using var ms = new MemoryStream();
        using (var w = System.Xml.XmlWriter.Create(ms, new System.Xml.XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true })) d.Save(w);
        return ms.ToArray();
    }

    private static (byte[] Json, string Aggregate) IntegrityManifest(EduCourse c, ElearningStandard std, List<(string Path, byte[] Data)> files)
    {
        long total = 0;
        var agg = new StringBuilder();
        using var ms = new MemoryStream();
        using (var j = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            j.WriteStartObject();
            j.WriteString("standard", Slug(std));
            j.WriteString("course", c.Id);
            j.WriteString("title", c.Title);
            j.WriteStartArray("files");
            foreach (var (p, d) in files)
            {
                var h = Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant();
                total += d.Length;
                agg.Append(p).Append(':').Append(h).Append(':').Append(d.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
                j.WriteStartObject(); j.WriteString("path", p); j.WriteString("sha256", h); j.WriteNumber("bytes", d.Length); j.WriteEndObject();
            }
            j.WriteEndArray();
            j.WriteNumber("fileCount", files.Count);
            j.WriteNumber("totalBytes", total);
            agg.Append("count=").Append(files.Count.ToString(CultureInfo.InvariantCulture)).Append(";bytes=").Append(total.ToString(CultureInfo.InvariantCulture));
            var aggregate = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agg.ToString()))).ToLowerInvariant();
            j.WriteString("aggregateSha256", aggregate);
            j.WriteEndObject();
            j.Flush();
            return (ms.ToArray(), aggregate);
        }
    }

    // ══════════════════════ 매니페스트 ══════════════════════

    public static XDocument Scorm12Manifest(EduCourse c, IReadOnlyList<(string Path, byte[] Data)> files)
    {
        XNamespace ns = Scorm12Ns, adl = Adlcp12Ns;
        var res = new XElement(ns + "resource", new XAttribute("identifier", "RES-1"), new XAttribute("type", "webcontent"), new XAttribute(adl + "scormtype", "sco"), new XAttribute("href", "index.html"));
        foreach (var f in files) res.Add(new XElement(ns + "file", new XAttribute("href", f.Path)));
        return new XDocument(new XElement(ns + "manifest",
            new XAttribute("identifier", "MANIFEST-" + SafeName(c.Id)), new XAttribute("version", "1.0"),
            new XAttribute(XNamespace.Xmlns + "adlcp", adl),
            new XElement(ns + "metadata", new XElement(ns + "schema", "ADL SCORM"), new XElement(ns + "schemaversion", "1.2")),
            new XElement(ns + "organizations", new XAttribute("default", "ORG-1"),
                new XElement(ns + "organization", new XAttribute("identifier", "ORG-1"), new XElement(ns + "title", c.Title),
                    new XElement(ns + "item", new XAttribute("identifier", "ITEM-1"), new XAttribute("identifierref", "RES-1"), new XElement(ns + "title", c.Title),
                        new XElement(adl + "masteryscore", ((int)Math.Round(c.PassingScore * 100)).ToString(CultureInfo.InvariantCulture))))),
            new XElement(ns + "resources", res)));
    }

    public static XDocument Scorm2004Manifest(EduCourse c, IReadOnlyList<(string Path, byte[] Data)> files)
    {
        XNamespace ns = Imscp2004Ns, adl = Adlcp2004Ns, seq = AdlseqNs, ss = ImsssNs;
        var res = new XElement(ns + "resource", new XAttribute("identifier", "RES-1"), new XAttribute("type", "webcontent"), new XAttribute(adl + "scormType", "sco"), new XAttribute("href", "index.html"));
        foreach (var f in files) res.Add(new XElement(ns + "file", new XAttribute("href", f.Path)));
        return new XDocument(new XElement(ns + "manifest",
            new XAttribute("identifier", "MANIFEST-" + SafeName(c.Id)), new XAttribute("version", "1.0"),
            new XAttribute(XNamespace.Xmlns + "adlcp", adl), new XAttribute(XNamespace.Xmlns + "adlseq", seq), new XAttribute(XNamespace.Xmlns + "imsss", ss),
            new XElement(ns + "metadata", new XElement(ns + "schema", "ADL SCORM"), new XElement(ns + "schemaversion", "2004 4th Edition")),
            new XElement(ns + "organizations", new XAttribute("default", "ORG-1"),
                new XElement(ns + "organization", new XAttribute("identifier", "ORG-1"), new XElement(ns + "title", c.Title),
                    new XElement(ns + "item", new XAttribute("identifier", "ITEM-1"), new XAttribute("identifierref", "RES-1"), new XElement(ns + "title", c.Title),
                        new XElement(ss + "sequencing",
                            new XElement(ss + "objectives", new XElement(ss + "primaryObjective", new XAttribute("objectiveID", "PRIMARY"), new XAttribute("satisfiedByMeasure", "true"),
                                new XElement(ss + "minNormalizedMeasure", c.PassingScore.ToString("0.##", CultureInfo.InvariantCulture)))),
                            new XElement(ss + "deliveryControls", new XAttribute("completionSetByContent", "true"), new XAttribute("objectiveSetByContent", "true")))))),
            new XElement(ns + "resources", res)));
    }

    public XDocument TinCanXml(EduCourse c)
    {
        XNamespace ns = TinCanNs;
        var acts = new XElement(ns + "activities",
            new XElement(ns + "activity", new XAttribute("id", CourseIri(c)), new XAttribute("type", "http://adlnet.gov/expapi/activities/course"),
                new XElement(ns + "name", new XAttribute("lang", c.Language), c.Title),
                new XElement(ns + "description", new XAttribute("lang", c.Language), c.Description),
                new XElement(ns + "launch", new XAttribute("lang", "und"), "index.html")));
        foreach (var q in c.Quizzes)
            acts.Add(new XElement(ns + "activity", new XAttribute("id", QuizIri(c, q)), new XAttribute("type", "http://adlnet.gov/expapi/activities/cmi.interaction"),
                new XElement(ns + "name", new XAttribute("lang", c.Language), q.Prompt),
                new XElement(ns + "description", new XAttribute("lang", c.Language), q.Concept)));
        return new XDocument(new XElement(ns + "tincan", acts));
    }

    public string LtiToolConfig(EduCourse c)
    {
        var url = LtiToolUrl.TrimEnd('/');
        using var ms = new MemoryStream();
        using (var j = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            j.WriteStartObject();
            j.WriteString("title", c.Title);
            j.WriteString("description", c.Description);
            j.WriteString("oidc_initiation_url", url + "/login");
            j.WriteString("target_link_uri", url + "/launch/" + Uri.EscapeDataString(c.Id));
            j.WriteString("public_jwk_url", url + "/.well-known/jwks.json");
            j.WriteStartArray("scopes");
            j.WriteStringValue("https://purl.imsglobal.org/spec/lti-ags/scope/score");
            j.WriteStringValue("https://purl.imsglobal.org/spec/lti-ags/scope/lineitem");
            j.WriteEndArray();
            j.WriteStartArray("claims"); foreach (var cl in new[] { "iss", "sub", "name", "email" }) j.WriteStringValue(cl); j.WriteEndArray();
            j.WriteStartObject("lineitem"); j.WriteNumber("scoreMaximum", 100); j.WriteString("label", c.Title); j.WriteString("resourceId", c.Id); j.WriteEndObject();
            j.WriteStartArray("messages"); j.WriteStartObject(); j.WriteString("type", "LtiResourceLinkRequest"); j.WriteString("target_link_uri", url + "/launch/" + Uri.EscapeDataString(c.Id)); j.WriteEndObject(); j.WriteEndArray();
            j.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public XDocument BltiLink(EduCourse c)
    {
        XNamespace cc = CcBltiNs, blti = BltiNs, lticm = LticmNs, lticp = LticpNs;
        var url = LtiToolUrl.TrimEnd('/') + "/launch/" + Uri.EscapeDataString(c.Id);
        return new XDocument(new XElement(cc + "cartridge_basiclti_link",
            new XAttribute(XNamespace.Xmlns + "blti", blti), new XAttribute(XNamespace.Xmlns + "lticm", lticm), new XAttribute(XNamespace.Xmlns + "lticp", lticp),
            new XElement(blti + "title", c.Title), new XElement(blti + "description", c.Description),
            new XElement(blti + "launch_url", url), new XElement(blti + "secure_launch_url", url),
            new XElement(blti + "extensions", new XAttribute("platform", "promptflow.studio"), new XElement(lticm + "property", new XAttribute("name", "lti_version"), "1.3.0")),
            new XElement(blti + "vendor", new XElement(lticp + "code", "promptflow"), new XElement(lticp + "name", "PromptFlow Studio"))));
    }

    public static XDocument CommonCartridgeManifest(EduCourse c)
    {
        XNamespace ns = CcNs;
        return new XDocument(new XElement(ns + "manifest", new XAttribute("identifier", "CC-" + SafeName(c.Id)),
            new XElement(ns + "metadata", new XElement(ns + "schema", "IMS Common Cartridge"), new XElement(ns + "schemaversion", "1.3.0")),
            new XElement(ns + "organizations", new XElement(ns + "organization", new XAttribute("identifier", "ORG-1"), new XAttribute("structure", "rooted-hierarchy"),
                new XElement(ns + "item", new XAttribute("identifier", "ROOT"),
                    new XElement(ns + "item", new XAttribute("identifier", "ITEM-1"), new XAttribute("identifierref", "LTI-1"), new XElement(ns + "title", c.Title))))),
            new XElement(ns + "resources", new XElement(ns + "resource", new XAttribute("identifier", "LTI-1"), new XAttribute("type", "imsbasiclti_xmlv1p0"),
                new XElement(ns + "file", new XAttribute("href", "lti/cartridge_basiclti_link.xml"))))));
    }

    // ══════════════════════ 플레이어 ══════════════════════

    public static string CourseJson(EduCourse c) => JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = false });

    private static string PlayerHtml(EduCourse c, ElearningStandard std, string? media)
    {
        var json = CourseJson(c).Replace("</", "<\\/", StringComparison.Ordinal);
        var title = System.Net.WebUtility.HtmlEncode(c.Title);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"").Append(c.Language).Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>").Append(title).Append("</title>\n");
        sb.Append("<style>body{margin:0;font-family:'Malgun Gothic',sans-serif;background:#0b1220;color:#e2e8f0}#stage{position:relative;max-width:960px;margin:20px auto}video,#poster{width:100%;aspect-ratio:16/9;background:#111827;display:block}#card{position:absolute;inset:0;background:rgba(0,0,0,.6);display:none;align-items:center;justify-content:center}#card>div{background:#0f172a;border-top:4px solid #6366f1;padding:20px;width:80%;border-radius:10px}button.opt{display:block;width:100%;margin:6px 0;padding:10px;background:#1e293b;color:#e2e8f0;border:0;border-radius:6px;text-align:left;cursor:pointer}#fb{margin-top:10px}#bar{height:8px;background:#1e293b}#bar>div{height:100%;background:#6366f1;width:0}</style></head><body>\n");
        sb.Append("<div id=\"stage\">");
        if (media is not null) sb.Append("<video id=\"video\" controls src=\"").Append(media).Append("\"></video>");
        else sb.Append("<div id=\"poster\"></div><button id=\"play\">▶</button>");
        sb.Append("<div id=\"card\"><div><div id=\"q\"></div><div id=\"opts\"></div><input id=\"txt\" style=\"display:none;width:100%\"><button id=\"submit\">제출</button><div id=\"fb\"></div></div></div><div id=\"bar\"><div></div></div></div>\n");
        sb.Append("<script id=\"course\" type=\"application/json\">").Append(json).Append("</script>\n");
        sb.Append("<script>window.PFS_STANDARD='").Append(Slug(std)).Append("';</script>\n<script src=\"player.js\"></script>\n</body></html>\n");
        return sb.ToString();
    }

    /// <summary>표준 어댑터 포함 퀴즈 플레이어 런타임(ES5 · 의존성 0).</summary>
    public const string PlayerJs = """
(function () {
  var course = JSON.parse(document.getElementById('course').textContent);
  var std = window.PFS_STANDARD, quizzes = course.Quizzes || [], done = {}, best = {}, active = -1, t0 = new Date();
  function findApi(name) { var w = window, n = 0; while (w && n++ < 10) { if (w[name]) return w[name]; if (w.parent === w) break; w = w.parent; } return window.opener && window.opener[name] ? window.opener[name] : null; }
  var api12 = std === 'scorm12' ? findApi('API') : null, api04 = std === 'scorm2004' ? findApi('API_1484_11') : null;
  var q = new URLSearchParams(location.search), lrs = q.get('endpoint'), auth = q.get('auth'), actor = q.get('actor');
  function stmt(verb, obj, result) {
    if (!lrs || !actor) return;
    var s = { actor: JSON.parse(actor), verb: { id: 'http://adlnet.gov/expapi/verbs/' + verb, display: { 'en-US': verb } }, object: { id: obj, objectType: 'Activity' }, timestamp: new Date().toISOString() };
    if (result) s.result = result;
    fetch(lrs.replace(/\/$/, '') + '/statements', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Experience-API-Version': '1.0.3', 'Authorization': auth || '' }, body: JSON.stringify(s) }).catch(function () {});
  }
  var courseIri = location.href.split('?')[0];
  function init() { if (api12) api12.LMSInitialize(''); if (api04) api04.Initialize(''); stmt('launched', courseIri); }
  function score() { var got = 0, tot = 0; quizzes.forEach(function (x) { tot += x.Points; got += x.Points * (best[x.Id] || 0); }); return tot ? got / tot : 1; }
  function report(progress) {
    var s = score(), complete = quizzes.every(function (x) { return !x.Required || done[x.Id]; }) && progress >= 0.9, passed = complete && s >= course.PassingScore;
    if (api12) { api12.LMSSetValue('cmi.core.score.raw', String(Math.round(s * 100))); api12.LMSSetValue('cmi.core.lesson_status', passed ? 'passed' : complete ? 'failed' : 'incomplete'); api12.LMSCommit(''); }
    if (api04) { api04.SetValue('cmi.score.scaled', s.toFixed(3)); api04.SetValue('cmi.progress_measure', progress.toFixed(3)); api04.SetValue('cmi.completion_status', complete ? 'completed' : 'incomplete'); api04.SetValue('cmi.success_status', complete ? (passed ? 'passed' : 'failed') : 'unknown'); api04.Commit(''); }
    if (complete) stmt(passed ? 'passed' : 'failed', courseIri, { score: { scaled: s }, success: passed, completion: true });
  }
  function grade(x, mask, text) {
    if (x.Kind === 3) { var n = function (s) { return (s || '').toLowerCase().replace(/[^\p{L}\p{N}]/gu, ''); }; return x.Answers.some(function (a) { return n(a) === n(text); }) ? 1 : 0; }
    if (x.Kind === 1) { var c = 0, h = 0, w = 0; for (var i = 0; i < 31; i++) { var b = 1 << i; if (x.CorrectMask & b) { c++; if (mask & b) h++; } else if (mask & b) w++; } return c ? Math.max(0, (h - w) / c) : 0; }
    return mask && mask === x.CorrectMask ? 1 : 0;
  }
  var video = document.getElementById('video'), card = document.getElementById('card'), sel = 0, tries = {};
  function show(i) {
    active = i; sel = 0; var x = quizzes[i]; if (video) video.pause();
    document.getElementById('q').textContent = x.Prompt; document.getElementById('fb').textContent = '';
    var opts = document.getElementById('opts'); opts.innerHTML = ''; document.getElementById('txt').style.display = x.Kind === 3 ? 'block' : 'none';
    (x.Options || []).forEach(function (o, k) { var b = document.createElement('button'); b.className = 'opt'; b.textContent = String.fromCharCode(65 + k) + '. ' + o; b.onclick = function () { sel = x.Kind === 1 ? sel ^ (1 << k) : (1 << k); b.style.background = (sel & (1 << k)) ? '#4f46e5' : '#1e293b'; }; opts.appendChild(b); });
    card.style.display = 'flex';
  }
  document.getElementById('submit').onclick = function () {
    var x = quizzes[active], s = grade(x, sel, document.getElementById('txt').value); tries[x.Id] = (tries[x.Id] || 0) + 1; best[x.Id] = Math.max(best[x.Id] || 0, s);
    var fin = s >= 1 || tries[x.Id] >= x.MaxAttempts; document.getElementById('fb').textContent = s >= 1 ? '정답! ' + x.Explanation : fin ? '해설: ' + x.Explanation : '다시 시도해 보세요. ' + (x.Hint || '');
    stmt('answered', courseIri + '/quiz/' + encodeURIComponent(x.Id), { score: { scaled: s }, success: s >= 1 });
    if (fin) { done[x.Id] = true; setTimeout(function () { card.style.display = 'none'; active = -1; if (video) video.play(); }, 1200); }
  };
  var pos = 0;
  function tick(t) {
    pos = t; document.querySelector('#bar>div').style.width = (100 * t / course.DurationSec) + '%';
    if (active < 0) for (var i = 0; i < quizzes.length; i++) if (!done[quizzes[i].Id] && quizzes[i].TimeSec <= t && quizzes[i].TimeSec > t - 1.5) { show(i); break; }
    report(Math.min(1, t / course.DurationSec));
  }
  if (video) video.addEventListener('timeupdate', function () { tick(video.currentTime); });
  else { var b = document.getElementById('play'); b.onclick = function () { setInterval(function () { if (active < 0 && pos < course.DurationSec) tick(pos + 1); }, 100); }; }
  window.addEventListener('beforeunload', function () { if (api12) api12.LMSFinish(''); if (api04) api04.Terminate(''); });
  init();
})();
""";

    // ══════════════════════ 텔레메트리 트랜스파일 ══════════════════════

    public static string VerbIri(LearningVerb v) => v switch
    {
        LearningVerb.Launched => "http://adlnet.gov/expapi/verbs/launched",
        LearningVerb.Progressed => "http://adlnet.gov/expapi/verbs/progressed",
        LearningVerb.Paused => "http://adlnet.gov/expapi/verbs/suspended",
        LearningVerb.Answered => "http://adlnet.gov/expapi/verbs/answered",
        LearningVerb.Skipped => "http://id.tincanapi.com/verb/skipped",
        LearningVerb.Asked => "http://adlnet.gov/expapi/verbs/asked",
        LearningVerb.Tutored => "http://adlnet.gov/expapi/verbs/experienced",
        LearningVerb.Completed => "http://adlnet.gov/expapi/verbs/completed",
        LearningVerb.Passed => "http://adlnet.gov/expapi/verbs/passed",
        _ => "http://adlnet.gov/expapi/verbs/failed",
    };

    private static string InteractionType(QuizKind k) => k switch { QuizKind.TrueFalse => "true-false", QuizKind.ShortAnswer => "fill-in", QuizKind.Hotspot => "performance", _ => "choice" };

    /// <summary>결정적 문장 ID(SHA-256 → UUID v4 형식) — 재전송 멱등.</summary>
    public static Guid StatementId(string courseId, string learnerId, int index, LearningVerb verb, DateTimeOffset at)
    {
        Span<byte> h = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(courseId + "|" + learnerId + "|" + index.ToString(CultureInfo.InvariantCulture) + "|" + ((int)verb).ToString(CultureInfo.InvariantCulture) + "|" + at.UtcTicks.ToString(CultureInfo.InvariantCulture)), h);
        h[6] = (byte)((h[6] & 0x0F) | 0x40); h[8] = (byte)((h[8] & 0x3F) | 0x80);
        return new Guid(h[..16], true);
    }

    /// <summary>학습 이벤트 → xAPI 문장 배열 JSON.</summary>
    public string BuildStatements(EduCourse c, LearnerSession s, XApiActor actor)
    {
        if (!actor.Mbox.StartsWith("mailto:", StringComparison.Ordinal)) throw new ArgumentException("xAPI actor.mbox 는 mailto: IRI 여야 합니다.");
        var registration = StatementId(c.Id, s.LearnerId, -1, LearningVerb.Launched, s.StartedAt);
        LearningEvent[] events;
        lock (s.Events) events = s.Events.ToArray();
        using var ms = new MemoryStream();
        using (var j = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            j.WriteStartArray();
            for (var i = 0; i < events.Length; i++)
            {
                var e = events[i];
                var q = (uint)e.QuizIndex < (uint)c.Quizzes.Count ? c.Quizzes[e.QuizIndex] : null;
                j.WriteStartObject();
                j.WriteString("id", StatementId(c.Id, s.LearnerId, i, e.Verb, e.At).ToString());
                j.WriteStartObject("actor"); j.WriteString("objectType", "Agent"); j.WriteString("name", actor.Name); j.WriteString("mbox", actor.Mbox); j.WriteEndObject();
                j.WriteStartObject("verb"); j.WriteString("id", VerbIri(e.Verb)); j.WriteStartObject("display"); j.WriteString("en-US", e.Verb.ToString().ToLowerInvariant()); j.WriteEndObject(); j.WriteEndObject();
                j.WriteStartObject("object");
                j.WriteString("objectType", "Activity");
                j.WriteString("id", q is null ? CourseIri(c) : QuizIri(c, q));
                j.WriteStartObject("definition");
                j.WriteStartObject("name"); j.WriteString(c.Language, q is null ? c.Title : q.Prompt); j.WriteEndObject();
                j.WriteString("type", q is null ? "http://adlnet.gov/expapi/activities/course" : "http://adlnet.gov/expapi/activities/cmi.interaction");
                if (q is not null)
                {
                    j.WriteString("interactionType", InteractionType(q.Kind));
                    j.WriteStartArray("correctResponsesPattern"); j.WriteStringValue(q.Kind == QuizKind.ShortAnswer ? InteractiveEduVideoQuizEngine.AnswerText(q) : q.CorrectMask.ToString(CultureInfo.InvariantCulture)); j.WriteEndArray();
                }
                j.WriteEndObject();
                j.WriteEndObject();
                if (e.Verb is LearningVerb.Answered or LearningVerb.Passed or LearningVerb.Failed or LearningVerb.Completed)
                {
                    j.WriteStartObject("result");
                    j.WriteStartObject("score"); j.WriteNumber("scaled", Math.Round(Math.Clamp(e.Score, 0, 1), 4)); j.WriteEndObject();
                    j.WriteBoolean("success", e.Success);
                    if (e.Verb != LearningVerb.Answered) j.WriteBoolean("completion", true);
                    if (e.Response is not null) j.WriteString("response", e.Response);
                    j.WriteEndObject();
                }
                j.WriteStartObject("context");
                j.WriteString("registration", registration.ToString());
                j.WriteStartObject("contextActivities"); j.WriteStartArray("parent"); j.WriteStartObject(); j.WriteString("id", CourseIri(c)); j.WriteEndObject(); j.WriteEndArray(); j.WriteEndObject();
                j.WriteStartObject("extensions"); j.WriteNumber("https://w3id.org/xapi/video/extensions/time", Math.Round(e.VideoSec, 3)); j.WriteEndObject();
                j.WriteEndObject();
                j.WriteString("timestamp", e.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                j.WriteEndObject();
            }
            j.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>xAPI 문장 스키마 검증(필수 필드 · IRI · mailto · 점수 범위 · 타임스탬프). 반환 = 위반 목록.</summary>
    public static List<string> ValidateStatements(string json)
    {
        var errs = new List<string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) { errs.Add("문장 배열 아님"); return errs; }
        var i = 0;
        foreach (var s in doc.RootElement.EnumerateArray())
        {
            var p = "#" + i.ToString(CultureInfo.InvariantCulture) + " ";
            if (!s.TryGetProperty("id", out var id) || !Guid.TryParse(id.GetString(), out _)) errs.Add(p + "id");
            if (!s.TryGetProperty("actor", out var a) || !a.TryGetProperty("mbox", out var mb) || mb.GetString()?.StartsWith("mailto:", StringComparison.Ordinal) != true) errs.Add(p + "actor.mbox");
            if (!s.TryGetProperty("verb", out var v) || !v.TryGetProperty("id", out var vid) || !Uri.TryCreate(vid.GetString(), UriKind.Absolute, out _)) errs.Add(p + "verb.id");
            if (!s.TryGetProperty("object", out var o) || !o.TryGetProperty("id", out var oid) || !Uri.TryCreate(oid.GetString(), UriKind.Absolute, out _)) errs.Add(p + "object.id");
            if (!s.TryGetProperty("timestamp", out var ts) || !DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)) errs.Add(p + "timestamp");
            if (s.TryGetProperty("result", out var r) && r.TryGetProperty("score", out var sc) && sc.TryGetProperty("scaled", out var scaled) && (scaled.GetDouble() < -1 || scaled.GetDouble() > 1)) errs.Add(p + "result.score.scaled");
            i++;
        }
        return errs;
    }

    /// <summary>SCORM CMI 데이터 모델 스냅샷(1.2 cmi.core.* / 2004 cmi.*) · interactions 포함.</summary>
    public static SortedDictionary<string, string> ToScormCmi(EduCourse c, LearnerSession s, bool scorm2004)
    {
        var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
        double got = 0, tot = 0;
        var allDone = true;
        for (var i = 0; i < c.Quizzes.Count && i < s.Best.Length; i++)
        {
            tot += c.Quizzes[i].Points; got += c.Quizzes[i].Points * s.Best[i];
            if (c.Quizzes[i].Required && !s.Done[i]) allDone = false;
        }
        var score = tot <= 0 ? 1 : got / tot;
        var progress = s.WatchedCount / (double)s.WatchSeconds;
        var complete = allDone && progress >= 0.9;
        var passed = complete && score >= c.PassingScore;
        var inv = CultureInfo.InvariantCulture;
        var n = 0;
        for (var i = 0; i < c.Quizzes.Count && i < s.Attempts.Length; i++)
        {
            if (s.Attempts[i] == 0) continue;
            var q = c.Quizzes[i];
            var pre = "cmi.interactions." + n.ToString(inv) + ".";
            d[pre + "id"] = SafeName(q.Id);
            d[pre + "type"] = InteractionType(q.Kind);
            d[pre + "result"] = s.Best[i] >= 0.999f ? "correct" : (scorm2004 ? "incorrect" : "wrong");
            d[pre + (scorm2004 ? "learner_response" : "student_response")] = q.Kind == QuizKind.ShortAnswer ? "" : s.Best[i].ToString("0.##", inv);
            n++;
        }
        if (scorm2004)
        {
            d["cmi.completion_status"] = complete ? "completed" : "incomplete";
            d["cmi.success_status"] = complete ? (passed ? "passed" : "failed") : "unknown";
            d["cmi.score.scaled"] = score.ToString("0.###", inv);
            d["cmi.score.raw"] = Math.Round(score * 100).ToString(inv);
            d["cmi.score.min"] = "0"; d["cmi.score.max"] = "100";
            d["cmi.progress_measure"] = Math.Min(1, progress).ToString("0.###", inv);
            d["cmi.interactions._count"] = n.ToString(inv);
        }
        else
        {
            d["cmi.core.lesson_status"] = passed ? "passed" : complete ? "failed" : "incomplete";
            d["cmi.core.score.raw"] = Math.Round(score * 100).ToString(inv);
            d["cmi.core.score.min"] = "0"; d["cmi.core.score.max"] = "100";
            d["cmi.interactions._count"] = n.ToString(inv);
        }
        return d;
    }

    /// <summary>문항 분석(다수 학습자 세션).</summary>
    public static List<QuizItemAnalytics> AnalyzeItems(EduCourse c, IReadOnlyList<LearnerSession> sessions)
    {
        var list = new List<QuizItemAnalytics>(c.Quizzes.Count);
        for (var i = 0; i < c.Quizzes.Count; i++)
        {
            int learners = 0, first = 0, attempts = 0; double sum = 0;
            foreach (var s in sessions)
            {
                if (i >= s.Attempts.Length || s.Attempts[i] == 0) continue;
                learners++; attempts += s.Attempts[i]; sum += s.Best[i];
                if (s.Attempts[i] == 1 && s.Best[i] >= 0.999f) first++;
            }
            var q = c.Quizzes[i];
            list.Add(new QuizItemAnalytics(q.Id, q.Prompt, q.Concept, learners, learners == 0 ? 0 : (float)(sum / learners), learners == 0 ? 0 : first / (float)learners, learners == 0 ? 0 : attempts / (float)learners));
        }
        return list;
    }

    // ══════════════════════ 검증 ══════════════════════

    public static PackageValidation Validate(string zipPath)
    {
        var errs = new List<string>();
        ElearningStandard? std = null;
        try
        {
            using var za = ZipFile.OpenRead(zipPath);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var e in za.Entries) entries[e.FullName] = e;
            byte[] Read(string n) { using var s = entries[n].Open(); using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }
            if (!entries.ContainsKey(ManifestName)) return new PackageValidation(false, null, new[] { "package-manifest.json 없음" });
            using (var doc = JsonDocument.Parse(Read(ManifestName)))
            {
                var r = doc.RootElement;
                std = r.GetProperty("standard").GetString() switch { "scorm12" => ElearningStandard.Scorm12, "scorm2004" => ElearningStandard.Scorm2004, "xapi" => ElearningStandard.XApi, "lti13" => ElearningStandard.Lti13, _ => null };
                if (std is null) errs.Add("알 수 없는 표준");
                var agg = new StringBuilder(); long total = 0; var count = 0;
                foreach (var f in r.GetProperty("files").EnumerateArray())
                {
                    var p = f.GetProperty("path").GetString()!;
                    if (!entries.ContainsKey(p)) { errs.Add("파일 누락: " + p); continue; }
                    var data = Read(p);
                    var h = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
                    if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(h), Encoding.ASCII.GetBytes(f.GetProperty("sha256").GetString() ?? ""))) errs.Add("해시 불일치: " + p);
                    agg.Append(p).Append(':').Append(h).Append(':').Append(data.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    total += data.Length; count++;
                }
                agg.Append("count=").Append(count.ToString(CultureInfo.InvariantCulture)).Append(";bytes=").Append(total.ToString(CultureInfo.InvariantCulture));
                var aggregate = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agg.ToString()))).ToLowerInvariant();
                if (r.GetProperty("fileCount").GetInt32() != count || r.GetProperty("totalBytes").GetInt64() != total) errs.Add("집계 불일치");
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(aggregate), Encoding.ASCII.GetBytes(r.GetProperty("aggregateSha256").GetString() ?? ""))) errs.Add("집계 해시 불일치");
            }
            foreach (var req in new[] { "index.html", "player.js", "course.json" }) if (!entries.ContainsKey(req)) errs.Add("필수 파일 없음: " + req);
            switch (std)
            {
                case ElearningStandard.Scorm12:
                case ElearningStandard.Scorm2004:
                {
                    if (!entries.ContainsKey("imsmanifest.xml")) { errs.Add("imsmanifest.xml 없음"); break; }
                    var x = XDocument.Parse(Encoding.UTF8.GetString(Read("imsmanifest.xml")));
                    var ver = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "schemaversion")?.Value;
                    if (ver != (std == ElearningStandard.Scorm12 ? "1.2" : "2004 4th Edition")) errs.Add("schemaversion 불일치: " + ver);
                    var rs = x.Descendants().Where(e => e.Name.LocalName == "resource").ToList();
                    if (rs.Count == 0) errs.Add("resource 없음");
                    foreach (var re in rs)
                    {
                        var href = (string?)re.Attribute("href");
                        if (href is null || !entries.ContainsKey(href)) errs.Add("resource href 누락: " + href);
                        if (!re.Attributes().Any(a => a.Name.LocalName.Equals("scormtype", StringComparison.OrdinalIgnoreCase) && a.Value == "sco")) errs.Add("scormtype=sco 없음");
                    }
                    break;
                }
                case ElearningStandard.XApi:
                {
                    if (!entries.ContainsKey("tincan.xml")) { errs.Add("tincan.xml 없음"); break; }
                    var x = XDocument.Parse(Encoding.UTF8.GetString(Read("tincan.xml")));
                    var launch = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "launch")?.Value;
                    if (launch is null || !entries.ContainsKey(launch)) errs.Add("launch 누락");
                    if (entries.ContainsKey("statements.json")) foreach (var e in ValidateStatements(Encoding.UTF8.GetString(Read("statements.json")))) errs.Add("문장 " + e);
                    break;
                }
                case ElearningStandard.Lti13:
                {
                    foreach (var req in new[] { "lti/tool-config.json", "lti/cartridge_basiclti_link.xml", "imsmanifest.xml" }) if (!entries.ContainsKey(req)) errs.Add("LTI 파일 없음: " + req);
                    if (entries.ContainsKey("lti/tool-config.json"))
                    {
                        using var cfg = JsonDocument.Parse(Read("lti/tool-config.json"));
                        foreach (var k in new[] { "oidc_initiation_url", "target_link_uri", "public_jwk_url" })
                            if (!cfg.RootElement.TryGetProperty(k, out var u) || !Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) errs.Add("LTI " + k + " https 아님");
                    }
                    if (entries.ContainsKey("lti/cartridge_basiclti_link.xml")) XDocument.Parse(Encoding.UTF8.GetString(Read("lti/cartridge_basiclti_link.xml")));
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or System.Xml.XmlException or KeyNotFoundException or InvalidOperationException)
        {
            errs.Add("패키지 손상: " + ex.GetType().Name);
        }
        return new PackageValidation(errs.Count == 0, std, errs);
    }

    // ══════════════════════ LRS 전송 ══════════════════════

    /// <summary>xAPI 문장 LRS 전송(Basic 인증 · 데드라인 · 지수 백오프 3회 · 응답 ID 배열 검증).</summary>
    public async Task<LrsSendResult> SendStatementsAsync(string endpoint, string statementsJson, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var ep) || (ep.Scheme != Uri.UriSchemeHttps && !ep.IsLoopback)) return new LrsSendResult(false, 0, 0, "LRS 엔드포인트는 https 여야 합니다.");
        var (key, secret) = _cred();
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret)) return new LrsSendResult(false, 0, 0, "LRS 자격 증명 없음(" + KeyVar + "/" + SecretVar + ")");
        var auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(key + ":" + secret)));
        var url = new Uri(ep.AbsoluteUri.TrimEnd('/') + "/statements");
        string? err = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(LrsTimeoutMs);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(statementsJson, Encoding.UTF8, "application/json") };
                req.Headers.Authorization = auth;
                req.Headers.Add("X-Experience-API-Version", XApiVersion);
                using var resp = await _http.SendAsync(req, d.Token).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(d.Token).ConfigureAwait(false);
                if ((int)resp.StatusCode is >= 400 and < 500 && resp.StatusCode != System.Net.HttpStatusCode.TooManyRequests) return new LrsSendResult(false, 0, attempt, "LRS 거부 " + ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture));
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) return new LrsSendResult(false, 0, attempt, "LRS 응답 스키마 불일치");
                    var n = 0;
                    foreach (var e in doc.RootElement.EnumerateArray()) { if (e.ValueKind != JsonValueKind.String || !Guid.TryParse(e.GetString(), out _)) return new LrsSendResult(false, 0, attempt, "LRS 응답 ID 형식 오류"); n++; }
                    LastAction = "📡 xAPI LRS 전송 " + n.ToString(CultureInfo.InvariantCulture) + "건";
                    Raise();
                    return new LrsSendResult(true, n, attempt, null);
                }
                err = "HTTP " + ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { err = "타임아웃"; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException) { err = ex.GetType().Name; }
            if (attempt < 3) await Task.Delay(200 * (1 << (attempt - 1)), ct).ConfigureAwait(false);
        }
        return new LrsSendResult(false, 0, 3, err);
    }

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Elearn.OnChange", ex); } }

    public void Dispose() => _http.Dispose();
}
