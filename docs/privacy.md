# Privacy and network

LlamaTree runs on your machine and has no account. Your code, prompts and conversations stay between you and the agents you run, exactly as they would in any terminal. This page lists everything LlamaTree itself connects to.

## What LlamaTree connects to

| What | Where | What's sent |
|---|---|---|
| Update check, at start and every 15 minutes | GitHub's API for this repository's latest release | An anonymous request. Nothing about you or your sessions. |
| Downloading an update, when you click Update | GitHub releases | An anonymous download, checked against GitHub's SHA-256 before it runs. |
| Tips, offers and news, at start and every 30 minutes | `downloads.llamachant.com` (always that address) | An anonymous request. Only notices signed with our code-signing certificate are shown. Tips and offers can be turned off. |
| The count of users, once an hour while it's open, **only if you've said yes** | `api.llamatree.dev` (always that address) | Six things: a random id that stays the same, the app's version, the operating system's name and version, and how many projects and sessions are open. Only the numbers. See [Counting users](#counting-users). |
| Sessions on a server, when you connect to one | The server you name, through your own `ssh` | Whatever the server's sessions need: their output, your typing, files you open or paste. Nothing goes through Llamachant. |
| Pull request chip | GitHub or Azure DevOps, for a branch's own remote | The credential Git Credential Manager already holds for that remote, over HTTPS, to that service only. It never opens a sign-in window. Cached for 3 minutes. |

Fetch, pull and push are ordinary Git commands you run. **Suggest** runs your own Claude Code or Codex with the diff, as described in [Git and diffs](git.md#suggest).

The update check can be turned off: click the version number at the top right and untick *Check for updates automatically*. *Show tips and offers*, in the same menu, turns tips and offers off; news still shows.

> [!NOTE]
> Tips, offers and news show only when the file they come in carries a valid signature by the certificate LlamaTree's installer is signed with, issued to Llamachant Technology Ltd. Whoever can change the file in storage, or sits between storage and the app, still can't put words in the banner.

## Counting users

We'd like to know how many people use LlamaTree, on which versions and systems, and how much. So the first time it runs, a line under the title bar asks whether it may count you. Nothing is sent until you say yes, and *No thanks* is remembered as firmly as yes. The switch is in the version menu, *Count me among the users*, with *What the count sends…* beside it.

When it's on, once an hour while the app is open (and once straight away when you say yes) it makes one request, `GET https://api.llamatree.dev/count/ping`, whose query string says six things:

- `id`: a random id (32 hex characters) made when you said yes;
- `v`: the app's version;
- `os` and `osv`: the operating system's name (`windows`, `macos` or `linux`) and version;
- `projects` and `sessions`: how many projects and sessions are open.

**Only the numbers**: never the names of projects or sessions, their folders, branches, prompts, conversations, code or anything else in them, and nothing that says who you are. Nothing is read back. The request's `User-Agent` is `LlamaTree/<version>`.

**The id stays the same.** It's the same in every request until you turn the count off: that's what lets a copy count once an hour however often it asks, and once a day or a month however many hours it's open. It also means the count shows which hours each copy was open. And like any server, ours sees the IP address each request comes from, so the id and the address arrive together: whoever runs the server could tell when an install at that address is running. The count service ([`server/`](../server)) doesn't keep the address. It keeps one row per id per hour (UTC) with the other five things, for 400 days, deleting older rows every day, and not the IP address, the user agent or anything else about the request.

Turning it off throws the id away on your machine, so turning it on again later starts over with a new id, as a new user, which can't be joined to the old one. Rows already recorded under the old id stay until they're 400 days old. The answer and the id are kept in `telemetry.json` beside `sessions.json`.

## On your machine

- **The MCP server** for panels and messages listens on `127.0.0.1` only, at a port picked when the app starts. Every session has its own token, which keeps other programs out.
- **The conversation rail** reads the agents' own saved transcripts from disk. Nothing is sent anywhere.
- **Your data** (sessions, panels, message rules, themes, plugins) is in `%APPDATA%\LlamaTree` on Windows, `~/.config/LlamaTree` on Linux and `~/Library/Application Support/LlamaTree` on macOS.

## This website

llamatree.dev sets no cookies and has no analytics. To show the latest version, download links and star count, your browser asks GitHub's public API directly, and remembers the answer for an hour in local storage.

## Questions

Email [support@llamachant.com](mailto:support@llamachant.com). The source is all on [GitHub](https://github.com/LlamachantTechnology/LlamaTree), if you'd rather check for yourself.
