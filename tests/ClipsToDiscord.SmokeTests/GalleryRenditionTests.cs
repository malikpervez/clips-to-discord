using System.Reflection;
using ClipsToDiscord;

internal static class GalleryRenditionTests
{
    internal static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "gallery-renditions");
        Directory.CreateDirectory(root);
        AssertFixedRenditionsAreNotStandaloneClips(root);
        RunSta(() =>
        {
            AssertProjectPresentationAndRetry(root);
            AssertSettingsFormRelay(root);
        });
    }

    private static void AssertFixedRenditionsAreNotStandaloneClips(string root)
    {
        var external = Path.Combine(root, "external");
        var capture = Path.Combine(root, "capture");
        Directory.CreateDirectory(external);
        var valid = CreateReadyLandscapeProject(
            capture,
            "Example Game",
            "Example Game__2026-08-25__12-00-00.mp4",
            seed: 11);
        var changed = CreateReadyLandscapeProject(
            capture,
            "Example Game",
            "Example Game__2026-08-25__12-01-00.mp4",
            seed: 29);
        File.WriteAllBytes(changed.OutputPath, [201, 202, 203]);
        var collisionOwner = CreateReadyLandscapeProject(
            capture,
            "Example Game",
            "Collision Owner__2026-08-25__12-02-00.mp4",
            seed: 41);
        var collisionProject = CreateReactionCameraProject(
            capture,
            collisionOwner.OutputPath,
            seed: 53);

        var game = Path.GetDirectoryName(valid.GameplayPath)!;
        var legacyFixedName = Path.Combine(game, SilhouetteArtifactStore.LandscapeFileName);
        var lookalikeName = Path.Combine(
            game,
            Path.GetFileNameWithoutExtension(valid.OutputPath) + "-copy.mp4");
        File.WriteAllBytes(legacyFixedName, [4, 5]);
        File.WriteAllBytes(lookalikeName, [6, 7]);

        var snapshot = GalleryCatalog.Scan(
            external,
            CancellationToken.None,
            captureLibraryRoot: capture);
        var clips = snapshot.Games.SelectMany(entry => entry.Clips).ToArray();
        var collisionMatches = clips.Where(clip => clip.Path.Equals(
            collisionOwner.OutputPath,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert(
            collisionMatches.Length == 1 &&
            collisionMatches[0].CaptureProject?.ProjectId == collisionProject.ProjectId,
            "The project-source collision guard must preserve exactly one card for the project whose gameplay source is another project's computed rendition path, and that card must retain the correct capture project.");
        Assert(
            clips.Count(clip => clip.Path.Equals(valid.GameplayPath, StringComparison.OrdinalIgnoreCase)) == 1 &&
            clips.All(clip =>
                !clip.Path.Equals(valid.OutputPath, StringComparison.OrdinalIgnoreCase)) &&
            clips.Any(clip => clip.Path.Equals(changed.GameplayPath, StringComparison.OrdinalIgnoreCase)) &&
            clips.Any(clip => clip.Path.Equals(changed.OutputPath, StringComparison.OrdinalIgnoreCase)) &&
            clips.Count(clip => clip.Path.Equals(
                collisionOwner.GameplayPath,
                StringComparison.OrdinalIgnoreCase)) == 1 &&
            clips.Any(clip => clip.Path.Equals(legacyFixedName, StringComparison.OrdinalIgnoreCase)) &&
            clips.Any(clip => clip.Path.Equals(lookalikeName, StringComparison.OrdinalIgnoreCase)) &&
            clips.All(clip => clip.Source == GalleryClipSource.ClipCord),
            "Only a fingerprint-valid Ready exact manifest-derived rendition path may fold into its source card; a path that is also another validated project's gameplay source, changed canonical, legacy fixed-name, and lookalike user files must remain discoverable.");
    }

    private static CaptureProjectSaveResult CreateReactionCameraProject(
        string libraryRoot,
        string gameplayPath,
        byte seed)
    {
        var staging = CaptureLibraryLayout.GetStagingDirectory(libraryRoot);
        Directory.CreateDirectory(staging);
        var cameraStage = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(
            cameraStage,
            Enumerable.Range(0, 64).Select(index => (byte)(seed ^ index)).ToArray());
        var createdUtc = new DateTimeOffset(2026, 8, 25, 12, seed % 50, 0, TimeSpan.Zero);
        var settings = CaptureSettings.Default with
        {
            LibraryRoot = libraryRoot,
            SilhouetteLandscapeEnabled = true,
            SilhouettePortraitEnabled = false
        };
        var composition = CaptureCompositionSnapshotFactory.Create(
            settings,
            gameplayPath,
            mirrorCamera: true,
            createdUtc);
        return CaptureProjectStore.SaveReactionCameraLayerAsync(
                libraryRoot,
                gameplayPath,
                cameraStage,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(10),
                mirrorCamera: true,
                composition,
                createdUtc)
            .GetAwaiter()
            .GetResult();
    }

    private static (string GameplayPath, string OutputPath) CreateReadyLandscapeProject(
        string libraryRoot,
        string gameName,
        string gameplayFileName,
        byte seed)
    {
        var game = Path.Combine(
            libraryRoot,
            CaptureLibraryLayout.LibraryFolderName,
            CaptureLibraryLayout.GameFolderName,
            gameName);
        var staging = CaptureLibraryLayout.GetStagingDirectory(libraryRoot);
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(staging);
        var gameplay = Path.Combine(game, gameplayFileName);
        var cameraStage = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(
            gameplay,
            Enumerable.Range(0, 96).Select(index => (byte)(seed + index)).ToArray());
        File.WriteAllBytes(
            cameraStage,
            Enumerable.Range(0, 128).Select(index => (byte)(seed ^ index)).ToArray());
        var createdUtc = new DateTimeOffset(2026, 8, 25, 12, seed % 50, 0, TimeSpan.Zero);
        var settings = CaptureSettings.Default with
        {
            LibraryRoot = libraryRoot,
            SilhouetteLandscapeEnabled = true,
            SilhouettePortraitEnabled = false
        };
        var composition = CaptureCompositionSnapshotFactory.Create(
            settings,
            gameplay,
            mirrorCamera: true,
            createdUtc);
        var project = CaptureProjectStore.SaveReactionCameraLayerAsync(
                libraryRoot,
                gameplay,
                cameraStage,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(10),
                mirrorCamera: true,
                composition,
                createdUtc)
            .GetAwaiter()
            .GetResult();

        var current = SilhouetteRenditionStore.CreateInitial(
            libraryRoot,
            project.ProjectId,
            "gallery-rendition-test",
            new string('a', 64),
            createdUtc.AddSeconds(1));
        current = SilhouetteRenditionStore.SaveAsync(
                libraryRoot,
                project.ProjectId,
                current,
                expectedGeneration: 0)
            .GetAwaiter()
            .GetResult();
        void Persist(SilhouetteRenditionDocument candidate) =>
            current = SilhouetteRenditionStore.SaveAsync(
                    libraryRoot,
                    project.ProjectId,
                    candidate,
                    expectedGeneration: current.Generation)
                .GetAwaiter()
                .GetResult();

        Persist(SilhouetteRenditionModel.StartMatte(current, createdUtc.AddSeconds(2)));
        var matteAttempt = current.Matte.AttemptGeneration;
        var matteTemporary = SilhouetteArtifactStore.CreateMatteTemporaryPath(
            libraryRoot,
            project.ProjectId,
            matteAttempt);
        File.WriteAllBytes(matteTemporary, [seed, (byte)(seed + 1), (byte)(seed + 2)]);
        var matteFingerprint = SilhouetteArtifactStore.FingerprintTemporaryAsync(
                libraryRoot,
                project.ProjectId,
                matteTemporary)
            .GetAwaiter()
            .GetResult();
        Persist(SilhouetteRenditionModel.BeginMatteCommit(
            current,
            matteAttempt,
            matteFingerprint,
            createdUtc.AddSeconds(3)));
        var promotedMatte = SilhouetteArtifactStore.PromoteMatteAsync(
                libraryRoot,
                project.ProjectId,
                matteAttempt,
                matteTemporary,
                matteFingerprint)
            .GetAwaiter()
            .GetResult();
        Persist(SilhouetteRenditionModel.CompleteMatte(
            current,
            matteAttempt,
            promotedMatte,
            createdUtc.AddSeconds(4)));

        Persist(SilhouetteRenditionModel.StartOutput(
            current,
            CompositionOrientationIds.Landscape,
            createdUtc.AddSeconds(5)));
        var output = current.Outputs.Single(candidate =>
            candidate.OrientationId == CompositionOrientationIds.Landscape);
        var outputTemporary = SilhouetteArtifactStore.CreateOutputTemporaryPath(
            libraryRoot,
            project.ProjectId,
            CompositionOrientationIds.Landscape,
            output.AttemptGeneration);
        File.WriteAllBytes(
            outputTemporary,
            Enumerable.Range(0, 257).Select(index => (byte)(seed * 3 + index)).ToArray());
        var outputFingerprint = SilhouetteArtifactStore.FingerprintTemporaryAsync(
                libraryRoot,
                project.ProjectId,
                outputTemporary)
            .GetAwaiter()
            .GetResult();
        Persist(SilhouetteRenditionModel.BeginOutputCommit(
            current,
            CompositionOrientationIds.Landscape,
            output.AttemptGeneration,
            outputFingerprint,
            createdUtc.AddSeconds(6)));
        var promotedOutput = SilhouetteArtifactStore.PromoteOutputAsync(
                libraryRoot,
                project.ProjectId,
                CompositionOrientationIds.Landscape,
                output.AttemptGeneration,
                outputTemporary,
                outputFingerprint)
            .GetAwaiter()
            .GetResult();
        Persist(SilhouetteRenditionModel.CompleteOutput(
            current,
            CompositionOrientationIds.Landscape,
            output.AttemptGeneration,
            promotedOutput,
            createdUtc.AddSeconds(7)));

        return (
            gameplay,
            SilhouetteArtifactStore.GetOutputPath(
                libraryRoot,
                project.ProjectId,
                CompositionOrientationIds.Landscape));
    }

    private static void AssertProjectPresentationAndRetry(string root)
    {
        var gameplay = Path.Combine(root, "project-gameplay.mp4");
        var camera = Path.Combine(root, "reaction-camera.mp4");
        var landscape = Path.Combine(root, SilhouetteArtifactStore.LandscapeFileName);
        var portrait = Path.Combine(root, SilhouetteArtifactStore.PortraitFileName);
        File.WriteAllBytes(gameplay, [1, 2, 3, 4]);
        File.WriteAllBytes(camera, [5, 6, 7]);
        File.WriteAllBytes(landscape, [8, 9, 10, 11, 12]);
        File.WriteAllBytes(portrait, [13, 14, 15, 16]);
        const string projectId =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var outputs = new GalleryRenditionOutputPresentation[]
        {
            new(
                CompositionOrientationIds.Landscape,
                "Landscape",
                GalleryRenditionOutputStatus.Ready,
                landscape,
                new FileInfo(landscape).Length,
                FailureReason: null),
            new(
                CompositionOrientationIds.Portrait,
                "Portrait",
                GalleryRenditionOutputStatus.Failed,
                portrait,
                Length: 0,
                "ClipCord could not render this format.")
        };
        var presentation = new GalleryRenditionPresentation(
            root,
            projectId,
            GalleryRenditionAggregateStatus.PartiallyReady,
            outputs,
            gameplay,
            camera,
            SilhouetteRenditionLoadStatus.Loaded,
            GalleryCatalog.GetRenditionStateStamp(root, projectId),
            "1 of 2 formats is ready. The other format needs attention.");
        var project = new CaptureProjectSummary(
            presentation.ProjectId,
            gameplay,
            Path.Combine(root, CaptureProjectStore.ManifestFileName),
            camera,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10),
            MirrorCamera: true);
        var clip = new GalleryClipEntry(
            gameplay,
            Path.GetFileName(gameplay),
            "Example Game",
            GalleryClipRoute.LocalOnly,
            new FileInfo(gameplay).Length,
            File.GetLastWriteTimeUtc(gameplay),
            GalleryClipSource.ClipCord,
            project,
            presentation);

        var readyPortrait = outputs[1] with
        {
            Status = GalleryRenditionOutputStatus.Ready,
            Length = new FileInfo(portrait).Length,
            FailureReason = null
        };
        var bothReady = clip with
        {
            Renditions = presentation with
            {
                Status = GalleryRenditionAggregateStatus.Ready,
                Outputs = [readyPortrait, outputs[0]]
            }
        };
        Assert(
            GalleryView.SelectPreferredReadyRendition(bothReady)?.OrientationId ==
            CompositionOrientationIds.Landscape,
            "The main clip Play action must prefer a playable Landscape reaction rendition even when Portrait is listed first.");
        var portraitOnly = bothReady with
        {
            Renditions = bothReady.Renditions! with
            {
                Outputs =
                [
                    readyPortrait,
                    outputs[0] with { Status = GalleryRenditionOutputStatus.Failed }
                ]
            }
        };
        Assert(
            GalleryView.SelectPreferredReadyRendition(portraitOnly)?.OrientationId ==
            CompositionOrientationIds.Portrait,
            "The main clip Play action must fall back to a playable Portrait reaction rendition.");
        Assert(
            !GalleryView.ShouldStackRenditionActions(144) &&
            !GalleryView.ShouldStackRenditionActions(192) &&
            GalleryView.GetRenditionOutputRowLogicalHeight(192, canPlay: true) == 82 &&
            GalleryView.ShouldStackRenditionActions(288) &&
            GalleryView.GetRenditionOutputRowLogicalHeight(288, canPlay: true) == 148 &&
            GalleryView.GetRenditionOutputRowLogicalHeight(288, canPlay: false) == 82,
            "Rendition actions must remain inline at 150/200% DPI and stack only when a playable row would overflow at 300% DPI.");

        var actionFavorites = new FakeFavoritesService();
        var playbackPreparer = new RecordingPlaybackPreparer();
        using (var actionGallery = new GalleryView(
                   root,
                   playbackPreparer: playbackPreparer,
                   favorites: actionFavorites,
                   captureLibraryRoot: root))
        using (var actionForm = new Form
               {
                   ClientSize = new Size(960, 672),
                   ShowInTaskbar = false,
                   StartPosition = FormStartPosition.Manual,
                   Location = new Point(-32000, -32000)
               })
        {
            actionGallery.Dock = DockStyle.Fill;
            actionForm.Controls.Add(actionGallery);
            (string Title, string Subtitle)? lastHeader = null;
            actionGallery.HeaderChanged += (title, subtitle) =>
                lastHeader = (title, subtitle);
            actionForm.Show();
            Application.DoEvents();

            var playClip = typeof(GalleryView).GetMethod(
                "PlayClip",
                BindingFlags.Instance | BindingFlags.NonPublic) ??
                throw new InvalidOperationException("Gallery PlayClip action was not found.");
            playClip.Invoke(actionGallery, [bothReady]);
            Assert(
                PumpUntil(() => playbackPreparer.SourcePaths.Count == 1) &&
                playbackPreparer.SourcePaths[0].Equals(landscape, StringComparison.OrdinalIgnoreCase),
                "The Gallery Play action must send the preferred Ready Landscape artifact through ClipPlayerView playback.");
            Assert(
                lastHeader == (
                    "Play clip",
                    $"Example Game · Landscape rendition · {Path.GetFileName(landscape)}"),
                "Rendition playback must publish the exact played orientation and artifact identity in the Gallery header subtitle.");
            var renditionPlayer = Find<ClipPlayerView>(actionGallery, "ClipPlayerView");
            var renditionTitle = Find<Label>(renditionPlayer, "ClipPlayerTitle");
            var favorite = Find<FavoriteButton>(renditionPlayer, "ClipPlayerFavoriteButton");
            var favoriteAccessibleName = favorite.AccessibleName ?? string.Empty;
            Assert(
                renditionTitle.Text == Path.GetFileName(landscape) &&
                favoriteAccessibleName.Contains(Path.GetFileName(gameplay), StringComparison.Ordinal) &&
                !favoriteAccessibleName.Contains(Path.GetFileName(landscape), StringComparison.Ordinal),
                "A rendition player must identify the file being played while its favorite control names the source clip owner.");
            typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(favorite, [EventArgs.Empty]);
            Assert(
                actionFavorites.SetCalls == 1 &&
                actionFavorites.LastSetClip is not null &&
                actionFavorites.LastSetClip.Path.Equals(gameplay, StringComparison.OrdinalIgnoreCase) &&
                actionFavorites.LastFavorite == true,
                "Favoriting from rendition playback must mutate the source clip owner, never the generated artifact.");

            var noReady = clip with
            {
                Renditions = presentation with
                {
                    Status = GalleryRenditionAggregateStatus.Failed,
                    Outputs = outputs.Select(output => output with
                    {
                        Status = GalleryRenditionOutputStatus.Failed,
                        FailureReason = "Test failure"
                    }).ToArray()
                }
            };
            playClip.Invoke(actionGallery, [noReady]);
            Assert(
                PumpUntil(() => playbackPreparer.SourcePaths.Count == 2) &&
                playbackPreparer.SourcePaths[1].Equals(gameplay, StringComparison.OrdinalIgnoreCase) &&
                Find<Label>(Find<ClipPlayerView>(actionGallery, "ClipPlayerView"), "ClipPlayerTitle").Text ==
                Path.GetFileName(gameplay) &&
                lastHeader == (
                    "Play clip",
                    $"Example Game · Local only · {Path.GetFileName(gameplay)}"),
                "When no Ready reaction rendition exists, the Gallery Play action and exact header subtitle must fall back to the original source clip.");
        }

        foreach (var dpi in new[] { 144, 192, 288 })
        {
            AssertRenderedProjectDetailLayout(root, bothReady, dpi);
        }

        using var gallery = new GalleryView(
            root,
            favorites: new FakeFavoritesService(),
            captureLibraryRoot: root);
        GalleryRenditionRetryRequestedEventArgs? request = null;
        gallery.RenditionRetryRequested += (_, eventArgs) => request = eventArgs;

        var buildPanel = typeof(GalleryView).GetMethod(
            "BuildRenditionPanel",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Gallery rendition panel builder was not found.");
        using var panel = (Control)(buildPanel.Invoke(gallery, [clip, gameplay]) ??
            throw new InvalidOperationException("Gallery rendition panel was not created."));
        var landscapePlay = Find<OutlineButton>(panel, "GalleryLandscapeRenditionPlayButton");
        var landscapeFolder = Find<OutlineButton>(panel, "GalleryLandscapeRenditionFolderButton");
        var portraitRetry = Find<OutlineButton>(panel, "GalleryPortraitRenditionRetryButton");
        Assert(
            landscapePlay.Enabled &&
            landscapePlay.LeadingIcon == FigmaIconAsset.Play &&
            landscapeFolder.Enabled &&
            landscapeFolder.LeadingIcon == FigmaIconAsset.Folder &&
            portraitRetry.Enabled &&
            portraitRetry.LeadingIcon == FigmaIconAsset.Refresh,
            "Ready rendition actions and the failed output retry must use the approved Figma assets and state gates.");
        using var readyPanel = (Control)(buildPanel.Invoke(gallery, [bothReady, landscape]) ??
            throw new InvalidOperationException("Ready Gallery rendition panel was not created."));
        var readyChip = Find<RoundedPanel>(readyPanel, "GalleryRenditionStatusChip");
        Assert(
            readyChip.BackColor == ClipCordTheme.SuccessSurface &&
            readyChip.BorderColor == ClipCordTheme.SuccessBorder &&
            readyChip.Controls.OfType<Label>().Single().ForeColor == ClipCordTheme.SuccessText,
            "Ready rendition status must use the shared ClipCord success tokens.");
        portraitRetry.PerformClick();
        Assert(
            request is not null &&
            request.LibraryRoot == root &&
            request.ProjectId == presentation.ProjectId &&
            request.OrientationId == CompositionOrientationIds.Portrait &&
            !portraitRetry.Enabled &&
            portraitRetry.Text == "Retry requested",
            "A per-output retry must emit the exact library/project/orientation identity once.");

        var missingReady = outputs[0] with
        {
            ArtifactPath = Path.Combine(root, "missing-landscape.mp4")
        };
        Assert(!missingReady.CanPlay,
            "A Ready presentation must not enable Play after its validated artifact disappears.");

        var buildDetail = typeof(GalleryView).GetMethod(
            "BuildProjectDetailContent",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Gallery project detail builder was not found.");
        var player = new ClipPlayerView(clip);
        using var detailHost = new BrandedScrollHost
        {
            Size = new Size(900, 635),
            Content = (Control)(buildDetail.Invoke(gallery, [player, clip, gameplay]) ??
                throw new InvalidOperationException("Gallery project detail content was not created."))
        };
        detailHost.RefreshContentLayout(preservePosition: false);
        detailHost.PerformLayout();
        detailHost.RefreshContentLayout(preservePosition: false);
        var detail = detailHost.Content!;
        var detailPanel = Find<Control>(detail, "GalleryRenditionPanel");
        var detailPlayer = Find<ClipPlayerView>(detail, "ClipPlayerView");
        var playerSurface = Find<Control>(detailPlayer, "ClipPlayerSurface");
        Assert(
            !detail.AutoSize &&
            detail.Width >= 850 &&
            detailPanel.Top == 0 &&
            detailPlayer.Top >= detailPanel.Bottom &&
            playerSurface.Height >= 250 &&
            detailPanel.Right <= detail.ClientSize.Width &&
            detailPlayer.Right <= detail.ClientSize.Width,
            "The rendition selector must stay above a full-width, usable player inside the branded scroll host.");

        var buildCard = typeof(GalleryView).GetMethod(
            "BuildClipCard",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Gallery clip card builder was not found.");
        using var card = (Control)(buildCard.Invoke(gallery, [bothReady]) ??
            throw new InvalidOperationException("Gallery clip card was not created."));
        var formatsBadge = Find<RoundedPanel>(card, "GalleryClipFormatsBadge");
        var formatsBadgeLabel = formatsBadge.Controls.OfType<Label>().Single();
        Assert(
            Enumerate(card).OfType<FavoriteButton>().Count() == 1 &&
            card.Controls.Find("GalleryClipFormatsBadge", searchAllChildren: true).Length == 1 &&
            formatsBadge.BackColor == ClipCordTheme.SuccessSurface &&
            formatsBadge.BorderColor == ClipCordTheme.SuccessBorder &&
            formatsBadgeLabel.ForeColor == ClipCordTheme.SuccessText,
            "One capture project must remain one Gallery/favorite owner with one success-token formats badge.");
    }

    private static void AssertRenderedProjectDetailLayout(
        string root,
        GalleryClipEntry clip,
        int effectiveDpi)
    {
        var playbackPreparer = new RecordingPlaybackPreparer();
        var settings = new AppSettings(
            root,
            string.Empty,
            StartWithWindows: false,
            AppSettings.DefaultCompressionTargetMb,
            "Gallery rendition layout QA",
            UploadToDiscord: false);
        var captureSettings = CaptureSettings.Default with { LibraryRoot = root };
        var scaledFonts = new List<Font>();
        using var form = new SettingsForm(
            settings,
            checkForUpdatesAsync: _ => Task.CompletedTask,
            initialPage: SettingsPage.Gallery,
            playbackPreparer: playbackPreparer,
            favorites: new FakeFavoritesService(),
            captureSettings: captureSettings,
            silhouetteSettingsDirectory: root);
        try
        {
            form.Show();
            form.ShowPage(SettingsPage.Gallery);
            Application.DoEvents();
            var gallery = Find<GalleryView>(form, "GalleryView");
            gallery.Deactivate();
            gallery.SetEffectiveDpiForTests(effectiveDpi);

            var playClip = typeof(GalleryView).GetMethod(
                "PlayClip",
                BindingFlags.Instance | BindingFlags.NonPublic) ??
                throw new InvalidOperationException("Gallery PlayClip action was not found.");
            playClip.Invoke(gallery, [clip]);
            Assert(
                PumpUntil(() => gallery.Controls.Find(
                    "GalleryProjectDetailContent",
                    searchAllChildren: true).Length == 1),
                $"The rendered Gallery detail page was not created at {effectiveDpi} DPI.");
            Assert(
                Find<Label>(form, "PageSubtitleLabel").Text ==
                $"Example Game · Landscape rendition · {Path.GetFileName(clip.Renditions!.Outputs.Single(output => output.OrientationId == CompositionOrientationIds.Landscape).ArtifactPath)}",
                $"The shared Gallery header must retain the exact rendition identity at {effectiveDpi} DPI.");

            // Keep the shell at Claude's exact constrained 1200x760 physical host. Scaling
            // the host itself would give the 300% row three times as much room and make the
            // original right-anchored action overflow invisible to this regression test.
            form.Hide();
            var deviceScale = Math.Max(96, form.DeviceDpi) / 96f;
            var effectiveScale = effectiveDpi / 96f;
            var relativeScale = effectiveScale / deviceScale;
            var detailForFontScaling = Find<Control>(gallery, "GalleryProjectDetailContent");
            if (Math.Abs(relativeScale - 1f) > 0.001f)
            {
                form.Scale(new SizeF(relativeScale, relativeScale));
                foreach (var control in new[] { detailForFontScaling }.Concat(Enumerate(detailForFontScaling)))
                {
                    var scaled = new Font(
                        control.Font.FontFamily,
                        control.Font.Size * relativeScale,
                        control.Font.Style,
                        GraphicsUnit.Point);
                    scaledFonts.Add(scaled);
                    control.Font = scaled;
                }
            }
            form.MinimumSize = Size.Empty;
            form.MaximumSize = Size.Empty;
            form.ClientSize = new Size(1200, 760);
            // Claude's reference host was a 144-DPI desktop, with the 216px shell rail
            // already laid out before its synthetic higher-DPI pass. Anchor chrome to
            // that physical baseline so this reproduction has the same content pressure
            // on 96-, 144-, and 192-DPI test runners.
            var qaChromeScale = effectiveDpi / 144f;
            var shell = Find<TableLayoutPanel>(form, "RootLayout");
            shell.ColumnStyles[0].SizeType = SizeType.Absolute;
            shell.ColumnStyles[0].Width = (int)Math.Round(
                SettingsForm.NavigationRailLogicalWidth * qaChromeScale);
            shell.RowStyles[0].SizeType = SizeType.Absolute;
            shell.RowStyles[0].Height = (int)Math.Round(
                SettingsForm.PageHeaderLogicalHeight * qaChromeScale);
            form.PerformLayout();
            gallery.RefreshViewport();
            Application.DoEvents();

            var detail = Find<Control>(gallery, "GalleryProjectDetailContent");
            var panel = Find<Control>(detail, "GalleryRenditionPanel");
            var player = Find<ClipPlayerView>(detail, "ClipPlayerView");
            var playerSurface = Find<Control>(player, "ClipPlayerSurface");
            Assert(
                detail.Width > 0 &&
                panel.Width == detail.ClientSize.Width &&
                player.Width == detail.ClientSize.Width &&
                panel.Top == 0 &&
                player.Top >= panel.Bottom &&
                playerSurface.Width >= 200 &&
                playerSurface.Height >= 200,
                $"The constrained full detail page must preserve a full-width, usable player beneath the rendition panel at {effectiveDpi} DPI: " +
                $"detail={detail.Bounds}, panel={panel.Bounds}, player={player.Bounds}, surface={playerSurface.Bounds}.");

            foreach (var orientation in new[] { "Landscape", "Portrait" })
            {
                var row = Find<Control>(panel, $"Gallery{orientation}RenditionRow");
                var actions = Find<FlowLayoutPanel>(row, $"Gallery{orientation}RenditionActions");
                var title = Find<Label>(row, $"Gallery{orientation}RenditionTitle");
                var copy = Find<Label>(row, $"Gallery{orientation}RenditionDetail");
                var play = Find<OutlineButton>(row, $"Gallery{orientation}RenditionPlayButton");
                var folder = Find<OutlineButton>(row, $"Gallery{orientation}RenditionFolderButton");
                var actionBounds = GetBoundsRelativeToAncestor(actions, row);
                var titleBounds = GetBoundsRelativeToAncestor(title, row);
                var copyBounds = GetBoundsRelativeToAncestor(copy, row);
                Assert(
                    ContainsWithTolerance(row.ClientRectangle, actionBounds) &&
                    ContainsWithTolerance(row.ClientRectangle, titleBounds) &&
                    ContainsWithTolerance(row.ClientRectangle, copyBounds) &&
                    title.Width >= 48 && title.Height > 0 &&
                    copy.Width >= 48 && copy.Height > 0 &&
                    !string.IsNullOrWhiteSpace(title.Text) &&
                    !string.IsNullOrWhiteSpace(copy.Text),
                    $"The {orientation} actions and both copy rows must remain inside their rendered output row at {effectiveDpi} DPI in a 1200x760 host: " +
                    $"row={row.ClientRectangle}, actions={actionBounds}, title={titleBounds}, detail={copyBounds}, flow={actions.FlowDirection}.");
                Assert(
                    effectiveDpi >= 288
                        ? actions.FlowDirection == FlowDirection.TopDown && folder.Top >= play.Bottom
                        : actions.FlowDirection == FlowDirection.LeftToRight && folder.Left >= play.Right,
                    $"The {orientation} action controls must use the expected rendered arrangement at {effectiveDpi} DPI.");
            }

            using var bitmap = new Bitmap(
                Math.Max(1, detail.ClientSize.Width),
                Math.Max(1, detail.ClientSize.Height));
            detail.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        }
        finally
        {
            foreach (var font in scaledFonts) font.Dispose();
        }
    }

    private static Rectangle GetBoundsRelativeToAncestor(Control control, Control ancestor)
    {
        var location = Point.Empty;
        for (Control? current = control;
             current is not null && !ReferenceEquals(current, ancestor);
             current = current.Parent)
        {
            location.Offset(current.Left, current.Top);
        }
        return new Rectangle(location, control.Size);
    }

    private static bool ContainsWithTolerance(Rectangle outer, Rectangle inner)
    {
        const int tolerance = 1;
        return inner.Left >= outer.Left - tolerance &&
               inner.Top >= outer.Top - tolerance &&
               inner.Right <= outer.Right + tolerance &&
               inner.Bottom <= outer.Bottom + tolerance;
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.Controls.Find(name, searchAllChildren: true).OfType<T>().Single();

    private static void AssertSettingsFormRelay(string root)
    {
        var captureRoot = Path.Combine(root, "relay-capture");
        Directory.CreateDirectory(captureRoot);
        var settings = new AppSettings(
            root,
            string.Empty,
            StartWithWindows: false,
            AppSettings.DefaultCompressionTargetMb,
            "Gallery QA",
            UploadToDiscord: false);
        var captureSettings = CaptureSettings.Default with { LibraryRoot = captureRoot };
        var form = new SettingsForm(
            settings,
            initialPage: SettingsPage.Gallery,
            favorites: new FakeFavoritesService(),
            captureSettings: captureSettings,
            silhouetteSettingsDirectory: root);
        try
        {
            const string projectId =
                "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
            var eventArgs = new GalleryRenditionRetryRequestedEventArgs(
                captureRoot,
                projectId,
                CompositionOrientationIds.Landscape);
            object? observedSender = null;
            GalleryRenditionRetryRequestedEventArgs? observed = null;
            var count = 0;
            form.GalleryRenditionRetryRequested += (sender, relayed) =>
            {
                observedSender = sender;
                observed = relayed;
                count++;
            };
            var relay = typeof(SettingsForm).GetMethod(
                "GalleryRenditionRetryRequestedFromView",
                BindingFlags.Instance | BindingFlags.NonPublic) ??
                throw new InvalidOperationException("Settings Gallery retry relay was not found.");
            relay.Invoke(form, [null, eventArgs]);
            Assert(
                count == 1 &&
                ReferenceEquals(observedSender, form) &&
                ReferenceEquals(observed, eventArgs) &&
                observed.LibraryRoot == captureRoot &&
                observed.ProjectId == projectId &&
                observed.OrientationId == CompositionOrientationIds.Landscape,
                "SettingsForm must relay the exact typed Gallery retry identity without rewriting it.");
            Assert(
                Find<Label>(form, "PageSubtitleLabel").AutoEllipsis,
                "The Settings shell subtitle must ellipsize long Gallery rendition names instead of drawing beneath header actions.");
            form.Dispose();
            relay.Invoke(form, [null, eventArgs]);
            Assert(count == 1,
                "Disposing SettingsForm must sever its outward Gallery retry event lifetime.");
        }
        finally
        {
            form.Dispose();
        }
    }

    private static IEnumerable<Control> Enumerate(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Enumerate(child)) yield return descendant;
        }
    }

    private static bool PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Application.DoEvents();
        return condition();
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException(
            "Gallery rendition UI assertion failed.",
            failure);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeFavoritesService : IFavoritesService
    {
        public event Action? Changed;

        internal int SetCalls { get; private set; }
        internal GalleryClipEntry? LastSetClip { get; private set; }
        internal bool? LastFavorite { get; private set; }

        public bool IsFavorite(GalleryClipEntry clip) => false;
        public int CountFavorites(IEnumerable<GalleryClipEntry> clips) => 0;
        public bool SetFavorite(GalleryClipEntry clip, bool favorite)
        {
            SetCalls++;
            LastSetClip = clip;
            LastFavorite = favorite;
            Changed?.Invoke();
            return true;
        }
        public bool MigrateFavorite(string originalPath, string archivedPath, bool originalKept) => true;
    }

    private sealed class RecordingPlaybackPreparer : IClipPlaybackPreparer
    {
        internal List<string> SourcePaths { get; } = [];

        public Task<ClipPlaybackSource> PrepareAsync(
            string sourcePath,
            CancellationToken cancellationToken,
            IProgress<ClipPlaybackPreparationProgress>? progress = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourcePaths.Add(sourcePath);
            return Task.FromResult(new ClipPlaybackSource(
                sourcePath,
                IsMixedRendition: false,
                AudioTrackCount: 1));
        }
    }
}
