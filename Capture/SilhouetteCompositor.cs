using System.Globalization;

namespace ClipsToDiscord;

internal sealed record SilhouetteRenderPlan(
    int CanvasWidth,
    int CanvasHeight,
    int CameraWidth,
    int CameraHeight,
    int CameraCropX,
    int CameraCropY,
    int CameraCropWidth,
    int CameraCropHeight,
    int SubjectX,
    int SubjectY,
    int SubjectWidth,
    int SubjectHeight,
    SilhouetteLayoutVariant Layout,
    PortraitCompositionSettings PortraitComposition);

internal static class SilhouetteCompositor
{
    internal const int PortraitWidth = 1080;
    internal const int PortraitHeight = 1920;
    private const int OutlinePadding = 24;

    internal static async Task RenderAsync(
        string ffmpegPath,
        string gameplayPath,
        string cameraPath,
        string mattePath,
        string outputPath,
        CaptureProjectManifest manifest,
        CaptureCompositionDocument composition,
        SilhouetteMatteAnalysis matte,
        string orientationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameplayPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(mattePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(matte);
        cancellationToken.ThrowIfCancellationRequested();

        var gameplay = await FfmpegCompressor.ProbeMediaAsync(
            gameplayPath,
            ffmpegPath,
            cancellationToken).ConfigureAwait(false);
        var camera = await FfmpegCompressor.ProbeMediaAsync(
            cameraPath,
            ffmpegPath,
            cancellationToken).ConfigureAwait(false);
        var arguments = BuildArguments(
            gameplayPath,
            cameraPath,
            mattePath,
            outputPath,
            manifest,
            composition,
            matte,
            gameplay,
            camera,
            orientationId);
        await FfmpegCompressor.RunAsync(ffmpegPath, arguments, cancellationToken)
            .ConfigureAwait(false);
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
        {
            throw new InvalidOperationException(
                "ClipCord's local silhouette renderer did not create an output clip.");
        }
        var output = await FfmpegCompressor.ProbeMediaAsync(
            outputPath,
            ffmpegPath,
            cancellationToken).ConfigureAwait(false);
        ValidateRenderedOutput(manifest, gameplay, output);
    }

    internal static void ValidateRenderedOutput(
        CaptureProjectManifest manifest,
        FfmpegCompressor.MediaProbe gameplay,
        FfmpegCompressor.MediaProbe output)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(gameplay);
        ArgumentNullException.ThrowIfNull(output);
        // The capture manifest records the camera timeline observed by the live encoder.
        // Finalizing the independent gameplay/audio pipeline can legitimately produce a
        // slightly shorter MP4 (startup and shutdown frames are not guaranteed to reach the
        // muxer). The immutable gameplay file is the authoritative output timeline: it is the
        // base layer the user is preserving, and FFmpeg cannot extend it merely because the
        // camera encoder observed a longer wall-clock interval.
        var expected = ResolveOutputDuration(manifest, gameplay);
        if (output.VideoWidth <= 0 || output.VideoHeight <= 0 ||
            Math.Abs((output.Duration - expected).TotalSeconds) > 0.35)
        {
            throw new InvalidDataException(
                "ClipCord's local silhouette rendition failed media validation.");
        }
    }

    internal static IReadOnlyList<string> BuildArguments(
        string gameplayPath,
        string cameraPath,
        string mattePath,
        string outputPath,
        CaptureProjectManifest manifest,
        CaptureCompositionDocument composition,
        SilhouetteMatteAnalysis matte,
        FfmpegCompressor.MediaProbe gameplay,
        FfmpegCompressor.MediaProbe camera,
        string orientationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameplayPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(mattePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(matte);
        ArgumentNullException.ThrowIfNull(gameplay);
        ArgumentNullException.ThrowIfNull(camera);
        if (gameplay.VideoWidth <= 0 || gameplay.VideoHeight <= 0 ||
            camera.VideoWidth <= 0 || camera.VideoHeight <= 0)
        {
            throw new InvalidDataException("The source video dimensions are unavailable.");
        }
        var outputDuration = ResolveOutputDuration(manifest, gameplay);
        var plan = CreatePlan(composition, matte, gameplay, camera, orientationId);
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y", "-i", gameplayPath
        };
        AddSynchronizedInput(arguments, cameraPath, manifest.CameraStartOffset);
        AddSynchronizedInput(arguments, mattePath, manifest.CameraStartOffset);
        arguments.AddRange([
            "-filter_complex", BuildFilter(plan, gameplay, orientationId),
            "-map", "[clipcordout]",
            "-map", "0:a?",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
            "-threads", "2", "-pix_fmt", "yuv420p",
            "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709",
            "-c:a", "copy", "-movflags", "+faststart",
            "-t", FormatSeconds(outputDuration),
            "-f", "mp4",
            outputPath
        ]);
        return arguments;
    }

