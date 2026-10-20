using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Ui;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Plugins.Sdk
{
    /// <summary>v16.29.0 플러그인 종류.</summary>
    public enum PluginKind : byte { VideoEngine, Filter, SubtitleTemplate }

    /// <summary>v16.29.0 호스트가 플러그인에 노출하는 최소 API(로그 · 데이터 폴더 · 취소).</summary>
    public interface IPluginHost
    {
        string HostVersion { get; }
        string DataFolder { get; }
        void Log(string message);
    }

    /// <summary>v16.29.0 모든 플러그인 공통 계약 — 기본 생성자 필수.</summary>
    public interface IPfsPlugin
    {
        string Id { get; }
        string Name { get; }
        string Version { get; }
        PluginKind Kind { get; }
        void Initialize(IPluginHost host);
    }

    /// <summary>v16.29.0 엔진 능력 선언(라우터 점수 입력).</summary>
    public sealed record EngineCapabilities(double Quality, double SecondsPerClipSecond, double UsdPerSecond, int MaxWidth, int MaxHeight, int MaxFps, double MaxDuration,
        bool TextToVideo, bool ImageToVideo, string[] Genres);

    /// <summary>v16.29.0 외부 AI 비디오 엔진 플러그인 — 결과 VideoUrl 은 https URL 또는 로컬 파일 경로.</summary>
    public interface IVideoEnginePlugin : IPfsPlugin
    {
        EngineCapabilities Capabilities { get; }
        bool Enabled { get; }
        Task<WanGenerationResult> GenerateAsync(WanVideoRequest request, CancellationToken ct);
    }

    /// <summary>v16.29.0 합성 필터 플러그인 — ffmpeg -vf 조각 반환(호스트가 허용 목록 검증).</summary>
    public interface IVideoFilterPlugin : IPfsPlugin
    {
        string BuildFilter(IReadOnlyDictionary<string, string> parameters);
    }

    /// <summary>v16.29.0 자막 템플릿 플러그인 — ASS [V4+ Styles] Style 라인.</summary>
    public interface ISubtitleTemplatePlugin : IPfsPlugin
    {
        string AssStyle(int width, int height);
    }
}

namespace PromptFlowStudio.Services.MultiEngine
{
    using PromptFlowStudio.Plugins.Sdk;

    public enum PluginStatus : byte { Discovered, Untrusted, Loaded, Disabled, Faulted }

    /// <summary>v16.29.0 plugin.json 매니페스트.</summary>
    public sealed record PluginManifest(string Id, string Name, string Version, PluginKind Kind, string? Assembly, string? Type, string? Author, string? Description,
        string? FilterTemplate, string? AssStyle, IReadOnlyList<string> FilterParams);

    /// <summary>v16.29.0 설치 플러그인 상태(UI 목록).</summary>
    public sealed class InstalledPlugin
    {
        public required PluginManifest Manifest { get; init; }
        public required string Folder { get; init; }
        public string? Sha256 { get; set; }
        public PluginStatus Status { get; set; }
        public bool Enabled { get; set; } = true;
        public int Faults { get; set; }
        public int Reloads { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset LoadedAt { get; set; }
        public bool IsDeclarative => Manifest.Assembly is null;
        internal IPfsPlugin? Instance;
        internal PluginLoadContext? Context;
    }

    /// <summary>플러그인별 수집 가능 로드 컨텍스트 — SDK/호스트 어셈블리는 기본 컨텍스트 공유(타입 동일성), 나머지는 플러그인 폴더에서.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("ios")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("maccatalyst")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("tvos")]
    internal sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _dir;

