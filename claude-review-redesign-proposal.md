# ClipCord — design review and redesign proposal

**Date:** 2026-08-18 · **Reviewed at:** v1.13.1 (`52260b2`)
**Figma file:** https://www.figma.com/design/Y88mP5yoSjQpNysACyFLit

Scope: the shipped desktop UI — window shell, navigation, and the Settings, Activity, Gallery,
Edit & upload and About pages. Reviewed from source (`SettingsForm.cs`, `BrandControls.cs`,
`ActivityView.cs`, `GalleryView.cs`, `LocalClipEditorView.cs`, `AboutView.cs`) and from the
rendered captures in `artifacts/` and `artifacts/qa/`.

Constraint honoured throughout: **the logo and the colour palette do not change.** Every hex in
the proposal already exists in `ClipCordTheme`. What changes is layout, density, and which colour
means what.

---

## 1. What already works

- **The mark is good.** Coral film strip + violet bolt is distinctive at 16 px and at 512 px, and
  it encodes the product (clips + speed) without a wordmark.
- **Dark shell suits the audience.** ClipCord sits next to Discord and a game; a dark window is
  the right call.
- **Custom chrome is worth keeping.** `FormBorderStyle.None` with a hand-built title bar
  (`SettingsForm.cs:210`) makes it feel like an app rather than a stock WinForms dialog.
- **The information architecture is sound.** Settings / Activity / Gallery / About is the right
  set of four. None of them should be merged or removed.
- **About is the best page in the app.** The hero + status + diagnostics + credits layout is
  genuinely well composed. Most of this proposal is an argument for making the rest of the app
  look like About already does.

---

## 2. Findings

### 2.1 The app ships two themes and flips between them mid-session — the biggest issue

`ClipCordTheme` (`BrandControls.cs:6-27`) contains two complete, incompatible surface families:

| Family | Tokens | Used by |
|---|---|---|
| Light | `Card` `#F9F9FB`, `CardBorder` `#DADEE7`, `Text` `#191E28`, `MutedText` `#656C7A` | Activity, Gallery, Edit & upload |
| Dark | `SettingsCard` `#151F31`, `SettingsField` `#101B2D`, `SettingsCardBorder` `#2D3A50` | Settings, About |

Navigating Settings → Gallery swings the content area from `#151F31` to `#F9F9FB` inside the same
chrome. `LocalClipEditorView.cs` alone sets `ClipCordTheme.Card` on 11 surfaces
(`:124, :179, :204, :233, :254, :280, :310, :328, :378, :1415, :1430`), against `SettingsForm.cs`
setting `SettingsCard` on 11 others. The token names record how this happened — the dark family is
prefixed `Settings*` because it was introduced for one page and never generalised.

This is also a contrast-and-eye-strain problem: a near-white panel at ~85% of the window area, in
a dark room, next to Discord.

### 2.2 Fixed chrome consumes 30% of the window

`SettingsForm.BuildRootLayout` (`SettingsForm.cs:290-296`) allocates fixed rows before any content:

```
header       108 px
navigation    66 px
body        (remainder)
footer        90 px   → 264 px of 886 px
```

Inside the header, the logo is 84×84 (`:326`) and the wordmark is 25 pt Bold display (`:345`,
`:347`) — larger than any type on any page. The chrome is louder than the content it frames.

### 2.3 The Settings page is mostly empty

`SettingsDesignedClientSize` is 1080×886 (`SettingsForm.cs:17`), but the four cards stop around
y≈620 (see `artifacts/qa/footer-fix-preview-v1.11.1.png`). Roughly 450 px — a third of the window
— is empty shell. The window is sized for content that does not exist.

### 2.4 Navigation spends 1080 px on four items

`BuildNavigation` (`:454-470`) is a 4-column `TableLayoutPanel` at 25% each, so the selected tab is
a ~380 px violet slab. Horizontal tabs also cap the app at four destinations and cost a full 66 px
row, on the axis that is scarcest.

### 2.5 The app's central state is a small pill in the corner

The one question a user opens ClipCord to answer — *is it watching, and where are clips going right
now?* — is answered by a 230 px status pill in the title bar (`:359-370`) plus a checkbox buried in
the "Upload behavior" card. The route (folder → mode → destination) is never shown as one thing.

