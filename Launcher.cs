using System.Diagnostics;

var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false };
psi.ArgumentList.Add("run");
psi.ArgumentList.Add("--project");
psi.ArgumentList.Add("PromptFlowStudio/PromptFlowStudio.csproj");
foreach (var a in args) psi.ArgumentList.Add(a);

using var proc = Process.Start(psi);
proc!.WaitForExit();
return proc.ExitCode;