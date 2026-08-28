using System.Net;
using System.Text;

namespace ClipsToDiscord;

internal static class DiscordRoutingProviderTests
{
    private const string Webhook =
        "https://discord.com/api/webhooks/123456789012345678/provider-secret-token";
    private const string CatalogWebhook =
        "https://discord.com/api/webhooks/999999999999999999/catalog-destination-token";

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var clipPath = Path.Combine(root, "routing-provider.mp4");
        await File.WriteAllBytesAsync(clipPath, [1, 3, 3, 7]);

        AssertConnectionIdentity();
        AssertReceiptParsing();
        await AssertLegacySuccessDoesNotRequireReceiptAsync(clipPath);
        await AssertConfirmedReceiptAsync(root, clipPath);
        await AssertMissingReceiptIsUnknownAsync(root, clipPath);
        await AssertConnectionMismatchRefusesSendAsync(root, clipPath);
        await AssertDefiniteRejectionFailsAsync(root, clipPath);
        await AssertTransportAmbiguityIsUnknownAsync(root, clipPath);
        await AssertRandomCatalogConnectionResolvesAsync(root, clipPath);
        await AssertMissingCatalogConnectionFailsClosedAsync(root, clipPath);
        await AssertTamperedCatalogConnectionFailsClosedAsync(root, clipPath);
        await AssertLegacyConnectionStillResolvesWithCatalogAsync(root, clipPath);
    }

    private static void AssertConnectionIdentity()
    {
        Assert(DiscordRoutingConnectionIdentity.TryCreate(Webhook, out var first) &&
               DiscordRoutingConnectionIdentity.TryCreate(Webhook + "/", out var trailingSlash) &&
               first == trailingSlash,
            "Equivalent Discord webhook spellings must produce one stable opaque connection id.");
        Assert(DiscordRoutingConnectionIdentity.TryCreate(
                   Webhook.Replace("provider-secret-token", "different-secret-token",
                       StringComparison.Ordinal),
                   out var changed) &&
               changed != first &&
               first.StartsWith("discord.", StringComparison.Ordinal) &&
               first.Length == "discord.".Length + 64 &&
               !first.Contains("provider-secret-token", StringComparison.Ordinal),
            "The Discord connection id must be content-bound, stable, and secret-free.");
        Assert(!DiscordRoutingConnectionIdentity.TryCreate(
                   "https://example.com/api/webhooks/123/token", out _),
            "A non-Discord webhook must never receive a routable connection id.");
    }

    private static void AssertReceiptParsing()
    {
        var receipt = DiscordWebhookClient.TryParseUploadReceipt(
            "{\"id\":\"1234567890123456789\",\"content\":\"ok\"}");
        Assert(receipt?.MessageId == "1234567890123456789" &&
               receipt.Reference == "discord:1234567890123456789",
            "A wait=true Discord message response must preserve its message id as a receipt.");
        foreach (var malformed in new[]
                 {
                     string.Empty,
                     "not-json",
                     "{}",
                     "{\"id\":123}",
                     "{\"id\":\"message-id\"}"
                 })
        {
            Assert(DiscordWebhookClient.TryParseUploadReceipt(malformed) is null,
                "Malformed or non-snowflake Discord success content must not invent a receipt.");
        }
    }

    private static async Task AssertLegacySuccessDoesNotRequireReceiptAsync(string clipPath)
    {
        var handler = new RecordingHandler((_, _) => Response(HttpStatusCode.OK, "{}"));
        using var client = new DiscordWebhookClient(handler);
        await client.UploadWithCompressionAsync(
            Webhook,
            clipPath,
            new DiscordUploadPresentation(Path.GetFileName(clipPath), "Test Game", null),
            95,
            "Tester",
            CancellationToken.None);
        Assert(handler.CallCount == 1,
            "The existing upload API must retain its one-POST success behavior when no receipt is returned.");
    }

    private static async Task AssertConfirmedReceiptAsync(string root, string clipPath)
    {
        var settings = Settings(root, Webhook);
        Assert(DiscordRoutingConnectionIdentity.TryCreate(Webhook, out var connectionId),
            "The provider test webhook must have a connection id.");
        var handler = new RecordingHandler((request, _) =>
        {
            Assert(request.RequestUri is not null &&
                   request.RequestUri.Query.Contains("wait=true", StringComparison.Ordinal),
                "Receipt-preserving Discord sends must explicitly request wait=true.");
            return Response(HttpStatusCode.OK, "{\"id\":\"987654321098765432\"}");
        });
        var provider = Provider(settings, handler);
        var result = await provider.SendAsync(
            Delivery(connectionId), Artifact(clipPath), CancellationToken.None);

        Assert(result.Outcome == RoutingDeliveryAttemptOutcome.Confirmed &&
               result.RemoteReceiptReference == "discord:987654321098765432" &&
               result.ErrorCode is null &&
               handler.CallCount == 1,
            "A valid Discord message response must confirm the delivery with its durable receipt.");
        Assert(!ResultText(result, connectionId).Contains(Webhook, StringComparison.Ordinal) &&
               !ResultText(result, connectionId).Contains(
                   "provider-secret-token", StringComparison.Ordinal),
            "Routing state derived from a Discord send must never contain the webhook or token.");
    }

    private static async Task AssertMissingReceiptIsUnknownAsync(string root, string clipPath)
    {
        var settings = Settings(root, Webhook);
        Assert(DiscordRoutingConnectionIdentity.TryCreate(Webhook, out var connectionId),
            "The provider test webhook must have a connection id.");
        var handler = new RecordingHandler((_, _) => Response(HttpStatusCode.NoContent, null));
        var result = await Provider(settings, handler).SendAsync(
            Delivery(connectionId), Artifact(clipPath), CancellationToken.None);
        Assert(result.Outcome == RoutingDeliveryAttemptOutcome.Unknown &&
               result.ErrorCode == "discord-receipt-missing" &&
               result.RemoteReceiptReference is null &&
               handler.CallCount == 1,
            "A successful Discord POST without a usable receipt must become delivery-unknown, not retryable failure.");
    }

    private static async Task AssertConnectionMismatchRefusesSendAsync(
        string root,
        string clipPath)
    {
        var settings = Settings(root, Webhook);
        Assert(DiscordRoutingConnectionIdentity.TryCreate(
                   Webhook.Replace("provider-secret-token", "old-secret-token",
                       StringComparison.Ordinal),
                   out var frozenConnectionId),
            "The old provider test webhook must have a connection id.");
        var handler = new RecordingHandler((_, _) =>
            throw new InvalidOperationException("A mismatched connection must not send."));
        var result = await Provider(settings, handler).SendAsync(
            Delivery(frozenConnectionId), Artifact(clipPath), CancellationToken.None);
        Assert(result.Outcome == RoutingDeliveryAttemptOutcome.Failed &&
               result.ErrorCode == "discord-connection-mismatch" &&
               handler.CallCount == 0,
            "A frozen plan must refuse to send after the current DPAPI-backed webhook changes.");
    }

    private static async Task AssertDefiniteRejectionFailsAsync(string root, string clipPath)
    {
        var settings = Settings(root, Webhook);
        Assert(DiscordRoutingConnectionIdentity.TryCreate(Webhook, out var connectionId),
            "The provider test webhook must have a connection id.");
        var handler = new RecordingHandler((_, _) =>
            Response(HttpStatusCode.Unauthorized, "{\"message\":\"rejected\"}"));
        var result = await Provider(settings, handler).SendAsync(
            Delivery(connectionId), Artifact(clipPath), CancellationToken.None);
        Assert(result.Outcome == RoutingDeliveryAttemptOutcome.Failed &&
               result.ErrorCode == "discord-upload-rejected",
            "A definite Discord HTTP rejection must remain a retryable failed attempt.");
    }

    private static async Task AssertTransportAmbiguityIsUnknownAsync(string root, string clipPath)
    {
        var settings = Settings(root, Webhook);
        Assert(DiscordRoutingConnectionIdentity.TryCreate(Webhook, out var connectionId),
            "The provider test webhook must have a connection id.");
        var handler = new RecordingHandler((_, _) =>
            throw new HttpRequestException("simulated connection loss after request start"));
        var result = await Provider(settings, handler).SendAsync(
            Delivery(connectionId), Artifact(clipPath), CancellationToken.None);
        Assert(result.Outcome == RoutingDeliveryAttemptOutcome.Unknown &&
               result.ErrorCode == "discord-result-unknown" &&
               result.RemoteReceiptReference is null,
            "A transport failure after provider entry must be delivery-unknown to prevent duplicate retry.");
    }

    private static async Task AssertRandomCatalogConnectionResolvesAsync(
        string root,
        string clipPath)
    {
        var fixture = Catalog(root, "random-id",
            new Guid("01234567-89ab-cdef-0123-456789abcdef"));
        var added = await fixture.Catalog.AddAsync(
            "Catalog destination", CatalogWebhook, DateTimeOffset.UtcNow);
        Assert(added.Status == DiscordConnectionMutationStatus.Added &&
               added.Connection is
               {
                   ConnectionId: "discord.0123456789abcdef0123456789abcdef",
                   Health: DiscordConnectionHealth.Ready
               },
            "The provider catalog fixture must create one canonical random connection id.");
        var handler = new RecordingHandler((request, _) =>
        {
            Assert(request.RequestUri is not null &&
                   request.RequestUri.AbsolutePath.Contains(
                       "catalog-destination-token", StringComparison.Ordinal) &&
                   !request.RequestUri.AbsolutePath.Contains(
                       "provider-secret-token", StringComparison.Ordinal),
                "A catalog delivery must resolve the encrypted catalog destination, not the legacy setting.");
            return Response(HttpStatusCode.OK, "{\"id\":\"444444444444444444\"}");
        });
        var result = await Provider(
                Settings(root, Webhook), handler, fixture.Catalog)
            .SendAsync(
                Delivery(added.Connection!.ConnectionId),
                Artifact(clipPath),
                CancellationToken.None);
        Assert(result.Outcome == RoutingDeliveryAttemptOutcome.Confirmed &&
               result.RemoteReceiptReference == "discord:444444444444444444" &&
               handler.CallCount == 1,
            "A random catalog connection id must resolve and send exactly once.");
    }

    private static async Task AssertMissingCatalogConnectionFailsClosedAsync(
        string root,
        string clipPath)
    {
        var fixture = Catalog(root, "missing-id", Guid.NewGuid());
        _ = await fixture.Store.LoadOrCreateAsync(DateTimeOffset.UtcNow);
        var handler = new RecordingHandler((_, _) =>
            throw new InvalidOperationException(
                "A missing random catalog id must never fall back to the legacy webhook."));
        var result = await Provider(
                Settings(root, Webhook), handler, fixture.Catalog)
            .SendAsync(
                Delivery("discord.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                Artifact(clipPath),
                CancellationToken.None);
        Assert(result.Outcome == RoutingDeliveryAttemptOutcome.Failed &&
               result.ErrorCode == "discord-connection-mismatch" &&
               handler.CallCount == 0,
            "A missing random catalog connection must fail closed without legacy fallback or network I/O.");

        var absent = Catalog(root, "missing-store", Guid.NewGuid());
        var unavailableResult = await Provider(
                Settings(root, Webhook), handler, absent.Catalog)
            .SendAsync(
                Delivery("discord.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
                Artifact(clipPath),
                CancellationToken.None);
        Assert(unavailableResult.Outcome == RoutingDeliveryAttemptOutcome.Failed &&
               unavailableResult.ErrorCode == "discord-connection-unavailable" &&
               handler.CallCount == 0,
            "A missing catalog file must leave a random connection unavailable, never reinterpret it as legacy.");
    }

    private static async Task AssertTamperedCatalogConnectionFailsClosedAsync(
        string root,
        string clipPath)
    {
        var fixture = Catalog(root, "tampered", Guid.NewGuid());
        var added = await fixture.Catalog.AddAsync(
            "Tamper target", CatalogWebhook, DateTimeOffset.UtcNow);
        Assert(added.Connection is not null,
            "The tamper fixture must create a catalog connection.");
        var current = fixture.Store.Load().Document!;
        var target = current.Connections.Single(item =>
            item.ConnectionId == added.Connection!.ConnectionId);
        var tampered = target with
        {
            ProtectedWebhook = Convert.ToBase64String([1, 2, 3, 4]),
            UpdatedUtc = current.UpdatedUtc.AddSeconds(1)
        };
        var next = DiscordConnectionCatalogModel.ReplaceConnections(
            current,
            current.Connections.Select(item =>
                    item.ConnectionId == target.ConnectionId ? tampered : item)
                .ToArray(),
            current.UpdatedUtc.AddSeconds(1));
        await fixture.Store.SaveAsync(next, current.Generation);

        var handler = new RecordingHandler((_, _) =>
            throw new InvalidOperationException(
                "A tampered protected webhook must never reach the network."));
        var result = await Provider(
                Settings(root, Webhook), handler, fixture.Catalog)
            .SendAsync(
                Delivery(target.ConnectionId),
                Artifact(clipPath),
                CancellationToken.None);
        Assert(result.Outcome == RoutingDeliveryAttemptOutcome.Failed &&
               result.ErrorCode == "discord-connection-unavailable" &&
               handler.CallCount == 0,
            "A structurally valid but undecryptable catalog credential must fail closed before provider I/O.");
    }

    private static async Task AssertLegacyConnectionStillResolvesWithCatalogAsync(
        string root,
        string clipPath)
    {
        var fixture = Catalog(root, "legacy-compatible", Guid.NewGuid());
        _ = await fixture.Store.LoadOrCreateAsync(DateTimeOffset.UtcNow);
        Assert(DiscordRoutingConnectionIdentity.TryCreate(Webhook, out var legacyConnectionId),
            "The legacy compatibility fixture must derive its stable digest id.");
        var handler = new RecordingHandler((request, _) =>
        {
            Assert(request.RequestUri?.AbsolutePath.Contains(
                       "provider-secret-token", StringComparison.Ordinal) == true,
                "A legacy digest id must continue resolving through the DPAPI-backed settings path.");
            return Response(HttpStatusCode.OK, "{\"id\":\"555555555555555555\"}");
        });
        var result = await Provider(
                Settings(root, Webhook), handler, fixture.Catalog)
            .SendAsync(
                Delivery(legacyConnectionId),
                Artifact(clipPath),
                CancellationToken.None);
        Assert(result.Outcome == RoutingDeliveryAttemptOutcome.Confirmed &&
               result.RemoteReceiptReference == "discord:555555555555555555" &&
               handler.CallCount == 1,
            "Adding the catalog resolver must preserve existing legacy digest-id deliveries.");
    }

    private static DiscordRoutingProvider Provider(
        AppSettings settings,
        RecordingHandler handler,
        DiscordConnectionCatalog? catalog = null) =>
        new(
            new DiscordRoutingConnectionResolver(() => settings, catalog),
            () => new DiscordWebhookClient(handler));

    private static CatalogFixture Catalog(string root, string name, Guid id)
    {
        var directory = Path.Combine(root, "catalog-provider", name, "routing");
        var store = new DiscordConnectionCatalogStore(
            Path.Combine(directory, DiscordConnectionCatalogStore.FileName));
        var routes = new RoutingSnapshotStore(
            Path.Combine(directory, RoutingSnapshotStore.FileName));
        var outbox = new RoutingOutboxStore(
            Path.Combine(directory, RoutingOutboxStore.FileName));
        var catalog = new DiscordConnectionCatalog(
            store,
            new CurrentUserDiscordWebhookProtector(),
            new DiscordConnectionReferenceProbe(routes, outbox),
            () => id);
        return new CatalogFixture(catalog, store);
    }

    private static PlannedDelivery Delivery(string connectionId)
    {
        var planId = Guid.NewGuid();
        var output = new RoutingOutputReference(
            "clip-discord-provider",
            RoutingOutputKind.Original,
            new string('a', 64));
        return RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(),
            planId,
            output.ClipId,
            new RoutingRouteSnapshotReference(
                1, Guid.NewGuid(), 1, "Discord provider route", Guid.NewGuid(), 0, 0),
            RoutingDestinationKind.Discord,
            connectionId,
            output,
            output,
            RoutingMissingOutputBehavior.UseOriginal,
            RoutingDeliveryMode.Automatic,
            new RoutingDeliverySettings(
                "A routed highlight",
                null,
                null,
                RoutingVisibility.Unspecified,
                false),
            artifactReady: true,
            intentionalDuplicate: null,
            DateTimeOffset.UtcNow);
    }

    private static RoutingResolvedArtifact Artifact(string clipPath) =>
        new(
            clipPath,
            Path.GetFileName(clipPath),
            "Test Game",
            new FileInfo(clipPath).Length,
            new string('a', 64));

    private static AppSettings Settings(string clipsFolder, string webhook) =>
        new(
            clipsFolder,
            webhook,
            false,
            AppSettings.DefaultCompressionTargetMb,
            "Routing Tester",
            true);

    private static HttpResponseMessage Response(HttpStatusCode status, string? json)
    {
        var response = new HttpResponseMessage(status);
        response.Content = json is null
            ? new ByteArrayContent([])
            : new StringContent(json, Encoding.UTF8, "application/json");
        return response;
    }

    private static string ResultText(RoutingDeliveryAttemptResult result, string connectionId) =>
        string.Join('|',
            connectionId,
            result.RemoteReceiptReference,
            result.ErrorCode,
            result.ProviderResumeReference);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send) : HttpMessageHandler
    {
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(send(request, cancellationToken));
        }
    }

    private sealed record CatalogFixture(
        DiscordConnectionCatalog Catalog,
        DiscordConnectionCatalogStore Store);
}
