# Privacy and network

LlamaTree runs on your machine and has no account. Your code, prompts and conversations stay between you and the agents you run, exactly as they would in any terminal. This page lists everything LlamaTree itself connects to.

## What LlamaTree connects to

| What | Where | What's sent |
|---|---|---|
| Update check, at start and every 15 minutes | GitHub's API for this repository's latest release | An anonymous request. Nothing about you or your sessions. |
| Downloading an update, when you click Update | GitHub releases | An anonymous download, checked against GitHub's SHA-256 before it runs. |
| Tips, offers and news, at start and every 30 minutes | `downloads.llamachant.com` (always that address) | An anonymous request. Only notices signed with our code-signing certificate are shown. Tips and offers can be turned off. |
| Sessions on a server, when you connect to one | The server you name, through your own `ssh` | Whatever the server's sessions need: their output, your typing, files you open or paste. Nothing goes through Llamachant. |
| Pull request chip | GitHub or Azure DevOps, for a branch's own remote | The credential Git Credential Manager already holds for that remote, over HTTPS, to that service only. It never opens a sign-in window. Cached for 3 minutes. |

Fetch, pull and push are ordinary Git commands you run. **Suggest** runs your own Claude Code or Codex with the diff, as described in [Git and diffs](git.md#suggest).

The update check can be turned off: click the version number at the top right and untick *Check for updates automatically*. *Show tips and offers*, in the same menu, turns tips and offers off; news still shows.

> [!NOTE]
> Tips, offers and news show only when the file they come in carries a valid signature by the certificate LlamaTree's installer is signed with, issued to Llamachant Technology Ltd. Whoever can change the file in storage, or sits between storage and the app, still can't put words in the banner.

## On your machine

- **The MCP server** for panels and messages listens on `127.0.0.1` only, at a port picked when the app starts. Every session has its own token, which keeps other programs out.
- **The conversation rail** reads the agents' own saved transcripts from disk. Nothing is sent anywhere.
- **Your data** (sessions, panels, message rules, themes, plugins) is in `%APPDATA%\LlamaTree` on Windows, `~/.config/LlamaTree` on Linux and `~/Library/Application Support/LlamaTree` on macOS.

## This website

llamatree.dev sets no cookies and has no analytics. To show the latest version, download links and star count, your browser asks GitHub's public API directly, and remembers the answer for an hour in local storage.

## Questions

Email [support@llamachant.com](mailto:support@llamachant.com). The source is all on [GitHub](https://github.com/LlamachantTechnology/LlamaTree), if you'd rather check for yourself.
