# Installs LlamaTree on Windows from the latest GitHub release:
#
#   powershell -c "irm https://github.com/LlamachantTechnology/LlamaTree/releases/latest/download/install.ps1 | iex"
#
# Downloads LlamaTree-Setup-<version>.exe and runs it silently, only once it has checked, as the in-app updater does,
# that it's the installer we built: it matches its line in the release's SHA256SUMS, which carries our signature
# (SHA256SUMS.p7s), and it carries our own Authenticode signature. "Ours" is pinned as the app pins it
# (src/LlamaTree/Updates/Publisher.cs): the certificate names Llamachant Technology Ltd with its registration number and
# country, is for code signing, and chains through Sectigo Public Code Signing Root R46's key. Not GitHub's word alone,
# so someone who could only publish on GitHub can't swap the installer. Releases before 1.3.1 have no SHA256SUMS, so
# this installs 1.3.1 or later only. Setup installs for this user only, with no admin, to
# %LOCALAPPDATA%\Programs\LlamaTree, or upgrades the copy there is. Then it opens LlamaTree.
#
#   $env:LLAMATREE_NO_LAUNCH=1  install only: don't open LlamaTree afterwards (for scripted installs)
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

    # Who we are, as Publisher.cs pins it: the certificate's O=, SERIALNUMBER= (the registration number) and
    # jurisdictionCountryName= (1.3.6.1.4.1.311.60.2.1.3, which Windows shows by its OID).
    $publisherName = 'Llamachant Technology Ltd'
    $publisherNumber = '2469513'
    $publisherCountry = 'CA'
    $codeSigning = '1.3.6.1.5.5.7.3.3'
    # Sectigo Public Code Signing Root R46 (thumbprint CCBBF9E1485AF63CE47ABF8E9E648C2504FC319D, valid to 2046), by the
    # SHA-256 of its RSA public key (the subjectPublicKey bits, PublicKey.EncodedKeyValue): by key, as in the app, so it
    # counts whether Windows reaches it as itself or cross-signed by an older root.
    $rootKeySha256 = '4d62e33740dec2018950b63a0db3f1b56a8dfb3890c8a4066bede128080e253c'
    # The certificate itself (as in Publisher.cs), only so the chain builds where Windows doesn't have it yet: given
    # to the chain as a certificate it may use, not as one it trusts. The key above is what's trusted.
    $rootCertificate = -join @(
        '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'
    )

    function Get-Sha256([byte[]]$bytes) {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { -join ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) } finally { $sha.Dispose() }
    }

    # Whether a certificate is ours: for code signing, and issued to us by name and registration, each named once.
    function Test-Publisher($certificate) {
        # One name per line, as Windows writes them (a value with special characters comes quoted, and doesn't match).
        $fields = @($certificate.SubjectName.Decode([Security.Cryptography.X509Certificates.X500DistinguishedNameFlags]::UseNewLines) -split "`r?`n")
        $field = {
            param([string[]]$names)
            $values = @(foreach ($f in $fields) { foreach ($n in $names) { if ($f.StartsWith("$n=")) { $f.Substring($n.Length + 1) } } })
            if ($values.Count -eq 1) { $values[0] }   # none, or the same field twice: nothing
        }
        $usages = @($certificate.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
            ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value })
        (& $field 'O') -ceq $publisherName -and (& $field 'SERIALNUMBER') -ceq $publisherNumber -and
            (& $field 'OID.1.3.6.1.4.1.311.60.2.1.3', '1.3.6.1.4.1.311.60.2.1.3') -ceq $publisherCountry -and $usages -contains $codeSigning
    }

    # Whether a certificate's chain, as of $at, is sound for code signing and goes through R46's key above it. The
    # pinned key is the trust here, not this PC's roots: an unknown root is allowed, as is revocation that can't be
    # checked (offline), and whatever $allow adds; anything else wrong in the chain (a bad signature, revoked, not for
    # code signing...) refuses it. Everything below R46 in the chain is signed by R46's key, so only Sectigo could issue it.
    function Test-Chain($certificate, $extra, [DateTime]$at, [string[]]$allow) {
        $chain = New-Object Security.Cryptography.X509Certificates.X509Chain
        try {
            $policy = $chain.ChainPolicy
            [void]$policy.ApplicationPolicy.Add((New-Object Security.Cryptography.Oid $codeSigning))
            if ($extra) { [void]$policy.ExtraStore.AddRange($extra) }
            [void]$policy.ExtraStore.Add((New-Object Security.Cryptography.X509Certificates.X509Certificate2 (,[Convert]::FromBase64String($rootCertificate))))
            $policy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::Online
            $policy.RevocationFlag = [Security.Cryptography.X509Certificates.X509RevocationFlag]::ExcludeRoot
            $policy.VerificationFlags = [Security.Cryptography.X509Certificates.X509VerificationFlags]::AllowUnknownCertificateAuthority
            $policy.VerificationTime = $at
            $policy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(20)
            [void]$chain.Build($certificate)
            $fine = @('NoError', 'UntrustedRoot', 'RevocationStatusUnknown', 'OfflineRevocation') + @($allow)
            $problems = @($chain.ChainStatus | Where-Object { $fine -notcontains "$($_.Status)" })
            $elements = @($chain.ChainElements | ForEach-Object { $_.Certificate })
            $throughRoot = @($elements | Select-Object -Skip 1 | Where-Object {
                $_.PublicKey.Oid.Value -eq '1.2.840.113549.1.1.1' -and (Get-Sha256 $_.PublicKey.EncodedKeyValue.RawData) -eq $rootKeySha256 }).Count -gt 0
            $problems.Count -eq 0 -and $elements.Count -gt 1 -and $elements[0].Equals($certificate) -and $throughRoot
        }
        finally { if ($chain -is [IDisposable]) { $chain.Dispose() } }
    }

    # Reads SHA256SUMS once SHA256SUMS.p7s shows it's ours, unchanged: a detached CMS signature by our certificate,
    # judged as of when it was signed (as in the app). Gives Sums (name -> SHA-256) and the Certificates that came with
    # the signature (the intermediates, which the installer's chain can use too). Throws why not, otherwise.
    function Read-SignedSums([byte[]]$content, [byte[]]$signature) {
        if (-not ('System.Security.Cryptography.Pkcs.SignedCms' -as [type])) {
            try { Add-Type -AssemblyName System.Security } catch { }
            try { Add-Type -AssemblyName System.Security.Cryptography.Pkcs } catch { }
        }
        $cms = New-Object Security.Cryptography.Pkcs.SignedCms ([Security.Cryptography.Pkcs.ContentInfo]::new($content)), $true
        try { $cms.Decode($signature); $cms.CheckSignature($true) }
        catch { throw "SHA256SUMS.p7s isn't a valid signature of SHA256SUMS ($($_.Exception.GetBaseException().Message.Trim()))." }
        if ($cms.SignerInfos.Count -ne 1 -or -not $cms.SignerInfos[0].Certificate) { throw 'SHA256SUMS.p7s must have exactly one signer.' }
        $signer = $cms.SignerInfos[0]
        if (-not (Test-Publisher $signer.Certificate)) { throw "SHA256SUMS is signed by someone else ($($signer.Certificate.Subject))." }
        $signed = [DateTime]::UtcNow
        foreach ($attribute in $signer.SignedAttributes) {
            if ($attribute.Oid.Value -eq '1.2.840.113549.1.9.5') {
                $time = New-Object Security.Cryptography.Pkcs.Pkcs9SigningTime
                $time.CopyFrom($attribute.Values[0])
                $signed = $time.SigningTime.ToUniversalTime()
            }
        }
        if ($signed -gt [DateTime]::UtcNow.AddDays(1)) { throw 'SHA256SUMS.p7s says it was signed in the future.' }
        if (-not (Test-Chain $signer.Certificate $cms.Certificates $signed.ToLocalTime() @())) {
            throw "SHA256SUMS's signing certificate doesn't chain to Sectigo Public Code Signing Root R46 ($($signer.Certificate.Subject))."
        }
        $sums = New-Object Collections.Hashtable ([StringComparer]::Ordinal)
        foreach ($line in ([Text.Encoding]::UTF8.GetString($content) -split "`n")) {
            if ($line -cmatch '^([0-9a-f]{64}) [ *](.+?)\r?$') {
                if ($sums.ContainsKey($Matches[2])) { throw "SHA256SUMS lists $($Matches[2]) twice." }
                $sums[$Matches[2]] = $Matches[1]
            }
        }
        @{ Sums = $sums; Certificates = $cms.Certificates }   # the intermediates too, for the installer's chain
    }

    # Whether the file carries a valid Authenticode signature (Windows' own check, as Explorer makes it: unchanged
    # since signing, trusted, timestamped) by our certificate, through R46. The chain is checked as of now with an
    # expired certificate allowed, since Windows has already judged it by its timestamp.
    function Test-SignedByUs([string]$path, $extra) {
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        "$($signature.Status)" -eq 'Valid' -and $signature.SignerCertificate -and (Test-Publisher $signature.SignerCertificate) -and
            (Test-Chain $signature.SignerCertificate $extra (Get-Date) @('NotTimeValid'))
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

    # The release's signed checksums, which every release from 1.3.1 on carries; without them it can't be checked.
    $sumsAsset = @($release.assets) | Where-Object { $_.name -eq 'SHA256SUMS' } | Select-Object -First 1
    $sigAsset = @($release.assets) | Where-Object { $_.name -eq 'SHA256SUMS.p7s' } | Select-Object -First 1
    if (-not $sumsAsset -or -not $sigAsset) {
        Fail "LlamaTree $version's release has no signed checksums (SHA256SUMS and SHA256SUMS.p7s), so its installer can't be checked and won't be installed. Download it from $releases/latest instead."
    }

    # A GitHub download over https, as in the app; a test stand-in may also serve from this machine.
    function Get-DownloadUrl($a) {
        $u = [Uri]"$($a.browser_download_url)"
        $fromGitHub = $u.IsAbsoluteUri -and $u.Scheme -eq 'https' -and $u.Host -eq 'github.com'
        if (-not $fromGitHub -and -not ($testUrl -and $u.IsAbsoluteUri -and $u.IsLoopback)) {
            Fail "The address of $($a.name) isn't a GitHub download, so it won't be used: $u"
        }
        $u
    }
    $url = Get-DownloadUrl $asset
    $sumsUrl = Get-DownloadUrl $sumsAsset
    $sigUrl = Get-DownloadUrl $sigAsset

    $dir = Join-Path ([IO.Path]::GetTempPath()) ('LlamaTree-Install-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $dir | Out-Null
    $keep = $false
    try {
        $setup = Join-Path $dir $name
        Write-Host "Downloading LlamaTree $version..."
        $sumsFile = Join-Path $dir 'SHA256SUMS'
        $sigFile = Join-Path $dir 'SHA256SUMS.p7s'
        foreach ($d in @(@($url, $setup), @($sumsUrl, $sumsFile), @($sigUrl, $sigFile))) {
            try { Invoke-WebRequest -Uri $d[0] -OutFile $d[1] -Headers @{ 'User-Agent' = 'LlamaTree-installer' } -UseBasicParsing }
            catch { Fail "Couldn't download $($d[0]): $($_.Exception.Message)" }
        }

        $actual = (Get-FileHash -Path $setup -Algorithm SHA256).Hash.ToLowerInvariant()
        $size = (Get-Item $setup).Length
        if ($actual -ne $expected -or ($asset.size -gt 0 -and $size -ne $asset.size)) {
            Fail "$name doesn't match the checksum GitHub publishes for it (got $actual, expected $expected), so it wasn't run."
        }

        # Ours, not only GitHub's word: its line in the signed SHA256SUMS, then its own signature.
        Write-Host 'Checking its signatures...'
        try { $signed = Read-SignedSums ([IO.File]::ReadAllBytes($sumsFile)) ([IO.File]::ReadAllBytes($sigFile)) }
        catch { Fail "The release's checksums aren't signed by Llamachant Technology Ltd, so $name wasn't run. $($_.Exception.Message)" }
        $sums = $signed.Sums
        if (-not $sums.ContainsKey($name)) { Fail "The release's signed SHA256SUMS doesn't list $name, so it wasn't run." }
        if ($sums[$name] -ne $actual) {
            Fail "$name doesn't match its checksum in the release's signed SHA256SUMS (got $actual, signed $($sums[$name])), so it wasn't run."
        }
        if (-not (Test-SignedByUs $setup $signed.Certificates)) {
            Fail "$name doesn't carry a valid signature by Llamachant Technology Ltd, so it wasn't run."
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

        # Setup only warns about this in a message box, which a silent install doesn't show.
        $webView2 = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
        $webView2Found = foreach ($path in "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", "HKLM:\$webView2", "HKCU:\$webView2") {
            $pv = (Get-ItemProperty -Path $path -ErrorAction SilentlyContinue).pv
            if ($pv -and $pv -ne '0.0.0.0') { $true; break }
        }

        # Opens the app, unless asked not to, it can't start (no WebView2), there's no desktop to show it on (session 0:
        # over SSH, or a service), or this window is elevated: the app would then run as administrator, which setup
        # avoids too (runasoriginaluser). Start-Process doesn't tie it to this window, so closing the window leaves it
        # open. When it's already open (an upgrade setup didn't have to close it), the new copy only brings the open
        # window forward and exits (Program.cs's single-instance lock).
        $exe = Join-Path $location 'LlamaTree.exe'
        $elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
        $launch = $env:LLAMATREE_NO_LAUNCH -ne '1' -and $webView2Found -and (Test-Path -LiteralPath $exe) -and
            (Get-Process -Id $PID).SessionId -ne 0 -and -not $elevated
        $launched = $false
        if ($launch) {
            try { Start-Process -FilePath $exe -WorkingDirectory $location; $launched = $true }
            catch { Write-Host "Couldn't start LlamaTree: $($_.Exception.Message)" -ForegroundColor Yellow }
        }

        Write-Host ''
        Write-Host "LlamaTree $version is installed in $location" -ForegroundColor Green
        if ($launched) {
            Write-Host 'LlamaTree is opening. Later, start it from the Start menu (LlamaTree).'
        }
        else {
            if ($elevated -and $env:LLAMATREE_NO_LAUNCH -ne '1') {
                Write-Host "It wasn't opened from this administrator window, so that it doesn't run as administrator."
            }
            Write-Host 'Start it from the Start menu (LlamaTree), or run:'
            Write-Host "  & '$exe'"
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
