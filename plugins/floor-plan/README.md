# Floor plan

A project's sessions on a city of its code. The repository's top-level folders are buildings, their footprint and height from how many files they hold; every file a session has read or edited lights a window (blue for read, amber for edited, outlined when two sessions touched it). Each session is a beacon on the street in front of the building it's working in, with a light trail along the streets it came by, and a card beside the city tells its story turn by turn. Whoever needs your answer is at the top. Files outside the repository land on the plaza in front.

## Using it

- Open it from a project's right-click menu, or a session's ⋯ menu or tab menu, with **Floor plan**: that project's city, in a document tab.
- The slider at the bottom rewinds through everything that happened; **Live** follows what's happening now.
- It asks the app for the sessions, what each did (read from their conversations) and the repository's folders every few seconds. Its colours and font come from the app's theme, so it looks right under Tron, Matrix, Nerds or one of your own.

## What it asks to see

`sessions`: the sessions, their state and what they did, read from their conversation files. The app asks you before it may, and you can take it away again under **Permissions**. It runs no agent and spends no tokens.

## Files

`index.html` is the page, `floor.js` its script, `floor.css` its styles. Everything is inside this folder; nothing is loaded from the network.
