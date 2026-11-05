# Contributing a plugin

A plugin is a web page of your own that LlamaTree shows in a tab or in the side panel. It runs in a sandboxed frame on an address of its own and sees nothing of the app, the sessions or the machine except what its manifest asks for and the user allows. See [Plugins](../README.md#plugins) in the app's README for what a plugin can do and the messages it can exchange with the app.

## Layout

One folder per plugin in `plugins/`. The folder's name is the plugin's id: letters, digits and hyphens, 40 at most, not starting or ending with a hyphen. It becomes the plugin's address (`https://<id>.plugin.local/`), so it can't change without the plugin becoming a new one to the app.

```
plugins/<id>/
  plugin.json     the manifest (see schema/plugin.schema.json)
  index.html      its page (or whatever "entry" names)
  icon.svg        its icon, if it has one
  README.md       what it does and how to use it
  CHANGELOG.md    a "## <version>" section per release, newest first
```

`plugin.json` needs a `title` and a `version` (major.minor.patch). Everything else is optional: `description`, `entry`, `icon`, `placement` (`tab` or `panel`), `access`, `inbox`, `menu`, `toolbar`, `minAppVersion`. The schema says what each is; `node plugins/tools/validate.mjs` checks them the way the app does.

Keep everything the plugin needs inside its folder: the app serves only that folder, and loads nothing from the network for it.

## Changing a plugin

1. Branch, make the change, bump `version` in `plugin.json` and add a `## <version>` section at the top of `CHANGELOG.md`.
2. Run `node plugins/tools/validate.mjs` and, if `plugin.json` changed what the README table shows, `node plugins/tools/catalog.mjs --readme`.
3. Open a pull request. The *Validate plugins* workflow checks the plugin, that a changed plugin has a new version, and that the README table is current. A review is needed to merge.

## Releasing

After the pull request has merged, tag the merge on `main` with `<id>-v<version>` and push the tag:

```
git tag messenger-v1.0.1 && git push origin messenger-v1.0.1
```

The *Release a plugin* workflow checks the plugin, zips its folder (`<id>-<version>.zip`, the folder as the zip's root), publishes a GitHub release with the zip, its SHA-256 and the CHANGELOG section as the notes (not marked latest, so the app's own releases stay the latest), then rebuilds `catalog.json` from every released plugin and puts it on the rolling `catalog` release. Each plugin is released and versioned on its own, apart from the app's `v1.x.y` releases.

## What the app will do with the catalog

LlamaTree will read `catalog.json` to list the plugins under *Get plugins…*, download a plugin's zip, check its hash and the manifest inside against the catalog entry, and unpack it into the plugins folder. Installing grants nothing: the app asks for the access a plugin wants the first time, and again whenever its manifest changes. The catalog will be signed with a key kept outside the repository before the app trusts it; until then it is for people, not the app.

## Licence

A plugin you contribute here is published under the repository's licence, the Apache License 2.0 (see [LICENSE](../LICENSE) and [NOTICE](../NOTICE)), like the app.

## Access, and why it's small

`access` is the whole of what a plugin may see beyond its own page: `sessions` (the sessions and their state), `message` (send a session a message and read its answer; with `inbox: true`, sessions may message it first), `ask` (put a question about sessions to an agent, which the user is asked about separately). Ask only for what the plugin uses: the user sees the list before allowing it, and a plugin that asks for more than it needs is harder to trust.
