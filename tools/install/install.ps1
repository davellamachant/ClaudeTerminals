# Installs LlamaTree on Windows from the latest GitHub release:
#
#   powershell -c "irm https://github.com/LlamachantTechnology/LlamaTree/releases/latest/download/install.ps1 | iex"
#
# Downloads LlamaTree-Setup-<version>.exe, checks it against the SHA-256 GitHub publishes for it (as the in-app
# updater does), and runs it silently. Setup installs for this user only, with no admin, to
# %LOCALAPPDATA%\Programs\LlamaTree, or upgrades the copy there is.
#
#   $env:LLAMATREE_UPDATE_URL   a stand-in for the GitHub API's latest release, for tests (downloads may then be local)
#
# Runs on Windows PowerShell 5.1 and PowerShell 7. It's piped into iex, so it never calls exit (that would close the
# window it was run in): a failure is thrown, which also gives `powershell -c` a nonzero exit code.

& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # Windows PowerShell's progress bar slows a download many times over

    $repo = 'LlamachantTechnology/LlamaTree'
    $releases = "https://github.com/$repo/releases"

    function Fail([string]$message) {
        Write-Host $message -ForegroundColor Red
        throw 'LlamaTree was not installed.'
    }

    if ($env:OS -ne 'Windows_NT') {
        Fail "This installs LlamaTree on Windows. On Linux, run:  curl -fsSL $releases/latest/download/install.sh | sh"
    }
    if (-not [Environment]::Is64BitOperatingSystem) { Fail 'LlamaTree needs 64-bit Windows.' }

    # Windows PowerShell may not offer TLS 1.2 by default, and GitHub needs it.
    if ($PSVersionTable.PSVersion.Major -lt 6) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }

    $testUrl = if ([string]::IsNullOrWhiteSpace($env:LLAMATREE_UPDATE_URL)) { $null } else { $env:LLAMATREE_UPDATE_URL }
    $api = if ($testUrl) { $testUrl } else { "https://api.github.com/repos/$repo/releases/latest" }
    $headers = @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'LlamaTree-installer' }

    try { $release = Invoke-RestMethod -Uri $api -Headers $headers -UseBasicParsing }
    catch { Fail "Couldn't find LlamaTree's latest release on GitHub: $($_.Exception.Message)" }

    $version = "$($release.tag_name)" -replace '^[vV]', ''
    if (-not $version) { Fail "Couldn't read the latest release's version." }
    $name = "LlamaTree-Setup-$version.exe"
    $asset = @($release.assets) | Where-Object { $_.name -eq $name } | Select-Object -First 1
    if (-not $asset) { Fail "LlamaTree $version has no $name. See $releases/latest" }

    # GitHub's own digest of the file; without one there's nothing to check the download against.
    $digest = "$($asset.digest)"
    if ($digest -notmatch '^sha256:([0-9a-fA-F]{64})$') {
        Fail "The release doesn't publish a checksum for $name, so it won't be installed. Download it from $releases/latest instead."
    }
    $expected = $Matches[1].ToLowerInvariant()

    # A GitHub download over https, as in the app; a test stand-in may also serve from this machine.
    $url = [Uri]"$($asset.browser_download_url)"
    $fromGitHub = $url.IsAbsoluteUri -and $url.Scheme -eq 'https' -and $url.Host -eq 'github.com'
    if (-not $fromGitHub -and -not ($testUrl -and $url.IsAbsoluteUri -and $url.IsLoopback)) {
        Fail "The installer's address isn't a GitHub download, so it won't be used: $url"
    }

    $dir = Join-Path ([IO.Path]::GetTempPath()) ('LlamaTree-Install-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $dir | Out-Null
    $keep = $false
    try {
        $setup = Join-Path $dir $name
        Write-Host "Downloading LlamaTree $version..."
        try { Invoke-WebRequest -Uri $url -OutFile $setup -Headers @{ 'User-Agent' = 'LlamaTree-installer' } -UseBasicParsing }
        catch { Fail "Couldn't download ${url}: $($_.Exception.Message)" }

        $actual = (Get-FileHash -Path $setup -Algorithm SHA256).Hash.ToLowerInvariant()
        $size = (Get-Item $setup).Length
        if ($actual -ne $expected -or ($asset.size -gt 0 -and $size -ne $asset.size)) {
            Fail "$name doesn't match the checksum GitHub publishes for it (got $actual, expected $expected), so it wasn't run."
        }

        if (Get-Process -Name LlamaTree -ErrorAction SilentlyContinue) {
            Write-Host 'LlamaTree is open: setup will ask you to close it before it carries on.' -ForegroundColor Yellow
        }

        # Silent, for this user only (setup's default); no restart. Setup asks nothing else.
        Write-Host "Installing LlamaTree $version..."
        $log = Join-Path $dir 'setup.log'
        $p = Start-Process -FilePath $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"" -PassThru
        $null = $p.Handle   # keeps the exit code readable once it has exited
        $p.WaitForExit()
        if ($p.ExitCode -ne 0) {
            $keep = $true
            Fail "Setup didn't finish (exit code $($p.ExitCode)). Its log is in $log"
        }

        # Where it went: the uninstall entry setup writes, per user first.
        $key = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{A224708F-0121-4F63-924D-5E894BC86AF6}_is1'
        $location = foreach ($root in 'HKCU:', 'HKLM:') {
            $entry = Get-ItemProperty -Path "$root\$key" -ErrorAction SilentlyContinue
            if ($entry -and $entry.InstallLocation) { $entry.InstallLocation.TrimEnd('\'); break }
        }
        if (-not $location) { $location = Join-Path $env:LOCALAPPDATA 'Programs\LlamaTree' }

        Write-Host ''
        Write-Host "LlamaTree $version is installed in $location" -ForegroundColor Green
        Write-Host 'Start it from the Start menu (LlamaTree), or run:'
        Write-Host "  & '$(Join-Path $location 'LlamaTree.exe')'"

        # Setup only warns about this in a message box, which a silent install doesn't show.
        $webView2 = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
        $webView2Found = foreach ($path in "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", "HKLM:\$webView2", "HKCU:\$webView2") {
            $pv = (Get-ItemProperty -Path $path -ErrorAction SilentlyContinue).pv
            if ($pv -and $pv -ne '0.0.0.0') { $true; break }
        }
        if (-not $webView2Found) {
            Write-Host ''
            Write-Host 'LlamaTree also needs the Microsoft Edge WebView2 Runtime, which was not found on this PC:' -ForegroundColor Yellow
            Write-Host '  https://developer.microsoft.com/microsoft-edge/webview2/'
        }
    }
    finally {
        if (-not $keep) { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
