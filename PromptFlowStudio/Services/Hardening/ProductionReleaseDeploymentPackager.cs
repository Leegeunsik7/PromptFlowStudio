using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.Security;

namespace PromptFlowStudio.Services.Hardening;

public enum ReleaseTarget : byte { WinX64, WinArm64, OsxArm64, LinuxX64AppImage, Android, Ios }

public enum TrimVerdict : byte { Safe, Warn, Unsafe }

/// <summary>퍼블리시 계획(프로젝트 · 인자 목록 · 가능 여부 · 차단 사유 · 산출 형식).</summary>
public sealed record PublishPlan(ReleaseTarget Target, string Rid, string Project, IReadOnlyList<string> Arguments, bool Feasible, IReadOnlyList<string> Blockers, string Artifact, bool Trimmed, bool Aot);

/// <summary>트림/AOT 호환성 리포트(IL 스캔).</summary>
public sealed record TrimReport(int Methods, int CallSites, int UnreferencedCode, int DynamicCode, int AssemblyFiles, int Reflection, IReadOnlyDictionary<string, int> TopApis,
    IReadOnlyList<string> PlatformBlockers, TrimVerdict Trim, TrimVerdict Aot, double ElapsedMs);

/// <summary>번들 결과(zip · 파일 수 · 바이트 · 서명).</summary>
public sealed record BundleResult(string ZipPath, int Files, long Bytes, string ManifestSha256, string Signature);

/// <summary>번들 검증 결과.</summary>
public sealed record BundleVerification(bool Ok, string Reason, int Files, IReadOnlyList<string> Tampered);

/// <summary>
/// [PromptFlow Studio v16.51.0] 📦 프로덕션 릴리스 단일 바이너리 배포 패키저.
///   ▸ 타깃 6종(Windows x64/ARM64 단일 파일 · macOS · Linux AppImage · Android · iOS) dotnet publish 계획 — ArgumentList(셸 미경유) · 단일 파일/R2R/자체 포함 · 트림/AOT 는 IL 분석 결과로 게이팅.
///   ▸ 트림/AOT 호환성 시뮬레이션: 어셈블리 IL 워커(OpCodes 표) → call/callvirt/newobj 대상 해석 → RequiresUnreferencedCode/RequiresDynamicCode/RequiresAssemblyFiles · 리플렉션 API 집계 · 플랫폼 종속 참조(WinForms/WebView2) 차단 사유.
///   ▸ 서명 매니페스트 번들링: 파일별 SHA-256 · 집계 필드(파일 수 · 총 바이트) canonical → HMAC-SHA256(SecureSecretVault) · zip(manifest.json + manifest.sig) · 검증(누락/추가/변조 · FixedTimeEquals).
///   ▸ 실 퍼블리시: dotnet 프로세스(ArgumentList · 타임아웃 · 프로세스 트리 Kill) · 실패 시 현재 빌드 출력 번들 폴백.
/// </summary>
public sealed class ProductionReleaseDeploymentPackager
{
    public const string ManifestKeyName = "release_manifest_hmac";
    public const string ManifestName = "manifest.json", SignatureName = "manifest.sig";

    private static readonly OpCode[] OneByte = new OpCode[256], TwoByte = new OpCode[256];
    private readonly Func<byte[]> _key;
    private long _bundles, _verified, _tamper;

