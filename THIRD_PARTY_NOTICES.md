# Third-party notices

ClipCord release packages may include an `ffmpeg.exe` binary to compress clips that Discord rejects for size.

FFmpeg is a separate project and is not covered by this repository's MIT License. Release packages use the pinned Gyan.dev FFmpeg 8.1.2 essentials build, licensed under GPLv3, and include the license shipped with that build. The archive and extracted files are verified against repository-pinned SHA-256 values before packaging. FFmpeg source and licensing information are available from the FFmpeg project and the binary build provider.

This repository does not commit or vendor an FFmpeg binary.

ClipCord uses NAudio 2.3.0 for Windows audio endpoint discovery and WASAPI capture.
NAudio is Copyright (c) 2001-2026 Mark Heath and contributors and is distributed
under the MIT License. Its source and license are available from the NAudio project.

ClipCord uses Vortice.Direct3D11 3.8.3 from the Vortice.Windows project for Direct3D 11
interop in the local Windows capture pipeline. Vortice.Windows is Copyright (c) Amer Koleci
and contributors and is distributed under the MIT License. Its source and license are
available from the Vortice.Windows project.

ClipCord redistributes the official, unmodified MODNet photographic portrait-matting ONNX
model for offline Reaction Camera background removal. MODNet's authors release the code,
models, and demos under the Apache License 2.0. The bundled model is pinned to SHA-256
`07c308cf0fc7e6e8b2065a12ed7fc07e1de8febb7dc7839d7b7f15dd66584df9`.
The complete license is included as `licenses/MODNet-APACHE-2.0.txt`.

ClipCord uses Microsoft.ML.OnnxRuntime.DirectML 1.24.4 to run that model locally with
DirectML acceleration and a CPU fallback. ONNX Runtime is distributed under the MIT License.
ClipCord also redistributes Microsoft.AI.DirectML 1.15.4 under Microsoft's DirectML
redistribution terms. Their complete license and third-party-notice files are included in the
`licenses` folder. These dependencies and the model run locally; ClipCord does not upload camera
frames for silhouette inference.
