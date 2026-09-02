using ClipsToDiscord;

internal static class RoutesFeatureTests
{
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        var store = new RoutingSnapshotStore(Path.Combine(root, RoutingSnapshotStore.FileName));
        var now = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        var tick = 0;
        var manager = new RoutingRouteManager(
            store,
            () => now.AddMinutes(tick++),
            TestRouteMutationAuthority.Allowed,
            TestRoutingConnectionMembership.AllowAll);
        var fallback = manager.AddAsync(new RoutingRouteDraft(
                "Everything else → Local only",
                RoutingTriggerKind.AnyNewSourceClip,
                Game: null,
                Destination: null,
                ConnectionId: null,
                RoutingOutputKind.Original,
                RoutingDeliveryMode.Automatic,
                RoutingMissingOutputBehavior.UseOriginal,
                FileIntoLibrary: true))
            .GetAwaiter().GetResult();
        Assert(fallback.Kind == RoutingRouteKind.Fallback &&
               fallback.Actions.Single().Kind == RoutingActionKind.FileIntoLibrary &&
               fallback.Actions.Single().LibraryArea == RoutingLibraryArea.LocalOnly,
            "A connectionless fallback must preserve current local-only filing behavior.");

        const string connectionId = "discord.11111111111111111111111111111111";
        var specific = manager.AddAsync(new RoutingRouteDraft(
                "Battlefield highlights",
                RoutingTriggerKind.InstantReplay,
                "Battlefield™-6",
                RoutingDestinationKind.Discord,
                connectionId,
                RoutingOutputKind.Portrait,
                RoutingDeliveryMode.Approval,
                RoutingMissingOutputBehavior.UseOriginal,
                FileIntoLibrary: true))
            .GetAwaiter().GetResult();
        Assert(specific.Kind == RoutingRouteKind.Specific &&
               specific.Prepare.Portrait && !specific.Prepare.Landscape &&
               specific.Conditions.Single().Field == RoutingConditionField.Game &&
               specific.Actions[0] is
               {
                   Kind: RoutingActionKind.Deliver,
                   Destination: RoutingDestinationKind.Discord,
                   ConnectionId: connectionId,
                   OutputRef: RoutingOutputKind.Portrait,
                   Mode: RoutingDeliveryMode.Approval
               } &&
               specific.Actions[1] is
               {
                   Kind: RoutingActionKind.FileIntoLibrary,
                   LibraryArea: RoutingLibraryArea.Uploaded
               },
            "A Discord route must freeze its condition, output, approval, connection, and post-delivery filing contract.");
        AssertThrows<InvalidDataException>(
            () => manager.AddAsync(new RoutingRouteDraft(
                    "Unfiled Discord route",
                    RoutingTriggerKind.ManualRecording,
                    Game: null,
                    RoutingDestinationKind.Discord,
                    connectionId,
                    RoutingOutputKind.Original,
                    RoutingDeliveryMode.Automatic,
                    RoutingMissingOutputBehavior.UseOriginal,
                    FileIntoLibrary: false))
                .GetAwaiter().GetResult(),
            "The Routes slice must not create a delivery that leaves its source outside the always-on Library.");

        manager.SetEnabledAsync(specific.RouteId, false).GetAwaiter().GetResult();
        var disabled = manager.Load().Document!.Routes.Single(route => route.RouteId == specific.RouteId);
        Assert(!disabled.Enabled && disabled.Revision == 2,
            "Toggling a route must advance only that route's revision.");
        manager.MoveAsync(specific.RouteId, -1).GetAwaiter().GetResult();
        var reordered = manager.Load().Document!.Routes.OrderBy(route => route.Priority).ToArray();
        Assert(reordered[0].RouteId == specific.RouteId &&
               reordered.Select(route => route.Priority).SequenceEqual(new[] { 0, 1 }),
            "Route reordering must persist a unique deterministic priority sequence.");

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var observed = false;
            try
            {
                manager.DeleteAsync(specific.RouteId, cancelled.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                observed = true;
            }
            Assert(observed && manager.Load().Document!.Routes.Any(route => route.RouteId == specific.RouteId),
                "A cancelled route command must leave the durable snapshot unchanged.");
        }

