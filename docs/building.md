# Building LlamaTree

How to build, run, test, package and release LlamaTree from source, and how the app is put together. This page is for people working on LlamaTree itself; to install and use it, start with [Getting started](getting-started.md).

## Building

```powershell
cd web; npm ci; cd ..                                   # once: xterm.js + Monaco
dotnet run --project src\LlamaTree -f net10.0-windows   # the Windows app
dotnet test                                             # tests
```

On macOS and Linux:

```sh
(cd web && npm ci)                  # once: xterm.js + Monaco
dotnet run --project src/LlamaTree
dotnet test
```

- **Host:** one app with two windows (`src/LlamaTree`). `net10.0-windows` is the Windows app: WinForms + WebView2 (`Hosting/WinForms`). `net10.0` is the macOS and Linux app: a [Photino](https://www.tryphotino.io/) window, which hosts WKWebView on macOS and WebKitGTK on Linux (`Hosting/Photino`); it also runs on Windows (`-f net10.0`, on WebView2) for trying it out. Everything else is shared: `AppHost` holds the sessions, git, panels and plugins, and talks to the same page through either window.
- **Session host:** `AppHost` runs in a process of its own, the same program started with `--host` (`Hosting/SessionHost.cs`), so sessions can outlive the window. The window starts it if it isn't running and connects through a named pipe (Windows) or a Unix socket (macOS, Linux; in `$XDG_RUNTIME_DIR` or the per-user temp folder) that only the same user can open, one per profile (`Hosting/Link`). The page's messages pass through unchanged; the folder picker, clipboard, title bar colours and plugins' pages stay with the window (`Hosting/HostLink.cs`). The host keeps the end of each session's output for a window that attaches later, and ends once no window is attached and no session is running. An update waits for both processes to end; the installer checks for either (`AppMutex`).
- **Sessions on servers among your own:** this computer's session host keeps an ssh connection to each server with sessions here (`Hosting/ServerConnection.cs`) and is a window to that server's host: what it tells its page about a session is passed to this page under the session's id here, and requests about the session go there under its id there; a request naming a server instead (adding sessions, browsing its folders, its saved conversations) goes to that server (`AppHost.Servers.cs`). `sessions.json` keeps each server session's place, project, and what it was last seen as (`Server`, `RemoteId`). While connected, the server's host passes its sessions' tool calls (panels, messages) up to this host, which answers them as for its own sessions; without it the server answers them itself.
- **Sessions on a server:** a window started with `--remote <target>` runs `ssh -T -o BatchMode=yes <target> llamatree --attach` and speaks the same protocol over ssh's input and output. On the server, `--attach` (`Hosting/HostBridge.cs`) connects to that user's session host there, starting it detached if needed, and copies between the two; when SSH drops only the bridge ends. The host's lock is per user across login sessions on macOS and Linux (`Hosting/InstanceLock.cs`), and it listens again if its socket file is removed (Linux empties `$XDG_RUNTIME_DIR` when the last login ends). Told the window is remote, the host skips update checks and opens nothing on its own desktop: a folder opens in the page's folder browser (`fs.list`), and a file is sent to the window in pieces (`Hosting/ServerFiles.cs`, `ServerCopies.cs`), which opens its copy. Pasted files go the other way (`upload.*`, the window waiting every few pieces for the host to catch up), and a plugin's folder is sent ahead of the page hearing of it, so the window serves the copy as it would a local folder. `LLAMATREE_SSH` names another ssh program; `LLAMATREE_REMOTE_COMMAND` replaces `llamatree --attach`.
- **Terminals:** real pseudo terminals rendered with xterm.js: ConPTY on Windows, where child processes live in a kill-on-close job object; a Unix pty on macOS and Linux (`UnixPtyProcess`, started with posix_spawn in a session of its own), where closing a session hangs up its terminal.
- **Diffs:** Monaco diff editor, with dark and light themes after Visual Studio's.

## macOS and Linux

Run from source as above; [GitHub Actions](../.github/workflows/build.yml) also builds and tests every push on Windows, Linux and macOS and keeps a self-contained build of each (`LlamaTree-osx-arm64`, `-osx-x64`, `-linux-x64`) on the run for two weeks. Releases carry Linux packages (see [Linux](getting-started.md#linux)), made by [`tools/package-linux.cs`](../tools/package-linux.cs) from `publish.ps1` (on Windows, with no Linux tools); there's no macOS app bundle yet, and a macOS copy doesn't update itself: *Check for updates* says when a new version is out and links its release.

- **Linux** needs WebKitGTK 4.1 and libnotify (Debian/Ubuntu: `sudo apt install libwebkit2gtk-4.1-0 libnotify4`). Pasting an image or copied files into a terminal uses `wl-paste` (Wayland) or `xclip` (X11) if they're installed.
- **Sessions get your login shell's environment** (`$SHELL -l -i`, read again after a minute), so `claude`, `codex`, `node` and `git` are found as in a terminal even when the app is started from the Dock or a desktop menu. Commands run through `/bin/sh`, so a session's command is written the way you'd type it in a terminal.
- **Keys:** on a Mac, ⌘ does what Ctrl does on Windows (⌘1…⌘9, ⌘⇧G, ⌘⇧N, ⌘W, ⌘C/⌘V in the terminal); Ctrl+C and the other Ctrl keys go to the terminal as themselves.
- **Data** lives in `~/Library/Application Support/LlamaTree` (macOS) or `~/.config/LlamaTree` (Linux); `LLAMATREE_DATA` picks another folder, as on Windows.
- **The page and plugins** are served from `http://127.0.0.1:<port>/` for this run only (WebView2's virtual hosts don't exist elsewhere). The window's message bridge is reachable from every frame, so the host only acts on messages carrying a token it gives the top-level page; each plugin's files are under a random path made for the run, so one plugin can't load another's page.
- **Undo Changes** moves files to the Trash (Linux: `gio trash`, else the freedesktop.org trash folder).

## Installer

```powershell
winget install JRSoftware.InnoSetup   # once
.\publish.ps1                         # -> artifacts\LlamaTree-Setup-<version>.exe
.\publish.ps1 -SkipInstaller          # -> artifacts\publish\ (portable folder only)
```

- Self-contained x64 build (no .NET install needed). The version comes from `<Version>` in `LlamaTree.csproj`; bump it for each release.
- Installs per-user to `%LOCALAPPDATA%\Programs\LlamaTree` (no admin), with a Start Menu shortcut, an optional desktop shortcut and an uninstaller. Admins can choose an all-users install.
- Upgrading: run the newer setup. It asks to close a running copy first. Saved sessions are kept; uninstalling also keeps them in `%APPDATA%\LlamaTree`.
- Setup warns and links to the download if the WebView2 Runtime is missing (it's built into Windows 11).
- Local builds aren't code-signed, so on other PCs SmartScreen shows "unknown publisher" (More info › Run anyway).
- The icon is generated by `tools\make-icon.ps1`.

## Releasing

Signed releases are built by `azure-pipelines.yml` on the Azure DevOps agent with the company's code signing token:

1. Bump `<Version>` in `LlamaTree.csproj` and merge to `main`.
2. In Azure DevOps, run the pipeline on `main` (or on a tag `v<version>`) with *Create the GitHub release* ticked. Untick it for a trial run that only builds and signs, keeping the installer on the run's Artifacts.
3. The pipeline runs `publish.ps1 -Sign`, which signs `LlamaTree.exe`/`.dll`, setup and its uninstaller as *Llamachant Technology Ltd* (picked from the certificate store by subject, as the NuGet signing does), timestamps them with Sectigo's server, and fails unless each carries a valid, timestamped signature from that publisher. It then creates a **draft** GitHub release with the installer, the Linux packages and the two install scripts from `tools/install` (which the one-line installs fetch from the latest release, so keep their names).
4. Write the release notes on the draft and publish it: that's what makes the in-app updater offer the new version. The updater shows everything above the notes' *Install* heading, and needs the installer named `LlamaTree-Setup-<version>.exe` on a `v<version>` tag (Linux copies update from the `.deb`, `.rpm` and `.tar.gz` under the names `publish.ps1` gives them).
