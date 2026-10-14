# ClaudeTerminals — whole-repo defect review (2026-10-14)

Collaborator run `88f911` · snapshot `61d45db7af2b7bbd4fe5f601799cc09d58e5b0b3` (clean tree) · analysts: Claude (Opus 5.5) and Codex (gpt-6-astra, codex-cli 0.162.0)

## 0. Combined answer

There is no single root cause: the review found ~44 independent defects plus one open requirement question, every one confirmed present in the source by both analysts, none reproduced (nothing was built or run). The six that matter most are reachable in ordinary use: **Shift+Enter submits the Claude prompt instead of adding a newline**; **"Open"/"Open file" executes the file** (`.cmd`, `.js`, `.exe`…) rather than opening it; **dropping a file or link onto the window navigates the WebView away**, hiding all sessions and handing the host bridge to whatever page loads; **Undo of a staged rename can overwrite a file recreated at the original name**, and **a failed HEAD lookup during Undo sends tracked files to the Recycle Bin**; and **Stop leaves the session stuck as "running"**. Fixes are local (navigation/drop/source filtering, an explicit editor verb with path containment, cancelling the handled key events, a host-built Undo plan that refuses on lookup failure, an explicit stopped message). These fixes will not address the ~30 lower-ranked races, failure paths and UI-state bugs, which are listed with their own fixes below.

## 1. Status

- 2026-10-14 — diagnosed (sections 0–8 below).
- 2026-10-14 — **P1 fixes F1–F6 implemented in the working tree, not committed** (see "Implementation" at the end). Includes F19 (stale exit) as part of F6. The other P2/P3 findings and O1 are still open.

## 2. Root cause

No unified root cause. The defects cluster into five mechanisms: (a) the WebView host trusts whatever document is loaded and whatever path/revision it sends; (b) destructive Git operations build their plan from state that may have changed or may have failed to load; (c) process/watcher callbacks outlive the object that owns them; (d) asynchronous UI replies overwrite newer state; (e) key handlers return `false` without cancelling the browser event.

**Severity rule (so you can re-rank):** the collaborator reserves the top rank for defects proven wrong in production (a log, query or stored value disagreeing with the code). Nothing here was run, so **no finding takes that rank**; every finding is "real code path, not yet observed". Within that, **P1** = reachable in ordinary use with high impact (data loss, code execution, core feature broken); **P2** = real with moderate impact or a narrower trigger; **P3** = narrow races, native-failure paths, hardening, debt. Where the two analysts ranked differently it is noted.

### P1

