namespace ClipsToDiscord;

internal sealed class RoutingDeliveryHistoryDialog : Form
{
    private readonly RoutingDeliveryHistoryReader _history;
    private readonly RoutingOperatorControl _operator;
    private readonly FlowLayoutPanel _items;
    private bool _busy;

    internal RoutingDeliveryHistoryDialog(RoutingOutboxStore? outboxStore = null)
    {
        var store = outboxStore ?? new RoutingOutboxStore();
        _history = new RoutingDeliveryHistoryReader(store);
        _operator = new RoutingOperatorControl(store);
        Text = "ClipCord — Delivery history";
        ClientSize = new Size(820, 620);
        MinimumSize = new Size(660, 500);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = ClipCordTheme.SurfaceBase;
        ForeColor = ClipCordTheme.TextPrimary;
        Font = ClipCordTheme.InterfaceFont(9.5f);
        ShowInTaskbar = false;
        Padding = new Padding(18);

        var root = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildHeader(), 0, 0);
        _items = new FlowLayoutPanel
        {
            Name = "RoutingDeliveryHistoryItems",
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Margin = Padding.Empty,
            Padding = new Padding(0, 4, 8, 8),
            BackColor = ClipCordTheme.SurfaceBase
        };
        _items.SizeChanged += (_, _) => FitCards();
        root.Controls.Add(_items, 0, 1);
        Controls.Add(root);
        Reload();
    }

    private Control BuildHeader()
    {
        var header = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Controls.Add(new Label
        {
            Text = "Delivery history",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.DisplayFont(16f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        }, 0, 0);
        header.Controls.Add(new Label
        {
            Text = "Retry, approve, or resolve only the durable plan you selected.",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.75f),
            TextAlign = ContentAlignment.TopLeft,
            Margin = Padding.Empty
        }, 0, 1);
        var close = CreateAction("Close", 88);
        close.Click += (_, _) => Close();
        header.Controls.Add(close, 1, 0);
        header.SetRowSpan(close, 2);
        return header;
    }

    private void Reload()
    {
        _items.SuspendLayout();
        try
        {
            _items.Controls.Clear();
            IReadOnlyList<RoutingDeliveryHistoryItem> history;
            try
            {
                history = _history.Read();
            }
            catch (Exception exception) when (
                exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Log.Error("Routing history could not be loaded.", exception);
                _items.Controls.Add(BuildNotice(
                    "Delivery history needs attention",
                    exception.Message));
                return;
            }

            if (history.Count == 0)
            {
                _items.Controls.Add(BuildNotice(
                    "No delivery plans yet",
                    "History will appear after the opt-in routing runtime evaluates its first clip."));
                return;
            }
            foreach (var item in history) _items.Controls.Add(BuildPlanCard(item));
        }
        finally
        {
            _items.ResumeLayout(true);
            FitCards();
        }
    }

    private Control BuildPlanCard(RoutingDeliveryHistoryItem item)
    {
        var rows = 1 + item.Deliveries.Count + item.FileDispositions.Count;
        var card = new RoundedPanel
        {
            Name = $"DeliveryPlan_{item.Plan.PlanId:N}",
            Width = Math.Max(300, _items.ClientSize.Width - 12),
            Height = 62 + Math.Max(1, rows - 1) * 58,
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = 10,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(14, 10, 14, 10),
            AccessibleName = $"Delivery plan for {item.Plan.SourceClipId}"
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = rows,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(BuildPlanHeader(item), 0, 0);
        var row = 1;
        foreach (var delivery in item.Deliveries)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.Controls.Add(BuildDeliveryRow(delivery, item.IsArchived), 0, row++);
        }
        foreach (var disposition in item.FileDispositions)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.Controls.Add(BuildDispositionRow(disposition, item.IsArchived), 0, row++);
        }
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildPlanHeader(RoutingDeliveryHistoryItem item)
    {
        var header = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        header.Controls.Add(new Label
        {
            Text = item.Plan.SourceClipId,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(10f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        }, 0, 0);
        header.Controls.Add(new Label
        {
            Text = item.IsArchived ? "ARCHIVED" : item.Plan.CreatedUtc.ToLocalTime().ToString("g"),
            Dock = DockStyle.Fill,
            ForeColor = item.IsArchived ? ClipCordTheme.TextTertiary : ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(7.75f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleRight,
            Margin = Padding.Empty
        }, 1, 0);
        return header;
    }

    private Control BuildDeliveryRow(PlannedDelivery delivery, bool archived)
    {
        var row = CreateHistoryRow();
        row.Controls.Add(new FigmaIconControl
        {
            Asset = delivery.Destination == RoutingDestinationKind.Discord
                ? FigmaIconAsset.Discord
                : FigmaIconAsset.Upload,
            IconColor = Color.FromArgb(176, 128, 255),
            Dock = DockStyle.Fill,
            Margin = new Padding(8, 17, 11, 17)
        }, 0, 0);
        row.Controls.Add(BuildDescription(
            $"{delivery.Destination} · {delivery.Output.Kind}",
            $"{delivery.State} · attempt {delivery.Attempts}"), 1, 0);
        row.Controls.Add(BuildDeliveryActions(delivery, archived), 2, 0);
        return row;
    }

    private Control BuildDispositionRow(PlannedFileDisposition disposition, bool archived)
    {
        var row = CreateHistoryRow();
        row.Controls.Add(new FigmaIconControl
        {
            Asset = FigmaIconAsset.Disk,
            IconColor = Color.FromArgb(49, 177, 113),
            Dock = DockStyle.Fill,
            Margin = new Padding(8, 17, 11, 17)
        }, 0, 0);
        row.Controls.Add(BuildDescription(
            $"File into Library · {disposition.LibraryArea}",
            $"{disposition.State} · attempt {disposition.Attempts}"), 1, 0);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = new Padding(0, 12, 0, 0)
        };
        if (!archived && disposition.State is PlannedFileDispositionState.Failed or
            PlannedFileDispositionState.RecoveryPending)
        {
            var retry = CreateAction("Retry filing", 92);
            retry.Click += async (_, _) => await RunAsync(
                () => _operator.RetryFailedFileDispositionAsync(disposition.DispositionId));
            actions.Controls.Add(retry);
        }
        row.Controls.Add(actions, 2, 0);
        return row;
    }

    private Control BuildDeliveryActions(PlannedDelivery delivery, bool archived)
    {
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = new Padding(0, 12, 0, 0)
        };
        if (archived) return actions;
        switch (delivery.State)
        {
            case PlannedDeliveryState.WaitingForApproval:
                AddAction(actions, "Approve", 78,
                    () => _operator.ApproveDeliveryAsync(delivery.DeliveryId));
                break;
            case PlannedDeliveryState.Failed:
                AddAction(actions, "Retry", 68,
                    () => _operator.RetryFailedDeliveryAsync(delivery.DeliveryId));
                break;
            case PlannedDeliveryState.NeedsAttention:
                AddAction(actions, "Rebuild", 74, () => _operator.ResolveNeedsAttentionAsync(
                    delivery.DeliveryId, RoutingNeedsAttentionResolution.RetryOrRebuild));
                AddAction(actions, "Skip", 58, () => _operator.ResolveNeedsAttentionAsync(
                    delivery.DeliveryId, RoutingNeedsAttentionResolution.Skip));
                AddAction(actions, "Use original", 92, () => _operator.ResolveNeedsAttentionAsync(
                    delivery.DeliveryId, RoutingNeedsAttentionResolution.UseOriginal));
                break;
            case PlannedDeliveryState.DeliveryUnknown:
                var resend = CreateAction("Send again", 86);
                resend.Click += async (_, _) =>
                {
                    if (MessageBox.Show(
                            this,
                            "ClipCord cannot know whether the previous send completed. Sending again may create a duplicate. Continue?",
                            "Duplicate delivery risk",
                            MessageBoxButtons.OKCancel,
                            MessageBoxIcon.Warning) != DialogResult.OK) return;
                    await RunAsync(() =>
                        _operator.AuthorizeUnknownSendAgainAsync(delivery.DeliveryId));
                };
                actions.Controls.Add(resend);
                var delivered = CreateAction("Mark delivered", 104);
                delivered.Click += async (_, _) =>
                {
                    var receipt = PromptForReceipt();
                    if (receipt is null) return;
                    await RunAsync(() => _operator.ResolveUnknownAsDeliveredAsync(
                        delivery.DeliveryId, receipt));
                };
                actions.Controls.Add(delivered);
                break;
        }
        return actions;
    }

    private void AddAction(
        FlowLayoutPanel host,
        string text,
        int width,
        Func<Task<RoutingOperatorResult>> command)
    {
        var button = CreateAction(text, width);
        button.Click += async (_, _) => await RunAsync(command);
        host.Controls.Add(button);
    }

    private async Task RunAsync(Func<Task<RoutingOperatorResult>> command)
    {
        if (_busy) return;
        _busy = true;
        Enabled = false;
        try
        {
            var result = await command();
            if (result.Outcome is RoutingOperatorOutcome.InvalidState or
                RoutingOperatorOutcome.NotFound or RoutingOperatorOutcome.Archived)
            {
                MessageBox.Show(this, $"The action was not applied: {result.Outcome}.",
                    "Delivery changed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            Reload();
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or IOException or
                UnauthorizedAccessException)
        {
            Log.Error("A delivery-history action failed.", exception);
            MessageBox.Show(this, exception.Message, "Delivery needs attention",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            Enabled = true;
            _busy = false;
        }
    }

    private string? PromptForReceipt()
    {
        using var prompt = new Form
        {
            Text = "Mark delivery complete",
            ClientSize = new Size(430, 140),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = ClipCordTheme.SurfaceBase,
            ForeColor = ClipCordTheme.TextPrimary,
            Padding = new Padding(14),
            ShowInTaskbar = false
        };
        var editor = new TextBox
        {
            AccessibleName = "Remote receipt reference",
            Dock = DockStyle.Top,
            BackColor = ClipCordTheme.SurfaceSunken,
            ForeColor = ClipCordTheme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle
        };
        var confirm = CreateAction("Confirm", 88);
        confirm.DialogResult = DialogResult.OK;
        confirm.Top = 72;
        confirm.Left = 310;
        prompt.Controls.Add(confirm);
        prompt.Controls.Add(editor);
        prompt.Controls.Add(new Label
        {
            Text = "Paste the provider receipt or message reference:",
            Dock = DockStyle.Top,
            Height = 28,
            ForeColor = ClipCordTheme.TextSecondary
        });
        prompt.AcceptButton = confirm;
        if (prompt.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(editor.Text))
            return null;
        return editor.Text.Trim();
    }

    private static BufferedTableLayoutPanel CreateHistoryRow()
    {
        var row = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 312));
        return row;
    }

    private static Control BuildDescription(string title, string detail)
    {
        var text = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        text.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(9f, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            Margin = Padding.Empty
        }, 0, 0);
        text.Controls.Add(new Label
        {
            Text = detail,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8f),
            TextAlign = ContentAlignment.TopLeft,
            Margin = Padding.Empty
        }, 0, 1);
        return text;
    }

    private static Control BuildNotice(string title, string detail)
    {
        var card = new RoundedPanel
        {
            Width = 740,
            Height = 100,
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = 10,
            Padding = new Padding(16),
            Margin = new Padding(0, 0, 0, 10)
        };
        var label = new Label
        {
            Text = $"{title}\r\n{detail}",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(9f),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        card.Controls.Add(label);
        return card;
    }

    private void FitCards()
    {
        var width = Math.Max(300, _items.ClientSize.Width - 28);
        foreach (Control card in _items.Controls) card.Width = width;
    }

    private static OutlineButton CreateAction(string text, int width) => new()
    {
        Text = text,
        Size = new Size(width, 30),
        Margin = new Padding(5, 0, 0, 0),
        SurfaceColor = ClipCordTheme.SurfaceControl,
        HoverColor = ClipCordTheme.SurfaceControlHover,
        OutlineColor = ClipCordTheme.BorderStrong,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(8.5f)
    };
}
