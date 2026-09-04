using System.Security.Cryptography;
using System.Text;

namespace ClipsToDiscord;

/// <summary>
/// Stable, non-secret identity for the one legacy Discord connection. The webhook itself remains
/// in the DPAPI-backed settings store; routes and outbox records retain only this digest.
/// </summary>
internal static class DiscordRoutingConnectionIdentity
{
    private const string Prefix = "discord.";

    internal static bool TryCreate(string? webhookUrl, out string connectionId)
    {
        connectionId = string.Empty;
        var normalized = webhookUrl?.Trim() ?? string.Empty;
        if (!WebhookValidation.IsDiscordWebhook(normalized)) return false;

        try
        {
            var uri = new Uri(normalized, UriKind.Absolute);
            var builder = new UriBuilder(uri)
            {
                Fragment = string.Empty,
                Path = uri.AbsolutePath.TrimEnd('/')
            };
            var requestIdentity = DiscordWebhookClient.WithWait(builder.Uri.AbsoluteUri);
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(requestIdentity));
            connectionId = Prefix + Convert.ToHexString(digest).ToLowerInvariant();
            return true;
        }
        catch (Exception exception) when (
            exception is UriFormatException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}

internal enum DiscordRoutingConnectionResolutionStatus
{
    Resolved,
    Unavailable,
    Mismatch
}

/// <summary>A short-lived in-memory view. ToString is deliberately redacted.</summary>
internal sealed class DiscordRoutingConnection
{
    internal DiscordRoutingConnection(string webhookUrl, AppSettings settings)
    {
        WebhookUrl = webhookUrl;
        CompressionTargetMb = settings.CompressionTargetMb;
        UploaderName = AppSettings.NormalizeUploaderName(settings.UploaderName);
    }

    internal string WebhookUrl { get; }
    internal int CompressionTargetMb { get; }
    internal string UploaderName { get; }

    public override string ToString() => "DiscordRoutingConnection { redacted }";
}

internal sealed class DiscordRoutingConnectionResolution
{
    private DiscordRoutingConnectionResolution(
        DiscordRoutingConnectionResolutionStatus status,
        DiscordRoutingConnection? connection)
    {
        Status = status;
        Connection = connection;
    }

    internal DiscordRoutingConnectionResolutionStatus Status { get; }
    internal DiscordRoutingConnection? Connection { get; }

    internal static DiscordRoutingConnectionResolution Resolved(
        DiscordRoutingConnection connection) =>
        new(DiscordRoutingConnectionResolutionStatus.Resolved, connection);

    internal static DiscordRoutingConnectionResolution Unavailable { get; } =
        new(DiscordRoutingConnectionResolutionStatus.Unavailable, null);

    internal static DiscordRoutingConnectionResolution Mismatch { get; } =
        new(DiscordRoutingConnectionResolutionStatus.Mismatch, null);
}

/// <summary>
/// Re-reads encrypted connection state for every provider attempt. Random catalog ids resolve
/// only through the DPAPI-backed catalog; legacy SHA-256 ids retain the settings-backed path.
/// A missing or damaged catalog record is never reinterpreted as a legacy destination.
/// </summary>
internal sealed class DiscordRoutingConnectionResolver
{
    private readonly Func<AppSettings> _settingsProvider;
    private readonly DiscordConnectionCatalog? _catalog;

    internal DiscordRoutingConnectionResolver()
        : this(SettingsStore.Load, new DiscordConnectionCatalog())
    {
    }

    internal DiscordRoutingConnectionResolver(Func<AppSettings> settingsProvider)
        : this(settingsProvider, catalog: null)
    {
    }

    internal DiscordRoutingConnectionResolver(
        Func<AppSettings> settingsProvider,
        DiscordConnectionCatalog? catalog)
    {
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _catalog = catalog;
    }

    internal DiscordRoutingConnectionResolution Resolve(string connectionId)
    {
        try
        {
            var settings = _settingsProvider();
            if (settings is null || settings.CompressionTargetMb is < 1 or > 100)
            {
                return DiscordRoutingConnectionResolution.Unavailable;
            }

            if (_catalog is not null)
            {
                var catalogResolution = _catalog.ResolveForRouting(connectionId, settings);
                if (catalogResolution.Status == DiscordRoutingConnectionResolutionStatus.Resolved)
                    return catalogResolution;
                // Catalog ids use a random 128-bit suffix. If one cannot be resolved, never let
                // the legacy digest resolver reinterpret it as a different destination.
                if (IsCatalogConnectionId(connectionId))
                {
                    return catalogResolution;
                }
            }

            if (!DiscordRoutingConnectionIdentity.TryCreate(
                    settings.WebhookUrl, out var currentConnectionId))
            {
                return DiscordRoutingConnectionResolution.Unavailable;
            }

            SensitiveDataRedactor.RegisterSecret(settings.WebhookUrl);
            if (!currentConnectionId.Equals(connectionId, StringComparison.Ordinal))
            {
                return DiscordRoutingConnectionResolution.Mismatch;
            }

            return DiscordRoutingConnectionResolution.Resolved(
                new DiscordRoutingConnection(settings.WebhookUrl.Trim(), settings));
        }
        catch
        {
            return DiscordRoutingConnectionResolution.Unavailable;
        }
    }

