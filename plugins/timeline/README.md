# Timeline

A live map of your sessions in the side panel. Each session is a line with time running left to right: rings are your prompts, the marks between them what Claude did (edits, reads, commands, tests, questions), and the newest ring shows the session's state. Each row says what the session is doing now and what its replies cost in tokens, read from the conversation files.

## Using it

- The title bar button, or **Timeline of your sessions** under the palette button's Plugins, shows every session. **Timeline of this session** on a session's ⋯ menu or tab menu follows that session and lists its prompts.
- Everything is read from the conversation files, so it costs nothing. Only **Summarise** runs Claude, once per press, headless with your own sign-in; the header counts those calls.
- The look follows the app's theme: dark, light, or the Grid when one of the Tron themes is on.

## What it asks to see

`sessions`: the sessions, their state and their conversations. `ask`: to put a question about them to an agent, which is what Summarise does; the app asks you about that separately. You can take either away again under **Permissions**.

## Files

`index.html` is the page, `timeline.js` its script, `timeline.css` its styles, `timeline.svg` its title bar icon. Everything is inside this folder; nothing is loaded from the network.
