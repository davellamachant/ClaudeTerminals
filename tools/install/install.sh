#!/bin/sh
# Installs LlamaTree on Linux from the latest GitHub release:
#
#   curl -fsSL https://github.com/LlamachantTechnology/LlamaTree/releases/latest/download/install.sh | sh
#
# With apt-get, dnf or zypper and administrator rights (root, or sudo: it asks for the password when there's a
# terminal to ask in), it installs the .deb or .rpm, which brings WebKitGTK and libnotify with it. Otherwise it unpacks
# the .tar.gz into ~/.local/share/LlamaTree with a `llamatree` command in ~/.local/bin; that copy updates itself too.
# The download is installed only once it's shown to be the one we built, as the in-app updater checks it: it matches its
# line in the release's SHA256SUMS, and SHA256SUMS.p7s is our signature of that list (checked with openssl), by a code
# signing certificate issued to Llamachant Technology Ltd with its registration number and country, chaining to
# Sectigo Public Code Signing Root R46, which is in this script. Not GitHub's word alone (its digest is checked too),
# so someone who could only publish on GitHub can't swap the download. Releases before 1.3.1 have no SHA256SUMS, so
# this installs 1.3.1 or later only.
#
#   LLAMATREE_INSTALL=tar      install for this user only, from the .tar.gz, even when a package could be installed
#   LLAMATREE_INSTALL=package  install the package or stop: never fall back to the .tar.gz
#   LLAMATREE_UPDATE_URL       a stand-in for the GitHub API's latest release, for tests (downloads may then be local)
#   LLAMATREE_INSECURE_SKIP_SIGNATURE=1
#                              where there's no openssl to check the signature with, install on GitHub's digest alone
#                              (not recommended: install openssl instead). It changes nothing where openssl is there.
#
# POSIX sh, so it runs under `curl | sh` on dash, busybox and bash alike; needs curl or wget, openssl, and no jq.
# Everything is inside main, called on the last line, so a download cut short runs nothing.

set -eu

REPO=LlamachantTechnology/LlamaTree
RELEASES="https://github.com/$REPO/releases"

say() { printf '%s\n' "$*"; }
warn() { printf 'LlamaTree: %s\n' "$*" >&2; }
die() { warn "$*"; exit 1; }
have() { command -v "$1" >/dev/null 2>&1; }

