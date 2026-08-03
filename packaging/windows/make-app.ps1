# ============================================================
# Windows 배포 패키지 생성 — .NET 런타임 설치 없이 실행되는 단일 exe.
#
#   사용:  powershell -ExecutionPolicy Bypass -File tools/packaging/windows/make-app.ps1
#   결과:  tools/dist/IAP Database Manager/IAP Database Manager.exe
#          tools/dist/IAPDatabaseManager-win-x64-<버전>.zip
#
# macOS 쪽은 packaging/macos/make-app.sh 가 담당한다 (.app 번들).
# ============================================================
[CmdletBinding()]
param(
    [string]$Rid = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$Version = '0.1.0',
    [switch]$SkipIcon,
    [switch]$SkipZip
)

$ErrorActionPreference = 'Stop'

$toolsDir = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$appName = 'IAP Database Manager'
$projectDir = Join-Path $toolsDir 'src\PrismOne.Studio'
$distDir = Join-Path $toolsDir 'dist'
$publishDir = Join-Path $distDir "publish-$Rid"
$stageDir = Join-Path $distDir $appName

if (-not $SkipIcon) {
    Write-Output '== icon.ico 생성 =='
    & (Join-Path $PSScriptRoot 'make-icon.ps1')
}

Write-Output "== publish ($Rid, self-contained single file) =="
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $projectDir `
    -c $Configuration -r $Rid --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $publishDir -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 실패 (exit $LASTEXITCODE)" }

Write-Output '== 배포 폴더 구성 =='
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
Copy-Item (Join-Path $publishDir '*') $stageDir -Recurse -Force

# 실행 파일 이름을 제품명으로 (작업 표시줄·시작 메뉴에 그대로 노출된다)
$publishedExe = Join-Path $stageDir 'PrismOne.Studio.exe'
$targetExe = Join-Path $stageDir "$appName.exe"
if (-not (Test-Path $publishedExe)) { throw "publish 산출물에 PrismOne.Studio.exe 가 없습니다: $stageDir" }
Move-Item $publishedExe $targetExe -Force

if (-not $SkipZip) {
    $zipPath = Join-Path $distDir "IAPDatabaseManager-$Rid-$Version.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path $stageDir -DestinationPath $zipPath
    Write-Output "== zip: $zipPath =="
}

$sizeMb = [math]::Round((Get-Item $targetExe).Length / 1MB, 1)
Write-Output "== 완료: $targetExe ($sizeMb MB) =="
