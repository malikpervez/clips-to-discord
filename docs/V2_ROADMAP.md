# ClipCord 2.0 roadmap

ClipCord 2.0 changes the product promise from watching another recorder's folder to a local-first
creator pipeline:

> One action captures the moment, the player's reaction, and independently editable audio, then
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
The next spike owns frame acquisition, GPU conversion, encoder enumeration, and measurements only;
it does not ship as an end-user recorder.

### 1. Manual recording

- Explicit start and stop controls with an unmistakable recording indicator.
- Hardware H.264 encoding with game/system audio and microphone on separate tracks.
- A shared monotonic clock for video and audio timestamps.
- Atomic completion into the selected clip folder using the existing Gallery/uploader pipeline.
- Recovery that never presents a partial MP4 as a completed recording.

### 2. Replay buffer

- Ring-buffer encoded packets, never raw video frames.
- Configurable duration, resolution, frame rate, bitrate, and hotkey.
- A keyframe index and bounded memory/storage accounting.
- Audio drift correction over long sessions.

### 3. Independent game, microphone, and chat audio

- Default-device loopback remains the compatibility fallback.
- Per-process capture separates the game and voice-chat process when Windows supports it.
- Every recording remains usable if chat isolation is unavailable.
- The editor exposes each track without rewriting the original.

### 4. Consent-based reaction camera

- Camera buffering is off by default and requires explicit, blocking consent.
- A persistent tray/overlay state indicates whenever the camera buffer is active.
- Unclipped camera data stays memory-only and is discarded when capture stops.
- A saved reaction segment remains a separate local layer, synchronized to the gameplay clip.
- Camera failure never prevents the gameplay clip from being saved.

### 5. Silhouette composition

- Background removal runs after capture or during export, not continuously during gameplay.
- Use a commercially distributable model with a separately verified weights license.
- Prefer DirectML so NVIDIA, AMD, and Intel users share one path.
- Apply temporal smoothing and full-resolution edge refinement.
- Default layouts: bottom-right for YouTube 16:9 and raised bottom-center for TikTok/Shorts.
- Placement, scale, mirroring, outline, and visibility remain non-destructive editor settings.

### 6. Publishing destinations

- Generate separate Discord, YouTube, YouTube Shorts, and TikTok renditions from one project.
- Start with explicit preview-and-publish actions and encrypted OAuth credentials.
- Implement resumable uploads, retries, idempotency, and per-destination completion state.
- Treat TikTok review, consent, and publishing UI requirements as ship gates.

### 7. Routes

- Triggers: captured, edited, favorited, or manually approved.
- Conditions: game, duration, tags, orientation, and destination.
- Transforms: trim, reframe, silhouette, audio mix, captions, and compression.
- Actions: save locally, Discord, YouTube, and TikTok draft/direct post.
- Begin with guided recipes; add a visual Zapier-style builder only after common recipes are known.

## Invariants

- Local-first is the default. Capture does not imply upload.
- Original gameplay and camera sources are never destructively rewritten.
- Camera and microphone use are always visible and independently controllable.
- Optional devices and newer Windows APIs degrade gracefully.
- Secrets never enter logs, diagnostics, route files, or media metadata.
- The 1.x public repository and updater remain operational while 2.0 is developed privately.
- Every external dependency, native binary, and model weight receives a commercial-distribution
  license review before it enters a production package.