    internal static TimeSpan ResolveOutputDuration(
        CaptureProjectManifest manifest,
        FfmpegCompressor.MediaProbe gameplay)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(gameplay);
        if (manifest.DurationTicks <= 0)
        {
            throw new InvalidDataException("The capture project duration is invalid.");
        }
        if (gameplay.Duration <= TimeSpan.Zero)
        {
            throw new InvalidDataException("The gameplay clip duration is unavailable.");
        }
        return gameplay.Duration;
    }

    internal static SilhouetteRenderPlan CreatePlan(
        CaptureCompositionDocument composition,
        SilhouetteMatteAnalysis matte,
        FfmpegCompressor.MediaProbe gameplay,
        FfmpegCompressor.MediaProbe camera,
        string orientationId)
    {
        if (!CompositionOrientationIds.All.Contains(orientationId, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "The silhouette orientation is not supported.",
                nameof(orientationId));
        }
        var layout = composition.SilhouetteLayouts.Single(candidate =>
            candidate.ProfileId.Equals(orientationId, StringComparison.Ordinal));
        if (!layout.Enabled)
        {
            throw new InvalidOperationException("The silhouette orientation is disabled.");
        }
        var canvasWidth = orientationId == CompositionOrientationIds.Portrait
            ? PortraitWidth
            : MakeEven(gameplay.VideoWidth);
        var canvasHeight = orientationId == CompositionOrientationIds.Portrait
            ? PortraitHeight
            : MakeEven(gameplay.VideoHeight);
        var cameraBounds = matte.SubjectBounds;
        var cropX = Math.Clamp(
            (int)Math.Floor(cameraBounds.Left * camera.VideoWidth),
            0,
            camera.VideoWidth - 2);
        var cropY = Math.Clamp(
            (int)Math.Floor(cameraBounds.Top * camera.VideoHeight),
            0,
            camera.VideoHeight - 2);
        var cropRight = Math.Clamp(
            (int)Math.Ceiling(cameraBounds.Right * camera.VideoWidth),
            cropX + 2,
            camera.VideoWidth);
        var cropBottom = Math.Clamp(
            (int)Math.Ceiling(cameraBounds.Bottom * camera.VideoHeight),
            cropY + 2,
            camera.VideoHeight);
        var cropWidth = cropRight - cropX;
        var cropHeight = cropBottom - cropY;
        var layerAspect = cropWidth / (double)cropHeight;
        var canvasAspect = canvasWidth / (double)canvasHeight;
        var fitted = CaptureCompositionModel.FitToCanvas(
            layout.Transform,
            layerAspect,
            canvasAspect);
        var subjectWidth = MakeEven(Math.Max(2, (int)Math.Round(fitted.Bounds.Width * canvasWidth)));
        var subjectHeight = MakeEven(Math.Max(2, (int)Math.Round(fitted.Bounds.Height * canvasHeight)));
        var subjectX = Math.Clamp(
            (int)Math.Round(fitted.Bounds.Left * canvasWidth),
            0,
            canvasWidth - subjectWidth);
        var subjectY = Math.Clamp(
            (int)Math.Round(fitted.Bounds.Top * canvasHeight),
            0,
            canvasHeight - subjectHeight);
        return new SilhouetteRenderPlan(
            canvasWidth,
            canvasHeight,
            camera.VideoWidth,
            camera.VideoHeight,
            cropX,
            cropY,
            cropWidth,
            cropHeight,
            subjectX,
            subjectY,
            subjectWidth,
            subjectHeight,
            layout with { Transform = fitted.Transform },
            CaptureCompositionModel.NormalizePortraitComposition(
                composition.PortraitComposition));
    }

    private static string BuildFilter(
        SilhouetteRenderPlan plan,
        FfmpegCompressor.MediaProbe gameplay,
        string orientationId)
    {
        var filter = BuildGameplayBase(plan, gameplay, orientationId);
        if (!plan.Layout.Transform.Visible)
        {
            return filter + ";[clipcordbase]null[clipcordout]";
        }
        var mirror = plan.Layout.Transform.MirrorHorizontally ? ",hflip" : string.Empty;
        filter +=
            $";[1:v]scale={plan.CameraWidth}:{plan.CameraHeight}:flags=lanczos,format=rgba[clipcordcamera]" +
            $";[2:v]scale={plan.CameraWidth}:{plan.CameraHeight}:flags=bicubic,format=gray[clipcordalpha]" +
            $";[clipcordcamera][clipcordalpha]alphamerge," +
            $"crop={plan.CameraCropWidth}:{plan.CameraCropHeight}:{plan.CameraCropX}:{plan.CameraCropY}" +
            $"{mirror},scale={plan.SubjectWidth}:{plan.SubjectHeight}:flags=lanczos,format=rgba," +
            $"pad=iw+{OutlinePadding}:ih+{OutlinePadding}:{OutlinePadding / 2}:{OutlinePadding / 2}:color=black@0[clipcordsubject]";

        var overlayX = plan.SubjectX - OutlinePadding / 2;
        var overlayY = plan.SubjectY - OutlinePadding / 2;
        if (plan.Layout.Transform.Outline == SilhouetteOutlineStyle.None)
        {
            filter += $";[clipcordbase][clipcordsubject]overlay={overlayX}:{overlayY}:" +
                      "eof_action=pass:repeatlast=0:shortest=0[clipcordout]";
            return filter;
        }

        var sigma = plan.Layout.Transform.Outline == SilhouetteOutlineStyle.Strong ? 9 : 5;
        filter +=
            ";[clipcordsubject]split=2[clipcordforeground][clipcordhalo]" +
            $";[clipcordhalo]gblur=sigma={sigma}:steps=2,lutrgb=r=255:g=255:b=255[clipcordoutline]" +
            $";[clipcordbase][clipcordoutline]overlay={overlayX}:{overlayY}:" +
            "eof_action=pass:repeatlast=0:shortest=0[clipcordoutlined]" +
            $";[clipcordoutlined][clipcordforeground]overlay={overlayX}:{overlayY}:" +
            "eof_action=pass:repeatlast=0:shortest=0[clipcordout]";
        return filter;
    }

    private static string BuildGameplayBase(
        SilhouetteRenderPlan plan,
        FfmpegCompressor.MediaProbe gameplay,
        string orientationId)
    {
        if (orientationId == CompositionOrientationIds.Landscape)
        {
            return $"[0:v]scale={plan.CanvasWidth}:{plan.CanvasHeight}:" +
                "force_original_aspect_ratio=decrease:flags=lanczos," +
                $"pad={plan.CanvasWidth}:{plan.CanvasHeight}:(ow-iw)/2:(oh-ih)/2:black," +
                "setsar=1[clipcordbase]";
        }

        var portrait = plan.PortraitComposition;
        if (portrait.Layout == PortraitGameplayLayoutMode.FocusCrop)
        {
            var visibleFraction = (9d / 16d) / (16d / 9d) / portrait.FocusCrop.Zoom;
            var cropWidth = MakeEven(Math.Max(2, (int)Math.Round(gameplay.VideoWidth * visibleFraction)));
            var cropX = Math.Clamp(
                (int)Math.Round(
                    gameplay.VideoWidth * portrait.FocusCrop.FocalCenterX - cropWidth / 2d),
                0,
                gameplay.VideoWidth - cropWidth);
            return $"[0:v]crop={cropWidth}:{gameplay.VideoHeight}:{cropX}:0," +
                   $"scale={PortraitWidth}:{PortraitHeight}:flags=lanczos,setsar=1[clipcordbase]";
        }

        var gameplayWidth = MakeEven(Math.Max(
            2,
            (int)Math.Round(PortraitWidth * portrait.Gameplay.WidthFraction)));
        var gameplayHeight = MakeEven(Math.Max(
            2,
            (int)Math.Round(gameplayWidth * 9d / 16d)));
        var gameplayX = Math.Clamp(
            (int)Math.Round(PortraitWidth * portrait.Gameplay.CenterX - gameplayWidth / 2d),
            0,
            PortraitWidth - gameplayWidth);
        var gameplayY = Math.Clamp(
            (int)Math.Round(PortraitHeight * portrait.Gameplay.TopY),
            0,
            PortraitHeight - gameplayHeight);
        return
            "[0:v]split=2[clipcordbackgroundsource][clipcordgameplaysource]" +
            $";[clipcordbackgroundsource]scale={PortraitWidth}:{PortraitHeight}:" +
            "force_original_aspect_ratio=increase:flags=lanczos," +
            $"crop={PortraitWidth}:{PortraitHeight},gblur=sigma=28," +
            "eq=brightness=-0.20[clipcordbackground]" +
            $";[clipcordgameplaysource]scale={gameplayWidth}:{gameplayHeight}:flags=lanczos," +
            "setsar=1[clipcordgameplay]" +
            $";[clipcordbackground][clipcordgameplay]overlay={gameplayX}:{gameplayY}" +
            "[clipcordbase]";
    }

    private static void AddSynchronizedInput(
        ICollection<string> arguments,
        string path,
        TimeSpan offset)
    {
        if (offset > TimeSpan.Zero)
        {
            arguments.Add("-itsoffset");
            arguments.Add(FormatSeconds(offset));
        }
        else if (offset < TimeSpan.Zero)
        {
            arguments.Add("-ss");
            arguments.Add(FormatSeconds(offset.Duration()));
        }
        arguments.Add("-i");
        arguments.Add(path);
    }

    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;

    private static string FormatSeconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);
}
