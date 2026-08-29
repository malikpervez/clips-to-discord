using System.Text;

namespace ClipsToDiscord;

internal static class DiscordConnectionCatalogTests
{
    private const string Webhook =
        "https://discord.com/api/webhooks/123456789012345678/catalog-secret-token";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await AssertCiphertextOnlyAndStableIdentityAsync(Path.Combine(root, "secure"));
        await AssertReferencesBlockCredentialChangesAsync(Path.Combine(root, "references"));
        await AssertLegacyImportAndCutoverAreOrderedAsync(Path.Combine(root, "legacy"));
        await AssertLocalOnlyCutoverDoesNotImportDormantWebhookAsync(
            Path.Combine(root, "local-only"));
        await AssertUnsafeCutoverLeavesLegacyAuthoritativeAsync(Path.Combine(root, "blocked"));
        await AssertCorruptCatalogFailsClosedAsync(Path.Combine(root, "corrupt"));
        AssertDormantLegacyWebhookIsNotStaged(root);
    }

    private static void AssertDormantLegacyWebhookIsNotStaged(string root)
    {
        var uploading = Settings(root, Webhook);
        var localOnly = uploading with { UploadToDiscord = false };
        Assert(TrayApplicationContext.ShouldStageLegacyDiscordConnection(uploading) &&
               !TrayApplicationContext.ShouldStageLegacyDiscordConnection(localOnly),
            "Opening Settings in Local-only mode must not silently turn a dormant legacy webhook into a connected Routes destination.");
    }

    private static async Task AssertCiphertextOnlyAndStableIdentityAsync(string root)
    {
        var fixture = Fixture(root, new Guid("11111111-2222-3333-4444-555555555555"));
        var added = await fixture.Catalog.AddAsync("Friends server", Webhook, Now);
        Assert(added.Status == DiscordConnectionMutationStatus.Added &&
               added.Connection is
               {
                   ConnectionId: "discord.11111111222233334444555555555555",
                   DisplayName: "Friends server",
                   Health: DiscordConnectionHealth.Ready,
                   ImportedFromLegacySettings: false
               },
            "A connection must receive one stable opaque id and a secret-free summary.");
        var connection = added.Connection ??
                         throw new InvalidOperationException(
                             "The added Discord connection summary is missing.");
        var persisted = File.ReadAllText(fixture.ConnectionStore.Path);
        Assert(!persisted.Contains(Webhook, StringComparison.Ordinal) &&
               !persisted.Contains("catalog-secret-token", StringComparison.Ordinal) &&
               !persisted.Contains("api/webhooks", StringComparison.OrdinalIgnoreCase),
            "The Discord connection catalog must persist ciphertext, never webhook material.");

        var duplicate = await fixture.Catalog.AddAsync(
            "Duplicate name does not matter", Webhook + "/", Now.AddMinutes(1));
        Assert(duplicate.Status == DiscordConnectionMutationStatus.AlreadyExists &&
               duplicate.Connection!.ConnectionId == connection.ConnectionId &&
               fixture.Catalog.Inspect().Connections.Count == 1,
            "Adding an existing webhook must return the same stable connection, not duplicate it.");
        var renamed = await fixture.Catalog.UpdateAsync(
            connection.ConnectionId, "Squad server", Webhook, Now.AddMinutes(2));
        Assert(renamed.Status == DiscordConnectionMutationStatus.Updated &&
               renamed.Connection!.ConnectionId == connection.ConnectionId &&
               renamed.Connection.DisplayName == "Squad server",
            "A metadata update must retain the opaque connection id.");
        var resolved = fixture.Catalog.ResolveForRouting(
            connection.ConnectionId, Settings(root, Webhook));
        Assert(resolved.Status == DiscordRoutingConnectionResolutionStatus.Resolved &&
               resolved.Connection is not null &&
               resolved.Connection.ToString() == "DiscordRoutingConnection { redacted }",
            "The provider-facing resolver must decrypt only into a short-lived redacted object.");
    }

    private static async Task AssertReferencesBlockCredentialChangesAsync(string root)
    {
        var fixture = Fixture(root, new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        var added = await fixture.Catalog.AddAsync("Friends server", Webhook, Now);
        var connectionId = added.Connection!.ConnectionId;
        await SaveRouteAsync(fixture.RouteStore, connectionId, Now.AddMinutes(1));

        var renamed = await fixture.Catalog.UpdateAsync(
            connectionId, "Renamed while active", Webhook, Now.AddMinutes(2));
        Assert(renamed.Status == DiscordConnectionMutationStatus.Updated,
            "A name-only change is safe while a route references the connection.");
        var redirected = await fixture.Catalog.UpdateAsync(
            connectionId,
            "Redirected",
            Webhook.Replace("catalog-secret-token", "different-destination-token",
                StringComparison.Ordinal),
            Now.AddMinutes(3));
        Assert(redirected.Status ==
               DiscordConnectionMutationStatus.CredentialReplacementRequired,
            "A stable connection id must never be silently redirected to another webhook.");
        var removed = await fixture.Catalog.RemoveAsync(connectionId, Now.AddMinutes(3));
        Assert(removed.Status == DiscordConnectionMutationStatus.InUse,
            "A route reference must block removing its encrypted credential.");

        var currentRoutes = fixture.RouteStore.Load().Document!;
        await fixture.RouteStore.SaveAsync(
            RoutingSnapshotModel.ReplaceRoutes(currentRoutes, [], Now.AddMinutes(4)),
            currentRoutes.Generation);
        await SavePendingDeliveryAsync(fixture.OutboxStore, connectionId, Now.AddMinutes(5));
        removed = await fixture.Catalog.RemoveAsync(connectionId, Now.AddMinutes(6));
        Assert(removed.Status == DiscordConnectionMutationStatus.InUse,
            "A non-terminal frozen outbox delivery must retain its encrypted connection.");
    }

    private static async Task AssertLegacyImportAndCutoverAreOrderedAsync(string root)
    {
        var fixture = Fixture(root, new Guid("12345678-1234-1234-1234-1234567890ab"));
        var clips = Path.Combine(root, "clips");
        Directory.CreateDirectory(clips);
        var settings = Settings(clips, Webhook);
        var state = State(clips);
        var coordinator = new LegacyRoutingMigrationCoordinator(
            fixture.RouteStore,
            new LegacyRoutingMigrationMarkerStore(
                Path.Combine(root, "routing", LegacyRoutingMigrationMarkerStore.FileName)));
        var adapter = new LegacyDiscordConnectionCutoverAdapter(
            fixture.Catalog, coordinator);
        var first = await adapter.ExecuteAsync(
            settings, state, legacyWorkerQuiesced: true, Now);
        Assert(first.Status == LegacyDiscordConnectionCutoverStatus.Completed &&
               first.MayReleaseLegacyOwnership &&
               first.Cutover!.Status == LegacyRoutingCutoverResultStatus.Committed &&
               first.Connection is
               {
                   Health: DiscordConnectionHealth.Ready,
                   ImportedFromLegacySettings: true
               },
            "A safe cutover must import the encrypted connection before committing route ownership evidence.");
        var route = fixture.RouteStore.Load().Document!.Routes.Single();
        Assert(route.Actions[0].ConnectionId == first.Connection!.ConnectionId &&
               route.Actions[0].Destination == RoutingDestinationKind.Discord,
            "The migrated route must retain only the opaque catalog connection id.");
        var durableText = string.Join('\n',
            File.ReadAllText(fixture.ConnectionStore.Path),
            File.ReadAllText(fixture.RouteStore.Path),
            File.ReadAllText(Path.Combine(
                root, "routing", LegacyRoutingMigrationMarkerStore.FileName)));
        Assert(!durableText.Contains(Webhook, StringComparison.Ordinal) &&
               !durableText.Contains("catalog-secret-token", StringComparison.Ordinal) &&
               !durableText.Contains("api/webhooks", StringComparison.OrdinalIgnoreCase),
            "Neither the encrypted catalog, route, nor cutover marker may reveal the webhook.");

        var restarted = new LegacyDiscordConnectionCutoverAdapter(
            fixture.Catalog,
            new LegacyRoutingMigrationCoordinator(
                fixture.RouteStore,
                new LegacyRoutingMigrationMarkerStore(Path.Combine(
                    root, "routing", LegacyRoutingMigrationMarkerStore.FileName))));
        var second = await restarted.ExecuteAsync(
            settings, state, legacyWorkerQuiesced: true, Now.AddMinutes(1));
        Assert(second.MayReleaseLegacyOwnership &&
               second.Cutover!.Status == LegacyRoutingCutoverResultStatus.AlreadyCommitted &&
               second.Connection!.ConnectionId == first.Connection.ConnectionId &&
               fixture.Catalog.Inspect().Connections.Count == 1,
            "Restart must reuse one imported credential and one committed migration route.");
    }

    private static async Task AssertUnsafeCutoverLeavesLegacyAuthoritativeAsync(string root)
    {
        var fixture = Fixture(root, new Guid("fedcba98-7654-3210-aaaa-bbbbbbbbbbbb"));
        var clips = Path.Combine(root, "clips");
        Directory.CreateDirectory(clips);
        var settings = Settings(clips, Webhook);
        var marker = new LegacyRoutingMigrationMarkerStore(
            Path.Combine(root, "routing", LegacyRoutingMigrationMarkerStore.FileName));
        var adapter = new LegacyDiscordConnectionCutoverAdapter(
            fixture.Catalog,
            new LegacyRoutingMigrationCoordinator(fixture.RouteStore, marker));
        var result = await adapter.ExecuteAsync(
            settings, State(clips), legacyWorkerQuiesced: false, Now);
        Assert(!result.MayReleaseLegacyOwnership &&
               result.Cutover!.Status == LegacyRoutingCutoverResultStatus.Blocked &&
               result.Cutover.Readiness.Status ==
               LegacyRoutingCutoverReadinessStatus.LegacyWorkerActive &&
               marker.Load().Status == RoutingDocumentLoadStatus.Missing &&
               fixture.RouteStore.Load().Status == RoutingDocumentLoadStatus.Missing &&
               settings.UploadToDiscord && settings.WebhookUrl == Webhook,
            "A blocked cutover may stage only encrypted connection state and must leave the legacy route authoritative.");
    }

    private static async Task AssertLocalOnlyCutoverDoesNotImportDormantWebhookAsync(
        string root)
    {
        var fixture = Fixture(root, new Guid("87654321-4321-4321-4321-ba0987654321"));
        var clips = Path.Combine(root, "clips");
        Directory.CreateDirectory(clips);
        var settings = Settings(clips, Webhook) with { UploadToDiscord = false };

        var directImport = await fixture.Catalog.EnsureLegacyConnectionAsync(settings, Now);
        Assert(directImport.Status == DiscordConnectionMutationStatus.InvalidInput &&
               fixture.Catalog.Inspect().Connections.Count == 0,
            "The catalog itself must reject importing a dormant webhook while Discord uploads are disabled.");

        var marker = new LegacyRoutingMigrationMarkerStore(
            Path.Combine(root, "routing", LegacyRoutingMigrationMarkerStore.FileName));
        var adapter = new LegacyDiscordConnectionCutoverAdapter(
            fixture.Catalog,
            new LegacyRoutingMigrationCoordinator(fixture.RouteStore, marker));
        var result = await adapter.ExecuteAsync(
            settings, State(clips), legacyWorkerQuiesced: true, Now.AddMinutes(1));
        var route = fixture.RouteStore.Load().Document!.Routes.Single();
        Assert(result.Status == LegacyDiscordConnectionCutoverStatus.Completed &&
               result.MayReleaseLegacyOwnership && result.Connection is null &&
               fixture.Catalog.Inspect().Connections.Count == 0 &&
               route.Source == RoutingRouteSource.Migration &&
               route.Kind == RoutingRouteKind.Fallback &&
               route.Actions.Count == 1 &&
               route.Actions[0] is
               {
                   Kind: RoutingActionKind.FileIntoLibrary,
                   LibraryArea: RoutingLibraryArea.LocalOnly,
                   ConnectionId: null
               },
            "Local-only cutover must remain connectionless and preserve only File into Library behavior.");
        var durableText = string.Join('\n',
            File.Exists(fixture.ConnectionStore.Path)
                ? File.ReadAllText(fixture.ConnectionStore.Path)
                : string.Empty,
            File.ReadAllText(fixture.RouteStore.Path),
            File.ReadAllText(marker.Path));
        Assert(!durableText.Contains(Webhook, StringComparison.Ordinal) &&
               !durableText.Contains("catalog-secret-token", StringComparison.Ordinal) &&
               !durableText.Contains("api/webhooks", StringComparison.OrdinalIgnoreCase),
            "A dormant Local-only webhook must never enter connection, route, or cutover state.");
    }

    private static async Task AssertCorruptCatalogFailsClosedAsync(string root)
    {
        var fixture = Fixture(root, Guid.NewGuid());
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ConnectionStore.Path)!);
        await File.WriteAllTextAsync(fixture.ConnectionStore.Path, "{ definitely not json");
        var result = await fixture.Catalog.AddAsync("Friends server", Webhook, Now);
        Assert(result.Status == DiscordConnectionMutationStatus.StateUnavailable &&
               await File.ReadAllTextAsync(fixture.ConnectionStore.Path) ==
               "{ definitely not json",
            "A corrupt encrypted catalog must never be replaced or partially repaired by mutation.");
    }

    private static CatalogFixture Fixture(string root, Guid connectionGuid)
    {
        var routingRoot = Path.Combine(root, "routing");
        var connections = new DiscordConnectionCatalogStore(
            Path.Combine(routingRoot, DiscordConnectionCatalogStore.FileName));
        var routes = new RoutingSnapshotStore(
            Path.Combine(routingRoot, RoutingSnapshotStore.FileName));
        var outbox = new RoutingOutboxStore(
            Path.Combine(routingRoot, RoutingOutboxStore.FileName));
        var catalog = new DiscordConnectionCatalog(
            connections,
            new TestWebhookProtector(),
            new DiscordConnectionReferenceProbe(routes, outbox),
            () => connectionGuid);
        return new CatalogFixture(catalog, connections, routes, outbox);
    }

    private static async Task SaveRouteAsync(
        RoutingSnapshotStore store,
        string connectionId,
        DateTimeOffset now)
    {
        var current = await store.LoadOrCreateAsync(now.AddSeconds(-1));
        var route = new RoutingRoute(
            Guid.NewGuid(),
            "Everything else → Friends server",
            Enabled: true,
            Priority: 0,
            Revision: 1,
            RoutingRouteSource.User,
            RoutingRouteKind.Fallback,
            RoutingTriggerKind.AnyNewSourceClip,
            new RoutingPrepareSettings(
                Landscape: false,
                Portrait: false,
                RoutingMissingOutputBehavior.UseOriginal),
            Conditions: [],
            Actions:
            [
                new RoutingAction(
                    Guid.NewGuid(),
                    Enabled: true,
                    RoutingActionKind.Deliver,
                    RoutingDestinationKind.Discord,
                    connectionId,
                    RoutingOutputKind.Original,
                    RoutingMissingOutputBehavior.UseOriginal,
                    RoutingDeliveryMode.Automatic,
                    LibraryArea: null,
                    new RoutingDeliverySettings(
                        Message: null,
                        Title: null,
                        Caption: null,
                        RoutingVisibility.Unspecified,
                        NotifyFollowers: false)),
                new RoutingAction(
                    Guid.NewGuid(),
                    Enabled: true,
                    RoutingActionKind.FileIntoLibrary,
                    Destination: null,
                    ConnectionId: null,
                    OutputRef: null,
                    OnMissingOutput: null,
                    RoutingDeliveryMode.Automatic,
                    RoutingLibraryArea.Uploaded,
                    DeliverySettings: null)
            ],
            CreatedUtc: now,
            ModifiedUtc: now);
        var next = RoutingSnapshotModel.ReplaceRoutes(current, [route], now);
        await store.SaveAsync(next, current.Generation);
    }

    private static async Task SavePendingDeliveryAsync(
        RoutingOutboxStore store,
        string connectionId,
        DateTimeOffset now)
    {
        var current = await store.LoadOrCreateAsync(now.AddSeconds(-1));
        var planId = Guid.NewGuid();
        const string clipId = "clip-connection-reference";
        var output = new RoutingOutputReference(
            clipId, RoutingOutputKind.Original, new string('a', 64));
        var route = new RoutingRouteSnapshotReference(
            RoutingGeneration: 2,
            RouteId: Guid.NewGuid(),
            RouteRevision: 1,
            RouteName: "Reference guard route",
            ActionId: Guid.NewGuid(),
            Priority: 0,
            Order: 0);
        var delivery = RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(),
            planId,
            clipId,
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
            now);
        var next = RoutingOutboxModel.AppendPlan(
            current, planId, [delivery], [], now);
        await store.SaveAsync(next, current.Generation);
    }

    private static AppSettings Settings(string clipsFolder, string webhook) => new(
        clipsFolder,
        webhook,
        StartWithWindows: false,
        AppSettings.DefaultCompressionTargetMb,
        "Catalog tester",
        UploadToDiscord: true);

    private static WatchState State(string clips) => new()
    {
        Version = 4,
        ClipsFolder = clips,
        CaptureSource = ClipCaptureSource.SteelSeriesGg,
        KnownContentHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        UploadedContentHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        LocalOnlyContentHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        IgnoredFileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        PendingMoves = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        PendingLocalOnlyMoves = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        PendingEditedUploads = []
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record CatalogFixture(
        DiscordConnectionCatalog Catalog,
        DiscordConnectionCatalogStore ConnectionStore,
        RoutingSnapshotStore RouteStore,
        RoutingOutboxStore OutboxStore);

    private sealed class TestWebhookProtector : IDiscordWebhookProtector
    {
        public string Protect(string webhookUrl, string connectionId)
        {
            if (!WebhookValidation.IsDiscordWebhook(webhookUrl))
                throw new ArgumentException("Invalid webhook.", nameof(webhookUrl));
            var bytes = Encoding.UTF8.GetBytes(
                $"test-v1\0{connectionId}\0{webhookUrl.Trim()}");
            for (var index = 0; index < bytes.Length; index++) bytes[index] ^= 0xa5;
            return Convert.ToBase64String(bytes);
        }

        public bool TryUnprotect(
            string protectedWebhook,
            string connectionId,
            out string webhookUrl)
        {
            webhookUrl = string.Empty;
            try
            {
                var bytes = Convert.FromBase64String(protectedWebhook);
                for (var index = 0; index < bytes.Length; index++) bytes[index] ^= 0xa5;
                var value = Encoding.UTF8.GetString(bytes);
                var prefix = $"test-v1\0{connectionId}\0";
                if (!value.StartsWith(prefix, StringComparison.Ordinal)) return false;
                var candidate = value[prefix.Length..];
                if (!WebhookValidation.IsDiscordWebhook(candidate)) return false;
                webhookUrl = candidate;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
