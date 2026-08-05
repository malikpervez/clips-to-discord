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

Settings changes and tray upload-mode changes use a stricter handoff than application exit: the app disables another reconfiguration, cancels the current controller, awaits its complete watcher cleanup without the exit timeout, and only then constructs the replacement. This prevents independent workers from saving stale copies of the same watch state. If applying the new configuration fails, ClipCord attempts to restore both the previous persisted settings and controller before reporting the error.

The scanner and both clip consumers share mutable watch state. Every runtime collection read, mutation, enumeration, and save is serialized through the same semaphore. Baseline construction happens before the consumers start.

## Successful-upload ordering

After Discord returns success, the app immediately adds the content hash and source path to state, writes a temporary state file, flushes it to disk, and atomically replaces the live state file. Only then does it attempt the archive move. A failed move therefore cannot trigger another upload; it remains a persisted pending move.

Local-only decisions use a separate persisted hash set and pending-move queue. That destination is saved before the move, so a crash, restart, or later setting change cannot reinterpret a withheld clip as a Discord upload. Existing `local-only` archives are included in safe-baseline hashing but are never marked as uploaded.

There is still an irreducible interval between Discord accepting the HTTP request and the local durable write. Incoming Discord webhooks do not offer an idempotency key. Eliminating duplicates completely would require choosing at-most-once behavior, which could silently lose a clip if the app died before Discord accepted it. The app keeps at-least-once delivery and narrows the interval to the immediate durable state write.

## Compression fallback

The configured target is 1–100 MB and defaults to 95 MB for new settings. Existing saved values are preserved. After a size rejection, FFmpeg compresses from the original clip. If Discord rejects that result, the app retries progressively smaller targets, up to five compression attempts; the default sequence reaches 9 MB on its fifth attempt.

Two-pass compression uses the Windows `NUL` device for its first pass. The app targets Windows, and the compressor also has an explicit platform guard so this path cannot silently run with different semantics elsewhere.

Discord documents a 10 MiB default per-file limit and notes that limits can be higher based on user or server status. See the official [API file-upload reference](https://docs.discord.com/developers/reference#uploading-files) and [status codes](https://docs.discord.com/developers/topics/opcodes-and-status-codes).

## State and migration safety

Legacy files are copied into a staging directory. State is installed before settings, and a marker is created before either becomes active. If migration is interrupted, the next watcher startup ignores questionable state and builds a safe content-hash baseline before uploading anything.

Missing or unreadable state always produces a baseline of existing top-level clips. Existing clips are never treated as a new upload queue merely because migration state is absent. When an interrupted-migration marker forces the baseline, readable upload and local-only pending moves are salvaged first so each clip still reaches its previously selected archive.

## Stable update discovery

Automatic checks persist their attempt time before contacting GitHub and run no more than once every 24 hours; an hourly UI timer only asks the coordinator whether a check is due. Manual checks bypass that schedule, and a semaphore rejects overlapping checks.

The client has a five-second connection deadline, a linked twelve-second deadline covering headers and every response-body read, and bounded release/checksum response sizes. It sends only fixed GitHub API headers—never the webhook, uploader name, clip paths, or application settings. Network, timeout, rate-limit, and malformed-response failures produce a non-fatal result and do not share the watcher or uploader lifecycle.

Stable mode calls only the repository's fixed `releases/latest` endpoint. The response is still treated as untrusted: draft/prerelease flags, semantic version, official HTTPS release path, exact installer name/state/path, and SHA-256 evidence are validated before presentation. GitHub's installer asset digest is preferred; a small official `SHA256SUMS.txt` asset is a bounded fallback. Automatic redirects are disabled globally; the checksum fallback may follow exactly one HTTPS redirect to an allow-listed GitHub asset CDN host. Duplicate installer assets, checksum assets, or installer entries are rejected as ambiguous. Branches, pull requests, tags without releases, and Actions artifacts cannot enter this path.

Skip and reminder choices live in a versioned `updates.json` written through a flushed temporary file and same-directory replacement. Missing, older, or corrupt preference data falls back to safe defaults. A skipped version does not hide a later version, deferring a newer release does not erase an older skip, and a reminder expires after 24 hours. Reminder deadlines more than 24 hours in the future are treated as clock skew rather than suppressing a release indefinitely.

**View changes** opens the already validated GitHub release page. **Install update** is an explicit user action that starts a separate, cancellable 30-minute download. The installer is capped at 512 MiB, follows at most one HTTPS redirect to an allow-listed GitHub asset CDN, and must match both the release's exact byte length and SHA-256 digest. It is streamed to a uniquely named partial file, flushed to disk, and moved to the final per-version path only after verification; failures and cancellation remove the partial.

An already staged file is reused only after its length and digest are checked again. Before execution, the launcher confines the exact filename to `%LOCALAPPDATA%\ClipsToDiscord\updates\v<version>`, rejects reparse points, rehashes the file, and retains a read-only handle until setup has started so it cannot be replaced in that gap. The tray application defers shutdown until its update dialogs unwind, then exits and releases its named mutex before setup starts. A launch failure leaves the current installation unchanged and ClipCord attempts to reopen itself; if setup itself later reports failure, setup attempts to reopen whichever installed executable remains. Ordinary automatic checks never download or execute anything.

## Webhook validation and logging

Validation permits Discord's unversioned `/api/webhooks/...` path and versioned `/api/v{number}/webhooks/...` path while retaining HTTPS and Discord-host allow-list checks.

All log output passes through a centralized redactor. It removes the exact registered webhook and pattern-matches supported Discord webhook URL forms. The webhook remains DPAPI-encrypted at rest and is never intentionally included in a log call.
