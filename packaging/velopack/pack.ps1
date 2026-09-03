# ============================================================
# Velopack 로컬 패키징 (Windows) — 릴리즈 워크플로우(.github/workflows/release.yml)와
# 같은 방식으로 Setup.exe · 포터블 zip · 업데이트 패키지를 만든다. 워크플로우를 돌리기 전에
# 손으로 검증할 때 쓴다. 실제 배포는 태그를 밀어 워크플로우가 하게 둔다.
#
#   사용:  powershell -ExecutionPolicy Bypass -File packaging/velopack/pack.ps1 -Version 0.4.0
#   결과:  dist/releases/Aurum-win-Setup.exe, Aurum-win-Portable.zip, *.nupkg
#
# Velopack 은 파일 단위로 갱신하므로 PublishSingleFile 을 쓰지 않는다
# (단일 exe 가 필요하면 packaging/windows/make-app.ps1 — 그 본은 자동 업데이트가 안 된다).
# ============================================================
[CmdletBinding()]
param(
    [string]$Rid = 'win-x64',
    [string]$Version = '0.3.0'
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$projectDir = Join-Path $root 'src\PrismOne.Studio'
$publishDir = Join-Path $root "dist\velopack-publish-$Rid"
$releaseDir = Join-Path $root 'dist\releases'

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Output '== vpk 설치 (dotnet tool) =='
    dotnet tool install -g vpk
    if ($LASTEXITCODE -ne 0) { throw "vpk 설치 실패 (exit $LASTEXITCODE)" }
}

Write-Output "== publish ($Rid, self-contained) =="
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $projectDir -c Release -r $Rid --self-contained `
    -p:Version=$Version -p:DebugType=none `
    -o $publishDir -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 실패 (exit $LASTEXITCODE)" }

Write-Output "== vpk pack $Version =="
vpk pack -u Aurum -v $Version -p $publishDir -e Aurum.exe `
    --packTitle Aurum --packAuthors 'HyeonGi Min' `
    --icon (Join-Path $projectDir 'Assets\icon.ico') `
    -c win -o $releaseDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack 실패 (exit $LASTEXITCODE)" }

Write-Output "== 완료: $releaseDir =="
Get-ChildItem $releaseDir | ForEach-Object {
    Write-Output ("   {0} ({1} MB)" -f $_.Name, [math]::Round($_.Length / 1MB, 1))
}