# Single-quotes a value for a shell script.
quote() { printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"; }

download() {   # url file
    if have curl; then curl -fsSL --retry 2 -H 'Accept: application/vnd.github+json' -o "$2" "$1"
    elif have wget; then wget -q --header='Accept: application/vnd.github+json' -O "$2" "$1"
    else die "Installing needs curl or wget."
    fi
}

sha256() {
    if have sha256sum; then sha256sum "$1" | cut -d' ' -f1
    elif have shasum; then shasum -a 256 "$1" | cut -d' ' -f1
    elif have openssl; then openssl dgst -sha256 -r "$1" | cut -d' ' -f1
    else die "Checking the download needs sha256sum, shasum or openssl."
    fi
}

# Reads the release JSON without jq. GitHub's asset objects list name, then digest, then browser_download_url, and
# none of the objects inside a release has another "name" key; a string's own commas and quotes stay escaped.
#   json_field file tag_name         -> the release's tag
#   json_field file digest <asset>   -> that asset's digest (sha256:...), or nothing
#   json_field file url <asset>      -> that asset's download address, or nothing
json_field() {
    awk -v what="$2" -v want="${3-}" '
        function value(s) { if (!sub(/^"[^"]*"[ \t\r\n]*:[ \t\r\n]*"/, "", s)) return ""; sub(/".*/, "", s); return s }
        BEGIN { RS = "," }
        {
            r = $0; sub(/^[ \t\r\n\[{]+/, "", r)
            if (r ~ /^"tag_name"[ \t\r\n]*:/) { if (what == "tag_name" && out == "") out = value(r) }
            else if (r ~ /^"name"[ \t\r\n]*:/) name = value(r)
            else if (r ~ /^"digest"[ \t\r\n]*:/) { if (what == "digest" && name == want) out = value(r) }
            else if (r ~ /^"browser_download_url"[ \t\r\n]*:/) { if (what == "url" && name == want) out = value(r) }
        }
        END { print out }' "$1"
}

# A download must come from github.com over https, as in the app; a test stand-in may also serve from this machine.
check_url() {
    case "$1" in
        https://github.com/*) return 0 ;;
        http://127.0.0.1[:/]* | http://localhost[:/]* | https://127.0.0.1[:/]* | https://localhost[:/]*)
            [ -n "${LLAMATREE_UPDATE_URL-}" ] && return 0 ;;
    esac
    die "The download address isn't a GitHub download, so it won't be used: $1"
}

# Sectigo Public Code Signing Root R46 (SHA-1 CC:BB:F9:E1:48:5A:F6:3C:E4:7A:BF:8E:9E:64:8C:25:04:FC:31:9D, valid to
# 2046), which our code signing certificate chains to: the certificate Publisher.cs in the app pins. It's the only
# authority SHA256SUMS.p7s is checked against, never this system's own roots.
r46_pem() {
    cat <<'PEM'
-----BEGIN CERTIFICATE-----
MIIFeDCCA2CgAwIBAgIQSyw7AQGLrSq8jHtbPu2QVzANBgkqhkiG9w0BAQwFADBW
MQswCQYDVQQGEwJHQjEYMBYGA1UEChMPU2VjdGlnbyBMaW1pdGVkMS0wKwYDVQQD
EyRTZWN0aWdvIFB1YmxpYyBDb2RlIFNpZ25pbmcgUm9vdCBSNDYwHhcNMjEwMzIy
MDAwMDAwWhcNNDYwMzIxMjM1OTU5WjBWMQswCQYDVQQGEwJHQjEYMBYGA1UEChMP
U2VjdGlnbyBMaW1pdGVkMS0wKwYDVQQDEyRTZWN0aWdvIFB1YmxpYyBDb2RlIFNp
Z25pbmcgUm9vdCBSNDYwggIiMA0GCSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQCN
55QSIgQkdC7/FiMCkoq2rjaFrEfUI5ErPtx94jGgUW+shJHjUoq14pbe0IdjJImK
/+8Skzt9u7aKvb0Ffyeba2XTpQxpsbxJOZrxbW6q5KCDJ9qaDStQ6Utbs7hkNqR+
Sj2pcaths3OzPAsM79szV+W+NDfjlxtd/R8SPYIDdub7P2bSlDFp+m2zNKzBenjc
klDyZMeqLQSrw2rq4C+np9xu1+j/2iGrQL+57g2extmeme/G3h+pDHazJyCh1rr9
gOcB0u/rgimVcI3/uxXP/tEPNqIuTzKQdEZrRzUTdwUzT2MuuC3hv2WnBGsY2HH6
zAjybYmZELGt2z4s5KoYsMYHAXVn3m3pY2MeNn9pib6qRT5uWl+PoVvLnTCGMOgD
s0DGDQ84zWeoU4j6uDBl+m/H5x2xg3RpPqzEaDux5mczmrYI4IAFSEDu9oJkRqj1
c7AGlfJsZZ+/VVscnFcax3hGfHCqlBuCF6yH6bbJDoEcQNYWFyn8XJwYK+pF9e+9
1WdPKF4F7pBMeufG9ND8+s0+MkYTIDaKBOq3qgdGnA2TOglmmVhcKaO5DKYwODzQ
RjY1fJy67sPV+Qp2+n4FG0DKkjXp1XrRtX8ArqmQqsV/AZwQsRb8zG4Y3G9i/qZQ
p7h7uJ0VP/4gDHXIIloTlRmQAOka1cKG8eOO7F/05QIDAQABo0IwQDAdBgNVHQ4E
FgQUMuuSmv81lkgvKEBCcCA2kVwXheYwDgYDVR0PAQH/BAQDAgGGMA8GA1UdEwEB
/wQFMAMBAf8wDQYJKoZIhvcNAQEMBQADggIBAHZlwuPXIkrXHYle/2lexhQCTXOm
zc0oyrA36r+nySGqql/av/aDbNCA0QpcAKTL88w5D55BcYjVPOiKe4wXI/fKNHSR
bAauUD8AWbImPDwXg1cDPi3RGj3UzwdUskMLUnKoiPXEF/Jv0Vil0WjkPZgIGO42
9EhImvpUcPCI1HAWMEJJ0Nk/dUtFcdiuorthDoiFUFe5uhErNikfjyBynlyeidGC
2kWNapnahHFrM6UQu3nwl/Z0gaA/V8eGjDCMDjiVrgHGHqvcqB9vL9f/dh6uF3Nt
5bl1s2EGqJUzwk5vsjfylb6FVBK5yL1iQnb3Kvz1NzEDJlf+0ebb8BYCcoOMCLOE
rKnkB/ihiMQTWlBHVEKm7dBBNCyYsT6iNKEMXb2s9395p79tDFYyhRtLl7jhrOSk
PHHxo+FOY9b0Rrr1CwjhYzztolkvCtQsayOinqFN7tESzRgzUO1Bbst/PUFgC2ML
ePV170MVtzYLEK/cXBipmNk22R3YhLMGioLjexskp0LO7g8+VlwyfexL3lYrOzu6
+XpY0FG2bNb2WKJSJHpEhqEcYD9J0/z6+YQcBcI0v+Lm8RkqmS9WVzWctfUHw0Yv
3jg9GQ37o/HfE57nqXJYMa+96trX1m13MzOO9Kz9wb9Jh9JwBWd0Bqb2eEAtFgSR
Dx/TFsS4ehcNJMmy
-----END CERTIFICATE-----
PEM
}

# Who we are, as Publisher.cs pins it: O=, serialNumber= (the registration number) and jurisdictionC=
# (jurisdictionCountryName), as openssl names them.
OUR_NAME="Llamachant Technology Ltd"
OUR_NUMBER=2469513
OUR_COUNTRY=CA

# Whether a certificate (PEM) is ours: for code signing, and naming us by each of those fields, each there once (a field
# in a multi-valued name never matches). Says why not, on stderr.
is_ours() {   # certificate
    subject=$(openssl x509 -in "$1" -noout -subject -nameopt RFC2253) || return 1
    subject=${subject#subject=}
    subject=${subject# }
    # One field per line: split at the commas between fields (one inside a value comes escaped, as \,).
    fields=$(printf '%s\n' "$subject" | awk '{
        f = ""
        for (i = 1; i <= length($0); i++) {
            c = substr($0, i, 1)
            if (c == "\\") { f = f c substr($0, i + 1, 1); i++ }
            else if (c == ",") { print f; f = "" }
            else f = f c
        }
        print f }')
    for want in "O=$OUR_NAME" "serialNumber=$OUR_NUMBER" "jurisdictionC=$OUR_COUNTRY"; do
        if [ "$(printf '%s\n' "$fields" | grep -c "^${want%%=*}=")" != 1 ] || ! printf '%s\n' "$fields" | grep -qxF "$want"; then
            warn "SHA256SUMS is signed by someone else ($subject)."
            return 1
        fi
    done
    if ! openssl x509 -in "$1" -noout -text | awk '/X509v3 Extended Key Usage/ { getline; print }' | grep -q 'Code Signing'; then
        warn "SHA256SUMS's signing certificate isn't for code signing ($subject)."
        return 1
    fi
}

# Whether $2 (SHA256SUMS.p7s) is our signature of $1 (SHA256SUMS), exactly as it is: a valid detached CMS signature,
# with one signer, whose certificate chains to R46 and is_ours. Says why not, on stderr.
#   -no-CApath (and -no-CAstore, where openssl has stores): R46 alone is trusted, not the system's roots.
#   -purpose any: openssl's default purpose is S/MIME, which a code signing certificate isn't; is_ours checks it's
#     for code signing instead.
#   -no_check_time: the app judges the signature as of when it was signed, so a release stays installable after the
#     certificate that signed it expires; here, as of no particular time.
verify_sums() {   # sums signature
    r46_pem >"$tmp/r46.pem"
    rm -f "$tmp/signer.pem"
    stores=-no-CApath
    if openssl cms -help 2>&1 | grep -q -- '-no-CAstore'; then stores="$stores -no-CAstore"; fi
    # shellcheck disable=SC2086 # $stores is one or two options
    if ! openssl cms -verify -binary -inform DER -in "$2" -content "$1" -CAfile "$tmp/r46.pem" $stores \
            -purpose any -no_check_time -signer "$tmp/signer.pem" -out /dev/null 2>"$tmp/openssl.log"; then
        warn "SHA256SUMS.p7s isn't a valid signature of SHA256SUMS by a certificate from Sectigo Public Code Signing Root R46:"
        head -n 3 "$tmp/openssl.log" >&2
        return 1
    fi
    [ "$(grep -c 'BEGIN CERTIFICATE' "$tmp/signer.pem")" = 1 ] || { warn "SHA256SUMS.p7s must have exactly one signer."; return 1; }
    is_ours "$tmp/signer.pem"
}

# The SHA-256 the signed SHA256SUMS ($1) gives for $2 ("<hash>  <name>", as sha256sum writes it), when it lists it once.
signed_sum() {   # sums name
    awk -v name="$2" 'substr($0, 65) == "  " name && substr($0, 1, 64) ~ /^[0-9a-f]+$/ { n++; h = substr($0, 1, 64) }
        END { if (n == 1) print h }' "$1"
}

# Whether the file ($2) is the release's $1 by the signed SHA256SUMS ($signed_sums, once fetch_signed_sums has checked it).
check_signed() {   # name file
    [ -n "$signed_sums" ] || return 0   # LLAMATREE_INSECURE_SKIP_SIGNATURE, with no openssl
    signed=$(signed_sum "$signed_sums" "$1")
    [ -n "$signed" ] || die "The release's signed SHA256SUMS doesn't list $1, so it wasn't installed."
    actual=$(sha256 "$2")
    [ "$actual" = "$signed" ] ||
        die "$1 doesn't match its checksum in the release's signed SHA256SUMS (got $actual, signed $signed), so it wasn't installed."
}

# Downloads the release's SHA256SUMS and SHA256SUMS.p7s into $tmp and checks the signature, setting $signed_sums.
# Fails when the release has none (before 1.3.1), or the signature isn't ours.
fetch_signed_sums() {
    sums_url=$(json_field "$json" url SHA256SUMS)
    sig_url=$(json_field "$json" url SHA256SUMS.p7s)
    [ -n "$sums_url" ] && [ -n "$sig_url" ] ||
        die "LlamaTree $version's release has no signed checksums (SHA256SUMS and SHA256SUMS.p7s), so its download can't be checked and won't be installed. See $RELEASES"
    signed_sums=""
    if ! have openssl; then
        if [ "${LLAMATREE_INSECURE_SKIP_SIGNATURE-}" != 1 ]; then
            warn "Checking that the download is ours needs openssl, which isn't installed here, so nothing was installed."
            warn "Install it (Debian/Ubuntu: sudo apt-get install openssl; Fedora: sudo dnf install openssl; openSUSE: sudo zypper install openssl) and run this again."
            die "Or, to install on GitHub's checksum alone, which anyone who can publish there could fake, run it with LLAMATREE_INSECURE_SKIP_SIGNATURE=1."
        fi
        warn "WARNING: openssl isn't installed, so the release's signature is NOT checked (LLAMATREE_INSECURE_SKIP_SIGNATURE=1)."
        warn "WARNING: installing on GitHub's checksum alone."
        return 0
    fi
    check_url "$sums_url"
    check_url "$sig_url"
    download "$sums_url" "$tmp/SHA256SUMS" || die "Couldn't download $sums_url"
    download "$sig_url" "$tmp/SHA256SUMS.p7s" || die "Couldn't download $sig_url"
    verify_sums "$tmp/SHA256SUMS" "$tmp/SHA256SUMS.p7s" ||
        die "The release's checksums aren't signed by Llamachant Technology Ltd, so nothing was installed."
    signed_sums="$tmp/SHA256SUMS"
}

# Downloads a release asset into $tmp and checks it against GitHub's digest and its line in the signed SHA256SUMS.
# Prints the file's path.
fetch_asset() {
    url=$(json_field "$json" url "$1")
    digest=$(json_field "$json" digest "$1")
    [ -n "$url" ] || return 1
    case "$digest" in
        sha256:*) expected=$(printf '%s' "${digest#sha256:}" | tr 'A-F' 'a-f') ;;
        *) die "The release doesn't publish a checksum for $1, so it won't be installed. Download it from $RELEASES instead." ;;
    esac
    check_url "$url"
    say "Downloading $1..." >&2
    download "$url" "$tmp/$1" || die "Couldn't download $url"
    actual=$(sha256 "$tmp/$1")
    [ "$actual" = "$expected" ] || die "$1 doesn't match the checksum GitHub publishes for it (got $actual, expected $expected), so it wasn't installed."
    check_signed "$1" "$tmp/$1"
    printf '%s\n' "$tmp/$1"
}

# Whether a terminal is there to ask for a password in: under `curl | sh` the script itself is on stdin.
have_tty() { [ -t 1 ] || [ -t 2 ]; } 2>/dev/null
can_open_tty() { (: </dev/tty) 2>/dev/null; }

# Sets $sudo to what runs a command as root ("" for root itself), or fails when that can't be done here.
find_admin() {
    if [ "$(id -u)" = 0 ]; then sudo=""; return 0; fi
    have sudo || return 1
    sudo=sudo
    sudo -n true 2>/dev/null && return 0
    # sudo asks on the terminal itself, not on stdin, so a piped script can still be given the password.
    if have_tty && can_open_tty; then
        say "Installing the package needs administrator rights: sudo may ask for your password."
        # shellcheck disable=SC2024 # the terminal is this user's, on purpose
        sudo -v </dev/tty && return 0
    fi
    return 1
}

install_package() {   # kind file
    # The package manager reads the file as its own user (apt's _apt): let it in to the private download folder.
    chmod 755 "$tmp"; chmod 644 "$2"
    case "$1" in
        deb)
            $sudo env DEBIAN_FRONTEND=noninteractive apt-get update -qq </dev/null || warn "apt-get update failed; installing with the package lists there are."
            $sudo env DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "$2" </dev/null ;;
        dnf) $sudo dnf install -y "$2" </dev/null ;;
        # The package isn't signed: zypper has to be told it may install it.
        zypper) $sudo zypper --non-interactive install --allow-unsigned-rpm "$2" </dev/null ;;
    esac
}

# Unpacks the .tar.gz as ~/.local/share/LlamaTree: a folder this user owns, in one they own, which is what lets the
# app update itself (it unpacks the new version beside it and swaps the folders).
install_tar() {   # file
    data="${XDG_DATA_HOME:-$HOME/.local/share}"
    app="$data/LlamaTree"
    bin="$HOME/.local/bin"
    mkdir -p "$data" "$bin"
    # Unpacked beside $app, on the same filesystem, so the swap is two renames. The old copy waits in staging until
    # the new one is in place, and until then cleanup puts it back (restore_previous).
    staging=$(mktemp -d "$data/.llamatree-install-XXXXXX")
    previous="$staging/previous"
    mkdir "$staging/new"
    tar -xzf "$1" -C "$staging/new"
    unpacked=""
    for d in "$staging"/new/LlamaTree-*; do [ -x "$d/LlamaTree" ] && unpacked="$d"; done
    [ -n "$unpacked" ] || die "The download isn't laid out as expected, so it wasn't installed."
    if [ -e "$app" ] || [ -L "$app" ]; then mv "$app" "$previous"; fi
    mv "$unpacked" "$app"
    # The new copy is in place: from here the old one is only removed.
    rm -rf "$staging"; staging=""; previous=""

    # A script rather than a link, as in the packages, so the app always starts from its own folder.
    printf '#!/bin/sh\nexec %s "$@"\n' "$(quote "$app/LlamaTree")" >"$bin/llamatree"
    chmod 755 "$bin/llamatree"

    # The menu entry, pointed at this copy.
    if [ -f "$app/llamatree.desktop" ]; then
        mkdir -p "$data/applications"
        awk -v exec="\"$bin/llamatree\"" -v icon="$app/app.png" '
            /^Exec=/ { print "Exec=" exec; next }
            /^Icon=/ { print "Icon=" icon; next }
            { print }' "$app/llamatree.desktop" >"$data/applications/llamatree.desktop"
        have update-desktop-database && update-desktop-database -q "$data/applications" 2>/dev/null || true
    fi

    # The packages bring these with them; an unpacked copy needs them installed already.
    ldconfig=$(command -v ldconfig || { [ -x /sbin/ldconfig ] && echo /sbin/ldconfig; } || true)
    if [ -n "$ldconfig" ] && ! "$ldconfig" -p 2>/dev/null | grep -q 'libwebkit2gtk-4\.1'; then
        warn "WebKitGTK 4.1 isn't installed, and LlamaTree's window needs it, with libnotify."
        warn "Install them with your package manager (Debian/Ubuntu: libwebkit2gtk-4.1-0 libnotify4; Fedora: webkit2gtk4.1 libnotify)."
    fi
}

# After a failure or a signal mid-swap: when the old copy was moved aside and the new one never took its place, puts
# the old one back. Fails only when that can't be done, and then staging must be kept.
restore_previous() {
    [ -n "${previous-}" ] || return 0
    [ -e "$previous" ] || [ -L "$previous" ] || return 0
    [ -e "$app" ] || [ -L "$app" ] && return 0   # the new copy got in: the old one can go
    if mv "$previous" "$app"; then
        warn "The previous installation was put back in $app."
        return 0
    fi
    warn "Couldn't put the previous installation back. It's in $previous: move it to $app to use it again."
    return 1
}

# Runs on any exit, and does its work once: a second Ctrl-C or TERM meanwhile can't cut the restore short.
cleanup() {
    trap '' HUP INT TERM
    if [ -n "${staging-}" ]; then
        restore_previous && rm -rf "$staging"
        staging=""; previous=""
    fi
    [ -n "${tmp-}" ] && rm -rf "$tmp"
    tmp=""
    return 0
}

main() {
    case "$(uname -s)" in
        Linux) ;;
        Darwin)
            warn "There's no LlamaTree package for macOS yet: it's coming once the app can be signed for Apple."
            warn "Until then, build it from source: see https://github.com/$REPO/blob/main/docs/building.md#macos-and-linux"
            exit 1 ;;
        *) die "This installs LlamaTree on Linux. On Windows, run in PowerShell:  irm $RELEASES/latest/download/install.ps1 | iex" ;;
    esac

    case "$(uname -m)" in
        x86_64 | amd64) deb_arch=amd64; rpm_arch=x86_64; tar_arch=x64 ;;
        aarch64 | arm64) deb_arch=arm64; rpm_arch=aarch64; tar_arch=arm64 ;;
        *) die "There's no LlamaTree build for this processor ($(uname -m)): only x86-64 and ARM64." ;;
    esac

    tmp=$(mktemp -d "${TMPDIR:-/tmp}/llamatree-install.XXXXXX")
    staging=""
    previous=""
    trap cleanup EXIT
    trap 'exit 129' HUP
    trap 'exit 130' INT
    trap 'exit 143' TERM

    json="$tmp/release.json"
    download "${LLAMATREE_UPDATE_URL:-https://api.github.com/repos/$REPO/releases/latest}" "$json" ||
        die "Couldn't find LlamaTree's latest release on GitHub."
    tag=$(json_field "$json" tag_name)
    version=${tag#[vV]}
    [ -n "$version" ] || die "Couldn't read the latest release's version."

    # A package when there's a package manager and administrator rights; otherwise this user's own copy.
    mode="${LLAMATREE_INSTALL:-}"
    case "$mode" in "" | package | tar) ;; *) die "LLAMATREE_INSTALL is package or tar, not \"$mode\"." ;; esac
    kind=tar
    if [ "$mode" != tar ]; then
        if have apt-get; then kind=deb; asset="llamatree_${version}_${deb_arch}.deb"
        elif have dnf; then kind=dnf; asset="llamatree-${version}-1.${rpm_arch}.rpm"
        elif have zypper; then kind=zypper; asset="llamatree-${version}-1.${rpm_arch}.rpm"
        elif [ "$mode" = package ]; then die "Couldn't find apt-get, dnf or zypper to install the package with."
        else say "No apt-get, dnf or zypper here: installing for this user only."
        fi
    fi
    if [ "$kind" != tar ] && [ -z "$(json_field "$json" url "$asset")" ]; then
        [ "$mode" = package ] && die "LlamaTree $version has no $asset."
        say "LlamaTree $version has no $asset: installing for this user only."
        kind=tar
    fi
    if [ "$kind" != tar ] && ! find_admin; then
        [ "$mode" = package ] && die "Installing the package needs root or sudo."
        say "Installing for this user only: installing the package needs root or sudo."
        kind=tar
    fi

    if [ "$kind" = tar ]; then
        asset="LlamaTree-${version}-linux-${tar_arch}.tar.gz"
        [ -n "$(json_field "$json" url "$asset")" ] ||
            die "LlamaTree $version has no Linux build for this processor ($asset). See $RELEASES/latest"
    fi

    say "Installing LlamaTree $version ($asset)."
    fetch_signed_sums
    file=$(fetch_asset "$asset")

    if [ "$kind" = tar ]; then
        install_tar "$file"
        say ""
        say "LlamaTree $version is installed in $app."
        say "Start it from your applications menu, or run: llamatree"
        case ":$PATH:" in
            *":$bin:"*) ;;
            *) say ""
               say "$bin isn't on your PATH, so the llamatree command isn't found yet. Add it, for example:"
               say "  echo 'export PATH=\"\$HOME/.local/bin:\$PATH\"' >> ~/.profile   (then log out and in again)" ;;
        esac
    else
        install_package "$kind" "$file" || die "The package didn't install. To install for this user only instead, run the installer with LLAMATREE_INSTALL=tar."
        say ""
        say "LlamaTree $version is installed. Start it from your applications menu, or run: llamatree"
    fi
}

main "$@"
