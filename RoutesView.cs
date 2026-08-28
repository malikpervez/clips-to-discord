using System.Security.Cryptography;

namespace ClipsToDiscord;

internal sealed record RoutingConnectionDisplay(
    string ConnectionId,
    string Name,
    string Detail,
    bool Available);

internal interface IRoutingConnectionViewSource
{
    IReadOnlyList<RoutingConnectionDisplay> LoadDiscordConnections();
}

internal interface IRoutingConnectionManagerViewSource : IRoutingConnectionViewSource
{
    Task<DiscordConnectionMutationResult> AddDiscordAsync(
        string displayName,
        string webhookUrl,
        CancellationToken cancellationToken = default);

    Task<DiscordConnectionMutationResult> RemoveDiscordAsync(
        string connectionId,
        CancellationToken cancellationToken = default);
}

internal sealed class LegacyDiscordConnectionViewSource : IRoutingConnectionViewSource
{
    private readonly Func<AppSettings> _settings;

    internal LegacyDiscordConnectionViewSource(Func<AppSettings> settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public IReadOnlyList<RoutingConnectionDisplay> LoadDiscordConnections()
    {
        var settings = _settings();
        if (!WebhookValidation.IsDiscordWebhook(settings.WebhookUrl) ||
            !DiscordRoutingConnectionIdentity.TryCreate(settings.WebhookUrl, out var connectionId))
        {
            return [];
        }

        return
        [
            new RoutingConnectionDisplay(
                connectionId,
                "Friends server",
                "Existing encrypted Discord webhook",
                Available: true)
        ];
    }
}

internal sealed class DiscordConnectionCatalogViewSource : IRoutingConnectionManagerViewSource
{
    private readonly DiscordConnectionCatalog _catalog;

    internal DiscordConnectionCatalogViewSource(DiscordConnectionCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public IReadOnlyList<RoutingConnectionDisplay> LoadDiscordConnections()
    {
        var snapshot = _catalog.Inspect();
        if (!snapshot.IsUsable) return [];
        return snapshot.Connections.Select(connection => new RoutingConnectionDisplay(
                connection.ConnectionId,
                connection.DisplayName,
                connection.ImportedFromLegacySettings
                    ? "Imported securely from your existing Discord destination"
                    : "Encrypted for this Windows account",
                connection.Health == DiscordConnectionHealth.Ready))
            .ToArray();
    }

    public Task<DiscordConnectionMutationResult> AddDiscordAsync(
        string displayName,
        string webhookUrl,
        CancellationToken cancellationToken = default) =>
        _catalog.AddAsync(displayName, webhookUrl, cancellationToken: cancellationToken);

    public Task<DiscordConnectionMutationResult> RemoveDiscordAsync(
        string connectionId,
        CancellationToken cancellationToken = default) =>
        _catalog.RemoveAsync(connectionId, cancellationToken: cancellationToken);
}

internal sealed class RoutesView : UserControl
{
    private readonly RoutingRouteManager _routeManager;
    private readonly IRoutingConnectionViewSource _connections;
    private readonly BrandedScrollHost _scrollHost;
    private readonly Panel _contentHost;
    private readonly OutlineButton _routesTab;
    private readonly OutlineButton _connectionsTab;
    private readonly GradientButton _newRouteButton;
    private readonly Func<bool> _isCutoverCommitted;
    private bool _showConnections;
    private bool _busy;
    private bool _cutoverCommitted;

    internal event EventHandler? OpenSettingsRequested;
    internal event EventHandler? DeliveryHistoryRequested;

    internal Control HeaderActionButton => _newRouteButton;

    internal RoutesView(
        RoutingRouteManager? routeManager = null,
        IRoutingConnectionViewSource? connections = null,
        Func<bool>? isCutoverCommitted = null)
    {
        _routeManager = routeManager ?? new RoutingRouteManager();
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _isCutoverCommitted = isCutoverCommitted ?? CreateCutoverStatusSource(_routeManager);
        Name = "RoutesView";
        Dock = DockStyle.Fill;
        BackColor = ClipCordTheme.SurfaceBase;
        DoubleBuffered = true;

        _newRouteButton = new GradientButton
        {
            Name = "NewRouteButton",
            Text = "+  New route",
            AccessibleName = "Create a new routing rule",
            Size = new Size(136, 34),
            Margin = Padding.Empty
        };
        _newRouteButton.Click += async (_, _) => await AddRouteAsync();

        _routesTab = CreateTab("Routes", selected: true);
        _connectionsTab = CreateTab("Connections", selected: false);
        _routesTab.Click += (_, _) => SelectTab(showConnections: false);
        _connectionsTab.Click += (_, _) => SelectTab(showConnections: true);

        var root = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(45)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildTabs(), 0, 0);

        _contentHost = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        _scrollHost = new BrandedScrollHost
        {
            Name = "RoutesScrollHost",
            AccessibleName = "Routing rules",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        _contentHost.Controls.Add(_scrollHost);
        root.Controls.Add(_contentHost, 0, 1);
        Controls.Add(root);
        Resize += (_, _) => RefreshViewport();
        Reload();
    }

    internal void ActivateView()
    {
        Reload();
        RefreshViewport();
    }

    internal void RefreshViewport()
    {
        _scrollHost.RefreshContentLayout();
        Invalidate(true);
    }

