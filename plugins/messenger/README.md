# Messenger

Send a message to a Claude or Codex session and see its answer, or read the messages sessions send to the plugin. It shows in the side panel, beside Git Changes.

## Using it

- Open it from the palette button, under **Plugins**, or right-click a session's tab (or open its ⋯ menu) and choose **Message this session**, which opens Messenger with that session picked.
- Pick a session, write a message and send it. The session's agent is told it has a message; its reply shows in the log when it comes.
- Sessions may also message Messenger first (`inbox`): those show in the log as they arrive, with who sent them.

## What it asks to see

`message`: to send messages to sessions and read their answers. The app asks you before it may, and you can take it away again under **Permissions**. It sees nothing else of your sessions.

## Files

`index.html` is the page, `messenger.js` its script, `icon.svg` its icon. Everything is inside this folder; nothing is loaded from the network.