### 2.6 The two brand colours have no assigned meaning

Violet does every interactive job — selection, toggles, focus, links, progress. Coral appears on
the logo and on **Save changes**, where an outline **Cancel** sits beside it. A red-family primary
next to a neutral secondary reads as *destructive*, which is the opposite of what the button does.

### 2.7 The footer does three jobs and changes shape per page

Rows 3 of the root layout carries a status line, a privacy line, and the primary actions; its
height is toggled to 0 on About (`:571-577`) and its button label swaps between "Cancel" and
"Close". A persistent 90 px bar that is sometimes absent and sometimes says something unrelated to
the current page is expensive furniture.

### 2.8 Radii and control shapes are inconsistent

Nav chips are 10 px (`:637`), cards 14 px, status pills 20 px, buttons vary. Capture-source
selection is two identical outline buttons that differ only by border colour (`:790-799`) rather
than a segmented control.

---

## 3. The proposal

### 3.1 Principles

1. **One dark surface family.** Retire the light `Card`/`Text` tokens. Depth comes from a four-step
   tint ladder (`#0A1220` → `#0F1726` → `#151F31` → `#182337`) plus one 1 px border. No shadows —
   cheap in GDI+ and stable at every DPI.
2. **Show the route.** ClipCord is a router. The pipeline — watched folder → mode → destination —
   becomes the primary object on screen, and the mode switch moves into permanent chrome.
3. **Chrome shrinks, content grows.** 264 px of fixed chrome becomes 76 px.
4. **Give the two brand colours a job.** Violet = interaction and the Discord route.
   Coral = the local-only route and destructive actions. Both are already in the mark, so the app
   ends up colour-coding its two modes with its own logo.

### 3.2 Layout change

| | Today | Proposed |
|---|---|---|
| Window | 1080 × 886, min 900 × 650 | 1200 × 760, min 960 × 620 |
| Navigation | top tab strip, 66 px tall, 4 items max | left rail, 216 px, room to grow |
| Brand | 84 px logo + 25 pt wordmark in a 108 px header | 28 px logo + 15 pt wordmark in the rail |
| Fixed chrome | 264 px vertical | 76 px page header |
| Mode switch | inside a Settings card + tray menu | always-visible segmented control in the rail |
| Watcher status | pill in the title bar | rail footer, next to the route it describes |
| Footer | permanent 90 px, 3 jobs | contextual save bar, only when there are unsaved changes |

### 3.3 Screens

**Home** *(new)* — the route dashboard. A hero card showing
`Game Clips → [routes to] → Discord · #clips`, three stat tiles (uploaded today / in queue /
local-only archive), a four-row activity timeline, and a shortcuts column. This is the page that
answers 2.5, and it also fills the window that 2.3 leaves empty.

**Settings** — one grouped settings list (Win11 pattern: label + helper on the left, control on the
right) instead of four cards of labelled fields. Sections: Clip source · Discord destination ·
Routing & quality · Application. Capture source becomes a real segmented control with a
one-line explanation under each option. The save bar appears only when something is dirty.

**Activity** — the same timeline as Home, full height, on dark. Coloured 3 px status rails
(green / violet / blue / amber — the values already in `ActivityView.cs`) replace full-width light
cards.

**Gallery** — grid-first on dark, with a per-game filter rail, route badges on each card, and
hover actions.

**Edit & upload** — same two-column composition as today, moved onto the dark family, with the
"Upload safely" explanation kept as-is because that copy is doing real work.

### 3.4 Palette mapping — no new colours