    private Control BuildTabs()
    {
        var host = new BufferedTableLayoutPanel
        {
            Name = "RoutesTabs",
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(ScaleLogical(28), 0, ScaleLogical(28), 0),
            BackColor = ClipCordTheme.SurfaceBase
        };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(92)));
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(124)));
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        host.Controls.Add(_routesTab, 0, 0);
        host.Controls.Add(_connectionsTab, 1, 0);
        var line = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.BorderDefault
        };
        host.Controls.Add(line, 0, 1);
        host.SetColumnSpan(line, 3);
        return host;
    }

    private OutlineButton CreateTab(string text, bool selected) => new()
    {
        Name = $"{text}RouteTab",
        Text = text,
        AccessibleRole = AccessibleRole.PageTab,
        AccessibleName = text,
        Dock = DockStyle.Fill,
        Margin = new Padding(0, 0, ScaleLogical(12), 0),
        SurfaceColor = ClipCordTheme.SurfaceBase,
        HoverColor = ClipCordTheme.SurfaceControl,
        OutlineColor = Color.Transparent,
        ForeColor = selected ? ClipCordTheme.TextPrimary : ClipCordTheme.TextSecondary,
        Font = ClipCordTheme.InterfaceFont(9.5f, selected ? FontStyle.Bold : FontStyle.Regular)
    };

    private void SelectTab(bool showConnections)
    {
        if (_showConnections == showConnections) return;
        _showConnections = showConnections;
        _routesTab.ForeColor = showConnections ? ClipCordTheme.TextSecondary : ClipCordTheme.TextPrimary;
        _routesTab.Font = ClipCordTheme.InterfaceFont(
            9.5f, showConnections ? FontStyle.Regular : FontStyle.Bold);
        _connectionsTab.ForeColor = showConnections ? ClipCordTheme.TextPrimary : ClipCordTheme.TextSecondary;
        _connectionsTab.Font = ClipCordTheme.InterfaceFont(
            9.5f, showConnections ? FontStyle.Bold : FontStyle.Regular);
        _newRouteButton.Visible = !showConnections;
        Reload();
    }

    private void Reload()
    {
        if (IsDisposed || Disposing) return;
        _cutoverCommitted = ReadCutoverStatus();
        _newRouteButton.Text = "+  New route";
        _newRouteButton.AccessibleName = "Create a new routing rule";
        _newRouteButton.Enabled = !_busy && _cutoverCommitted;
        _newRouteButton.AccessibleDescription = _cutoverCommitted
            ? "Create a new routing rule. Runtime activation is a separate explicit action."
            : "Route editing unlocks only during the final safe routing cutover.";
        SuspendLayout();
        try
        {
            _scrollHost.Content = _showConnections
                ? BuildConnectionsContent()
                : BuildRoutesContent();
            _scrollHost.RefreshContentLayout(preservePosition: false);
        }
        finally
        {
            ResumeLayout(true);
        }
    }

    private Control BuildRoutesContent()
    {
        var content = CreateContentTable("RoutesContent");
        var row = 0;
        content.Controls.Add(BuildRouteToolbar(), 0, row++);
        content.Controls.Add(BuildLibraryNotice(), 0, row++);
        content.Controls.Add(BuildCutoverNotice(), 0, row++);

        var loaded = _routeManager.Load();
        if (loaded.Status == RoutingDocumentLoadStatus.Missing)
        {
            content.Controls.Add(BuildEmptyState(), 0, row++);
            return FinishContent(content, row);
        }
        if (!loaded.LoadedFromDisk || loaded.Document is null)
        {
            content.Controls.Add(BuildUnavailableState(loaded.Status), 0, row++);
            return FinishContent(content, row);
        }

        var ordered = loaded.Document.Routes.OrderBy(route => route.Priority).ToArray();
        var specific = ordered.Where(route => route.Kind == RoutingRouteKind.Specific).ToArray();
        var fallback = ordered.Where(route => route.Kind == RoutingRouteKind.Fallback).ToArray();
        content.Controls.Add(BuildSectionHeader(
            "CONFIGURED ROUTES",
            $"{specific.Count(route => route.Enabled)} configured"), 0, row++);
        if (specific.Length == 0)
        {
            content.Controls.Add(BuildInlineEmpty(
                "No specific routes yet",
                _cutoverCommitted
                    ? "Create a route for a game, capture type, or watched-folder source."
                    : "Specific route editing remains locked while the existing watcher is authoritative."), 0, row++);
        }
        else
        {
            foreach (var route in specific)
                content.Controls.Add(BuildRouteCard(route, ordered), 0, row++);
        }

        content.Controls.Add(BuildSectionHeader(
            "FALLBACK",
            "Runs only when no specific route matches"), 0, row++);
        if (fallback.Length == 0)
        {
            content.Controls.Add(BuildInlineEmpty(
                "No fallback route",
                _cutoverCommitted
                    ? "The committed legacy fallback is missing. Routing must remain inactive until it is restored."
                    : "Safe cutover will create the fallback that preserves today’s Discord or Local-only behavior."), 0, row++);
        }
        else
        {
            foreach (var route in fallback)
                content.Controls.Add(BuildRouteCard(route, ordered), 0, row++);
        }
        return FinishContent(content, row);
    }

    private Control BuildConnectionsContent()
    {
        var content = CreateContentTable("ConnectionsContent");
        var row = 0;
        content.Controls.Add(BuildSectionHeader(
            "CONNECTED ACCOUNTS",
            "Destinations routes can safely reference"), 0, row++);
        if (_connections is IRoutingConnectionManagerViewSource)
            content.Controls.Add(BuildConnectionToolbar(), 0, row++);
        var connections = SafeLoadConnections();
        if (connections.Count == 0)
        {
            var empty = BuildInlineEmpty(
                "Discord is not connected",
                "Add and test a Discord webhook in Settings before creating a Discord route.");
            var settings = CreateSmallButton("Open Settings", 124);
            settings.Name = "OpenDiscordSettingsButton";
            settings.Click += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
            empty.Controls.Add(settings);
            settings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            settings.Location = new Point(
                Math.Max(12, empty.Width - settings.Width - 14),
                ScaleLogical(20));
            empty.Resize += (_, _) => settings.Left =
                Math.Max(12, empty.ClientSize.Width - settings.Width - ScaleLogical(14));
            content.Controls.Add(empty, 0, row++);
        }
        else
        {
            foreach (var connection in connections)
                content.Controls.Add(BuildConnectionCard(connection), 0, row++);
        }

        content.Controls.Add(BuildConnectorRoadmap(), 0, row++);
        return FinishContent(content, row);
    }

    private BufferedTableLayoutPanel CreateContentTable(string name)
    {
        var content = new BufferedTableLayoutPanel
        {
            Name = name,
            AutoSize = false,
            ColumnCount = 1,
            RowCount = 0,
            Margin = Padding.Empty,
            Padding = new Padding(
                ScaleLogical(28),
                ScaleLogical(16),
                ScaleLogical(28),
                ScaleLogical(28)),
            BackColor = ClipCordTheme.SurfaceBase,
            MinimumSize = new Size(Math.Max(1, Width), 1)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return content;
    }

    private static Control FinishContent(BufferedTableLayoutPanel content, int rows)
    {
        content.RowCount = rows;
        for (var index = 0; index < rows; index++)
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return content;
    }

    private Control BuildRouteToolbar()
    {
        var toolbar = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = ScaleLogical(38),
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, ScaleLogical(12)),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(108)));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(144)));
        toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        toolbar.Controls.Add(new Label
        {
            Text = "Matching specific routes run in order. Fallback runs only when none match.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(9f),
            AutoEllipsis = true,
            Margin = Padding.Empty
        }, 0, 0);
        var reorder = CreateSmallButton("Reorder", 96);
        reorder.Name = "ReorderRoutesButton";
        reorder.Enabled = false;
        reorder.AccessibleDescription = "Use the up and down buttons on each route.";
        toolbar.Controls.Add(reorder, 1, 0);
        var history = CreateSmallButton("Delivery history", 132);
        history.Name = "DeliveryHistoryButton";
        history.Click += (_, _) => DeliveryHistoryRequested?.Invoke(this, EventArgs.Empty);
        toolbar.Controls.Add(history, 2, 0);
        return toolbar;
    }

    private Control BuildLibraryNotice()
    {
        var notice = new RoundedPanel
        {
            Name = "RoutingLibraryNotice",
            Dock = DockStyle.Top,
            Height = ScaleLogical(48),
            BackColor = Color.FromArgb(19, 49, 47),
            BorderColor = Color.FromArgb(42, 120, 93),
            CornerRadius = ScaleLogical(9),
            Margin = new Padding(0, 0, 0, ScaleLogical(18)),
            Padding = new Padding(ScaleLogical(14), 0, ScaleLogical(14), 0)
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(29)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new FigmaIconControl
        {
            Asset = FigmaIconAsset.Disk,
            IconColor = Color.FromArgb(49, 177, 113),
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 14, 8, 14)
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "Library is always on · every source clip is filed after its route finishes and stays available in Gallery.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(190, 238, 218),
            Font = ClipCordTheme.InterfaceFont(9f),
            AutoEllipsis = true,
            Margin = Padding.Empty
        }, 1, 0);
        notice.Controls.Add(layout);
        return notice;
    }

    private Control BuildCutoverNotice()
    {
        var ready = _cutoverCommitted;
        var notice = new RoundedPanel
        {
            Name = "RoutingCutoverNotice",
            Dock = DockStyle.Top,
            Height = ScaleLogical(54),
            BackColor = ready
                ? Color.FromArgb(23, 43, 56)
                : Color.FromArgb(52, 39, 24),
            BorderColor = ready
                ? Color.FromArgb(55, 92, 119)
                : Color.FromArgb(137, 94, 36),
            CornerRadius = ScaleLogical(9),
            Margin = new Padding(0, 0, 0, ScaleLogical(18)),
            Padding = new Padding(ScaleLogical(14), 0, ScaleLogical(14), 0)
        };
        notice.Controls.Add(new Label
        {
            Name = "RoutingCutoverStatusLabel",
            Text = ready
                ? "CONFIGURATION READY · The committed fallback preserves 1.x behavior. Runtime activation remains a separate explicit step."
                : "ROUTING INACTIVE · Your existing watcher remains authoritative. Connections can be prepared now; route editing unlocks after safe cutover.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ready
                ? Color.FromArgb(181, 211, 231)
                : Color.FromArgb(236, 201, 146),
            Font = ClipCordTheme.InterfaceFont(8.75f, FontStyle.Bold),
            AutoEllipsis = true,
            Margin = Padding.Empty
        });
        return notice;
    }

    private Control BuildSectionHeader(string title, string detail)
    {
        var header = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = ScaleLogical(27),
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, ScaleLogical(2), 0, ScaleLogical(8)),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8f, FontStyle.Bold),
            Margin = Padding.Empty
        }, 0, 0);
        header.Controls.Add(new Label
        {
            Text = detail,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8.25f),
            Margin = Padding.Empty
        }, 1, 0);
        return header;
    }

    private Control BuildRouteCard(
        RoutingRoute route,
        IReadOnlyList<RoutingRoute> orderedRoutes)
    {
        var card = new RoundedPanel
        {
            Name = $"RouteCard_{route.RouteId:N}",
            Dock = DockStyle.Top,
            Height = ScaleLogical(116),
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(10),
            Margin = new Padding(0, 0, 0, ScaleLogical(10)),
            Padding = new Padding(ScaleLogical(16), ScaleLogical(12), ScaleLogical(12), ScaleLogical(12)),
            AccessibleName = route.Name
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(242)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(31)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(27)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var name = new Label
        {
            Text = route.Name,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = route.Enabled ? ClipCordTheme.TextPrimary : ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(10.5f, FontStyle.Bold),
            Margin = Padding.Empty
        };
        layout.Controls.Add(name, 0, 0);
        var actions = BuildRouteActions(route, orderedRoutes);
        layout.Controls.Add(actions, 1, 0);
        layout.SetRowSpan(actions, 3);
        layout.Controls.Add(new Label
        {
            Text = $"WHEN  {DescribeTrigger(route)}",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.5f, FontStyle.Bold),
            Margin = Padding.Empty
        }, 0, 1);
        layout.Controls.Add(new Label
        {
            Text = $"THEN   {DescribeActions(route)}",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            Margin = Padding.Empty
        }, 0, 2);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildRouteActions(
        RoutingRoute route,
        IReadOnlyList<RoutingRoute> orderedRoutes)
    {
        var editable = _cutoverCommitted && route.Source == RoutingRouteSource.User;
        var host = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            Margin = Padding.Empty,
            Padding = new Padding(0, 1, 0, 0),
            BackColor = Color.Transparent
        };
        var enabled = CreateSmallButton(
            route.Source == RoutingRouteSource.Migration
                ? "Managed"
                : route.Enabled ? "On" : "Off",
            route.Source == RoutingRouteSource.Migration ? 72 : 54);
        enabled.Name = $"RouteEnabled_{route.RouteId:N}";
        enabled.AccessibleRole = AccessibleRole.CheckButton;
        enabled.AccessibleName = $"{(route.Enabled ? "Disable" : "Enable")} {route.Name}";
        enabled.AccessibleDescription = route.Source == RoutingRouteSource.Migration
            ? "This fallback is pinned to the committed legacy cutover."
            : !_cutoverCommitted
                ? "Route editing is unavailable before safe cutover."
                : null;
        enabled.Enabled = editable;
        enabled.ForeColor = route.Enabled ? Color.FromArgb(76, 210, 145) : ClipCordTheme.TextSecondary;
        enabled.Click += async (_, _) => await RunCommandAsync(
            () => _routeManager.SetEnabledAsync(route.RouteId, !route.Enabled));
        host.Controls.Add(enabled);
        var remove = CreateSmallButton("Delete", 66);
        remove.Name = $"DeleteRoute_{route.RouteId:N}";
        remove.Click += async (_, _) =>
        {
            if (MessageBox.Show(
                    this,
                    $"Delete ‘{route.Name}’? Existing delivery plans keep their frozen settings.",
                    "Delete route",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning) != DialogResult.OK) return;
            await RunCommandAsync(() => _routeManager.DeleteAsync(route.RouteId));
        };
        remove.Enabled = editable;
        remove.AccessibleDescription = editable
            ? null
            : "The route cannot be deleted before safe cutover or when it preserves legacy behavior.";
        host.Controls.Add(remove);
        var sorted = orderedRoutes.OrderBy(item => item.Priority).ToArray();
        var index = Array.FindIndex(sorted, item => item.RouteId == route.RouteId);
        var previousIsEditable = index > 0 &&
                                 sorted[index - 1].Source == RoutingRouteSource.User;
        var nextIsEditable = index >= 0 && index < sorted.Length - 1 &&
                             sorted[index + 1].Source == RoutingRouteSource.User;
        var down = CreateSmallButton("↓", 34);
        down.Name = $"MoveRouteDown_{route.RouteId:N}";
        down.Enabled = editable && nextIsEditable;
        down.AccessibleName = $"Move {route.Name} down";
        down.Click += async (_, _) => await RunCommandAsync(
            () => _routeManager.MoveAsync(route.RouteId, 1));
        host.Controls.Add(down);
        var up = CreateSmallButton("↑", 34);
        up.Name = $"MoveRouteUp_{route.RouteId:N}";
        up.Enabled = editable && previousIsEditable;
        up.AccessibleName = $"Move {route.Name} up";
        up.Click += async (_, _) => await RunCommandAsync(
            () => _routeManager.MoveAsync(route.RouteId, -1));
        host.Controls.Add(up);
        return host;
    }

    private Control BuildEmptyState() => BuildInlineEmpty(
        _cutoverCommitted ? "Build your first route" : "Safe cutover has not run",
        _cutoverCommitted
            ? "Choose what ClipCord should do when a new clip arrives. Runtime activation remains a separate step."
            : "The current watcher still handles every clip. ClipCord will not create a competing route snapshot from this screen.");

    private Control BuildUnavailableState(RoutingDocumentLoadStatus status) => BuildInlineEmpty(
        "Routes need attention",
        $"ClipCord did not modify the routing document because it is {status}. Restore or remove the invalid document before continuing.");

    private Control BuildInlineEmpty(string title, string detail)
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Top,
            Height = ScaleLogical(86),
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(10),
            Margin = new Padding(0, 0, 0, ScaleLogical(16)),
            Padding = new Padding(ScaleLogical(16), ScaleLogical(13), ScaleLogical(16), ScaleLogical(12))
        };
        var text = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        text.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(27)));
        text.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        text.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(10f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        }, 0, 0);
        text.Controls.Add(new Label
        {
            Text = detail,
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.75f),
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true,
            Margin = Padding.Empty
        }, 0, 1);
        card.Controls.Add(text);
        return card;
    }

    private Control BuildConnectionCard(RoutingConnectionDisplay connection)
    {
        var card = new RoundedPanel
        {
            Name = $"ConnectionCard_{connection.ConnectionId}",
            Dock = DockStyle.Top,
            Height = ScaleLogical(94),
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(10),
            Margin = new Padding(0, 0, 0, ScaleLogical(10)),
            Padding = new Padding(ScaleLogical(16), ScaleLogical(13), ScaleLogical(16), ScaleLogical(12))
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(42)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(92)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(30)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new FigmaIconControl
        {
            Asset = FigmaIconAsset.Discord,
            IconColor = Color.FromArgb(176, 128, 255),
            Dock = DockStyle.Fill,
            Margin = new Padding(6, 6, 10, 6)
        }, 0, 0);
        layout.SetRowSpan(layout.GetControlFromPosition(0, 0)!, 2);
        layout.Controls.Add(new Label
        {
            Text = connection.Name,
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(10.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        }, 1, 0);
        layout.Controls.Add(new Label
        {
            Text = connection.Detail,
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.75f),
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true,
            Margin = Padding.Empty
        }, 1, 1);
        var status = new Label
        {
            Text = connection.Available ? "CONNECTED" : "NEEDS ATTENTION",
            Dock = DockStyle.Fill,
            ForeColor = connection.Available ? Color.FromArgb(76, 210, 145) : Color.FromArgb(224, 151, 54),
            Font = ClipCordTheme.InterfaceFont(7.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleRight,
            Margin = Padding.Empty
        };
        layout.Controls.Add(status, 2, 0);
        if (_connections is IRoutingConnectionManagerViewSource manager)
        {
            var remove = CreateSmallButton("Remove", 76);
            remove.Name = $"RemoveConnection_{connection.ConnectionId}";
            remove.Dock = DockStyle.None;
            remove.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            remove.Click += async (_, _) =>
            {
                if (MessageBox.Show(
                        this,
                        $"Remove ‘{connection.Name}’? Routes and pending deliveries must be moved first.",
                        "Remove Discord connection",
                        MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Warning) != DialogResult.OK) return;
                await RunConnectionCommandAsync(
                    () => manager.RemoveDiscordAsync(connection.ConnectionId));
            };
            layout.Controls.Add(remove, 2, 1);
        }
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildConnectionToolbar()
    {
        var host = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = ScaleLogical(40),
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, ScaleLogical(10)),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        var add = CreateSmallButton("+  Add Discord", 124);
        add.Name = "AddDiscordConnectionButton";
        add.Click += async (_, _) => await AddDiscordConnectionAsync();
        host.Controls.Add(add);
        return host;
    }

    private Control BuildConnectorRoadmap()
    {
        var card = BuildInlineEmpty(
            "More destinations are staged for ClipCord 2.0",
            "YouTube and TikTok will use account-based connectors. Discord is the first end-to-end route slice.");
        card.Name = "FutureConnectorsCard";
        return card;
    }

    private async Task AddRouteAsync()
    {
        if (_busy || !_cutoverCommitted) return;
        var connections = SafeLoadConnections().Where(item => item.Available).ToArray();
        using var dialog = new RouteEditorDialog(connections);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Draft is null) return;
        await RunCommandAsync(() => _routeManager.AddAsync(dialog.Draft));
    }

    private async Task AddDiscordConnectionAsync()
    {
        if (_busy || _connections is not IRoutingConnectionManagerViewSource manager) return;
        using var dialog = new DiscordConnectionDialog();
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        var name = dialog.DisplayName;
        var webhook = dialog.WebhookUrl;
        try
        {
            await RunConnectionCommandAsync(() => manager.AddDiscordAsync(name, webhook));
        }
        finally
        {
            dialog.ClearSecret();
            webhook = string.Empty;
        }
    }

    private async Task RunConnectionCommandAsync(
        Func<Task<DiscordConnectionMutationResult>> command)
    {
        if (_busy) return;
        _busy = true;
        _newRouteButton.Enabled = false;
        try
        {
            var result = await command();
            if (!result.Succeeded)
            {
                MessageBox.Show(this, result.Reason, "Discord connection needs attention",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            Reload();
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or IOException or
                UnauthorizedAccessException or CryptographicException)
        {
            Log.Error("A Discord connection command failed.", exception);
            MessageBox.Show(this, "ClipCord could not update the encrypted Discord connection.",
                "Discord connection needs attention", MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _busy = false;
            _newRouteButton.Enabled = _cutoverCommitted;
        }
    }

    private async Task RunCommandAsync(Func<Task> command)
    {
        if (_busy) return;
        _busy = true;
        _newRouteButton.Enabled = false;
        try
        {
            await command();
            Reload();
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or IOException or
                UnauthorizedAccessException)
        {
            Log.Error("A routing command could not be completed.", exception);
            MessageBox.Show(
                this,
                exception.Message,
                "Routes need attention",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _busy = false;
            _newRouteButton.Enabled = _cutoverCommitted;
        }
    }

    private IReadOnlyList<RoutingConnectionDisplay> SafeLoadConnections()
    {
        try
        {
            return _connections.LoadDiscordConnections()
                .Where(item => !string.IsNullOrWhiteSpace(item.ConnectionId))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            Log.Error("Routing connections could not be listed.", exception);
            return [];
        }
    }

    private bool ReadCutoverStatus()
    {
        try
        {
            return _isCutoverCommitted();
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Routing cutover status could not be verified.", exception);
            return false;
        }
    }

    private static Func<bool> CreateCutoverStatusSource(RoutingRouteManager routeManager)
    {
        var directory = Path.GetDirectoryName(routeManager.SnapshotPath)
            ?? throw new InvalidOperationException("The routing state directory is unavailable.");
        var marker = new LegacyRoutingMigrationMarkerStore(
            Path.Combine(directory, LegacyRoutingMigrationMarkerStore.FileName));
        return () =>
        {
            var loaded = marker.Load();
            if (!loaded.LoadedFromDisk ||
                loaded.Document?.Phase != LegacyRoutingMigrationMarkerPhase.Committed)
            {
                return false;
            }
            var routes = routeManager.Load();
            if (!routes.LoadedFromDisk || routes.Document is null) return false;
            var migrationRoute = routes.Document.Routes.SingleOrDefault(route =>
                route.RouteId == loaded.Document.Route.RouteId);
            return migrationRoute is not null &&
                   LegacyRoutingMigrationPlanner.IsEquivalentMigrationRoute(
                       migrationRoute, loaded.Document.Route);
        };
    }

    private static string DescribeTrigger(RoutingRoute route)
    {
        var trigger = route.Trigger switch
        {
            RoutingTriggerKind.AnyNewSourceClip => "Any new source clip",
            RoutingTriggerKind.InstantReplay => "Instant Replay saves a clip",
            RoutingTriggerKind.ManualRecording => "Manual Recording finishes",
            RoutingTriggerKind.WatchedFolder => "A watched-folder clip arrives",
            _ => route.Trigger.ToString()
        };
        var conditions = route.Conditions.Select(condition => condition.Field switch
        {
            RoutingConditionField.Game => $"Game is {condition.Value}",
            RoutingConditionField.ReactionCamera => $"Reaction camera is {condition.Value}",
            RoutingConditionField.ClipSource => $"Source is {condition.Value}",
            RoutingConditionField.CaptureType => $"Capture type is {condition.Value}",
            RoutingConditionField.Duration => $"Duration {condition.Operator} {condition.Value} ms",
            _ => condition.Value
        }).ToArray();
        return conditions.Length == 0 ? trigger : $"{trigger} · {string.Join(" AND ", conditions)}";
    }

    private static string DescribeActions(RoutingRoute route)
    {
        var actions = route.Actions.Where(action => action.Enabled).Select(action => action.Kind switch
        {
            RoutingActionKind.FileIntoLibrary => action.LibraryArea == RoutingLibraryArea.LocalOnly
                ? "File into Library · Local only"
                : "File into Library · Uploaded",
            RoutingActionKind.Deliver =>
                $"{action.Destination} · {action.OutputRef} · {action.Mode}",
            _ => action.Kind.ToString()
        });
        return string.Join("   →   ", actions);
    }

    private static OutlineButton CreateSmallButton(string text, int width) => new()
    {
        Text = text,
        AutoSize = false,
        Size = new Size(width, 30),
        Margin = new Padding(5, 0, 0, 0),
        SurfaceColor = ClipCordTheme.SurfaceControl,
        HoverColor = ClipCordTheme.SurfaceControlHover,
        OutlineColor = ClipCordTheme.BorderStrong,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(8.5f)
    };

    private int ScaleLogical(int value) =>
        Math.Max(1, (int)Math.Round(value * Math.Max(96, DeviceDpi) / 96d));
}

internal sealed class DiscordConnectionDialog : Form
{
    private readonly TextBox _name;
    private readonly TextBox _webhook;

    internal string DisplayName => _name.Text.Trim();
    internal string WebhookUrl => _webhook.Text.Trim();

    internal DiscordConnectionDialog()
    {
        Text = "Add Discord connection";
        ClientSize = new Size(520, 228);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = ClipCordTheme.SurfaceBase;
        ForeColor = ClipCordTheme.TextPrimary;
        Padding = new Padding(18);
        _name = CreateInput("Connection name");
        _name.Text = "Friends server";
        _webhook = CreateInput("Discord webhook URL");
        _webhook.UseSystemPasswordChar = true;

        var root = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(new Label
        {
            Text = "Add Discord\r\nThe webhook is encrypted for this Windows account and never stored in a route.",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(9f),
            Margin = Padding.Empty
        }, 0, 0);
        root.Controls.Add(_name, 0, 1);
        root.Controls.Add(_webhook, 0, 2);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, 8, 0, 0),
            BackColor = ClipCordTheme.SurfaceBase
        };
        var save = new GradientButton { Text = "Add connection", Size = new Size(132, 34) };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text) ||
                !WebhookValidation.IsDiscordWebhook(_webhook.Text))
            {
                MessageBox.Show(this, "Enter a name and a valid Discord webhook URL.",
                    "Connection details required", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            DialogResult = DialogResult.OK;
        };
        var cancel = new OutlineButton
        {
            Text = "Cancel",
            Size = new Size(90, 34),
            SurfaceColor = ClipCordTheme.SurfaceControl,
            HoverColor = ClipCordTheme.SurfaceControlHover,
            OutlineColor = ClipCordTheme.BorderStrong,
            ForeColor = ClipCordTheme.TextPrimary
        };
        cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        root.Controls.Add(actions, 0, 3);
        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;
    }

    internal void ClearSecret()
    {
        _webhook.Text = string.Empty;
        _webhook.ClearUndo();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ClearSecret();
        base.Dispose(disposing);
    }

    private static TextBox CreateInput(string accessibleName) => new()
    {
        AccessibleName = accessibleName,
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = ClipCordTheme.SurfaceSunken,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(9.5f),
        Margin = new Padding(0, 5, 0, 5)
    };
}

