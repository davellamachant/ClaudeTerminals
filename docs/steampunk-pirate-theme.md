# The Steampunk Pirate theme

Steampunk Pirate is one of the four themes that ship with Claude Terminals. It dresses the app as an airship captain's cabin: walnut, leather and brass, with a sea chart on the table and the ship's machinery at work around it. Everything on screen is drawn by the page; nothing is an image, a logo or a trademark, and the only names in it are made up.

This document describes what the theme is, what each colour means, what the floor shows and how it reads the sessions, the theme keys it introduced, and how it was checked. The theme lives in [`src/ClaudeTerminals/wwwroot/themes/steampunk-pirate.json`](../src/ClaudeTerminals/wwwroot/themes/steampunk-pirate.json); its floor is the `cabin` block of [`src/ClaudeTerminals/wwwroot/app.js`](../src/ClaudeTerminals/wwwroot/app.js).

## Choosing it

*Theme* in the menu, then *Steampunk Pirate*. The whole app changes at once: the title bar, tabs, panels, Git's changes, the terminals and the diff editor.

*Accent colour* offers four choices while the theme is on: **Brass** (the theme's own), **Copper**, **Verdigris** and **Blood red**. The frame, the chart's ink and the brasswork on the floor take the pick, so the same cabin can be lit in copper or drawn in verdigris. No other accent is offered, because the theme has an accent of its own (see [Accent choices](#accent-choices) below).

## The look

Parchment text on dark walnut, in Constantia (a stock Windows face) without a glow. Brass draws lines, labels and the accent. The app's meanings for colours are kept, each cast in the cabin's materials:

| Meaning | Colour | Where you see it |
|---|---|---|
| Accent, lines, labels | brass `#d4aa4f` (strong `#6b5118`) | borders, focus, links' hover, the chart's ink |
| Went well | verdigris `#4fa38a` | success, links, additions in diffs, the branch graph |
| Working | lamp amber `#e0a93a` | a session that's busy, modified files |
| Needs you | copper `#d2763a` | attention, a session waiting on permission |
| A question | plum `#a05a9c` | a session asking |
| Danger | wax-seal red `#b8322a` (fill `#6e1d16`) | errors, deletions, a stopped session |
| Text | parchment `#efe2c4`, dim `#b89c6e`, faint `#6f5c3f`, strong `#fff6e0` | the page |
| Chrome | walnut `#1a120c`, `#150e09`, `#0f0a06` | the title bar, the floor behind the chart, code wells |
| Panels | leather `#2a1d13`, `#33241a`; hover `#3d2c1f` | Git's panel, cards, bubbles |

The terminal's sixteen colours and the diff editor's colours dress to match: red is the wax seal, green is verdigris, yellow is lamp amber, magenta is plum, blue and cyan are muted sea colours, and the bright set lifts each. Keywords in the diff editor are brass, strings verdigris, numbers amber and types copper. Every colour the page uses is written out in the file, so none of the Dark theme shows through, and the catalog test checks the set is complete.

## The floor: the chart table

With the theme on, the window's floor is a canvas fixed behind the page. The title bar, tabs, panels and terminal headers are see-through over it, and the terminal tiles veil it under their text so the text still reads. The floor's drawing has two layers.

### Standing still

Drawn once, and again when the window is resized or the accent changes:

- the walnut's grain and a vignette;
- a riveted brass frame with corner plates and scrollwork, and pipework along it with flanges and a hand-wheel;
- the chart, in the accent's ink: a grid, rhumb lines from a sixteen-point compass rose inside a bolted porthole ring, a hatched coast and an island, soundings in italic numerals, the rings a mug left, "here be monsters", the navigator's dividers, a reading glass and a pair of goggles;
- an instrument plate with an engraved nameplate reading *H.M.A. CONSOLE · PATENT 1887*;
- a boiler with a firebox, a sight glass, a stack, a whistle and a safety valve;
- a Tesla coil and the rod it arcs to, a Jacob's ladder's rods, a bell's bracket, a pennant's pole, a cannon's carriage and a treasure chest on the island.

### Moving

Redrawn about sixteen times a second (every 60 ms):

- **The course.** A route is plotted leg by leg from a port to an X, five seconds a leg, then rests seven seconds and a new one is drawn. A leagues counter with five rolling drums adds up the distance across courses.
- **The helm** turns to the current leg's heading. A parrot perches on it, bobbing and blinking.
- **The machinery.** A train of three gears in the top corner and a pair at the bottom, cut so they mesh (a fixed radius per tooth, each gear phased into the last one's gaps), drive a piston in a cylinder and a chain to a sprocket. A flyball governor spins with the steam. A balance wheel with its hairspring, pallet anchor and escape wheel ticks twice a second.
- **The boiler.** The firebox flickers and throws its light on the floor; the stack smokes; a turbine vent spins with the steam.
- **Electricity.** The Tesla coil throws jagged arcs to its rod every few seconds. On the Jacob's ladder an arc climbs the rods every 2.4 seconds, widening as it rises.
- **Steam**, soft-edged and a little turbulent: a puff from the valve beside the plate now and then, the whistle for just over a second every half minute or so, a sideways jet from the safety valve, wisps from two weeping flanges, a breath from the piston at each stroke, and a low haze drifting across the lower chart. The particles are capped at 420.
- **At sea.** The serpent swims, its humps rising and sinking with ripples and its head lifting. The cannon on the island fires out to sea every half minute or so: smoke at the muzzle, a recoil, a ball's arc, a splash and a spout. A message in a bottle bobs.
- **In the air.** An airship with a spinning propeller and a smoking funnel drifts across once in a while, its searchlight sweeping the chart below it. A lantern sways on its chain, and its flame is the source of the glow on the table. A skull-and-crossbones pennant ripples on the left pipe. The bell swings for a moment every so often. Oil drips from a flange: a drop swells, falls and rings.

### What reads the sessions

The instrument plate at the bottom left is the part of the floor that tells you something:

- **STEAM** gauge: the needle stands at how many sessions are working. At six or more it is in the red: the safety valve lifts and keeps lifting, and the whistle screams.
- **CREW** gauge: how many sessions there are.
- **Ship's clock**: real time.
- **WATCH** lamps: a row of eight, one per session, lit in that session's status colour (verdigris running, amber working, copper needing you, plum asking), and dark with a pilot dot for a session that has stopped.
- **The telegraph** taps the sessions' names in Morse onto a paper tape that scrolls off and curls. The tape is rebuilt when the names change.

### Rest and motion

The floor pauses while the window is hidden. Under Windows' reduce-motion setting the table stands still with the course plotted and the machinery at rest; nothing flickers or arcs.

## What the theme introduced

Two things in the theme system are new with this theme.

### The `cabin` key

`"cabin": true` in a theme file asks for the chart table as the window's floor, the way `grid` asks for Tron's Grid and `rain` for Matrix's glyphs. The catalog parses it into `ThemeInfo.Cabin`, the page sets `data-cabin` on the root, and `app.css` keys the translucency rules on it alongside the other two. A saved theme carries `cabin` with `grid` and `rain`. Any theme file can set it; the chart is drawn in whatever accent that theme has.

### Accent choices

A theme with an `accent` of its own may now also list `accents`. When it does, those are the only alternatives to the theme's accent: the menu names the current pick with *Change…*, and the *Accent colour* dialog shows just the suggestions. Matrix, which has an accent and no list, behaves as it always did. Steampunk Pirate lists Brass, Copper, Verdigris and Blood red.

## Making a variant

Copy `steampunk-pirate.json` into your own themes folder, rename it, and change colours. The quickest changes:

- **Another metal.** Set `accent.line` and `accent.strong` to the metal and put it first in `accents`; the frame, pipes, gears and chart's ink follow.
- **Lighter wood.** Lift the three `chrome` values and the two `panel` values together, keeping their order, and raise `text-faint` so labels still read.
- **No machinery.** Remove `"cabin": true` and the theme is the colours alone, on a plain walnut floor.

The file's name is the theme's id, and a user theme whose file name matches a built-in one is left out with a notice, so give your copy a file name of its own.

## How it was checked

The theme was built on a scratch copy of the app with a demo profile and WebView2's debugging port open, driven by scripts over the Chrome DevTools Protocol that switched the theme, read page state and took screenshots. Each element was judged at 1:1 in the city view and in an expanded terminal, and anything that only read when enlarged was made stronger. The font was confirmed to resolve to Constantia. Along the way the course was watched to its X, the airship across, the lamps against the sessions' statuses, the gauges into the red, and the console for page errors.

The catalog tests check that the theme loads, that its colour sets are complete and that `cabin` parses; the whole suite passes. The work arrived in three commits on [pull request #52](https://github.com/davellamachant/ClaudeTerminals/pull/52): the cabin, then the clockwork, telegraph and counter, then the Jacob's ladder, parrot, searchlight, turbine and cannon.
