# Reliability design and tradeoffs

## File readiness

The scanner requires the last write to be at least twenty seconds old and records length plus last-write time across observations at least ten seconds apart. It then opens the file with read sharing: other readers are allowed, but any active writer causes a sharing violation. This is stronger than a fully shared read and avoids the false permanent lock caused by an unrelated reader that makes `FileShare.None` too strict.

Unreadable or writer-locked files use exponential retry delays from ten seconds up to five minutes. After three consecutive open failures the app logs the stuck clip, then throttles repeated notices to once every five minutes. A file that continues changing returns to the stability window instead of entering the upload queue.

## Content identity

SHA-256 of the complete file is the stable duplicate key. Renaming a clip, changing its timestamp, or renaming the configured folder does not change that identity.

The cost is one extra sequential disk read before upload. This is intentional: metadata-only keys are cheap but cannot distinguish a real new clip from a renamed or retimestamped old one. Hashes are cached for the current attempt and persisted after baselining or successful upload.

## Upload queue and deadlines

Discovery writes to a bounded queue with two consumers. If one upload stalls, the second consumer and scanner continue. Connection establishment has a 15-second timeout, and each complete multipart upload has a five-minute deadline.

Failed uploads retain the existing five-minute retry delay. Cancellation caused by Discord closing stops active requests cleanly.

Discord must be absent for three consecutive two-second polls before the worker is cancelled. The controller records that exit reason instead of polling again, so a quick updater relaunch cannot strand it awaiting a still-running worker. Each linked cancellation source remains alive until its worker task has been observed. Application exit waits up to ten seconds on the UI thread; if cleanup takes longer, it remains observed in the background rather than hanging the tray application.

The scanner and both upload consumers share mutable watch state. Every runtime collection read, mutation, enumeration, and save is serialized through the same semaphore. Baseline construction happens before the consumers start.

## Successful-upload ordering

After Discord returns success, the app immediately adds the content hash and source path to state, writes a temporary state file, flushes it to disk, and atomically replaces the live state file. Only then does it attempt the archive move. A failed move therefore cannot trigger another upload; it remains a persisted pending move.

There is still an irreducible interval between Discord accepting the HTTP request and the local durable write. Incoming Discord webhooks do not offer an idempotency key. Eliminating duplicates completely would require choosing at-most-once behavior, which could silently lose a clip if the app died before Discord accepted it. The app keeps at-least-once delivery and narrows the interval to the immediate durable state write.

## Compression fallback

The configured target is 1–100 MB and defaults to 95 MB for new settings. Existing saved values are preserved. After a size rejection, FFmpeg compresses from the original clip. If Discord rejects that result, the app retries progressively smaller targets, up to five compression attempts; the default sequence reaches 9 MB on its fifth attempt.

Two-pass compression uses the Windows `NUL` device for its first pass. The app targets Windows, and the compressor also has an explicit platform guard so this path cannot silently run with different semantics elsewhere.

Discord documents a 10 MiB default per-file limit and notes that limits can be higher based on user or server status. See the official [API file-upload reference](https://docs.discord.com/developers/reference#uploading-files) and [status codes](https://docs.discord.com/developers/topics/opcodes-and-status-codes).

## State and migration safety

Legacy files are copied into a staging directory. State is installed before settings, and a marker is created before either becomes active. If migration is interrupted, the next watcher startup ignores questionable state and builds a safe content-hash baseline before uploading anything.

Missing or unreadable state always produces a baseline of existing top-level clips. Existing clips are never treated as a new upload queue merely because migration state is absent. When an interrupted-migration marker forces the baseline, any readable `PendingMoves` are salvaged first so already-uploaded clips still reach the archive folder.

## Webhook validation and logging

Validation permits Discord's unversioned `/api/webhooks/...` path and versioned `/api/v{number}/webhooks/...` path while retaining HTTPS and Discord-host allow-list checks.

All log output passes through a centralized redactor. It removes the exact registered webhook and pattern-matches supported Discord webhook URL forms. The webhook remains DPAPI-encrypted at rest and is never intentionally included in a log call.