internal sealed class RouteEditorDialog : Form
{
    private readonly TextBox _name;
    private readonly TextBox _game;
    private readonly RadioButton _instantReplay;
    private readonly RadioButton _manualRecording;
    private readonly RadioButton _watchedFolder;
    private readonly RadioButton _anyClip;
    private readonly RadioButton _discord;
    private readonly RadioButton _localOnly;
    private readonly ComboBox _discordConnection;
    private readonly RadioButton _original;
    private readonly RadioButton _landscape;
    private readonly RadioButton _portrait;
    private readonly CheckBox _approval;
    private readonly CheckBox _fileIntoLibrary;
    private readonly IReadOnlyList<RoutingConnectionDisplay> _connections;

    internal RoutingRouteDraft? Draft { get; private set; }

    internal RouteEditorDialog(IReadOnlyList<RoutingConnectionDisplay> connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        Text = "New route";
        ClientSize = new Size(600, 610);
        MinimumSize = new Size(560, 570);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = ClipCordTheme.SurfaceBase;
        ForeColor = ClipCordTheme.TextPrimary;
        Font = ClipCordTheme.InterfaceFont(9.5f);
        Padding = new Padding(24, 18, 24, 18);

        _name = CreateEditor("Route name");
        _game = CreateEditor("Optional exact game name");
        _instantReplay = CreateRadio("Instant Replay", selected: true);
        _manualRecording = CreateRadio("Manual Recording");
        _watchedFolder = CreateRadio("Watched folder");
        _anyClip = CreateRadio("Any new source clip");
        _discord = CreateRadio("Discord", selected: _connections.Count > 0);
        _localOnly = CreateRadio("File into Library only", selected: _connections.Count == 0);
        _discord.Enabled = _connections.Count > 0;
        _discordConnection = new ComboBox
        {
            Name = "RouteDiscordConnectionSelector",
            AccessibleName = "Discord connection",
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = ClipCordTheme.SurfaceSunken,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(9f),
            Margin = new Padding(0, 2, 0, 3)
        };
        foreach (var connection in _connections)
            _discordConnection.Items.Add(new DiscordConnectionChoice(connection));
        _discordConnection.SelectedIndex = -1;
        _original = CreateRadio("Original", selected: true);
        _landscape = CreateRadio("Landscape");
        _portrait = CreateRadio("Portrait");
        _approval = CreateCheck("Require approval before sending");
        _fileIntoLibrary = CreateCheck("File into Library after route finishes", selected: true);
        void UpdateDestinationOptions()
        {
            _fileIntoLibrary.Checked = true;
            _fileIntoLibrary.Enabled = false;
            _approval.Enabled = !_localOnly.Checked;
            _discordConnection.Enabled = _discord.Checked;
        }
        _localOnly.CheckedChanged += (_, _) => UpdateDestinationOptions();
        _discord.CheckedChanged += (_, _) => UpdateDestinationOptions();
        UpdateDestinationOptions();

        var root = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 11,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.Controls.Add(CreateTitle(), 0, 0);
        root.Controls.Add(CreateField("ROUTE NAME", _name), 0, 1);
        root.Controls.Add(CreateSectionLabel("WHEN"), 0, 2);
        var triggers = CreateHorizontalChoices(
            _instantReplay, _manualRecording, _watchedFolder, _anyClip);
        root.Controls.Add(triggers, 0, 3);
        root.SetRowSpan(triggers, 2);
        root.Controls.Add(CreateField("IF · OPTIONAL", _game), 0, 5);
        root.Controls.Add(CreateSectionLabel("PREPARE"), 0, 6);
        root.Controls.Add(CreateHorizontalChoices(_original, _landscape, _portrait), 0, 7);
        root.Controls.Add(BuildDestinationBlock(), 0, 8);
        root.Controls.Add(new Label
        {
            Text = "Capture owns which renditions exist. The route chooses one and falls back to Original when needed.",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(0, 8, 0, 0)
        }, 0, 9);
        root.Controls.Add(BuildActions(), 0, 10);
        Controls.Add(root);
    }

