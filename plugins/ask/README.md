# Ask

A chat beside your sessions. Ask what a session is doing or waiting on, which one touched a file, or for a message to send one; an agent run headless by the app answers from the sessions' conversations. Each answer names the sessions it drew on (click one to open it) and can be sent to any running session as a message from the plugin. The composer's left-hand box picks where what you type goes: to the assistant, or to a session.

## Using it

- The title bar button, or **Ask about your sessions** under the palette button's Plugins, opens it in the side panel about every session. **Ask about this session** on a session's ⋯ menu or tab menu, and **Ask about this project** on a project's right-click menu, open it about that one.
- Each question runs Claude once, headless, with your own sign-in, the way *Suggest a commit message* does, and counts against your usage. The agent runs with no tools and saves no conversation, so it can't change anything.
- A message you send a session is labelled as from Ask and listed under **Permissions › Messages**; the session's agent reads it when it next runs.
- The chat is kept only while the tab is open. It can move between the side panel and a wide document tab for big tables and long code.

## What it asks to see

`sessions`: the sessions, their state and their conversations. `ask`: to put a question about them to an agent, which the app asks you about separately. `message`: to send a session a message and read its answer. The app asks you before it may have any of them, and you can take each away again under **Permissions**.

## Files

`index.html` is the page, `ask.js` its script, `ask.css` its styles, `ask.svg` its title bar icon. Everything is inside this folder; nothing is loaded from the network.
