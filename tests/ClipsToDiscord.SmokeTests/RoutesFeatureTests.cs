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
        RunOnSta(() =>
        {
            AssertRoutesView(manager, connectionId);
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
                DiscordConnectionIds: []),
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
            BackColor = ClipCordTheme.SurfaceBase
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
               } && statusText.Contains("ROUTING INACTIVE", StringComparison.Ordinal) &&
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
        form.Close();

        AssertExplicitDiscordConnectionSelection(connectionId);
    }

    private static void AssertExplicitDiscordConnectionSelection(string firstConnectionId)
    {
        const string secondConnectionId = "discord.22222222222222222222222222222222";
        using var dialog = new RouteEditorDialog(
        [
            new RoutingConnectionDisplay(
                firstConnectionId, "Alpha server", "Encrypted test connection", true),
            new RoutingConnectionDisplay(
                secondConnectionId, "Bravo server", "Encrypted test connection", true)
        ]);
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new Point(-32000, -32000);
        dialog.Show();
        Application.DoEvents();
        var controls = Enumerate(dialog).ToArray();
        var selector = controls.OfType<ComboBox>().Single(control =>
            control.Name == "RouteDiscordConnectionSelector");
        var selectorLabel = controls.OfType<Label>().Single(label =>
            label.Text == "DISCORD CONNECTION");
        Assert(selector.Items.Count == 2 && selector.SelectedIndex == -1,
            "A route must not silently select the alphabetically first Discord connection.");
        Assert(selectorLabel.Visible && selector.AccessibleName == "Discord connection" &&
               selectorLabel.Parent == selector.Parent &&
               !selectorLabel.Bounds.IntersectsWith(selector.Bounds),
            "The Discord connection selector must have a visible non-overlapping label as well as an accessible name.");
        controls.OfType<TextBox>().Single(control =>
            control.AccessibleName == "Route name").Text = "Explicit destination";
        selector.SelectedIndex = 1;
        controls.OfType<Button>().Single(button => button.Text == "Save route").PerformClick();
        Assert(dialog.Draft?.ConnectionId == secondConnectionId,
            "Saving a route must freeze the exact Discord connection explicitly selected by the user.");
    }

    private static void AssertRoutesScaledLayout(
        string root,
        RoutingRouteManager manager,
        string connectionId,
        int dpi)
    {
        var scale = dpi / 96d;
        using var form = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ClientSize = new Size(
                (int)Math.Round(984 * scale),
                (int)Math.Round(696 * scale)),
            BackColor = ClipCordTheme.SurfaceBase
        };
        using var view = new RoutesView(
            manager,
            new FixedConnections(connectionId),
            isCutoverCommitted: () => true,
            layoutDpi: dpi);
        form.Controls.Add(view);
        form.Show();
        view.ActivateView();
        Application.DoEvents();
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
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!completed.Wait(TimeSpan.FromSeconds(20)))
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

    private sealed class FixedConnections(string connectionId) : IRoutingConnectionViewSource
    {
        public IReadOnlyList<RoutingConnectionDisplay> LoadDiscordConnections() =>
        [new(connectionId, "Friends server", "Encrypted test connection", Available: true)];
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
