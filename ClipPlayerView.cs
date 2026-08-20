using System.Windows.Forms.Integration;
using MediaElement = System.Windows.Controls.MediaElement;
using WpfMediaState = System.Windows.Controls.MediaState;
using WpfStretch = System.Windows.Media.Stretch;
using WpfStretchDirection = System.Windows.Controls.StretchDirection;

namespace ClipsToDiscord;

/// <summary>
/// Plays a Gallery clip inside ClipCord instead of handing it to whichever application
/// owns .mp4. Local-only clips in particular should never be passed to an unknown player,
/// which may sit inside a folder that syncs to the cloud.
///
/// The file that gets opened comes from <see cref="IClipPlaybackPreparer"/> rather than
/// straight off disk, so a clip recorded with the microphone on a separate track is heard
/// complete rather than as its first stream alone.
/// </summary>
internal sealed class ClipPlayerView : UserControl
{
    private readonly GalleryClipEntry _clip;
    private readonly IClipPlaybackPreparer _preparer;
    private readonly ElementHost _mediaHost;
    private readonly MediaElement _mediaElement;
    private readonly Label _statusLabel;
    private readonly Label _positionLabel;
    private readonly Label _audioLabel;
    private readonly OutlineButton _playButton;
    private readonly OutlineButton _muteButton;
    private readonly PlaybackSeekBar _seekBar;
    private readonly System.Windows.Forms.Timer _positionTimer;
    private CancellationTokenSource? _lifetimeCancellation;
    private TimeSpan _duration;
    private bool _mediaReady;
    private bool _isPlaying;
    private bool _seeking;
    private bool _playbackStarted;
    private bool _startupDpiScaleApplied;
    private bool _disposed;

