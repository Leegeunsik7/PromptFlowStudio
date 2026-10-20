using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Deploy;

/// <summary>
/// [PromptFlow Studio v8.2.0] B2B 배포 사양(불변) — 앱/이미지/레플리카/포트/자원/오토스케일 범위.
/// </summary>
public sealed record DeploymentSpec(
    string AppName, string Image, int Replicas, int ContainerPort, int NodePort,
    string Namespace = "promptflow", int CpuMilli = 500, int MemoryMi = 512,
    int MinReplicas = 2, int MaxReplicas = 8, int TargetCpuPercent = 70);

/// <summary>
/// [PromptFlow Studio v8.2.0] 엔터프라이즈 K8s/Docker 자동 패키저(관찰 가능 · 순수 BCL 문자열 조립 · 외부 템플릿 엔진 0).
///   ▸ B2B 온프렘/클라우드 배포용 <c>Dockerfile · namespace · Deployment · Service(NodePort) · HPA</c> 매니페스트를 코드로 생성한다.
///   ▸ HPA(HorizontalPodAutoscaler)로 <see cref="PromptFlowStudio.Services.Wan.CloudEdgeAutoScaler"/> 의 앱-레벨 오토스케일을 클러스터-레벨과 정합.
///   ▸ 이름은 RFC1123 DNS 라벨로 정규화(대문자·언더스코어 제거), 수치는 <see cref="CultureInfo.InvariantCulture"/> — 로캘 독립.
///   ▸ Graceful: 파일 쓰기 실패는 삼키고 <see cref="LastError"/> 로 보고(앱 무중단).
/// </summary>
public sealed class DeploymentPackager
{
    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    public int LastBundleFileCount { get; private set; }
    public DateTimeOffset? LastGeneratedAt { get; private set; }
    public string? LastOutputDir { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>전체 배포 번들 생성 — 파일명 → 내용 사전(파일 미기록 · 미리보기/검증용).</summary>
    public IReadOnlyDictionary<string, string> GenerateBundle(DeploymentSpec spec)
    {
        var s = Normalize(spec);
        var bundle = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Dockerfile"] = GenerateDockerfile(s),
            [".dockerignore"] = GenerateDockerIgnore(),
            ["k8s/namespace.yaml"] = GenerateNamespaceYaml(s),
            ["k8s/deployment.yaml"] = GenerateDeploymentYaml(s),
            ["k8s/service.yaml"] = GenerateServiceYaml(s),
            ["k8s/hpa.yaml"] = GenerateHpaYaml(s),
            ["README.md"] = GenerateReadme(s),
        };
        LastBundleFileCount = bundle.Count;
        LastGeneratedAt = DateTimeOffset.Now;
        Notify();
        return bundle;
    }

