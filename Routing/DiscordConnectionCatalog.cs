using System.Security.Cryptography;
using System.Text;

namespace ClipsToDiscord;

internal enum DiscordConnectionHealth
{
    Ready,
    NeedsAttention
}

internal sealed record DiscordConnectionSummary(
    string ConnectionId,
    string DisplayName,
    DiscordConnectionHealth Health,
    bool ImportedFromLegacySettings,
    DateTimeOffset UpdatedUtc)
{
    public override string ToString() =>
        $"DiscordConnectionSummary {{ {ConnectionId}, {DisplayName}, {Health} }}";
}

internal sealed record DiscordConnectionCatalogSnapshot(
    RoutingDocumentLoadStatus StoreStatus,
    long Generation,
    IReadOnlyList<DiscordConnectionSummary> Connections)
{
    internal bool IsUsable => StoreStatus is
        RoutingDocumentLoadStatus.Missing or RoutingDocumentLoadStatus.Loaded;
}

internal enum DiscordConnectionMutationStatus
{
    Added,
    Updated,
    Removed,
    AlreadyExists,
    NotFound,
    InUse,
    InvalidInput,
    CredentialReplacementRequired,
    StateUnavailable,
    Conflict
}

internal sealed record DiscordConnectionMutationResult(
    DiscordConnectionMutationStatus Status,
    string Reason,
    DiscordConnectionSummary? Connection)
{
    internal bool Succeeded => Status is
        DiscordConnectionMutationStatus.Added or
        DiscordConnectionMutationStatus.Updated or
        DiscordConnectionMutationStatus.Removed or
        DiscordConnectionMutationStatus.AlreadyExists;
}

internal sealed record DiscordConnectionSecretRecord(
    string ConnectionId,
    string DisplayName,
    string ProtectedWebhook,
    string? LegacyWebhookIdentity,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    public override string ToString() =>
        $"DiscordConnectionSecretRecord {{ {ConnectionId}, {DisplayName}, redacted }}";
}

