# Contributing to LlamaTree

Thanks for helping. Bug reports, ideas, docs fixes, themes, plugins and code are all welcome.

## Where to start

- **Found a bug?** [Open an issue](https://github.com/LlamachantTechnology/LlamaTree/issues/new) with what you did, what you expected and what happened, plus your LlamaTree version (top right of the window) and your OS. A screenshot helps; check it doesn't show anything private.
- **Want to help but not sure where?** Issues labelled [good first issue](https://github.com/LlamachantTechnology/LlamaTree/labels/good%20first%20issue) are small and self-contained.
- **Planning something bigger?** Open an issue first to say what you have in mind, so we can agree on the approach before you spend time on it.
- **A security problem?** Don't open a public issue. Email [support@llamachant.com](mailto:support@llamachant.com) instead.

## Building and testing

[Building LlamaTree](docs/building.md) has the commands for Windows, macOS and Linux and explains how the app is put together. In short:

```sh
(cd web && npm ci)               # once
dotnet run --project src/LlamaTree
dotnet test
```

GitHub Actions builds and tests every pull request on Windows, Linux and macOS.

To try your build without touching your own sessions, give it a separate data folder: set `LLAMATREE_DATA` to an empty folder before starting it.

## Pull requests

- Keep each pull request to one change, and explain why in its description.
- Add or update tests in `tests/` for behaviour you change, and run `dotnet test` before you push.
- Follow the style of the code around your change.
- If you change what people see or do, update the page for it in [`docs/`](docs/README.md). The guide on [llamatree.dev](https://llamatree.dev/docs/) is built from those pages at each release.
- Leave the version number alone; it changes when a release is made.

## Themes and plugins

You don't need to touch the app to add a theme or a plugin. A theme is one JSON file and a plugin is a folder holding a web page; [Themes and plugins](docs/customize.md) explains both. To share a theme with everyone, open a pull request adding it to `src/LlamaTree/wwwroot/themes/`.

## Licence

LlamaTree is licensed under the [Apache License 2.0](LICENSE). Anything you contribute is under the same licence, as section 5 of the licence describes.