        AssertMigrationFallbackIsImmutable(
            Path.Combine(root, "migration-fallback"), fallback, specific, now);
        AssertCutoverGateAndDomainMutationAuthority(
            Path.Combine(root, "cutover-gate"), now);
        AssertConnectionMembershipIsEnforced(
            Path.Combine(root, "connection-membership"), now);
        AssertRetiredOnlyDefaultXboxSourcePreparationAsync(
                Path.Combine(root, "retired-default-source"), now)
            .GetAwaiter().GetResult();
        AssertProductionBlockedClipDisplayAsync(
                Path.Combine(root, "blocked-clip-display"), now)
            .GetAwaiter().GetResult();
        AssertNamedInputSourceRegistrationSafetyAsync(
                Path.Combine(root, "named-input-source-registration"), now)
            .GetAwaiter().GetResult();
        RunOnSta(() =>
        {
            AssertRoutesView(manager, connectionId);
            AssertRoutingLocalOnlyModeView(
                Path.Combine(root, "local-only-view-96"), connectionId, 96, verifyActions: true);
            AssertRoutingLocalOnlyModeView(
                Path.Combine(root, "local-only-view-144"), connectionId, 144, verifyActions: false);
            AssertRoutingLocalOnlyModeView(
                Path.Combine(root, "local-only-view-192"), connectionId, 192, verifyActions: false);
            AssertDisabledLocalOnlyShortcutNotice(
                Path.Combine(root, "local-only-disabled-shortcut"), connectionId);
            AssertSettingsRoutingAuthorityActions(
                Path.Combine(root, "settings-routing-authority"), connectionId);
            AssertConstrainedHeightLayouts(
                Path.Combine(root, "constrained-height"), manager);
            AssertRoutesRuntimeStateMatrix(manager, connectionId);
            AssertRuntimeStateChangesBlockCommandAdmission(manager, connectionId);
            AssertRouteEditorDialogLayout(connectionId, 96);
            AssertRouteEditorDialogLayout(connectionId, 144);
            AssertRouteEditorDialogLayout(connectionId, 192);
            AssertRouteEditorSaveGuardsAreInjectable(connectionId);
            AssertXboxRouteEditorFlowAndLayout(96, verifyDraft: true);
            AssertXboxRouteEditorFlowAndLayout(144, verifyDraft: false);
            AssertXboxRouteEditorFlowAndLayout(192, verifyDraft: false);
            AssertInputSourceManagementAndLayout(manager, connectionId, 96, verifyActions: true);
            AssertInputSourceManagementAndLayout(manager, connectionId, 144, verifyActions: false);
            AssertInputSourceManagementAndLayout(manager, connectionId, 192, verifyActions: false);
            AssertInputSourceConnectionDialog(
                Path.Combine(root, "source-dialog-96"), 96, verifyDraft: true);
            AssertInputSourceConnectionDialog(
                Path.Combine(root, "source-dialog-144"), 144, verifyDraft: false);
            AssertInputSourceConnectionDialog(
                Path.Combine(root, "source-dialog-192"), 192, verifyDraft: false);
            AssertNamedInputSourceConnectionsAndRouteSelection(
                manager, connectionId, 96, verifyActions: true);
            AssertNamedInputSourceConnectionsAndRouteSelection(
                manager, connectionId, 144, verifyActions: false);
            AssertNamedInputSourceConnectionsAndRouteSelection(
                manager, connectionId, 192, verifyActions: false);
            AssertRoutesScaledLayout(root, manager, connectionId, 144);
            AssertRoutesScaledLayout(root, manager, connectionId, 192);
        });
    }

    private static void AssertMigrationFallbackIsImmutable(
        string root,
        RoutingRoute fallback,
        RoutingRoute specific,
        DateTimeOffset now)
    {
        Directory.CreateDirectory(root);
        var store = new RoutingSnapshotStore(Path.Combine(root, RoutingSnapshotStore.FileName));
        var migrationFallback = fallback with
        {
            Source = RoutingRouteSource.Migration,
            Priority = 0,
            Revision = 1,
            CreatedUtc = now,
            ModifiedUtc = now
        };
        var userRoute = specific with
        {
            Enabled = true,
            Priority = 1,
            Revision = 1,
            Source = RoutingRouteSource.User,
            CreatedUtc = now,
            ModifiedUtc = now
        };
        var document = new RoutingSnapshotDocument(
            RoutingSnapshotStore.CurrentSchemaVersion,
            Generation: 1,
            [migrationFallback, userRoute],
            now,
            now);
        _ = store.SaveAsync(document, expectedGeneration: 0).GetAwaiter().GetResult();
        var manager = new RoutingRouteManager(
            store,
            () => now.AddMinutes(1),
            TestRouteMutationAuthority.Allowed,
            TestRoutingConnectionMembership.AllowAll);
        AssertThrows<InvalidOperationException>(
            () => manager.SetEnabledAsync(migrationFallback.RouteId, false)
                .GetAwaiter().GetResult(),
            "The migration fallback must not be disabled behind its committed marker.");
        AssertThrows<InvalidOperationException>(
            () => manager.DeleteAsync(migrationFallback.RouteId).GetAwaiter().GetResult(),
            "The migration fallback must not be deleted behind its committed marker.");
        AssertThrows<InvalidOperationException>(
            () => manager.MoveAsync(userRoute.RouteId, -1).GetAwaiter().GetResult(),
            "A user route must not reorder across and revise the immutable migration fallback.");
        Assert(manager.Load().Document == document,
            "Rejected fallback mutations must leave the route snapshot unchanged.");
    }

    private static void AssertCutoverGateAndDomainMutationAuthority(
        string root,
        DateTimeOffset now)
    {
        Directory.CreateDirectory(root);
        var clips = Path.Combine(root, "clips");
        Directory.CreateDirectory(clips);
        var captureLibrary = Directory.CreateDirectory(
            Path.Combine(root, "capture-library")).FullName;
        var captureLibraryBinding = RoutingCaptureLibraryBindingModel.Create(captureLibrary);
        var settings = new AppSettings(
            clips,
            string.Empty,
            StartWithWindows: false,
            AppSettings.DefaultCompressionTargetMb,
            "Routes tester",
            UploadToDiscord: false,
            CaptureSource: ClipCaptureSource.SteelSeriesGg);
        var state = new WatchState
        {
            Version = 4,
            ClipsFolder = clips,
            CaptureSource = ClipCaptureSource.SteelSeriesGg
        };
        var readiness = LegacyRoutingMigrationPlanner.Evaluate(
            new LegacyRoutingMigrationInput(
                settings,
                state,
                LegacyWorkerQuiesced: true,
                DiscordConnectionIds: [],
                CaptureLibraryBinding: captureLibraryBinding),
            now);
        var plan = readiness.Plan ?? throw new InvalidOperationException(
            "The cutover-gate fixture could not produce a migration plan.");
        var store = new RoutingSnapshotStore(
            Path.Combine(root, RoutingSnapshotStore.FileName));
        var snapshot = new RoutingSnapshotDocument(
            RoutingSnapshotStore.CurrentSchemaVersion,
            Generation: 1,
            Routes: [plan.Route],
            CreatedUtc: now,
            UpdatedUtc: now);
        _ = store.SaveAsync(snapshot, expectedGeneration: 0).GetAwaiter().GetResult();
        var markers = new LegacyRoutingMigrationMarkerStore(
            Path.Combine(root, LegacyRoutingMigrationMarkerStore.FileName));
        var manager = new RoutingRouteManager(store);

        Assert(!manager.CanMutate() &&
               !CommittedRoutingRouteMutationAuthority.IsCutoverCommitted(markers, snapshot),
            "A missing cutover marker must keep domain and UI route mutation locked.");
        var prepared = LegacyRoutingMigrationMarkerModel.CreatePrepared(plan, now);
        _ = markers.SaveAsync(prepared, expectedGeneration: 0).GetAwaiter().GetResult();
        Assert(!manager.CanMutate() &&
               !CommittedRoutingRouteMutationAuthority.IsCutoverCommitted(markers, snapshot),
            "A merely prepared cutover marker must not authorize route mutation.");
        var committed = LegacyRoutingMigrationMarkerModel.Commit(prepared, now.AddSeconds(1));
        _ = markers.SaveAsync(committed, prepared.Generation).GetAwaiter().GetResult();
        Assert(manager.CanMutate() &&
               CommittedRoutingRouteMutationAuthority.IsCutoverCommitted(markers, snapshot),
            "Only an exact committed marker and migration route may authorize route mutation.");
        var mismatchedRoute = plan.Route with
        {
            Name = "Changed migration fallback",
            Revision = RoutingValidation.NextGeneration(plan.Route.Revision),
            ModifiedUtc = now.AddSeconds(2)
        };
        var mismatchedSnapshot = snapshot with { Routes = [mismatchedRoute] };
        Assert(!CommittedRoutingRouteMutationAuthority.IsCutoverCommitted(
                   markers, mismatchedSnapshot),
            "A committed marker with the same route id but different route content must fail closed.");
        File.WriteAllText(markers.Path, "{ corrupt marker bytes");
        Assert(!manager.CanMutate() &&
               !CommittedRoutingRouteMutationAuthority.IsCutoverCommitted(markers, snapshot),
            "A corrupt marker must fail closed rather than unlocking route editing.");

        var blockedRoot = Path.Combine(root, "blocked-domain");
        Directory.CreateDirectory(blockedRoot);
        var blockedStore = new RoutingSnapshotStore(
            Path.Combine(blockedRoot, RoutingSnapshotStore.FileName));
        var setup = new RoutingRouteManager(
            blockedStore,
            () => now,
            TestRouteMutationAuthority.Allowed,
            TestRoutingConnectionMembership.AllowAll);
        var first = setup.AddAsync(LocalDraft("First", RoutingTriggerKind.ManualRecording))
            .GetAwaiter().GetResult();
        var second = setup.AddAsync(LocalDraft("Second", RoutingTriggerKind.InstantReplay))
            .GetAwaiter().GetResult();
        var baseline = blockedStore.Load().Document!;
        var blocked = new RoutingRouteManager(
            blockedStore,
            () => now.AddMinutes(1),
            TestRouteMutationAuthority.Blocked,
            TestRoutingConnectionMembership.AllowAll);
        AssertThrows<InvalidOperationException>(
            () => blocked.AddAsync(LocalDraft("Third", RoutingTriggerKind.ManualRecording))
                .GetAwaiter().GetResult(),
            "The domain manager must block Add before committed cutover.");
        AssertThrows<InvalidOperationException>(
            () => blocked.SetEnabledAsync(first.RouteId, false).GetAwaiter().GetResult(),
            "The domain manager must block enable changes before committed cutover.");
        AssertThrows<InvalidOperationException>(
            () => blocked.DeleteAsync(first.RouteId).GetAwaiter().GetResult(),
            "The domain manager must block deletion before committed cutover.");
        AssertThrows<InvalidOperationException>(
            () => blocked.MoveAsync(second.RouteId, -1).GetAwaiter().GetResult(),
            "The domain manager must block reordering before committed cutover.");
        Assert(blockedStore.Load().Document == baseline,
            "Rejected pre-cutover commands must leave the exact snapshot generation unchanged.");
    }

    private static void AssertConnectionMembershipIsEnforced(
        string root,
        DateTimeOffset now)
    {
        Directory.CreateDirectory(root);
        const string selected = "discord.22222222222222222222222222222222";
        const string missing = "discord.33333333333333333333333333333333";
        var membership = new TestRoutingConnectionMembership([selected]);
        var store = new RoutingSnapshotStore(
            Path.Combine(root, RoutingSnapshotStore.FileName));
        var manager = new RoutingRouteManager(
            store,
            () => now,
            TestRouteMutationAuthority.Allowed,
            membership);
        var saved = manager.AddAsync(DiscordDraft("Selected destination", selected))
            .GetAwaiter().GetResult();
        Assert(saved.Actions[0].ConnectionId == selected,
            "The domain manager must freeze the exact ready connection selected by the user.");
        var baseline = store.Load().Document!;
        AssertThrows<InvalidOperationException>(
            () => manager.AddAsync(DiscordDraft("Missing destination", missing))
                .GetAwaiter().GetResult(),
            "A well-formed but nonexistent or unhealthy connection must be rejected before save.");
        Assert(store.Load().Document == baseline,
            "Rejecting a missing connection must not advance the route snapshot generation.");
    }

    private static RoutingRouteDraft LocalDraft(string name, RoutingTriggerKind trigger) => new(
        name,
        trigger,
        Game: null,
        Destination: null,
        ConnectionId: null,
        RoutingOutputKind.Original,
        RoutingDeliveryMode.Automatic,
        RoutingMissingOutputBehavior.UseOriginal,
        FileIntoLibrary: true);

    private static RoutingRouteDraft DiscordDraft(string name, string connectionId) => new(
        name,
        RoutingTriggerKind.InstantReplay,
        Game: null,
        RoutingDestinationKind.Discord,
        connectionId,
        RoutingOutputKind.Original,
        RoutingDeliveryMode.Automatic,
        RoutingMissingOutputBehavior.UseOriginal,
        FileIntoLibrary: true);

    private static void AssertRoutesView(RoutingRouteManager manager, string connectionId)
    {
        var editorCalls = 0;
        var baseline = manager.Load().Document!;
        using var form = new Form
        {
            ClientSize = new Size(984, 696),
            BackColor = ClipCordTheme.SurfaceBase,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000)
        };
        using var view = new RoutesView(
            manager,
            new FixedConnections(connectionId),
            isCutoverCommitted: () => false,
            editRoute: _ =>
            {
                editorCalls++;
                return LocalDraft("Must not be added", RoutingTriggerKind.ManualRecording);
            });
        form.Controls.Add(view);
        form.Show();
        Application.DoEvents();
        view.ActivateView();
        Application.DoEvents();
        var controls = Enumerate(view).ToArray();
        Assert(controls.Count(control => control.Name.StartsWith("RouteCard_", StringComparison.Ordinal)) == 2 &&
               view.HeaderActionButton.Name == "NewRouteButton" &&
               controls.Single(control => control.Name == "DeliveryHistoryButton").TabStop &&
               controls.Single(control => control.Name == "RoutesScrollHost") is BrandedScrollHost,
            "The Routes page must render durable specific/fallback cards, its primary action, operator history, and branded scrolling.");
        Assert(controls.OfType<FigmaIconControl>().Any(icon => icon.Asset == FigmaIconAsset.Disk),
            "The Routes page must reuse the exact Figma Library icon rather than a font glyph.");
        Assert(!view.HeaderActionButton.Enabled &&
               controls.Single(control => control.Name == "RoutingCutoverStatusLabel") is Label
               {
                   Text: var statusText
               } && statusText.StartsWith("ROUTING NOT ACTIVE", StringComparison.Ordinal) &&
               controls.Where(control =>
                       control.Name.StartsWith("RouteEnabled_", StringComparison.Ordinal) ||
                       control.Name.StartsWith("DeleteRoute_", StringComparison.Ordinal) ||
                       control.Name.StartsWith("MoveRoute", StringComparison.Ordinal))
                   .All(control => !control.Enabled),
            "Before cutover, Routes must say the runtime is inactive and expose no configuration mutation that can block migration.");
        Assert(controls.Single(control => control.Name == "RoutingLibraryNotice")
                   .Controls.Cast<Control>().SelectMany(Enumerate)
                   .OfType<Label>().Any(label =>
                       label.Text.Contains("Library is always on", StringComparison.Ordinal)),
            "The Routes page must state the Figma contract that every source finishes in the Library.");
        Assert(controls.OfType<Label>().Any(label => label.Text == "1 configured"),
            "The configured-route counter must count visible configured routes, including routes that are currently off.");
        view.AddRouteAsync().GetAwaiter().GetResult();
        Assert(editorCalls == 0 && manager.Load().Document == baseline,
            "The Add route handler must re-check cutover before opening its editor or changing durable routes.");

        ((Button)controls.Single(control =>
            control.Name == "ConnectionsRouteTab")).PerformClick();
        Application.DoEvents();
        var connectionControls = Enumerate(view).ToArray();
        Assert(connectionControls.OfType<Label>().Any(label => label.Text == "READY") &&
               connectionControls.OfType<Label>().All(label => label.Text != "CONNECTED"),
            "A stored, decryptable Discord connection must be described as ready rather than implying a live connectivity check.");
        form.Close();

        AssertExplicitDiscordConnectionSelection(connectionId);
    }

    private static void AssertRoutingLocalOnlyModeView(
        string root,
        string connectionId,
        int dpi,
        bool verifyActions)
    {
        Directory.CreateDirectory(root);
        var manager = new RoutingRouteManager(
            new RoutingSnapshotStore(Path.Combine(root, RoutingSnapshotStore.FileName)),
            mutationAuthority: TestRouteMutationAuthority.Allowed,
            connectionMembership: TestRoutingConnectionMembership.AllowAll);
        var deliveryRoute = manager.AddAsync(DiscordDraft(
                $"Local-only UI route {dpi}", connectionId))
            .GetAwaiter().GetResult();
        var localOnly = new MutableLocalOnlyModeViewSource(new RoutingLocalOnlyModeViewSnapshot(
            IsAvailable: true,
            EffectiveEnabled: !verifyActions,
            HotkeyDisplayText: "Ctrl + Shift + U",
            FirstRunNoticeDismissed: false,
            StatusDetail: "Local-only mode is ready."));
        var scale = dpi / 96d;
        var requestedClientSize = new Size(
            (int)Math.Round(984 * scale),
            (int)Math.Round(696 * scale));
        using var form = new Form
        {
            ClientSize = requestedClientSize,
            BackColor = ClipCordTheme.SurfaceBase,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ShowInTaskbar = false,
            Opacity = 0
        };
        using var view = new RoutesView(
            manager,
            new FixedConnections(connectionId),
            isCutoverCommitted: () => true,
            layoutDpi: dpi,
            localOnlyMode: localOnly);
        var changedEvents = 0;
        view.LocalOnlyModeChanged += (_, _) => changedEvents++;
        form.Controls.Add(view);
        form.Show();
        // Windows clamps an opening form to the runner's working area. Restore the
        // synthetic viewport after the handle exists so 144/192-DPI probes test
        // the requested layout rather than the CI desktop's unrelated resolution.
        form.ClientSize = requestedClientSize;
        form.PerformLayout();
        Application.DoEvents();
        Assert(form.ClientSize == requestedClientSize,
            $"The Local-only DPI fixture must preserve its requested {dpi}-DPI viewport; " +
            $"requested={requestedClientSize}, actual={form.ClientSize}.");
        view.ActivateView();
        Application.DoEvents();

        AssertLocalOnlyModeLayout(view, dpi, expectEnabled: !verifyActions);
        var initial = Enumerate(view).ToArray();
        var initialShortcutField = initial.OfType<TextBox>().Single(control =>
            control.Name == "LocalOnlyModeShortcutField");
        var initialKeycaps = initial.OfType<FlowLayoutPanel>().Single(control =>
            control.Name == "LocalOnlyModeShortcutKeycaps");
        Assert(initialShortcutField is
               {
                   Text: "Ctrl + Shift + U",
                   ReadOnly: true,
                   TabStop: false
               } &&
               !initialShortcutField.Visible && initialKeycaps.Visible &&
               initialKeycaps.Controls.OfType<RoundedPanel>()
                   .Select(keycap => keycap.AccessibleName)
                   .SequenceEqual(["Ctrl", "Shift", "U"]) &&
               initial.Single(control => control.Name == "LocalOnlyShortcutMigrationNotice").Visible &&
               initial.Single(control => control.Name == "DismissLocalOnlyShortcutMigrationButton") is Button
               {
                   Enabled: true,
                   TabStop: true
               },
            $"The Local-only card must preserve the migrated shortcut and expose the first-run explanation at {dpi} DPI.");

        if (verifyActions)
        {
            Assert(initial.Single(control => control.Name == "RoutingLocalOnlyModeTitleLabel") is Label
                   {
                       Text: "Local-only mode"
                   } &&
                   initial.Single(control => control.Name == "RoutingLocalOnlyModeDetailLabel") is Label
                   {
                       Text: "Off · future clips follow your active Routes"
                   } &&
                   initial.Single(control => control.Name == "LocalOnlyModeToggle") is ToggleSwitch
                   {
                       Checked: false,
                       Enabled: true,
                       TabStop: true
                   },
                "The available Local-only control must render a truthful, keyboard-reachable OFF state.");

            ((Button)initial.Single(control =>
                control.Name == "DismissLocalOnlyShortcutMigrationButton")).PerformClick();
            PumpUntil(
                () => localOnly.DismissCalls == 1 &&
                      !Enumerate(view).Any(control =>
                          control.Name == "LocalOnlyShortcutMigrationNotice"),
                "the Local-only first-run explanation to dismiss");
            Assert(localOnly.Snapshot.FirstRunNoticeDismissed,
                "Dismissing the first-run explanation must persist through the Local-only view source.");

            var offToggle = (ToggleSwitch)Enumerate(view).Single(control =>
                control.Name == "LocalOnlyModeToggle");
            offToggle.Checked = true;
            PumpUntil(
                () => localOnly.SetEnabledCalls == 1 &&
                      localOnly.Snapshot.EffectiveEnabled &&
                      Enumerate(view).OfType<Label>().Any(label =>
                          label.Name == "RoutingLocalOnlyModeTitleLabel" &&
                          label.Text == "Local-only mode on"),
                "the Local-only mode toggle to persist ON");
            var enabledControls = Enumerate(view).ToArray();
            Assert(changedEvents == 1 &&
                   enabledControls.Single(control => control.Name == "LocalOnlyModeToggle") is ToggleSwitch
                   {
                       Checked: true,
                       Enabled: true,
                       TabStop: true
                   } &&
                   enabledControls.OfType<Label>().Any(label =>
                       label.Name == "RoutingCutoverStatusLabel" &&
                       label.Text.Contains("EXTERNAL DELIVERIES PAUSED", StringComparison.Ordinal)) &&
                   enabledControls.Single(control => control.Name == "RoutingLibraryNotice")
                       .Controls.Cast<Control>().SelectMany(Enumerate).OfType<Label>().Any(label =>
                           label.Text.Contains("External delivery actions are paused", StringComparison.Ordinal)),
                "Turning Local-only mode on must refresh the card, status, Library promise, and shell event exactly once.");
            var enabledButton = enabledControls.OfType<Button>().Single(control =>
                control.Name == $"RouteEnabled_{deliveryRoute.RouteId:N}");
            var routeCard = enabledControls.Single(control =>
                control.Name == $"RouteCard_{deliveryRoute.RouteId:N}");
            var deliveryAction = deliveryRoute.Actions.Single(action =>
                action.Kind == RoutingActionKind.Deliver);
            var libraryAction = deliveryRoute.Actions.Single(action =>
                action.Kind == RoutingActionKind.FileIntoLibrary);
            Assert(enabledButton.Text == "On" &&
                   Enumerate(routeCard).Any(control =>
                       control.Name == $"RouteActionPausedBadge_{deliveryAction.ActionId:N}" &&
                       control.AccessibleName == "Paused by Local-only mode") &&
                   Enumerate(routeCard).Any(control =>
                       control.Name == $"RouteActionActiveBadge_{libraryAction.ActionId:N}" &&
                       control.AccessibleName == "Auto") &&
                   Enumerate(routeCard).OfType<Label>().Any(label =>
                       label.Name == $"RouteActionLabel_{libraryAction.ActionId:N}" &&
                       label.Text == "File into Library · Uploaded"),
                "Local-only mode must pause each external Deliver action while File into Library remains visibly active.");
            Assert(Enumerate(view.HeaderActions).Any(control =>
                       control.Name == "HeaderLocalOnlyModePill" && control.Visible),
                "R25 must surface the Local-only mode pill beside New route while the override is on.");

            var changeShortcut = (Button)enabledControls.Single(control =>
                control.Name == "ChangeLocalOnlyModeShortcutButton");
            changeShortcut.PerformClick();
            PumpUntil(
                () => Enumerate(view).Single(control =>
                    control.Name == "LocalOnlyModeShortcutField") is TextBox
                    {
                        Text: "Press a shortcut…",
                        TabStop: true,
                        Visible: true
                    },
                "the Local-only shortcut recorder to become keyboard reachable");
            localOnly.RejectNextHotkeyAsConflict = true;
            SendKeyDown(
                Enumerate(view).Single(control => control.Name == "LocalOnlyModeShortcutField"),
                Keys.Control | Keys.Alt | Keys.L);
            PumpUntil(
                () => localOnly.SetHotkeyCalls == 1 &&
                      Enumerate(view).OfType<Label>().Any(label =>
                          label.Name == "RoutingLocalOnlyModeDetailLabel" &&
                          label.Text.Contains("already registered", StringComparison.OrdinalIgnoreCase)),
                "the conflicting Local-only shortcut result to render");
            Assert(localOnly.Snapshot.HotkeyDisplayText == "Ctrl + Shift + U" &&
                   Enumerate(view).Single(control =>
                       control.Name == "LocalOnlyModeShortcutField").TabStop,
                "A shortcut conflict must preserve the previously registered shortcut and keep the recorder reachable for retry.");

            SendKeyDown(
                Enumerate(view).Single(control => control.Name == "LocalOnlyModeShortcutField"),
                Keys.Control | Keys.Alt | Keys.K);
            PumpUntil(
                () => localOnly.SetHotkeyCalls == 2 &&
                      localOnly.Snapshot.HotkeyDisplayText == "Ctrl + Alt + K" &&
                      Enumerate(view).Single(control =>
                          control.Name == "LocalOnlyModeShortcutField") is TextBox
                          {
                              Text: "Ctrl + Alt + K",
                              TabStop: false,
                              Visible: false
                          },
                "the retried Local-only shortcut to persist");
            Assert(changedEvents == 2,
                "Only successful Local-only enable and shortcut changes may raise shell refresh events.");
        }

        form.Close();
    }

    private static void AssertDisabledLocalOnlyShortcutNotice(
        string root,
        string connectionId)
    {
        Directory.CreateDirectory(root);
        var manager = new RoutingRouteManager(
            new RoutingSnapshotStore(Path.Combine(root, RoutingSnapshotStore.FileName)),
            mutationAuthority: TestRouteMutationAuthority.Allowed,
            connectionMembership: TestRoutingConnectionMembership.AllowAll);
        _ = manager.AddAsync(DiscordDraft("Disabled shortcut route", connectionId))
            .GetAwaiter().GetResult();
        var localOnly = new MutableLocalOnlyModeViewSource(new RoutingLocalOnlyModeViewSnapshot(
            IsAvailable: true,
            EffectiveEnabled: false,
            HotkeyDisplayText: string.Empty,
            FirstRunNoticeDismissed: false,
            StatusDetail: "Local-only mode is ready."));
        using var form = new Form
        {
            ClientSize = new Size(984, 696),
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ShowInTaskbar = false,
            Opacity = 0
        };
        using var view = new RoutesView(
            manager,
            new FixedConnections(connectionId),
            isCutoverCommitted: () => true,
            localOnlyMode: localOnly);
        form.Controls.Add(view);
        form.Show();
        view.ActivateView();
        Application.DoEvents();

        var controls = Enumerate(view).ToArray();
        Assert(controls.OfType<Label>().Single(label =>
                   label.Name == "LocalOnlyShortcutMigrationTitleLabel").Text ==
               "Your Local-only shortcut is currently disabled. Choose Change shortcut to add one." &&
               controls.OfType<Button>().Single(button =>
                   button.Name == "ChangeLocalOnlyModeShortcutButton").Text == "Change shortcut" &&
               !controls.OfType<Label>().Any(label =>
                   label.Text.Contains("Your existing  shortcut", StringComparison.Ordinal)),
            "A disabled migrated shortcut must use explicit nonblank copy and the approved Change shortcut action.");
        form.Close();
    }

    private static void AssertSettingsRoutingAuthorityActions(
        string root,
        string connectionId)
    {
        Directory.CreateDirectory(root);
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var settings = new AppSettings(
            clips,
            "https://discord.com/api/webhooks/123456/settings-routing-authority-token",
            StartWithWindows: false,
            AppSettings.DefaultCompressionTargetMb,
            "Routing authority tester",
            UploadToDiscord: true,
            ModeToggleHotkey: "Ctrl + Shift + U");
        var manager = new RoutingRouteManager(
            new RoutingSnapshotStore(Path.Combine(root, RoutingSnapshotStore.FileName)),
            mutationAuthority: TestRouteMutationAuthority.Allowed,
            connectionMembership: TestRoutingConnectionMembership.AllowAll);
        _ = manager.AddAsync(DiscordDraft("Settings authority route", connectionId))
            .GetAwaiter().GetResult();
        var runtimeState = RoutesRuntimeViewState.LegacyActive;
        var localOnly = new MutableLocalOnlyModeViewSource(new RoutingLocalOnlyModeViewSnapshot(
            IsAvailable: true,
            EffectiveEnabled: false,
            HotkeyDisplayText: "Ctrl + Alt + L",
            FirstRunNoticeDismissed: true,
            StatusDetail: "Local-only mode is ready."));
        using var form = new SettingsForm(
            settings,
            initialPage: SettingsPage.Home,
            routingRouteManager: manager,
            routesRuntimeStateProvider: () => runtimeState,
            localOnlyMode: localOnly);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.ShowInTaskbar = false;
        form.Opacity = 0;
        form.Show();
        Application.DoEvents();

        Assert(form.GetAuthoritativeModeHotkey() == "Ctrl + Shift + U",
            "Capture must reserve the legacy Settings shortcut while legacy processing still owns admission.");
        runtimeState = RoutesRuntimeViewState.Active;
        form.RefreshRoutingPresentation();
        Application.DoEvents();
        Assert(form.GetAuthoritativeModeHotkey() == "Ctrl + Alt + L",
            "Capture must reserve the Routing-owned Local-only shortcut after cutover, not the retired legacy value.");

        form.HandleHomeRoutingActionAsync(HomeRoutingAction.OpenRoutes)
            .GetAwaiter().GetResult();
        Assert(form.Text == "ClipCord — Routes",
            "The Home Open Routes action must enter the Routes page.");

        localOnly.BlockNextEnabledMutation();
        var first = form.HandleHomeRoutingActionAsync(HomeRoutingAction.EnableLocalOnlyMode);
        Application.DoEvents();
        var ignored = form.HandleHomeRoutingActionAsync(HomeRoutingAction.DisableLocalOnlyMode);
        Assert(localOnly.SetEnabledCalls == 1 && ignored.IsCompleted,
            "Home must ignore a reentrant Local-only action while the first durable mutation is pending.");
        localOnly.ReleaseEnabledMutation();
        PumpUntil(() => first.IsCompleted, "the Home Local-only action to complete");
        first.GetAwaiter().GetResult();
        Assert(localOnly.Snapshot.EffectiveEnabled && localOnly.SetEnabledCalls == 1,
            "The first Home Local-only action must persist and refresh exactly once.");

        var disable = form.HandleHomeRoutingActionAsync(HomeRoutingAction.DisableLocalOnlyMode);
        PumpUntil(() => disable.IsCompleted, "the Home Local-only disable action to complete");
        disable.GetAwaiter().GetResult();
        localOnly.RejectNextEnabledMutation = true;
        var rejected = form.HandleHomeRoutingActionAsync(HomeRoutingAction.EnableLocalOnlyMode);
        PumpUntil(() => rejected.IsCompleted, "the rejected Home Local-only action to settle");
        rejected.GetAwaiter().GetResult();
        Assert(!localOnly.Snapshot.EffectiveEnabled && localOnly.SetEnabledCalls == 3 &&
               form.Text == "ClipCord — Routes",
            "A failed Home mutation must preserve the prior mode and lead the user to the authoritative Routes surface.");
        form.Close();
    }

    private static void AssertConstrainedHeightLayouts(
        string root,
        RoutingRouteManager manager)
    {
        Directory.CreateDirectory(root);
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var settings = new AppSettings(
            clips,
            "https://discord.com/api/webhooks/123456/constrained-height-token",
            StartWithWindows: false,
            AppSettings.DefaultCompressionTargetMb,
            "Constrained height tester",
            UploadToDiscord: true,
            ModeToggleHotkey: "Ctrl + Shift + U");
        using var form = new SettingsForm(
            settings,
            initialPage: SettingsPage.Settings,
            routingRouteManager: manager,
            routesRuntimeStateProvider: () => RoutesRuntimeViewState.Active);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.ShowInTaskbar = false;
        form.Opacity = 0;
        form.Show();
        Application.DoEvents();

        // Figma R30/R31 use a 1200x560 logical-pixel constrained window. Scale that
        // contract into physical pixels so this hidden probe exercises the same
        // viewport at every runner DPI rather than shrinking below the supported
        // logical minimum on a high-DPI desktop.
        var dpi = Math.Max(96, form.DeviceDpi);
        var constrainedClientSize = new Size(
            SettingsForm.GetDesignedOpeningSize(SettingsPage.Settings, dpi).Width,
            (int)Math.Round(560 * dpi / 96d));
        form.MinimumSize = Size.Empty;
        form.ClientSize = constrainedClientSize;
        form.PerformLayout();
        Application.DoEvents();

        var settingsHost = Enumerate(form).OfType<BrandedScrollHost>().Single(control =>
            control.Name == "SettingsScrollHost");
        settingsHost.RefreshContentLayout(preservePosition: false);
        var settingsContent = settingsHost.Content ??
            throw new InvalidOperationException("Settings constrained-height content is missing.");
        Assert(form.ClientSize == constrainedClientSize &&
               settingsHost.HasOverflow &&
               settingsContent.Height > settingsHost.ClientSize.Height,
            $"Settings must use its branded overflow viewport at the approved 1200x560 logical constrained height; " +
            $"form={form.ClientSize}, viewport={settingsHost.ClientSize}, content={settingsContent.Size}.");
        Assert(!Enumerate(settingsContent).OfType<ScrollableControl>().Any(control =>
                   control.AutoScroll || control.HorizontalScroll.Visible ||
                   control.VerticalScroll.Visible),
            "Settings constrained-height content must not introduce a native Windows scrollbar.");
        AssertDescendantsContained(settingsContent, dpi);
        AssertSiblingGeometry(settingsContent, dpi);
        AssertReachableThroughBrandedScrollHost(
            settingsHost,
            Enumerate(settingsContent).Single(control => control.Name == "ManagedLocalOnlyModeRow"),
            "Settings");

        form.ShowPage(SettingsPage.Routes);
        Application.DoEvents();
        var routes = Enumerate(form).OfType<RoutesView>().Single(control => control.Visible);
        routes.RefreshViewport();
        Application.DoEvents();
        var routesHost = Enumerate(routes).OfType<BrandedScrollHost>().Single(control =>
            control.Name == "RoutesScrollHost");
        routesHost.RefreshContentLayout(preservePosition: false);
        var routesContent = routesHost.Content ??
            throw new InvalidOperationException("Routes constrained-height content is missing.");
        Assert(form.ClientSize == constrainedClientSize &&
               routesHost.HasOverflow &&
               routesContent.Height > routesHost.ClientSize.Height,
            $"Routes must use its branded overflow viewport at the approved 1200x560 logical constrained height; " +
            $"form={form.ClientSize}, viewport={routesHost.ClientSize}, content={routesContent.Size}.");
        Assert(!Enumerate(routesContent).OfType<ScrollableControl>().Any(control =>
                   control.AutoScroll || control.HorizontalScroll.Visible ||
                   control.VerticalScroll.Visible),
            "Routes constrained-height content must not introduce a native Windows scrollbar.");
        AssertDescendantsContained(routes, dpi);
        AssertSiblingGeometry(routes, dpi);
        AssertReachableThroughBrandedScrollHost(
            routesHost,
            Enumerate(routesContent).Last(control =>
                control.Name.StartsWith("RouteCard_", StringComparison.Ordinal)),
            "Routes");
        form.Close();

        static void AssertReachableThroughBrandedScrollHost(
            BrandedScrollHost host,
            Control target,
            string page)
        {
            host.EnsureControlVisible(target);
            Application.DoEvents();
            var bounds = host.RectangleToClient(
                target.RectangleToScreen(target.ClientRectangle));
            Assert(host.ScrollOffset > 0 &&
                   bounds.Top >= -1 && bounds.Bottom <= host.ClientSize.Height + 1,
                $"{page}'s final constrained-height control must remain reachable through branded scrolling; " +
                $"target={bounds}, viewport={host.ClientSize}, offset={host.ScrollOffset}.");
            host.RefreshContentLayout(preservePosition: false);
        }
    }

    private static void AssertLocalOnlyModeLayout(
        RoutesView view,
        int dpi,
        bool expectEnabled)
    {
        var controls = Enumerate(view).ToArray();
        var card = controls.Single(control => control.Name == "RoutingLocalOnlyModeControl");
        var notice = controls.Single(control => control.Name == "LocalOnlyShortcutMigrationNotice");
        var shortcut = controls.OfType<TextBox>().Single(control =>
            control.Name == "LocalOnlyModeShortcutField");
        var keycaps = controls.OfType<FlowLayoutPanel>().Single(control =>
            control.Name == "LocalOnlyModeShortcutKeycaps");
        var shieldTile = controls.OfType<RoundedPanel>().Single(control =>
            control.Name == "RoutingLocalOnlyModeShieldTile");
        var change = controls.OfType<Button>().Single(control =>
            control.Name == "ChangeLocalOnlyModeShortcutButton");
        var dismiss = controls.OfType<Button>().Single(control =>
            control.Name == "DismissLocalOnlyShortcutMigrationButton");
        var toggle = controls.OfType<ToggleSwitch>().Single(control =>
            control.Name == "LocalOnlyModeToggle");
        var scrollHost = controls.OfType<BrandedScrollHost>().Single(control =>
            control.Name == "RoutesScrollHost");
        var headerPill = Enumerate(view.HeaderActions).Single(control =>
            control.Name == "HeaderLocalOnlyModePill");
        scrollHost.RefreshContentLayout(preservePosition: false);

        Assert(card.Height == RoutesView.ScaleLogicalMetric(66, dpi) &&
               notice.Height == RoutesView.ScaleLogicalMetric(58, dpi) &&
               shieldTile.Size == new Size(
                   RoutesView.ScaleLogicalMetric(28, dpi),
                   RoutesView.ScaleLogicalMetric(28, dpi)) &&
               toggle.Size == new Size(
                   RoutesView.ScaleLogicalMetric(42, dpi),
                   RoutesView.ScaleLogicalMetric(23, dpi)) &&
               change.Size == new Size(
                   RoutesView.ScaleLogicalMetric(127, dpi),
                   RoutesView.ScaleLogicalMetric(29, dpi)) &&
               dismiss.Size == new Size(
                   RoutesView.ScaleLogicalMetric(70, dpi),
                   RoutesView.ScaleLogicalMetric(30, dpi)),
            $"The Local-only card, notice, toggle, and actions must scale from their approved logical metrics at {dpi} DPI; " +
            $"card={card.Size}, notice={notice.Size}, shield={shieldTile.Size}, toggle={toggle.Size}, change={change.Size}, dismiss={dismiss.Size}.");
        Assert(toggle.Checked == expectEnabled && toggle.Enabled && toggle.TabStop &&
               change.Enabled && change.TabStop && dismiss.Enabled && dismiss.TabStop &&
               !shortcut.TabStop && !shortcut.Visible && keycaps.Visible && scrollHost.TabStop &&
               headerPill.Visible == expectEnabled,
            $"The Local-only actions and branded viewport must remain in a deliberate keyboard order at {dpi} DPI.");
        Assert(!controls.OfType<ScrollableControl>().Any(control =>
                   control.AutoScroll || control.HorizontalScroll.Visible ||
                   control.VerticalScroll.Visible),
            $"Routes must not introduce a native Windows scrollbar for Local-only mode at {dpi} DPI.");
        AssertDescendantsContained(card, dpi);
        AssertSiblingGeometry(card, dpi);
        AssertDescendantsContained(notice, dpi);
        AssertSiblingGeometry(notice, dpi);

        foreach (var label in controls.OfType<Label>().Where(label => label.Name is
                     "RoutingLocalOnlyModeTitleLabel" or
                     "RoutingLocalOnlyModeDetailLabel"))
        {
            var measured = TextRenderer.MeasureText(
                label.Text,
                label.Font,
                Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            var predicted = PredictAtDpi(measured, label.DeviceDpi, dpi);
            Assert(label.ClientSize.Width + RoutesView.ScaleLogicalMetric(3, dpi) >= predicted.Width &&
                   label.ClientSize.Height + RoutesView.ScaleLogicalMetric(3, dpi) >= predicted.Height,
                $"Local-only text '{label.Text}' must fit without ellipsis at {dpi} DPI: predicted={predicted}, actual={label.ClientSize}.");
        }

        foreach (var target in new Control[] { toggle, change, dismiss })
        {
            scrollHost.EnsureControlVisible(target);
            Application.DoEvents();
            var bounds = scrollHost.RectangleToClient(
                target.RectangleToScreen(target.ClientRectangle));
            Assert(bounds.Top >= -1 && bounds.Bottom <= scrollHost.ClientSize.Height + 1,
                $"The Local-only action '{target.Name}' must be scroll- and tab-reachable at {dpi} DPI: target={bounds}, viewport={scrollHost.ClientSize}.");
        }
        scrollHost.RefreshContentLayout(preservePosition: false);
    }

    private static void SendKeyDown(Control control, Keys keyData)
    {
        typeof(Control).GetMethod(
                "OnKeyDown",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!
            .Invoke(control, [new KeyEventArgs(keyData)]);
        Application.DoEvents();
    }

    private static async Task AssertRetiredOnlyDefaultXboxSourcePreparationAsync(
        string root,
        DateTimeOffset now)
    {
        const string originalIdentity =
            "1111111111111111111111111111111111111111111111111111111111111111";
        const string replacementIdentity =
            "2222222222222222222222222222222222222222222222222222222222222222";
        const string rediscoveredIdentity =
            "3333333333333333333333333333333333333333333333333333333333333333";
        var originalRoot = Path.GetFullPath(Path.Combine(root, "original", "Xbox Game DVR"));
        var replacementRoot = Path.GetFullPath(
            Path.Combine(root, "replacement", "Xbox Game DVR"));
        Directory.CreateDirectory(originalRoot);
        Directory.CreateDirectory(replacementRoot);
        var ids = new Queue<Guid>(
        [
            new Guid("11111111-1111-1111-1111-111111111111"),
            new Guid("22222222-2222-2222-2222-222222222222"),
            new Guid("33333333-3333-3333-3333-333333333333")
        ]);
        var catalog = new RoutingInputSourceCatalog(
            new RoutingInputSourceCatalogStore(Path.Combine(
                root, "routing", RoutingInputSourceCatalogStore.FileName)),
            new UnreferencedInputSourceProbe(),
            () => ids.Dequeue(),
            () => now);
        var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [originalRoot] = originalIdentity,
            [replacementRoot] = replacementIdentity
        };
        var inspector = new MutableInputSourceIdentityInspector(identities);
        var recovery = new XboxDvrInputSourceRecoveryService(
            catalog,
            Path.Combine(root, "routing"),
            inspector,
            () => now);
        var original = await recovery.RegisterAsync(
            "Xbox captures · OneDrive",
            originalRoot,
            TimeZoneInfo.Utc.Id,
            enabled: false);
        var replacement = await recovery.RegisterReplacementAsync(
            original.Source!.SourceId,
            original.Source.Revision,
            replacementRoot,
            enabled: false);
        var removed = await catalog.RemoveAsync(
            replacement.Source!.SourceId,
            replacement.Source.Revision,
            now);
        Assert(removed.Succeeded &&
               catalog.Inspect().Sources is [{ Retired: true }],
            "The preparation control must begin with only a durable retired Xbox tombstone.");

        var viewSource = new RoutingInputSourceCatalogViewSource(
            catalog,
            new UnusedXboxMetadataFileSystem(),
            recovery,
            () => originalRoot);
        var failedClosed = false;
        try
        {
            _ = await viewSource.PrepareDefaultXboxSourceAsync();
        }
        catch (InvalidOperationException exception)
        {
            failedClosed = exception.Message.Contains(
                "replaced source identity", StringComparison.OrdinalIgnoreCase) &&
                !exception.Message.Contains(originalRoot, StringComparison.OrdinalIgnoreCase);
        }
        Assert(failedClosed && inspector.Inspections >= 3 &&
               catalog.Inspect().Sources is [{ Retired: true }],
            "A same-authority retired tombstone must not masquerade as the active default; verified preparation must fail closed without leaking its path or mutating the catalog.");

        identities[originalRoot] = rediscoveredIdentity;
        var prepared = await viewSource.PrepareDefaultXboxSourceAsync();
        var afterRediscovery = catalog.Inspect().Sources;
        Assert(prepared is { Retired: false, Enabled: false } &&
               afterRediscovery.Count == 2 &&
               afterRediscovery.Single(source => source.Retired).SourceId ==
                   original.Source.SourceId &&
               afterRediscovery.Single(source => !source.Retired).SourceId ==
                   prepared.SourceId,
            "When only a retired tombstone remains, a verified different native authority at the current default folder must register as a fresh disabled source instead of returning the tombstone.");
    }

    private static async Task AssertProductionBlockedClipDisplayAsync(
        string root,
        DateTimeOffset now)
    {
        const string identity =
            "4444444444444444444444444444444444444444444444444444444444444444";
        const string firstOccurrence =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string firstRevision =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string secondOccurrence =
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        const string secondRevision =
            "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        const string thirdOccurrence =
            "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        const string thirdRevision =
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
        var routingRoot = Path.Combine(root, "routing");
        var sourceRoot = Path.GetFullPath(Path.Combine(root, "Xbox Game DVR"));
        Directory.CreateDirectory(sourceRoot);
        var catalog = new RoutingInputSourceCatalog(
            new RoutingInputSourceCatalogStore(Path.Combine(
                routingRoot, RoutingInputSourceCatalogStore.FileName)),
            new UnreferencedInputSourceProbe(),
            () => new Guid("44444444-4444-4444-4444-444444444444"),
            () => now);
        var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [sourceRoot] = identity
        };
        var recovery = new XboxDvrInputSourceRecoveryService(
            catalog,
            routingRoot,
            new MutableInputSourceIdentityInspector(identities),
            () => now);
        var registered = await recovery.RegisterAsync(
            "Xbox captures · OneDrive",
            sourceRoot,
            TimeZoneInfo.Utc.Id,
            enabled: true);
        var source = registered.Source!;
        var journal = new XboxDvrOccurrenceJournal(
            new XboxDvrOccurrenceStore(routingRoot, source.SourceId),
            source.SourceId,
            identity,
            () => now);
        _ = await journal.SelectAsync(
            firstOccurrence,
            firstRevision,
            "Battlefield 6-2026_09_01-17-00-00.mp4",
            "Battlefield 6",
            now.AddHours(-1),
            4L * 1024 * 1024,
            now.AddHours(-1).UtcTicks);
        _ = await journal.MarkNeedsAttentionAsync(
            firstOccurrence,
            firstRevision,
            "invalid-media");
        _ = await journal.SelectAsync(
            secondOccurrence,
            secondRevision,
            "Another Game-2026_09_01-17-30-00.mp4",
            "Another Game",
            now.AddMinutes(-30),
            5L * 1024 * 1024,
            now.AddMinutes(-30).UtcTicks);
        _ = await journal.MarkNeedsAttentionAsync(
            secondOccurrence,
            secondRevision,
            "library-conflict");
        var attention = await catalog.SetNeedsAttentionAsync(
            source.SourceId,
            source.Revision,
            RoutingInputSourceAttentionReason.BlockedOccurrence,
            now);
        var viewSource = new RoutingInputSourceCatalogViewSource(
            catalog,
            new UnusedXboxMetadataFileSystem(),
            recovery,
            () => sourceRoot);
        var blocked = viewSource.InspectInputSources().Sources.Single();
        Assert(attention.Succeeded &&
               blocked.SkippableBlockedClipCount == 2 &&
               !blocked.BlockedClipRecoveryPending &&
               !blocked.BlockedClipRetryPending,
            "The production source view must expose an exact count only from an authority-matching durable journal containing known skippable blockers.");

        _ = await journal.SkipBlockedAsync(firstOccurrence, firstRevision);
        _ = await journal.SkipBlockedAsync(secondOccurrence, secondRevision);
        var pending = viewSource.InspectInputSources().Sources.Single();
        Assert(pending.SkippableBlockedClipCount == 0 &&
               pending.BlockedClipRecoveryPending &&
               !pending.BlockedClipRetryPending,
            "A prior durable skip must become an explicit source-resume state, never another destructive Skip prompt.");

        _ = await journal.SelectAsync(
            thirdOccurrence,
            thirdRevision,
            "Retry Game-2026_09_01-17-45-00.mp4",
            "Retry Game",
            now.AddMinutes(-15),
            6L * 1024 * 1024,
            now.AddMinutes(-15).UtcTicks);
        _ = await journal.MarkNeedsAttentionAsync(
            thirdOccurrence,
            thirdRevision,
            "invalid-media");
        _ = await journal.RetryBlockedAsync(thirdOccurrence, thirdRevision);
        var retryPending = viewSource.InspectInputSources().Sources.Single();
        Assert(retryPending.SkippableBlockedClipCount == 0 &&
               !retryPending.BlockedClipRecoveryPending &&
               retryPending.BlockedClipRetryPending,
            "A durable blocked-clip retry awaiting catalog convergence must expose Resume retry rather than becoming unreachable behind Recheck.");

        var generic = await catalog.SetNeedsAttentionAsync(
            source.SourceId,
            pending.Revision,
            RoutingInputSourceAttentionReason.MetadataInvalid,
            now.AddMinutes(1));
        var genericDisplay = viewSource.InspectInputSources().Sources.Single();
        Assert(generic.Succeeded &&
               genericDisplay.SkippableBlockedClipCount == 0 &&
               !genericDisplay.BlockedClipRecoveryPending &&
               !genericDisplay.BlockedClipRetryPending,
            "Generic MetadataInvalid evidence must fail closed to no Skip/Resume action even when old skipped journal entries exist.");
    }

    private static async Task AssertNamedInputSourceRegistrationSafetyAsync(
        string root,
        DateTimeOffset now)
    {
        var routingRoot = Path.Combine(root, "routing");
        var baselineRoot = Path.Combine(routingRoot, "named-baselines");
        var legacyRoot = Path.GetFullPath(Path.Combine(root, "legacy-clips"));
        var captureLibraryRoot = Path.GetFullPath(Path.Combine(root, "capture-library"));
        var steelSeriesRoot = Path.GetFullPath(Path.Combine(root, "steelseries"));
        var nestedRecorderRoot = Path.Combine(steelSeriesRoot, "nested-recorder");
        var replacementRoot = Path.GetFullPath(Path.Combine(root, "steelseries-moved"));
        Directory.CreateDirectory(legacyRoot);
        Directory.CreateDirectory(Path.Combine(legacyRoot, "nested"));
        Directory.CreateDirectory(captureLibraryRoot);
        Directory.CreateDirectory(Path.Combine(captureLibraryRoot, "nested"));
        Directory.CreateDirectory(steelSeriesRoot);
        Directory.CreateDirectory(nestedRecorderRoot);
        Directory.CreateDirectory(replacementRoot);
        var catalog = new RoutingInputSourceCatalog(
            new RoutingInputSourceCatalogStore(Path.Combine(
                routingRoot, RoutingInputSourceCatalogStore.FileName)),
            new UnreferencedInputSourceProbe(),
            () => Guid.NewGuid(),
            () => now);
        var baselines = new RoutingNamedWatchedBaselineStore(baselineRoot);
        var viewSource = new RoutingInputSourceCatalogViewSource(
            catalog,
            new UnusedXboxMetadataFileSystem(),
            libraryRoot: captureLibraryRoot,
            legacyWatchedRoot: legacyRoot,
            namedWatchedBaselines: baselines);

        var legacyOverlap = await viewSource.RegisterAsync(
            "Overlapping legacy source",
            RoutingInputSourceKind.SteelSeriesGg,
            Path.Combine(legacyRoot, "nested"));
        var libraryOverlap = await viewSource.RegisterAsync(
            "Overlapping Capture source",
            RoutingInputSourceKind.Nvidia,
            Path.Combine(captureLibraryRoot, "nested"));
        Assert(!legacyOverlap.Succeeded && !libraryOverlap.Succeeded &&
               catalog.Inspect().Sources.Count == 0 &&
               legacyOverlap.Reason.Contains("migrated", StringComparison.OrdinalIgnoreCase) &&
               libraryOverlap.Reason.Contains("Capture Library", StringComparison.Ordinal),
            "Named source registration must reject nesting with the migrated watcher and Capture Library before creating catalog authority.");

        var added = await viewSource.RegisterAsync(
            "SteelSeries on games drive",
            RoutingInputSourceKind.SteelSeriesGg,
            steelSeriesRoot,
            enabled: true);
        var source = added.Source;
        Assert(added.Succeeded && source is { Enabled: false, Available: true } &&
               baselines.Load(source.SourceId).LoadedFromDisk,
            "Adding a named recorder source must persist it off and establish the immutable existing-clips baseline even if a caller requests immediate enablement.");

        var nestedOverlap = await viewSource.RegisterAsync(
            "Nested duplicate watcher",
            RoutingInputSourceKind.Nvidia,
            nestedRecorderRoot);
        Assert(!nestedOverlap.Succeeded &&
               nestedOverlap.Reason.Contains("overlaps another", StringComparison.OrdinalIgnoreCase) &&
               catalog.Inspect().Sources.Count == 1,
            "A second named source must not nest inside an existing non-retired source and double-admit the same clip.");

        var enabled = await viewSource.EnableAsync(source!.SourceId);
        var enabledRecord = catalog.Inspect().Sources.Single();
        Assert(enabled && enabledRecord.Enabled,
            "A route may enable a verified named source only after its matching baseline exists.");
        var disabled = await viewSource.SetEnabledAsync(
            enabledRecord.SourceId,
            enabledRecord.Revision,
            enabled: false);
        Assert(disabled.Succeeded && disabled.Source is { Enabled: false },
            "The source-card Disable action must keep a named source durable and off.");
        var disabledSource = disabled.Source ?? throw new InvalidOperationException(
            "The disabled named source result was missing its durable source.");
        var baselineLeaf = disabledSource!.SourceId[
            RoutingInputSourceCatalogModel.SourceIdPrefix.Length..];
        File.Delete(Path.Combine(baselineRoot, $"{baselineLeaf}.json"));
        var refused = await viewSource.SetEnabledAsync(
            disabledSource.SourceId,
            disabledSource.Revision,
            enabled: true);
        Assert(refused.Status == RoutingInputSourceViewActionStatus.NeedsAttention &&
               catalog.Inspect().Sources.Single() is { Enabled: false } &&
               refused.Reason.Contains("baseline", StringComparison.OrdinalIgnoreCase),
            "A missing named-source baseline must fail closed on the direct Enable action instead of redefining 'from now' at a later time.");

        var beforeReplacement = catalog.Inspect().Sources.Single();
        var replacementRequired = await viewSource.RelocateAsync(
            beforeReplacement.SourceId,
            beforeReplacement.Revision,
            replacementRoot);
        var marked = replacementRequired.Source ?? throw new InvalidOperationException(
            "The replacement-required result did not return its updated source revision.");
        Assert(replacementRequired.Status ==
                   RoutingInputSourceViewActionStatus.ReplacementRequired &&
               marked.Health == RoutingInputSourceHealth.NeedsAttention &&
               marked.AttentionReason ==
                   RoutingInputSourceAttentionReason.RootAuthorityChanged &&
               marked.Revision == beforeReplacement.Revision + 1,
            "Choosing a different SteelSeries folder must durably mark the original authority before offering replacement; ordinary watched roots cannot claim an identity-preserving move.");
        var replacement = await viewSource.RegisterReplacementAsync(
            marked.SourceId,
            marked.Revision,
            replacementRoot);
        var replacementSources = catalog.Inspect().Sources;
        var created = replacement.Source ?? throw new InvalidOperationException(
            "The named replacement result did not return its created source.");
        Assert(replacement.Succeeded && !created.Enabled && !created.Retired &&
               created.Kind == RoutingInputSourceKind.SteelSeriesGg &&
               replacementSources.Single(candidate =>
                   candidate.SourceId == marked.SourceId).Retired &&
               baselines.Load(created.SourceId).LoadedFromDisk,
            "A confirmed named-folder replacement must consume the updated revision, retain the old source tombstone, and create a separately baselined disabled source.");
    }

    private static void AssertRoutesRuntimeStateMatrix(
        RoutingRouteManager manager,
        string connectionId)
    {
        AssertRuntimeState(
            RoutesRuntimeViewState.Active,
            cutoverCommitted: true,
            expectedButtonText: "+  New route",
            expectedButtonEnabled: true,
            expectedStatusPrefix: "ROUTING ACTIVE",
            expectRouteMutationEnabled: true,
            expectRetry: false);
        AssertRuntimeState(
            RoutesRuntimeViewState.Activating,
            cutoverCommitted: false,
            expectedButtonText: "Starting…",
            expectedButtonEnabled: false,
            expectedStatusPrefix: "STARTING ROUTES",
            expectRouteMutationEnabled: false,
            expectRetry: false);
        AssertRuntimeState(
            RoutesRuntimeViewState.LegacySetupNeeded,
            cutoverCommitted: false,
            expectedButtonText: "Open Settings",
            expectedButtonEnabled: true,
            expectedStatusPrefix: "SETUP REQUIRED",
            expectRouteMutationEnabled: false,
            expectRetry: false,
            expectOpenSettings: true);
        AssertRuntimeState(
            RoutesRuntimeViewState.LegacyActive,
            cutoverCommitted: false,
            expectedButtonText: "Retry activation",
            expectedButtonEnabled: true,
            expectedStatusPrefix: "ROUTING NOT ACTIVE",
            expectRouteMutationEnabled: false,
            expectRetry: true);
        AssertRuntimeState(
            RoutesRuntimeViewState.RecoveryNeeded,
            cutoverCommitted: true,
            expectedButtonText: "Retry recovery",
            expectedButtonEnabled: true,
            expectedStatusPrefix: "ROUTES NEED ATTENTION",
            expectRouteMutationEnabled: false,
            expectRetry: true);
        AssertRuntimeState(
            RoutesRuntimeViewState.Blocked,
            cutoverCommitted: true,
            expectedButtonText: "Needs attention",
            expectedButtonEnabled: false,
            expectedStatusPrefix: "ROUTES BLOCKED",
            expectRouteMutationEnabled: false,
            expectRetry: false);
        AssertRuntimeState(
            RoutesRuntimeViewState.Active,
            cutoverCommitted: false,
            expectedButtonText: "+  New route",
            expectedButtonEnabled: false,
            expectedStatusPrefix: "ROUTES BLOCKED",
            expectRouteMutationEnabled: false,
            expectRetry: false);

        void AssertRuntimeState(
            RoutesRuntimeViewState state,
            bool cutoverCommitted,
            string expectedButtonText,
            bool expectedButtonEnabled,
            string expectedStatusPrefix,
            bool expectRouteMutationEnabled,
            bool expectRetry,
            bool expectOpenSettings = false)
        {
            var retryCalls = 0;
            var openSettingsCalls = 0;
            using var form = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000),
                ClientSize = new Size(984, 696),
                BackColor = ClipCordTheme.SurfaceBase
            };
            using var view = new RoutesView(
                manager,
                new FixedConnections(connectionId),
                isCutoverCommitted: () => cutoverCommitted,
                runtimeStateProvider: () => state,
                retryRuntimeAsync: () =>
                {
                    retryCalls++;
                    return Task.FromResult(true);
                });
            view.OpenSettingsRequested += (_, _) => openSettingsCalls++;
            form.Controls.Add(view);
            form.Show();
            view.ActivateView();
            Application.DoEvents();

            var controls = Enumerate(view).ToArray();
            var status = controls.OfType<Label>().Single(label =>
                label.Name == "RoutingCutoverStatusLabel");
            Assert(view.HeaderActionButton.Text == expectedButtonText &&
                   view.HeaderActionButton.Enabled == expectedButtonEnabled &&
                   status.Text.StartsWith(expectedStatusPrefix, StringComparison.Ordinal),
                $"Routes state {state} (cutover={cutoverCommitted}) must expose the truthful primary action and status copy.");

            var mutationControls = controls.Where(control =>
                    control.Name.StartsWith("RouteEnabled_", StringComparison.Ordinal) ||
                    control.Name.StartsWith("DeleteRoute_", StringComparison.Ordinal))
                .ToArray();
            Assert(mutationControls.Length > 0 &&
                   mutationControls.All(control =>
                       control.Enabled == expectRouteMutationEnabled),
                $"Routes state {state} (cutover={cutoverCommitted}) must {(expectRouteMutationEnabled ? "allow" : "block")} durable route mutation.");

            if (expectRetry || expectOpenSettings)
            {
                ((Button)view.HeaderActionButton).PerformClick();
                Application.DoEvents();
                Assert(retryCalls == (expectRetry ? 1 : 0) &&
                       openSettingsCalls == (expectOpenSettings ? 1 : 0),
                    $"Routes state {state} must invoke only its truthful recovery or setup action exactly once.");
            }
            else
            {
                Assert(retryCalls == 0 && openSettingsCalls == 0,
                    $"Routes state {state} must not invoke a recovery or setup callback without that enabled action.");
            }

            form.Close();
        }
    }

    private static void AssertRuntimeStateChangesBlockCommandAdmission(
        RoutingRouteManager manager,
        string connectionId)
    {
        var runtimeState = RoutesRuntimeViewState.Active;
        var runtimeReads = 0;
        var editorCalls = 0;
        var commandCalls = 0;
        var baseline = manager.Load().Document!;
        using var form = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ClientSize = new Size(984, 696),
            BackColor = ClipCordTheme.SurfaceBase
        };
        using var view = new RoutesView(
            manager,
            new FixedConnections(connectionId),
            isCutoverCommitted: () => true,
            editRoute: _ =>
            {
                editorCalls++;
                return null;
            },
            runtimeStateProvider: () =>
            {
                runtimeReads++;
                return runtimeState;
            });
        form.Controls.Add(view);
        form.Show();
        view.ActivateView();
        Application.DoEvents();
        Assert(view.HeaderActionButton.Enabled,
            "The admission regression fixture must begin with the cached Routes state active.");
        Assert(view.TryBeginCreateRoute() && editorCalls == 1 &&
               manager.Load().Document == baseline,
            "Home's create-route handoff must select Routes and invoke the real New route action while Routing is active.");
        editorCalls = 0;

        runtimeState = RoutesRuntimeViewState.RecoveryNeeded;
        var readsBeforeAdd = runtimeReads;
        view.AddRouteAsync().GetAwaiter().GetResult();
        Assert(runtimeReads > readsBeforeAdd && editorCalls == 0 &&
               manager.Load().Document == baseline,
            "Add route admission must re-read the runtime provider and avoid opening the editor after Active changes to RecoveryNeeded.");

        runtimeState = RoutesRuntimeViewState.Blocked;
        var readsBeforeCommand = runtimeReads;
        var admitted = view.RunRouteCommandAsync(() =>
        {
            commandCalls++;
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
        Assert(runtimeReads > readsBeforeCommand && !admitted && commandCalls == 0 &&
               manager.Load().Document == baseline,
            "Route command admission must re-read the runtime provider and reject a mutation after Active changes to Blocked.");

        form.Close();
    }

    private static void AssertExplicitDiscordConnectionSelection(string firstConnectionId)
    {
        const string secondConnectionId = "discord.22222222222222222222222222222222";
        RoutingConnectionDisplay[] connections =
        [
            new RoutingConnectionDisplay(
                firstConnectionId, "Alpha server", "Encrypted test connection", true),
            new RoutingConnectionDisplay(
                secondConnectionId, "Bravo server", "Encrypted test connection", true)
        ];
        using var dialog = new RouteEditorDialog(connections, layoutDpi: 96);
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new Point(-32000, -32000);
        dialog.Opacity = 0;
        dialog.Show();
        Application.DoEvents();
        Enumerate(dialog).OfType<TextBox>().Single(control =>
            control.AccessibleName == "Route name").Text = "Explicit destination";
        var next = Enumerate(dialog).OfType<Button>().Single(button =>
            button.Name == "NextRouteStepButton");
        next.PerformClick();
        next.PerformClick();
        Application.DoEvents();
        var controls = Enumerate(dialog).ToArray();
        var selector = controls.OfType<RouteConnectionSelector>().Single(control =>
            control.Name == "RouteDiscordConnectionSelector");
        var selectorLabel = controls.OfType<Label>().Single(label =>
            label.Text == "DISCORD CONNECTION");
        Assert(selector.ItemCount == 2 && selector.SelectedIndex == -1,
            "A route must not silently select the alphabetically first Discord connection.");
        Assert(selectorLabel.Visible && selector.AccessibleName == "Discord connection" &&
               selectorLabel.Parent == selector.Parent &&
               !selectorLabel.Bounds.IntersectsWith(selector.Bounds),
            "The Discord connection selector must have a visible non-overlapping label as well as an accessible name.");
        selector.SelectedIndex = 1;
        controls.OfType<Button>().Single(button =>
            button.Name == "NextRouteStepButton").PerformClick();
        Assert(dialog.Draft?.ConnectionId == secondConnectionId,
            "Saving a route must freeze the exact Discord connection explicitly selected by the user.");
    }

    private static void AssertRouteEditorSaveGuardsAreInjectable(string connectionId)
    {
        var messages = new List<(string Message, string Caption, MessageBoxIcon Icon)>();
        DialogResult ShowGuard(string message, string caption, MessageBoxIcon icon)
        {
            messages.Add((message, caption, icon));
            return DialogResult.OK;
        }

        using (var nameDialog = new RouteEditorDialog(
                   connections: [],
                   layoutDpi: 96,
                   saveGuardMessage: ShowGuard))
        {
            LayoutHeadlessly(nameDialog);

            InvokeSaveDraft(nameDialog);

            Assert(nameDialog.Draft is null && messages is
                   [
                       {
                           Message: "Give this route a name.",
                           Caption: "Route name required",
                           Icon: MessageBoxIcon.Information
                       }
                   ],
                "The missing-name save guard must use the injected non-modal seam while preserving its production copy and information severity.");
        }
        messages.Clear();

        using (var discordDialog = new RouteEditorDialog(
                   [new RoutingConnectionDisplay(
                       connectionId, "Friends server", "Encrypted test connection", true)],
                   layoutDpi: 96,
                   saveGuardMessage: ShowGuard))
        {
            LayoutHeadlessly(discordDialog);
            Enumerate(discordDialog).OfType<TextBox>().Single(control =>
                control.Name == "RouteNameEditor").Text = "Connection guard";

            InvokeSaveDraft(discordDialog);

            Assert(discordDialog.Draft is null && messages is
                   [
                       {
                           Message: "Choose the Discord connection this route should use.",
                           Caption: "Choose a Discord connection",
                           Icon: MessageBoxIcon.Warning
                       }
                   ],
                "The missing-Discord-connection save guard must use the injected non-modal seam while preserving its production copy and warning severity.");
        }
        messages.Clear();

        const string sourceId = "source.0123456789abcdef0123456789abcdef";
        var xboxSource = new RoutingInputSourceDisplay(
            sourceId,
            "Xbox captures · OneDrive",
            "Console captures synced through OneDrive",
            RoutingInputSourceKind.XboxGameDvrOneDrive,
            Path.Combine("C:\\", "Xbox", "Game DVR"),
            Enabled: true,
            Available: true);
        using var preflightStarted = new ManualResetEventSlim();
        using var releasePreflight = new ManualResetEventSlim();
        XboxDvrPreflightPreview BlockingPreflight(
            RoutingInputSourceDisplay source,
            XboxDvrHistoryPolicy policy,
            CancellationToken cancellationToken)
        {
            preflightStarted.Set();
            releasePreflight.Wait(cancellationToken);
            return new XboxDvrPreflightPreview(
                source.SourceId,
                source.CanonicalRoot,
                policy,
                TotalClipCount: 0,
                TotalLogicalBytes: 0,
                ParsedClipCount: 0,
                WindowMatchCount: 0,
                EligibleHistoricalCount: 0,
                EligibleHistoricalBytes: 0,
                BaselineOnlyCount: 0,
                NeedsAttentionCount: 0,
                Items: []);
        }

        try
        {
            using var xboxDialog = new RouteEditorDialog(
                connections: [],
                layoutDpi: 96,
                inputSources: [xboxSource],
                xboxPreflight: BlockingPreflight,
                saveGuardMessage: ShowGuard);
            LayoutHeadlessly(xboxDialog);
            var controls = Enumerate(xboxDialog).ToArray();
            controls.OfType<TextBox>().Single(control =>
                control.Name == "RouteNameEditor").Text = "Pending preview guard";
            controls.OfType<RadioButton>().Single(control =>
                control.Name == "RouteTriggerWatchedFolder").Checked = true;
            controls.OfType<ComboBox>().Single(control =>
                control.Name == "RouteWatchedSourceSelector").SelectedIndex = 1;
            PumpUntil(
                () => preflightStarted.IsSet && !xboxDialog.XboxPreflightCompletion.IsCompleted,
                "blocked Xbox route preflight");

            InvokeSaveDraft(xboxDialog);

            Assert(xboxDialog.Draft is null && messages is
                   [
                       {
                           Message: "Wait for ClipCord to finish the metadata-only Xbox preview before saving this route.",
                           Caption: "Xbox preview is still loading",
                           Icon: MessageBoxIcon.Information
                       }
                   ],
                "The pending-Xbox-preview save guard must use the injected non-modal seam while preserving its production copy and information severity.");
            releasePreflight.Set();
            SettleXboxPreflight(
                xboxDialog,
                () => true,
                "released Xbox route preflight after exercising the save guard");
        }
        finally
        {
            releasePreflight.Set();
        }
    }

    private static void AssertRouteEditorDialogLayout(string connectionId, int dpi)
    {
        var expectedClient = new Size(
            RouteEditorDialog.ScaleLogicalMetric(984, dpi),
            RouteEditorDialog.ScaleLogicalMetric(700, dpi));
        using var dialog = new RouteEditorDialog(
        [
            new RoutingConnectionDisplay(
                connectionId, "Friends server with a comfortably long name", "Encrypted test connection", true)
        ], layoutDpi: dpi)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Opacity = 0
        };
        dialog.Show();
        dialog.ClientSize = expectedClient;
        dialog.PerformLayout();
        Application.DoEvents();

        Assert(dialog.ClientSize == expectedClient,
            $"The route editor viewport must scale as one coherent surface at {dpi} DPI; expected={expectedClient}, actual={dialog.ClientSize}.");
        Assert(RouteEditorDialog.ScaleLogicalMetric(78, dpi) ==
               (int)Math.Round(78 * Math.Max(96, dpi) / 96d),
            $"Route editor logical metrics must remain deterministic at {dpi} DPI.");

        var stepsSeen = new[]
        {
            "RouteEditorStageContentStep1",
            "RouteEditorStageContentStep2",
            "RouteEditorStageContentStep3"
        };
        var stageRoots = new List<Control>();
        for (var step = 0; step < stepsSeen.Length; step++)
        {
            Application.DoEvents();
            var controls = Enumerate(dialog).Prepend<Control>(dialog).ToArray();
            var stageRoot = controls.SingleOrDefault(control => control.Name == stepsSeen[step]);
            Assert(stageRoot is not null,
                $"Route editor step {step + 1} must attach its intended stage at {dpi} DPI.");
            stageRoots.Add(stageRoot!);
            Assert(controls.Any(control => control.Name == "RouteEditorSummary") &&
                   controls.Any(control => control.Name == "RouteEditorStageHost") &&
                   controls.Any(control => control.Name == "RouteDialogActions"),
                "The route editor must retain the Figma summary, stage, and step-action hierarchy.");
            AssertDescendantsContained(dialog, dpi);
            AssertSiblingGeometry(dialog, dpi);
            AssertChoiceCards(controls, step + 1, dpi);
            AssertRouteEditorText(controls, dpi);

            if (step < stepsSeen.Length - 1)
            {
                controls.OfType<Button>().Single(button =>
                    button.Name == "NextRouteStepButton").PerformClick();
            }
        }

        var finalControls = Enumerate(dialog).ToArray();
        var connectionLabel = finalControls.OfType<Label>().Single(label =>
            label.Name == "RouteDiscordConnectionLabel");
        var connection = finalControls.OfType<RouteConnectionSelector>().Single(combo =>
            combo.Name == "RouteDiscordConnectionSelector");
        var predictedPreferredHeight = (int)Math.Ceiling(
            connection.GetPreferredSize(Size.Empty).Height * Math.Max(96, dpi) /
            (double)Math.Max(96, connection.DeviceDpi));
        Assert(connectionLabel.Bottom <= connection.Top &&
               connection.Height >= predictedPreferredHeight,
            $"The Discord field label and selector must remain separated and usable at {dpi} DPI; label={connectionLabel.Bounds}, selector={connection.Bounds}, predictedPreferredHeight={predictedPreferredHeight}.");
        Assert(RouteConnectionSelector.ScalePopupMetric(32, dpi) ==
               (int)Math.Round(32 * Math.Max(96, dpi) / 96d) &&
               RouteConnectionSelector.ScalePopupMetric(180, dpi) ==
               (int)Math.Round(180 * Math.Max(96, dpi) / 96d),
            $"The Discord connection menu must scale its row height and minimum width at {dpi} DPI.");

        var popupPresenterCalls = 0;
        using (var activationProbe = new RouteConnectionSelector(
                   [new RoutingConnectionDisplay(
                       connectionId, "Friends server", "Encrypted test connection", true)],
                   (_, _, _) => popupPresenterCalls++))
        {
            activationProbe.CreateControl();
            activationProbe.ActivatePopup();
            Assert(popupPresenterCalls == 1 &&
                   activationProbe.LastAppliedPopupDpi == Math.Max(96, activationProbe.DeviceDpi),
                "Activating the Discord selector must apply popup metrics before presenting the menu.");
        }

        connection.ApplyPopupMetrics(dpi);
        var popupItems = connection.PopupItemMetrics;
        var expectedPopupPadding = new Padding(
            RouteConnectionSelector.ScalePopupMetric(4, dpi));
        var expectedItemMargin = new Padding(
            RouteConnectionSelector.ScalePopupMetric(4, dpi), 0,
            RouteConnectionSelector.ScalePopupMetric(4, dpi), 0);
        var expectedItemPadding = new Padding(
            RouteConnectionSelector.ScalePopupMetric(8, dpi), 0,
            RouteConnectionSelector.ScalePopupMetric(8, dpi), 0);
        var expectedItemHeight = RouteConnectionSelector.ScalePopupMetric(32, dpi);
        var expectedItemWidth = connection.PopupClientWidth -
                                RouteConnectionSelector.ScalePopupMetric(8, dpi);
        var expectedPopupHeight = popupItems.Count *
                                  (expectedItemHeight + expectedItemMargin.Vertical) +
                                  RouteConnectionSelector.ScalePopupMetric(10, dpi);
        Assert(connection.PopupPadding == expectedPopupPadding &&
               connection.PopupWidth == Math.Max(
                   connection.Width, RouteConnectionSelector.ScalePopupMetric(180, dpi)) &&
               connection.PopupHeight == expectedPopupHeight &&
               popupItems.Count == connection.ItemCount &&
               popupItems.All(item => item.Width == expectedItemWidth &&
                                      item.Height == expectedItemHeight &&
                                      item.Margin == expectedItemMargin &&
                                      item.Padding == expectedItemPadding),
            $"The rendered Discord connection menu must apply scaled dimensions and padding at {dpi} DPI; " +
            $"padding={connection.PopupPadding}/{expectedPopupPadding}, width={connection.PopupWidth}/" +
            $"{Math.Max(connection.Width, RouteConnectionSelector.ScalePopupMetric(180, dpi))}, " +
            $"height={connection.PopupHeight}/{expectedPopupHeight}, clientWidth={connection.PopupClientWidth}, " +
            $"items={string.Join(", ", popupItems.Select(item => $"{item.Width}x{item.Height} margin={item.Margin} padding={item.Padding}"))}, " +
            $"expectedItem={expectedItemWidth}x{expectedItemHeight} margin={expectedItemMargin} padding={expectedItemPadding}.");
        dialog.Close();
        dialog.Dispose();
        Assert(stageRoots.All(stage => stage.IsDisposed) && connection.IsDisposed,
            "Closing the route editor must dispose attached and detached step controls.");
    }

    private static void AssertInputSourceConnectionDialog(
        string root,
        int dpi,
        bool verifyDraft)
    {
        var recorderRoot = Path.GetFullPath(Path.Combine(root, "nvidia-library"));
        Directory.CreateDirectory(recorderRoot);
        var validationMessages = new List<(string Message, string Caption)>();
        using var dialog = new InputSourceConnectionDialog(
            layoutDpi: dpi,
            chooseFolder: (_, _) => recorderRoot,
            showValidation: (message, caption) =>
                validationMessages.Add((message, caption)));
        LayoutHeadlessly(dialog);
        var controls = Enumerate(dialog).ToArray();
        var dialogRoot = controls.Single(control =>
            control.Name == "InputSourceDialogRoot");
        var steelSeries = controls.OfType<RadioButton>().Single(control =>
            control.Name == "InputSourceKindSteelSeries");
        var nvidia = controls.OfType<RadioButton>().Single(control =>
            control.Name == "InputSourceKindNvidia");
        var xbox = controls.OfType<RadioButton>().Single(control =>
            control.Name == "InputSourceKindXbox");
        var structureTitle = controls.OfType<Label>().Single(control =>
            control.Name == "InputSourceStructureTitle");
        var structureDetail = controls.OfType<Label>().Single(control =>
            control.Name == "InputSourceStructureDetail");
        var baseline = controls.OfType<Label>().Single(control =>
            control.Name == "InputSourceBaselineDetail");
        var browse = controls.OfType<Button>().Single(control =>
            control.Name == "BrowseInputSourceFolderButton");
        var save = controls.OfType<Button>().Single(control =>
            control.Name == "ConfirmAddInputSourceButton");
        Assert(steelSeries.Checked && !nvidia.Checked && !xbox.Checked &&
               structureTitle.Text.Contains("Flat folder", StringComparison.Ordinal) &&
               structureTitle.Text.Contains("directly", StringComparison.OrdinalIgnoreCase) &&
               structureDetail.Text.Contains("Subfolders are not scanned", StringComparison.Ordinal) &&
               baseline.Text.Contains("Existing clips", StringComparison.Ordinal) &&
               baseline.Text.Contains("stays off until a route", StringComparison.Ordinal) &&
               new Control[] { steelSeries, nvidia, xbox, browse, save }
                   .All(control => control.Enabled) &&
               steelSeries.TabStop && browse.TabStop && save.TabStop,
            $"The Add source dialog must open on the safe SteelSeries structure with keyboard-reachable recorder, folder, and save controls at {dpi} DPI; " +
            $"checked={steelSeries.Checked}/{nvidia.Checked}/{xbox.Checked}, title='{structureTitle.Text}', detail='{structureDetail.Text}', baseline='{baseline.Text}', " +
            $"enabled={steelSeries.Enabled}/{nvidia.Enabled}/{xbox.Enabled}/{browse.Enabled}/{save.Enabled}, tab={steelSeries.TabStop}/{browse.TabStop}/{save.TabStop}.");

        xbox.Checked = true;
        Assert(structureTitle.Text.Contains("Xbox Game DVR", StringComparison.Ordinal) &&
               structureDetail.Text.Contains("OneDrive", StringComparison.Ordinal) &&
               structureDetail.Text.Contains("remain", StringComparison.OrdinalIgnoreCase),
            "The Xbox source choice must explain its top-level OneDrive structure and untouched-original contract.");
        nvidia.Checked = true;
        Assert(structureTitle.Text.Contains("<Game>\\<clip>.mp4", StringComparison.Ordinal) &&
               structureDetail.Text.Contains("one folder per game", StringComparison.OrdinalIgnoreCase),
            "The NVIDIA source choice must identify the exact one-game-folder geometry that the watcher accepts.");
        InvokeControlClick(browse);
        var folder = controls.OfType<TextBox>().Single(control =>
            control.Name == "InputSourceFolder");
        var name = controls.OfType<TextBox>().Single(control =>
            control.Name == "InputSourceName");
        Assert(folder.Text == recorderRoot && folder.ReadOnly && !folder.TabStop,
            $"Folder browsing must place the canonical chosen recorder root in a read-only field without adding a redundant tab stop; text='{folder.Text}', expected='{recorderRoot}', readOnly={folder.ReadOnly}, tabStop={folder.TabStop}.");

        LayoutHeadlessly(dialog);
        AssertLocallyVisibleDescendantsContained(dialogRoot, dpi);
        AssertLocallyVisibleSiblingGeometry(dialogRoot, dpi);
        Assert(browse.Size == new Size(
                   RoutesView.ScaleLogicalMetric(98, dpi),
                   RoutesView.ScaleLogicalMetric(45, dpi)) ||
               browse.Width == RoutesView.ScaleLogicalMetric(98, dpi),
            $"The Add source folder action must preserve its scaled action column at {dpi} DPI.");
        if (!verifyDraft) return;
        name.Text = "NVIDIA clips · Games drive";
        InvokeControlClick(save);
        Assert(validationMessages.Count == 0 && dialog.Draft is
               {
                   DisplayName: "NVIDIA clips · Games drive",
                   Kind: RoutingInputSourceKind.Nvidia
               } draft &&
               draft.CanonicalRoot == recorderRoot,
            "The Add source dialog must return the exact custom name, recorder kind, and user-selected root without enabling or scanning it itself.");
    }

    private static void AssertNamedInputSourceConnectionsAndRouteSelection(
        RoutingRouteManager manager,
        string connectionId,
        int dpi,
        bool verifyActions)
    {
        const string privateRoot = @"D:\Recorder Libraries\SteelSeries";
        var sourceView = MutableInputSourceViewSource.Empty();
        var messages = new List<(string Message, string Caption)>();
        using var view = new RoutesView(
            manager,
            new FixedConnections(connectionId),
            isCutoverCommitted: () => true,
            layoutDpi: dpi,
            inputSources: sourceView,
            showInputSourceMessage: (message, caption) =>
                messages.Add((message, caption)),
            createInputSourceDraft: () => new RoutingInputSourceRegistrationDraft(
                "SteelSeries clips · Games drive",
                RoutingInputSourceKind.SteelSeriesGg,
                privateRoot),
            migratedInputSource: new RoutingMigratedInputSourceDisplay(
                "NVIDIA · migrated source",
                "Existing 1.x folder · locked to migration",
                ClipCaptureSource.Nvidia))
        {
            Size = new Size(
                RoutesView.ScaleLogicalMetric(984, dpi),
                RoutesView.ScaleLogicalMetric(696, dpi))
        };
        LayoutHeadlessly(view);
        Enumerate(view).OfType<Button>().Single(button =>
            button.Name == "ConnectionsRouteTab").PerformClick();
        LayoutHeadlessly(view);
        var controls = Enumerate(view).ToArray();
        var toolbar = controls.Single(control =>
            control.Name == "InputSourceToolbar");
        var add = controls.OfType<Button>().Single(button =>
            button.Name == "AddInputSourceButton");
        var migrated = controls.Single(control =>
            control.Name == "MigratedInputSourceCard");
        Assert(add.Enabled && add.TabStop &&
               migrated.Controls.Cast<Control>().SelectMany(Enumerate)
                   .OfType<Button>().Count() == 0 &&
               controls.OfType<Label>().Any(label =>
                   label.Name == "MigratedInputSourceStatus" &&
                   label.Text == "ACTIVE · MIGRATED") &&
               controls.OfType<Label>().Any(label =>
                   label.Name == "MigratedInputSourceLockExplanation" &&
                   label.Text.Contains("Read-only", StringComparison.Ordinal)),
            $"Connections must pair a keyboard-reachable Add source action with an explicit locked migrated-source card at {dpi} DPI.");

        add.PerformClick();
        PumpUntil(
            () => sourceView.RegisterCalls == 1 &&
                  Enumerate(view).Any(control =>
                      control.Name == "InputSourceCard_source.00000000000000000000000000000001"),
            $"named source registration at {dpi} DPI");
        LayoutHeadlessly(view);
        controls = Enumerate(view).ToArray();
        var source = sourceView.InspectInputSources().Sources.Single();
        var card = controls.Single(control =>
            control.Name == $"InputSourceCard_{source.SourceId}");
        var status = controls.OfType<Label>().Single(label =>
            label.Name == $"InputSourceStatus_{source.SourceId}");
        var replaceFolder = controls.OfType<Button>().Single(button =>
            button.Name == $"RelocateInputSource_{source.SourceId}");
        Assert(sourceView.LastRegistration is
               {
                   DisplayName: "SteelSeries clips · Games drive",
                   Kind: RoutingInputSourceKind.SteelSeriesGg,
                   CanonicalRoot: privateRoot
               } &&
               sourceView.LastRegistrationEnabled == false &&
               status.Text == "OFF" &&
               replaceFolder.Text == "Replace folder" &&
               replaceFolder.AccessibleName ==
                   "Replace folder for SteelSeries clips · Games drive" &&
               replaceFolder.AccessibleDescription?.Contains(
                   "separate source", StringComparison.OrdinalIgnoreCase) == true &&
               replaceFolder.AccessibleDescription?.Contains(
                   "does not inherit", StringComparison.OrdinalIgnoreCase) == true &&
               replaceFolder.AccessibleDescription?.Contains(
                   "pending work", StringComparison.OrdinalIgnoreCase) == true &&
               replaceFolder.AccessibleDescription?.Contains(
                   "reconnect the original", StringComparison.OrdinalIgnoreCase) == true &&
               messages.Any(item =>
                   item.Message.Contains("Existing clips", StringComparison.Ordinal) &&
                   item.Message.Contains("new clips from now", StringComparison.OrdinalIgnoreCase)) &&
               controls.All(control =>
                   !control.Text.Contains(privateRoot, StringComparison.OrdinalIgnoreCase)),
            "Adding SteelSeries from Connections must preserve the arbitrary folder privately, keep the source off, and explain the from-now baseline before route activation.");
        AssertLocallyVisibleDescendantsContained(toolbar, dpi);
        AssertLocallyVisibleSiblingGeometry(toolbar, dpi);
        AssertLocallyVisibleDescendantsContained(migrated, dpi);
        AssertLocallyVisibleSiblingGeometry(migrated, dpi);
        AssertLocallyVisibleDescendantsContained(card, dpi);
        AssertLocallyVisibleSiblingGeometry(card, dpi);

        var nvidiaSource = source with
        {
            SourceId = "source.22222222333344445555666677778888",
            Name = "NVIDIA clips · Editing drive",
            Detail = "NVIDIA · clips inside one game folder below the selected folder",
            Kind = RoutingInputSourceKind.Nvidia,
            CanonicalRoot = @"E:\ShadowPlay",
            Revision = 1
        };
        using var editor = new RouteEditorDialog(
            connections: [],
            layoutDpi: dpi,
            inputSources: [source, nvidiaSource],
            migratedInputSource: new RoutingMigratedInputSourceDisplay(
                "NVIDIA · migrated source",
                "Existing 1.x folder · locked to migration",
                ClipCaptureSource.Nvidia),
            saveGuardMessage: (_, _, _) => DialogResult.OK);
        LayoutHeadlessly(editor);
        var editorControls = Enumerate(editor).ToArray();
        var watchedFolder = editorControls.OfType<RadioButton>().Single(control =>
            control.Name == "RouteTriggerWatchedFolder");
        var selector = editorControls.OfType<ComboBox>().Single(control =>
            control.Name == "RouteWatchedSourceSelector");
        var history = editorControls.Single(control =>
            control.Name == "RouteXboxHistoryConfiguration");
        var safety = editorControls.OfType<Label>().Single(control =>
            control.Parent?.Name == "RouteSourceSafetyNote");
        var summaryWhen = editorControls.OfType<Label>().Single(control =>
            control.Name == "RouteSummaryWhenValue");
        Assert(selector.Items.Count == 3 &&
               selector.Items[0]!.ToString() == "NVIDIA · migrated source" &&
               selector.Items[1]!.ToString() == source.Name &&
               selector.Items[2]!.ToString() == nvidiaSource.Name,
            "The route editor must list the locked migrated watcher and every named recorder source by the user's saved name.");
        watchedFolder.Checked = true;
        selector.SelectedIndex = 1;
        LayoutHeadlessly(editor);
        Assert(!IsLocallyVisible(history) && !history.TabStop &&
               safety.Text.Contains("directly in this folder", StringComparison.Ordinal) &&
               safety.Text.Contains("only new completed clips", StringComparison.Ordinal) &&
               summaryWhen.Text == "SteelSeries GG",
            "Selecting SteelSeries must show its flat-folder and from-now contract without exposing Xbox HISTORY controls.");
        selector.SelectedIndex = 2;
        Assert(!IsLocallyVisible(history) &&
               safety.Text.Contains("<Game>\\<clip>.mp4", StringComparison.Ordinal) &&
               summaryWhen.Text == "NVIDIA",
            "Selecting NVIDIA must show its exact game-folder contract without Xbox history semantics.");
        selector.SelectedIndex = 1;
        editorControls.OfType<TextBox>().Single(control =>
            control.Name == "RouteNameEditor").Text = "SteelSeries friends route";
        InvokeSaveDraft(editor);
        Assert(editor.Draft is
               {
                   Trigger: RoutingTriggerKind.WatchedFolder,
                   WatchedSourceId: var watchedSourceId,
                   WatchedSourceKind: RoutingInputSourceKind.SteelSeriesGg,
                   EarliestCapturedUtc: null,
                   XboxHistorySelection: null
               } && watchedSourceId == source.SourceId,
            "Saving a named watched-folder route must freeze both its stable source id and exact recorder kind without fabricating Xbox history authority.");

        if (!verifyActions) return;
        var isolatedSource = new MutableInputSourceViewSource(source)
        {
            PrepareXboxFails = true
        };
        var isolationMessages = new List<string>();
        using (var isolatedView = new RoutesView(
                   manager,
                   new FixedConnections(connectionId),
                   isCutoverCommitted: () => true,
                   inputSources: isolatedSource,
                   showInputSourceMessage: (message, _) =>
                       isolationMessages.Add(message)))
        {
            var prepare = typeof(RoutesView).GetMethod(
                "PrepareInputSourcesAsync",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic) ??
                throw new InvalidOperationException(
                    "The route input-source preparation method was not found.");
            var prepared = ((Task<IReadOnlyList<RoutingInputSourceDisplay>>)prepare.Invoke(
                    isolatedView,
                    null)!)
                .GetAwaiter()
                .GetResult();
            Assert(prepared is [{ SourceId: var preparedSourceId }] &&
                   preparedSourceId == source.SourceId &&
                   isolationMessages.Single().Contains(
                       "Xbox", StringComparison.Ordinal),
                "A default-Xbox preparation failure must warn about Xbox while keeping valid SteelSeries and NVIDIA sources selectable in New Route.");
        }

        var orderingRoot = Path.Combine(
            Path.GetTempPath(),
            $"clipcord-route-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(orderingRoot);
        try
        {
            var orderingManager = new RoutingRouteManager(
                new RoutingSnapshotStore(Path.Combine(
                    orderingRoot, RoutingSnapshotStore.FileName)),
                () => new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
                TestRouteMutationAuthority.Allowed,
                TestRoutingConnectionMembership.AllowAll,
                new FixedInputSourceMembership(
                    source.SourceId,
                    RoutingInputSourceKind.SteelSeriesGg));
            var orderingSource = new MutableInputSourceViewSource(source)
            {
                EnableResult = false
            };
            var routeExistedBeforeEnable = false;
            orderingSource.BeforeEnable = () =>
                routeExistedBeforeEnable = orderingManager.Load().Document?.Routes.Any(route =>
                    route.Name == "Durable before watcher") == true;
            var warning = string.Empty;
            var orderedDraft = new RoutingRouteDraft(
                "Durable before watcher",
                RoutingTriggerKind.WatchedFolder,
                Game: null,
                Destination: null,
                ConnectionId: null,
                RoutingOutputKind.Original,
                RoutingDeliveryMode.Automatic,
                RoutingMissingOutputBehavior.UseOriginal,
                FileIntoLibrary: true,
                WatchedSourceId: source.SourceId,
                WatchedSourceKind: RoutingInputSourceKind.SteelSeriesGg);
            using var orderingView = new RoutesView(
                orderingManager,
                new FixedConnections(connectionId),
                isCutoverCommitted: () => true,
                editRoute: _ => orderedDraft,
                inputSources: orderingSource,
                showInputSourceMessage: (message, _) => warning = message);
            orderingView.AddRouteAsync().GetAwaiter().GetResult();
            Assert(routeExistedBeforeEnable && orderingSource.EnableCalls == 1 &&
                   orderingManager.Load().Document?.Routes.Any(route =>
                       route.Name == "Durable before watcher") == true &&
                   warning.Contains("route was saved", StringComparison.OrdinalIgnoreCase),
                "Route creation must durably bind the named source before enabling its watcher, and an enable failure must leave the saved route safely dormant instead of deleting it or risking fallback delivery.");
        }
        finally
        {
            Directory.Delete(orderingRoot, recursive: true);
        }
    }

    private static void AssertInputSourceManagementAndLayout(
        RoutingRouteManager manager,
        string connectionId,
        int dpi,
        bool verifyActions)
    {
        const string sourceId = "source.7777777788889999aaaabbbbbbbbbbbb";
        const string privateRoot = @"C:\Users\Tester\OneDrive\Videos\Xbox Game DVR";
        const string privateIdentity =
            "abababababababababababababababababababababababababababababababab";
        var source = new RoutingInputSourceDisplay(
            sourceId,
            "Xbox captures · OneDrive",
            "Console captures synced through OneDrive",
            RoutingInputSourceKind.XboxGameDvrOneDrive,
            privateRoot,
            Enabled: true,
            Available: false,
            Revision: 4,
            Health: RoutingInputSourceHealth.NeedsAttention);
        var sourceView = new MutableInputSourceViewSource(source);
        var allowRemove = false;
        var confirmationCalls = 0;
        using var view = new RoutesView(
            manager,
            new FixedConnections(connectionId),
            isCutoverCommitted: () => true,
            layoutDpi: dpi,
            inputSources: sourceView,
            confirmInputSourceRemoval: (_, _) =>
            {
                confirmationCalls++;
                return allowRemove ? DialogResult.OK : DialogResult.Cancel;
            })
        {
            Size = new Size(
                RoutesView.ScaleLogicalMetric(984, dpi),
                RoutesView.ScaleLogicalMetric(696, dpi))
        };
        LayoutHeadlessly(view);
        var connectionsTab = Enumerate(view).OfType<Button>().Single(button =>
            button.Name == "ConnectionsRouteTab");
        connectionsTab.PerformClick();
        LayoutHeadlessly(view);

        var controls = Enumerate(view).ToArray();
        var card = controls.Single(control =>
            control.Name == $"InputSourceCard_{sourceId}");
        var status = controls.OfType<Label>().Single(label =>
            label.Name == $"InputSourceStatus_{sourceId}");
        var recheck = controls.OfType<Button>().Single(button =>
            button.Name == $"RecheckInputSource_{sourceId}");
        var toggle = controls.OfType<Button>().Single(button =>
            button.Name == $"ToggleInputSource_{sourceId}");
        var remove = controls.OfType<Button>().Single(button =>
            button.Name == $"RemoveInputSource_{sourceId}");
        var relocate = controls.OfType<Button>().Single(button =>
            button.Name == $"RelocateInputSource_{sourceId}");
        Assert(status.Text == "NEEDS ATTENTION" &&
               recheck.Enabled && recheck.TabStop &&
               relocate.Enabled && relocate.TabStop &&
               toggle.Text == "Disable" && toggle.Enabled && toggle.TabStop &&
               remove.Enabled && remove.TabStop,
            "Connections must retain an unhealthy Xbox source with an explicit status and keyboard-reachable Recheck, Disable, and guarded Remove actions.");
        Assert(controls.OfType<Label>().Any(label =>
                   label.Text.Contains("Recheck before using", StringComparison.Ordinal)) &&
               controls.All(control =>
                   !control.Text.Contains(privateRoot, StringComparison.OrdinalIgnoreCase) &&
                   !control.Text.Contains(privateIdentity, StringComparison.OrdinalIgnoreCase) &&
                   !(control.AccessibleName?.Contains(
                       privateRoot, StringComparison.OrdinalIgnoreCase) ?? false) &&
                   !(control.AccessibleDescription?.Contains(
                       privateRoot, StringComparison.OrdinalIgnoreCase) ?? false)),
            "Source management must explain recovery without rendering the canonical OneDrive path or its root identity/hash.");
        AssertLocallyVisibleDescendantsContained(card, dpi);
        AssertLocallyVisibleSiblingGeometry(card, dpi);
        foreach (var essential in new Control[]
                 {
                     status, recheck, relocate, toggle, remove
                 })
        {
            var measured = TextRenderer.MeasureText(
                essential.Text,
                essential.Font,
                Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            var predictedWidth = (int)Math.Ceiling(
                measured.Width * Math.Max(96, dpi) /
                (double)Math.Max(96, essential.DeviceDpi));
            Assert(essential.Width + RoutesView.ScaleLogicalMetric(3, dpi) >= predictedWidth,
                $"{essential.Name} must fit without ellipsis at {dpi} DPI; predicted width={predictedWidth}, bounds={essential.ClientSize}.");
        }
        Assert(recheck.Size == new Size(
                   RoutesView.ScaleLogicalMetric(76, dpi),
                   RoutesView.ScaleLogicalMetric(30, dpi)) &&
               toggle.Size == new Size(
                   RoutesView.ScaleLogicalMetric(72, dpi),
                   RoutesView.ScaleLogicalMetric(30, dpi)) &&
               relocate.Size == new Size(
                   RoutesView.ScaleLogicalMetric(92, dpi),
                   RoutesView.ScaleLogicalMetric(30, dpi)) &&
               remove.Size == new Size(
                   RoutesView.ScaleLogicalMetric(72, dpi),
                   RoutesView.ScaleLogicalMetric(30, dpi)),
            $"Clip-source actions must scale coherently at {dpi} DPI.");
        var scroll = controls.OfType<BrandedScrollHost>().Single(host =>
            host.Name == "RoutesScrollHost");
        Assert(scroll.Content is not null && scroll.Content.Width > 0 &&
               scroll.Content.Height > 0 &&
               (!scroll.HasOverflow || scroll.Content.Height > scroll.ClientSize.Height),
            $"Clip-source management must remain inside branded scrolling at {dpi} DPI.");

        var blockedLayoutSource = source with
        {
            AttentionReason = RoutingInputSourceAttentionReason.BlockedOccurrence,
            SkippableBlockedClipCount = 2
        };
        using (var blockedLayoutView = new RoutesView(
                   manager,
                   new FixedConnections(connectionId),
                   isCutoverCommitted: () => true,
                   layoutDpi: dpi,
                   inputSources: new MutableInputSourceViewSource(blockedLayoutSource),
                   confirmInputSourceSkip: (_, _) => DialogResult.Cancel)
               {
                   Size = view.Size
               })
        {
            LayoutHeadlessly(blockedLayoutView);
            Enumerate(blockedLayoutView).OfType<Button>().Single(button =>
                button.Name == "ConnectionsRouteTab").PerformClick();
            LayoutHeadlessly(blockedLayoutView);
            var blockedControls = Enumerate(blockedLayoutView).ToArray();
            var blockedCard = blockedControls.Single(control =>
                control.Name == $"InputSourceCard_{sourceId}");
            var skip = blockedControls.OfType<Button>().Single(button =>
                button.Name == $"SkipBlockedInputSource_{sourceId}");
            var retry = blockedControls.OfType<Button>().Single(button =>
                button.Name == $"RetryBlockedInputSource_{sourceId}");
            static int PredictedTextWidth(Button button, int targetDpi)
            {
                var measured = TextRenderer.MeasureText(
                    button.Text,
                    button.Font,
                    Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                return (int)Math.Ceiling(
                    measured.Width * Math.Max(96, targetDpi) /
                    (double)Math.Max(96, button.DeviceDpi));
            }
            Assert(skip.Text == "Skip blocked clips (2)" &&
                   skip.Enabled && skip.TabStop &&
                   skip.Width + RoutesView.ScaleLogicalMetric(3, dpi) >=
                       PredictedTextWidth(skip, dpi) &&
                   retry.Text == "Retry blocked clips (2)" &&
                   retry.Enabled && retry.TabStop &&
                   retry.Width + RoutesView.ScaleLogicalMetric(3, dpi) >=
                       PredictedTextWidth(retry, dpi) &&
                   blockedControls.OfType<Button>().All(button =>
                       button.Name != $"RecheckInputSource_{sourceId}" &&
                       button.Name != $"RelocateInputSource_{sourceId}"),
                $"A known blocked Xbox source must expose exact, unclipped Retry and Skip actions at {dpi} DPI without unrelated folder recovery.");
            AssertLocallyVisibleDescendantsContained(blockedCard, dpi);
            AssertLocallyVisibleSiblingGeometry(blockedCard, dpi);
            Assert(blockedControls.All(control =>
                    !control.Text.Contains(privateRoot, StringComparison.OrdinalIgnoreCase)),
                $"Blocked-source recovery UI must keep source paths private at {dpi} DPI.");
        }

        if (verifyActions)
        {
            recheck.PerformClick();
            PumpUntil(
                () => sourceView.RecheckCalls == 1 &&
                      Enumerate(view).OfType<Label>().Any(label =>
                          label.Name == $"InputSourceStatus_{sourceId}" &&
                          label.Text == "READY"),
                "Xbox source recheck action");
            controls = Enumerate(view).ToArray();
            recheck = controls.OfType<Button>().Single(button =>
                button.Name == $"RecheckInputSource_{sourceId}");
            toggle = controls.OfType<Button>().Single(button =>
                button.Name == $"ToggleInputSource_{sourceId}");
            Assert(!recheck.Enabled && !recheck.TabStop && toggle.Text == "Disable",
                "A successful Recheck must refresh the card to Ready and remove the no-longer-applicable action from tab order.");

            toggle.PerformClick();
            PumpUntil(
                () => sourceView.ToggleCalls == 1 &&
                      Enumerate(view).OfType<Label>().Any(label =>
                          label.Name == $"InputSourceStatus_{sourceId}" &&
                          label.Text == "OFF"),
                "Xbox source disable action");
            toggle = Enumerate(view).OfType<Button>().Single(button =>
                button.Name == $"ToggleInputSource_{sourceId}");
            Assert(toggle.Text == "Enable" && toggle.Enabled && toggle.TabStop,
                "Disabling a healthy source must preserve it in management and expose Enable.");
            toggle.PerformClick();
            PumpUntil(() => sourceView.ToggleCalls == 2, "Xbox source enable action");

            remove = Enumerate(view).OfType<Button>().Single(button =>
                button.Name == $"RemoveInputSource_{sourceId}");
            remove.PerformClick();
            Application.DoEvents();
            Assert(confirmationCalls == 1 && sourceView.RemoveCalls == 0,
                "Cancelling guarded source removal must not call the durable catalog.");
            allowRemove = true;
            remove.PerformClick();
            PumpUntil(
                () => sourceView.RemoveCalls == 1 &&
                      Enumerate(view).Any(control =>
                          control.Name == "InputSourcesEmptyState"),
                "Xbox source guarded removal action");
            Assert(confirmationCalls == 2,
                "Confirmed source removal must call the durable catalog exactly once.");
        }

        if (dpi != 96) return;
        using var unavailableView = new RoutesView(
            manager,
            new FixedConnections(connectionId),
            isCutoverCommitted: () => true,
            layoutDpi: dpi,
            inputSources: MutableInputSourceViewSource.Unavailable())
        {
            Size = view.Size
        };
        LayoutHeadlessly(unavailableView);
        Enumerate(unavailableView).OfType<Button>().Single(button =>
            button.Name == "ConnectionsRouteTab").PerformClick();
        LayoutHeadlessly(unavailableView);
        var unavailableControls = Enumerate(unavailableView).ToArray();
        Assert(unavailableControls.Any(control =>
                   control.Name == "InputSourcesUnavailableState") &&
               unavailableControls.All(control =>
                   control.Name != "InputSourcesEmptyState") &&
               unavailableControls.OfType<Label>().Any(label =>
                   label.Text == "Status unavailable"),
            "An unreadable input-source catalog must render an explicit unavailable state, never a misleading zero-source state.");

        var retiredSource = source with
        {
            SourceId = "source.66666666777788889999aaaaaaaaaaaa",
            Name = "Xbox captures · Replaced OneDrive",
            Enabled = false,
            Retired = true
        };
        using var retiredView = new RoutesView(
            manager,
            new FixedConnections(connectionId),
            isCutoverCommitted: () => true,
            layoutDpi: dpi,
            inputSources: new MutableInputSourceViewSource(retiredSource))
        {
            Size = view.Size
        };
        LayoutHeadlessly(retiredView);
        Enumerate(retiredView).OfType<Button>().Single(button =>
            button.Name == "ConnectionsRouteTab").PerformClick();
        LayoutHeadlessly(retiredView);
        var retiredControls = Enumerate(retiredView).ToArray();
        var retiredStatus = retiredControls.OfType<Label>().Single(label =>
            label.Name == $"InputSourceStatus_{retiredSource.SourceId}");
        var retiredToggle = retiredControls.OfType<Button>().Single(button =>
            button.Name == $"ToggleInputSource_{retiredSource.SourceId}");
        Assert(retiredStatus.Text == "REPLACED" &&
               retiredControls.OfType<Button>().All(button =>
                   button.Name != $"RecheckInputSource_{retiredSource.SourceId}" &&
                   button.Name != $"RelocateInputSource_{retiredSource.SourceId}") &&
               !retiredToggle.Enabled && !retiredToggle.TabStop &&
               retiredControls.OfType<Label>().Any(label =>
                   label.Text.Contains(
                       "retained for existing routes", StringComparison.Ordinal)),
            "A retired replacement tombstone must stay visible for existing references while remaining unavailable to Recheck, Enable, and new work.");
        AssertInputSourceRecoveryActions(manager, connectionId, view.Size);
    }

    private static void AssertInputSourceRecoveryActions(
        RoutingRouteManager manager,
        string connectionId,
        Size viewport)
    {
        const string sourceId = "source.44444444555566667777888888888888";
        const string oldRoot = @"C:\private\saved\Xbox Game DVR";
        const string movedRoot = @"D:\private\moved\Xbox Game DVR";
        const string differentRoot = @"E:\private\different\Xbox Game DVR";
        var source = new RoutingInputSourceDisplay(
            sourceId,
            "Xbox captures · OneDrive",
            "Console captures synced through OneDrive",
            RoutingInputSourceKind.XboxGameDvrOneDrive,
            oldRoot,
            Enabled: true,
            Available: false,
            Revision: 4,
            Health: RoutingInputSourceHealth.NeedsAttention,
            AttentionReason: RoutingInputSourceAttentionReason.MetadataInvalid);

        var readySource = source with
        {
            Available = true,
            Health = RoutingInputSourceHealth.Ready,
            AttentionReason = RoutingInputSourceAttentionReason.None
        };
        var relocation = new MutableInputSourceViewSource(readySource);
        var relocationMessages = new List<string>();
        using (var view = CreateView(
                   relocation,
                   () => movedRoot,
                   (_, _) => DialogResult.Cancel,
                   (message, _) => relocationMessages.Add(message)))
        {
            ClickConnections(view);
            var findFolder = Enumerate(view).OfType<Button>().Single(button =>
                button.Name == $"RelocateInputSource_{sourceId}");
            Assert(findFolder.Enabled && findFolder.TabStop,
                "A Ready Xbox source whose saved folder went offline must still expose a keyboard-reachable Find folder action.");
            Click(view, $"RelocateInputSource_{sourceId}");
            PumpUntil(
                () => relocation.RelocateCalls == 1 &&
                      Enumerate(view).OfType<Label>().Any(label =>
                          label.Name == $"InputSourceStatus_{sourceId}" &&
                          label.Text == "READY"),
                "same-authority Xbox source relocation");
            var relocated = relocation.InspectInputSources().Sources.Single();
            Assert(relocated.SourceId == sourceId &&
                   relocated.CanonicalRoot == movedRoot &&
                   relocation.LastRelocationRoot == movedRoot &&
                   relocation.ReplacementCalls == 0 &&
                   relocationMessages.Count == 0,
                "A metadata-verified same-authority move must preserve SourceId and update only the private root without invoking replacement.");
            Assert(Enumerate(view).All(control =>
                    !control.Text.Contains(movedRoot, StringComparison.OrdinalIgnoreCase) &&
                    !(control.AccessibleName?.Contains(
                        movedRoot, StringComparison.OrdinalIgnoreCase) ?? false)),
                "A successful relocation must not render the discovered private path.");
        }

        var invalidRelocation = new MutableInputSourceViewSource(source);
        using (var view = CreateView(
                   invalidRelocation,
                   () => movedRoot,
                   (_, _) => DialogResult.Cancel,
                   (_, _) => { }))
        {
            ClickConnections(view);
            Click(view, $"RelocateInputSource_{sourceId}");
            PumpUntil(
                () => invalidRelocation.RelocateCalls == 1 &&
                      Enumerate(view).OfType<Label>().Any(label =>
                          label.Name == $"InputSourceStatus_{sourceId}" &&
                          label.Text == "NEEDS ATTENTION"),
                "metadata-invalid Xbox source relocation control");
            var invalidAfterMove = invalidRelocation.InspectInputSources().Sources.Single();
            Assert(invalidAfterMove.CanonicalRoot == movedRoot &&
                   invalidAfterMove.Health == RoutingInputSourceHealth.NeedsAttention &&
                   invalidAfterMove.AttentionReason ==
                       RoutingInputSourceAttentionReason.MetadataInvalid &&
                   !invalidAfterMove.Available,
                "Finding a same-authority moved folder must not falsely clear invalid occurrence metadata; only RootAuthorityChanged can recover through relocation.");
            var invalidControls = Enumerate(view).ToArray();
            Assert(invalidControls.OfType<Button>().All(button =>
                       button.Name != $"SkipBlockedInputSource_{sourceId}" &&
                       button.Name != $"RetryBlockedInputSource_{sourceId}") &&
                   invalidControls.OfType<Button>().Any(button =>
                       button.Name == $"RecheckInputSource_{sourceId}"),
                "Generic MetadataInvalid source evidence must never be mislabeled as a durably skippable blocked clip.");
        }

        var blockedSource = source with
        {
            AttentionReason = RoutingInputSourceAttentionReason.BlockedOccurrence,
            SkippableBlockedClipCount = 2
        };
        var blocked = new MutableInputSourceViewSource(blockedSource);
        var allowSkip = false;
        var skipConfirmations = new List<string>();
        var skipMessages = new List<string>();
        using (var view = CreateView(
                   blocked,
                   () => null,
                   (_, _) => DialogResult.Cancel,
                   (message, _) => skipMessages.Add(message),
                   (message, _) =>
                   {
                       skipConfirmations.Add(message);
                       return allowSkip ? DialogResult.OK : DialogResult.Cancel;
                   }))
        {
            ClickConnections(view);
            var controls = Enumerate(view).ToArray();
            var skip = controls.OfType<Button>().Single(button =>
                button.Name == $"SkipBlockedInputSource_{sourceId}");
            var retry = controls.OfType<Button>().Single(button =>
                button.Name == $"RetryBlockedInputSource_{sourceId}");
            Assert(skip.Text == "Skip blocked clips (2)" &&
                   skip.Enabled && skip.TabStop &&
                   retry.Text == "Retry blocked clips (2)" &&
                   retry.Enabled && retry.TabStop &&
                   controls.OfType<Button>().All(button =>
                       button.Name != $"RecheckInputSource_{sourceId}") &&
                   controls.OfType<Label>().Any(label =>
                       label.Text.Contains(
                           "This Xbox source is paused", StringComparison.Ordinal) &&
                       label.Text.Contains(
                           "Retry them safely", StringComparison.Ordinal) &&
                       label.Text.Contains(
                           "other clip sources keep running", StringComparison.Ordinal)),
                "Only a source with a known durable blocker count may expose exact, keyboard-reachable Retry and Skip actions and a source-scoped paused state.");
            skip.PerformClick();
            Application.DoEvents();
            Assert(blocked.SkipBlockedCalls == 0 && skipConfirmations.Count == 1,
                "Cancelling blocked-clip confirmation must not change the occurrence journal or source state.");
            allowSkip = true;
            skip.PerformClick();
            PumpUntil(
                () => blocked.SkipBlockedCalls == 1 &&
                      Enumerate(view).OfType<Label>().Any(label =>
                          label.Name == $"InputSourceStatus_{sourceId}" &&
                          label.Text == "READY"),
                "confirmed blocked Xbox clip skip");
            Assert(skipConfirmations.Last().Contains(
                       "Skip 2 blocked clips", StringComparison.Ordinal) &&
                   skipConfirmations.Last().Contains(
                       "OneDrive originals remain untouched", StringComparison.Ordinal) &&
                   skipConfirmations.Last().Contains(
                       "clips will not be routed", StringComparison.Ordinal) &&
                   skipConfirmations.Last().Contains(
                       "future clips resume", StringComparison.Ordinal) &&
                   skipMessages.Single().Contains(
                       "2 blocked clips were skipped", StringComparison.Ordinal) &&
                   skipMessages.Single().Contains(
                       "OneDrive originals remain untouched", StringComparison.Ordinal) &&
                   skipMessages.All(message =>
                       !message.Contains(oldRoot, StringComparison.OrdinalIgnoreCase)),
                "Confirmed skip must explain the exact durable set, preserve OneDrive originals, exclude those clips from routing, and make future-source recovery explicit without exposing private paths.");
        }

        var retryBlocked = new MutableInputSourceViewSource(blockedSource);
        var retryMessages = new List<string>();
        using (var view = CreateView(
                   retryBlocked,
                   () => null,
                   (_, _) => DialogResult.Cancel,
                   (message, _) => retryMessages.Add(message)))
        {
            ClickConnections(view);
            Click(view, $"RetryBlockedInputSource_{sourceId}");
            PumpUntil(
                () => retryBlocked.RetryBlockedCalls == 1 &&
                      Enumerate(view).OfType<Label>().Any(label =>
                          label.Name == $"InputSourceStatus_{sourceId}" &&
                          label.Text == "READY"),
                "blocked Xbox clip retry");
            Assert(retryMessages.Single().Contains(
                       "queued for safe retry", StringComparison.Ordinal) &&
                   retryMessages.Single().Contains(
                       "OneDrive originals remain untouched", StringComparison.Ordinal) &&
                   retryMessages.Single().Contains(
                       "before later clips", StringComparison.Ordinal) &&
                   retryMessages.All(message =>
                       !message.Contains(oldRoot, StringComparison.OrdinalIgnoreCase)),
                "Retry must preserve OneDrive originals, resume blocked work before later clips, and keep private paths out of UI copy.");
        }

        var retryPendingSource = source with
        {
            AttentionReason = RoutingInputSourceAttentionReason.BlockedOccurrence,
            BlockedClipRetryPending = true
        };
        var retryPendingViewSource = new MutableInputSourceViewSource(retryPendingSource);
        var retryPendingMessages = new List<string>();
        using (var view = CreateView(
                   retryPendingViewSource,
                   () => null,
                   (_, _) => DialogResult.Cancel,
                   (message, _) => retryPendingMessages.Add(message)))
        {
            ClickConnections(view);
            var controls = Enumerate(view).ToArray();
            var resumeRetry = controls.OfType<Button>().Single(button =>
                button.Name == $"ResumeRetryInputSource_{sourceId}");
            Assert(resumeRetry.Text == "Resume retry" &&
                   resumeRetry.Enabled && resumeRetry.TabStop &&
                   controls.OfType<Button>().All(button =>
                       button.Name != $"RetryBlockedInputSource_{sourceId}" &&
                       button.Name != $"SkipBlockedInputSource_{sourceId}" &&
                       button.Name != $"RecheckInputSource_{sourceId}" &&
                       button.Name != $"RelocateInputSource_{sourceId}") &&
                   controls.OfType<Label>().Any(label =>
                       label.Text.Contains(
                           "retry is already durable", StringComparison.Ordinal) &&
                       label.Text.Contains(
                           "before later clips continue", StringComparison.Ordinal)),
                "A durable retry awaiting catalog convergence must expose only the exact Resume retry recovery action.");
            resumeRetry.PerformClick();
            PumpUntil(
                () => retryPendingViewSource.RetryBlockedCalls == 1 &&
                      Enumerate(view).OfType<Label>().Any(label =>
                          label.Name == $"InputSourceStatus_{sourceId}" &&
                          label.Text == "READY"),
                "durable blocked Xbox retry resume");
            Assert(retryPendingMessages.Single().Contains(
                       "already durable", StringComparison.Ordinal) &&
                   retryPendingMessages.Single().Contains(
                       "ready to resume", StringComparison.Ordinal) &&
                   retryPendingMessages.All(message =>
                       !message.Contains(oldRoot, StringComparison.OrdinalIgnoreCase)),
                "Resume retry must converge the catalog without repeating or exposing private source data.");
        }

        var recoveryPendingSource = source with
        {
            AttentionReason = RoutingInputSourceAttentionReason.BlockedOccurrence,
            BlockedClipRecoveryPending = true
        };
        var recoveryPending = new MutableInputSourceViewSource(recoveryPendingSource);
        var recoveryMessages = new List<string>();
        using (var view = CreateView(
                   recoveryPending,
                   () => null,
                   (_, _) => DialogResult.Cancel,
                   (message, _) => recoveryMessages.Add(message)))
        {
            ClickConnections(view);
            var controls = Enumerate(view).ToArray();
            var resume = controls.OfType<Button>().Single(button =>
                button.Name == $"ResumeBlockedInputSource_{sourceId}");
            Assert(resume.Text == "Resume source" &&
                   resume.Enabled && resume.TabStop &&
                   controls.OfType<Button>().All(button =>
                       button.Name != $"SkipBlockedInputSource_{sourceId}" &&
                       button.Name != $"RetryBlockedInputSource_{sourceId}") &&
                   controls.OfType<Label>().Any(label =>
                       label.Text.Contains(
                           "blocked clips were already skipped", StringComparison.Ordinal) &&
                       label.Text.Contains(
                           "other clip sources keep running", StringComparison.Ordinal)),
                "A durable prior skip awaiting source convergence must expose a source-scoped Resume action without asking to skip the clips again.");
            resume.PerformClick();
            PumpUntil(
                () => recoveryPending.SkipBlockedCalls == 1 &&
                      Enumerate(view).OfType<Label>().Any(label =>
                          label.Name == $"InputSourceStatus_{sourceId}" &&
                          label.Text == "READY"),
                "blocked Xbox source resume");
            Assert(recoveryMessages.Single().Contains(
                       "Xbox source is ready", StringComparison.Ordinal) &&
                   recoveryMessages.All(message =>
                       !message.Contains(oldRoot, StringComparison.OrdinalIgnoreCase)),
                "Resume must finish the already-confirmed recovery without exposing private source data.");
        }

        var samePathReplacement = new MutableInputSourceViewSource(source)
        {
            RecheckRequiresReplacement = true
        };
        var allowReplacement = false;
        var replacementConfirmations = new List<string>();
        var replacementMessages = new List<string>();
        using (var view = CreateView(
                   samePathReplacement,
                   () => null,
                   (message, _) =>
                   {
                       replacementConfirmations.Add(message);
                       return allowReplacement ? DialogResult.OK : DialogResult.Cancel;
                   },
                   (message, _) => replacementMessages.Add(message)))
        {
            ClickConnections(view);
            Click(view, $"RecheckInputSource_{sourceId}");
            PumpUntil(
                () => samePathReplacement.RecheckCalls == 1 &&
                      Enumerate(view).OfType<Button>().Any(button =>
                          button.Name == $"ReplaceInputSource_{sourceId}"),
                "same-path Xbox replacement discovery");
            var controls = Enumerate(view).ToArray();
            var replacementStatus = controls.OfType<Label>().Single(label =>
                label.Name == $"InputSourceStatus_{sourceId}");
            var replace = controls.OfType<Button>().Single(button =>
                button.Name == $"ReplaceInputSource_{sourceId}");
            Assert(replacementStatus.Text == "NEEDS REPLACEMENT" &&
                   controls.OfType<Button>().All(button =>
                       button.Name != $"RecheckInputSource_{sourceId}") &&
                   replace.Enabled && replace.TabStop &&
                   replacementMessages.Single().Contains(
                       "cannot inherit existing routes or approved history",
                       StringComparison.Ordinal) &&
                   replacementMessages.Single().Contains(
                       "create a new route", StringComparison.OrdinalIgnoreCase),
                "ReplacementRequired from Recheck must become an explicit, keyboard-reachable replacement action with fresh-route/history guidance.");

            replace.PerformClick();
            Application.DoEvents();
            Assert(replacementConfirmations.Count == 1 &&
                   samePathReplacement.ReplacementCalls == 0,
                "Cancelling replacement confirmation must leave both source identity and route bindings unchanged.");
            allowReplacement = true;
            replace.PerformClick();
            PumpUntil(
                () => samePathReplacement.ReplacementCalls == 1 &&
                      samePathReplacement.InspectInputSources().Sources.Count == 2,
                "same-path Xbox replacement registration");
            var replacedSources = samePathReplacement.InspectInputSources().Sources;
            Assert(replacedSources.Single(item => item.SourceId == sourceId).Retired &&
                   replacedSources.Single(item => item.SourceId != sourceId) is
                   { Enabled: false, Retired: false } &&
                   samePathReplacement.LastReplacementRoot == oldRoot &&
                   replacementConfirmations.Last().Contains(
                       "Existing routes and approved history remain bound",
                       StringComparison.Ordinal) &&
                   replacementConfirmations.Last().Contains(
                       "create a new route and approve its history",
                       StringComparison.OrdinalIgnoreCase),
                "Confirmed same-path replacement must retain the old tombstone, create a distinct disabled source, and explain the mandatory new route/history approval.");
            var refreshed = Enumerate(view).ToArray();
            Assert(refreshed.OfType<Label>().Any(label => label.Text == "REPLACED") &&
                   refreshed.OfType<Label>().Any(label => label.Text == "OFF") &&
                   refreshed.All(control =>
                       !control.Text.Contains(oldRoot, StringComparison.OrdinalIgnoreCase)),
                "Replacement completion must show both safe states without exposing the configured path.");
        }

        var candidateReplacement = new MutableInputSourceViewSource(source)
        {
            RelocateRequiresReplacement = true
        };
        string? candidateConfirmation = null;
        using (var view = CreateView(
                   candidateReplacement,
                   () => differentRoot,
                   (message, _) =>
                   {
                       candidateConfirmation = message;
                       return DialogResult.OK;
                   },
                   (_, _) => { }))
        {
            ClickConnections(view);
            Click(view, $"RelocateInputSource_{sourceId}");
            PumpUntil(
                () => candidateReplacement.RelocateCalls == 1 &&
                      candidateReplacement.ReplacementCalls == 1,
                "different-authority discovered-folder replacement");
            Assert(candidateReplacement.LastRelocationRoot == differentRoot &&
                   candidateReplacement.LastReplacementRoot == differentRoot &&
                   candidateReplacement.InspectInputSources().Sources.Count == 2 &&
                   candidateConfirmation is not null &&
                   candidateConfirmation.Contains(
                       "Existing routes and approved history remain bound",
                       StringComparison.Ordinal) &&
                   Enumerate(view).All(control =>
                       !control.Text.Contains(
                           differentRoot, StringComparison.OrdinalIgnoreCase)),
                "A different identity at the discovered path must require confirmation, then use the candidate-root replacement transaction without rendering the path.");
        }

        var unavailableDiscovery = new MutableInputSourceViewSource(source);
        var unavailableMessages = new List<string>();
        using (var view = CreateView(
                   unavailableDiscovery,
                   () => null,
                   (_, _) => DialogResult.Cancel,
                   (message, _) => unavailableMessages.Add(message)))
        {
            ClickConnections(view);
            Click(view, $"RelocateInputSource_{sourceId}");
            Application.DoEvents();
            Assert(unavailableDiscovery.RelocateCalls == 0 &&
                   unavailableDiscovery.ReplacementCalls == 0 &&
                   unavailableMessages.Single().Contains(
                       "No source was changed", StringComparison.Ordinal),
                "When no moved Xbox folder is discovered, recovery must fail closed with clear copy and no catalog operation.");
        }

        RoutesView CreateView(
            MutableInputSourceViewSource inputSources,
            Func<string?> discover,
            Func<string, string, DialogResult> confirmReplacement,
            Action<string, string> showMessage,
            Func<string, string, DialogResult>? confirmSkip = null)
        {
            var view = new RoutesView(
                manager,
                new FixedConnections(connectionId),
                isCutoverCommitted: () => true,
                layoutDpi: 96,
                inputSources: inputSources,
                confirmInputSourceReplacement: confirmReplacement,
                confirmInputSourceSkip: confirmSkip,
                discoverDefaultXboxRoot: discover,
                showInputSourceMessage: showMessage)
            {
                Size = viewport
            };
            LayoutHeadlessly(view);
            return view;
        }

        static void ClickConnections(RoutesView view)
        {
            Click(view, "ConnectionsRouteTab");
            LayoutHeadlessly(view);
        }

        static void Click(Control root, string name) =>
            Enumerate(root).OfType<Button>().Single(button => button.Name == name)
                .PerformClick();
    }

    private static void AssertXboxRouteEditorFlowAndLayout(int dpi, bool verifyDraft)
    {
        const string sourceId = "source.11111111222233334444555555555555";
        var sourceRoot = Path.Combine(
            "C:\\", "Users", "Tester", "OneDrive", "Videos", "Xbox Game DVR");
        var source = new RoutingInputSourceDisplay(
            sourceId,
            "Xbox captures · OneDrive",
            "Console captures synced through OneDrive",
            RoutingInputSourceKind.XboxGameDvrOneDrive,
            sourceRoot,
            Enabled: true,
            Available: true);
        var attentionSource = new RoutingInputSourceDisplay(
            "source.99999999aaaabbbbccccdddddddddddd",
            "Xbox captures · Previous OneDrive",
            "Console captures synced through OneDrive",
            RoutingInputSourceKind.XboxGameDvrOneDrive,
            Path.Combine("C:\\", "redacted", "must-not-render"),
            Enabled: false,
            Available: false,
            Revision: 7,
            Health: RoutingInputSourceHealth.NeedsAttention);
        var preflight = new XboxRouteEditorPreflightProbe();
        string? confirmationMessage = null;
        string? confirmationCaption = null;
        var confirmationCalls = 0;
        using var dialog = new RouteEditorDialog(
            connections: [],
            layoutDpi: dpi,
            inputSources: [source, attentionSource],
            xboxPreflight: preflight.Preflight,
            xboxPreflightTaskRunner: RunXboxPreflightInline,
            xboxHistoryConfirmation: (message, caption) =>
            {
                confirmationCalls++;
                confirmationMessage = message;
                confirmationCaption = caption;
                return DialogResult.Yes;
            },
            saveGuardMessage: FailUnexpectedSaveGuard);

        // Create and lay out handles without ever showing a top-level window.
        LayoutHeadlessly(dialog);
        var controls = Enumerate(dialog).ToArray();
        var watchedFolder = controls.OfType<RadioButton>().Single(control =>
            control.Name == "RouteTriggerWatchedFolder");
        var sourceSelector = controls.OfType<ComboBox>().Single(control =>
            control.Name == "RouteWatchedSourceSelector");
        var sourceConfiguration = controls.Single(control =>
            control.Name == "RouteSourceConfiguration");
        var historyConfiguration = controls.Single(control =>
            control.Name == "RouteXboxHistoryConfiguration");
        var historyOptions = new[]
        {
            controls.OfType<RadioButton>().Single(control =>
                control.Name == "RouteXboxHistoryNewOnly"),
            controls.OfType<RadioButton>().Single(control =>
                control.Name == "RouteXboxHistoryLastDay"),
            controls.OfType<RadioButton>().Single(control =>
                control.Name == "RouteXboxHistoryLastWeek")
        };
        var preflightStatus = controls.OfType<Label>().Single(control =>
            control.Name == "RouteXboxPreflightStatus");
        var summaryWhen = controls.OfType<Label>().Single(control =>
            control.Name == "RouteSummaryWhenValue");
        var summaryIf = controls.OfType<Label>().Single(control =>
            control.Name == "RouteSummaryIfValue");
        var fields = System.Reflection.BindingFlags.Instance |
                     System.Reflection.BindingFlags.NonPublic;
        var originalOutput = (RadioButton)typeof(RouteEditorDialog)
            .GetField("_original", fields)!.GetValue(dialog)!;
        var landscapeOutput = (RadioButton)typeof(RouteEditorDialog)
            .GetField("_landscape", fields)!.GetValue(dialog)!;
        var portraitOutput = (RadioButton)typeof(RouteEditorDialog)
            .GetField("_portrait", fields)!.GetValue(dialog)!;

        Assert(sourceSelector.Items.Count == 3 &&
               sourceSelector.Items[0]!.ToString() ==
               "Current migrated folder · SteelSeries/NVIDIA" &&
               sourceSelector.Items[1]!.ToString() == source.Name &&
               sourceSelector.Items[2]!.ToString()!.Contains(
                   "Needs attention", StringComparison.Ordinal),
            "External watched folder must offer the configured default and named Xbox source while explicitly retaining an unhealthy source for recovery context.");
        Assert(!IsLocallyVisible(sourceConfiguration) &&
               !IsLocallyVisible(historyConfiguration) &&
               historyOptions.All(control => !control.TabStop),
            "Source and HISTORY configuration must begin absent from the Instant Replay flow and tab order.");

        watchedFolder.Checked = true;
        LayoutHeadlessly(dialog);
        Assert(IsLocallyVisible(sourceConfiguration) && sourceSelector.TabStop &&
               !IsLocallyVisible(historyConfiguration) &&
               historyOptions.All(control => !control.TabStop),
            "External watched folder must reveal source selection while the default source keeps Xbox HISTORY hidden and untabbable.");

        sourceSelector.SelectedIndex = 2;
        Assert(sourceSelector.SelectedIndex == 0 &&
               sourceSelector.AccessibleDescription is { } unavailableDescription &&
               unavailableDescription.Contains("needs attention", StringComparison.OrdinalIgnoreCase),
            "A Needs attention Xbox source must remain visible but unselectable for new route creation, with an accessible recovery instruction.");

        sourceSelector.SelectedIndex = 1;
        SettleXboxPreflight(
            dialog,
            () => preflight.Calls.Any(call =>
                      call.Policy.Window == XboxDvrHistoryWindow.NewOnly) &&
                  preflightStatus.Text.Contains("clips found", StringComparison.Ordinal),
            $"Xbox From now metadata preview at {dpi} DPI");
        LayoutHeadlessly(dialog);
        Assert(IsLocallyVisible(historyConfiguration) &&
               historyConfiguration.TabStop &&
               historyOptions.All(control => control.TabStop) &&
               historyOptions.Count(control => control.Checked) == 1 &&
               historyOptions[0].Checked,
            "Selecting the named Xbox source must reveal exactly three keyboard-reachable HISTORY choices with From now selected.");
        Assert(summaryWhen.Text == "Xbox · OneDrive" &&
               summaryIf.Text == "Any game · From now",
            "The route summary must identify Xbox OneDrive and its selected history window without exposing a path.");
        Assert(originalOutput.Checked && originalOutput.Enabled &&
               !landscapeOutput.Enabled && !landscapeOutput.TabStop &&
               !portraitOutput.Enabled && !portraitOutput.TabStop,
            "Xbox routes must use the original console clip and remove unavailable ClipCord reaction-camera renditions from keyboard selection.");
        var safetyText = string.Join(
            " ",
            Enumerate(controls.Single(control => control.Name == "RouteSourceSafetyNote"))
                .OfType<Label>()
                .Select(label => label.Text));
        Assert(safetyText.Contains("OneDrive original stays in place", StringComparison.Ordinal) &&
               safetyText.Contains("reads metadata only", StringComparison.Ordinal),
            "Xbox source safety copy must promise non-destructive import and metadata-only preview.");
        Assert(controls.All(control =>
                   !control.Text.Contains(sourceRoot, StringComparison.OrdinalIgnoreCase) &&
                   !(control.AccessibleName?.Contains(
                       sourceRoot, StringComparison.OrdinalIgnoreCase) ?? false)) &&
               sourceSelector.Items.Cast<object>().All(item =>
                   !item.ToString()!.Contains(sourceRoot, StringComparison.OrdinalIgnoreCase)),
            "The route editor must keep the canonical OneDrive path out of visible and accessible summary strings.");

        var preflightMethod = typeof(XboxRouteEditorPreflightProbe).GetMethod(
            nameof(XboxRouteEditorPreflightProbe.Preflight),
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!;
        Assert(preflightMethod.GetParameters().Select(parameter => parameter.ParameterType)
                   .SequenceEqual(
                   [
                       typeof(RoutingInputSourceDisplay),
                       typeof(XboxDvrHistoryPolicy),
                       typeof(CancellationToken)
                   ]) &&
               preflightMethod.GetParameters().All(parameter =>
                   !typeof(Stream).IsAssignableFrom(parameter.ParameterType)),
            "The injected route preflight seam must expose source metadata, history policy, and cancellation only—never clip content.");

        var game = controls.OfType<TextBox>().Single(control =>
            control.Name == "RouteGameConditionEditor");
        var preflightCallsBeforeDaySelection = preflight.Calls.Count;
        historyOptions[1].Checked = true;
        SettleXboxPreflight(
            dialog,
            () => preflight.Calls.LastOrDefault()?.Policy.Window ==
                      XboxDvrHistoryWindow.Last24Hours &&
                  preflightStatus.Text.Contains("3 match", StringComparison.Ordinal) &&
                  preflightStatus.Text.Contains("about 25 MB", StringComparison.Ordinal),
            $"Xbox Last 24 hours metadata preview at {dpi} DPI");
        Assert(preflight.Calls.Count == preflightCallsBeforeDaySelection + 1,
            "Selecting one Xbox history option must launch exactly one metadata preview; unchecking the previous radio option must not launch a redundant canceled preview.");
        var dayCall = preflight.Calls.Last(call =>
            call.Policy.Window == XboxDvrHistoryWindow.Last24Hours);
        Assert(dayCall.SourceId == sourceId &&
               dayCall.CanonicalRoot == sourceRoot &&
               dayCall.EligibleHistoricalCount == 3 &&
               dayCall.EligibleHistoricalBytes == 25L * 1024 * 1024,
            "The metadata-only preflight fake must receive the exact source identity, canonical root, history choice, count, and logical size.");

        PumpMessagesFor(TimeSpan.FromMilliseconds(30));
        var preflightCallsBeforeGameFilter = preflight.Calls.Count;
        game.Text = "BATTLEFIELD 6";
        Application.DoEvents();
        Assert(preflightStatus.Text.Contains("2 match", StringComparison.Ordinal) &&
               preflightStatus.Text.Contains("about 10 MB", StringComparison.Ordinal) &&
               preflight.Calls.Count == preflightCallsBeforeGameFilter,
            "Typing an exact game must re-count the cached metadata case-insensitively without opening content or running preflight again.");
        game.Text = "Battlefield 6";
        Application.DoEvents();
        Assert(preflight.Calls.Count == preflightCallsBeforeGameFilter,
            "Changing only game-name casing must remain a local cached-metadata operation.");

        LayoutHeadlessly(dialog);
        AssertXboxEditorGeometry(dialog, dpi, source);
        AssertXboxHistoryLimitBlocksSave(source, dpi);
        if (!verifyDraft) return;

        var preflightCallsBeforeWeekSelection = preflight.Calls.Count;
        historyOptions[2].Checked = true;
        SettleXboxPreflight(
            dialog,
            () => preflight.Calls.LastOrDefault()?.Policy.Window ==
                  XboxDvrHistoryWindow.Last7Days,
            "Xbox Last 7 days metadata preview");
        Assert(preflight.Calls.Count == preflightCallsBeforeWeekSelection + 1,
            "Changing to one Xbox history option must launch exactly one metadata preview.");
        var weekCall = preflight.Calls.Last(call =>
            call.Policy.Window == XboxDvrHistoryWindow.Last7Days);
        var newOnlyCall = preflight.Calls.First(call =>
            call.Policy.Window == XboxDvrHistoryWindow.NewOnly);
        Assert(newOnlyCall.Policy.HistoricalCutoffUtc ==
                   newOnlyCall.Policy.ActivationUtc &&
               dayCall.Policy.ActivationUtc == newOnlyCall.Policy.ActivationUtc &&
               dayCall.Policy.HistoricalCutoffUtc ==
                   newOnlyCall.Policy.ActivationUtc.AddHours(-24) &&
               weekCall.Policy.ActivationUtc == newOnlyCall.Policy.ActivationUtc &&
               weekCall.Policy.HistoricalCutoffUtc ==
                   newOnlyCall.Policy.ActivationUtc.AddDays(-7),
            "From now, Last 24 hours, and Last 7 days must freeze absolute cutoffs from one route-editor activation instant.");

        sourceSelector.SelectedIndex = 0;
        LayoutHeadlessly(dialog);
        Assert(IsLocallyVisible(sourceConfiguration) &&
               !IsLocallyVisible(historyConfiguration) &&
               !historyConfiguration.TabStop &&
               historyOptions.All(control => !control.TabStop) &&
               landscapeOutput.Enabled && portraitOutput.Enabled &&
               summaryWhen.Text == "Migrated watched folder" &&
               summaryIf.Text == "Game · Battlefield 6",
            "Switching back to the default watched folder must remove HISTORY from view and tab order and clear Xbox summary semantics.");

        sourceSelector.SelectedIndex = 1;
        historyOptions[1].Checked = true;
        SettleXboxPreflight(
            dialog,
            () => preflight.Calls.LastOrDefault() is
                  {
                      Policy: { Window: XboxDvrHistoryWindow.Last24Hours },
                      EligibleHistoricalCount: 3
                  } &&
                  preflightStatus.Text.Contains("2 match", StringComparison.Ordinal) &&
                  preflightStatus.Text.Contains("about 10 MB", StringComparison.Ordinal),
            "Xbox fixed-cutoff save preview");
        var previewSaveCall = preflight.Calls.Last();
        controls.OfType<TextBox>().Single(control =>
            control.Name == "RouteNameEditor").Text = "Xbox Battlefield route";
        Assert(summaryWhen.Text == "Xbox · OneDrive" &&
               summaryIf.Text == "Battlefield 6 · Last 24h",
            "The final route summary must pair its Xbox source, game, and selected history policy.");

        var preflightCallsBeforeSave = preflight.Calls.Count;
        InvokeSaveDraft(dialog);
        var draft = dialog.Draft;
        var saveCall = preflight.Calls
            .Skip(preflightCallsBeforeSave)
            .SingleOrDefault(call => draft?.XboxHistorySelection is { } history &&
                                     call.Policy.ActivationUtc == history.ActivationUtc);
        Assert(saveCall is not null && draft is
               {
                   Trigger: RoutingTriggerKind.WatchedFolder,
                   Game: "Battlefield 6",
                   Output: RoutingOutputKind.Original,
                   WatchedSourceId: sourceId,
                   EarliestCapturedUtc: var cutoff,
                   XboxHistorySelection:
                   {
                       ActivationUtc: var activation,
                       HistoricalOccurrences: var occurrences
                   }
               } &&
               cutoff == saveCall.Policy.HistoricalCutoffUtc &&
               cutoff == saveCall.Policy.ActivationUtc.AddHours(-24) &&
               activation == saveCall.Policy.ActivationUtc &&
               saveCall.Policy.ActivationUtc >= previewSaveCall.Policy.ActivationUtc &&
               occurrences.SequenceEqual(
               [
                   new RoutingXboxHistoricalOccurrence(
                       "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                       "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                   new RoutingXboxHistoricalOccurrence(
                       "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                       "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
               ]),
            "Saving the Xbox route must persist its exact source, activation, fixed cutoff, and canonically sorted game-filtered occurrence/revision set.");
        Assert(confirmationCalls == 1 &&
               confirmationCaption == "Import Xbox clip history?" &&
               confirmationMessage is not null &&
               confirmationMessage.Contains(
                   "retrieve 2 existing Xbox clips currently found (10 MB)", StringComparison.Ordinal) &&
               confirmationMessage.Contains(
                   "Only this displayed historical set is approved", StringComparison.Ordinal) &&
               confirmationMessage.Contains(
                   "captured after activation continue automatically", StringComparison.Ordinal) &&
               !confirmationMessage.Contains("25 MB", StringComparison.Ordinal),
            "The save confirmation must use the same game-filtered cached count and bytes shown in preflight and explain the frozen reviewed set.");
        AssertXboxNewOnlyDurableSelection(source);
        AssertXboxSaveTimeActivationBoundary(source);
        AssertXboxGameFilterPrecedesHistorySelectionCap(source);
        Application.DoEvents();
    }

    private static void AssertXboxNewOnlyDurableSelection(
        RoutingInputSourceDisplay source)
    {
        var preflight = new XboxRouteEditorPreflightProbe();
        var confirmationCalls = 0;
        using var dialog = new RouteEditorDialog(
            connections: [],
            layoutDpi: 96,
            inputSources: [source],
            xboxPreflight: preflight.Preflight,
            xboxPreflightTaskRunner: RunXboxPreflightInline,
            xboxHistoryConfirmation: (_, _) =>
            {
                confirmationCalls++;
                return DialogResult.No;
            },
            saveGuardMessage: FailUnexpectedSaveGuard);
        LayoutHeadlessly(dialog);
        var controls = Enumerate(dialog).ToArray();
        controls.OfType<RadioButton>().Single(control =>
            control.Name == "RouteTriggerWatchedFolder").Checked = true;
        controls.OfType<ComboBox>().Single(control =>
            control.Name == "RouteWatchedSourceSelector").SelectedIndex = 1;
        var preflightStatus = controls.OfType<Label>().Single(control =>
            control.Name == "RouteXboxPreflightStatus");
        SettleXboxPreflight(
            dialog,
            () => preflight.Calls.LastOrDefault()?.Policy.Window ==
                      XboxDvrHistoryWindow.NewOnly &&
                  preflightStatus.Text.Contains("clips found", StringComparison.Ordinal),
            "Xbox From now durable selection preview");
        controls.OfType<TextBox>().Single(control =>
            control.Name == "RouteNameEditor").Text = "Xbox new clips only";
        var previewCall = preflight.Calls.Last();

        var preflightCallsBeforeSave = preflight.Calls.Count;
        InvokeSaveDraft(dialog);
        var draft = dialog.Draft;
        var saveCall = preflight.Calls
            .Skip(preflightCallsBeforeSave)
            .SingleOrDefault(call => draft?.XboxHistorySelection is { } history &&
                                     call.Policy.ActivationUtc == history.ActivationUtc);

        Assert(saveCall is not null && draft is
               {
                   WatchedSourceId: var sourceId,
                   EarliestCapturedUtc: var cutoff,
                   XboxHistorySelection:
                   {
                       ActivationUtc: var activation,
                       HistoricalOccurrences.Count: 0
                   }
               } &&
               sourceId == source.SourceId &&
               cutoff == saveCall.Policy.ActivationUtc &&
               activation == saveCall.Policy.ActivationUtc &&
               activation >= previewCall.Policy.ActivationUtc &&
               confirmationCalls == 0,
            "From now must persist the fixed activation boundary with an explicitly empty historical occurrence set and require no history confirmation.");
    }

    private static void AssertXboxHistoryLimitBlocksSave(
        RoutingInputSourceDisplay source,
        int dpi)
    {
        var activationUtc = new DateTimeOffset(
            2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var preflight = new XboxRouteEditorPreflightProbe
        {
            WindowMatchCountOverride =
                XboxDvrHistoryPolicy.MaximumAllowedHistoricalClips + 1
        };
        var confirmationCalls = 0;
        string? confirmationMessage = null;
        using var dialog = new RouteEditorDialog(
            connections: [],
            layoutDpi: dpi,
            inputSources: [source],
            xboxPreflight: preflight.Preflight,
            xboxPreflightTaskRunner: RunXboxPreflightInline,
            xboxHistoryConfirmation: (message, _) =>
            {
                confirmationCalls++;
                confirmationMessage = message;
                return DialogResult.Yes;
            },
            clock: () => activationUtc,
            saveGuardMessage: FailUnexpectedSaveGuard);
        LayoutHeadlessly(dialog);
        var controls = Enumerate(dialog).ToArray();
        controls.OfType<RadioButton>().Single(control =>
            control.Name == "RouteTriggerWatchedFolder").Checked = true;
        controls.OfType<ComboBox>().Single(control =>
            control.Name == "RouteWatchedSourceSelector").SelectedIndex = 1;
        controls.OfType<RadioButton>().Single(control =>
            control.Name == "RouteXboxHistoryLastWeek").Checked = true;
        var status = controls.OfType<Label>().Single(control =>
            control.Name == "RouteXboxPreflightStatus");
        SettleXboxPreflight(
            dialog,
            () => preflight.Calls.LastOrDefault()?.Policy.Window ==
                      XboxDvrHistoryWindow.Last7Days &&
                  status.Text.Contains(
                      "shorten history before saving", StringComparison.Ordinal),
            "Xbox over-limit metadata preview");
        LayoutHeadlessly(dialog);
        AssertXboxEditorGeometry(dialog, dpi, source);
        controls.OfType<TextBox>().Single(control =>
            control.Name == "RouteNameEditor").Text = "Xbox history over limit";

        InvokeSaveDraft(dialog);

        Assert(dialog.Draft is null && confirmationCalls == 0 &&
               status.Text.Contains(
                   $"maximum {XboxDvrHistoryPolicy.MaximumAllowedHistoricalClips:N0}",
                   StringComparison.Ordinal),
            "A blank-game history window above the global match limit must fail closed before confirmation or draft creation and clearly require a shorter window.");
        if (dpi != 96) return;
        dialog.Dispose();

        var filteredPreflight = new XboxRouteEditorPreflightProbe
        {
            WindowMatchCountOverride =
                XboxDvrHistoryPolicy.MaximumAllowedHistoricalClips + 1
        };
        var filteredConfirmationCalls = 0;
        string? filteredConfirmationMessage = null;
        using var filteredDialog = new RouteEditorDialog(
            connections: [],
            layoutDpi: dpi,
            inputSources: [source],
            xboxPreflight: filteredPreflight.Preflight,
            xboxPreflightTaskRunner: RunXboxPreflightInline,
            xboxHistoryConfirmation: (message, _) =>
            {
                filteredConfirmationCalls++;
                filteredConfirmationMessage = message;
                return DialogResult.Yes;
            },
            clock: () => activationUtc,
            saveGuardMessage: FailUnexpectedSaveGuard);
        LayoutHeadlessly(filteredDialog);
        var filteredControls = Enumerate(filteredDialog).ToArray();
        filteredControls.OfType<TextBox>().Single(control =>
            control.Name == "RouteNameEditor").Text = "Xbox Battlefield history";
        filteredControls.OfType<TextBox>().Single(control =>
            control.Name == "RouteGameConditionEditor").Text = "Battlefield 6";
        filteredControls.OfType<RadioButton>().Single(control =>
            control.Name == "RouteTriggerWatchedFolder").Checked = true;
        filteredControls.OfType<ComboBox>().Single(control =>
            control.Name == "RouteWatchedSourceSelector").SelectedIndex = 1;
        filteredControls.OfType<RadioButton>().Single(control =>
            control.Name == "RouteXboxHistoryLastWeek").Checked = true;
        var filteredStatus = filteredControls.OfType<Label>().Single(control =>
            control.Name == "RouteXboxPreflightStatus");
        SettleXboxPreflight(
            filteredDialog,
            () => filteredPreflight.Calls.LastOrDefault()?.Policy.Window ==
                      XboxDvrHistoryWindow.Last7Days &&
                  filteredStatus.Text.Contains("2 match", StringComparison.Ordinal) &&
                  filteredStatus.Text.Contains("about 10 MB", StringComparison.Ordinal),
            "Xbox game-filtered over-limit preview");
        Assert(!filteredStatus.Text.Contains(
                   "shorten history before saving", StringComparison.Ordinal),
            "An exact Xbox game condition must narrow the raw over-limit window to its two matching metadata items.");

        InvokeSaveDraft(filteredDialog);

        Assert(filteredDialog.Draft is
               {
                   Game: "Battlefield 6",
                   EarliestCapturedUtc: var cutoff,
                   XboxHistorySelection:
                   {
                       ActivationUtc: var activation,
                       HistoricalOccurrences: var occurrences
                   }
               } &&
               activation == activationUtc &&
               cutoff == activationUtc.AddDays(-7) &&
               occurrences.SequenceEqual(
               [
                   new RoutingXboxHistoricalOccurrence(
                       "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                       "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                   new RoutingXboxHistoricalOccurrence(
                       "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                       "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
               ]) &&
               filteredConfirmationCalls == 1 &&
               filteredConfirmationMessage is not null &&
               filteredConfirmationMessage.Contains(
                   "retrieve 2 existing Xbox clips currently found (10 MB)",
                   StringComparison.Ordinal) &&
               !filteredConfirmationMessage.Contains("25 MB", StringComparison.Ordinal),
            "The same raw over-limit preview must save when an exact game narrows it to two items, confirm only those two, and freeze their exact occurrence/revision pairs.");
    }

    private static void AssertXboxGameFilterPrecedesHistorySelectionCap(
        RoutingInputSourceDisplay source)
    {
        var activationUtc = new DateTimeOffset(
            2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var battlefieldOccurrenceA = new string('a', 64);
        var battlefieldRevisionA = new string('b', 64);
        var battlefieldOccurrenceB = new string('c', 64);
        var battlefieldRevisionB = new string('d', 64);
        var calls = 0;
        string? confirmation = null;

        XboxDvrPreflightPreview Preflight(
            RoutingInputSourceDisplay selected,
            XboxDvrHistoryPolicy policy,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref calls);
            var items = new List<XboxDvrPreflightItem>(
                XboxDvrHistoryPolicy.MaximumAllowedHistoricalClips + 2);
            for (var index = 0;
                 index < XboxDvrHistoryPolicy.MaximumAllowedHistoricalClips;
                 index++)
            {
                var capturedUtc = policy.ActivationUtc.AddMinutes(-(index + 1));
                items.Add(new XboxDvrPreflightItem(
                    $"Another Game-{capturedUtc:yyyy_MM_dd-HH-mm-ss}.mp4",
                    "Another Game",
                    capturedUtc,
                    1024 * 1024,
                    (index + 1).ToString("x64"),
                    (index + 2001).ToString("x64"),
                    XboxDvrReparseClassification.CloudPlaceholder,
                    XboxDvrPreflightDisposition.EligibleHistorical,
                    XboxDvrNeedsAttentionReason.None));
            }
            items.Add(new XboxDvrPreflightItem(
                "Battlefield™ 6-2026_08_31-16-00-00.mp4",
                "Battlefield™ 6",
                policy.ActivationUtc.AddHours(-20),
                4L * 1024 * 1024,
                battlefieldOccurrenceA,
                battlefieldRevisionA,
                XboxDvrReparseClassification.CloudPlaceholder,
                XboxDvrPreflightDisposition.BaselineOnly,
                XboxDvrNeedsAttentionReason.None));
            items.Add(new XboxDvrPreflightItem(
                "Battlefield 6-2026_08_31-15-00-00.mp4",
                "Battlefield 6",
                policy.ActivationUtc.AddHours(-21),
                6L * 1024 * 1024,
                battlefieldOccurrenceB,
                battlefieldRevisionB,
                XboxDvrReparseClassification.CloudPlaceholder,
                XboxDvrPreflightDisposition.BaselineOnly,
                XboxDvrNeedsAttentionReason.None));

            return new XboxDvrPreflightPreview(
                selected.SourceId,
                selected.CanonicalRoot,
                policy,
                TotalClipCount: items.Count,
                TotalLogicalBytes: 1010L * 1024 * 1024,
                ParsedClipCount: items.Count,
                WindowMatchCount: items.Count,
                EligibleHistoricalCount: XboxDvrHistoryPolicy.MaximumAllowedHistoricalClips,
                EligibleHistoricalBytes:
                    XboxDvrHistoryPolicy.MaximumAllowedHistoricalClips * 1024L * 1024,
                BaselineOnlyCount: 2,
                NeedsAttentionCount: 0,
                Items: items);
        }

        using var dialog = new RouteEditorDialog(
            connections: [],
            layoutDpi: 96,
            inputSources: [source],
            xboxPreflight: Preflight,
            xboxPreflightTaskRunner: RunXboxPreflightInline,
            xboxHistoryConfirmation: (message, _) =>
            {
                confirmation = message;
                return DialogResult.Yes;
            },
            clock: () => activationUtc,
            saveGuardMessage: FailUnexpectedSaveGuard);
        LayoutHeadlessly(dialog);
        var controls = Enumerate(dialog).ToArray();
        controls.OfType<TextBox>().Single(control =>
            control.Name == "RouteNameEditor").Text = "Battlefield below raw cap";
        controls.OfType<TextBox>().Single(control =>
            control.Name == "RouteGameConditionEditor").Text = "Battlefield 6";
        controls.OfType<RadioButton>().Single(control =>
            control.Name == "RouteTriggerWatchedFolder").Checked = true;
        controls.OfType<ComboBox>().Single(control =>
            control.Name == "RouteWatchedSourceSelector").SelectedIndex = 1;
        controls.OfType<RadioButton>().Single(control =>
            control.Name == "RouteXboxHistoryLastDay").Checked = true;
        var status = controls.OfType<Label>().Single(control =>
            control.Name == "RouteXboxPreflightStatus");
        SettleXboxPreflight(
            dialog,
            () => Volatile.Read(ref calls) >= 1 &&
                  status.Text.Contains("2 match", StringComparison.Ordinal) &&
                  status.Text.Contains("about 10 MB", StringComparison.Ordinal),
            "Xbox game filtering before the 1,000-item history selection cap");
        Assert(!status.Text.Contains(
                   "shorten history before saving", StringComparison.Ordinal),
            "The raw 1,002-item window must not block a route whose exact game filter matches only two in-window items outside the preselected 1,000.");

        var callsBeforeSave = Volatile.Read(ref calls);
        InvokeSaveDraft(dialog);

        Assert(Volatile.Read(ref calls) == callsBeforeSave + 1 && dialog.Draft is
               {
                   Game: "Battlefield 6",
                   XboxHistorySelection.HistoricalOccurrences: var occurrences
               } &&
               occurrences.SequenceEqual(
               [
                   new RoutingXboxHistoricalOccurrence(
                       battlefieldOccurrenceA, battlefieldRevisionA),
                   new RoutingXboxHistoricalOccurrence(
                       battlefieldOccurrenceB, battlefieldRevisionB)
               ]) &&
               confirmation is not null &&
               confirmation.Contains(
                   "retrieve 2 existing Xbox clips currently found (10 MB)",
                   StringComparison.Ordinal) &&
               !confirmation.Contains("1,000", StringComparison.Ordinal) &&
               !confirmation.Contains("1,002", StringComparison.Ordinal),
            "Game filtering must run across all in-window metadata before the 1,000-item historical selection cap, then freeze and confirm exactly the two matching occurrence/revision pairs.");
    }

    private static void AssertXboxSaveTimeActivationBoundary(
        RoutingInputSourceDisplay source)
    {
        var openedUtc = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var savedUtc = openedUtc.AddMinutes(5);
        var betweenUtc = openedUtc.AddMinutes(2);
        var clockCalls = 0;
        DateTimeOffset Clock() => Interlocked.Increment(ref clockCalls) == 1
            ? openedUtc
            : savedUtc;
        var occurrence = new string('7', 64);
        var revision = new string('8', 64);
        string? confirmation = null;
        XboxDvrPreflightPreview Preflight(
            RoutingInputSourceDisplay selected,
            XboxDvrHistoryPolicy policy,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var eligible = betweenUtc <= policy.ActivationUtc;
            var item = new XboxDvrPreflightItem(
                "Battlefield™ 6-2026_09_01-12-02-00.mp4",
                "Battlefield™ 6",
                betweenUtc,
                5L * 1024 * 1024,
                occurrence,
                revision,
                XboxDvrReparseClassification.CloudPlaceholder,
                eligible
                    ? XboxDvrPreflightDisposition.EligibleHistorical
                    : XboxDvrPreflightDisposition.NeedsAttention,
                eligible
                    ? XboxDvrNeedsAttentionReason.None
                    : XboxDvrNeedsAttentionReason.CaptureTimeAfterActivation);
            return new XboxDvrPreflightPreview(
                selected.SourceId,
                selected.CanonicalRoot,
                policy,
                TotalClipCount: 1,
                TotalLogicalBytes: item.LogicalBytes,
                ParsedClipCount: 1,
                WindowMatchCount: eligible ? 1 : 0,
                EligibleHistoricalCount: eligible ? 1 : 0,
                EligibleHistoricalBytes: eligible ? item.LogicalBytes : 0,
                BaselineOnlyCount: 0,
                NeedsAttentionCount: eligible ? 0 : 1,
                [item]);
        }

        using var dialog = new RouteEditorDialog(
            connections: [],
            layoutDpi: 96,
            inputSources: [source],
            xboxPreflight: Preflight,
            xboxPreflightTaskRunner: RunXboxPreflightInline,
            xboxHistoryConfirmation: (message, _) =>
            {
                confirmation = message;
                return DialogResult.Yes;
            },
            clock: Clock,
            saveGuardMessage: FailUnexpectedSaveGuard);
        LayoutHeadlessly(dialog);
        var controls = Enumerate(dialog).ToArray();
        controls.OfType<RadioButton>().Single(control =>
            control.Name == "RouteTriggerWatchedFolder").Checked = true;
        controls.OfType<ComboBox>().Single(control =>
            control.Name == "RouteWatchedSourceSelector").SelectedIndex = 1;
        controls.OfType<RadioButton>().Single(control =>
            control.Name == "RouteXboxHistoryLastDay").Checked = true;
        var status = controls.OfType<Label>().Single(control =>
            control.Name == "RouteXboxPreflightStatus");
        SettleXboxPreflight(
            dialog,
            () => status.Text.Contains("0 match", StringComparison.Ordinal) &&
                  status.Text.Contains("skipped", StringComparison.Ordinal),
            "Xbox preview before final activation snapshot");
        controls.OfType<TextBox>().Single(control =>
            control.Name == "RouteNameEditor").Text = "Save-time boundary";

        InvokeSaveDraft(dialog);

        Assert(dialog.Draft?.XboxHistorySelection is
               {
                   ActivationUtc: var activation,
                   HistoricalOccurrences:
                   [{ OccurrenceId: var selectedOccurrence, RevisionId: var selectedRevision }]
               } &&
               activation == savedUtc &&
               selectedOccurrence == occurrence &&
               selectedRevision == revision &&
               confirmation is not null &&
               confirmation.Contains("retrieve 1 existing Xbox clip", StringComparison.Ordinal),
            "Save must take a fresh metadata-only snapshot and confirm a clip captured between editor-open and activation before runtime can admit it.");
    }

    private static void InvokeSaveDraft(RouteEditorDialog dialog)
    {
        var saveDraft = typeof(RouteEditorDialog).GetMethod(
            "SaveDraft",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!;
        try
        {
            _ = saveDraft.Invoke(dialog, null);
        }
        catch (System.Reflection.TargetInvocationException exception)
        {
            throw exception.InnerException ?? exception;
        }
    }

    private static void SettleXboxPreflight(
        RouteEditorDialog dialog,
        Func<bool> expectedState,
        string description)
    {
        var preflightStatus = Enumerate(dialog).OfType<Label>().Single(control =>
            control.Name == "RouteXboxPreflightStatus");
        PumpUntil(
            () => dialog.XboxPreflightCompletion.IsCompleted &&
                  dialog.XboxPreflightReadyForCurrentSelection &&
                  expectedState(),
            description,
            () =>
                $"completion={dialog.XboxPreflightCompletion.Status}; " +
                $"ready={dialog.XboxPreflightReadyForCurrentSelection}; " +
                $"generation={dialog.XboxPreflightGeneration}; " +
                $"synchronizationContext={SynchronizationContext.Current?.GetType().Name ?? "none"}; " +
                $"status={preflightStatus.Text}; " +
                dialog.XboxPreflightReadinessDiagnostic);
        Assert(dialog.XboxPreflightCompletion.IsCompleted &&
               dialog.XboxPreflightReadyForCurrentSelection,
            $"{description} must not proceed while an Xbox metadata preview is still running.");
    }

    private static DialogResult FailUnexpectedSaveGuard(
        string message,
        string caption,
        MessageBoxIcon icon) =>
        throw new InvalidOperationException(
            $"An unexpected route save guard attempted to open: {caption} ({icon}) — {message}");

    private static Task<XboxDvrPreflightPreview> RunXboxPreflightInline(
        Func<XboxDvrPreflightPreview> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(operation());
    }

    private static void AssertXboxEditorGeometry(
        RouteEditorDialog dialog,
        int dpi,
        RoutingInputSourceDisplay source)
    {
        SettleXboxPreflight(
            dialog,
            () => true,
            $"Xbox metadata preview before {dpi} DPI geometry assertions");
        LayoutHeadlessly(dialog);
        var controls = Enumerate(dialog).ToArray();
        var history = controls.Single(control =>
            control.Name == "RouteXboxHistoryConfiguration");
        var stage = controls.OfType<BrandedScrollHost>().Single(control =>
            control.Name == "RouteEditorStageHost");
        Assert(IsLocallyVisible(history) && stage.Content is not null &&
               stage.Content.Width > 0 && stage.Content.Height > 0 &&
               (!stage.HasOverflow || stage.Content.Height > stage.ClientSize.Height),
            $"Xbox route content must stay inside the branded scroll model at {dpi} DPI without compression or a native scrollbar.");
        AssertLocallyVisibleDescendantsContained(dialog, dpi);
        AssertLocallyVisibleSiblingGeometry(dialog, dpi);
        AssertRouteEditorText(controls, dpi);

        var historyTitleNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "RouteXboxHistoryNewOnlyTitle",
            "RouteXboxHistoryLastDayTitle",
            "RouteXboxHistoryLastWeekTitle"
        };
        foreach (var title in controls.OfType<Label>().Where(label =>
                     historyTitleNames.Contains(label.Name)))
        {
            var titleMeasured = TextRenderer.MeasureText(
                title.Text,
                title.Font,
                Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            var titlePredicted = PredictAtDpi(titleMeasured, title.DeviceDpi, dpi);
            Assert(title.Width + RouteEditorDialog.ScaleLogicalMetric(3, dpi) >=
                       titlePredicted.Width &&
                   title.Height + RouteEditorDialog.ScaleLogicalMetric(3, dpi) >=
                       titlePredicted.Height,
                $"{title.Name} must fit without ellipsis in the visible Xbox HISTORY state at {dpi} DPI; predicted={titlePredicted}, bounds={title.ClientSize}.");
        }

        var status = controls.OfType<Label>().Single(control =>
            control.Name == "RouteXboxPreflightStatus");
        var measured = TextRenderer.MeasureText(
            status.Text,
            status.Font,
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        var predicted = PredictAtDpi(measured, status.DeviceDpi, dpi);
        var previewLayout = status.Parent as TableLayoutPanel;
        var iconColumnWidth = RouteEditorDialog.ScaleLogicalMetric(28, dpi);
        var allocated = previewLayout is null
            ? Size.Empty
            : new Size(
                Math.Max(0, previewLayout.ClientSize.Width - iconColumnWidth),
                previewLayout.ClientSize.Height);
        Assert(previewLayout is
               {
                   ColumnCount: 2,
                   ColumnStyles.Count: 2
               } &&
               previewLayout.ColumnStyles[0].SizeType == SizeType.Absolute &&
               Math.Abs(previewLayout.ColumnStyles[0].Width - iconColumnWidth) < 0.01f &&
               previewLayout.ColumnStyles[1].SizeType == SizeType.Percent &&
               status.Dock == DockStyle.Fill &&
               allocated.Width + RouteEditorDialog.ScaleLogicalMetric(3, dpi) >= predicted.Width &&
               allocated.Height + RouteEditorDialog.ScaleLogicalMetric(3, dpi) >= predicted.Height,
            $"The settled Xbox count/size preview must fill a deterministic table-layout allocation without ellipsis at {dpi} DPI; predicted={predicted}, allocated={allocated}, leaf={status.ClientSize}.");

        var selector = controls.OfType<ComboBox>().Single(control =>
            control.Name == "RouteWatchedSourceSelector");
        var titleFont = ClipCordTheme.InterfaceFont(8.75f, FontStyle.Bold);
        var detailFont = ClipCordTheme.InterfaceFont(7.5f);
        var availableWidth = selector.Width - RouteEditorDialog.ScaleLogicalMetric(16, dpi);
        var titleWidth = PredictAtDpi(
            TextRenderer.MeasureText(
                source.Name, titleFont, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding),
            selector.DeviceDpi,
            dpi).Width;
        var detailWidth = PredictAtDpi(
            TextRenderer.MeasureText(
                source.Detail, detailFont, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding),
            selector.DeviceDpi,
            dpi).Width;
        Assert(availableWidth >= Math.Max(titleWidth, detailWidth),
            $"The Xbox source name and safety detail must fit its selector at {dpi} DPI; available={availableWidth}, title={titleWidth}, detail={detailWidth}.");
    }

    private static void LayoutHeadlessly(Control root)
    {
        root.CreateControl();
        for (var pass = 0; pass < 3; pass++)
        {
            root.PerformLayout();
            foreach (var control in Enumerate(root))
            {
                control.CreateControl();
                control.PerformLayout();
            }
            foreach (var scrollHost in Enumerate(root).OfType<BrandedScrollHost>())
                scrollHost.RefreshContentLayout(preservePosition: true);
        }
        Application.DoEvents();
    }

    private static void InvokeControlClick(Control control)
    {
        var onClick = typeof(Control).GetMethod(
            "OnClick",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Control.OnClick was not found.");
        _ = onClick.Invoke(control, [EventArgs.Empty]);
        Application.DoEvents();
    }

    private static void AssertLocallyVisibleDescendantsContained(Control root, int dpi)
    {
        var tolerance = Math.Max(1, RouteEditorDialog.ScaleLogicalMetric(2, dpi));
        foreach (var parent in Enumerate(root).Where(IsLocallyVisible))
        {
            foreach (Control child in parent.Controls)
            {
                if (!IsLocallyVisible(child) || child.Width <= 0 || child.Height <= 0) continue;
                if (parent is BrandedScrollHost) continue;
                var bounds = Rectangle.Inflate(parent.ClientRectangle, tolerance, tolerance);
                Assert(bounds.Contains(child.Bounds),
                    $"{child.Name} escapes {parent.Name} in the headless Xbox editor at {dpi} DPI: child={child.Bounds}, parent={parent.ClientRectangle}.");
            }
        }
    }

    private static void AssertLocallyVisibleSiblingGeometry(Control root, int dpi)
    {
        foreach (var parent in Enumerate(root).Where(IsLocallyVisible))
        {
            var children = parent.Controls.Cast<Control>()
                .Where(control => IsLocallyVisible(control) &&
                                  control.Width > 0 && control.Height > 0)
                .ToArray();
            for (var left = 0; left < children.Length; left++)
            {
                for (var right = left + 1; right < children.Length; right++)
                {
                    var overlap = Rectangle.Intersect(
                        children[left].Bounds, children[right].Bounds);
                    Assert(overlap.Width <= 1 || overlap.Height <= 1,
                        $"{children[left].Name} overlaps {children[right].Name} inside {parent.Name} in the headless Xbox editor at {dpi} DPI: {overlap}.");
                }
            }
        }
    }

    private static bool IsLocallyVisible(Control control)
    {
        var states = typeof(Control).GetNestedType(
            "States", System.Reflection.BindingFlags.NonPublic) ??
                     throw new InvalidOperationException("WinForms control states are unavailable.");
        var getState = typeof(Control).GetMethod(
            "GetState",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: [states],
            modifiers: null) ??
                       throw new InvalidOperationException("WinForms local state probe is unavailable.");
        var visible = Enum.Parse(states, "Visible");
        return (bool)getState.Invoke(control, [visible])!;
    }

    private static Size PredictAtDpi(Size measured, int measuredDpi, int targetDpi)
    {
        var scale = Math.Max(96, targetDpi) / (double)Math.Max(96, measuredDpi);
        return new Size(
            (int)Math.Ceiling(measured.Width * scale),
            (int)Math.Ceiling(measured.Height * scale));
    }

    private static void PumpUntil(
        Func<bool> condition,
        string description,
        Func<string>? timeoutDiagnostics = null)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            Application.DoEvents();
            if (condition()) return;
            Thread.Yield();
        }
        var diagnostics = timeoutDiagnostics?.Invoke();
        throw new TimeoutException(
            $"Timed out waiting for {description}." +
            (string.IsNullOrWhiteSpace(diagnostics) ? string.Empty : $" {diagnostics}"));
    }

    private static void PumpMessagesFor(TimeSpan duration)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < duration)
        {
            Application.DoEvents();
            Thread.Yield();
        }
    }

    private static void AssertDescendantsContained(Control root, int dpi)
    {
        var tolerance = Math.Max(1, RouteEditorDialog.ScaleLogicalMetric(2, dpi));
        foreach (Control parent in Enumerate(root).Prepend(root))
        {
            foreach (Control child in parent.Controls)
            {
                if (!child.Visible || child.Width <= 0 || child.Height <= 0) continue;
                if (parent is BrandedScrollHost) continue;
                var bounds = Rectangle.Inflate(parent.ClientRectangle, tolerance, tolerance);
                Assert(bounds.Contains(child.Bounds),
                    $"{child.GetType().Name} '{child.Name}' text='{child.Text}' escapes " +
                    $"{parent.GetType().Name} '{parent.Name}' owned by " +
                    $"{parent.Parent?.GetType().Name} '{parent.Parent?.Name}' at {dpi} DPI: " +
                    $"child={child.Bounds}, parent={parent.ClientRectangle}.");
            }
        }
    }

    private static void AssertSiblingGeometry(Control root, int dpi)
    {
        foreach (Control parent in Enumerate(root).Prepend(root))
        {
            var children = parent.Controls.Cast<Control>()
                .Where(control => control.Visible && control.Width > 0 && control.Height > 0)
                .ToArray();
            for (var left = 0; left < children.Length; left++)
            {
                for (var right = left + 1; right < children.Length; right++)
                {
                    var overlap = Rectangle.Intersect(children[left].Bounds, children[right].Bounds);
                    Assert(overlap.Width <= 1 || overlap.Height <= 1,
                        $"{children[left].Name} overlaps {children[right].Name} inside {parent.Name} at {dpi} DPI: {overlap}.");
                }
            }
        }
    }

    private static void AssertChoiceCards(Control[] controls, int step, int dpi)
    {
        var gridName = step switch
        {
            1 => "RouteTriggerChoices",
            2 => "RouteOutputChoices",
            _ => "RouteDestinationChoices"
        };
        var expectedCount = step switch { 1 => 4, 2 => 3, _ => 2 };
        var grid = controls.OfType<TableLayoutPanel>().Single(control => control.Name == gridName);
        var cards = grid.Controls.OfType<RoundedPanel>().ToArray();
        Assert(cards.Length == expectedCount,
            $"{gridName} must expose {expectedCount} deliberate cards rather than a clipped no-wrap text row.");
        Assert(cards.All(card => card.Height >= RouteEditorDialog.ScaleLogicalMetric(58, dpi)),
            $"{gridName} cards must retain a readable target-DPI height at {dpi} DPI.");
        var icons = cards.SelectMany(Enumerate).OfType<FigmaIconControl>().ToArray();
        Assert(icons.Length == expectedCount && icons.All(icon => icon.Width == icon.Height),
            $"{gridName} must use one square Figma icon per choice so assets are never stretched or morphed at {dpi} DPI.");
        if (step == 1)
        {
            Assert(icons.Single(icon => icon.Name == "RouteTriggerAnySourceIcon").Asset == FigmaIconAsset.Film &&
                   icons.Single(icon => icon.Name == "RouteTriggerInstantReplayIcon").Asset == FigmaIconAsset.Bolt &&
                   icons.Single(icon => icon.Name == "RouteTriggerManualRecordingIcon").Asset == FigmaIconAsset.Capture &&
                   icons.Single(icon => icon.Name == "RouteTriggerWatchedFolderIcon").Asset == FigmaIconAsset.Folder,
                "The trigger cards must keep the approved Figma film, bolt, capture, and folder assets.");
        }
    }

    private static void AssertRouteEditorText(Control[] controls, int dpi)
    {
        var title = controls.OfType<Label>().Single(label => label.Name == "RouteDialogTitle");
        var measuredTitle = TextRenderer.MeasureText(
            title.Text,
            title.Font,
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        var predictedTitle = PredictAtDpi(measuredTitle, title.DeviceDpi, dpi);
        Assert(title.Width >= predictedTitle.Width && title.Height >= predictedTitle.Height,
            $"The New route heading must not be clipped at {dpi} DPI; predicted={predictedTitle}, bounds={title.ClientSize}.");

        foreach (var label in controls.OfType<Label>().Where(label =>
                     label.Visible &&
                     label.Name.EndsWith("Title", StringComparison.Ordinal) &&
                     label.Name.StartsWith("Route", StringComparison.Ordinal) &&
                     label.Name != "RouteDialogTitle"))
        {
            var measured = TextRenderer.MeasureText(
                label.Text,
                label.Font,
                Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            var predicted = PredictAtDpi(measured, label.DeviceDpi, dpi);
            Assert(label.Width + RouteEditorDialog.ScaleLogicalMetric(3, dpi) >= predicted.Width &&
                   label.Height + RouteEditorDialog.ScaleLogicalMetric(3, dpi) >= predicted.Height,
                $"{label.Name} must fit without ellipsis at {dpi} DPI; predicted={predicted}, bounds={label.ClientSize}.");
        }

        static Size PredictAtDpi(Size measured, int measuredDpi, int targetDpi)
        {
            var scale = Math.Max(96, targetDpi) / (double)Math.Max(96, measuredDpi);
            return new Size(
                (int)Math.Ceiling(measured.Width * scale),
                (int)Math.Ceiling(measured.Height * scale));
        }
    }

    private static void AssertRoutesScaledLayout(
        string root,
        RoutingRouteManager manager,
        string connectionId,
        int dpi)
    {
        var scale = dpi / 96d;
        var requestedClientSize = new Size(
            (int)Math.Round(984 * scale),
            (int)Math.Round(696 * scale));
        using var form = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ClientSize = requestedClientSize,
            BackColor = ClipCordTheme.SurfaceBase
        };
        using var view = new RoutesView(
            manager,
            new FixedConnections(connectionId),
            isCutoverCommitted: () => true,
            layoutDpi: dpi);
        form.Controls.Add(view);
        form.Show();
        form.ClientSize = requestedClientSize;
        form.PerformLayout();
        view.ActivateView();
        Application.DoEvents();
        Assert(form.ClientSize == requestedClientSize,
            $"The Routes DPI fixture must preserve its requested {dpi}-DPI viewport; " +
            $"requested={requestedClientSize}, actual={form.ClientSize}.");
        var controls = Enumerate(view).ToArray();
        AssertButtonSize("ReorderRoutesButton", 96);
        AssertButtonSize("DeliveryHistoryButton", 132);
        AssertButtonSize(
            controls.First(control => control.Name.StartsWith(
                "DeleteRoute_", StringComparison.Ordinal)).Name,
            66);
        AssertButtonSize(
            controls.First(control => control.Name.StartsWith(
                "MoveRouteUp_", StringComparison.Ordinal)).Name,
            34);
        Assert(RoutesView.ScaleLogicalMetric(96, dpi) == (int)Math.Round(96 * scale) &&
               RoutesView.ScaleLogicalMetric(30, dpi) == (int)Math.Round(30 * scale),
            $"Routes must retain deterministic logical metrics at {dpi} DPI.");
        form.Close();

        var outboxRoot = Path.Combine(root, $"history-{dpi}");
        Directory.CreateDirectory(outboxRoot);
        var outbox = new RoutingOutboxStore(
            Path.Combine(outboxRoot, RoutingOutboxStore.FileName));
        var timestamp = new DateTimeOffset(2026, 8, 28, 14, 0, 0, TimeSpan.Zero);
        var empty = outbox.LoadOrCreateAsync(timestamp).GetAwaiter().GetResult();
        var planId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var route = new RoutingRouteSnapshotReference(
            RoutingGeneration: 1,
            RouteId: Guid.NewGuid(),
            RouteRevision: 1,
            RouteName: "DPI history route",
            ActionId: Guid.NewGuid(),
            Priority: 0,
            Order: 0);
        var output = new RoutingOutputReference(
            "clip-dpi-history",
            RoutingOutputKind.Original,
            new string('a', 64));
        var delivery = RoutingOutboxModel.CreateDelivery(
            deliveryId,
            planId,
            "clip-dpi-history",
            route,
            RoutingDestinationKind.Discord,
            connectionId,
            output,
            output,
            RoutingMissingOutputBehavior.UseOriginal,
            RoutingDeliveryMode.Automatic,
            new RoutingDeliverySettings(
                null, null, null, RoutingVisibility.Unspecified, false),
            artifactReady: true,
            intentionalDuplicate: null,
            timestamp);
        var current = RoutingOutboxModel.AppendPlan(
            empty, planId, [delivery], [], timestamp.AddSeconds(1));
        current = outbox.SaveAsync(current, empty.Generation).GetAwaiter().GetResult();
        var attemptId = Guid.NewGuid();
        var sending = RoutingOutboxModel.StartDelivery(
            current, deliveryId, attemptId, timestamp.AddSeconds(2));
        current = outbox.SaveAsync(sending, current.Generation).GetAwaiter().GetResult();
        var failed = RoutingOutboxModel.FailDelivery(
            current,
            deliveryId,
            attemptId,
            "dpi-test-failure",
            providerResumeReference: null,
            timestamp.AddSeconds(3));
        _ = outbox.SaveAsync(failed, current.Generation).GetAwaiter().GetResult();
        using var history = new RoutingDeliveryHistoryDialog(
            outbox,
            layoutDpi: dpi);
        history.CreateControl();
        history.PerformLayout();
        var close = Enumerate(history).OfType<Button>().Single(button => button.Text == "Close");
        var retry = Enumerate(history).OfType<Button>().Single(button => button.Text == "Retry");
        var historyRow = retry.Parent?.Parent as TableLayoutPanel;
        Assert(close.Size == new Size(
                   RoutingDeliveryHistoryDialog.ScaleLogicalMetric(88, dpi),
                   RoutingDeliveryHistoryDialog.ScaleLogicalMetric(30, dpi)) &&
               retry.Size == new Size(
                   RoutingDeliveryHistoryDialog.ScaleLogicalMetric(68, dpi),
                   RoutingDeliveryHistoryDialog.ScaleLogicalMetric(30, dpi)) &&
               historyRow?.ColumnStyles[2].Width ==
                   RoutingDeliveryHistoryDialog.ScaleLogicalMetric(312, dpi) &&
               history.ClientSize == new Size(
                   RoutingDeliveryHistoryDialog.ScaleLogicalMetric(820, dpi),
                   RoutingDeliveryHistoryDialog.ScaleLogicalMetric(620, dpi)),
            $"Delivery history actions and viewport must scale together at {dpi} DPI.");

        void AssertButtonSize(string name, int logicalWidth)
        {
            var button = controls.OfType<Button>().Single(control => control.Name == name);
            var expected = new Size(
                RoutesView.ScaleLogicalMetric(logicalWidth, dpi),
                RoutesView.ScaleLogicalMetric(30, dpi));
            Assert(button.Size == expected,
                $"{name} must scale from {logicalWidth}x30 to {expected} at {dpi} DPI; actual={button.Size}.");
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

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { completed.Set(); }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!completed.Wait(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("The Routes UI smoke test did not finish.");
        thread.Join();
        if (failure is not null) throw new InvalidOperationException(
            "The Routes UI smoke test failed.", failure);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class XboxRouteEditorPreflightProbe
    {
        private readonly object _gate = new();
        private readonly List<XboxRouteEditorPreflightCall> _calls = [];
        private int _windowMatchCountOverride = -1;

        internal int WindowMatchCountOverride
        {
            get => Volatile.Read(ref _windowMatchCountOverride);
            init => Volatile.Write(ref _windowMatchCountOverride, value);
        }

        internal IReadOnlyList<XboxRouteEditorPreflightCall> Calls
        {
            get
            {
                lock (_gate) return _calls.ToArray();
            }
        }

        internal XboxDvrPreflightPreview Preflight(
            RoutingInputSourceDisplay source,
            XboxDvrHistoryPolicy policy,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var historical = policy.Window != XboxDvrHistoryWindow.NewOnly;
            var eligibleCount = historical ? 3 : 0;
            var eligibleBytes = historical ? 25L * 1024 * 1024 : 0;
            IReadOnlyList<XboxDvrPreflightItem> items = historical
                ?
                [
                    new XboxDvrPreflightItem(
                        "Battlefield™ 6-2026_08_31-20-30-00.mp4",
                        "Battlefield™ 6",
                        policy.ActivationUtc.AddMinutes(-30),
                        4L * 1024 * 1024,
                        "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                        XboxDvrReparseClassification.Ordinary,
                        XboxDvrPreflightDisposition.EligibleHistorical,
                        XboxDvrNeedsAttentionReason.None),
                    new XboxDvrPreflightItem(
                        "Battlefield 6-2026_08_31-20-00-00.mp4",
                        "Battlefield 6",
                        policy.ActivationUtc.AddHours(-1),
                        6L * 1024 * 1024,
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                        "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
                        XboxDvrReparseClassification.Ordinary,
                        XboxDvrPreflightDisposition.EligibleHistorical,
                        XboxDvrNeedsAttentionReason.None),
                    new XboxDvrPreflightItem(
                        "Another Game-2026_08_31-19-00-00.mp4",
                        "Another Game",
                        policy.ActivationUtc.AddHours(-2),
                        15L * 1024 * 1024,
                        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
                        XboxDvrReparseClassification.Ordinary,
                        XboxDvrPreflightDisposition.EligibleHistorical,
                        XboxDvrNeedsAttentionReason.None)
                ]
                : [];
            var call = new XboxRouteEditorPreflightCall(
                source.SourceId,
                source.CanonicalRoot,
                policy,
                eligibleCount,
                eligibleBytes);
            lock (_gate) _calls.Add(call);
            var windowMatchCount = historical && WindowMatchCountOverride >= 0
                ? WindowMatchCountOverride
                : eligibleCount;
            return new XboxDvrPreflightPreview(
                source.SourceId,
                source.CanonicalRoot,
                policy,
                TotalClipCount: 8,
                TotalLogicalBytes: 100L * 1024 * 1024,
                ParsedClipCount: 8,
                WindowMatchCount: windowMatchCount,
                EligibleHistoricalCount: eligibleCount,
                EligibleHistoricalBytes: eligibleBytes,
                BaselineOnlyCount: 8 - eligibleCount,
                NeedsAttentionCount: 0,
                Items: items);
        }
    }

    private sealed record XboxRouteEditorPreflightCall(
        string SourceId,
        string CanonicalRoot,
        XboxDvrHistoryPolicy Policy,
        int EligibleHistoricalCount,
        long EligibleHistoricalBytes);

    private sealed class MutableInputSourceViewSource : IRoutingInputSourceViewSource
    {
        private RoutingInputSourceViewSnapshot _snapshot;

        internal MutableInputSourceViewSource(params RoutingInputSourceDisplay[] sources)
        {
            _snapshot = new RoutingInputSourceViewSnapshot(
                IsUsable: true,
                Sources: sources.ToArray(),
                StatusDetail: string.Empty);
        }

        private MutableInputSourceViewSource(RoutingInputSourceViewSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        internal int RegisterCalls { get; private set; }
        internal int EnableCalls { get; private set; }
        internal int RecheckCalls { get; private set; }
        internal int ToggleCalls { get; private set; }
        internal int RemoveCalls { get; private set; }
        internal int RelocateCalls { get; private set; }
        internal int ReplacementCalls { get; private set; }
        internal int SkipBlockedCalls { get; private set; }
        internal int RetryBlockedCalls { get; private set; }
        internal bool RecheckRequiresReplacement { get; init; }
        internal bool RelocateRequiresReplacement { get; init; }
        internal bool PrepareXboxFails { get; init; }
        internal string? LastRelocationRoot { get; private set; }
        internal string? LastReplacementRoot { get; private set; }
        internal RoutingInputSourceRegistrationDraft? LastRegistration { get; private set; }
        internal bool? LastRegistrationEnabled { get; private set; }
        internal Action? BeforeEnable { get; set; }
        internal bool EnableResult { get; set; } = true;

        internal static MutableInputSourceViewSource Empty() => new();

        internal static MutableInputSourceViewSource Unavailable() => new(
            new RoutingInputSourceViewSnapshot(
                IsUsable: false,
                Sources: [],
                StatusDetail:
                    "Saved clip-source status could not be read. No source was removed or changed."));

        public RoutingInputSourceViewSnapshot InspectInputSources() => _snapshot with
        {
            Sources = _snapshot.Sources.ToArray()
        };

        public Task<RoutingInputSourceViewActionResult> RegisterAsync(
            string displayName,
            RoutingInputSourceKind kind,
            string canonicalRoot,
            bool enabled = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RegisterCalls++;
            LastRegistrationEnabled = enabled;
            LastRegistration = new RoutingInputSourceRegistrationDraft(
                displayName,
                kind,
                canonicalRoot);
            var sourceId = $"source.{(_snapshot.Sources.Count + 1):x32}";
            var source = new RoutingInputSourceDisplay(
                sourceId,
                displayName,
                kind switch
                {
                    RoutingInputSourceKind.SteelSeriesGg =>
                        "SteelSeries GG · MP4 clips directly in the selected folder",
                    RoutingInputSourceKind.Nvidia =>
                        "NVIDIA · clips inside one game folder below the selected folder",
                    _ => "Console captures synced through OneDrive"
                },
                kind,
                canonicalRoot,
                Enabled: enabled,
                Available: true,
                Revision: 1,
                Health: RoutingInputSourceHealth.Ready);
            _snapshot = _snapshot with
            {
                Sources = _snapshot.Sources.Concat([source]).ToArray()
            };
            return Task.FromResult(new RoutingInputSourceViewActionResult(
                RoutingInputSourceViewActionStatus.Completed,
                "The clip source was added.",
                source));
        }

        public Task<RoutingInputSourceDisplay?> PrepareDefaultXboxSourceAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PrepareXboxFails)
                throw new InvalidOperationException(
                    "The default Xbox source could not be prepared.");
            return Task.FromResult(_snapshot.Sources.FirstOrDefault(source =>
                source.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive));
        }

        public Task<bool> EnableAsync(
            string sourceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnableCalls++;
            BeforeEnable?.Invoke();
            var source = _snapshot.Sources.FirstOrDefault(candidate =>
                candidate.SourceId == sourceId && candidate.Available);
            if (source is null || !EnableResult) return Task.FromResult(false);
            Replace(source with { Enabled = true, Revision = source.Revision + 1 });
            return Task.FromResult(true);
        }

        public Task<RoutingInputSourceViewActionResult> RecheckAsync(
            string sourceId,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecheckCalls++;
            var source = RecheckRequiresReplacement
                ? Find(sourceId, expectedRevision) with
                {
                    Revision = expectedRevision + 1,
                    Available = false,
                    Health = RoutingInputSourceHealth.NeedsAttention,
                    AttentionReason =
                        RoutingInputSourceAttentionReason.RootAuthorityChanged
                }
                : Find(sourceId, expectedRevision) with
            {
                Revision = expectedRevision + 1,
                Available = true,
                Health = RoutingInputSourceHealth.Ready,
                AttentionReason = RoutingInputSourceAttentionReason.None,
                SkippableBlockedClipCount = 0,
                BlockedClipRecoveryPending = false,
                BlockedClipRetryPending = false
            };
            Replace(source);
            return Task.FromResult(new RoutingInputSourceViewActionResult(
                RecheckRequiresReplacement
                    ? RoutingInputSourceViewActionStatus.ReplacementRequired
                    : RoutingInputSourceViewActionStatus.Completed,
                RecheckRequiresReplacement
                    ? "A different native Xbox source was found."
                    : "The Xbox clip source is ready.",
                source));
        }

        public Task<RoutingInputSourceViewActionResult> SetEnabledAsync(
            string sourceId,
            long expectedRevision,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ToggleCalls++;
            var source = Find(sourceId, expectedRevision) with
            {
                Revision = expectedRevision + 1,
                Enabled = enabled
            };
            Replace(source);
            return Task.FromResult(new RoutingInputSourceViewActionResult(
                RoutingInputSourceViewActionStatus.Completed,
                enabled ? "The clip source was enabled." : "The clip source was disabled.",
                source));
        }

        public Task<RoutingInputSourceViewActionResult> RemoveAsync(
            string sourceId,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Find(sourceId, expectedRevision);
            RemoveCalls++;
            _snapshot = _snapshot with { Sources = [] };
            return Task.FromResult(new RoutingInputSourceViewActionResult(
                RoutingInputSourceViewActionStatus.Completed,
                "The clip source was removed."));
        }

        public Task<RoutingInputSourceViewActionResult> RelocateAsync(
            string sourceId,
            long expectedRevision,
            string newCanonicalRoot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RelocateCalls++;
            LastRelocationRoot = newCanonicalRoot;
            if (RelocateRequiresReplacement)
            {
                var replacementRequired = Find(sourceId, expectedRevision) with
                {
                    Revision = expectedRevision + 1,
                    Available = false,
                    Health = RoutingInputSourceHealth.NeedsAttention,
                    AttentionReason =
                        RoutingInputSourceAttentionReason.RootAuthorityChanged
                };
                Replace(replacementRequired);
                return Task.FromResult(new RoutingInputSourceViewActionResult(
                    RoutingInputSourceViewActionStatus.ReplacementRequired,
                    "The discovered folder is a different native source.",
                    replacementRequired));
            }
            var current = Find(sourceId, expectedRevision);
            var clearsRootAuthorityAttention =
                current.AttentionReason ==
                RoutingInputSourceAttentionReason.RootAuthorityChanged;
            var source = current with
            {
                Revision = expectedRevision + 1,
                CanonicalRoot = newCanonicalRoot,
                Available = current.Health == RoutingInputSourceHealth.Ready ||
                    clearsRootAuthorityAttention,
                Health = clearsRootAuthorityAttention
                    ? RoutingInputSourceHealth.Ready
                    : current.Health,
                AttentionReason = clearsRootAuthorityAttention
                    ? RoutingInputSourceAttentionReason.None
                    : current.AttentionReason
            };
            Replace(source);
            return Task.FromResult(new RoutingInputSourceViewActionResult(
                RoutingInputSourceViewActionStatus.Completed,
                "The moved Xbox clip source was verified.",
                source));
        }

        public Task<RoutingInputSourceViewActionResult> RegisterReplacementAsync(
            string sourceId,
            long expectedRevision,
            string candidateCanonicalRoot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplacementCalls++;
            LastReplacementRoot = candidateCanonicalRoot;
            var source = Find(sourceId, expectedRevision);
            var retired = source with
            {
                Revision = expectedRevision + 1,
                Enabled = false,
                Available = false,
                Retired = true
            };
            var replacement = source with
            {
                SourceId = "source.55555555666677778888999999999999",
                Revision = 1,
                Name = "Xbox captures · Replacement",
                CanonicalRoot = candidateCanonicalRoot,
                Enabled = false,
                Available = true,
                Retired = false,
                Health = RoutingInputSourceHealth.Ready,
                AttentionReason = RoutingInputSourceAttentionReason.None
            };
            _snapshot = _snapshot with { Sources = [retired, replacement] };
            return Task.FromResult(new RoutingInputSourceViewActionResult(
                RoutingInputSourceViewActionStatus.Completed,
                "The replacement source was registered.",
                replacement));
        }

        public Task<RoutingInputSourceViewActionResult> SkipBlockedOccurrencesAsync(
            string sourceId,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SkipBlockedCalls++;
            var current = Find(sourceId, expectedRevision);
            var affectedCount = current.BlockedClipRecoveryPending ? 0 : 2;
            var source = current with
            {
                Revision = expectedRevision + 1,
                Available = true,
                Health = RoutingInputSourceHealth.Ready,
                AttentionReason = RoutingInputSourceAttentionReason.None,
                SkippableBlockedClipCount = 0,
                BlockedClipRecoveryPending = false,
                BlockedClipRetryPending = false
            };
            Replace(source);
            return Task.FromResult(new RoutingInputSourceViewActionResult(
                RoutingInputSourceViewActionStatus.Completed,
                affectedCount == 0
                    ? "No blocked clips remained; the Xbox source is ready."
                    : "The blocked clips were skipped. Their OneDrive originals were untouched, and future clips can resume.",
                source,
                affectedCount));
        }

        public Task<RoutingInputSourceViewActionResult> RetryBlockedOccurrencesAsync(
            string sourceId,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RetryBlockedCalls++;
            var current = Find(sourceId, expectedRevision);
            var affectedCount = current.SkippableBlockedClipCount;
            var recoveryPending = current.BlockedClipRetryPending;
            var source = current with
            {
                Revision = expectedRevision + 1,
                Available = true,
                Health = RoutingInputSourceHealth.Ready,
                AttentionReason = RoutingInputSourceAttentionReason.None,
                SkippableBlockedClipCount = 0,
                BlockedClipRecoveryPending = false,
                BlockedClipRetryPending = false
            };
            Replace(source);
            return Task.FromResult(new RoutingInputSourceViewActionResult(
                RoutingInputSourceViewActionStatus.Completed,
                recoveryPending
                    ? "The blocked-clip retry was already durable. The Xbox source is ready to resume."
                    : "The blocked clips are ready for safe recovery.",
                source,
                affectedCount));
        }

        public XboxDvrPreflightPreview Preflight(
            RoutingInputSourceDisplay source,
            XboxDvrHistoryPolicy policy,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Source-management smoke tests must not run metadata preflight.");

        private RoutingInputSourceDisplay Find(string sourceId, long expectedRevision) =>
            _snapshot.Sources.Single(source =>
                source.SourceId == sourceId && source.Revision == expectedRevision);

        private void Replace(RoutingInputSourceDisplay changed)
        {
            _snapshot = _snapshot with
            {
                Sources = _snapshot.Sources
                    .Select(source => source.SourceId == changed.SourceId ? changed : source)
                    .ToArray()
            };
        }
    }

    private sealed class MutableLocalOnlyModeViewSource(
        RoutingLocalOnlyModeViewSnapshot snapshot) : IRoutingLocalOnlyModeViewSource
    {
        private TaskCompletionSource? _enabledMutationRelease;
        internal RoutingLocalOnlyModeViewSnapshot Snapshot { get; private set; } = snapshot;
        internal int SetEnabledCalls { get; private set; }
        internal int SetHotkeyCalls { get; private set; }
        internal int DismissCalls { get; private set; }
        internal bool RejectNextHotkeyAsConflict { get; set; }
        internal bool RejectNextEnabledMutation { get; set; }

        internal void BlockNextEnabledMutation() =>
            _enabledMutationRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ReleaseEnabledMutation() => _enabledMutationRelease?.TrySetResult();

        public RoutingLocalOnlyModeViewSnapshot Inspect() => Snapshot;

        public async Task<RoutingLocalOnlyModeViewActionResult> SetEnabledAsync(
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetEnabledCalls++;
            var release = _enabledMutationRelease;
            if (release is not null)
            {
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
                if (ReferenceEquals(_enabledMutationRelease, release))
                    _enabledMutationRelease = null;
            }
            if (RejectNextEnabledMutation)
            {
                RejectNextEnabledMutation = false;
                return new RoutingLocalOnlyModeViewActionResult(
                    false,
                    Snapshot,
                    "ClipCord could not save Local-only mode.");
            }
            Snapshot = Snapshot with
            {
                EffectiveEnabled = enabled,
                StatusDetail = enabled
                    ? "External delivery is paused."
                    : "External delivery follows active routes."
            };
            return new RoutingLocalOnlyModeViewActionResult(
                true,
                Snapshot);
        }

        public Task<RoutingLocalOnlyModeViewActionResult> SetHotkeyAsync(
            string hotkeyDisplayText,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetHotkeyCalls++;
            if (RejectNextHotkeyAsConflict)
            {
                RejectNextHotkeyAsConflict = false;
                return Task.FromResult(new RoutingLocalOnlyModeViewActionResult(
                    false,
                    Snapshot,
                    "That shortcut is already registered by another ClipCord command.",
                    HotkeyConflict: true));
            }

            Snapshot = Snapshot with { HotkeyDisplayText = hotkeyDisplayText };
            return Task.FromResult(new RoutingLocalOnlyModeViewActionResult(
                true,
                Snapshot));
        }

        public Task<RoutingLocalOnlyModeViewActionResult> DismissFirstRunNoticeAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DismissCalls++;
            Snapshot = Snapshot with { FirstRunNoticeDismissed = true };
            return Task.FromResult(new RoutingLocalOnlyModeViewActionResult(
                true,
                Snapshot));
        }
    }

    private sealed class FixedConnections(string connectionId) : IRoutingConnectionViewSource
    {
        public IReadOnlyList<RoutingConnectionDisplay> LoadDiscordConnections() =>
        [new(connectionId, "Friends server", "Encrypted test connection", Available: true)];
    }

    private sealed class UnreferencedInputSourceProbe : IRoutingInputSourceReferenceProbe
    {
        public RoutingInputSourceReferenceStatus Inspect(
            string sourceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RoutingInputSourceCatalogModel.ValidateSourceId(sourceId);
            return RoutingInputSourceReferenceStatus.NotReferenced;
        }
    }

    private sealed class MutableInputSourceIdentityInspector(
        IReadOnlyDictionary<string, string> identities) : IXboxDvrSourceIdentityInspector
    {
        internal int Inspections { get; private set; }

        public XboxDvrRuntimeMetadataSnapshot Inspect(
            string canonicalRoot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Inspections++;
            return new XboxDvrRuntimeMetadataSnapshot(
                identities[canonicalRoot],
                []);
        }
    }

    private sealed class UnusedXboxMetadataFileSystem : IXboxDvrMetadataFileSystem
    {
        public IReadOnlyList<XboxDvrCandidateMetadata> EnumerateMetadata(
            string canonicalRoot,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Retired-source preparation must not enumerate clip metadata.");
    }
}

internal sealed class TestRouteMutationAuthority(bool allowed) : IRoutingRouteMutationAuthority
{
    internal static TestRouteMutationAuthority Allowed { get; } = new(true);
    internal static TestRouteMutationAuthority Blocked { get; } = new(false);

    public bool CanMutate(
        RoutingSnapshotDocument snapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);
        return allowed;
    }
}

internal sealed class TestRoutingConnectionMembership : IRoutingConnectionMembership
{
    private readonly IReadOnlySet<string>? _ready;

    internal static TestRoutingConnectionMembership AllowAll { get; } = new(null);

    internal TestRoutingConnectionMembership(IEnumerable<string>? ready)
    {
        _ready = ready?.ToHashSet(StringComparer.Ordinal);
    }

    public bool IsReady(
        RoutingDestinationKind destination,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return destination == RoutingDestinationKind.Discord &&
               (_ready is null || _ready.Contains(connectionId));
    }
}

internal sealed class FixedInputSourceMembership(
    string sourceId,
    RoutingInputSourceKind kind) : IRoutingInputSourceMembership
{
    public bool IsReady(
        string candidateSourceId,
        RoutingInputSourceKind candidateKind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return candidateSourceId.Equals(sourceId, StringComparison.Ordinal) &&
               candidateKind == kind;
    }

    public bool IsRouteBindable(
        string candidateSourceId,
        RoutingInputSourceKind candidateKind,
        CancellationToken cancellationToken = default) =>
        IsReady(candidateSourceId, candidateKind, cancellationToken);
}
