# ClipCord 2.0 roadmap

ClipCord 2.0 changes the product promise from watching another recorder's folder to a local-first
creator pipeline:

> One action captures the moment, the player's reaction, and the chosen audio inputs, then
> prepares the clip for each destination without uploading anything the user has not approved.

The existing watched-folder, Gallery, editor, and Discord paths remain supported. ClipCord-owned
recordings deliberately enter that same pipeline rather than creating a parallel library.

## Delivery sequence

### 0. Capture feasibility gate

- Use `Windows.Graphics.Capture` with a free-threaded frame pool.
- Keep conversion and hardware encoding on the GPU.
- Measure 1080p60 and 1440p60 while a real game is GPU-bound.
- Continue only when sustained capture adds no more than 5% frame-time overhead and produces no
  material stutter.
- Test NVIDIA, AMD, and Intel hardware separately; CI validates contracts with fakes, not devices.

The first committed foundation supplies the Windows capability seam and ClipCord output naming.
Phase 0A owns frame acquisition, GPU copies, encoder enumeration, and callback measurements. Phase
0B adds BGRA→NV12 conversion, actual hardware H.264 encoding to a discard sink, GOP control, and the
PresentMon comparison. Neither spike ships as an end-user recorder.

### 1. Manual recording

- Run capture work in an isolated child process launched from the same signed ClipCord executable.
  A versioned, current-user-only named pipe owns control messages; the host exits when ClipCord exits
  and can be restarted without restarting the main interface.
- Explicit start and stop controls with an unmistakable recording indicator.
- Hardware H.264 encoding with enabled game, microphone, and voice-chat inputs mixed into one
  compatible clip-audio stream.
- A shared monotonic clock for video and audio timestamps.
- Atomic completion into the selected clip folder using the existing Gallery/uploader pipeline.
- Recovery that never presents a partial MP4 as a completed recording.
- Automatic targeting recognizes a stable foreground game from local Windows/game-library signals,
  creates a capture item for that exact HWND, and fails closed to the explicit picker when uncertain.

ClipCord-owned media uses one storage model from the beginning:

```text
ClipCord\
├─ Library\Game\<Game>\<Game>__<yyyy-MM-dd>__<HH-mm-ss>.mp4
├─ Exports\<Destination>\<Game>\<rendition>.mp4
└─ .clipcord\{Projects,Staging,Thumbnails}\
```

The folder hierarchy stays shallow. Each original clip carries its local recording date and time in the filename instead of adding year and date folders.

`Library` holds the one original; a route records status in ClipCord metadata rather than moving or
duplicating that original. `Exports` contains only user-requested destination renditions.

### 2. Replay buffer

- Ring-buffer encoded packets, never raw video frames.
- Configurable duration, resolution, frame rate, bitrate, and hotkey.
- A keyframe index and bounded memory/storage accounting.
- Audio drift correction over long sessions.

The current 2.0 branch now implements the first usable replay slice: exact-window H.264 segments stay
in a duration- and byte-bounded memory ring; enabled audio inputs stay in timestamped PCM rings; the
global save shortcut snapshots and finalizes them through private staging into the shared Gallery
library while the live buffer continues. Cross-GPU performance measurement and long-session drift
soak tests remain phase gates before release.

### 3. Independent game, microphone, and chat inputs

- Default-device loopback remains the compatibility fallback.
- A separate communications-device input includes voice chat when it is routed away from the game
  output. Chat on the game output is already included by loopback capture.
- Every recording remains usable if chat isolation is unavailable.
- Enabled inputs are mixed into one encoded audio stream; the editor does not promise separate
  source tracks.

### 4. Consent-based reaction camera

- Camera capture is off by default and requires explicit, blocking, one-time ClipCord consent;
  Windows camera privacy remains a separate permission and can deny access independently.
- Apart from an explicitly requested consent preview, the camera opens only while a manual recording
  or recognized-game replay buffer is actively capturing. Instant Replay waiting for a game does not
  open the camera.
- A persistent Figma-based tray state indicates while the camera is starting or active, and remains
  visible with exit guidance if Windows does not confirm device release promptly.
- Unsaved replay camera segments stay encoded in bounded RAM and are discarded when buffering stops
  or ClipCord exits.
- A saved reaction segment remains synchronized to the gameplay clip as a separate local project
  layer beneath `.clipcord\Projects`; the gameplay original is never rewritten.
- An owned camera project whose gameplay file remains missing for 30 days is reclaimed at startup;
  unknown project data is never deleted by that reconciliation.
- Camera denial, disconnection, contention, or encoding failure degrades to gameplay-only capture and
  never prevents the gameplay clip from being saved.
