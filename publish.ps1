# Builds a self-contained release and the Inno Setup installer.
#   .\publish.ps1                 -> artifacts\publish\ + artifacts\ClaudeTerminals-Setup-<version>.exe
#   .\publish.ps1 -SkipInstaller  -> artifacts\publish\ only (portable folder)
param([switch]$SkipInstaller)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\ClaudeTerminals\ClaudeTerminals.csproj'
$publishDir = Join-Path $root 'artifacts\publish'

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $project" }
Write-Host "Claude Terminals $version" -ForegroundColor Cyan

# Front-end libraries (xterm.js, Monaco) are copied from web\node_modules during publish.
if (-not (Test-Path (Join-Path $root 'web\node_modules\monaco-editor'))) {
    Write-Host 'Restoring front-end libraries…'
    Push-Location (Join-Path $root 'web')
    try { npm ci; if ($LASTEXITCODE) { throw 'npm ci failed' } } finally { Pop-Location }
}

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false -o $publishDir
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

if ($SkipInstaller) {
    Write-Host "Published to $publishDir" -ForegroundColor Green
    return
}

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup not found. Install it with: winget install JRSoftware.InnoSetup' }

& $iscc /Q "/DAppVersion=$version" "/DSourceDir=$publishDir" (Join-Path $root 'installer\ClaudeTerminals.iss')
if ($LASTEXITCODE) { throw 'Inno Setup compile failed' }

$setup = Join-Path $root "artifacts\ClaudeTerminals-Setup-$version.exe"
Write-Host "Installer: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)" -ForegroundColor Green
