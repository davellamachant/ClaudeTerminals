#!/bin/sh
# Installs LlamaTree on Linux from the latest GitHub release:
#
#   curl -fsSL https://github.com/LlamachantTechnology/LlamaTree/releases/latest/download/install.sh | sh
#
# With apt-get, dnf or zypper and administrator rights (root, or sudo: it asks for the password when there's a
# terminal to ask in), it installs the .deb or .rpm, which brings WebKitGTK and libnotify with it. Otherwise it unpacks
# the .tar.gz into ~/.local/share/LlamaTree with a `llamatree` command in ~/.local/bin; that copy updates itself too.
# Each download is checked against the SHA-256 GitHub publishes for it (the in-app updater checks the release's signed
# SHA256SUMS as well).
#
#   LLAMATREE_INSTALL=tar      install for this user only, from the .tar.gz, even when a package could be installed
#   LLAMATREE_INSTALL=package  install the package or stop: never fall back to the .tar.gz
#   LLAMATREE_UPDATE_URL       a stand-in for the GitHub API's latest release, for tests (downloads may then be local)
#
# POSIX sh, so it runs under `curl | sh` on dash, busybox and bash alike; needs curl or wget, and no jq.
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

# Downloads a release asset into $tmp and checks it against GitHub's digest. Prints the file's path.
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
    staging=$(mktemp -d "$data/.llamatree-install-XXXXXX")
    tar -xzf "$1" -C "$staging"
    unpacked=""
    for d in "$staging"/LlamaTree-*; do [ -x "$d/LlamaTree" ] && unpacked="$d"; done
    [ -n "$unpacked" ] || die "The download isn't laid out as expected, so it wasn't installed."
    if [ -e "$app" ]; then mv "$app" "$staging/previous"; fi
    mv "$unpacked" "$app"
    rm -rf "$staging"; staging=""

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

cleanup() {
    [ -n "${tmp-}" ] && rm -rf "$tmp"
    [ -n "${staging-}" ] && rm -rf "$staging"
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
    trap cleanup EXIT
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