| ID | Defect | Evidence | Found by |
|---|---|---|---|
| F1 | Shift+Enter submits the prompt. The handler posts `ESC CR` and returns `false` without `preventDefault`; xterm 6.0.0's `_keyDown` returns early without cancelling, the browser fires `keypress` (charCode 13), the handler returns `true` for non-keydown, and `_keyPress` sends `\r`. Contradicts README:52. | `src/ClaudeTerminals/wwwroot/app.js:523-545`; xterm 6.0.0 `CoreBrowserTerminal.ts` `_keyDown`/`_keyPress` (fetched by both; lockfile pins 6.0.0, `web/package-lock.json:51-52`) | A |
| F2 | "Open" (Git Changes menu) and "Open file" (diff toolbar) call ShellExecute with the default verb on `Path.Combine(repo, path)` with no containment check: scripts/executables Claude wrote get **run**; a rooted or `..\` path escapes the repo. | `MainForm.cs:368-373, 396-406`; `app.js:1016, 1539` | A |
| F3 | No `NavigationStarting` filter, `AllowExternalDrop` left at default (true), `e.Source` never checked, no drop/dragover handler in the page. Dropping a screenshot/file/link (a common Claude Code habit) navigates the WebView away: UI gone, sessions keep running unseen. A page that loads this way can message the host (`addSessions` with any `command`, `start`, `input`, `openFile`). | `MainForm.cs:59-75, 181-204`; no `drop` handler in `app.js` | A (B: same exposure, left as could-not-determine) |
| F4 | Staged Undo of a rename `a→b` also restores `a` from HEAD with `restore --staged --worktree`, overwriting any new `a` created since (not recycled); the confirmation doesn't mention it. More generally the host re-reads status and can execute a different plan from the one confirmed. | `Git/GitService.cs:378-397`; `app.js:1040-1076`; `MainForm.cs:349-355` | B |
| F5 | Undo treats lookup failure as absence: `HasHeadAsync` maps any non-zero exit to false → empty "in HEAD" set; `PathsInHeadAsync` ignores `r.Ok` and treats missing output lines as absent → tracked files go to `git rm --cached -f` and the Recycle Bin. | `GitService.cs:352-353, 403-413, 388-397` | B |
| F6 | Stop never produces an `exited` message (`PseudoConsoleProcess.Dispose` nulls `Exited` before terminating; JS ignores the reply; `syncSessions` ORs `running`). Tile stays "running", input is dropped, Enter-to-restart is dead. "Normally": a natural exit already in flight can race it. | `TerminalSession.cs:54-58`; `PseudoConsoleProcess.cs:163-169`; `MainForm.cs:237-239`; `app.js:157, 654` | both |

### P2

| ID | Defect | Evidence | Found by |
|---|---|---|---|
| F7 | Ctrl+V doesn't paste: handler returns `true`, xterm maps it to `0x16` and cancels the event, so the browser paste never fires (README:53, menu label :608). Whether Claude Code reads the clipboard itself on `^V` is unknown. | `app.js:523-546`; xterm 6.0.0 `Keyboard.ts` ctrl A-Z branch (verified) | A |
| F8 | New Session buttons pass the click `MouseEvent` as `mode`, so the dialog opens with no tab selected, no worktree list, worktree picks ignored, last command not saved. Ctrl+Shift+N works. | `app.js:1560-1561` vs `:725, 738, 744, 770, 779` | both |
| F9 | Unstaging a rename restores only the new path; the original's deletion stays staged and goes in with the next "Commit Staged". *B ranked High, A Low.* | `app.js:989, 1011-1030`; `MainForm.cs:333-340`; `GitService.cs:344-349` | both |
| F10 | `RunAsync` writes all stdin before draining stdout/stderr; `cat-file --batch-check` emits per line, so a large Undo can deadlock (threshold not established). | `GitService.cs:51-61, 409` | both |
| F11 | `addSessions` installs each folder before validating the next; on error nothing is saved/posted and a retry duplicates sessions (two Claude processes in one folder). Prunable worktrees are offered; worktree replies aren't tied to the current request. | `MainForm.cs:241-258`; `GitService.cs:204-224`; `app.js:735-779` | both |
| F12 | Only `.exe`/`.com` are resolved; npm installs (`claude.cmd`) fall back to `cmd /d /c <command>` in the session folder, where cmd may pick a repo-local `claude.bat`/`.cmd` first (unless `NoDefaultCurrentDirectoryInExePath` is set), and quoted paths/args are mangled by `/c` quote stripping (`/s` alone is not a full fix). | `Terminal/CommandLineResolver.cs:15-60` | both |
| F13 | Commit message box is recreated on every status render (≥ every 15 s while Claude works): caret jumps to end, selection/IME lost. A successful commit also clears a newer draft typed while it ran. | `app.js:940-966, 969-981` | A (rebuild) + B (draft) |
| F14 | `InitGitAsync` overlap (double Refresh) or session removal mid-init leaks a recursive `FileSystemWatcher` + 15 s `git status` poll for the app's lifetime. | `TerminalSession.cs:60-73, 102-108` | both |
| F15 | Exit handling closes the console, raises `Exited`, and disposal nulls `Output`/closes the stream with no reader-completion handshake, so the last error text (e.g. bad `--resume` id) can be lost. Windows 11 24H2 `ClosePseudoConsole` returns immediately, so it is no drain barrier. | `PseudoConsoleProcess.cs:110-142, 161-179`; `TerminalSession.cs:31-35` | A (B: unverified) |
| F16 | The 4 MB diff limit is checked only after `git show` output is fully buffered — large blobs are loaded whole (twice for staged/commit diffs) before "too large". | `GitService.cs:57-62, 267-270, 294` | B (A had wrongly ruled the cap fine) |
| F17 | Open diffs refresh only for the session selected in the Git panel; history isn't refreshed on status changes (README:62 promises live diffs). | `app.js:904-907, 1167-1170, 1519-1522` | both |
| F18 | Conversations under 96 KB stop scanning once cwd/prompt/branch are found, so later custom titles / last prompts are never seen in the picker. | `Sessions/ConversationIndex.cs:100-117` | B |

### P3

| ID | Defect | Evidence | Found by |
|---|---|---|---|
| F19 | Old process's exit callback fires `Exited` even when the identity check fails → a restarted process is marked exited. *B Medium, A Low.* | `TerminalSession.cs:31-35` | both |
| F20 | `GitWatcher.Kick` can call `debounce.Change` after `Dispose` → `ObjectDisposedException` on a pool thread (process exit, which the kill-on-close job turns into "all sessions die"). *B Medium, A Low.* | `GitWatcher.cs:60-63, 70-77` | both |
| F21 | `hPC` used by `Resize` without sync vs close on the exit thread; `hProcess` closed while the exit callback may use it (`Unregister(null)` doesn't wait). | `PseudoConsoleProcess.cs:104-108, 129-142, 166, 182-186` | A |
| F22 | Status-refresh coalescing can drop a request between loop exit and `statusRunning = 0`. | `TerminalSession.cs:76-99` | both |
| F23 | Job creation/config/assignment and `ResumeThread` results unchecked; a failure leaves an uncontained (or suspended) child reported as running. *B High, A Low.* | `PseudoConsoleProcess.cs:61-72, 144-151` | B |
| F24 | Terminal input is written synchronously on the UI thread; if the PTY input pipe stops draining, a large paste freezes the window. *B High, A Low.* | `MainForm.cs:222-224`; `PseudoConsoleProcess.cs:87-101` | B |
| F25 | Undo recycles files one synchronous shell call at a time on the UI thread. | `GitService.cs:395-397, 416-432` | A |
| F26 | An unreadable `sessions.json` loads as `[]` and is overwritten by the next save; null records / duplicate IDs aren't validated. | `SessionStore.cs:28-47`; `MainForm.cs:40, 100-119` | A + B |
| F27 | `FolderBrowserDialog.ShowDialog` runs synchronously inside `WebMessageReceived` (WebView2 threading guidance says defer). | `MainForm.cs:289-295` | A |
| F28 | Profile identity: mutex hashes the raw `CLAUDE_TERMINALS_DATA` string (no `GetFullPath`/trailing-separator normalisation); activation picks any same-named process; installer `AppMutex` checks only the default name. | `Program.cs:16-37`; `installer/ClaudeTerminals.iss:34-35` | both (installer gap: noted by A, raised by B) |
| F29 | ConversationIndex: a missing session folder drops the project filter (lists all projects); `FileInfo.Length` on a file deleted mid-scan fails the whole list; `"message": null` throws. | `ConversationIndex.cs:46-60, 147-154` | A |
| F30 | Bridge hardening: `sha`/`commit`/`parent` can start with `-` (git option injection); unstaged diff reads `Path.Combine(dir, path)` without containment. Matters mainly via F3. | `GitService.cs:166-172, 246-269`; `MainForm.cs:323-331` | A |
| F31 | `windowsPty.buildNumber` hard-coded to 22621; on Windows 10 (supported per README) this enables xterm reflow and bypasses its older-ConPTY wrap workarounds. Visual effect unobserved. | `app.js:206` | A (mechanism corrected by B) |
| F32 | No OSC 8 `linkHandler`; xterm's fallback (`confirm` + `window.open`) is blocked by `NewWindowRequested`. | `app.js:198-214`; `MainForm.cs:74` | A |
| F33 | Every `sessions` message re-appends all tiles, detaching the focused terminal. | `app.js:163` | A |
| F34 | A status refresh during a new diff tab's first load can carry over the previous file's view state. | `app.js:1432-1466, 1519-1522` | A |
| F35 | Resizes are dropped while start/restart is in flight and never re-sent. | `app.js:233, 255-274` | A |
| F36 | Two Enters on an exited session send two restarts; the second kills the first. | `app.js:225-228`; `MainForm.cs:230-235` | A |
| F37 | Global shortcuts stay live under a modal (Ctrl+Shift+N discards a half-filled dialog). | `app.js:498-519, 548-554` | A |
| F38 | `publish.ps1` runs `npm ci` only if `node_modules\monaco-editor` is missing → stale libraries ship after a lockfile bump. | `publish.ps1:15-20` | both |
| F39 | No `ProcessFailed` handler: a renderer crash leaves a dead page. | `MainForm.cs:50-80` | A |
| F40 | Conversation picker reads the host's `CLAUDE_CONFIG_DIR`; children get the registry environment, which drops a process-only override. | `ConversationIndex.cs:22-30`; `SessionEnvironment.cs:18-30` | B |
| F41 | A late `commitDetails` reply is assigned before the selection check, so a later render shows commit A's details under B. | `app.js:1201-1210` | B |
| F42 | Opening Git Repository before the first status arrives leaves "Loading…" until another action. | `app.js:899-907, 1162-1169` | B |
| F43 | Commit text containing `\x1e`/`\x1f` breaks log/detail framing; detail command failures unchecked. | `GitService.cs:152-159, 166-200` | B |
| F44 | Valid JSON with a non-object root or bad `reqId` throws outside the handler's `try` in an `async void`. | `MainForm.cs:184-202` | B |

### Open — owner decision needed

**O1 — Esc that closes the terminal context menu also reaches the terminal and interrupts Claude** (`app.js:548-554`). *Claude:* closing a menu shouldn't interrupt the running task. *Codex:* README:58 says "Plain Esc always goes to the terminal", so this isn't a defect without a requirement change. No executable check decides intent; pick the policy before fixing.

## 3. Ruled out

| Hypothesis | Verdict | Evidence |
|---|---|---|
| File/branch names, commit text, titles injected as HTML | No dynamic HTML injection at app call sites (all via `h()` text nodes; `innerHTML` only for static SVG). Bundled libraries not audited. | `app.js:12-25, 57-61, 1555-1556` |
| JS↔C# message names/fields mismatch | No mismatch (camelCase via `JsonSerializerDefaults.Web`). Malformed input is F44. | `MainForm.cs:17, 206-373`; `app.js:95-117` |
| Pathspec wildcard expansion on stage/unstage/undo | Ruled out: literal pathspecs, NUL-delimited stdin. | `GitService.cs:332-349` |
| Undo trusts the page's untracked/rename classification | Ruled out (host re-reads status, skips conflicts); F4/F5 remain. | `MainForm.cs:345-358` |
| `openUrl` accepts non-HTTP schemes | Ruled out (HTTP/HTTPS only). `openFile` is F2. | `MainForm.cs:306-310` |
| Save truncates `sessions.json` mid-write | Ruled out (temp file then move). F26 remains. | `SessionStore.cs:42-47` |
| Environment block malformed | Ruled out: case-insensitive keys, sorted, double-NUL, Unicode flag. | `SessionEnvironment.cs:38-44`; `PseudoConsoleProcess.cs:61-63` |
| Porcelain v2 `-z` parsing wrong | Ordinary/rename/unmerged/untracked branches correct by inspection; not fixture-tested. | `GitService.cs:81-122` |
| `ClosePseudoConsole` on the UI thread | Ruled out for the shown paths. | `PseudoConsoleProcess.cs:129-142, 173-179` |
| STARTUPINFOEX / job structs / COORD layouts | Correct by inspection; no ABI measurement. | `Native.cs` |
| Installer AppId, default mutex, WebView2 check, publish fail-fast | Present. Custom-profile mutex is F28; stale libs F38. | `installer/ClaudeTerminals.iss`; `publish.ps1` |
| **Withdrawn Claude claim:** "4 MB diff cap is fine" | Rejected — see F16. | `GitService.cs:267-270, 294` |

## 4. Proposed fix

- **F1, F7:** in `terminalKey`, call `e.preventDefault()` on every keydown it handles (suppresses the following keypress); for plain Ctrl+V return `false` on keydown *without* preventDefault so the native `paste` event reaches xterm. Verify bytes, not just behaviour.
- **F2, F30:** open with an explicit editor (`edit` verb → fallback `openas`/notepad) or `explorer /select,`; never the default verb for executables/scripts. Centralise a "path stays under repo root + `\`" check (beware reparse points) for `openFile` and working-file reads; validate revisions as hex or pass `--end-of-options`.
- **F3, F44:** `webView.AllowExternalDrop = false` (or handle drop in JS and paste paths like Windows Terminal); cancel `NavigationStarting` outside `https://app.local/`; drop messages whose `e.Source` isn't that origin; move all parsing inside the `try`.
- **F4, F5, F9, F10:** build one host-side Undo plan (both rename paths, exact restore/recycle actions), show *that* plan, and re-validate before executing; refuse on any failed/incomplete HEAD or `cat-file` result (distinguish a verified unborn branch); refuse when a rename's original path is occupied; include `OrigPath` on every unstage route; start stdout/stderr reads before writing stdin.
- **F6, F19, F35, F36:** give each process a generation; post `{type:"exited"}` from Stop (or raise it from `TerminalSession.Stop`); only raise `Exited` when the identity check succeeds; make `syncSessions` trust `info.running`; add a `restarting` guard; re-send the current size after start resolves.
- **F8:** `() => showAddDialog()` on both buttons.
- **F11:** validate every folder first, then add; filter `prunable`; tie worktree replies to the request.
- **F12:** resolve `.cmd`/`.bat` via PATH+PATHEXT to an absolute path and launch `"%ComSpec%" /d /s /c ""<full path>" <args>"`, or set `NoDefaultCurrentDirectoryInExePath=1` in the child environment; test quoting with spaces/quotes/metacharacters.
- **F13:** keep the textarea element across renders; clear the draft only if it still equals what was submitted.
- **F14, F20, F22:** disposed flag + init generation in `TerminalSession`; dispose superseded watchers; guard `debounce.Change` against disposal; re-check `statusDirty` after clearing `statusRunning`.
- **F15, F21:** raise `Exited` only after the reader hits EOF; don't clear `Output` until then; lock `hPC` between Resize and close; own `hProcess` with a SafeHandle and unregister with a wait handle.
- **F16:** `git cat-file -s` before `git show`, and cap the stream while reading.
- Remaining P3 items: fixes as listed in each row's description (one-liners: `Task.Yield()` before the folder dialog; `File.Replace`/rename unreadable `sessions.json` to `.bad`; `GetFullPath` + trim for the mutex; real OS build to xterm; `linkHandler` → `openUrl`; append only new tiles; check job/resume results; off-UI-thread input writer and recycling; always `npm ci` in `publish.ps1`; `ProcessFailed` → `Reload()` and resync via `ready`).

