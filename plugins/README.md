# Plugins

The plugins for LlamaTree, one folder each. A plugin is a web page shown in a tab or in the side panel; it runs sandboxed on an address of its own and sees nothing of your sessions until you allow what its manifest asks for. See [Plugins](../README.md#plugins) in the app's README for what a plugin can do and the messages it exchanges with the app.

## The plugins

<!-- plugins:start -->
| Plugin | What it does | Version | Shows in | Asks to see |
|---|---|---|---|---|
| [Ask](ask) | A chat beside your sessions: ask what each is doing or waiting on, have a message drafted, and send it to any session | [1.0.0](https://github.com/LlamachantTechnology/LlamaTree/releases/tag/ask-v1.0.0) | the side panel | sessions, ask, message |
| [Floor plan](floor-plan) | A project's sessions on a city of its code: each top-level folder a building with a floor per sub-folder, lit where files were read and edited, each session a beacon where it's working | [1.0.0](https://github.com/LlamachantTechnology/LlamaTree/releases/tag/floor-plan-v1.0.0) | a tab | sessions |
| [Messenger](messenger) | Send a message to a Claude or Codex session and see the answers, or the messages sessions send it | [1.0.0](https://github.com/LlamachantTechnology/LlamaTree/releases/tag/messenger-v1.0.0) | the side panel | message |
| [Timeline](timeline) | A live map of your sessions in the side panel: what each is doing now, your prompts and what Claude did between them, the tokens each is using, and a summary on demand | [1.0.0](https://github.com/LlamachantTechnology/LlamaTree/releases/tag/timeline-v1.0.0) | the side panel | sessions, ask |
<!-- plugins:end -->

The table is written by `node plugins/tools/catalog.mjs --readme` from each plugin's `plugin.json`; the version links to its release.

## Installing one

1. Open the plugin's release (the version in the table) and download `<id>-<version>.zip`.
2. In LlamaTree, click the palette button at the top right and choose **Open the plugins folder**.
3. Unzip into that folder, so it holds `plugins\<id>\plugin.json`.
4. Open the palette menu again: the plugin is listed under **Plugins**. The first time it wants to see anything, the app asks you.

LlamaTree will soon list these plugins itself under *Get plugins…* and install them from here, reading [`catalog.json`](https://github.com/LlamachantTechnology/LlamaTree/releases/tag/catalog), which the release workflow rebuilds after every plugin release.

## Licence

The plugins are under the same licence as the app, the Apache License 2.0: see [LICENSE](../LICENSE) and [NOTICE](../NOTICE).

## Writing one

See [CONTRIBUTING.md](CONTRIBUTING.md): the folder layout, the manifest ([`schema/plugin.schema.json`](schema/plugin.schema.json)), the checks (`node plugins/tools/validate.mjs`), and how a plugin is released (a tag `<id>-v<version>` on `main`).
