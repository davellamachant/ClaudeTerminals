# Themes and plugins

Make LlamaTree look how you like with a theme and an accent colour, and add your own tabs, side panels, menu items and title bar buttons with plugins.

## Theme and accent colour

From the palette button at the top right, choose *System*, *Light*, *Dark* or one of the [themes](#built-in-themes). *System* (the default) follows your OS's light or dark mode (on Windows, its app mode in Settings › Personalisation › Colours), and changes with it.

- **Terminals and diffs follow.** The terminals go light too, and each Claude session is started in its matching theme (`--settings` with `{"theme":"light"}` or `"dark"`): Claude can't detect the terminal's colours itself, because the Windows console layer doesn't pass the question through. Sessions whose command passes its own `--settings` are left alone.
- **Restarting Claude:** Claude reads its theme when it starts, so after switching, LlamaTree offers to restart running Claude sessions; each carries on in its conversation.

**Accent colour:** by default the app uses your Windows accent colour for tabs, buttons and highlights, and follows it when you change it. To pick another, use *Accent colour › Change…* under the palette button: Windows accent, the original purple, eight swatches or any custom colour, previewed as you click (Cancel or <kbd>Esc</kbd> puts back what you had). Shades are chosen so white text and the background stay readable in both themes, so a very light or dark colour is toned where needed.

### Built-in themes

A theme is a JSON file of colours. The app comes with three:

- **Nerds**: everything in shades of your accent colour on near-black, like an old monochrome screen, in a monospaced face with a faint glow. Pick green under *Accent colour* for a phosphor terminal, amber for an old amber one; the whole app recolours as you click. Status, Git's changes and the terminal's colours differ by how light or strong their shade is rather than by hue, and the status dots still differ in shape.
- **Tron**: the Grid, near-black ruled with light-lines in your accent colour (cyan for the original, red for Ares), with amber for what needs you, mint for what went well and red for what went wrong, in Bahnschrift with a glow and a light cycle riding beside the cursor; the terminals and diffs dress to match. Like Nerds it's built from the accent colour, but keeps those signal colours, Git's change colours and the terminal's own sixteen.
- **Matrix**: green phosphor on black in a monospaced face, with glyphs raining down the window's floor and, dimmed, behind the terminals' text; red for a question.

### Your own theme

The quickest start is *Save as a new theme…* under the palette button: it writes the theme you're looking at (Light, Dark, Nerds in your accent, or one of yours) to your themes folder as a file with every colour written out, for you to change. Or choose *Open the themes folder* and put a `<name>.json` there (letters, digits and hyphens; not `light`, `dark` or `system`). The folder is read each time the menu opens, so a theme shows without restarting. For example:

```json
{
  "name": "Forest",
  "base": "dark",
  "font": "Cascadia Mono",
  "glow": true,
  "accent": { "line": "#39ff6a", "strong": "#0b6b26" },
  "colors": { "chrome": "#050a06", "text": "#8cf0a6", "working": "#3fe0d0" },
  "terminal": { "background": "#020703", "foreground": "#5cf08a", "red": "#e0524a" },
  "diff": { "insertedLine": "#1a5a2a55", "removedLine": "#5a1d1d55", "keyword": "#c8ffd8" }
}
```

A theme can set three colours or all of them:

- **`base`** (`dark` or `light`) fills in every colour the theme leaves out. It's also what Claude and plugins are told, so Claude runs in its dark or light colours.
- **`monochrome`:** `true` builds every colour from the accent colour, as [Nerds](../src/LlamaTree/wwwroot/themes/nerds.json) does; colours the theme sets itself still win.
- **`colors`:** the app's own: `chrome`, `chrome-2`, `chrome-3` (backgrounds, darkest last), `panel`, `panel-2`, `hover`, `hover-strong`, `border`, `border-soft`, `border-strong`, `text`, `text-dim`, `text-faint`, `text-strong`, `focus`, `select`, `select-inactive`, `terminal-bg`, `editor-bg`, the status colours `ok`, `working`, `attention`, `asking`, `danger`, `danger-fill`, Git's `st-m`, `st-a`, `st-d`, `st-r`, `st-u` (modified, added, deleted, renamed, untracked), `graph`, `ref-bg`, `ref-fg`, `ref-head-bg`, `ref-head-fg`, `ref-tag-bg`, `ref-tag-fg`, and `info-bg`, `info-border`, `error-bg`, `error-border`, `pv-warn-bg`, `pv-warn-border`, `ico-folder`, `ico-file`, `scroll-thumb`, `scroll-thumb-hover`, `tab-border-shown`, `tab-border-hover`, `tick`, `tick-active`, `tick-hot`, `card-bg`, `card-border`, `bubble`, `code-fg`, `code-bg`, `well`, `table-head`, `link`, `update-fg`. Colours are `#rgb`, `#rrggbb`, `#rrggbbaa`, `rgb()` or `rgba()`.
- **`terminal`:** xterm.js's names: `background`, `foreground`, `cursor`, `cursorAccent`, `selectionBackground`, `selectionForeground`, `selectionInactiveBackground`, the sixteen `black` … `white` and `brightBlack` … `brightWhite`, and `overviewRulerBorder`, `scrollbarSliderBackground`, `scrollbarSliderHoverBackground`, `scrollbarSliderActiveBackground`.
- **`diff`:** `background`, `foreground`, `lineNumber`, `lineNumberActive`, `selection`, `cursor`, `lineHighlightBorder`, `insertedLine`, `insertedText`, `removedLine`, `removedText`, `insertedGutter`, `removedGutter`, `insertedOverview`, `removedOverview`, `diagonalFill`, `scrollbar`, and the syntax colours `comment`, `keyword`, `string`, `number`, `type`; `#rrggbb` or `#rrggbbaa` only.
- **`accents`** suggests accent colours, offered first in the *Accent colour* dialog while the theme is on: up to eight `{ "name": "Ares", "color": "#ff3b3b" }`, each a short name and a `#rrggbb`. Tron suggests its own cyan and Ares' red; Nerds green phosphor, amber and paper white. They're only suggestions: any accent still works.
- **`accent`** replaces the accent colour while the theme is on: `line` for lines and highlights, `strong` for fills with white text. Leave it out to keep the one picked in *Accent colour*.
- **`font`** is a font family for the app's own text (not the terminals'); **`glow`** makes text glow faintly in the accent colour.
- **`grid`:** `true` rules the window's floor in faint lines of the accent colour, with a glow at the horizon, behind the title bar, tabs and panels and under the terminals' text, as the Tron themes do; editors, menus and cards keep their own backgrounds.
- **`rain`:** `true` has glyphs rain down the window's floor in the accent colour, as Matrix does, behind the title bar, tabs and panels and, dimmed, under the terminals' text; editors, menus and cards keep their own backgrounds. With Windows' reduce-motion setting on, the rain stands still.
- **`cursor`:** `"light-cycle"` puts a light cycle beside the terminals' cursor, as in the Tron themes: it rides a little to the right of the cursor (which stays a blinking bar in the cursor colour, so you can see where you're typing and whether the terminal has focus; the cycle dims while it hasn't), glows in that colour and leaves a ribbon of light fading behind it as it moves, turning at right angles when it changes line. Leave it out for the plain cursor. With Windows' reduce-motion setting on, the glow stays and the ribbon doesn't.

