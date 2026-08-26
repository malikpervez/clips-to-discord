# ClipCord Capture Spike

This private development tool is Phase 0A of the ClipCord 2.0 capture feasibility gate. It does not
ship with ClipCord and it does not save or upload captured frames.

It currently validates:

- the Windows Graphics Capture picker and free-threaded frame pool;
- D3D11 frame surfaces and GPU-to-GPU texture copies without CPU readback;
- installed Media Foundation hardware H.264 encoder discovery; and
- frame delivery rate plus p50/p95/p99 callback time.

Run it from an interactive Windows terminal:

```powershell
dotnet run --project .\tools\ClipCord.CaptureSpike\ClipCord.CaptureSpike.csproj -c Release -- --seconds 15
```

Select the game window or display in the Windows picker. The result is printed as JSON after the
requested duration. Cancelling the picker exits without capturing.

The Codex command host may cause the system picker to cancel immediately; run the command directly
in an interactive terminal when that occurs.

This is not the complete Phase 0 gate. Phase 0B must connect BGRA→NV12 GPU conversion to the
enumerated hardware H.264 Media Foundation transform, force the intended GOP, and write encoded
packets to a discard sink. Only then should PresentMon compare baseline and capture frame time in a
real GPU-bound game at 1080p60 and 1440p60. The feature proceeds when sustained capture adds no more
than 5% frame-time overhead and causes no material stutter.
