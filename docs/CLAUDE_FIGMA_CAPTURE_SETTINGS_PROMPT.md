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

The estimate must respond immediately to resolution, FPS, duration, number of audio tracks, and the
reaction-camera toggle. It is a variable-bitrate range, never a guaranteed exact size.

Initial H.264 targets, including three 192 kbps audio tracks, for a 60-second clip:

| Profile | Expected | Honest range |
|---|---:|---:|
| 1080p30 | about 90 MB | 72–112 MB |
| 1080p60 | about 147 MB | 118–184 MB |
| 1440p30 | about 176 MB | 141–220 MB |
| 1440p60 | about 255 MB | 204–318 MB |
| 4K30 | about 326 MB | 261–408 MB |
| 4K60 | about 469 MB | 375–586 MB |

Use the selected duration to scale these values. Enabling the separate reaction-camera layer adds
approximately **43 MB per minute**, before variable-bitrate range. Suggested primary copy for the
default profile:

> Estimated clip size: about 147 MB
>
> Usually 118–184 MB for a 60-second clip. Motion and detail affect the final size.

Also show the approximate live replay-buffer memory for the selected duration. Avoid a frightening
technical dashboard; the estimate should help users make a decision at a glance.

### Audio tracks

Design independent rows for:

- Game audio
- Microphone
- Voice chat

The result is up to three separately editable tracks. Voice chat may fall back to the mixed game
audio track when Windows or the selected app cannot isolate it. Communicate that fallback plainly.
Include device selectors for microphone and output device, but keep their treatment consistent with
current ClipCord fields.

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
- Explain: “Originals are organized by game and date. Uploads never move or replace the original.”
- Show a compact, polished folder-structure preview or info popover using this exact model:

```text
ClipCord\
├─ Library\
│  └─ Valorant\
│     └─ 2026\
│        └─ 2026-08-22\
│           └─ Valorant__2026-08-22__14-35-41.mp4
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
- 1080p60/60 sec shows about 147 MB with the 118–184 MB range.
- Folder organization visibly preserves one original across multiple destinations.
- Camera is never presented as silently active.
- Every control has a readable label, keyboard-focus treatment, disabled treatment, and sufficient
  contrast.
- No control, helper text, or Open/Change folder action is clipped at the designed size.

When finished, provide the Figma file URL, node-specific links for every deliverable frame, a list
of new or modified components, and any product questions. Do not modify the repository.