A theme is only data: the app takes the names above and checks each value, so it can't restyle anything else or load anything. A file it can't use, or a value it leaves out, is listed under *Why?* in the palette menu.

## Plugins

A plugin is a web page of your own, shown in a document tab (like a diff or the Project view) or in the side panel (beside Git Changes). The palette button lists your plugins under **Plugins**, each with the menu items it adds there; pick one to open it. Plugins can add items to a project's or a session's menu too, and a button to the title bar.

**Open the plugins folder**, in the palette menu, opens the folder they live in: `plugins` in the app's data folder (`%APPDATA%\LlamaTree\plugins` on Windows). The folder is read each time the menu opens, so a plugin you add shows without restarting. The plugins themselves live in this repository under [`plugins/`](../plugins), one folder each with a release to download (its zip and checksum under [Releases](https://github.com/LlamachantTechnology/LlamaTree/releases), tagged `<id>-v<version>`): unzip it into that folder. [`plugins/CONTRIBUTING.md`](../plugins/CONTRIBUTING.md) says how one is written, checked and released.

### plugin.json

Each plugin is a folder holding a `plugin.json` and its page:

```text
plugins\
  counter\
    plugin.json
    index.html
```

```json
{
  "title": "Counter",
  "description": "Shown when you hover its tab",
  "entry": "index.html",
  "placement": "panel",
  "menu": [
    { "label": "Start again", "command": "reset" },
    { "label": "Count this session", "command": "count", "where": ["session", "sessionTab"] }
  ],
  "toolbar": { "icon": "counter.svg", "tooltip": "open the counter", "command": "open" }
}
```