internal sealed record DiscordConnectionCatalogDocument(
    int SchemaVersion,
    long Generation,
    IReadOnlyList<DiscordConnectionSecretRecord> Connections,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

internal static class DiscordConnectionCatalogModel
{
    internal const int MaximumConnections = 32;
    internal const int MaximumDisplayNameLength = 80;
    internal const int MaximumWebhookLength = 4_096;
    internal const int MaximumProtectedSecretBytes = 16 * 1024;

    internal static DiscordConnectionCatalogDocument CreateEmpty(DateTimeOffset? now = null)
    {
        var timestamp = RoutingValidation.Utc(now ?? DateTimeOffset.UtcNow);
        return new DiscordConnectionCatalogDocument(
            DiscordConnectionCatalogStore.CurrentSchemaVersion,
            Generation: 1,
            Connections: [],
            CreatedUtc: timestamp,
            UpdatedUtc: timestamp);
    }

    internal static DiscordConnectionCatalogDocument ReplaceConnections(
        DiscordConnectionCatalogDocument current,
        IReadOnlyList<DiscordConnectionSecretRecord> connections,
        DateTimeOffset now)
    {
        Validate(current);
        ArgumentNullException.ThrowIfNull(connections);
        var candidate = current with
        {
            Generation = RoutingValidation.NextGeneration(current.Generation),
            Connections = connections.ToArray(),
            UpdatedUtc = RoutingValidation.Utc(now)
        };
        ValidateSuccessor(current, candidate);
        return candidate;
    }

    internal static void Validate(DiscordConnectionCatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RoutingValidation.Require(
            document.SchemaVersion == DiscordConnectionCatalogStore.CurrentSchemaVersion,
            "The Discord connection catalog schema is unsupported.");
        RoutingValidation.Require(document.Generation > 0,
            "The Discord connection catalog generation is invalid.");
        RoutingValidation.RequireUtc(document.CreatedUtc,
            "Discord connection catalog creation timestamp");
        RoutingValidation.RequireUtc(document.UpdatedUtc,
            "Discord connection catalog update timestamp");
        RoutingValidation.Require(document.UpdatedUtc >= document.CreatedUtc,
            "The Discord connection catalog timestamps are inconsistent.");
        var connections = document.Connections ??
            throw new InvalidDataException("The Discord connection collection is missing.");
        RoutingValidation.Require(connections.Count <= MaximumConnections,
            "The Discord connection catalog contains too many connections.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var legacyIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connection in connections)
        {
            if (connection is null)
                throw new InvalidDataException("A Discord connection record is missing.");
            RoutingValidation.RequireOpaqueId(
                connection.ConnectionId, 128, "Discord connection id");
            RoutingValidation.Require(
                connection.ConnectionId.StartsWith("discord.", StringComparison.Ordinal) &&
                connection.ConnectionId.Length == "discord.".Length + 32 &&
                connection.ConnectionId["discord.".Length..].All(character =>
                    character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
                ids.Add(connection.ConnectionId),
                "Discord connection ids must use the canonical random Discord namespace and be unique.");
            RoutingValidation.RequireText(
                connection.DisplayName, 1, MaximumDisplayNameLength,
                "Discord connection display name");
            RoutingValidation.Require(
                connection.DisplayName == connection.DisplayName.Trim(),
                "The Discord connection display name is not canonical.");
            RoutingValidation.RequireUtc(connection.CreatedUtc,
                "Discord connection creation timestamp");
            RoutingValidation.RequireUtc(connection.UpdatedUtc,
                "Discord connection update timestamp");
            RoutingValidation.Require(
                connection.CreatedUtc >= document.CreatedUtc &&
                connection.UpdatedUtc >= connection.CreatedUtc &&
                connection.UpdatedUtc <= document.UpdatedUtc,
                "The Discord connection timestamps are outside the catalog history.");
            RoutingValidation.Require(
                TryDecodeProtectedSecret(connection.ProtectedWebhook, out var protectedBytes) &&
                protectedBytes is > 0 and <= MaximumProtectedSecretBytes,
                "The protected Discord credential is invalid.");
            if (connection.LegacyWebhookIdentity is not null)
            {
                RoutingValidation.Require(
                    connection.LegacyWebhookIdentity.StartsWith("discord.", StringComparison.Ordinal) &&
                    connection.LegacyWebhookIdentity.Length == "discord.".Length + 64 &&
                    connection.LegacyWebhookIdentity["discord.".Length..].All(Uri.IsHexDigit) &&
                    legacyIdentities.Add(connection.LegacyWebhookIdentity),
                    "The legacy Discord connection identity is invalid or duplicated.");
            }
        }
    }

    internal static void ValidateSuccessor(
        DiscordConnectionCatalogDocument current,
        DiscordConnectionCatalogDocument candidate)
    {
        Validate(current);
        Validate(candidate);
        RoutingValidation.Require(
            candidate.Generation == checked(current.Generation + 1) &&
            candidate.CreatedUtc == current.CreatedUtc &&
            candidate.UpdatedUtc >= current.UpdatedUtc,
            "The Discord connection catalog successor is inconsistent.");
        var currentById = current.Connections.ToDictionary(
            item => item.ConnectionId, StringComparer.Ordinal);
        foreach (var connection in candidate.Connections)
        {
            if (!currentById.TryGetValue(connection.ConnectionId, out var previous))
            {
                RoutingValidation.Require(
                    connection.CreatedUtc == connection.UpdatedUtc &&
                    connection.CreatedUtc >= current.UpdatedUtc,
                    "A new Discord connection has invalid timestamps.");
                continue;
            }
            RoutingValidation.Require(
                connection.CreatedUtc == previous.CreatedUtc &&
                connection.UpdatedUtc >= previous.UpdatedUtc &&
                (previous.LegacyWebhookIdentity is null ||
                 connection.LegacyWebhookIdentity == previous.LegacyWebhookIdentity),
                "A Discord connection changed immutable history.");
        }
    }

    internal static void ValidateInitial(DiscordConnectionCatalogDocument document)
    {
        Validate(document);
        RoutingValidation.Require(
            document.Generation == 1 &&
            document.Connections.Count == 0 &&
            document.CreatedUtc == document.UpdatedUtc,
            "The initial Discord connection catalog must be empty.");
    }

    private static bool TryDecodeProtectedSecret(string? value, out int length)
    {
        length = 0;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumProtectedSecretBytes * 2)
            return false;
        try
        {
            length = Convert.FromBase64String(value).Length;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal sealed class DiscordConnectionCatalogStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumDocumentBytes = 256 * 1024;
    internal const string FileName = "discord-connections.json";

    private readonly RoutingAtomicJsonStore<DiscordConnectionCatalogDocument> _store;

    internal DiscordConnectionCatalogStore()
        : this(System.IO.Path.Combine(SettingsStore.DataDirectory, "routing", FileName))
    {
    }

    internal DiscordConnectionCatalogStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = System.IO.Path.GetFullPath(path);
        if (!System.IO.Path.GetFileName(canonical).Equals(FileName, StringComparison.Ordinal))
            throw new ArgumentException(
                $"The Discord connection catalog must be named {FileName}.", nameof(path));
        _store = new RoutingAtomicJsonStore<DiscordConnectionCatalogDocument>(
            canonical,
            MaximumDocumentBytes,
            CurrentSchemaVersion,
            document => document.SchemaVersion,
            document => document.Generation,
            DiscordConnectionCatalogModel.Validate,
            DiscordConnectionCatalogModel.ValidateSuccessor,
            DiscordConnectionCatalogModel.ValidateInitial);
    }

    internal string Path => _store.Path;

    internal RoutingDocumentLoadResult<DiscordConnectionCatalogDocument> Load(
        CancellationToken cancellationToken = default) => _store.Load(cancellationToken);

    internal Task<DiscordConnectionCatalogDocument> SaveAsync(
        DiscordConnectionCatalogDocument document,
        long expectedGeneration,
        CancellationToken cancellationToken = default) =>
        _store.SaveAsync(document, expectedGeneration, cancellationToken);

    internal Task<DiscordConnectionCatalogDocument> LoadOrCreateAsync(
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default) =>
        _store.LoadOrCreateAsync(
            () => DiscordConnectionCatalogModel.CreateEmpty(now), cancellationToken);
}

internal interface IDiscordWebhookProtector
{
    string Protect(string webhookUrl, string connectionId);
    bool TryUnprotect(string protectedWebhook, string connectionId, out string webhookUrl);
}

internal sealed class CurrentUserDiscordWebhookProtector : IDiscordWebhookProtector
{
    private const string Purpose = "ClipCord.DiscordConnection.v1";

    public string Protect(string webhookUrl, string connectionId)
    {
        if (webhookUrl.Length > DiscordConnectionCatalogModel.MaximumWebhookLength ||
            !WebhookValidation.IsDiscordWebhook(webhookUrl))
            throw new ArgumentException("The Discord webhook is invalid.", nameof(webhookUrl));
        RoutingValidation.RequireOpaqueId(connectionId, 128, "Discord connection id");
        var normalized = webhookUrl.Trim();
        SensitiveDataRedactor.RegisterSecret(normalized);
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(normalized),
            Entropy(connectionId),
            DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public bool TryUnprotect(
        string protectedWebhook,
        string connectionId,
        out string webhookUrl)
    {
        webhookUrl = string.Empty;
        try
        {
            RoutingValidation.RequireOpaqueId(connectionId, 128, "Discord connection id");
            var protectedBytes = Convert.FromBase64String(protectedWebhook);
            var plaintext = ProtectedData.Unprotect(
                protectedBytes,
                Entropy(connectionId),
                DataProtectionScope.CurrentUser);
            var candidate = Encoding.UTF8.GetString(plaintext).Trim();
            if (candidate.Length > DiscordConnectionCatalogModel.MaximumWebhookLength ||
                !WebhookValidation.IsDiscordWebhook(candidate)) return false;
            SensitiveDataRedactor.RegisterSecret(candidate);
            webhookUrl = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is CryptographicException or FormatException or
                ArgumentException or InvalidDataException)
        {
            return false;
        }
    }

    private static byte[] Entropy(string connectionId) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{Purpose}\0{connectionId}"));
}

internal enum DiscordConnectionReferenceStatus
{
    NotReferenced,
    Referenced,
    StateUnavailable
}

/// <summary>
/// Conservative removal/update guard. A connection credential cannot be redirected or removed
/// while a route or any non-terminal hot-outbox delivery still names it. Invalid persistence
/// blocks the mutation rather than guessing that the connection is unused.
/// </summary>
internal sealed class DiscordConnectionReferenceProbe
{
    private readonly RoutingSnapshotStore _routes;
    private readonly RoutingOutboxStore _outbox;

    internal DiscordConnectionReferenceProbe()
        : this(new RoutingSnapshotStore(), new RoutingOutboxStore())
    {
    }

    internal DiscordConnectionReferenceProbe(
        RoutingSnapshotStore routes,
        RoutingOutboxStore outbox)
    {
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    }

    internal DiscordConnectionReferenceStatus Inspect(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        RoutingValidation.RequireOpaqueId(connectionId, 128, "Discord connection id");
        var routes = _routes.Load(cancellationToken);
        if (routes.Status != RoutingDocumentLoadStatus.Missing && !routes.LoadedFromDisk)
            return DiscordConnectionReferenceStatus.StateUnavailable;
        if (routes.Document?.Routes.Any(route =>
                route.Actions.Any(action =>
                    action.Kind == RoutingActionKind.Deliver &&
                    string.Equals(action.ConnectionId, connectionId,
                        StringComparison.Ordinal))) == true)
        {
            return DiscordConnectionReferenceStatus.Referenced;
        }

        var outbox = _outbox.Load(cancellationToken);
        if (outbox.Status != RoutingDocumentLoadStatus.Missing && !outbox.LoadedFromDisk)
            return DiscordConnectionReferenceStatus.StateUnavailable;
        return outbox.Document?.Deliveries.Any(delivery =>
                   string.Equals(delivery.ConnectionId, connectionId,
                       StringComparison.Ordinal) &&
                   !IsTerminal(delivery.State)) == true
            ? DiscordConnectionReferenceStatus.Referenced
            : DiscordConnectionReferenceStatus.NotReferenced;
    }

    private static bool IsTerminal(PlannedDeliveryState state) => state is
        PlannedDeliveryState.Delivered or
        PlannedDeliveryState.Skipped or
        PlannedDeliveryState.Cancelled or
        PlannedDeliveryState.Expired;
}

/// <summary>
/// The catalog persists ciphertext and non-secret metadata only. All mutations are generation-CAS
/// operations; connection ids remain stable across a safe credential update.
/// </summary>
internal sealed class DiscordConnectionCatalog : IRoutingConnectionMembership
{
    private const string DefaultLegacyDisplayName = "Friends server";
    private readonly DiscordConnectionCatalogStore _store;
    private readonly IDiscordWebhookProtector _protector;
    private readonly DiscordConnectionReferenceProbe _references;
    private readonly Func<Guid> _idFactory;

    internal DiscordConnectionCatalog()
        : this(
            new DiscordConnectionCatalogStore(),
            new CurrentUserDiscordWebhookProtector(),
            new DiscordConnectionReferenceProbe(),
            Guid.NewGuid)
    {
    }

    internal DiscordConnectionCatalog(
        DiscordConnectionCatalogStore store,
        IDiscordWebhookProtector protector,
        DiscordConnectionReferenceProbe references,
        Func<Guid>? idFactory = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _references = references ?? throw new ArgumentNullException(nameof(references));
        _idFactory = idFactory ?? Guid.NewGuid;
    }

    internal DiscordConnectionCatalogSnapshot Inspect(
        CancellationToken cancellationToken = default)
    {
        var loaded = _store.Load(cancellationToken);
        if (!loaded.LoadedFromDisk)
        {
            return new DiscordConnectionCatalogSnapshot(
                loaded.Status,
                Generation: 0,
                Connections: []);
        }
        return new DiscordConnectionCatalogSnapshot(
            RoutingDocumentLoadStatus.Loaded,
            loaded.Document!.Generation,
            loaded.Document.Connections.Select(ToSummary).ToArray());
    }

    bool IRoutingConnectionMembership.IsReady(
        RoutingDestinationKind destination,
        string connectionId,
        CancellationToken cancellationToken)
    {
        if (destination != RoutingDestinationKind.Discord ||
            !TryValidateConnectionId(connectionId))
        {
            return false;
        }
        var snapshot = Inspect(cancellationToken);
        return snapshot.IsUsable && snapshot.Connections.Any(connection =>
            connection.ConnectionId.Equals(connectionId, StringComparison.Ordinal) &&
            connection.Health == DiscordConnectionHealth.Ready);
    }

    internal async Task<DiscordConnectionMutationResult> AddAsync(
        string displayName,
        string webhookUrl,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default) =>
        await AddCoreAsync(displayName, webhookUrl, legacyIdentity: null,
            now, cancellationToken).ConfigureAwait(false);

    internal async Task<DiscordConnectionMutationResult> UpdateAsync(
        string connectionId,
        string displayName,
        string webhookUrl,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeInput(displayName, webhookUrl, out var name, out var webhook) ||
            !TryValidateConnectionId(connectionId))
        {
            return InvalidInput();
        }

        for (var attempt = 0; attempt < 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiscordConnectionCatalogDocument current;
            try
            {
                current = await _store.LoadOrCreateAsync(now, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                return StateUnavailable();
            }
            var existing = current.Connections.SingleOrDefault(item =>
                item.ConnectionId.Equals(connectionId, StringComparison.Ordinal));
            if (existing is null)
                return Result(DiscordConnectionMutationStatus.NotFound,
                    "The Discord connection no longer exists.", null);
            if (!_protector.TryUnprotect(
                    existing.ProtectedWebhook, existing.ConnectionId, out var oldWebhook))
            {
                return StateUnavailable();
            }

            var credentialChanges = !string.Equals(
                oldWebhook, webhook, StringComparison.Ordinal);
            if (credentialChanges)
            {
                // A webhook is the destination identity, not a mutable password. Preserving the
                // id while replacing it could redirect a concurrently-created or frozen route.
                // Add the new webhook as a new connection, explicitly repoint routes, then remove
                // this one after the reference guard is clear.
                return Result(
                    DiscordConnectionMutationStatus.CredentialReplacementRequired,
                    "Add the new webhook as a new connection, replace route references, then remove this destination.",
                    ToSummary(existing));
            }

            var timestamp = MutationTimestamp(current, now);
            string protectedWebhook;
            try
            {
                protectedWebhook = credentialChanges
                    ? _protector.Protect(webhook, connectionId)
                    : existing.ProtectedWebhook;
            }
            catch (Exception exception) when (IsProtectionFailure(exception))
            {
                return StateUnavailable();
            }
            var changed = existing with
            {
                DisplayName = name,
                ProtectedWebhook = protectedWebhook,
                UpdatedUtc = timestamp
            };
            var next = DiscordConnectionCatalogModel.ReplaceConnections(
                current,
                current.Connections.Select(item =>
                        item.ConnectionId.Equals(connectionId, StringComparison.Ordinal)
                            ? changed
                            : item)
                    .ToArray(),
                timestamp);
            try
            {
                var saved = await _store.SaveAsync(next, current.Generation, cancellationToken)
                    .ConfigureAwait(false);
                return Result(DiscordConnectionMutationStatus.Updated,
                    "The Discord connection was updated securely.",
                    ToSummary(saved.Connections.Single(item =>
                        item.ConnectionId.Equals(connectionId, StringComparison.Ordinal))));
            }
            catch (RoutingConcurrencyException) when (attempt < 5)
            {
                // Reload the winning generation and re-check references before changing a secret.
            }
            catch (RoutingConcurrencyException)
            {
                return Conflict();
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                return StateUnavailable();
            }
        }
        return Conflict();
    }

    internal async Task<DiscordConnectionMutationResult> RemoveAsync(
        string connectionId,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateConnectionId(connectionId)) return InvalidInput();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = _references.Inspect(connectionId, cancellationToken);
            if (reference == DiscordConnectionReferenceStatus.Referenced)
            {
                return Result(DiscordConnectionMutationStatus.InUse,
                    "Disable or replace every route and resolve pending deliveries before removing this destination.",
                    null);
            }
            if (reference == DiscordConnectionReferenceStatus.StateUnavailable)
                return StateUnavailable();

            DiscordConnectionCatalogDocument current;
            try
            {
                current = await _store.LoadOrCreateAsync(now, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                return StateUnavailable();
            }
            var existing = current.Connections.SingleOrDefault(item =>
                item.ConnectionId.Equals(connectionId, StringComparison.Ordinal));
            if (existing is null)
                return Result(DiscordConnectionMutationStatus.NotFound,
                    "The Discord connection no longer exists.", null);
            var timestamp = MutationTimestamp(current, now);
            var next = DiscordConnectionCatalogModel.ReplaceConnections(
                current,
                current.Connections.Where(item =>
                        !item.ConnectionId.Equals(connectionId, StringComparison.Ordinal))
                    .ToArray(),
                timestamp);
            try
            {
                await _store.SaveAsync(next, current.Generation, cancellationToken)
                    .ConfigureAwait(false);
                return Result(DiscordConnectionMutationStatus.Removed,
                    "The Discord connection was removed.", ToSummary(existing));
            }
            catch (RoutingConcurrencyException) when (attempt < 5)
            {
                // Recheck every reference after a competing mutation wins.
            }
            catch (RoutingConcurrencyException)
            {
                return Conflict();
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                return StateUnavailable();
            }
        }
        return Conflict();
    }

    internal Task<DiscordConnectionMutationResult> EnsureLegacyConnectionAsync(
        AppSettings settings,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.UploadToDiscord ||
            !DiscordRoutingConnectionIdentity.TryCreate(
                settings.WebhookUrl, out var legacyIdentity))
        {
            return Task.FromResult(InvalidInput());
        }
        return AddCoreAsync(
            DefaultLegacyDisplayName,
            settings.WebhookUrl,
            legacyIdentity,
            now,
            cancellationToken);
    }

    /// <summary>
    /// Returns only the imported connection bound to the current legacy webhook. Other user-added
    /// Discord connections must not make the one-destination 1.x cutover appear ambiguous.
    /// </summary>
    internal IReadOnlyList<string> GetLegacyConnectionIds(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.UploadToDiscord ||
            !DiscordRoutingConnectionIdentity.TryCreate(
                settings.WebhookUrl, out var legacyIdentity))
        {
            return [];
        }
        var loaded = _store.Load(cancellationToken);
        if (!loaded.LoadedFromDisk) return [];
        var matches = loaded.Document!.Connections.Where(item =>
                string.Equals(item.LegacyWebhookIdentity, legacyIdentity,
                    StringComparison.Ordinal) &&
                _protector.TryUnprotect(
                    item.ProtectedWebhook, item.ConnectionId, out var webhook) &&
                string.Equals(
                    NormalizeWebhook(webhook), NormalizeWebhook(settings.WebhookUrl),
                    StringComparison.Ordinal))
            .Select(item => item.ConnectionId)
            .ToArray();
        return matches.Length == 1 ? matches : [];
    }

    internal DiscordRoutingConnectionResolution ResolveForRouting(
        string connectionId,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!TryValidateConnectionId(connectionId) ||
            settings.CompressionTargetMb is < 1 or > 100)
        {
            return DiscordRoutingConnectionResolution.Unavailable;
        }
        var loaded = _store.Load(cancellationToken);
        if (!loaded.LoadedFromDisk)
            return DiscordRoutingConnectionResolution.Unavailable;
        var connection = loaded.Document!.Connections.SingleOrDefault(item =>
            item.ConnectionId.Equals(connectionId, StringComparison.Ordinal));
        if (connection is null)
            return DiscordRoutingConnectionResolution.Mismatch;
        return _protector.TryUnprotect(
            connection.ProtectedWebhook, connection.ConnectionId, out var webhook)
            ? DiscordRoutingConnectionResolution.Resolved(
                new DiscordRoutingConnection(webhook, settings))
            : DiscordRoutingConnectionResolution.Unavailable;
    }

    private async Task<DiscordConnectionMutationResult> AddCoreAsync(
        string displayName,
        string webhookUrl,
        string? legacyIdentity,
        DateTimeOffset? now,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeInput(displayName, webhookUrl, out var name, out var webhook))
            return InvalidInput();
        if (legacyIdentity is not null && !TryValidateConnectionId(legacyIdentity))
            return InvalidInput();

        for (var attempt = 0; attempt < 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiscordConnectionCatalogDocument current;
            try
            {
                current = await _store.LoadOrCreateAsync(now, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                return StateUnavailable();
            }

            foreach (var existing in current.Connections)
            {
                if (legacyIdentity is not null && string.Equals(
                        existing.LegacyWebhookIdentity, legacyIdentity,
                        StringComparison.Ordinal))
                {
                    if (!_protector.TryUnprotect(
                            existing.ProtectedWebhook, existing.ConnectionId,
                            out var existingWebhook) ||
                        !string.Equals(existingWebhook, webhook, StringComparison.Ordinal))
                    {
                        return StateUnavailable();
                    }
                    return Result(DiscordConnectionMutationStatus.AlreadyExists,
                        "The existing encrypted Discord destination is already available to Routes.",
                        ToSummary(existing));
                }
                if (_protector.TryUnprotect(
                        existing.ProtectedWebhook, existing.ConnectionId,
                        out var duplicateWebhook) &&
                    string.Equals(duplicateWebhook, webhook, StringComparison.Ordinal))
                {
                    if (legacyIdentity is null ||
                        existing.LegacyWebhookIdentity == legacyIdentity)
                    {
                        return Result(DiscordConnectionMutationStatus.AlreadyExists,
                            "This Discord destination is already connected.",
                            ToSummary(existing));
                    }
                    var marked = await MarkLegacyIdentityAsync(
                            current, existing, legacyIdentity, now,
                            cancellationToken, attempt)
                        .ConfigureAwait(false);
                    if (marked is not null) return marked;
                    continue;
                }
            }

            if (current.Connections.Count >= DiscordConnectionCatalogModel.MaximumConnections)
            {
                return Result(DiscordConnectionMutationStatus.InvalidInput,
                    "The maximum number of Discord connections has been reached.", null);
            }
            var connectionId = CreateUniqueId(current);
            var timestamp = MutationTimestamp(current, now);
            string protectedWebhook;
            try
            {
                protectedWebhook = _protector.Protect(webhook, connectionId);
            }
            catch (Exception exception) when (IsProtectionFailure(exception))
            {
                return StateUnavailable();
            }
            var added = new DiscordConnectionSecretRecord(
                connectionId,
                name,
                protectedWebhook,
                legacyIdentity,
                timestamp,
                timestamp);
            var next = DiscordConnectionCatalogModel.ReplaceConnections(
                current, current.Connections.Concat([added]).ToArray(), timestamp);
            try
            {
                var saved = await _store.SaveAsync(next, current.Generation, cancellationToken)
                    .ConfigureAwait(false);
                return Result(DiscordConnectionMutationStatus.Added,
                    "The Discord connection was encrypted for this Windows account.",
                    ToSummary(saved.Connections.Single(item =>
                        item.ConnectionId.Equals(connectionId, StringComparison.Ordinal))));
            }
            catch (RoutingConcurrencyException) when (attempt < 5)
            {
                // Reload and detect a concurrent duplicate before trying another id.
            }
            catch (RoutingConcurrencyException)
            {
                return Conflict();
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                return StateUnavailable();
            }
        }
        return Conflict();
    }

    private async Task<DiscordConnectionMutationResult?> MarkLegacyIdentityAsync(
        DiscordConnectionCatalogDocument current,
        DiscordConnectionSecretRecord existing,
        string legacyIdentity,
        DateTimeOffset? now,
        CancellationToken cancellationToken,
        int attempt)
    {
        var timestamp = MutationTimestamp(current, now);
        var changed = existing with
        {
            LegacyWebhookIdentity = legacyIdentity,
            UpdatedUtc = timestamp
        };
        var next = DiscordConnectionCatalogModel.ReplaceConnections(
            current,
            current.Connections.Select(item =>
                    item.ConnectionId.Equals(existing.ConnectionId, StringComparison.Ordinal)
                        ? changed
                        : item)
                .ToArray(),
            timestamp);
        try
        {
            var saved = await _store.SaveAsync(next, current.Generation, cancellationToken)
                .ConfigureAwait(false);
            return Result(DiscordConnectionMutationStatus.AlreadyExists,
                "The existing encrypted Discord destination is now linked to the legacy route.",
                ToSummary(saved.Connections.Single(item =>
                    item.ConnectionId.Equals(existing.ConnectionId, StringComparison.Ordinal))));
        }
        catch (RoutingConcurrencyException) when (attempt < 5)
        {
            return null;
        }
        catch (RoutingConcurrencyException)
        {
            return null;
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return StateUnavailable();
        }
    }

    private string CreateUniqueId(DiscordConnectionCatalogDocument document)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var value = _idFactory();
            if (value == Guid.Empty) continue;
            var candidate = "discord." + value.ToString("N");
            if (document.Connections.All(item =>
                    !item.ConnectionId.Equals(candidate, StringComparison.Ordinal)))
                return candidate;
        }
        throw new InvalidOperationException("A unique Discord connection id could not be created.");
    }

    private DiscordConnectionSummary ToSummary(DiscordConnectionSecretRecord record) =>
        new(
            record.ConnectionId,
            record.DisplayName,
            _protector.TryUnprotect(
                record.ProtectedWebhook, record.ConnectionId, out _)
                ? DiscordConnectionHealth.Ready
                : DiscordConnectionHealth.NeedsAttention,
            record.LegacyWebhookIdentity is not null,
            record.UpdatedUtc);

    private static bool TryNormalizeInput(
        string displayName,
        string webhookUrl,
        out string normalizedName,
        out string normalizedWebhook)
    {
        normalizedName = (displayName ?? string.Empty).Trim();
        normalizedWebhook = NormalizeWebhook(webhookUrl);
        return normalizedName.Length is > 0 and <=
                   DiscordConnectionCatalogModel.MaximumDisplayNameLength &&
               normalizedName.All(character => !char.IsControl(character)) &&
               normalizedWebhook.Length <=
                   DiscordConnectionCatalogModel.MaximumWebhookLength &&
               WebhookValidation.IsDiscordWebhook(normalizedWebhook);
    }

    private static string NormalizeWebhook(string? webhookUrl)
    {
        var candidate = (webhookUrl ?? string.Empty).Trim();
        if (!WebhookValidation.IsDiscordWebhook(candidate)) return candidate;
        try
        {
            var uri = new Uri(candidate, UriKind.Absolute);
            return new UriBuilder(uri)
            {
                Fragment = string.Empty,
                Path = uri.AbsolutePath.TrimEnd('/')
            }.Uri.AbsoluteUri;
        }
        catch (UriFormatException)
        {
            return candidate;
        }
    }

    private static DateTimeOffset MutationTimestamp(
        DiscordConnectionCatalogDocument current,
        DateTimeOffset? requested)
    {
        var timestamp = RoutingValidation.Utc(requested ?? DateTimeOffset.UtcNow);
        return timestamp < current.UpdatedUtc ? current.UpdatedUtc : timestamp;
    }

    private static bool TryValidateConnectionId(string? connectionId)
    {
        try
        {
            RoutingValidation.RequireOpaqueId(
                connectionId, 128, "Discord connection id");
            return connectionId!.StartsWith("discord.", StringComparison.Ordinal);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool IsProtectionFailure(Exception exception) => exception is
        CryptographicException or ArgumentException or InvalidDataException;

    private static bool IsPersistenceFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or InvalidDataException or
        CryptographicException;

    private static DiscordConnectionMutationResult InvalidInput() =>
        Result(DiscordConnectionMutationStatus.InvalidInput,
            "Enter a name and a valid Discord webhook.", null);

    private static DiscordConnectionMutationResult StateUnavailable() =>
        Result(DiscordConnectionMutationStatus.StateUnavailable,
            "The encrypted Discord connection state is unavailable. No change was made.", null);

    private static DiscordConnectionMutationResult Conflict() =>
        Result(DiscordConnectionMutationStatus.Conflict,
            "The Discord connection state changed. No credential was overwritten.", null);

    private static DiscordConnectionMutationResult Result(
        DiscordConnectionMutationStatus status,
        string reason,
        DiscordConnectionSummary? connection) => new(status, reason, connection);
}

internal enum LegacyDiscordConnectionCutoverStatus
{
    Completed,
    ConnectionUnavailable
}

internal sealed record LegacyDiscordConnectionCutoverResult(
    LegacyDiscordConnectionCutoverStatus Status,
    string Reason,
    DiscordConnectionSummary? Connection,
    LegacyRoutingCutoverResult? Cutover)
{
    /// <summary>
    /// The adapter never changes runtime ownership. The caller may consider a transfer only after
    /// the existing coordinator has durably committed both the exact route and marker.
    /// </summary>
    internal bool MayReleaseLegacyOwnership =>
        Status == LegacyDiscordConnectionCutoverStatus.Completed &&
        Cutover is { IsCommitted: true, Marker.Phase: LegacyRoutingMigrationMarkerPhase.Committed };
}

/// <summary>
/// Imports the current DPAPI-backed 1.x webhook into the connection catalog before running the
/// existing two-phase route cutover. A crash can leave an unused encrypted connection, but never
/// disables the legacy worker or exposes a route whose credential is absent.
/// </summary>
internal sealed class LegacyDiscordConnectionCutoverAdapter
{
    private readonly DiscordConnectionCatalog _connections;
    private readonly LegacyRoutingMigrationCoordinator _migration;

    internal LegacyDiscordConnectionCutoverAdapter(
        DiscordConnectionCatalog connections,
        LegacyRoutingMigrationCoordinator migration)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _migration = migration ?? throw new ArgumentNullException(nameof(migration));
    }

    internal async Task<LegacyDiscordConnectionCutoverResult> ExecuteAsync(
        AppSettings settings,
        WatchState watchState,
        bool legacyWorkerQuiesced,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(watchState);
        DiscordConnectionSummary? connection = null;
        IReadOnlyList<string> connectionIds = [];
        if (settings.UploadToDiscord)
        {
            var imported = await _connections.EnsureLegacyConnectionAsync(
                    settings, now, cancellationToken)
                .ConfigureAwait(false);
            if (!imported.Succeeded || imported.Connection is null ||
                imported.Connection.Health != DiscordConnectionHealth.Ready)
            {
                return new LegacyDiscordConnectionCutoverResult(
                    LegacyDiscordConnectionCutoverStatus.ConnectionUnavailable,
                    "The existing Discord destination could not be imported securely. Legacy processing remains authoritative.",
                    imported.Connection,
                    Cutover: null);
            }
            connection = imported.Connection;
            connectionIds = [connection.ConnectionId];
        }

        var cutover = await _migration.ExecuteAsync(
                new LegacyRoutingMigrationInput(
                    settings,
                    watchState,
                    legacyWorkerQuiesced,
                    connectionIds),
                now,
                cancellationToken)
            .ConfigureAwait(false);
        return new LegacyDiscordConnectionCutoverResult(
            LegacyDiscordConnectionCutoverStatus.Completed,
            cutover.Reason,
            connection,
            cutover);
    }
}