        public PluginLoadContext(string mainPath) : base("pfs-plugin:" + Path.GetFileName(Path.GetDirectoryName(mainPath)), isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(mainPath);
            _dir = Path.GetDirectoryName(mainPath)!;
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is null || name.Name == "PromptFlowStudio" || name.Name.StartsWith("System", StringComparison.Ordinal) || name.Name.StartsWith("Microsoft", StringComparison.Ordinal)) return null;
            var p = _resolver.ResolveAssemblyToPath(name);
            if (p is null)
            {
                var local = Path.Combine(_dir, name.Name + ".dll");
                if (File.Exists(local)) p = local;
            }
            return p is null ? null : LoadFromStream(new MemoryStream(File.ReadAllBytes(p)));   // 파일 잠금 없이(핫 리로드 교체 가능)
        }
    }

    /// <summary>
    /// [PromptFlowStudio] v16.29.0 🧩 서드파티 커스텀 플러그인 확장 런타임 — Open SDK(<see cref="IPfsPlugin"/> · 엔진/필터/자막) · 동적 로드 · 핫 리로드 · 격리 실행.
    ///   ▸ 설치 구조: <c>%LocalAppData%\PromptFlowStudio\Plugins\{id}\plugin.json</c> (+ DLL). 매니페스트 스키마 즉시 검증(id 형식 · 종류 · 버전).
    ///   ▸ DLL 플러그인: 플러그인별 collectible <see cref="AssemblyLoadContext"/> + 메모리 로드(파일 잠금 0) · SDK/호스트 어셈블리 공유 · 기본 생성자 인스턴스화.
    ///     <b>신뢰 게이트</b>: DLL SHA-256 을 사용자가 승인(<see cref="Trust"/>)한 경우에만 로드 — 해시가 바뀌면(업데이트) 재승인 필요. 신뢰 목록은 DPAPI 봉인 시크릿 HMAC 으로 무결성 보호.
    ///     ⚠ ALC 는 보안 샌드박스가 아님(동일 프로세스 권한) — 신뢰된 개발자 플러그인만 승인할 것.
    ///   ▸ 선언형(스크립트) 플러그인: DLL 없이 plugin.json 의 필터 템플릿(<c>{param}</c> 치환 · 허용 필터 목록 · 값 정화)/ASS 스타일 — 코드 실행 0.
    ///     (C# 스크립트 컴파일은 Roslyn NuGet 필요 → Pure BCL 원칙상 미지원, 선언형으로 대체)
    ///   ▸ 핫 리로드: FileSystemWatcher → <see cref="SafeDebouncer"/> 400ms → 언로드(인스턴스 해제 · ALC Unload) → 재로드.
    ///   ▸ 격리 실행(<see cref="Invoke{T}"/>): 타임아웃 · 예외 포착 · 연속 3회 장애 시 자동 비활성(Faulted) · 필터 출력 허용 목록 검증.
    /// </summary>
    public sealed class PluginExtensionMarketplaceFramework : IPluginHost, IDisposable
    {
        public const int MaxPlugins = 64;
        public const int FaultLimit = 3;
        public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

        /// <summary>필터 플러그인 출력에 허용되는 ffmpeg 필터 이름.</summary>
        public static readonly HashSet<string> AllowedFilters = new(StringComparer.Ordinal)
        {
            "eq", "hue", "curves", "colorbalance", "colorchannelmixer", "vignette", "unsharp", "gblur", "boxblur", "noise", "drawbox",
            "scale", "crop", "pad", "fade", "setsar", "format", "hflip", "vflip", "transpose", "rotate", "chromashift", "negate", "edgedetect", "deflicker", "tmix",
        };

        private readonly string _root;
        private readonly Func<byte[]> _trustKey;
        private readonly object _gate = new();
        private readonly Dictionary<string, InstalledPlugin> _plugins = new(StringComparer.Ordinal);
        private readonly HashSet<string> _trusted = new(StringComparer.OrdinalIgnoreCase);
        private readonly SafeDebouncer _debounce;
        private FileSystemWatcher? _watcher;
        private int _loads, _unloads;
        private bool _disposed;

        public PluginExtensionMarketplaceFramework(PromptFlowStudio.Services.Security.SecureSecretVault vault)
            : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "Plugins"), () => vault.ResolveOrCreate("PLUGIN_TRUST")) { }

        public PluginExtensionMarketplaceFramework(string root, Func<byte[]> trustKey)
        {
            _root = root;
            _trustKey = trustKey;
            _debounce = new SafeDebouncer(ex => Program.Log("Plugins.Debounce", ex));
            LoadTrust();
        }

        public event Action? OnChange;

        public string HostVersion => "16.29.0";
        public string DataFolder => _root;
        public string Root => _root;
        public bool HotReloadEnabled => ProcessCompat.IsSupported && Volatile.Read(ref _watcher) is not null;
        public int LoadCount => Volatile.Read(ref _loads);
        public int UnloadCount => Volatile.Read(ref _unloads);
        public string? LastAction { get; private set; }
        public IReadOnlyList<InstalledPlugin> Plugins { get { lock (_gate) { var l = new List<InstalledPlugin>(_plugins.Values); l.Sort(static (a, b) => string.CompareOrdinal(a.Manifest.Id, b.Manifest.Id)); return l; } } }

        void IPluginHost.Log(string message) => Program.Log("Plugin", new InvalidOperationException(message.Length > 500 ? message[..500] : message));

        // ══════════════════════ 매니페스트 ══════════════════════

        public static bool IsSafeId(string? id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 48) return false;
            foreach (var ch in id) if (!(char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch is '-' or '_' or '.')) return false;
            return true;
        }

        /// <summary>plugin.json 파싱 + 스키마 검증(실패 null · 사유 out).</summary>
        public static PluginManifest? ParseManifest(string? json, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(json) || json.Length > 64 * 1024) { error = "매니페스트가 비었거나 너무 큽니다."; return null; }
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                string? S(string n) => r.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                var id = S("id"); var name = S("name"); var ver = S("version"); var kind = S("kind");
                if (!IsSafeId(id)) { error = "id 형식 오류(소문자/숫자/-_. · 48자)"; return null; }
                if (string.IsNullOrWhiteSpace(name) || name.Length > 64) { error = "name 누락"; return null; }
                if (ver is null || !Version.TryParse(ver, out _)) { error = "version 형식 오류(예: 1.0.0)"; return null; }
                PluginKind k;
                switch (kind) { case "engine": k = PluginKind.VideoEngine; break; case "filter": k = PluginKind.Filter; break; case "subtitle": k = PluginKind.SubtitleTemplate; break; default: error = "kind 는 engine|filter|subtitle"; return null; }
                var asm = S("assembly"); var type = S("type");
                if (asm is not null && (asm.Contains('/') || asm.Contains('\\') || asm.Contains("..", StringComparison.Ordinal) || !asm.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) { error = "assembly 는 폴더 내 .dll 파일명만"; return null; }
                if (asm is not null && string.IsNullOrWhiteSpace(type)) { error = "DLL 플러그인은 type(전체 형식 이름) 필수"; return null; }
                var tpl = S("filterTemplate"); var ass = S("assStyle");
                if (asm is null && k == PluginKind.VideoEngine) { error = "엔진 플러그인은 DLL 필수"; return null; }
                if (asm is null && k == PluginKind.Filter && string.IsNullOrWhiteSpace(tpl)) { error = "선언형 필터는 filterTemplate 필수"; return null; }
                if (asm is null && k == PluginKind.SubtitleTemplate && string.IsNullOrWhiteSpace(ass)) { error = "선언형 자막은 assStyle 필수"; return null; }
                var ps = new List<string>();
                if (r.TryGetProperty("params", out var pe) && pe.ValueKind == JsonValueKind.Array)
                    foreach (var p in pe.EnumerateArray()) if (p.ValueKind == JsonValueKind.String && IsSafeId(p.GetString())) ps.Add(p.GetString()!);
                return new PluginManifest(id!, name!, ver, k, asm, type, S("author"), S("description"), tpl, ass, ps);
            }
            catch (JsonException ex) { error = "JSON 오류: " + ex.Message; return null; }
        }

        /// <summary>
        /// ffmpeg 필터 체인 검증 — 쉼표 구분 각 필터 이름이 허용 목록에 있고, 따옴표/세미콜론/대괄호(그래프 라벨)/파이프 금지.
        /// </summary>
        public static bool IsSafeFilter(string? chain)
        {
            if (string.IsNullOrWhiteSpace(chain) || chain.Length > 1024) return false;
            foreach (var ch in chain) if (ch is ';' or '[' or ']' or '\'' or '"' or '`' or '|' || char.IsControl(ch)) return false;
            foreach (var part in chain.Split(','))
            {
                var p = part.Trim();
                var eq = p.IndexOf('=');
                var name = (eq < 0 ? p : p[..eq]).Trim();
                if (!AllowedFilters.Contains(name)) return false;   // 파일 경로를 받는 필터(lut3d·movie·subtitles 등)는 목록에서 제외
            }
            return true;
        }

        /// <summary>선언형 템플릿 치환 — 값은 숫자/영문/._- 만 허용(그 외 거부 → null).</summary>
        public static string? FillTemplate(string template, IReadOnlyList<string> allowed, IReadOnlyDictionary<string, string> values)
        {
            var sb = new StringBuilder(template.Length + 32);
            for (var i = 0; i < template.Length; i++)
            {
                if (template[i] != '{') { sb.Append(template[i]); continue; }
                var end = template.IndexOf('}', i);
                if (end < 0) return null;
                var key = template[(i + 1)..end];
                var ok = false;
                foreach (var a in allowed) if (a == key) { ok = true; break; }
                if (!ok || !values.TryGetValue(key, out var v) || v.Length is 0 or > 32) return null;
                foreach (var ch in v) if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_')) return null;
                sb.Append(v);
                i = end;
            }
            return sb.ToString();
        }

        // ══════════════════════ 신뢰 ══════════════════════

        private string TrustPath => Path.Combine(_root, "trusted.json");

        public bool IsTrusted(string? sha256) => sha256 is not null && _trusted.Contains(sha256);

        /// <summary>DLL 해시 승인(사용자 동의) → 재로드.</summary>
        public void Trust(string pluginId)
        {
            InstalledPlugin? p;
            lock (_gate) _plugins.TryGetValue(pluginId, out p);
            if (p?.Sha256 is null) return;
            lock (_gate) _trusted.Add(p.Sha256);
            SaveTrust();
            LastAction = "🔐 신뢰 승인 · " + p.Manifest.Name;
            Reload(pluginId);
        }

        public void Untrust(string pluginId)
        {
            InstalledPlugin? p;
            lock (_gate) _plugins.TryGetValue(pluginId, out p);
            if (p?.Sha256 is null) return;
            lock (_gate) _trusted.Remove(p.Sha256);
            SaveTrust();
            Reload(pluginId);
        }

        private void SaveTrust()
        {
            try
            {
                Directory.CreateDirectory(_root);
                string[] list;
                lock (_gate) { list = new string[_trusted.Count]; _trusted.CopyTo(list); }
                Array.Sort(list, StringComparer.Ordinal);
                var body = string.Join("\n", list);
                var mac = Convert.ToHexString(HMACSHA256.HashData(_trustKey(), Encoding.UTF8.GetBytes(body)));
                File.WriteAllText(TrustPath, mac + "\n" + body);
            }
            catch (Exception ex) { Program.Log("Plugins.Trust", ex); }
        }

        private void LoadTrust()
        {
            try
            {
                if (!File.Exists(TrustPath)) return;
                var text = File.ReadAllText(TrustPath);
                var nl = text.IndexOf('\n');
                var mac = nl < 0 ? text : text[..nl];
                var body = nl < 0 ? "" : text[(nl + 1)..];
                var expect = HMACSHA256.HashData(_trustKey(), Encoding.UTF8.GetBytes(body));
                byte[] got;
                try { got = Convert.FromHexString(mac.Trim()); } catch (FormatException) { return; }
                if (!CryptographicOperations.FixedTimeEquals(expect, got)) { Program.Log("Plugins.Trust", new CryptographicException("신뢰 목록 무결성 실패 — 무시")); return; }
                foreach (var l in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) if (l.Length == 64) _trusted.Add(l);
            }
            catch (Exception ex) { Program.Log("Plugins.Trust", ex); }
        }

        // ══════════════════════ 검색 / 로드 / 언로드 ══════════════════════

        /// <summary>플러그인 폴더 전체 검색 → (재)로드. 반환: 로드 성공 수.</summary>
        public int ScanAndLoad()
        {
            if (_disposed) return 0;
            try { Directory.CreateDirectory(_root); } catch (Exception ex) { Program.Log("Plugins.Root", ex); return 0; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ok = 0;
            foreach (var dir in Directory.EnumerateDirectories(_root))
            {
                if (seen.Count >= MaxPlugins) break;
                var mf = Path.Combine(dir, "plugin.json");
                if (!File.Exists(mf)) continue;
                var m = ParseManifest(SafeRead(mf), out var err);
                if (m is null) { Program.Log("Plugins.Manifest", new InvalidDataException(Path.GetFileName(dir) + ": " + err)); continue; }
                if (!seen.Add(m.Id)) continue;
                if (LoadOne(m, dir)) ok++;
            }
            List<string> gone = new();
            lock (_gate) foreach (var id in _plugins.Keys) if (!seen.Contains(id)) gone.Add(id);
            foreach (var id in gone) { Unload(id); lock (_gate) _plugins.Remove(id); }
            LastAction = "🧩 플러그인 " + ok.ToString(CultureInfo.InvariantCulture) + "개 로드";
            Raise();
            return ok;
        }

        private static string? SafeRead(string path)
        {
            try { return File.ReadAllText(path); } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
        }

        private bool LoadOne(PluginManifest m, string dir)
        {
            InstalledPlugin p;
            lock (_gate)
            {
                if (_plugins.TryGetValue(m.Id, out var old)) { UnloadLocked(old); p = new InstalledPlugin { Manifest = m, Folder = dir, Enabled = old.Enabled, Reloads = old.Reloads + 1 }; }
                else p = new InstalledPlugin { Manifest = m, Folder = dir };
                _plugins[m.Id] = p;
            }
            if (p.IsDeclarative)
            {
                p.Status = p.Enabled ? PluginStatus.Loaded : PluginStatus.Disabled;
                p.LoadedAt = DateTimeOffset.Now;
                Interlocked.Increment(ref _loads);
                return true;
            }
            var dll = Path.Combine(dir, m.Assembly!);
            try
            {
                if (!File.Exists(dll)) { p.Status = PluginStatus.Faulted; p.LastError = "DLL 없음"; return false; }
                var bytes = File.ReadAllBytes(dll);
                p.Sha256 = Convert.ToHexString(SHA256.HashData(bytes));
                if (!IsTrusted(p.Sha256)) { p.Status = PluginStatus.Untrusted; p.LastError = "신뢰 승인 필요(SHA-256 " + p.Sha256[..12] + "…)"; return false; }
                if (!p.Enabled) { p.Status = PluginStatus.Disabled; return false; }
                if (!ProcessCompat.IsSupported) { p.Status = PluginStatus.Faulted; p.LastError = "동적 로드 미지원 플랫폼"; return false; }
                var ctx = new PluginLoadContext(dll);
                var asm = ctx.LoadFromStream(new MemoryStream(bytes));
                var type = asm.GetType(m.Type!, throwOnError: false);
                if (type is null || !typeof(IPfsPlugin).IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
                {
                    ctx.Unload();
                    p.Status = PluginStatus.Faulted; p.LastError = "type 이 IPfsPlugin 구현(기본 생성자)이 아닙니다.";
                    return false;
                }
                var inst = (IPfsPlugin)Activator.CreateInstance(type)!;
                if (inst.Id != m.Id || inst.Kind != m.Kind) { ctx.Unload(); p.Status = PluginStatus.Faulted; p.LastError = "매니페스트와 id/kind 불일치"; return false; }
                if (!RunGuarded(p, () => { inst.Initialize(this); return true; }, out _)) { ctx.Unload(); return false; }
                lock (_gate) { p.Instance = inst; p.Context = ctx; }
                p.Status = PluginStatus.Loaded;
                p.LoadedAt = DateTimeOffset.Now;
                Interlocked.Increment(ref _loads);
                return true;
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException or TypeLoadException or MissingMethodException or TargetInvocationException)
            {
                p.Status = PluginStatus.Faulted; p.LastError = ex.GetType().Name + ": " + ex.Message;
                Program.Log("Plugins.Load", ex);
                return false;
            }
        }

        /// <summary>단일 플러그인 재로드(매니페스트 재해석 포함).</summary>
        public bool Reload(string id)
        {
            InstalledPlugin? p;
            lock (_gate) _plugins.TryGetValue(id, out p);
            if (p is null) return false;
            var m = ParseManifest(SafeRead(Path.Combine(p.Folder, "plugin.json")), out var err);
            if (m is null || m.Id != id) { p.Status = PluginStatus.Faulted; p.LastError = err ?? "id 변경 불가"; Raise(); return false; }
            var ok = LoadOne(m, p.Folder);
            LastAction = (ok ? "♻ 리로드 · " : "⚠ 리로드 실패 · ") + m.Name;
            Raise();
            return ok;
        }

        public void SetEnabled(string id, bool enabled)
        {
            InstalledPlugin? p;
            lock (_gate) _plugins.TryGetValue(id, out p);
            if (p is null) return;
            p.Enabled = enabled;
            if (!enabled) { Unload(id); p.Status = PluginStatus.Disabled; Raise(); }
            else { p.Faults = 0; Reload(id); }
        }

        private void Unload(string id)
        {
            lock (_gate) if (_plugins.TryGetValue(id, out var p)) UnloadLocked(p);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void UnloadLocked(InstalledPlugin p)
        {
            if (p.Instance is IDisposable d) { try { d.Dispose(); } catch (Exception ex) { Program.Log("Plugins.Dispose", ex); } }
            p.Instance = null;
            if (p.Context is not null)
            {
                try { p.Context.Unload(); } catch (Exception ex) { Program.Log("Plugins.Unload", ex); }
                p.Context = null;
                Interlocked.Increment(ref _unloads);
            }
        }

        // ══════════════════════ 핫 리로드 ══════════════════════

        public bool EnableHotReload()
        {
            if (!ProcessCompat.IsSupported) return false;   // v17.63.2: iOS 계열 FileSystemWatcher 미지원
            if (_disposed || _watcher is not null) return _watcher is not null;
            try
            {
                Directory.CreateDirectory(_root);
                var w = new FileSystemWatcher(_root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName };
                w.Filters.Add("*.dll"); w.Filters.Add("plugin.json");
                w.Changed += OnFs; w.Created += OnFs; w.Deleted += OnFs; w.Renamed += OnFs;
                w.EnableRaisingEvents = true;
                _watcher = w;
                LastAction = "👀 핫 리로드 감시 시작";
                Raise();
                return true;
            }
            catch (Exception ex) { Program.Log("Plugins.Watch", ex); return false; }
        }

        public void DisableHotReload()
        {
            if (!ProcessCompat.IsSupported) return;
            var w = Interlocked.Exchange(ref _watcher, null);
            if (w is null) return;
            w.EnableRaisingEvents = false;
            w.Dispose();
            Raise();
        }

        private void OnFs(object sender, FileSystemEventArgs e)
        {
            try { _ = _debounce.Schedule(TimeSpan.FromMilliseconds(400), _ => { ScanAndLoad(); return Task.CompletedTask; }); }
            catch (Exception ex) { Program.Log("Plugins.Fs", ex); }
        }

        // ══════════════════════ 격리 실행 ══════════════════════

        /// <summary>격리 호출 — 예외 포착 · 연속 장애 시 자동 비활성(Faulted). 성공 시 장애 카운터 리셋.</summary>
        private bool RunGuarded<T>(InstalledPlugin p, Func<T> call, out T? result)
        {
            result = default;
            try
            {
                var t = Task.Run(call);
                if (!t.Wait(CallTimeout)) throw new TimeoutException("플러그인 호출 시간 초과(" + CallTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + "초)");
                result = t.Result;
                p.Faults = 0;
                return true;
            }
            catch (Exception ex)
            {
                var inner = ex is AggregateException ae && ae.InnerException is not null ? ae.InnerException : ex;
                p.Faults++;
                p.LastError = inner.GetType().Name + ": " + inner.Message;
                if (p.Faults >= FaultLimit) { p.Status = PluginStatus.Faulted; Unload(p.Manifest.Id); }
                Program.Log("Plugins.Call", inner);
                Raise();
                return false;
            }
        }

        /// <summary>플러그인 호출(동기 · 격리).</summary>
        public T? Invoke<T>(string id, Func<IPfsPlugin, T> call)
        {
            InstalledPlugin? p;
            lock (_gate) _plugins.TryGetValue(id, out p);
            if (p?.Instance is null || p.Status != PluginStatus.Loaded) return default;
            var inst = p.Instance;
            return RunGuarded(p, () => call(inst), out var r) ? r : default;
        }

        /// <summary>필터 생성 — 선언형 템플릿 또는 DLL BuildFilter → 허용 목록 검증(실패 null).</summary>
        public string? BuildFilter(string id, IReadOnlyDictionary<string, string> parameters)
        {
            InstalledPlugin? p;
            lock (_gate) _plugins.TryGetValue(id, out p);
            if (p is null || p.Status != PluginStatus.Loaded || p.Manifest.Kind != PluginKind.Filter) return null;
            var chain = p.IsDeclarative ? FillTemplate(p.Manifest.FilterTemplate!, p.Manifest.FilterParams, parameters) : Invoke(id, x => ((IVideoFilterPlugin)x).BuildFilter(parameters));
            if (!IsSafeFilter(chain)) { p.LastError = "필터 출력이 허용 목록 검증에 실패했습니다."; Raise(); return null; }
            return chain;
        }

        /// <summary>자막 스타일 — 선언형 assStyle(Style: 접두 검증) 또는 DLL.</summary>
        public string? AssStyle(string id, int width, int height)
        {
            InstalledPlugin? p;
            lock (_gate) _plugins.TryGetValue(id, out p);
            if (p is null || p.Status != PluginStatus.Loaded || p.Manifest.Kind != PluginKind.SubtitleTemplate) return null;
            var s = p.IsDeclarative ? p.Manifest.AssStyle : Invoke(id, x => ((ISubtitleTemplatePlugin)x).AssStyle(width, height));
            if (s is null || !s.StartsWith("Style:", StringComparison.Ordinal) || s.Length > 512 || s.IndexOfAny(new[] { '\n', '\r', '{', '}' }) >= 0) return null;
            return s;
        }

        /// <summary>로드된 엔진 플러그인 목록(라우터 등록용).</summary>
        public List<IVideoEnginePlugin> EnginePlugins()
        {
            var list = new List<IVideoEnginePlugin>();
            lock (_gate) foreach (var p in _plugins.Values) if (p.Status == PluginStatus.Loaded && p.Instance is IVideoEnginePlugin e) list.Add(e);
            return list;
        }

        /// <summary>엔진 플러그인 비동기 호출(격리 · 타임아웃은 호출측 CT) — 장애 카운터 반영.</summary>
        public async Task<WanGenerationResult> GenerateWithPluginAsync(IVideoEnginePlugin engine, WanVideoRequest req, CancellationToken ct)
        {
            InstalledPlugin? p;
            lock (_gate) _plugins.TryGetValue(engine.Id, out p);
            try
            {
                var r = await engine.GenerateAsync(req, ct).ConfigureAwait(false);
                if (p is not null) p.Faults = r.Ok ? 0 : p.Faults;
                return r;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (p is not null)
                {
                    p.Faults++; p.LastError = ex.Message;
                    if (p.Faults >= FaultLimit) { p.Status = PluginStatus.Faulted; Unload(p.Manifest.Id); }
                }
                Program.Log("Plugins.Engine", ex);
                Raise();
                return new WanGenerationResult(false, WanTaskStatus.Failed, null, "플러그인 예외: " + ex.Message, null, TimeSpan.Zero);
            }
        }

        private void Raise()
        {
            var h = OnChange;
            if (h is null) return;
            foreach (var d in h.GetInvocationList())
            {
                try { ((Action)d)(); } catch (Exception ex) { Program.Log("Plugins.OnChange", ex); }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DisableHotReload();
            _debounce.Dispose();
            lock (_gate) foreach (var p in _plugins.Values) UnloadLocked(p);
            OnChange = null;
        }
    }
}