- The first local silhouette pipeline now performs post-capture background removal and independent
  Landscape/Portrait composition. Upload routing for those renditions remains later work.

### 5. Silhouette composition

- Background removal runs after capture or during export, not continuously during gameplay.
- Capture remains responsible only for immutable gameplay and synchronized Reaction Camera sources;
  silhouette inference and rendering run in a separate post-capture composition path.
- Use the author-distributed MODNet photographic ONNX model under Apache-2.0, pinned by SHA-256.
- Run it through ONNX Runtime DirectML with a CPU fallback so NVIDIA, AMD, and Intel users share one
  path, with inference isolated from both the app UI and the capture host.
- Apply temporal smoothing and full-resolution edge refinement.
- Exactly two reusable output layouts exist: Landscape 16:9 and Portrait 9:16. Landscape starts
  enabled; Portrait is optional; Reaction Camera capture always retains at least one selected output.
- Landscape defaults to bottom-right. Portrait defaults to Context framing: the complete 16:9
  gameplay frame remains visible over a blurred/dimmed 9:16 fill, with the silhouette raised at
  bottom-center. Focus Crop and Custom are explicit user choices rather than automatic guesses.
- If both outputs are selected, background removal runs once and both renditions reuse the same
  matte. A failed rendition is retried independently and never invalidates a ready sibling.
- Completed formats are visible siblings of the gameplay source in `Library\Game\<Game>`, named
  with `__Reaction-Camera__Landscape.mp4` and `__Reaction-Camera__Portrait.mp4` suffixes. Gallery
  folds only fingerprint-valid project outputs into the source card; unrelated lookalike files stay
  visible and are never overwritten.
- Discord/standard YouTube consume Landscape while TikTok/Shorts consume Portrait through future
  routing rules; destinations do not own or duplicate the layout preferences.
- Placement is stored as normalized bottom-center anchors and canvas-relative height, never pixels
  or DPI-scaled values. Each capture snapshots the resolved defaults so later edits affect only
  future clips.
- Placement, scale, mirroring, outline, and visibility remain non-destructive editor settings in an
  atomic `composition.json` beside the immutable capture `project.json`.
- Gallery joins a ClipCord gameplay clip to a camera project only after validating the deterministic
  project identity, both bounded media paths, and non-empty ordinary MP4 files.
- Capture-project schema v2 binds gameplay and camera files to SHA-256 plus byte length and commits
  the capture-time `composition.json` in the same atomic project promotion. The compositor must
  still probe both media durations and reconcile the stored clock offset instead of trusting
  container duration to equal the capture-clock span.
- The durable `renditions.json` state models one shared matte and independent Landscape/Portrait
  attempts with generation-CAS updates, fingerprint-backed Ready validation, exact temporary names,
  and atomic artifact promotion. The low-priority worker runs after the immutable camera project
  commits and can recover abandoned Processing/Rendering or Committing states after restart.

### 6. Publishing destinations

- Route the project’s Landscape and/or Portrait rendition to one or more destinations without
  regenerating destination-named copies of the same visual layout.
- Start with explicit preview-and-publish actions and encrypted OAuth credentials.
- Implement resumable uploads, retries, idempotency, and per-destination completion state.
- Treat TikTok review, consent, and publishing UI requirements as ship gates.

### 7. Routes

- Triggers: captured, edited, favorited, or manually approved.
- Conditions: game, duration, tags, orientation, and destination.
- Transforms: trim, reframe, silhouette, audio mix, captions, and compression.
- Actions: save locally, Discord, YouTube, and TikTok draft/direct post.
- Begin with guided recipes; add a visual Zapier-style builder only after common recipes are known.
- The Discord-first foundation now includes crash-safe plans, encrypted named Discord connections,
  operator retry/approval controls, the Figma-aligned Routes editor, and an active production
  runtime shared by ClipCord Capture and the exact SteelSeries/NVIDIA watched-folder adapters.