    internal ClipPlayerView(GalleryClipEntry clip, IClipPlaybackPreparer? preparer = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _clip = clip;
        _preparer = preparer ?? new ClipPlaybackPreparer();
        Name = "ClipPlayerView";
        AccessibleName = $"Play {clip.FileName}";
        Dock = DockStyle.Fill;
        BackColor = ClipCordTheme.Shell;
        Font = ClipCordTheme.InterfaceFont(9.5f);
        Padding = Padding.Empty;

        var page = new BufferedTableLayoutPanel
        {
            Name = "ClipPlayerLayout",
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var card = new RoundedPanel
        {
            Name = "ClipPlayerCard",
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = 16,
            Padding = new Padding(20, 18, 20, 18),
            Margin = new Padding(0, 0, 0, 12)
        };

        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceRaised
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(BuildHeading(), 0, 0);

        var surface = new RoundedPanel
        {
            Name = "ClipPlayerSurface",
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.SurfaceChrome,
            BorderColor = ClipCordTheme.BorderStrong,
            CornerRadius = 12,
            Margin = new Padding(0, 12, 0, 10),
            Padding = new Padding(2)
        };
        _statusLabel = new Label
        {
            Name = "ClipPlayerStatus",
            Text = "Preparing playback…",
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ForeColor = ClipCordTheme.ShellMutedText,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = ClipCordTheme.InterfaceFont(10f)
        };
        _mediaElement = new MediaElement
        {
            LoadedBehavior = WpfMediaState.Manual,
            UnloadedBehavior = WpfMediaState.Manual,
            Stretch = WpfStretch.Uniform,
            StretchDirection = WpfStretchDirection.Both,
            Volume = 1.0,
            IsMuted = false
        };
        _mediaElement.MediaOpened += MediaOpened;
        _mediaElement.MediaEnded += MediaEnded;
        _mediaElement.MediaFailed += MediaFailed;
        _mediaHost = new ElementHost
        {
            Name = "ClipPlayerHost",
            Dock = DockStyle.Fill,
            Visible = false,
            BackColor = Color.Transparent,
            Child = _mediaElement
        };
        surface.Controls.Add(_statusLabel);
        surface.Controls.Add(_mediaHost);
        layout.Controls.Add(surface, 0, 1);

        // The transport sits below the video rather than over it: ElementHost renders into
        // its own window, so anything Windows Forms paints in the same area lands behind
        // the picture instead of on top of it.
        _playButton = CreateTransportButton("Play", 92, primary: true);
        _playButton.Name = "ClipPlayerPlayButton";
        _playButton.Click += (_, _) => TogglePlayback();
        _playButton.Enabled = false;
        _muteButton = CreateTransportButton("Mute", 78);
        _muteButton.Name = "ClipPlayerMuteButton";
        _muteButton.Click += (_, _) => ToggleMute();
        _muteButton.Enabled = false;
        _positionLabel = CreateMutedLabel("0:00.0 / —");
        _positionLabel.Name = "ClipPlayerPositionLabel";
        _audioLabel = CreateMutedLabel("Preparing every detected audio track for playback…");
        _audioLabel.Name = "ClipPlayerAudioLabel";
        _seekBar = new PlaybackSeekBar
        {
            Name = "ClipPlayerSeekBar",
            Dock = DockStyle.Fill,
            Enabled = false,
            Margin = new Padding(0, 6, 0, 6),
            AccessibleName = "Playback position"
        };
        _seekBar.SeekStarted += () => _seeking = true;
        _seekBar.SeekCommitted += SeekCommitted;
        layout.Controls.Add(BuildTransport(), 0, 2);

        card.Controls.Add(layout);
        page.Controls.Add(card, 0, 0);
        page.Controls.Add(BuildPrivacyNote(), 0, 1);
        Controls.Add(page);

        _positionTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _positionTimer.Tick += (_, _) => RefreshPosition();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var width = Math.Max(1, proposedSize.Width);
        var horizontalCardPadding = ScaleLogical(40);
        var videoWidth = Math.Max(1, width - horizontalCardPadding);
        var videoHeight = Math.Clamp(
            (int)Math.Round(videoWidth / 1.9d),
            ScaleLogical(260),
            ScaleLogical(410));
        return new Size(width, videoHeight + ScaleLogical(194));
    }

    private Control BuildHeading()
    {
        var heading = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceRaised
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = new Label
        {
            Name = "ClipPlayerTitle",
            Text = _clip.FileName,
            AutoSize = false,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(0, 28),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.DisplayFont(13f, FontStyle.Bold),
            Margin = Padding.Empty,
            UseMnemonic = false
        };
        heading.Controls.Add(title, 0, 0);
        heading.Controls.Add(CreateRouteChip(_clip.Route), 1, 0);
        var metadata = CreateMutedLabel($"{_clip.GameName} · {GalleryView.FormatBytes(_clip.Length)}");
        metadata.Name = "ClipPlayerMetadata";
        metadata.Margin = new Padding(0, 3, 0, 0);
        heading.Controls.Add(metadata, 0, 1);
        heading.SetColumnSpan(metadata, 2);
        return heading;
    }

    private Control BuildTransport()
    {
        var transport = new BufferedTableLayoutPanel
        {
            Name = "ClipPlayerTransport",
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 2,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceRaised
        };
        transport.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        transport.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        transport.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        transport.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        transport.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        transport.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _playButton.Margin = new Padding(0, 0, 12, 0);
        _muteButton.Margin = new Padding(12, 0, 0, 0);
        _positionLabel.Anchor = AnchorStyles.Right;
        _positionLabel.Margin = new Padding(12, 0, 0, 0);
        transport.Controls.Add(_playButton, 0, 0);
        transport.Controls.Add(_seekBar, 1, 0);
        transport.Controls.Add(_positionLabel, 2, 0);
        transport.Controls.Add(_muteButton, 3, 0);

        var audioNote = new RoundedPanel
        {
            Name = "ClipPlayerAudioNote",
            Dock = DockStyle.Fill,
            AutoSize = true,
            MinimumSize = new Size(0, 34),
            BackColor = ClipCordTheme.SurfaceSunken,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = 8,
            Padding = new Padding(8, 5, 10, 5),
            Margin = new Padding(0, 9, 0, 0),
            AccessibleName = "Playback audio information",
            AccessibleRole = AccessibleRole.Grouping
        };
        var audioLayout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceSunken
        };
        audioLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 22));
        audioLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var audioGlyph = new BrandGlyphControl
        {
            Glyph = BrandGlyph.Activity,
            GlyphColor = Color.FromArgb(62, 211, 151),
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            AccessibleRole = AccessibleRole.None
        };
        _audioLabel.AutoSize = false;
        _audioLabel.Dock = DockStyle.Fill;
        _audioLabel.TextAlign = ContentAlignment.MiddleLeft;
        _audioLabel.AutoEllipsis = true;
        _audioLabel.Margin = Padding.Empty;
        audioLayout.Controls.Add(audioGlyph, 0, 0);
        audioLayout.Controls.Add(_audioLabel, 1, 0);
        audioNote.Controls.Add(audioLayout);
        transport.Controls.Add(audioNote, 0, 1);
        transport.SetColumnSpan(audioNote, 4);
        return transport;
    }

    private Control BuildPrivacyNote()
    {
        var note = new BufferedTableLayoutPanel
        {
            Name = "ClipPlayerPrivacyNote",
            Dock = DockStyle.Top,
            AutoSize = true,
            MinimumSize = new Size(0, 26),
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(2, 0, 0, 0),
            BackColor = ClipCordTheme.SurfaceBase,
            AccessibleName = "Playback privacy information",
            AccessibleRole = AccessibleRole.Grouping
        };
        note.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 22));
        note.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var shield = new BrandGlyphControl
        {
            Glyph = BrandGlyph.Shield,
            GlyphColor = Color.FromArgb(62, 211, 151),
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            AccessibleRole = AccessibleRole.None
        };
        var copy = CreateMutedLabel(
            "Playback happens inside ClipCord. A local-only clip is never handed to another application.");
        copy.Name = "ClipPlayerPrivacyLabel";
        copy.Dock = DockStyle.Fill;
        copy.AutoSize = false;
        copy.AutoEllipsis = true;
        copy.TextAlign = ContentAlignment.MiddleLeft;
        copy.Margin = Padding.Empty;
        note.Controls.Add(shield, 0, 0);
        note.Controls.Add(copy, 1, 0);
        return note;
    }

    private static Control CreateRouteChip(GalleryClipRoute route)
    {
        var localOnly = route == GalleryClipRoute.LocalOnly;
        var label = new Label
        {
            Text = localOnly ? "●  Local only" : "●  Uploaded",
            AutoSize = true,
            ForeColor = localOnly ? Color.FromArgb(255, 213, 216) : Color.FromArgb(190, 238, 218),
            Font = ClipCordTheme.InterfaceFont(8.2f),
            BackColor = Color.Transparent,
            Location = new Point(9, 4),
            Margin = Padding.Empty,
            UseMnemonic = false,
            AccessibleRole = AccessibleRole.None
        };
        var chip = new RoundedPanel
        {
            Name = "ClipPlayerRouteChip",
            AccessibleName = localOnly ? "Local only clip" : "Uploaded clip",
            AccessibleRole = AccessibleRole.StaticText,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = localOnly ? Color.FromArgb(54, 25, 39) : Color.FromArgb(19, 49, 47),
            BorderColor = localOnly ? ClipCordTheme.Coral : Color.FromArgb(48, 184, 130),
            CornerRadius = 8,
            Padding = new Padding(9, 4, 9, 4),
            Margin = new Padding(12, 0, 0, 0)
        };
        chip.Controls.Add(label);
        return chip;
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        ApplyStartupDpiScale();
        // A handle is recreated whenever the view is reparented, which must not restart a
        // clip the viewer is part-way through.
        if (_playbackStarted) return;
        _playbackStarted = true;
        _lifetimeCancellation ??= new CancellationTokenSource();
        _ = BeginPlaybackAsync(_lifetimeCancellation.Token);
    }

    private void ApplyStartupDpiScale()
    {
        if (_startupDpiScaleApplied) return;
        _startupDpiScaleApplied = true;
        var scale = Math.Max(1f, DeviceDpi / 96f);
        if (scale > 1.001f)
        {
            Scale(new SizeF(scale, scale));
        }
        if (Parent is BrandedScrollHost scrollHost)
        {
            scrollHost.RefreshContentLayout(preservePosition: false);
        }
    }

    private async Task BeginPlaybackAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_clip.Path))
        {
            SetStatus("This clip is no longer on disk.");
            return;
        }

        try
        {
            var progress = new ControlProgress<ClipPlaybackPreparationProgress>(
                this,
                update => SetStatus(update.Message));
            var prepared = await _preparer.PrepareAsync(_clip.Path, cancellationToken, progress);
            if (cancellationToken.IsCancellationRequested || !CanUpdate()) return;
            _audioLabel.Text = DescribeAudio(prepared);
            OpenMedia(prepared.Path);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Log.Error($"Could not prepare Gallery playback for {_clip.FileName}.", exception);
            if (!CanUpdate()) return;
            // Falling back to the source keeps the clip watchable, but a multi-track
            // recording would lose its microphone, so that is stated rather than hidden.
            // An unreadable file is a different problem and must not be described as one.
            _audioLabel.Text = exception is InvalidOperationException
                ? "This clip could not be inspected — it may be incomplete or damaged."
                : "Audio tracks could not be mixed — only the first track will be audible.";
            OpenMedia(_clip.Path);
        }
    }

    /// <summary>
    /// Describes what a viewer is about to hear. An unknown track count is reported as
    /// unknown: claiming a single track when nothing measured it is how a missing
    /// microphone would pass unnoticed.
    /// </summary>
    private static string DescribeAudio(ClipPlaybackSource prepared)
    {
        if (prepared.IsMixedRendition)
        {
            return $"{prepared.AudioTrackCount} audio tracks mixed for playback, matching what an upload would contain.";
        }
        return prepared.AudioTrackCount == ClipPlaybackSource.UnknownAudioTrackCount
            ? "Audio tracks could not be inspected without FFmpeg — if this clip has a separate microphone track, only the first will be audible."
            : "Every detected audio track is available for playback, matching what an upload would contain.";
    }

    private void OpenMedia(string path)
    {
        SetStatus("Loading…");
        try
        {
            _mediaElement.Stop();
            _mediaElement.Source = new Uri(path, UriKind.Absolute);
            _mediaElement.Play();
            _isPlaying = true;
            _playButton.Text = "Pause";
        }
        catch (Exception exception)
        {
            Log.Error($"Could not open {_clip.FileName} for in-app playback.", exception);
            SetStatus("This clip could not be played in ClipCord.");
        }
    }

    private void MediaOpened(object? sender, EventArgs eventArgs)
    {
        if (!CanUpdate()) return;
        _duration = _mediaElement.NaturalDuration.HasTimeSpan
            ? _mediaElement.NaturalDuration.TimeSpan
            : TimeSpan.Zero;
        _mediaReady = true;
        _statusLabel.Visible = false;
        _mediaHost.Visible = true;
        _mediaHost.BringToFront();
        _playButton.Enabled = true;
        _muteButton.Enabled = true;
        _seekBar.Enabled = _duration > TimeSpan.Zero;
        _seekBar.SetDuration(_duration);
        _positionTimer.Start();
        RefreshPosition();
    }

    private void MediaEnded(object? sender, EventArgs eventArgs)
    {
        if (!CanUpdate()) return;
        _isPlaying = false;
        _playButton.Text = "Play";
        _mediaElement.Pause();
        _mediaElement.Position = TimeSpan.Zero;
        RefreshPosition();
    }

    private void MediaFailed(object? sender, System.Windows.ExceptionRoutedEventArgs eventArgs)
    {
        if (!CanUpdate()) return;
        Log.Error($"In-app playback failed for {_clip.FileName}.", eventArgs.ErrorException);
        _positionTimer.Stop();
        _mediaHost.Visible = false;
        _isPlaying = false;
        _playButton.Text = "Play";
        _playButton.Enabled = false;
        _muteButton.Enabled = false;
        _seekBar.Enabled = false;
        SetStatus(File.Exists(_clip.Path)
            ? "Windows could not decode this clip. Media Feature Pack may be missing."
            : "This clip is no longer on disk.");
    }

    private void TogglePlayback()
    {
        if (!_mediaReady) return;
        if (_isPlaying)
        {
            _mediaElement.Pause();
            _isPlaying = false;
            _playButton.Text = "Play";
            return;
        }
        _mediaElement.Play();
        _isPlaying = true;
        _playButton.Text = "Pause";
    }

    private void ToggleMute()
    {
        if (!_mediaReady) return;
        _mediaElement.IsMuted = !_mediaElement.IsMuted;
        _muteButton.Text = _mediaElement.IsMuted ? "Unmute" : "Mute";
    }

    private void SeekCommitted(double fraction)
    {
        _seeking = false;
        if (!_mediaReady || _duration <= TimeSpan.Zero) return;
        var target = TimeSpan.FromSeconds(_duration.TotalSeconds * Math.Clamp(fraction, 0, 1));
        _mediaElement.Position = target;
        RefreshPosition();
    }

    private void RefreshPosition()
    {
        if (!CanUpdate() || !_mediaReady) return;
        var position = _mediaElement.Position;
        _positionLabel.Text = _duration > TimeSpan.Zero
            ? $"{FormatTime(position)} / {FormatTime(_duration)}"
            : FormatTime(position);
        if (!_seeking && _duration > TimeSpan.Zero)
        {
            _seekBar.SetPosition(position);
        }
    }

    private void SetStatus(string text)
    {
        if (!CanUpdate()) return;
        _statusLabel.Text = text;
        _statusLabel.Visible = true;
        _statusLabel.BringToFront();
        _mediaHost.Visible = false;
    }

    /// <summary>
    /// Releases the media file. MediaElement holds a handle on whatever it has open, and
    /// the clip underneath may be moved or recycled by an edit, so the source is always
    /// cleared before this view goes away.
    /// </summary>
    internal void StopPlayback()
    {
        _positionTimer.Stop();
        try
        {
            _mediaElement.Stop();
            _mediaElement.Position = TimeSpan.Zero;
            _mediaElement.Source = null;
        }
        catch (Exception exception)
        {
            Log.Error($"Could not release the player for {_clip.FileName}.", exception);
        }
        _isPlaying = false;
        _mediaReady = false;
    }

    private bool CanUpdate() => !_disposed && !IsDisposed && !Disposing;

    private void TryPostToUi(Action action)
    {
        if (!CanUpdate()) return;
        if (!InvokeRequired)
        {
            action();
            return;
        }
        if (!IsHandleCreated) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (CanUpdate()) action();
            }));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    internal static string FormatTime(TimeSpan value) =>
        value < TimeSpan.Zero
            ? "0:00.0"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}.{value.Milliseconds / 100}";

    private int ScaleLogical(int value) =>
        Math.Max(1, (int)Math.Round(value * DeviceDpi / 96d));

    private static Label CreateMutedLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = ClipCordTheme.MutedText,
        Font = ClipCordTheme.InterfaceFont(8.5f),
        Margin = Padding.Empty,
        BackColor = Color.Transparent
    };

    private static OutlineButton CreateTransportButton(string text, int width, bool primary = false) => new()
    {
        Text = text,
        Width = width,
        Height = 36,
        SurfaceColor = primary ? ClipCordTheme.Violet : ClipCordTheme.SurfaceControl,
        HoverColor = primary ? Color.FromArgb(157, 85, 255) : ClipCordTheme.SurfaceControlHover,
        OutlineColor = primary ? ClipCordTheme.Violet : ClipCordTheme.BorderStrong,
        DisabledSurfaceColor = Color.FromArgb(27, 37, 54),
        DisabledTextColor = ClipCordTheme.TextTertiary,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(8.5f, primary ? FontStyle.Bold : FontStyle.Regular),
        Margin = Padding.Empty
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            StopPlayback();
            _positionTimer.Dispose();
            _lifetimeCancellation?.Cancel();
            _lifetimeCancellation?.Dispose();
            _lifetimeCancellation = null;
            _mediaElement.MediaOpened -= MediaOpened;
            _mediaElement.MediaEnded -= MediaEnded;
            _mediaElement.MediaFailed -= MediaFailed;
            _mediaHost.Child = null;
            _mediaHost.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class ControlProgress<T>(ClipPlayerView owner, Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => owner.TryPostToUi(() => handler(value));
    }
}