    /// <summary>번들을 디스크에 기록(하위 디렉터리 포함). 성공 시 출력 경로 반환 · 실패 시 null(Graceful).</summary>
    public async Task<string?> WriteBundleAsync(DeploymentSpec spec, string outDir)
    {
        try
        {
            var bundle = GenerateBundle(spec);
            var root = Path.GetFullPath(outDir);
            foreach (var (rel, content) in bundle)
            {
                var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, content, new UTF8Encoding(false)).ConfigureAwait(false);
            }
            LastOutputDir = root; LastError = null;
            Notify();
            return root;
        }
        catch (Exception ex)
        {
            Program.Log("DeploymentPackager.Write", ex);
            LastError = ex.Message;
            Notify();
            return null;
        }
    }

    // ── 생성기(순수 함수) ─────────────────────────────────────────────────────

    public string GenerateDockerfile(DeploymentSpec spec)
    {
        var s = Normalize(spec);
        var sb = new StringBuilder();
        sb.AppendLine("# syntax=docker/dockerfile:1");
        sb.AppendLine("# [PromptFlow Studio v8.2.0] 멀티 스테이지 빌드 — .NET 8 SDK 빌드 → ASP.NET 런타임");
        sb.AppendLine("FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build");
        sb.AppendLine("WORKDIR /src");
        sb.AppendLine("COPY . .");
        sb.AppendLine("RUN dotnet restore");
        sb.AppendLine("RUN dotnet publish -c Release -o /app --no-restore");
        sb.AppendLine();
        sb.AppendLine("FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime");
        sb.AppendLine("WORKDIR /app");
        sb.AppendLine("COPY --from=build /app .");
        sb.Append("EXPOSE ").Append(Int(s.ContainerPort)).AppendLine();
        sb.Append("ENV ASPNETCORE_URLS=http://+:").Append(Int(s.ContainerPort)).AppendLine();
        sb.Append("ENTRYPOINT [\"dotnet\", \"").Append(s.AppName).AppendLine(".dll\"]");
        return sb.ToString();
    }

    public string GenerateNamespaceYaml(DeploymentSpec spec)
    {
        var s = Normalize(spec);
        return $"apiVersion: v1\nkind: Namespace\nmetadata:\n  name: {s.Namespace}\n  labels:\n    app.kubernetes.io/part-of: promptflow-studio\n";
    }

    public string GenerateDeploymentYaml(DeploymentSpec spec)
    {
        var s = Normalize(spec);
        var sb = new StringBuilder();
        sb.AppendLine("apiVersion: apps/v1");
        sb.AppendLine("kind: Deployment");
        sb.AppendLine("metadata:");
        sb.Append("  name: ").AppendLine(s.AppName);
        sb.Append("  namespace: ").AppendLine(s.Namespace);
        sb.AppendLine("  labels:");
        sb.Append("    app: ").AppendLine(s.AppName);
        sb.AppendLine("spec:");
        sb.Append("  replicas: ").AppendLine(Int(s.Replicas));
        sb.AppendLine("  selector:");
        sb.AppendLine("    matchLabels:");
        sb.Append("      app: ").AppendLine(s.AppName);
        sb.AppendLine("  template:");
        sb.AppendLine("    metadata:");
        sb.AppendLine("      labels:");
        sb.Append("        app: ").AppendLine(s.AppName);
        sb.AppendLine("    spec:");
        sb.AppendLine("      containers:");
        sb.Append("        - name: ").AppendLine(s.AppName);
        sb.Append("          image: ").AppendLine(s.Image);
        sb.AppendLine("          ports:");
        sb.Append("            - containerPort: ").AppendLine(Int(s.ContainerPort));
        sb.AppendLine("          resources:");
        sb.AppendLine("            requests:");
        sb.Append("              cpu: \"").Append(Int(s.CpuMilli)).AppendLine("m\"");
        sb.Append("              memory: \"").Append(Int(s.MemoryMi)).AppendLine("Mi\"");
        sb.AppendLine("            limits:");
        sb.Append("              cpu: \"").Append(Int(s.CpuMilli * 2)).AppendLine("m\"");
        sb.Append("              memory: \"").Append(Int(s.MemoryMi * 2)).AppendLine("Mi\"");
        sb.AppendLine("          readinessProbe:");
        sb.AppendLine("            httpGet:");
        sb.AppendLine("              path: /health");
        sb.Append("              port: ").AppendLine(Int(s.ContainerPort));
        sb.AppendLine("            initialDelaySeconds: 5");
        sb.AppendLine("            periodSeconds: 10");
        return sb.ToString();
    }

    public string GenerateServiceYaml(DeploymentSpec spec)
    {
        var s = Normalize(spec);
        var sb = new StringBuilder();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Service");
        sb.AppendLine("metadata:");
        sb.Append("  name: ").Append(s.AppName).AppendLine("-svc");
        sb.Append("  namespace: ").AppendLine(s.Namespace);
        sb.AppendLine("spec:");
        sb.AppendLine("  type: NodePort");
        sb.AppendLine("  selector:");
        sb.Append("    app: ").AppendLine(s.AppName);
        sb.AppendLine("  ports:");
        sb.AppendLine("    - protocol: TCP");
        sb.Append("      port: ").AppendLine(Int(s.ContainerPort));
        sb.Append("      targetPort: ").AppendLine(Int(s.ContainerPort));
        sb.Append("      nodePort: ").AppendLine(Int(s.NodePort));
        return sb.ToString();
    }

    public string GenerateHpaYaml(DeploymentSpec spec)
    {
        var s = Normalize(spec);
        var sb = new StringBuilder();
        sb.AppendLine("apiVersion: autoscaling/v2");
        sb.AppendLine("kind: HorizontalPodAutoscaler");
        sb.AppendLine("metadata:");
        sb.Append("  name: ").Append(s.AppName).AppendLine("-hpa");
        sb.Append("  namespace: ").AppendLine(s.Namespace);
        sb.AppendLine("spec:");
        sb.AppendLine("  scaleTargetRef:");
        sb.AppendLine("    apiVersion: apps/v1");
        sb.AppendLine("    kind: Deployment");
        sb.Append("    name: ").AppendLine(s.AppName);
        sb.Append("  minReplicas: ").AppendLine(Int(s.MinReplicas));
        sb.Append("  maxReplicas: ").AppendLine(Int(s.MaxReplicas));
        sb.AppendLine("  metrics:");
        sb.AppendLine("    - type: Resource");
        sb.AppendLine("      resource:");
        sb.AppendLine("        name: cpu");
        sb.AppendLine("        target:");
        sb.AppendLine("          type: Utilization");
        sb.Append("          averageUtilization: ").AppendLine(Int(s.TargetCpuPercent));
        return sb.ToString();
    }

    private static string GenerateDockerIgnore()
        => "bin/\nobj/\n.git/\n.vs/\n**/node_modules/\n*.user\nTemp/\n";

    private string GenerateReadme(DeploymentSpec s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {s.AppName} — PromptFlow Studio 배포 번들 (v8.2.0)");
        sb.AppendLine();
        sb.AppendLine("순수 BCL `DeploymentPackager` 로 생성된 K8s/Docker 매니페스트입니다.");
        sb.AppendLine();
        sb.AppendLine("## 빌드 & 푸시");
        sb.AppendLine("```bash");
        sb.AppendLine($"docker build -t {s.Image} .");
        sb.AppendLine($"docker push {s.Image}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## 배포");
        sb.AppendLine("```bash");
        sb.AppendLine("kubectl apply -f k8s/namespace.yaml");
        sb.AppendLine("kubectl apply -f k8s/deployment.yaml");
        sb.AppendLine("kubectl apply -f k8s/service.yaml");
        sb.AppendLine("kubectl apply -f k8s/hpa.yaml");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine($"- NodePort: `{Int(s.NodePort)}` · 컨테이너 포트: `{Int(s.ContainerPort)}`");
        sb.AppendLine($"- 오토스케일: {Int(s.MinReplicas)}~{Int(s.MaxReplicas)} 레플리카 · CPU {Int(s.TargetCpuPercent)}% 목표");
        return sb.ToString();
    }

    // ── 정규화 / 헬퍼 ─────────────────────────────────────────────────────────

    /// <summary>이름을 RFC1123 DNS 라벨로, 수치를 안전 범위로 클램프한다.</summary>
    private static DeploymentSpec Normalize(DeploymentSpec spec)
    {
        var app = DnsLabel(spec.AppName, "promptflow-studio");
        var ns = DnsLabel(spec.Namespace, "promptflow");
        var image = string.IsNullOrWhiteSpace(spec.Image) ? "promptflow/studio:latest" : spec.Image.Trim();
        return spec with
        {
            AppName = app,
            Namespace = ns,
            Image = image,
            Replicas = Math.Clamp(spec.Replicas, 1, 100),
            ContainerPort = Math.Clamp(spec.ContainerPort, 1, 65535),
            NodePort = Math.Clamp(spec.NodePort, 30000, 32767),
            CpuMilli = Math.Clamp(spec.CpuMilli, 50, 64000),
            MemoryMi = Math.Clamp(spec.MemoryMi, 64, 262144),
            MinReplicas = Math.Clamp(spec.MinReplicas, 1, 100),
            MaxReplicas = Math.Clamp(Math.Max(spec.MaxReplicas, spec.MinReplicas), 1, 100),
            TargetCpuPercent = Math.Clamp(spec.TargetCpuPercent, 1, 100),
        };
    }

    /// <summary>임의 문자열 → 소문자 영숫자/하이픈 DNS-1123 라벨(빈 값이면 폴백).</summary>
    private static string DnsLabel(string? raw, string fallback)
    {
        var src = (raw ?? "").Trim().ToLowerInvariant();
        var sb = new StringBuilder(src.Length);
        foreach (var ch in src)
            sb.Append(ch is >= 'a' and <= 'z' or >= '0' and <= '9' ? ch : '-');
        var label = sb.ToString().Trim('-');
        while (label.Contains("--")) label = label.Replace("--", "-");
        if (label.Length > 63) label = label[..63].Trim('-');
        return string.IsNullOrEmpty(label) ? fallback : label;
    }

    private static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);
}
