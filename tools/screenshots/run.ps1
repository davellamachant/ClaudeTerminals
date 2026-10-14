# Regenerates the README screenshots from a fictional demo project; no real sessions, repos or
# conversations are involved. Requires Node.js and a build of the app.
#   .\tools\screenshots\run.ps1                 # demo in <drive>:\Demo, images in docs\images
#   .\tools\screenshots\run.ps1 -KeepDemo       # leave the demo repos for poking around
param(
    [string]$DemoRoot = (Join-Path ([IO.Path]::GetPathRoot($PSScriptRoot)) 'Demo'),
    [int]$Port = 9333,
    [switch]$KeepDemo
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$exe = Join-Path $repo 'src\ClaudeTerminals\bin\Debug\net10.0-windows\ClaudeTerminals.exe'

dotnet build (Join-Path $repo 'src\ClaudeTerminals') -v q -nologo | Out-Null
if ($LASTEXITCODE) { throw 'Build failed' }

$demo = node (Join-Path $PSScriptRoot 'setup-demo.mjs') $DemoRoot | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Demo setup failed' }

# Isolated profile + fake conversation history; nothing from the real user profile is loaded.
$saved = @{}
$vars = @{
    CLAUDE_TERMINALS_DATA = $demo.profile
    CLAUDE_CONFIG_DIR = $demo.claudeConfig
    WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$Port"
}
foreach ($k in $vars.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $vars[$k]) }

$app = Start-Process $exe -PassThru
try {
    node (Join-Path $PSScriptRoot 'capture.mjs') $Port (Join-Path $repo 'docs\images')
    if ($LASTEXITCODE) { throw 'Capture failed' }
}
finally {
    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
    if (-not $KeepDemo) {
        Start-Sleep 2   # let the session processes release the worktrees
        if (Test-Path (Join-Path $demo.root '.claude-terminals-demo')) { Remove-Item $demo.root -Recurse -Force }
    }
}
