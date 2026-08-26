# Claude prompt — ClipCord 2.0 Capture settings in Figma

You are designing the approved ClipCord 2.0 Capture settings experience in Figma. This is a
**Figma-only design task**. Work read-only against the repository: do not edit, create, delete,
format, commit, or push any source file. Do not publish a release. You may inspect the connected
private repository and the existing connected ClipCord Figma file.

## Sources of truth

- Private repo: `malikpervez/clipcord-2.0`
- Branch: `codex/2.0-foundation`
- Local repo when available:
  `C:\Users\mip12\Documents\Codex\2026-07-31\i-w\work\moments-to-discord`
- Product roadmap and privacy invariants: `docs/V2_ROADMAP.md`
- Exact supported profiles and size formula: `Capture/CaptureProfiles.cs`
- The approved experience mixes enabled game, microphone, and voice-chat inputs into one
  clip-audio stream. Inputs are independently controllable; output tracks are not.
- Existing visual system: use the approved ClipCord Figma components, variables, icons, rail,
  header, cards, typography, spacing, focus states, and Save changes behavior already present in
  the file. Do not redesign ClipCord's visual language.

Inspect the current Figma file first. Add this work as a clearly named ClipCord 2.0 design section
or page in that same file. Do not create an unrelated visual system or a disconnected blank file.
If Capture needs a new navigation icon, design it as a proper Figma vector component using the same
stroke weight, optical size, bounds, and states as the existing rail icons.

## Product model

ClipCord 2.0 can record and replay gameplay itself. Capture remains local-first: enabling recording
does not enable upload. The camera and microphone are independent opt-ins. One original recording
can later be routed to Discord, YouTube, and TikTok, so an original is never moved into a folder to
represent an upload status.

Design a dedicated **Capture** destination in the shared left navigation rail, positioned near
Gallery. The page title is **Capture** and the supporting line should communicate that ClipCord can
save recent gameplay locally with hardware encoding.

The designed opening canvas remains the current ClipCord shared shell at 1200×760 logical pixels.
It must also have credible 150% and 200% DPI behavior and a constrained-height scrolling state.

## Required controls and defaults

### Capture status and Instant Replay

- Master setting: **Instant Replay** — Off by default until the user explicitly enables it.
- Replay duration choices: **15 sec, 30 sec, 60 sec, 2 min, 5 min**.
- Default after opt-in: **60 seconds**.
- Show the configured save-hotkey and a secondary action to change it.
- Make the states visually unmistakable: Off, Ready, Recording/Buffering, Paused, and Unavailable.
- While buffering, show a persistent privacy/status explanation; do not imply that a clip is being
  uploaded.

### Video quality

- Resolution choices, presented as accessible selectable cards or a strong segmented group:
  - **1080p** — 1920×1080; label as Recommended.
  - **1440p** — 2560×1440.
  - **4K** — 3840×2160.
- Frame-rate choices: **30 FPS** and **60 FPS**.
- Default: **1080p / 60 FPS**.
- Hardware encoding is **Automatic** in this version; do not expose NVENC/AMF/QSV jargon as a
  normal user setting. A small status row may name the detected GPU/encoder.
- Include an unsupported state in which 4K60 is disabled with a concise hardware explanation.
- Resolution, frame rate, and duration cannot change while the replay buffer is running. Design the
  locked state and the instruction to pause capture first.

### Dynamic size estimate

The estimate must respond immediately to resolution, FPS, duration, whether clip audio is enabled,
and the reaction-camera toggle. It is a variable-bitrate range, never a guaranteed exact size.

Initial H.264 targets, including one 192 kbps mixed audio stream, for a 60-second clip:

| Profile | Expected | Honest range |
|---|---:|---:|
| 1080p30 | about 87 MB | 70–109 MB |
| 1080p60 | about 144 MB | 116–181 MB |
| 1440p30 | about 173 MB | 138–216 MB |
| 1440p60 | about 252 MB | 201–315 MB |
| 4K30 | about 323 MB | 259–404 MB |
| 4K60 | about 466 MB | 373–583 MB |

Use the selected duration to scale these values. Enabling the separate reaction-camera layer adds
approximately **43 MB per minute**, before variable-bitrate range. Suggested primary copy for the
default profile:

> Estimated clip size: about 144 MB
>
> Usually 116–181 MB for a 60-second clip. Motion and detail affect the final size.

Also show the approximate live replay-buffer memory for the selected duration. Avoid a frightening
technical dashboard; the estimate should help users make a decision at a glance.

### Audio recording

Design one concise audio section with these three inputs:

- **Record game audio** — captures the selected Windows/game output, including voice chat when it
  is already part of that output. Include an output-device selector.
- **Include microphone** — mixes the selected microphone into the same clip-audio stream.
- **Include voice chat** — mixes a separately routed communications device into the same stream.
  Include a communications-device selector and default it to the Windows default communications
  device.

All enabled inputs are compiled into one final audio stream, matching ClipCord's existing behavior.
Do not show or promise separate game, microphone, or voice-chat tracks in the editor. Explain that
voice chat is already included when it uses the game output, and that the separate communications
selector is for chat routed elsewhere. Keep selectors consistent with current ClipCord fields.
When every audio input is disabled, the estimate should remove the one 192 kbps audio stream.

### Reaction camera

- **Include reaction camera** is Off by default.
- Enabling it must lead into a clear camera-consent state, device selection, and preview—not silently
  activate the webcam.
- Explain that unclipped webcam frames remain memory-only and are discarded; a saved clip keeps its
  synchronized camera segment locally as a separate editable layer.
- Show the persistent camera-active indicator that will also exist in the tray/overlay.
- This design does not need to solve silhouette placement; it should link forward to the editor,
  where YouTube defaults bottom-right and TikTok/Shorts defaults raised bottom-center.

### Recording location and folder structure

- Default location: `%USERPROFILE%\Videos\ClipCord`.
- Controls: **Change folder** and **Open folder**.
- Explain: “Originals are organized by game. Each filename includes its recording date and time. Uploads never move or replace the original.”
- Show a compact, polished folder-structure preview or info popover using this exact model:

```text
ClipCord\
├─ Library\
│  └─ Game\
│     └─ Valorant\
│        └─ Valorant__2026-08-22__14-35-41.mp4
├─ Exports\
│  ├─ Discord\Valorant\
│  ├─ YouTube\Valorant\
│  └─ TikTok\Valorant\
└─ .clipcord\
   ├─ Projects\
   ├─ Staging\
   └─ Thumbnails\
```

`Library` contains the one original. `Exports` contains only user-requested platform renditions.
`.clipcord` is app-managed project/cache data and should be visually de-emphasized. Route status is
metadata inside ClipCord, not a reason to duplicate or move the original.

Design these storage states as well:

- Valid local folder with available-space summary.
- Folder missing or disconnected.
- Insufficient space for the selected replay profile.
- Network/cloud-synced folder warning recommending a local disk for reliable capture.

## Required Figma deliverables

1. Desktop Capture settings — default Ready configuration.
2. Instant Replay Off / first opt-in state.
3. Active buffering state with locked quality controls.
4. Camera permission and preview state.
5. Unsupported 4K60 / no hardware encoder state.
6. Constrained-height or responsive layout demonstrating scrolling and no cutoff controls.
7. Component/state sheet for any newly created Capture icon, quality selector, status indicator,
   estimate panel, or folder preview.
8. A short annotation block specifying spacing, sizes, selected/hover/focus/disabled states, and the
   exact node names intended for implementation handoff.

Verify before reporting completion:

- Exact 1080p/1440p/4K and 30/60 FPS options exist.
- The estimate changes visibly and never claims exact output size.
- 1080p60/60 sec with audio shows about 144 MB with the 116–181 MB range.
- Audio inputs are mixed into one stream; no separately editable audio tracks are shown or promised.
- Game audio, microphone, and voice chat each have an explicit input control and relevant device
  selector.
- Folder organization visibly preserves one original across multiple destinations.
- Camera is never presented as silently active.
- Every control has a readable label, keyboard-focus treatment, disabled treatment, and sufficient
  contrast.
- No control, helper text, or Open/Change folder action is clipped at the designed size.

When finished, provide the Figma file URL, node-specific links for every deliverable frame, a list
of new or modified components, and any product questions. Do not modify the repository.