    static ProductionReleaseDeploymentPackager()
    {
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.GetValue(null) is not OpCode op) continue;
            var v = (ushort)op.Value;
            if (op.Size == 1) OneByte[v] = op; else TwoByte[v & 0xFF] = op;
        }
    }

    public ProductionReleaseDeploymentPackager(IServiceProvider sp)
        : this(sp.GetService(typeof(SecureSecretVault)) is SecureSecretVault v ? () => v.ResolveOrCreate(ManifestKeyName) : () => SHA256.HashData("pfs-release-manifest-dev"u8)) { }

    public ProductionReleaseDeploymentPackager(Func<byte[]> key) => _key = key;

    public event Action? OnChange;
    public event Action<string>? OnLog;

    public string Version { get; set; } = "16.51.0";
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public TrimReport? LastTrim { get; private set; }
    public BundleResult? LastBundle { get; private set; }
    public long Bundles => Interlocked.Read(ref _bundles);
    public long Verified => Interlocked.Read(ref _verified);
    public long TamperDetected => Interlocked.Read(ref _tamper);
    public bool Busy { get; private set; }
    public string? LastAction { get; private set; }

    // ══════════════════════ 퍼블리시 계획 ══════════════════════

    public static string Rid(ReleaseTarget t) => t switch
    {
        ReleaseTarget.WinX64 => "win-x64", ReleaseTarget.WinArm64 => "win-arm64", ReleaseTarget.OsxArm64 => "osx-arm64", ReleaseTarget.LinuxX64AppImage => "linux-x64",
        ReleaseTarget.Android => "android-arm64", _ => "ios-arm64",
    };

    public static string TargetLabel(ReleaseTarget t) => t switch
    {
        ReleaseTarget.WinX64 => "Windows x64 단일 EXE", ReleaseTarget.WinArm64 => "Windows ARM64 단일 EXE", ReleaseTarget.OsxArm64 => "macOS 단일 파일", ReleaseTarget.LinuxX64AppImage => "Linux AppImage",
        ReleaseTarget.Android => "Android AAB(MAUI)", _ => "iOS IPA(MAUI)",
    };

    /// <summary>타깃별 publish 계획. 데스크톱 헤드(net8.0-windows · WinForms/WebView2)는 macOS/Linux 빌드 불가 → 차단 사유 명시.</summary>
    public PublishPlan Plan(ReleaseTarget t, string repoRoot, TrimReport? trim = null, bool hostIsWindows = true, bool hostIsMac = false)
    {
        trim ??= LastTrim;
        var rid = Rid(t);
        var blockers = new List<string>();
        var args = new List<string> { "publish" };
        string project, artifact;
        var trimmed = false; var aot = false;
        if (t is ReleaseTarget.Android or ReleaseTarget.Ios)
        {
            project = Path.Combine(repoRoot, "PromptFlowStudio.Mobile", "PromptFlowStudio.Mobile.csproj");
            var tfm = t == ReleaseTarget.Android ? "net8.0-android" : "net8.0-ios";
            args.AddRange(new[] { project, "-c", "Release", "-f", tfm, "-r", rid });
            if (t == ReleaseTarget.Android) { args.Add("-p:AndroidPackageFormat=aab"); artifact = "PromptFlowStudio.aab"; }
            else
            {
                args.Add("-p:ArchiveOnBuild=true"); artifact = "PromptFlowStudio.ipa";
                if (!hostIsMac) blockers.Add("iOS 퍼블리시는 macOS 빌드 호스트(Xcode) 필요");
            }
            trimmed = true;                                                                                   // MAUI Release 기본 링커(SDK 관리)
        }
        else
        {
            project = Path.Combine(repoRoot, "PromptFlowStudio", "PromptFlowStudio.csproj");
            args.AddRange(new[] { project, "-c", "Release", "-r", rid, "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:PublishReadyToRun=true" });
            if (trim is { Trim: TrimVerdict.Safe }) { args.Add("-p:PublishTrimmed=true"); trimmed = true; }
            else args.Add("-p:PublishTrimmed=false");
            if (trim is { Aot: TrimVerdict.Safe }) { args.Add("-p:PublishAot=true"); aot = true; }
            artifact = t switch { ReleaseTarget.LinuxX64AppImage => "PromptFlowStudio-x86_64.AppImage", ReleaseTarget.OsxArm64 => "PromptFlowStudio.app", _ => "PromptFlowStudio.exe" };
            if (t is ReleaseTarget.OsxArm64 or ReleaseTarget.LinuxX64AppImage)
            {
                blockers.Add("데스크톱 헤드가 net8.0-windows(WinForms + WebView2) — " + (t == ReleaseTarget.OsxArm64 ? "macOS" : "Linux") + " 런타임 미지원(MAUI Mac Catalyst/Photino 헤드 이식 필요)");
                if (trim is not null) foreach (var b in trim.PlatformBlockers) blockers.Add(b);
            }
            else if (!hostIsWindows) blockers.Add("Windows 데스크톱 퍼블리시는 Windows 빌드 호스트 권장(WindowsDesktop 워크로드)");
        }
        args.Add("-o"); args.Add(Path.Combine(repoRoot, "publish", "release", rid));
        return new PublishPlan(t, rid, project, args, blockers.Count == 0, blockers, artifact, trimmed, aot);
    }

    // ══════════════════════ 트림/AOT 호환성 시뮬레이션 ══════════════════════

    private static readonly string[] ReflectionApis =
    {
        "System.Type.GetType", "System.Activator.CreateInstance", "System.Reflection.MethodBase.Invoke", "System.Reflection.Assembly.Load", "System.Reflection.Assembly.LoadFrom",
        "System.Type.InvokeMember", "System.Reflection.PropertyInfo.GetValue", "System.Reflection.PropertyInfo.SetValue", "System.Type.MakeGenericType", "System.Reflection.MethodInfo.MakeGenericMethod",
    };

    private static readonly string[] PlatformAssemblies = { "System.Windows.Forms", "Microsoft.Web.WebView2.Core", "Microsoft.Web.WebView2.WinForms", "Microsoft.AspNetCore.Components.WebView.WindowsForms", "PresentationFramework" };

    /// <summary>IL 워커로 호출 대상 스캔(타입 필터 선택). 무거운 작업 — 백그라운드 호출 권장.</summary>
    public TrimReport AnalyzeTrimCompatibility(Assembly asm, Func<Type, bool>? typeFilter = null)
    {
        var sw = Stopwatch.StartNew();
        int methods = 0, sites = 0, ruc = 0, rdc = 0, raf = 0, refl = 0;
        var top = new Dictionary<string, int>(StringComparer.Ordinal);
        var module = asm.ManifestModule;
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(x => x is not null).ToArray()!; }
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var t in types)
        {
            if (typeFilter is not null && !typeFilter(t)) continue;
            foreach (var m in t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
            {
                byte[]? il;
                try { il = m.GetMethodBody()?.GetILAsByteArray(); }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or BadImageFormatException) { il = null; }
                if (il is null) continue;
                methods++;
                var targs = t.IsGenericType ? t.GetGenericArguments() : null;
                var margs = m.IsGenericMethod ? m.GetGenericArguments() : null;
                for (var i = 0; i < il.Length;)
                {
                    OpCode op;
                    if (il[i] == 0xFE && i + 1 < il.Length) { op = TwoByte[il[i + 1]]; i += 2; }
                    else { op = OneByte[il[i]]; i += 1; }
                    var size = OperandSize(op.OperandType, il, i);
                    if (size < 0 || i + size > il.Length) break;
                    if (op.OperandType == OperandType.InlineMethod && (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj || op == OpCodes.Ldftn))
                    {
                        var token = BitConverter.ToInt32(il, i);
                        MethodBase? target = null;
                        try { target = module.ResolveMethod(token, targs, margs); }
                        catch (Exception ex) when (ex is ArgumentException or TypeLoadException or FileNotFoundException or FileLoadException or BadImageFormatException or MissingMethodException or NotSupportedException) { }
                        if (target is not null)
                        {
                            sites++;
                            var hit = false;
                            if (Has<RequiresUnreferencedCodeAttribute>(target)) { ruc++; hit = true; }
                            if (Has<RequiresDynamicCodeAttribute>(target)) { rdc++; hit = true; }
                            if (Has<RequiresAssemblyFilesAttribute>(target)) { raf++; hit = true; }
                            var full = target.DeclaringType?.FullName + "." + target.Name;
                            if (Array.IndexOf(ReflectionApis, full) >= 0) { refl++; hit = true; }
                            if (hit) top[full] = top.TryGetValue(full, out var c) ? c + 1 : 1;
                        }
                    }
                    i += size;
                }
            }
        }
        var platform = new List<string>();
        foreach (var r in asm.GetReferencedAssemblies())
            if (Array.IndexOf(PlatformAssemblies, r.Name) >= 0) platform.Add(r.Name + " 참조(Windows 전용 · 트림/AOT 미지원)");
        var trimV = platform.Count > 0 || ruc > 20 ? TrimVerdict.Unsafe : ruc > 0 || refl > 0 ? TrimVerdict.Warn : TrimVerdict.Safe;
        var aotV = platform.Count > 0 || rdc > 0 || trimV == TrimVerdict.Unsafe ? TrimVerdict.Unsafe : trimV;
        var topSorted = top.OrderByDescending(kv => kv.Value).Take(12).ToDictionary(kv => kv.Key, kv => kv.Value);
        var rep = new TrimReport(methods, sites, ruc, rdc, raf, refl, topSorted, platform, trimV, aotV, sw.Elapsed.TotalMilliseconds);
        LastTrim = rep;
        LastAction = "🔍 트림/AOT 분석 · 호출 " + sites.ToString(CultureInfo.InvariantCulture) + " · RUC " + ruc.ToString(CultureInfo.InvariantCulture) + " · RDC " + rdc.ToString(CultureInfo.InvariantCulture) + " → 트림 " + VerdictLabel(trimV) + " / AOT " + VerdictLabel(aotV);
        Raise();
        return rep;
    }

    private static bool Has<T>(MethodBase m) where T : Attribute
    {
        try { return m.IsDefined(typeof(T), false) || m.DeclaringType is { } d && d.IsDefined(typeof(T), false); }
        catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException or CustomAttributeFormatException) { return false; }
    }

    public static int OperandSize(OperandType t, byte[] il, int at) => t switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => at + 4 <= il.Length ? 4 + 4 * BitConverter.ToInt32(il, at) : -1,
        _ => 4,
    };

    public static string VerdictLabel(TrimVerdict v) => v switch { TrimVerdict.Safe => "안전", TrimVerdict.Warn => "경고", _ => "불가" };

    // ══════════════════════ 서명 매니페스트 번들 ══════════════════════

    public static string Canonical(string version, ReleaseTarget target, IReadOnlyList<(string Path, long Size, string Sha256)> files, long totalBytes)
    {
        var sb = new StringBuilder("pfs-release-manifest/v1|");
        sb.Append(version).Append('|').Append(Rid(target)).Append('|').Append(files.Count.ToString(CultureInfo.InvariantCulture)).Append('|').Append(totalBytes.ToString(CultureInfo.InvariantCulture));
        foreach (var (p, s, h) in files) sb.Append('\n').Append(p).Append('|').Append(s.ToString(CultureInfo.InvariantCulture)).Append('|').Append(h);
        return sb.ToString();
    }

    /// <summary>폴더 → 서명 매니페스트 zip. 경로는 '/' 정규화 · 정렬(결정적).</summary>
    public BundleResult Bundle(ReleaseTarget target, string sourceDir, string outZip)
    {
        if (!Directory.Exists(sourceDir)) throw new DirectoryNotFoundException(sourceDir);
        var root = Path.GetFullPath(sourceDir);
        var paths = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .Where(p => p != ManifestName && p != SignatureName && !p.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        var files = new List<(string, long, string)>(paths.Count);
        long total = 0;
        foreach (var rel in paths)
        {
            using var fs = File.OpenRead(Path.Combine(root, rel));
            var h = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            files.Add((rel, fs.Length, h)); total += fs.Length;
        }
        var canonical = Canonical(Version, target, files, total);
        var sig = Convert.ToHexString(HMACSHA256.HashData(_key(), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var manifest = BuildManifestJson(target, files, total);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outZip))!);
        if (File.Exists(outZip)) File.Delete(outZip);
        using (var zip = ZipFile.Open(outZip, ZipArchiveMode.Create))
        {
            foreach (var rel in paths) zip.CreateEntryFromFile(Path.Combine(root, rel), rel, CompressionLevel.Optimal);
            using (var w = new StreamWriter(zip.CreateEntry(ManifestName).Open(), new UTF8Encoding(false))) w.Write(manifest);
            using (var w = new StreamWriter(zip.CreateEntry(SignatureName).Open(), new UTF8Encoding(false))) w.Write(sig);
        }
        var res = new BundleResult(outZip, files.Count, total, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant(), sig);
        LastBundle = res;
        Interlocked.Increment(ref _bundles);
        LastAction = "📦 번들 · " + TargetLabel(target) + " · " + files.Count.ToString(CultureInfo.InvariantCulture) + "파일 · " + (total / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + "MB";
        Raise();
        return res;
    }

    private string BuildManifestJson(ReleaseTarget target, List<(string Path, long Size, string Sha256)> files, long total)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("product", "PromptFlow Studio");
            w.WriteString("version", Version);
            w.WriteString("target", target.ToString());
            w.WriteString("rid", Rid(target));
            w.WriteNumber("fileCount", files.Count);
            w.WriteNumber("totalBytes", total);
            if (LastTrim is { } tr) { w.WriteString("trim", tr.Trim.ToString()); w.WriteString("aot", tr.Aot.ToString()); }
            w.WriteStartArray("files");
            foreach (var (p, s, h) in files) { w.WriteStartObject(); w.WriteString("path", p); w.WriteNumber("size", s); w.WriteString("sha256", h); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>번들 검증: 매니페스트 스키마 · HMAC(FixedTimeEquals) · 엔트리 해시/크기 · 누락/추가 파일.</summary>
    public BundleVerification Verify(string zipPath)
    {
        var tampered = new List<string>();
        BundleVerification Done(bool ok, string why, int n)
        {
            if (ok) Interlocked.Increment(ref _verified); else Interlocked.Increment(ref _tamper);
            LastAction = ok ? "✅ 번들 검증 통과 · " + n.ToString(CultureInfo.InvariantCulture) + "파일" : "❌ 번들 검증 실패 · " + why;
            Raise();
            return new BundleVerification(ok, why, n, tampered);
        }
        using var zip = ZipFile.OpenRead(zipPath);
        var mEntry = zip.GetEntry(ManifestName); var sEntry = zip.GetEntry(SignatureName);
        if (mEntry is null || sEntry is null) return Done(false, "매니페스트/서명 누락", 0);
        string manifest, sig;
        using (var r = new StreamReader(mEntry.Open(), Encoding.UTF8)) manifest = r.ReadToEnd();
        using (var r = new StreamReader(sEntry.Open(), Encoding.UTF8)) sig = r.ReadToEnd().Trim();
        var files = new List<(string, long, string)>();
        ReleaseTarget target; string version; long total;
        try
        {
            using var doc = JsonDocument.Parse(manifest);
            var root = doc.RootElement;
            version = root.GetProperty("version").GetString() ?? "";
            if (!Enum.TryParse(root.GetProperty("target").GetString(), out target)) return Done(false, "타깃 스키마 오류", 0);
            total = root.GetProperty("totalBytes").GetInt64();
            foreach (var f in root.GetProperty("files").EnumerateArray())
                files.Add((f.GetProperty("path").GetString() ?? "", f.GetProperty("size").GetInt64(), f.GetProperty("sha256").GetString() ?? ""));
            if (root.GetProperty("fileCount").GetInt32() != files.Count) return Done(false, "집계 필드 불일치", files.Count);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return Done(false, "매니페스트 스키마 오류", 0); }
        byte[] got;
        try { got = Convert.FromHexString(sig); } catch (FormatException) { return Done(false, "서명 형식 오류", files.Count); }
        var expect = HMACSHA256.HashData(_key(), Encoding.UTF8.GetBytes(Canonical(version, target, files, total)));
        if (got.Length != expect.Length || !CryptographicOperations.FixedTimeEquals(got, expect)) return Done(false, "서명 불일치", files.Count);
        long sum = 0;
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (p, size, h) in files)
        {
            listed.Add(p); sum += size;
            var e = zip.GetEntry(p);
            if (e is null) { tampered.Add(p + " (누락)"); continue; }
            using var s = e.Open();
            var hh = SHA256.HashData(s);
            if (e.Length != size || !CryptographicOperations.FixedTimeEquals(hh, Convert.FromHexString(h))) tampered.Add(p);
        }
        foreach (var e in zip.Entries)
            if (e.FullName != ManifestName && e.FullName != SignatureName && !e.FullName.EndsWith('/') && !listed.Contains(e.FullName)) tampered.Add(e.FullName + " (미등재)");
        if (sum != total) return Done(false, "총 바이트 불일치", files.Count);
        return tampered.Count == 0 ? Done(true, "OK", files.Count) : Done(false, "파일 변조 " + tampered.Count.ToString(CultureInfo.InvariantCulture) + "건", files.Count);
    }

    // ══════════════════════ 실 퍼블리시 ══════════════════════

    /// <summary>dotnet publish 실행(ArgumentList · 타임아웃 → 프로세스 트리 Kill). 반환 = 종료 코드(-1 시작 실패 · -2 타임아웃).</summary>
    public async Task<int> RunPublishAsync(PublishPlan plan, CancellationToken ct = default)
    {
        if (!plan.Feasible) throw new InvalidOperationException("퍼블리시 불가: " + string.Join(" / ", plan.Blockers));
        if (!File.Exists(plan.Project)) throw new FileNotFoundException("프로젝트 파일 없음", plan.Project);
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in plan.Arguments) psi.ArgumentList.Add(a);
        Busy = true; Raise();
        try
        {
            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data is { } d) Log(d); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is { } d) Log("ERR " + d); };
            try { if (!p.Start()) return -1; }
            catch (System.ComponentModel.Win32Exception ex) { Log("dotnet 실행 실패: " + ex.Message); return -1; }
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(PublishTimeout);
            try { await p.WaitForExitAsync(d.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                Log("⏱ 퍼블리시 타임아웃/취소 → 프로세스 트리 종료");
                return -2;
            }
            LastAction = "🏗 publish " + plan.Rid + " 종료 코드 " + p.ExitCode.ToString(CultureInfo.InvariantCulture);
            return p.ExitCode;
        }
        finally { Busy = false; Raise(); }
    }

    /// <summary>레포 루트 탐색(PromptFlowStudio/PromptFlowStudio.csproj 보유 상위 폴더). 없으면 null.</summary>
    public static string? FindRepoRoot(string start)
    {
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PromptFlowStudio", "PromptFlowStudio.csproj"))) return d.FullName;
        return null;
    }

    private void Log(string s) { try { OnLog?.Invoke(s); } catch (Exception ex) { PromptFlowStudio.Program.Log("Release.OnLog", ex); } }

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Release.OnChange", ex); } }
}