**Design rules the next fix must respect:** establish sender trust, valid input, complete lookup results and the confirmed plan *before* any destructive step; drain subprocess streams concurrently; deliver final terminal output before the exit notification; check ownership (process generation, init generation, request identity) before mutating shared state; Stop/dispose idempotent and one pending restart = one replacement; never silently replace unreadable saved state; keep literal pathspecs and HTTP(S)-only `openUrl`; apply the O1 decision explicitly rather than by accident.

## 5. Deployment

Single project `src/ClaudeTerminals` (v1.0.2, `net10.0-windows`, WebView2 SDK 1.0.4258.31, xterm 6.0.0, Monaco 0.57.0). Released via `publish.ps1` (Release/win-x64 self-contained) → Inno Setup installer. Host fixes need a new executable; front-end fixes need the published `wwwroot`. Bump `<Version>` per README. No database, schema, mirror copy, pay window or other consumer — not applicable. Which build users are running is unknown.

## 6. Verification plan

Run in a dev build against a disposable repo; each check names what it proves.
1. **F1:** log `onData` bytes for one physical Shift+Enter — must be exactly `1b 0d`.
2. **F7:** Ctrl+V with text on the clipboard — the text arrives via xterm's paste, no `0x16`.
3. **F2:** "Open" on a harmless `hello.cmd` that writes a sentinel file — the sentinel must not appear; a crafted `..\..\` path must be refused.
4. **F3:** drop a PNG and a local HTML file on the window — no navigation; a message from a non-app origin is ignored.
5. **F4:** stage `git mv a b`, recreate `a` with distinct content, Undo the rename — must refuse or preserve the new `a`.
6. **F5:** make `rev-parse HEAD` / `cat-file` fail (e.g. wrapper on PATH) during staged Undo — zero mutations.
7. **F9:** unstage a staged rename via file, folder and section actions — both index entries restored.
8. **F10:** staged Undo of ~2,000 files — completes.
9. **F6:** Stop a running session — tile shows exited; Enter restarts it.
10. **F8, F11, F13, F14, F15, F16:** each row's scenario as written above.
11. **Regression sensitivity:** for each fix, revert it and confirm its check fails, then restore byte-identical.

## 7. Contribution log

**What Claude did.** Two read-only subagents read every tracked file (host: Terminal/Sessions/Git/MainForm/Program; front end + packaging: app.js/index.html/app.css/MainForm bridge/csproj/publish/installer/README) and produced A1–A34. The orchestrator re-read the cited lines for the top findings, saved `claude-diagnosis.md` *before* opening Codex's output, then re-read every range Codex cited (all 25 confirmed), fetched xterm 6.0.0 `CoreBrowserTerminal.ts` and `Keyboard.ts` to confirm F1/F7 against the locked version, and confirmed the installer mutex gap. *Got right:* the bridge/drop exposure (F3), Open executing files (F2), Shift+Enter (F1), Ctrl+V (F7), the commit-box rebuild, and most lifecycle bugs. *Got wrong or missed:* both Undo data-loss paths (F4, F5); wrongly ruled the 4 MB cap fine (F16); missed F18, F40–F44; mis-stated F31's mechanism, overstated F6 ("never"), F10 (path-count threshold), F12 ("/s fixes it") and F26's flush wording.

**What ChatGPT (Codex) did.** `diag/a1` read nothing (sandbox exec helper failed: `helper_unknown_error: setup refresh had errors`); `diag/a2`, given the full source inline, produced C1–C25, including the two Undo data-loss paths, the diff-cap memory issue and the small-transcript bug. `review/a1` checked A1–A34 against source and version-matched docs (xterm 6.0.0, WebView2, git, .NET): confirmed all, corrected F31's mechanism and four over-statements, raised O1, added C36 (Undo not bound to confirmation), C42 (malformed bridge JSON), C43 (installer mutex). `combined/a1` drafted the merge. *Missed:* Open executing files (examined only `openUrl`), Shift+Enter, Ctrl+V, the commit-box rebuild, Esc, the cmd current-directory search; left the drop/bridge exposure as could-not-determine.

**Who decided what.** Source reads (both sides) settled what the code contains. F1 and F7 were further settled by xterm 6.0.0 source fetched by both analysts. F31's corrected mechanism rests on xterm `Buffer.ts:275-280` as cited by Codex — not re-fetched by Claude. Microsoft documentation cited by Codex was not re-fetched by Claude. Nothing was settled by an executed check.

**Found issues, by tool**

| ID | Issue | Found by | Verdict | Evidence | Impact / likelihood / confidence / prod | Decisive check | Outcome |
|---|---|---|---|---|---|---|---|
| F1 | Shift+Enter submits | A | confirmed (source + xterm 6.0.0) | app.js:523-545 | high / every Shift+Enter / high / unknown | §6.1 | P1 |
| F2 | Open executes file | A | confirmed (source) | MainForm.cs:368-406 | high / on Open of script or exe / high / unknown | §6.3 | P1 |
| F3 | Drop navigates; bridge trusts any page | A (B: c_n_d) | confirmed (source; drop behaviour per WebView2 docs) | MainForm.cs:59-75,181-204 | high / on drop / medium / unknown | §6.4 | P1 |
| F4 | Rename Undo overwrites; plan not bound | B | confirmed (source) | GitService.cs:378-397 | data loss / rename+recreate / high / unknown | §6.5 | P1 |
| F5 | Lookup failure → recycle tracked files | B | confirmed (source) | GitService.cs:352-413 | data loss (recoverable) / git failure mid-Undo / high / unknown | §6.6 | P1 |
| F6 | Stop leaves "running" | both | confirmed (source) | TerminalSession.cs:54-58 | core feature / every Stop / high / unknown | §6.9 | P1 |
| F7 | Ctrl+V no paste | A | confirmed (source + xterm 6.0.0) | app.js:523-546 | moderate / every Ctrl+V / high / unknown | §6.2 | P2 |
| F8 | Buttons pass MouseEvent | both | confirmed | app.js:1560-1561 | moderate / every mouse-open / high / unknown | compare mouse vs keyboard | P2 |
| F9 | Unstage rename partial | both | confirmed | GitService.cs:344-349 | moderate / every rename unstage / high / unknown — *B High, A Low* | §6.7 | P2 |
| F10 | stdin deadlock | both | confirmed (mechanism) | GitService.cs:51-61 | hang / large Undo / medium / unknown | §6.8 | P2 |
| F11 | addSessions non-atomic, prunable | both | confirmed | MainForm.cs:241-258 | duplicate sessions / stale worktree / high / unknown | valid+invalid batch | P2 |
| F12 | cmd fallback cwd/quoting | both | confirmed (narrowed) | CommandLineResolver.cs:15-60 | wrong program / npm install + cwd file / medium / unknown | PATH shim vs cwd fixture | P2 |
| F13 | Commit box rebuilt; draft cleared | A + B | confirmed | app.js:940-981 | annoyance-to-loss / while Claude works / high / unknown | type during status update | P2 |
| F14 | Watcher leak | both | confirmed | TerminalSession.cs:60-108 | resource leak / double refresh / high / unknown | count watchers | P2 |
| F15 | Final output lost | A (B: unverified) | confirmed (race in source) | PseudoConsoleProcess.cs:110-179 | lost error text / timing / medium / unknown | numbered final block | P2 |
| F16 | Diff cap after full load | B | confirmed; A's "fine" rejected | GitService.cs:57-62,267-294 | memory / large blob / high / unknown | measure allocation | P2 |
| F17 | Diff refresh gated on Git selection | both | confirmed | app.js:904-907 | stale diff / common / high / unknown | edit A while B selected | P2 |
| F18 | Small transcripts miss titles | B | confirmed | ConversationIndex.cs:100-117 | stale titles / <96 KB convs / high / unknown | short fixture | P2 |
| F19 | Stale exit event | both | confirmed | TerminalSession.cs:31-35 | wrong state / race / medium / unknown — *B Medium, A Low* | paused callback | P3 |
| F20 | Timer.Change after dispose | both | confirmed | GitWatcher.cs:60-77 | crash / narrow race / medium / unknown — *B Medium, A Low* | paused Kick | P3 |
| F21 | Handle races | A | confirmed | PseudoConsoleProcess.cs:104-186 | native misuse / race / medium / unknown | forced interleaving | P3 |
| F22 | Refresh coalescing loss | both | confirmed | TerminalSession.cs:76-99 | stale status ≤15 s / narrow / high / unknown | injected handoff | P3 |
| F23 | Job/resume unchecked | B | confirmed | PseudoConsoleProcess.cs:61-72 | uncontained child / native failure / high / unknown — *B High, A Low* | fault injection | P3 |
| F24 | Sync input on UI thread | B | confirmed (code); trigger unestablished | PseudoConsoleProcess.cs:87-101 | freeze / stalled PTY / low / unknown — *B High, A Low* | stalled child | P3 |
| F25 | Sync recycling on UI thread | A | confirmed | GitService.cs:395-432 | freeze / big Undo / high / unknown | large batch | P3 |
| F26 | sessions.json overwrite / unvalidated | A + B | confirmed (A's flush wording narrowed) | SessionStore.cs:28-47 | lost sessions / corrupt file / high / unknown | corrupt fixture | P3 |
| F27 | Modal in WebMessageReceived | A | confirmed (code; hang unreproduced) | MainForm.cs:289-295 | reentrancy / Browse / medium / unknown | native debug | P3 |
| F28 | Profile identity / installer mutex | both (+B C43) | confirmed | Program.cs:16-37; iss:34 | double instance / aliased paths / high / unknown | aliased launch | P3 |
| F29 | ConversationIndex robustness | A | confirmed (drive-root "C--" claim unverifiable) | ConversationIndex.cs:46-154 | list fails / edge / high / unknown | fixtures | P3 |
| F30 | Revision/path hardening | A | confirmed | GitService.cs:166-172,256 | via F3 only / — / high / unknown | option-like sha | P3 |
| F31 | Hard-coded build 22621 | A | confirmed; mechanism corrected by B | app.js:206 | rendering / Windows 10 / medium / unknown | Win10 resize | P3 |
| F32 | OSC 8 links blocked | A | confirmed (+ xterm source) | app.js:198-214 | link dead / OSC 8 output / high / unknown | emit OSC 8 | P3 |
| F33 | Tile re-append steals focus | A | confirmed | app.js:163 | focus loss / after rename/edit / medium / unknown | activeElement | P3 |
| F34 | Diff view state carry-over | A | confirmed | app.js:1432-1466 | wrong scroll / race / medium / unknown | delayed load | P3 |
| F35 | Resize dropped during start | A | confirmed | app.js:233 | wrong PTY size / race / high / unknown | delayed start | P3 |
| F36 | Double restart | A | confirmed | app.js:225-228 | killed restart / double Enter / high / unknown | delayed reply | P3 |
| F37 | Shortcuts under modal | A | confirmed | app.js:548-554 | lost dialog input / rare / high / unknown | shortcut in modal | P3 |
| F38 | publish skips npm ci | both | confirmed | publish.ps1:15-20 | stale libs / lockfile bump / high / unknown | stale workspace | P3 |
| F39 | No renderer-crash recovery | A | confirmed | MainForm.cs:50-80 | dead UI / crash / high / unknown | kill renderer | P3 |
| F40 | CLAUDE_CONFIG_DIR mismatch | B | confirmed | ConversationIndex.cs:22-30 | wrong resume list / process-only override / high / unknown | two config dirs | P3 |
| F41 | Stale commitDetails | B | confirmed (A's subagent had called the guard fine) | app.js:1201-1210 | wrong details / out-of-order reply / high / unknown | reversed replies | P3 |
| F42 | Repository "Loading…" stuck | B | confirmed | app.js:1162-1169 | stuck view / early tab switch / high / unknown | delayed status | P3 |
| F43 | Control chars in commit text | B | confirmed | GitService.cs:152-200 | garbled log / rare commits / high / unknown | fixture commits | P3 |
| F44 | Malformed JSON outside try | B | confirmed | MainForm.cs:184-202 | unhandled exception / via F3 / high / unknown | malformed fixtures | P3 |
| O1 | Esc closes menu and interrupts | A | disputed by B (README:58) | app.js:548-554 | requirement question | — | open → owner |
| R1 | "4 MB cap fine" (A ruled-out) | A | rejected | GitService.cs:267-294 | — | — | replaced by F16 |
| R2 | "No XSS" (A ruled-out) | A | narrowed | app.js call sites | — | — | §3 row 1 |
| R3 | Suspected-only items (STARTF_USESTDHANDLES, resume-id provenance, long-path hashing, gitlink "missing", backpressure, FSW blocking worktree removal, network-volume recycle, minimise→0×0, clipboard permission, header clicks, hidden-terminal focus) | A | unverifiable | — | — | see §8 | not promoted |

**Found independently by both** (coverage, not confidence): F6, F8, F9, F10, F11, F12, F14, F17, F19, F20, F22, F28, F38 — 13 of 44.

**Codex calls**

| Call | Attempt | Started | Duration | state / validation / exit | PID | Model / effort requested | Effective effort | Reasoning tokens | Search | Activity | Prompt / out SHA-256 (12) | Pack |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| diag | a1 | 18:46:28 | 0.7 min | success / ok / 0 | 69176 | gpt-6-astra / xhigh | not observable | 408 | off | 2 commands, both failed (sandbox helper); 2 MCP list calls; 0 file_change | E614CE4EFDB2 / 20EA0202B639 | v1 |
| diag | a2 | 18:48:03 | 11.2 min | success / ok / 0 | 51840 | gpt-6-astra / xhigh | not observable | 13,969 | off | 0 commands; 0 file_change | 138BFDC370D6 / 9749EDAC20C7 | v2 |
| review | a1 | 19:00:29 | 7.4 min | success / ok / 0 | 11404 | gpt-6-astra / xhigh | not observable | 4,606 | off requested; **12 web searches observed** | 0 file_change | F7CB26ABEAEE / 6F1A46511F20 | v2 |
| combined | a1 | 19:09:03 | 8.0 min | success / ok / 0 | 74520 | gpt-6-astra / xhigh | not observable | 3,978 | off requested; **9 web searches observed** | 0 file_change | 8D25FE91ED81 / 016961AB0DCA | v2 |

`diag/a1` was accepted by the launcher but produced no source-based output; it was superseded by `a2` (the collaborator's "re-run with the source pasted" remedy), which is the one allowed retry. Codex used web search on `review`/`combined` although `-Search` was not passed (owner decision D9 says off unless asked); the searches were targeted (xterm 6.0.0 source, WebView2/.NET/git docs) — flagged for the owner, not a stop condition.

**Setup checks (Phase 0)**

| # | Item | Result |
|---|---|---|
| 1 | `BASH_MAX_TIMEOUT_MS=2400000` in `~/.claude/settings.json` | present |
| 2 | Query connection read-only (HARD) | not applicable — no database in this repo |
| 3 | Repo trusted / hook active (HARD) | **absent** — run was driven from this session, not as the collaborator agent, so `collab-guard.ps1` was not loaded. No commit/push/deploy was attempted |
| 4 | Manifest hashes (HARD) | present — all 7 files in CareCrew `tools/collab/MANIFEST.sha256` match |
| 5 | Hook fires (HARD) | **absent** — see 3 |
| 6 | `collab.rules` forbids `git push` | present — `"decision": "forbidden"` (wrapped shape not checked) |
| 7 | `codex debug prompt-input` | not run (no `AGENTS.md` in this repo) |
| 8 | Tool boundary (HARD) | present — no DB-write/migrate/deploy/push MCP tool in this session |
| 9 | Tool parity (HARD) | not checked — no KB/MCP sources used by either side for this repo |
| 10 | `codex debug models` lists gpt-6-astra (HARD) | present |
| 11 | Run ID, scratch, binary, version | present — `88f911`, codex-cli 0.162.0; salt not needed |
| 12 | Snapshot identity | present — HEAD `61d45db`, clean |
| 13 | Policy drift check | not applicable — no CLAUDE.md / AGENTS.md in this repo |
| 14 | Web-search mode recorded | off requested; web use observed (see above) |

Deviations from the collaborator workflow, all because this repo is outside its CareCrew/NHM scope: no SQL preflight, KB lookup or mirror diff; no `AGENTS.md` created; the security-relevant bridge/trust-boundary code was sent to Codex (the CareCrew guardrail would keep it single-model) because the owner asked for the collaborator.

**Redaction:** none needed — source code only, no personal data.

**Inputs Codex did not have:** none of substance — it received the full source, Claude's diagnosis, Claude's verdicts and the settled-disagreement list verbatim. The subagents' intermediate reasoning was not passed (only their reports).

**Passes used:** 2 (diag ×2 attempts, review). **Total Codex calls:** 3 calls, 4 attempts.

**Run ledger:** see the line appended to `%LOCALAPPDATA%\collab\ledger.jsonl` (quoted in the hand-off message).

Run artefacts: `%LOCALAPPDATA%\collab\2026-10-14-claudeterminals-review-88f911\`.

## 8. Not verified / open questions

- **Nothing was built or run.** Every behaviour above is static inference from source (plus version-matched xterm source for F1/F7/F32). §6 lists the decisive checks.
- **O1** (Esc policy) — owner decision.
- Which build/Windows version/WebView2 runtime/Git/Claude Code version users run; whether any failure has occurred.
- Whether Claude Code reads the clipboard itself on `^V` (affects F7's impact) and how it treats `ESC CR` + `CR` (F1).
- Effective `PATH`, `ComSpec`, `NoDefaultCurrentDirectoryInExePath`, file associations (F2, F12).
- Claude Code's project-folder encoding for drive roots and long paths; transcript `sessionId` provenance; the resume id is interpolated into the command line unvalidated (matters on the `cmd` fallback path).
- No output backpressure from xterm to the PTY reader.
- STARTF_USESTDHANDLES absent when the host has redirected stdio; gitlink entries reported "missing" by older Git during Undo; recursive watcher blocking `git worktree remove`; Recycle Bin absent on network volumes; minimise → 0×0 → all PTYs shrink; clipboard permission prompt; header rebuild swallowing clicks; hidden terminal keeping focus; installer `CloseApplications` catching custom-profile instances; power-loss durability of `sessions.json`.

## Implementation — P1 fixes (2026-10-14, uncommitted)

Owner request, verbatim: "fix the P1 issues". Base `61d45db`; 8 tracked files changed (+322/−59), new test project `tests/ClaudeTerminals.Tests` (50 → 77 tests).

| Finding | Change |
|---|---|
| F1 Shift+Enter | `app.js` `terminalKey`: Shift+Enter handled before the keydown-only filter; cancels the keydown and swallows the keypress/keyup, posts `ESC CR` once. |
| F2 Open executes | `MainForm` `openFile`: path must stay in the repo lexically (`GitService.FullPathInRepo`) and after resolving junctions/links (`ExistingPathInRepo`, `GetFinalPathNameByHandle`); runnable types (explicit script list ∪ `PATHEXT` ∪ `AssocIsDangerous` ∪ default verb runs `%1` or a known script host) open with the `edit` verb, else `notepad.exe`; other types keep the default action. |
| F3 Drop / bridge | `AllowExternalDrop = false`; `NavigationStarting` cancels everything but `https://app.local/index.html`; messages from, and posts to, any other document are ignored. |
| F4 Rename undo / binding | The page sends the files as the dialog showed them plus the commit (`head`) and which files had unstaged edits; the host re-reads status and refuses on any difference. Staged undo refuses to restore over a file that exists again at a rename's old name or a staged deletion's path (case-only renames exempt). Copies no longer restore their source. Restore runs right after that check, then unstage (original order). |
| F5 HEAD lookup | The separate `rev-parse`/`cat-file` lookup is gone: "in HEAD" comes from the same `status --porcelain=v2` snapshot (`mH`), restores use that snapshot's commit, and a restore with no known commit is refused. A broken HEAD makes `git status` fail, so nothing changes. |
| F6 Stop | Host posts `exited` (code null → "Process stopped") after Stop; `Stop()` reports whether it stopped anything; exits carry a process generation checked on the UI thread, so a late exit can't mark a restarted session stopped (also F19). |

