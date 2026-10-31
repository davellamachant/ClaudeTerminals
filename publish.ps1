# Builds a self-contained release and the Inno Setup installer.
#   .\publish.ps1                 -> artifacts\publish\ + artifacts\ClaudeTerminals-Setup-<version>.exe
#   .\publish.ps1 -SkipInstaller  -> artifacts\publish\ only (portable folder)
#   .\publish.ps1 -Sign -TimestampServer <url>
#                                 -> the same, with the app, setup and uninstaller code-signed (on the build agent with
#                                    the company's certificate; see azure-pipelines.yml)
param(
    [switch]$SkipInstaller,
    [switch]$Sign,
    # The certificate is picked from the Windows certificate store by subject (on the agent the SafeNet token provides it).
    [string]$CertificateSubject = 'Llamachant Technology Ltd',
    # An RFC 3161 timestamp server, so signatures stay valid after the certificate expires.
    [string]$TimestampServer,
    # The public storage account the app reads its notices from (the release pipeline's StorageAccount variable).
    # Without it the app shows no notices.
    [string]$NoticesStorageAccount
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\ClaudeTerminals\ClaudeTerminals.csproj'
$publishDir = Join-Path $root 'artifacts\publish'

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $project" }
Write-Host "Claude Terminals $version" -ForegroundColor Cyan

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
if ($NoticesStorageAccount) { Write-Host "Notices from the storage account $NoticesStorageAccount" }
else { Write-Warning 'No -NoticesStorageAccount: this build shows no notices.' }
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false "-p:NoticesStorageAccount=$NoticesStorageAccount" -o $publishDir
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

if ($Sign) {
    # Only our own files: the .NET runtime and WebView2 files that ship alongside are already signed by Microsoft.
    $ours = 'ClaudeTerminals.exe', 'ClaudeTerminals.dll' | ForEach-Object { Join-Path $publishDir $_ }
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
& $iscc @isccArgs (Join-Path $root 'installer\ClaudeTerminals.iss')
if ($LASTEXITCODE) { throw 'Inno Setup compile failed' }

$setup = Join-Path $root "artifacts\ClaudeTerminals-Setup-$version.exe"
if ($Sign) { Assert-Signed $setup }
Write-Host "Installer: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)" -ForegroundColor Green