- **The folder's name** is the plugin's id and its address: `https://counter.plugin.local/index.html`. Use letters, digits and hyphens.
- **`title`** is its name on the menu and its tab (the folder's name if left out). **`description`** is shown when you hover its tab.
- **`icon`** is shown beside its name there and in Permissions: an image file in its folder (SVG, PNG, ICO, GIF, JPEG or WebP, up to 64 KB). Without one it has its title bar button's icon, if it has a button, or the app's plugin icon.
- **`entry`** is its page, a file inside the folder (`index.html` if left out). Its scripts, styles and images load from the same folder with relative paths.
- **`placement`:** `"tab"` (the default) opens it as a document tab; `"panel"` gives it a tab in the side panel, which is there as soon as the app has read the plugin, without opening it first, and which you can drag to another group like the others. Close it with a middle-click or its right-click menu; it then stays out of the panel until you open it again from the palette, a menu or its button.
- **`menu`:** up to 8 items. Picking one opens the plugin and sends it the item's `command`, with what was clicked. **`where`** names the menus it's on, one or a list (the palette menu if left out):
  - `"palette"`: under the plugin's name in the palette menu;
  - `"project"`: a project's right-click menu in the Projects row;
  - `"session"`: a session's ⋯ menu on its tile (or a right-click on the tile's header);
  - `"sessionTab"`: a session tab's right-click menu.

  In the project and session menus, plugins' items are under a **Plugins** heading at the end, each with its plugin's name beside it.