| New token | Value | Was |
|---|---|---|
| `surface/chrome` | `#0A1220` | `Header` |
| `surface/rail` | `#0D1626` | `Sidebar` |
| `surface/base` | `#0F1726` | `Shell` |
| `surface/sunken` | `#101B2D` | `SettingsField` |
| `surface/raised` | `#151F31` | `SettingsCard` |
| `surface/control` | `#182337` | `SettingsButton` |
| `surface/control-hover` | `#222F47` | `SettingsButtonHover` |
| `border/default` | `#2D3A50` | `SettingsCardBorder` |
| `border/strong` | `#354258` | `SettingsFieldBorder` |
| `brand/violet` | `#8B3DFF` | `Violet` |
| `brand/violet-soft` | `#302A4A` | `VioletMuted` |
| `brand/coral` | `#E04346` | `Coral` |
| `text/primary` | `#F5F7FC` | `ShellText` |
| `text/secondary` | `#A6AFC2` | `ShellMutedText` |
| `text/tertiary` | `#707B8E` | literal in `AboutView.cs` |
| `status/success` | `#31B171` | literal in `ActivityView.cs` |
| `status/warning` | `#E09736` | literal in `ActivityView.cs` |
| `status/info` | `#5B93FF` | literal in `ActivityView.cs` |

**Deleted:** `Card` `#F9F9FB`, `CardBorder` `#DADEE7`, `Text` `#191E28`, `MutedText` `#656C7A`.

### 3.5 Suggested implementation order

1. Add the semantic aliases to `ClipCordTheme` and point `Card`/`Text` at the dark values. One
   commit, no layout work — this alone fixes 2.1 across Activity, Gallery and the editor.
2. Replace `BuildNavigation` with the rail; shrink `BuildHeader` to 76 px. Fixes 2.2 and 2.4.
3. Make the footer conditional on dirty state. Fixes 2.7.
4. Add the Home page and move the mode switch into the rail. Fixes 2.5 and 2.3.
5. Rebuild the Settings body as a row list. Fixes 2.8.

Steps 1–3 are mechanical and land most of the visual improvement; 4–5 are the design work.

---

## 4. In-app playback

Added after the review, on `feature/in-app-playback-all-audio`.

### 4.1 Playback was the one path that skipped the audio mix

`BuildAudioArguments` (`ClipEditProcessor.cs:148`) mixes every audio stream together, because a
clip recorded with the microphone on its own track would otherwise lose the voice — the v1.13.1
fix. It runs in the edit path, the compression path, and the trimmed-playback file.

The in-editor player was the exception. `LocalClipEditorView.cs:1196` handed `MediaElement` the
raw source, and `MediaElement` renders only the **first** audio stream. A two-track clip previewed
as game audio with no voice, then uploaded with the voice in it. The correctly-mixed
`CreateTrimmedPlaybackAsync` route only ran when `MediaElement` *failed*, so the fallback was more
faithful than the fast path.

**Fix:** `ClipPlaybackPreparer` resolves what a player opens. One track plays straight from disk;
more than one is rendered once through the same mix filter the edit uses, with the video
stream-copied rather than re-encoded, cached per source, and prewarmed when the editor opens. The
rendition is probed afterwards and rejected unless the streams actually collapsed to one.

### 4.2 Controls sit below the video

`MediaElement` is hosted through `ElementHost`, which renders into its own window — anything the
app paints in that area lands behind the picture. So the overlay title and route badge moved into
the card heading and the transport became a strip underneath. Overlaid controls would mean
authoring them in WPF and maintaining a second copy of this palette; worth paying later, not now.

### 4.3 Gallery plays in place

`PlayClip` called `Process.Start` with `UseShellExecute`, handing the file to whichever
application owns `.mp4`. That is a poor fit for local-only clips, which may end up in an app that
syncs off the machine. It now opens `ClipPlayerView` inside the Gallery.

### 4.4 Proving it

The player states "2 audio tracks mixed" beneath the transport. Off-screen, the rendition is
rejected unless it collapsed to one stream, and a smoke test builds a two-track clip carrying
440 Hz and 880 Hz tones and fails if either is missing from the mix — verified to fail when only
the first track is mapped.

**Follow-up not taken:** the mute toggle still controls both the exported clip and monitoring
volume. Those are different decisions and should be separated.

## 5. What is in the Figma file

- **01 · Foundations** — the mark at four sizes and three lockups, the full palette organised by
  role with the retired tokens called out, the eight-step type ramp, radius/spacing/elevation
  scales, and the control library (buttons, toggle, segmented, field, route chips, status pills).
- **02 · Screens** — Home, Settings, Activity, Gallery, Edit & upload, and Play clip at 1200 × 760.
- **03 · Rationale** — the findings above, the before/after table, the palette mapping, the ship
  order, and the three in-app playback decisions.