- The first activation quiesces the 1.x watcher, converts its initial ignored-file baseline to
  content-hash exclusions, commits the equivalent Discord or File into Library route, and verifies
  that snapshot before writing durable Routing execution authority. The prepared migration and
  authority also carry privacy-safe hashes for the exact Capture library path and native Windows
  directory identity. Cold startup verifies strict Capture settings and both hashes before orphan
  cleanup, journal recovery, capture-host startup, Gallery access, outbox reconciliation, or active
  Routing work. The live process retains a no-delete handle for that directory and shares one
  renewable permit across capture, silhouette, Gallery, planning, delivery, and filing boundaries.
  A lifetime monitor rechecks that permit while Capture is available. Losing settings, migration,
  authority, or native-identity evidence revokes the hotkey, aborts the isolated recorder without
  finalizing its active clip, and pauses every downstream path before another file or outbox
  mutation. Bound repair accepts only the exact previously authorized directory. Before any
  binding exists, reversible Legacy mode may validate and pin a user-selected ordinary directory
  as fresh strict settings. Both repair paths require a restart before Capture components return.
  A Legacy library switch is rejected while recording, replay, camera, or rendition work is active;
  the candidate is pinned and recovered before its settings and live permit replace the old root.
  A stale reversible preparation
  passes through an `Aborting` marker, removes only its exact migration-owned route, and can then
  re-plan from the latest drained Legacy state. Once the marker itself is committed, it becomes a
  no-Legacy recovery fence even if a crash happens before the separate authority write; the next
  launch resumes Routing under that fence. Unreadable or inconsistent migration/authority evidence
  pauses processing instead of ever falling back to the legacy watcher.
- One process-local ownership lease and one serialized outbox executor fence both source families.
  Startup reconciles the Capture journal before admitting new watched-folder occurrences. Watched
  clips are committed to an immutable, source-bound journal after containment, native-file identity,
  stability, media, and post-probe identity checks; Capture clips enter through their existing
  atomic project journal. Both paths resolve artifacts, persist planned deliveries before sends,
  guard physical Discord deliveries with durable receipts, and file completed work through the same
  route lifecycle.
- Xbox Game DVR clips synced through OneDrive are an additive named Routing source, not a replacement
  for the SteelSeries/NVIDIA watched folder. Route setup offers From now, Last 24 hours, or Last 7
  days, optionally narrowed to one game, and freezes the exact approved historical occurrence and
  revision identities so a cloud folder cannot produce an accidental backlog blast. Setup enumerates
  top-level MP4 metadata without hydrating content. Only an admitted clip is opened, copied into
  `Library\Game\<Game>`, verified, promoted atomically, and passed through the existing route plan;
  the OneDrive original is never moved, renamed, written, or deleted. Each named source persists its
  native root authority before a route can bind it. Health recovery is compare-and-swap and
  fail-closed: an unchanged authority can be rechecked, while a replaced folder retires the old
  source and receives a new opaque source id that requires fresh route and history confirmation.
  A supported corrupt or conflicting clip pauses only that Xbox source; the user can explicitly skip
  the durable occurrence without changing the OneDrive original, after which later clips resume.
- SteelSeries GG and NVIDIA can now be added as additional named Routing sources at arbitrary local
  folder locations without changing the frozen 1.x migrated source. Registration preserves an
  immutable metadata-only **from now on** baseline, routes bind to the exact opaque source id, and a
  single optional supervisor scans every enabled named source through the established recorder
  layout adapter. One unavailable or conflicting source is isolated from the others. Completed clips
  retain the established watched-folder behavior and move into that source root's `uploaded` or
  `local-only` archive; Activity and Gallery resolve the same durable source authority. These recorder
  sources are bound to their saved canonical paths: reconnecting the original path can restore an
  existing source, but choosing a different path registers a disabled replacement with a new source id
  and baseline. It does not inherit routes or pending work from the retired source.
- A first watched-folder comparison seam is now available but remains disabled by default. When a
  developer explicitly selects Shadow mode, the existing legacy watcher stays the sole scanner and
  owner: after it has accepted a stable, hashed SteelSeries or NVIDIA clip, a fail-open observer runs
  the pure route evaluator and appends a bounded, path-free per-occurrence summary to a separate
  shadow document. Equal content at a second path is marked as duplicate evidence without discarding
  that occurrence's route and filing projection. Observation itself has a bounded timeout; a slow or
  failed comparison yields to the unchanged legacy path.
  Shadow mode cannot create production outbox work, call a provider, move or claim a file, alter the
  legacy decision, or advertise routing source coverage. It remains a separate developer comparison
  aid and is not part of the active runtime.
- Active authority deliberately freezes the legacy watched root, capture-source kind, Capture
  library path/native directory identity, upload-mode switch, and legacy webhook identity. Those
  fields require a future explicit source/destination switch transaction; unrelated preferences
  remain editable while Routing continues processing.

## Invariants

- Local-first is the default. Capture does not imply upload.
- Original gameplay and camera sources are never destructively rewritten.
- Camera and microphone use are always visible and independently controllable.
- Optional devices and newer Windows APIs degrade gracefully.
- Secrets never enter logs, diagnostics, route files, or media metadata.
- The 1.x public repository and updater remain operational while 2.0 is developed privately.
- Every external dependency, native binary, and model weight receives a commercial-distribution
  license review before it enters a production package.