    private static bool IsCatalogConnectionId(string? connectionId) =>
        connectionId is not null &&
        connectionId.StartsWith("discord.", StringComparison.Ordinal) &&
        connectionId.Length == "discord.".Length + 32 &&
        connectionId["discord.".Length..].All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// Discord delivery adapter. Definite local/configuration or HTTP rejections are retryable failed
/// attempts. Any successful POST without a receipt, transport interruption, or unexpected provider
/// failure is delivery-unknown so the outbox never blindly duplicates a possibly accepted post.
/// </summary>
internal sealed class DiscordRoutingProvider : IRoutingDeliveryProvider
{
    private readonly DiscordRoutingConnectionResolver _connectionResolver;
    private readonly Func<DiscordWebhookClient> _clientFactory;

    internal DiscordRoutingProvider() : this(
        new DiscordRoutingConnectionResolver(),
        static () => new DiscordWebhookClient())
    {
    }

    internal DiscordRoutingProvider(
        DiscordRoutingConnectionResolver connectionResolver,
        Func<DiscordWebhookClient> clientFactory)
    {
        _connectionResolver = connectionResolver ??
                              throw new ArgumentNullException(nameof(connectionResolver));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public bool Supports(RoutingDestinationKind destination) =>
        destination == RoutingDestinationKind.Discord;

    public async Task<RoutingDeliveryAttemptResult> SendAsync(
        PlannedDelivery delivery,
        RoutingResolvedArtifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(artifact);
        if (delivery.Destination != RoutingDestinationKind.Discord)
        {
            return RoutingDeliveryAttemptResult.Failed("discord-destination-unsupported");
        }

        var resolution = _connectionResolver.Resolve(delivery.ConnectionId);
        if (resolution.Status == DiscordRoutingConnectionResolutionStatus.Mismatch)
        {
            return RoutingDeliveryAttemptResult.Failed("discord-connection-mismatch");
        }
        if (resolution.Status != DiscordRoutingConnectionResolutionStatus.Resolved ||
            resolution.Connection is null)
        {
            return RoutingDeliveryAttemptResult.Failed("discord-connection-unavailable");
        }

        var connection = resolution.Connection;
        try
        {
            using var client = _clientFactory();
            var receipt = await client.UploadWithCompressionForReceiptAsync(
                    connection.WebhookUrl,
                    artifact.Path,
                    new DiscordUploadPresentation(
                        artifact.DisplayFileName,
                        artifact.GameName,
                        delivery.Settings.Message),
                    connection.CompressionTargetMb,
                    connection.UploaderName,
                    cancellationToken)
                .ConfigureAwait(false);
            return receipt is null
                ? RoutingDeliveryAttemptResult.Unknown("discord-receipt-missing")
                : RoutingDeliveryAttemptResult.Confirmed(receipt.Reference);
        }
        catch (DiscordUploadException)
        {
            return RoutingDeliveryAttemptResult.Failed("discord-upload-rejected");
        }
        catch (CompressionTargetUnachievableException)
        {
            return RoutingDeliveryAttemptResult.Failed("discord-compression-unavailable");
        }
        catch (FileNotFoundException)
        {
            return RoutingDeliveryAttemptResult.Failed("discord-artifact-unavailable");
        }
        catch (DirectoryNotFoundException)
        {
            return RoutingDeliveryAttemptResult.Failed("discord-artifact-unavailable");
        }
        catch (ArgumentException)
        {
            return RoutingDeliveryAttemptResult.Failed("discord-request-invalid");
        }
        catch (InvalidDataException)
        {
            return RoutingDeliveryAttemptResult.Failed("discord-request-invalid");
        }
        catch (InvalidOperationException)
        {
            return RoutingDeliveryAttemptResult.Failed("discord-upload-unavailable");
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or TimeoutException or
                HttpRequestException or IOException)
        {
            return RoutingDeliveryAttemptResult.Unknown("discord-result-unknown");
        }
        catch
        {
            return RoutingDeliveryAttemptResult.Unknown("discord-result-unknown");
        }
    }
}
