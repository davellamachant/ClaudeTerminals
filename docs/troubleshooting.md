# Troubleshooting

Fixes for the problems people hit most. If yours isn't here, [open an issue](https://github.com/LlamachantTechnology/LlamaTree/issues) or email [support@llamachant.com](mailto:support@llamachant.com).

## "Windows protected your PC"

A new release can trigger SmartScreen until it builds up a reputation. Choose *More info*, check that the publisher is **Llamachant Technology Ltd**, then *Run anyway*. If the publisher is anything else, don't run it.

## "claude" or "codex" isn't found

LlamaTree runs the same commands you'd type in a terminal, so they need to be on your `PATH`. Check by opening a new terminal and running `claude --version` or `codex --version`. If you installed one while LlamaTree was open, restart LlamaTree so it picks up the new `PATH`.

On Linux and macOS, sessions get your login shell's environment (`$SHELL -l -i`, read again after a minute), so anything your shell profile adds to `PATH` is found even when LlamaTree is started from the applications menu or the Dock. Commands run through `/bin/sh`, so write a session's command the way you'd type it in a terminal.

## A blank window on Windows 10

LlamaTree draws its window with the WebView2 runtime. Windows 11 has it built in; on Windows 10, setup offers to download it. If you skipped that, install the [Evergreen WebView2 runtime](https://developer.microsoft.com/microsoft-edge/webview2/) from Microsoft.

## Linux: it won't start from a .tar.gz

The `.deb` and `.rpm` bring their dependencies with them; an unpacked `.tar.gz` needs WebKitGTK 4.1 and libnotify. On Debian or Ubuntu:

```sh
sudo apt install libwebkit2gtk-4.1-0 libnotify4
```

Pasting images or copied files into a terminal uses `wl-paste` (Wayland) or `xclip` (X11), if they're installed.

## The pink "needs your answer" dot doesn't show

It shows for Claude Code and Codex. For other programs, they need to report it themselves with [OSC 7501](claude-and-codex.md#other-agents-the-needs-your-answer-dot). Check the palette button › **Status dots** to see each state in your chosen style.

## Can't connect to a server

- **It asks for a password, or fails at once:** LlamaTree signs in with your key or ssh-agent only. Check that `ssh user@server` works from a terminal without a password prompt.
- **Host key not known:** connect once from a terminal and accept the server's key.
- **LlamaTree isn't installed there:** use *Install LlamaTree on &lt;server&gt;…* on the session's menu, or install it by hand (see [Sessions on a server](servers.md#installing-llamatree-on-the-server)).
- **The tile says *Can't reach*:** LlamaTree keeps trying; click the server's name to try now. The sessions are still running there.
- **Sessions end when you disconnect:** the server ends your processes when you log out (systemd's `KillUserProcesses`). Run `loginctl enable-linger` on the server once.

## No pull request chip

The chip needs a credential for the remote in Git Credential Manager. Looking up the pull request never opens a sign-in window, so without a stored credential there's simply no chip. Run `git fetch` in that repository from a terminal once, sign in when asked, and the chip appears within a few minutes.

## Moving or resetting your data

Everything is in `%APPDATA%\LlamaTree` on Windows, `~/.config/LlamaTree` on Linux and `~/Library/Application Support/LlamaTree` on macOS. Set the `LLAMATREE_DATA` environment variable to use another folder, for example a separate profile for testing. Your agents' conversations are kept by Claude Code and Codex themselves, not by LlamaTree.

## Uninstalling

- **Windows:** Settings › Apps › LlamaTree › Uninstall.
- **Linux:** `sudo apt remove llamatree` or `sudo dnf remove llamatree`; for a `.tar.gz`, delete its folder.

Your sessions and settings stay in the data folder above, in case you come back. Delete it to remove them too.
