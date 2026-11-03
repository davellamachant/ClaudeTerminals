# Builds a self-contained release and the Inno Setup installer, then the Linux packages.
#   .\publish.ps1                 -> artifacts\publish\ + artifacts\LlamaTree-Setup-<version>.exe
#                                    + artifacts\linux\: llamatree_<version>_amd64.deb, llamatree-<version>-1.x86_64.rpm and
#                                      LlamaTree-<version>-linux-x64.tar.gz (see tools\package-linux.cs)
#   .\publish.ps1 -SkipInstaller  -> artifacts\publish\ only (portable folder)
#   .\publish.ps1 -SkipLinux      -> the Windows installer only
#   .\publish.ps1 -Sign -TimestampServer <url>
#                                 -> the same, with the app, setup and uninstaller code-signed (on the build agent with
#                                    the company's certificate; see azure-pipelines.yml)
param(
    [switch]$SkipInstaller,
    [switch]$SkipLinux,
    [switch]$Sign,
    # The certificate is picked from the Windows certificate store by subject (on the agent the SafeNet token provides it).
    [string]$CertificateSubject = 'Llamachant Technology Ltd',
    # An RFC 3161 timestamp server, so signatures stay valid after the certificate expires.
    [string]$TimestampServer
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\LlamaTree\LlamaTree.csproj'
$publishDir = Join-Path $root 'artifacts\publish'

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $project" }
Write-Host "LlamaTree $version" -ForegroundColor Cyan

if ($Sign) {
    if (-not $TimestampServer) { throw '-Sign needs -TimestampServer.' }
    # signtool comes with the Windows SDK; take the newest x64 one if it isn't on PATH.
    $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    if (-not $signtool) {
        $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object { [version]($_.Directory.Parent.Name) } -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $signtool) { throw 'signtool.exe not found. Install the Windows SDK (Signing Tools for Desktop Apps).' }
    Write-Host "Signing as '$CertificateSubject' with $signtool"
}

function Invoke-Sign([string[]]$Files) {
    & $signtool sign /n $CertificateSubject /fd sha256 /tr $TimestampServer /td sha256 /v @Files
    if ($LASTEXITCODE) { throw "signtool failed for: $($Files -join ', ')" }
}

# Signed, by our certificate, and timestamped; anything else stops the release.
function Assert-Signed([string[]]$Files) {
    foreach ($file in $Files) {
        $sig = Get-AuthenticodeSignature $file
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notlike "*$CertificateSubject*" -or -not $sig.TimeStamperCertificate) {
            throw "$file is not properly signed: $($sig.Status) $($sig.StatusMessage) (signer: $($sig.SignerCertificate.Subject))"
        }
        Write-Host "  signed: $(Split-Path $file -Leaf)"
    }
}

# Front-end libraries (xterm.js, Monaco) are copied from web\node_modules during publish.
if (-not (Test-Path (Join-Path $root 'web\node_modules\monaco-editor'))) {
    Write-Host 'Restoring front-end libraries...'
    Push-Location (Join-Path $root 'web')
    try { npm ci; if ($LASTEXITCODE) { throw 'npm ci failed' } } finally { Pop-Location }
}

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
# The Windows build (WinForms + WebView2); the project also builds net10.0 for macOS and Linux.
dotnet publish $project -f net10.0-windows -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false -o $publishDir
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

if ($Sign) {
    # Only our own files: the .NET runtime and WebView2 files that ship alongside are already signed by Microsoft.
    $ours = 'LlamaTree.exe', 'LlamaTree.dll' | ForEach-Object { Join-Path $publishDir $_ }
    Invoke-Sign $ours
    Assert-Signed $ours
}

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

$isccArgs = @('/Q', "/DAppVersion=$version", "/DSourceDir=$publishDir")
if ($Sign) {
    # Inno Setup signs setup and the uninstaller it embeds with this command ($f is the file, $q a quote).
    $isccArgs += '/DSign', "/Ssigntool=`$q$signtool`$q sign /n `$q$CertificateSubject`$q /fd sha256 /tr `$q$TimestampServer`$q /td sha256 `$f"
}
& $iscc @isccArgs (Join-Path $root 'installer\LlamaTree.iss')
if ($LASTEXITCODE) { throw 'Inno Setup compile failed' }

$setup = Join-Path $root "artifacts\LlamaTree-Setup-$version.exe"
if ($Sign) { Assert-Signed $setup }
Write-Host "Installer: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)" -ForegroundColor Green

if ($SkipLinux) { return }

# Linux: the net10.0 build (a Photino window), self-contained, packaged as .deb, .rpm and .tar.gz. Built here on
# Windows; the packages need no Linux tools to make (see tools\package-linux.cs).
$linuxDir = Join-Path $root 'artifacts\linux-x64'
if (Test-Path $linuxDir) { Remove-Item $linuxDir -Recurse -Force }
dotnet publish $project -f net10.0 -c Release -r linux-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $linuxDir
if ($LASTEXITCODE) { throw 'dotnet publish (linux-x64) failed' }
dotnet run (Join-Path $root 'tools\package-linux.cs') -- $linuxDir $version (Join-Path $root 'artifacts\linux') x64
if ($LASTEXITCODE) { throw 'Linux packaging failed' }
Write-Host "Linux packages in artifacts\linux: llamatree_${version}_amd64.deb, llamatree-$version-1.x86_64.rpm, LlamaTree-$version-linux-x64.tar.gz" -ForegroundColor Green