- **`access`:** what it asks for beyond its own page, one or a list: `"sessions"` (see [Seeing your sessions](#seeing-your-sessions)), `"message"` (see [Messaging your sessions](#messaging-your-sessions)), and `"ask"` (see [Asking Claude about your sessions](#asking-claude-about-your-sessions)). Leave it out and it gets nothing. No access lets a plugin type into a session as you: what it has to say reaches an agent as a message from it.
- **`inbox`:** `true` lets sessions message it first, not only answer what it sends them (it needs `"message"` access too).
- **`toolbar`:** a button in the title bar, after New Session and Git Changes, set apart from them.
  - **`icon`** is an image file in the plugin's folder (SVG, PNG, ICO, GIF, JPEG or WebP, up to 64 KB). The button shows only the icon, never text, so it can't pass for one of the app's own buttons.
  - Its tooltip is the plugin's name, then **`tooltip`** if given.
  - Clicking it opens the plugin and sends **`command`**, if given, with the open session as context. The plugin's badge shows on its button too.
  - Right-click the button › **Hide this button** to hide it; the plugin's **Show its title bar button** in the palette menu brings it back.

### Talking to the app

A plugin and the app exchange a few messages, each `{ type: 'llamatree:<name>', … }`. Listen with `addEventListener('message', …)`; send with `parent.postMessage(…, '*')`. Plugins written before 1.2 use `'claude-terminals:<name>'`, which still works: the app answers them under that name too.

| Message | Direction | What it does |
|---|---|---|
| `theme` `{ theme: 'light' \| 'dark', id, name, colors, font, glow }` | To the plugin | Sent when its page loads and whenever the theme changes. `id` and `name` are the theme file on (`nerds`, one of yours), or null for System, Light and Dark. `colors` are the app's own colours by the names in [Your own theme](#your-own-theme) (`chrome`, `panel`, `text`, `accent`, `accent-strong`, `ok`, `working`, `asking`, …), `font` the theme's font family or null, `glow` whether its text glows, so a plugin can paint itself to match whatever theme is on. The page is also opened with `?theme=light` or `?theme=dark`, and `?look=<id>` when a theme file is on. |
| `command` `{ command, context }` | To the plugin | One of its menu items, or its title bar button, was picked; sent once its page has loaded. `context` says what was clicked (below). |
| `open` `{ placement, state }` | From the plugin | Moves the plugin to the other placement for now: `tab` (a document tab, wide) or `panel` (the side panel). Its page starts again there, so it sends along whatever `state` it wants back (JSON, up to 1 MB), and gets it in a `state` message once it has loaded. It returns to its `plugin.json` placement when that tab closes. The page is opened with `?placement=tab` or `?placement=panel`, so it knows where it is. |
| `state` `{ placement, state }` | To the plugin | After an `open`: where the page is now, and the state it sent along. |
| `badge` `{ count }` or `{ dot: true }` | From the plugin | Puts a count (99+ above 99) or a dot on its own tab; `{ count: 0 }` clears it. |
| `title` `{ title }` | From the plugin | Changes its own tab's caption (up to 60 characters); `{ title: null }` puts its name back. |
| `request` `{ requestId, name, … }` | From the plugin | Asks for something its `access` lets it see (below). |
| `response` `{ requestId, result }` or `{ requestId, error }` | To the plugin | The answer to a request. |
| `message` `{ unread, from, fromName }` | To the plugin | A message came for it (with `"message"` access); read it with `readMessages` (below). |

The badge and caption last until its tab is closed. A plugin can only change its own tab; anything else it sends is ignored.

### What a command's context holds

By the menu it came from:

| Menu | `context` |
|---|---|
| `palette` | `{ menu: 'palette' }` |
| `toolbar` (its title bar button) | `{ menu: 'toolbar' }`, plus the session fields below for the session that's open, if one is |
| `project` | `{ menu: 'project', project }`: the project's name (`null` for the sessions with no project) |
| `session`, `sessionTab` | `{ menu, sessionId, session, directory, project }`: the session's id and name, its working folder, and its project's name (`null` if none) |

A plugin's items are only on the sessions and projects it may see: it needs `access` that you haven't refused, and a session in its projects that you haven't hidden from it. Until you've answered, its items are there, but say only `{ menu }`; choosing one is how it gets to ask. Its title bar button is always there, with the session fields only for a session it may see.

Without `access`, that's all a plugin learns. The session id tells clicks on different sessions apart and stays the same while the session exists; it isn't a way into the session. The folder is the one the session runs in; the plugin still can't read it.

### Seeing your sessions

A plugin whose `plugin.json` has `"access": ["sessions"]` can ask about your sessions. The first time it asks, the app asks you whether to let it, saying what it would see, and keeps your answer either way. You also choose which sessions: **Only sessions in these projects** (the open session's project is ticked to start with), or **All sessions, including projects you add later**.

A session in a project you ticked is seen as it comes and goes; a new project isn't until you add it, and a session moved out of one stops being seen (the plugin's page reloads, so it no longer holds what it was shown). A plugin allowed before projects could be chosen keeps seeing every project. It's read only: nothing here types into a session or changes a file.

| Request | Answer |
|---|---|
| `sessions` | Every session: `{ id, name, project, directory, worktree, status, open }`. `worktree` is the top of its Git working tree (`null` outside Git). `status` is its status dot: `working`, `running` (idle), `asking` (needs your answer), `attention` (finished, waiting for you) or `exited`. `open` is true for the session that's open. |
| `activity` `{ ids, have }` | What Claude did in each session, read from its conversation: `[{ id, found, conversation, version, from, events }]`. Each event is `{ t, k, file, text, ok, q }`: its time; its kind (`prompt`, `read`, `search`, `edit`, `run`, `build`, `test`, `ask`, `say`, `agent`, `compact` or `other`); the file read or edited; the prompt, question, command or first sentence of an answer; whether a test, build or edit succeeded, once known; and whether an answer ends in a question. The first event of each reply also carries its cost, `ctx` (the tokens the agent was sent, cached or not) and `out` (the tokens it wrote). `have` maps a session id to the `{ conversation, version }` you already have, so only newer events come back, from index `from`. |
| `folders` `{ sessionId }` | The top-level folders of the session's repository with how many tracked files each holds, and the folders directly in each with theirs (the biggest forty): `{ root, folders: [{ name, files, folders: [{ name, files }] }] }`, `root` being the repository's main worktree. `null` outside Git. |
| `showSession` `{ sessionId }` | Opens the session, as clicking its tab would. |
| `showPrompt` `{ sessionId, at, index, text }` | Opens the session's Conversation tab scrolled to the prompt sent at `at` (an ISO time from `activity`), or failing that the `index`th prompt; answers `{ index }`. Claude's full-screen mode keeps no terminal scrollback, so the Conversation tab is where a prompt can be jumped to. |

### Messaging your sessions

A plugin with `"access": ["message"]` can message Claude and Codex sessions through the same relay sessions use (see [Messages between sessions](panels.md#messages-between-sessions)). You're asked first, and choose the projects whose sessions it may message, as for seeing them; the two share the same projects and the sessions you hid.

Its messages reach an agent as from the plugin ("Messenger (plugin)"), never from you, and the agent is told it has one as for any message. With `"inbox": true` sessions may message it first too; otherwise they can only answer it. Plugins never message each other, and every message is in Permissions › **Messages**.

| Request | Answer |
|---|---|
| `messageTargets` | The sessions it may message now: `[{ id, name, project, agent, running }]`. |
| `sendMessage` `{ to, text, replyTo }` | Sends `text` to the session `to` (an id from `messageTargets`), as a reply to `replyTo` if given: `{ outcome, id }`, `outcome` being `delivered` or `denied`, and `id` the message's, which an answer names as its `replyTo`. |
| `readMessages` | The messages sent to it since it last read them, each read once: `[{ id, from, fromName, text, sent, replyTo, turn }]`. |

When a message comes for it, the app sends `message` `{ unread, from, fromName }` to its page (once it has loaded, if it isn't open yet); read them with `readMessages`. [Messenger](../plugins/messenger) is a plugin that uses them: pick a session, send it a message, and see its answer. To try it, download its release and unzip it into your plugins folder.

### Asking Claude about your sessions

A plugin with `"access": ["sessions", "ask"]` can also put a question about your sessions to an agent. You're asked separately before it may, because each question runs Claude once, headless, with your own sign-in, the way [Suggest](git.md#suggest) does, and counts against your usage.

The agent is the sessions' own when they all share one that can answer this way (Codex, say), otherwise Claude. It runs with no tools and saves no conversation, so it can't change anything. The question goes with the conversations of the sessions named: your prompts, what the agent answered and the tools it used, each session's newest turns first within a fixed budget, so an old or long conversation is cut short at the top. The plugin sees the answers. **Stop it asking Claude about your sessions** in the palette menu takes it away again.

| Request | Answer |
|---|---|
| `ask` `{ question, ids, history }` | `{ answer, writer, sessions }`: the agent's answer (light Markdown), the name of the agent that wrote it, and the ids of the sessions it was about. `ids` names the sessions to ask about (stopped ones too: their saved conversations are read); `history` is earlier `[{ question, answer }]` of the same chat, the last eight, so a follow-up makes sense. |

### Permissions

The palette menu's **Permissions** opens a tab showing what every plugin and session may do.

**By plugin** lists each plugin with the access its `plugin.json` asks for, each set to **Allowed**, **Ask first** (you're asked the next time it needs it) or **Not allowed**; change any of them there at any time.

- Taking access away reloads the plugin's page, so it no longer holds what it was shown.
- A plugin allowed to see your sessions sees those in the projects you chose (**Change projects** changes them) except any you've hidden from it, which are listed under its access with **Show all**.
- Answers about a plugin you've since removed are listed too, with **Forget**.

**By session** starts with **All sessions**: the global rules for every Claude and Codex session messaging a session in the same project, and any other session (**Allowed**, **Ask first** or **Not allowed**). Then each session, with:

- the plugins that ask to see sessions, each marked whether it sees this one. Click one to hide the session from it, or show it again; a plugin can't tell a hidden session from one that doesn't exist, and hiding one reloads its page.
- for Claude and Codex sessions, **Messages it may send**: its own rules for any other session, for sessions in its project, and for each other session. Each of a session's own is **Default** unless you set it, leaving it to the next rule, and says what that comes to.

The most specific rule decides: one for that very session, then the session's rule for its project, then for anyone, then the global rule for the same project, then for anyone; with none, you're asked. Messages waiting for your answer are marked there, and clicking the mark asks you again. **Pause messages** on the toolbar stops every session messaging another until you resume. **Messages** lists the messages themselves (see [Messages between sessions](panels.md#seeing-and-changing-it)).

**Reset.** A plugin's card in By plugin has **Reset** once you've answered anything about it: it forgets what it may do, its projects and the sessions hidden from it, so it asks again. A session's card in By session has **Reset** once it has rules of its own: they're cleared, and All sessions' rules decide.

**Where your answers are kept.** Your answers about plugins (what each may do, its projects, the sessions you hid) are kept in `plugin-grants.json` next to `sessions.json`, where both the page and the message relay read them. Each answer that allows something remembers the plugin's `plugin.json`: if that file changes, what you allowed is asked again, and Permissions marks the plugin **Changed since you allowed it**.

### What a plugin can't do

A plugin is only a page. It's given no terminals, files or Git, no sessions unless you allowed it to see them, and it can't reach the app:

- it runs in a sandboxed frame with an origin of its own, so it can't read or change the app's page, or another plugin's;
- the app listens to it only for the messages above, never through the bridge the app's own page uses, and answers a request only with what that plugin was allowed to see;
- it can't navigate the app, open windows, or use storage, the clipboard, the camera or notifications;
- other origins can't read a plugin's files, and its frame can't leave its own plugin's address.

It can still run scripts, show forms and dialogs, load files from its own folder, and reach websites as any page can. Its page reloads when its tab is closed, or dragged to another group.

> [!WARNING]
> A plugin you let see your sessions could send what it sees elsewhere. Install only plugins you trust, as you would any page you open.