    private Control CreateTitle()
    {
        var panel = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        panel.Controls.Add(new Label
        {
            Text = "Create a route",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.DisplayFont(16f, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            Margin = Padding.Empty
        }, 0, 0);
        panel.Controls.Add(new Label
        {
            Text = "WHEN → IF → PREPARE → THEN",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            TextAlign = ContentAlignment.TopLeft,
            Margin = Padding.Empty
        }, 0, 1);
        return panel;
    }

    private Control BuildDestinationBlock()
    {
        var panel = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(CreateSectionLabel("THEN"), 0, 0);
        panel.Controls.Add(CreateHorizontalChoices(_discord, _localOnly), 0, 1);
        panel.Controls.Add(_discordConnection, 0, 2);
        panel.Controls.Add(CreateHorizontalChoices(_approval, _fileIntoLibrary), 0, 3);
        return panel;
    }

    private Control BuildActions()
    {
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, 7, 0, 0),
            BackColor = ClipCordTheme.SurfaceBase
        };
        var save = new GradientButton
        {
            Text = "Save route",
            Size = new Size(126, 36),
            Margin = Padding.Empty
        };
        save.Click += (_, _) => SaveDraft();
        var cancel = new OutlineButton
        {
            Text = "Cancel",
            Size = new Size(92, 36),
            Margin = new Padding(0, 0, 10, 0),
            SurfaceColor = ClipCordTheme.SurfaceControl,
            HoverColor = ClipCordTheme.SurfaceControlHover,
            OutlineColor = ClipCordTheme.BorderStrong,
            ForeColor = ClipCordTheme.TextPrimary
        };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
        return actions;
    }

    private void SaveDraft()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            MessageBox.Show(this, "Give this route a name.", "Route name required",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            _name.Focus();
            return;
        }
        var connection = _discord.Checked
            ? (_discordConnection.SelectedItem as DiscordConnectionChoice)?.Connection
            : null;
        if (_discord.Checked && connection is null)
        {
            MessageBox.Show(this, "Connect Discord before creating this route.",
                "Choose a Discord connection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _discordConnection.Focus();
            return;
        }
        var trigger = _manualRecording.Checked
            ? RoutingTriggerKind.ManualRecording
            : _watchedFolder.Checked
                ? RoutingTriggerKind.WatchedFolder
                : _anyClip.Checked
                    ? RoutingTriggerKind.AnyNewSourceClip
                    : RoutingTriggerKind.InstantReplay;
        var output = _landscape.Checked
            ? RoutingOutputKind.Landscape
            : _portrait.Checked
                ? RoutingOutputKind.Portrait
                : RoutingOutputKind.Original;
        Draft = new RoutingRouteDraft(
            _name.Text,
            trigger,
            string.IsNullOrWhiteSpace(_game.Text) ? null : _game.Text,
            _discord.Checked ? RoutingDestinationKind.Discord : null,
            connection?.ConnectionId,
            output,
            _approval.Checked ? RoutingDeliveryMode.Approval : RoutingDeliveryMode.Automatic,
            RoutingMissingOutputBehavior.UseOriginal,
            _localOnly.Checked || _fileIntoLibrary.Checked);
        DialogResult = DialogResult.OK;
    }

    private static TextBox CreateEditor(string accessibleName) => new()
    {
        AccessibleName = accessibleName,
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = ClipCordTheme.SurfaceSunken,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(9.5f),
        Margin = Padding.Empty
    };

    private static RadioButton CreateRadio(string text, bool selected = false) => new()
    {
        Text = text,
        Checked = selected,
        AutoSize = true,
        ForeColor = ClipCordTheme.TextPrimary,
        BackColor = ClipCordTheme.SurfaceBase,
        Font = ClipCordTheme.InterfaceFont(9f),
        Margin = new Padding(0, 2, 16, 0)
    };

    private static CheckBox CreateCheck(string text, bool selected = false) => new()
    {
        Text = text,
        Checked = selected,
        AutoSize = true,
        ForeColor = ClipCordTheme.TextSecondary,
        BackColor = ClipCordTheme.SurfaceBase,
        Font = ClipCordTheme.InterfaceFont(8.75f),
        Margin = new Padding(0, 3, 16, 0)
    };

    private static Control CreateField(string label, Control editor)
    {
        var field = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(0, 2, 0, 6),
            BackColor = ClipCordTheme.SurfaceBase
        };
        field.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        field.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        field.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(7.5f, FontStyle.Bold),
            Margin = Padding.Empty
        }, 0, 0);
        field.Controls.Add(editor, 0, 1);
        return field;
    }

    private static Label CreateSectionLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = Color.FromArgb(176, 128, 255),
        Font = ClipCordTheme.InterfaceFont(7.75f, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty
    };

    private static Control CreateHorizontalChoices(params Control[] choices)
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        flow.Controls.AddRange(choices);
        return flow;
    }

    private sealed record DiscordConnectionChoice(RoutingConnectionDisplay Connection)
    {
        public override string ToString()
        {
            var id = Connection.ConnectionId;
            var suffix = id.Length <= 6 ? id : id[^6..];
            return $"{Connection.Name} · …{suffix}";
        }
    }
}