/// <summary>
/// A single-value scrub track for playback position. The editor's TrimRangeControl covers
/// a start/end pair, which is a different interaction, so position gets its own control
/// rather than a second range with one handle disabled.
/// </summary>
internal sealed class PlaybackSeekBar : Control
{
    private const int LogicalHeight = 26;
    private const int LogicalTrackHeight = 6;
    private const int LogicalThumbDiameter = 14;

    private TimeSpan _duration;
    private TimeSpan _position;
    private bool _dragging;

    internal event Action? SeekStarted;
    internal event Action<double>? SeekCommitted;

    internal PlaybackSeekBar()
    {
        // SetStyle must come first: Control rejects a transparent BackColor outright until
        // SupportsTransparentBackColor is enabled, and the throw takes the whole player down.
        SetStyle(ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        DoubleBuffered = true;
        ResizeRedraw = true;
        Height = LogicalHeight;
        TabStop = true;
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.Slider;
    }

    internal void SetDuration(TimeSpan duration)
    {
        _duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
        Invalidate();
    }

    internal void SetPosition(TimeSpan position)
    {
        _position = position;
        Invalidate();
    }

    private double Fraction => _duration > TimeSpan.Zero
        ? Math.Clamp(_position.TotalSeconds / _duration.TotalSeconds, 0, 1)
        : 0;

    private double FractionAt(int x)
    {
        var metrics = GetPaintMetrics();
        var usable = Math.Max(1, Width - metrics.ThumbDiameter);
        return Math.Clamp((x - (metrics.ThumbDiameter / 2.0)) / usable, 0, 1);
    }

    /// <summary>
    /// Derives paint geometry from the control height that WinForms has already scaled.
    /// DeviceDpi is insufficient for the suite's synthetic startup-DPI passes, while raw
    /// constants leave the thumb at 14 physical pixels on a real 200% display.
    /// </summary>
    internal (int TrackHeight, int ThumbDiameter) GetPaintMetrics()
    {
        var scale = Math.Max(1f / LogicalHeight, Height / (float)LogicalHeight);
        return (
            Math.Max(1, (int)Math.Round(LogicalTrackHeight * scale)),
            Math.Max(1, (int)Math.Round(LogicalThumbDiameter * scale)));
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        if (!Enabled || eventArgs.Button != MouseButtons.Left) return;
        Focus();
        _dragging = true;
        SeekStarted?.Invoke();
        _position = TimeSpan.FromSeconds(_duration.TotalSeconds * FractionAt(eventArgs.X));
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        if (!_dragging) return;
        _position = TimeSpan.FromSeconds(_duration.TotalSeconds * FractionAt(eventArgs.X));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        if (!_dragging) return;
        _dragging = false;
        SeekCommitted?.Invoke(FractionAt(eventArgs.X));
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        base.OnKeyDown(eventArgs);
        if (!Enabled || _duration <= TimeSpan.Zero) return;
        var fraction = Fraction;
        var step = 5.0 / Math.Max(1, _duration.TotalSeconds);
        fraction = eventArgs.KeyCode switch
        {
            Keys.Left => Math.Clamp(fraction - step, 0, 1),
            Keys.Right => Math.Clamp(fraction + step, 0, 1),
            Keys.Home => 0,
            Keys.End => 1,
            _ => -1
        };
        if (fraction < 0) return;
        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;
        _position = TimeSpan.FromSeconds(_duration.TotalSeconds * fraction);
        Invalidate();
        SeekCommitted?.Invoke(fraction);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        if (Width <= 0 || Height <= 0) return;
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var metrics = GetPaintMetrics();
        var trackTop = (Height - metrics.TrackHeight) / 2;
        var trackRect = new Rectangle(
            metrics.ThumbDiameter / 2,
            trackTop,
            Math.Max(1, Width - metrics.ThumbDiameter),
            metrics.TrackHeight);
        using (var background = new SolidBrush(Enabled ? ClipCordTheme.SurfaceControlHover : ClipCordTheme.BorderDefault))
        using (var path = RoundedPanel.CreateRoundedPath(trackRect, metrics.TrackHeight))
        {
            graphics.FillPath(background, path);
        }

        var filledWidth = (int)Math.Round(trackRect.Width * Fraction);
        if (filledWidth > 0)
        {
            var filled = new Rectangle(trackRect.X, trackRect.Y, filledWidth, trackRect.Height);
            using var brush = new SolidBrush(Enabled ? ClipCordTheme.Violet : ClipCordTheme.TextTertiary);
            using var path = RoundedPanel.CreateRoundedPath(filled, metrics.TrackHeight);
            graphics.FillPath(brush, path);
        }

        var thumbX = trackRect.X + filledWidth - (metrics.ThumbDiameter / 2);
        var thumbRect = new Rectangle(
            thumbX,
            (Height - metrics.ThumbDiameter) / 2,
            metrics.ThumbDiameter,
            metrics.ThumbDiameter);
        var paintScale = Height / (float)LogicalHeight;
        using var thumbBrush = new SolidBrush(Enabled ? ClipCordTheme.TextPrimary : ClipCordTheme.TextSecondary);
        using var thumbPen = new Pen(
            Enabled ? ClipCordTheme.Violet : ClipCordTheme.TextTertiary,
            Math.Max(1f, 2f * paintScale));
        graphics.FillEllipse(thumbBrush, thumbRect);
        graphics.DrawEllipse(thumbPen, thumbRect);
        if (Focused)
        {
            using var focusPen = new Pen(ClipCordTheme.Violet, Math.Max(1f, paintScale))
            {
                DashStyle = System.Drawing.Drawing2D.DashStyle.Dot
            };
            graphics.DrawRectangle(focusPen, 0, 0, Width - 1, Height - 1);
        }
    }
}