**Evidence (each check states what it proves):**

- Build: `dotnet build ClaudeTerminals.slnx` — 0 warnings, 0 errors (compiles; not that paths run).
- Tests: 77/77 pass (`tests/ClaudeTerminals.Tests`), against real temporary git repositories (Undo), a real pseudo console (Stop/exit), real junctions (containment) and pure functions (runnable classification, page origin).
- Regression sensitivity: each guard disabled in turn, full suite run, file restored byte-identical — 14 of 15 guards are caught (by 1–11 failing tests each). **Not caught:** the compare-exchange guard on a superseded process's exit (a race no test can force); the UI-thread generation check that now carries that protection is caught.
- Real app (Debug build, isolated profile, driven over CDP): with HEAD's `app.js`, one Shift+Enter posted `1b 0d` **and** `0d`; with the fix, `1b 0d` only. Stop → "[Process stopped — press Enter to restart]", Enter restarts. `Page.navigate` to `https://example.com/` and `file:///C:/Windows/win.ini` → `net::ERR_ABORTED`, app page intact. Undo through the real confirm dialog restored a staged file (payload with `head`/`inHead` accepted). "Open" on a `.cmd` that writes a marker: no marker; a `..\` path refused.
- **Not verified:** OS-level drag-and-drop (CDP can't inject an OLE drop); a message from a non-app document (none can be loaded any more); that "Open" actually displayed the `.cmd` (no new editor process was seen — probably a tab in an already-open Notepad); IME composition and key repeat with Shift+Enter; real Recycle Bin behaviour (tests substitute a recorder).

**Codex in Phase 4.** `tests` call (requirement only, no source): a1 could not read `REQUIREMENT.md` (sandbox), a2 with it pasted produced 33 expectations; automated: F2.3, F2.5 (partly), F4.1–F4.4, F4.6, F5.1–F5.3, F5.6, F5.7, F6.1, F6.3 (partly); manual/runtime: F1, F3, F6.2; not done: F2.4 (initially; later fixed), F4.5 (content-only change — the action is unchanged), F4.7 (replayed request), F5.4/F5.5 (the parser they target was removed), F5.8. `diffreview` a1 (validation ok, candidate unchanged during review) raised 9 defects, all accepted and fixed: C1 binding missed unstaged edits and HEAD → bound; C2 check/restore gap → restore immediately after the check (a millisecond window to an external writer remains; no file lock is taken); C3 fallback to live HEAD → refused; C4 copies treated as renames → fixed; C5 unstage before restore → original order restored; C6 junction escape → final-path check (also guards recycling); C7 unknown script associations → default-verb handler check (residual: script hosts not on the list); C8 any `app.local` document → exact page; C9 stale exit after compare-exchange → generation check.

**Not cross-reviewed:** everything changed after `diffreview/a1` — `GitService.cs`, `MainForm.cs`, `TerminalSession.cs`, `Native.cs`, `app.js` and the tests (the C1–C9 fixes). A delta review is the owner's call (D5). KB logging (`log_new_fix`) — not applicable to this repo.

**Hand-off:** committing is git-committer's (on a topic branch, not `main`); there is no deploy step until the owner asks for a release.
