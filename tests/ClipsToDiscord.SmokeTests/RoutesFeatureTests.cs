using ClipsToDiscord;

internal static class RoutesFeatureTests
{
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        var store = new RoutingSnapshotStore(Path.Combine(root, RoutingSnapshotStore.FileName));
        var now = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        var tick = 0;
        var manager = new RoutingRouteManager(store, () => now.AddMinutes(tick++));
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
        RunOnSta(() => AssertRoutesView(manager, connectionId));
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
        var manager = new RoutingRouteManager(store, () => now.AddMinutes(1));
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

    private static void AssertRoutesView(RoutingRouteManager manager, string connectionId)
    {
        using var form = new Form
        {
            ClientSize = new Size(984, 696),
            BackColor = ClipCordTheme.SurfaceBase
        };
        using var view = new RoutesView(
            manager,
            new FixedConnections(connectionId),
            isCutoverCommitted: () => false);
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
        Assert(selector.Items.Count == 2 && selector.SelectedIndex == -1,
            "A route must not silently select the alphabetically first Discord connection.");
        controls.OfType<TextBox>().Single(control =>
            control.AccessibleName == "Route name").Text = "Explicit destination";
        selector.SelectedIndex = 1;
        controls.OfType<Button>().Single(button => button.Text == "Save route").PerformClick();
        Assert(dialog.Draft?.ConnectionId == secondConnectionId,
            "Saving a route must freeze the exact Discord connection explicitly selected by the user.");
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
