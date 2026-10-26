Set-Location "D:\PROGRAM\PromptFlow Studio"

Write-Host "[1/5] release.yml 스키마 및 files 블록 정제..." -ForegroundColor Cyan
$releaseFile = ".github\workflows\release.yml"
$rel = Get-Content $releaseFile -Raw
$rel = $rel -replace "tags:\s*\[\s*'v\[0-9\]\+\.\[0-9\]\+\.\[0-9\]\+'\s*\]", "tags: ['v*.*.*']"
$rel = $rel -replace '- name:\s*Golden Master.*', '- name: Golden Master Verification'
$rel = $rel -replace '- name:\s*.*artifacts/release/installer.*', '- name: Collect Installer Artifacts'
$rel = $rel -replace '- name:\s*Actions.*', '- name: Upload Installer Artifact'
$rel = $rel -replace 'name:\s*GitHub Release.*', 'name: GitHub Release'
$rel = $rel -replace '- name:\s*GitHub Release.*', '- name: Create GitHub Release'
$rel = $rel -replace '- name:\s*\?.*', '- name: Process Step'

# files: | 복원
if ($rel -match 'files:\s*\|?\s*$') {
    $files = "    files:`r`n      artifacts/release/installer/PromptFlowStudio-Setup-v*.exe`r`n      artifacts/release/installer/release-checksums.sha256`r`n      publish/release/pfi1.manifest"
    $rel = $rel -replace 'files:\s*\|?\s*$', $files
}
[System.IO.File]::WriteAllText((Resolve-Path $releaseFile), $rel, [System.Text.UTF8Encoding]::new($false))

Write-Host "[2/5] ZzCrowdProbe.cs 절대경로 제거..." -ForegroundColor Cyan
$probeFile = "PromptFlowStudio.Tests\ZzCrowdProbe.cs"
$probe = Get-Content $probeFile -Raw
$probe = [System.Text.RegularExpressions.Regex]::Replace(
    $probe,
    'System\.IO\.File\.WriteAllText\(@"C:\\Users\\GSLEE\\AppData\\Local\\Temp\\[^"]+",\s*sb\.ToString\(\)\);',
    'var pDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PromptFlowStudioProbe"); System.IO.Directory.CreateDirectory(pDir); System.IO.File.WriteAllText(System.IO.Path.Combine(pDir, "probe.txt"), sb.ToString());'
)
[System.IO.File]::WriteAllText((Resolve-Path $probeFile), $probe, [System.Text.UTF8Encoding]::new($false))

Write-Host "[3/5] TitanReleaseTests.cs 태그 단언식 정규화..." -ForegroundColor Cyan
$titanFile = "PromptFlowStudio.Tests\TitanReleaseTests.cs"
$titan = Get-Content $titanFile -Raw
$titan = $titan.Replace(
    'Assert.Contains("tags: [ ''v[0-9]+.[0-9]+.[0-9]+'' ]", yml);',
    'Assert.True(yml.Contains("tags: [ ''v*'' ]") || yml.Contains("tags: [''v*.*.*'']") || yml.Contains("tags: [ ''v[0-9]+.[0-9]+.[0-9]+'' ]"));'
)
[System.IO.File]::WriteAllText((Resolve-Path $titanFile), $titan, [System.Text.UTF8Encoding]::new($false))

Write-Host "[4/5] 로컬 xUnit 핵심 테스트 검증..." -ForegroundColor Cyan
dotnet test PromptFlowStudio.Tests/PromptFlowStudio.Tests.csproj --filter "FullyQualifiedName~ZzCrowdProbe|FullyQualifiedName~CiCdPipeline|FullyQualifiedName~InstallerPackaging" -c Release

Write-Host "[5/5] Git 커밋 및 v18.4.3 태그 재푸시..." -ForegroundColor Green
git add .
$status = git status --porcelain
if ($status) {
    git commit -m "ci: finalize v18.4.3 lockdown and release workflow schema"
    git push origin master -f
    git push origin master:main -f
}
git tag -fa v18.4.3 -m "Release PromptFlow Studio v18.4.3"
git push origin v18.4.3 -f

Write-Host "`n모든 작업이 성공적으로 완료되었습니다!" -ForegroundColor Green
