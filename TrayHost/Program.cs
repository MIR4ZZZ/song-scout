using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SongScout;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(true, @"Local\SongScout.App", out var firstInstance);
        if (!firstInstance)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
        GC.KeepAlive(singleInstance);
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon trayIcon;
    private readonly ToolStripMenuItem identifyItem;
    private readonly ToolStripMenuItem lastResultItem;
    private readonly ToolStripMenuItem notificationsItem;
    private readonly ToolStripMenuItem updatesItem;
    private readonly UpdateService updater;
    private Process? worker;
    private ResultPopup? popup;
    private RecognitionResult? lastResult;
    private volatile bool isBusy;

    public TrayContext()
    {
        identifyItem = new ToolStripMenuItem("Identify song", null, async (_, _) => await IdentifyAsync());
        identifyItem.Font = new Font(identifyItem.Font, FontStyle.Bold);
        lastResultItem = new ToolStripMenuItem("Show last result", null, (_, _) => ShowLastResult()) { Enabled = false };
        notificationsItem = new ToolStripMenuItem("Windows result notifications")
        {
            CheckOnClick = true,
            Checked = AppSettings.WindowsNotifications,
        };
        notificationsItem.CheckedChanged += (_, _) => AppSettings.SaveWindowsNotifications(notificationsItem.Checked);
        updater = new UpdateService(() => !isBusy, ExitApp);
        updatesItem = new ToolStripMenuItem("Check for updates", null, (_, _) => updater.CheckNow());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            identifyItem,
            lastResultItem,
            new ToolStripSeparator(),
            notificationsItem,
            new ToolStripSeparator(),
            updatesItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit Song Scout", null, (_, _) => ExitApp()),
        ]);

        trayIcon = new NotifyIcon
        {
            Icon = TrayIconFactory.Create(),
            Text = "Song Scout — click to identify",
            ContextMenuStrip = menu,
            Visible = true,
        };
        trayIcon.MouseClick += async (_, eventArgs) =>
        {
            if (eventArgs.Button != MouseButtons.Left)
                return;
            if (isBusy)
                TogglePopup();
            else
                await IdentifyAsync();
        };
        trayIcon.BalloonTipClicked += (_, _) => ShowLastResult();
        updatesItem.Enabled = updater.Start();
    }

    private ResultPopup GetPopup()
    {
        if (popup is not null && !popup.IsDisposed)
            return popup;

        popup = new ResultPopup();
        popup.ListenAgainRequested += async (_, _) => await IdentifyAsync();
        return popup;
    }

    private async Task IdentifyAsync()
    {
        if (isBusy)
            return;

        var recognizerPath = Path.Combine(AppContext.BaseDirectory, "SongScoutRecognizer.exe");
        if (!File.Exists(recognizerPath))
        {
            ShowStatus("Recognizer missing", "Reinstall Song Scout to restore its recognition engine.");
            return;
        }

        isBusy = true;
        identifyItem.Enabled = false;
        trayIcon.Text = "Song Scout — listening";
        var resultPopup = GetPopup();
        resultPopup.ShowListening();
        ShowPopup(resultPopup);

        try
        {
            var startInfo = new ProcessStartInfo(recognizerPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            worker = Process.Start(startInfo) ?? throw new InvalidOperationException("The recognition engine could not start.");
            var outputTask = worker.StandardOutput.ReadToEndAsync();
            var errorTask = worker.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await worker.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;

            var result = JsonSerializer.Deserialize<RecognitionResult>(
                output.Trim(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );
            if (result is null)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Recognition returned no result." : error.Trim());

            lastResult = result;
            lastResultItem.Enabled = true;
            resultPopup.ShowResult(result);
            ShowPopup(resultPopup);
            if (notificationsItem.Checked)
                trayIcon.ShowBalloonTip(4000, result.Ok ? result.Title ?? "Song recognized" : "No match", result.Ok ? result.Artist ?? "" : result.Status ?? "Try again.", result.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        catch (OperationCanceledException)
        {
            worker?.Kill(entireProcessTree: true);
            ShowStatus("Recognition timed out", "Check the internet connection and try again.");
        }
        catch (Exception error)
        {
            ShowStatus("Couldn’t identify the song", error.Message);
        }
        finally
        {
            worker?.Dispose();
            worker = null;
            isBusy = false;
            identifyItem.Enabled = true;
            trayIcon.Text = "Song Scout — click to identify";
        }
    }

    private void ShowLastResult()
    {
        if (lastResult is null)
            return;
        var resultPopup = GetPopup();
        resultPopup.ShowResult(lastResult);
        ShowPopup(resultPopup);
    }

    private static void ShowPopup(ResultPopup resultPopup)
    {
        resultPopup.PlaceNearTaskbar();
        resultPopup.ShowAnimated();
        resultPopup.Activate();
    }

    private void TogglePopup()
    {
        var resultPopup = GetPopup();
        if (resultPopup.Visible)
            resultPopup.Hide();
        else
            ShowPopup(resultPopup);
    }

    private void ShowStatus(string title, string message)
    {
        lastResult = new RecognitionResult(false, message, title, null, null, null);
        lastResultItem.Enabled = true;
        var resultPopup = GetPopup();
        resultPopup.ShowResult(lastResult);
        ShowPopup(resultPopup);
        if (notificationsItem.Checked)
            trayIcon.ShowBalloonTip(4000, title, message, ToolTipIcon.Warning);
    }

    private void ExitApp()
    {
        updater.Dispose();
        if (worker is { HasExited: false })
            worker.Kill(entireProcessTree: true);
        popup?.Dispose();
        trayIcon.Visible = false;
        trayIcon.Icon?.Dispose();
        trayIcon.Dispose();
        ExitThread();
    }
}

internal sealed class ResultPopup : Form
{
    private static readonly Color Background = Color.FromArgb(21, 23, 24);
    private static readonly Color Surface = Color.FromArgb(37, 40, 42);
    private static readonly Color Border = Color.FromArgb(59, 62, 64);
    private static readonly Color Accent = Color.FromArgb(227, 232, 235);
    private static readonly Color Muted = Color.FromArgb(171, 176, 178);
    private static readonly HttpClient ArtworkClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly Label stateLabel;
    private readonly RoundedPanel artworkPanel;
    private readonly PictureBox artwork;
    private readonly ArtworkPlaceholder artworkFallback;
    private readonly WaveformControl waveform;
    private readonly Label titleLabel;
    private readonly Label artistLabel;
    private readonly Label detailLabel;
    private readonly ReactiveEqualizerStrip equalizerStrip;
    private readonly RoundedButton primaryButton;
    private readonly RoundedButton secondaryButton;
    private readonly System.Windows.Forms.Timer progressTimer;
    private readonly System.Windows.Forms.Timer entranceTimer;
    private DateTime listeningStarted;
    private DateTime entranceStarted;
    private Point entranceTarget;
    private double entranceStartOpacity;
    private Uri? songUri;
    private int artworkRequest;

    public event EventHandler? ListenAgainRequested;

    public ResultPopup()
    {
        Text = "Song Scout";
        AccessibleName = "Song Scout result";
        BackColor = Background;
        ForeColor = Color.White;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(430, 454);
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        KeyPreview = true;
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Escape)
                Hide();
        };

        stateLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 9.5f),
            ForeColor = Accent,
            Location = new Point(27, 22),
        };
        var closeButton = CreateButton("", new Point(367, 12), new Size(40, 40), Surface, Color.White);
        closeButton.AccessibleName = "Hide Song Scout";
        closeButton.CornerRadius = 20;
        closeButton.Glyph = ButtonGlyph.Close;
        closeButton.Click += (_, _) => Hide();

        artworkPanel = new RoundedPanel
        {
            BackColor = Surface,
            CornerRadius = 20,
            Location = new Point(117, 57),
            Size = new Size(196, 196),
        };
        artwork = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            Visible = false,
        };
        artworkFallback = new ArtworkPlaceholder
        {
            Dock = DockStyle.Fill,
            ForeColor = Accent,
        };
        waveform = new WaveformControl
        {
            AccentColor = Accent,
            Dock = DockStyle.Fill,
            Visible = false,
        };
        artworkPanel.Controls.Add(artwork);
        artworkPanel.Controls.Add(artworkFallback);
        artworkPanel.Controls.Add(waveform);

        titleLabel = new Label
        {
            AutoEllipsis = true,
            Font = new Font("Segoe UI Semibold", 18),
            ForeColor = Color.White,
            Location = new Point(27, 270),
            Size = new Size(376, 36),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        artistLabel = new Label
        {
            AutoEllipsis = true,
            Font = new Font("Segoe UI", 11.5f),
            ForeColor = Muted,
            Location = new Point(27, 307),
            Size = new Size(376, 26),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        detailLabel = new Label
        {
            AutoEllipsis = true,
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(149, 154, 156),
            Location = new Point(27, 334),
            Size = new Size(376, 23),
            TextAlign = ContentAlignment.MiddleCenter,
        };

        equalizerStrip = new ReactiveEqualizerStrip
        {
            BaseColor = Color.FromArgb(67, 71, 73),
            AccentColor = Accent,
            Location = new Point(27, 363),
            Size = new Size(376, 28),
        };

        primaryButton = CreateButton("Open song", new Point(219, 397), new Size(184, 44), Accent, Background);
        primaryButton.Font = new Font("Segoe UI Semibold", 9.5f);
        primaryButton.Glyph = ButtonGlyph.Open;
        primaryButton.Click += (_, _) =>
        {
            if (songUri is not null)
                Process.Start(new ProcessStartInfo(songUri.AbsoluteUri) { UseShellExecute = true });
            else
                ListenAgainRequested?.Invoke(this, EventArgs.Empty);
        };
        secondaryButton = CreateButton("Listen again", new Point(27, 397), new Size(184, 44), Surface, Color.White);
        secondaryButton.Glyph = ButtonGlyph.Replay;
        secondaryButton.Click += (_, _) => ListenAgainRequested?.Invoke(this, EventArgs.Empty);

        Controls.Add(stateLabel);
        Controls.Add(closeButton);
        Controls.Add(artworkPanel);
        Controls.Add(titleLabel);
        Controls.Add(artistLabel);
        Controls.Add(detailLabel);
        Controls.Add(equalizerStrip);
        Controls.Add(primaryButton);
        Controls.Add(secondaryButton);

        progressTimer = new System.Windows.Forms.Timer { Interval = 80 };
        progressTimer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - listeningStarted).TotalSeconds;
            var fraction = Math.Clamp(elapsed / 8, 0, 1);
            var motionEnabled = SystemInformation.IsMenuAnimationEnabled;
            equalizerStrip.Progress = fraction;
            equalizerStrip.Phase = motionEnabled ? elapsed * 4 : 0;
            waveform.Phase = motionEnabled ? elapsed * 6 : 0.8;
            var pulse = motionEnabled ? (Math.Sin(elapsed * 5) + 1) / 2 : 0.35;
            artworkPanel.BackColor = Blend(Surface, Color.FromArgb(49, 53, 55), pulse * 0.65);
            artworkFallback.ForeColor = Blend(Accent, Color.FromArgb(238, 240, 239), pulse * 0.5);
        };

        entranceTimer = new System.Windows.Forms.Timer { Interval = 15 };
        entranceTimer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - entranceStarted).TotalMilliseconds;
            var progress = Math.Clamp(elapsed / 190, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 4);
            Opacity = entranceStartOpacity + ((1 - entranceStartOpacity) * eased);
            Location = new Point(entranceTarget.X, entranceTarget.Y + (int)Math.Round(8 * (1 - eased)));
            if (progress >= 1)
            {
                entranceTimer.Stop();
                Opacity = 1;
                Location = entranceTarget;
            }
        };
    }

    public void PlaceNearTaskbar()
    {
        var workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(
            Math.Max(workArea.Left + 8, workArea.Right - Width - 16),
            Math.Max(workArea.Top + 8, workArea.Bottom - Height - 16));
    }

    public void ShowAnimated()
    {
        entranceTarget = Location;
        if (!SystemInformation.IsMenuAnimationEnabled)
        {
            Opacity = 1;
            if (!Visible)
                Show();
            return;
        }

        entranceStartOpacity = Visible ? 0.88 : 0;
        Opacity = entranceStartOpacity;
        Location = new Point(entranceTarget.X, entranceTarget.Y + 8);
        if (!Visible)
            Show();
        entranceStarted = DateTime.UtcNow;
        entranceTimer.Start();
    }

    public void ShowListening()
    {
        artworkRequest++;
        ClearArtwork();
        artworkFallback.Visible = false;
        waveform.Visible = true;
        waveform.BringToFront();
        stateLabel.Text = "Listening";
        titleLabel.Text = "Listening to this device";
        artistLabel.Text = "Keep the song playing";
        detailLabel.Text = "Microphone stays off";
        equalizerStrip.Visible = true;
        equalizerStrip.Progress = 0;
        equalizerStrip.Phase = 0;
        primaryButton.Visible = false;
        secondaryButton.Visible = false;
        songUri = null;
        listeningStarted = DateTime.UtcNow;
        progressTimer.Start();
    }

    public void ShowResult(RecognitionResult result)
    {
        progressTimer.Stop();
        equalizerStrip.Visible = false;
        artworkPanel.BackColor = Surface;
        artworkFallback.ForeColor = Accent;
        songUri = null;
        var request = ++artworkRequest;
        waveform.Visible = false;
        ClearArtwork();

        if (result.Ok)
        {
            artworkFallback.ShowBrandMark = false;
            stateLabel.Text = "Match found";
            titleLabel.Text = result.Title ?? "Unknown song";
            artistLabel.Text = result.Artist ?? "Unknown artist";
            detailLabel.Text = "Captured from Windows audio";
            primaryButton.Text = "Open song";
            primaryButton.Glyph = ButtonGlyph.Open;
            primaryButton.Visible = Uri.TryCreate(result.Url, UriKind.Absolute, out songUri);
            secondaryButton.Text = "Listen again";
            secondaryButton.Visible = true;
            if (!string.IsNullOrWhiteSpace(result.Cover))
                _ = LoadArtworkAsync(result.Cover, request);
        }
        else
        {
            artworkFallback.ShowBrandMark = true;
            var isError = !string.IsNullOrWhiteSpace(result.Title);
            stateLabel.Text = isError ? "Needs attention" : "No match";
            titleLabel.Text = result.Title ?? "Couldn’t identify that";
            artistLabel.Text = result.Status ?? "Try a clearer section of the song.";
            detailLabel.Text = isError ? "Song Scout couldn’t complete this request" : "Try a louder or less crowded section";
            primaryButton.Text = "Try again";
            primaryButton.Glyph = ButtonGlyph.Replay;
            primaryButton.Visible = true;
            secondaryButton.Visible = false;
        }
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        var preference = 2;
        DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int));
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        using var pen = new Pen(Border);
        eventArgs.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            progressTimer.Dispose();
            entranceTimer.Dispose();
            artwork.Image?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static RoundedButton CreateButton(string text, Point location, Size size, Color background, Color foreground)
    {
        var button = new RoundedButton
        {
            Text = text,
            Location = location,
            Size = size,
            BackColor = background,
            ForeColor = foreground,
            CornerRadius = 12,
            Cursor = Cursors.Hand,
            HoverBackColor = background == Surface ? Color.FromArgb(49, 52, 54) : Color.FromArgb(228, 235, 232),
            PressedBackColor = background == Surface ? Color.FromArgb(30, 33, 35) : Color.FromArgb(181, 193, 189),
            BorderColor = background == Surface ? Border : background,
        };
        return button;
    }

    private async Task LoadArtworkAsync(string url, int request)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var artworkUri) || artworkUri.Scheme != Uri.UriSchemeHttps)
                return;
            var bytes = await ArtworkClient.GetByteArrayAsync(artworkUri);
            if (bytes.Length > 5_000_000)
                return;
            using var stream = new MemoryStream(bytes);
            using var source = Image.FromStream(stream);
            var image = new Bitmap(source);
            if (request != artworkRequest || IsDisposed)
            {
                image.Dispose();
                return;
            }

            artwork.Image?.Dispose();
            artwork.Image = image;
            artworkFallback.Visible = false;
            artwork.Visible = true;
        }
        catch
        {
            // Album art is optional; recognition remains useful without it.
        }
    }

    private void ClearArtwork()
    {
        artwork.Image?.Dispose();
        artwork.Image = null;
        artwork.Visible = false;
        artworkFallback.Visible = true;
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(from.R + ((to.R - from.R) * amount)),
            (int)Math.Round(from.G + ((to.G - from.G) * amount)),
            (int)Math.Round(from.B + ((to.B - from.B) * amount))
        );
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

internal enum ButtonGlyph { None, Close, Replay, Open }

internal sealed class ArtworkPlaceholder : Control
{
    private bool showBrandMark = true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowBrandMark
    {
        get => showBrandMark;
        set
        {
            showBrandMark = value;
            Invalidate();
        }
    }

    public ArtworkPlaceholder()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new LinearGradientBrush(ClientRectangle, Color.FromArgb(44, 47, 49), Color.FromArgb(32, 35, 37), 45f);
        graphics.FillRectangle(background, ClientRectangle);
        if (ShowBrandMark)
            BrandMark.Draw(graphics, ClientRectangle, ForeColor);
        else
            TextRenderer.DrawText(graphics, "Artwork unavailable", Font, ClientRectangle,
                Color.FromArgb(171, 176, 178), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

internal sealed class RoundedButton : Button
{
    private readonly System.Windows.Forms.Timer colorTimer;
    private Color animatedBackColor;
    private Color targetBackColor;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 10;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HoverBackColor { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color PressedBackColor { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ButtonGlyph Glyph { get; set; }

    public RoundedButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        animatedBackColor = BackColor;
        targetBackColor = BackColor;
        colorTimer = new System.Windows.Forms.Timer { Interval = 15 };
        colorTimer.Tick += (_, _) =>
        {
            animatedBackColor = BlendToward(animatedBackColor, targetBackColor, 0.28);
            Invalidate();
            if (ColorDistance(animatedBackColor, targetBackColor) <= 3)
            {
                animatedBackColor = targetBackColor;
                colorTimer.Stop();
            }
        };
    }

    protected override void OnBackColorChanged(EventArgs eventArgs)
    {
        base.OnBackColorChanged(eventArgs);
        if (colorTimer is null || !colorTimer.Enabled)
        {
            animatedBackColor = BackColor;
            targetBackColor = BackColor;
        }
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        base.OnMouseEnter(eventArgs);
        AnimateTo(HoverBackColor);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        AnimateTo(BackColor);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        AnimateTo(PressedBackColor);
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        AnimateTo(ClientRectangle.Contains(eventArgs.Location) ? HoverBackColor : BackColor);
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        base.OnKeyDown(eventArgs);
        if (eventArgs.KeyCode is Keys.Space or Keys.Enter)
            AnimateTo(PressedBackColor);
    }

    protected override void OnKeyUp(KeyEventArgs eventArgs)
    {
        base.OnKeyUp(eventArgs);
        AnimateTo(BackColor);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = UiGeometry.RoundedRectangle(bounds, CornerRadius);
        using var fill = new SolidBrush(Enabled ? animatedBackColor : Color.FromArgb(42, 45, 47));
        using var border = new Pen(BorderColor);
        eventArgs.Graphics.FillPath(fill, path);
        eventArgs.Graphics.DrawPath(border, path);

        var textColor = Enabled ? ForeColor : Color.FromArgb(128, 133, 135);
        var labelBounds = ClientRectangle;
        if (Glyph != ButtonGlyph.None && Glyph != ButtonGlyph.Close)
            labelBounds = new Rectangle(16, 0, Width, Height);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            Text,
            Font,
            labelBounds,
            textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
        );
        using var glyphPen = new Pen(textColor, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (Glyph == ButtonGlyph.Close)
        {
            eventArgs.Graphics.DrawLine(glyphPen, Width / 2f - 5, Height / 2f - 5, Width / 2f + 5, Height / 2f + 5);
            eventArgs.Graphics.DrawLine(glyphPen, Width / 2f + 5, Height / 2f - 5, Width / 2f - 5, Height / 2f + 5);
        }
        else if (Glyph == ButtonGlyph.Open)
        {
            var x = Width / 2f - 39;
            var y = Height / 2f;
            eventArgs.Graphics.DrawLine(glyphPen, x - 4, y + 5, x + 6, y - 5);
            eventArgs.Graphics.DrawLines(glyphPen, [new PointF(x, y - 5), new PointF(x + 6, y - 5), new PointF(x + 6, y + 1)]);
        }
        else if (Glyph == ButtonGlyph.Replay)
        {
            var x = Width / 2f - 43;
            var y = Height / 2f;
            eventArgs.Graphics.DrawArc(glyphPen, x - 6, y - 6, 12, 12, -55, 310);
            eventArgs.Graphics.DrawLines(glyphPen, [new PointF(x - 6, y - 7), new PointF(x - 6, y - 1), new PointF(x, y - 1)]);
        }

        if (Focused && ShowFocusCues)
        {
            var focusBounds = Rectangle.Inflate(ClientRectangle, -4, -4);
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, focusBounds, textColor, animatedBackColor);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            colorTimer.Dispose();
        base.Dispose(disposing);
    }

    private void AnimateTo(Color color)
    {
        targetBackColor = color;
        if (!SystemInformation.IsMenuAnimationEnabled)
        {
            animatedBackColor = color;
            Invalidate();
            return;
        }
        colorTimer.Start();
    }

    private static Color BlendToward(Color from, Color to, double amount) => Color.FromArgb(
        (int)Math.Round(from.R + ((to.R - from.R) * amount)),
        (int)Math.Round(from.G + ((to.G - from.G) * amount)),
        (int)Math.Round(from.B + ((to.B - from.B) * amount))
    );

    private static int ColorDistance(Color a, Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
}

internal sealed class RoundedPanel : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 12;

    protected override void OnSizeChanged(EventArgs eventArgs)
    {
        base.OnSizeChanged(eventArgs);
        if (Width <= 0 || Height <= 0)
            return;
        using var path = UiGeometry.RoundedRectangle(new RectangleF(0, 0, Width, Height), CornerRadius);
        var oldRegion = Region;
        Region = new Region(path);
        oldRegion?.Dispose();
    }
}

internal sealed class ReactiveEqualizerStrip : Control
{
    private double progress;
    private double phase;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BaseColor { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color AccentColor { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Progress
    {
        get => progress;
        set
        {
            progress = Math.Clamp(value, 0, 1);
            Invalidate();
        }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Phase
    {
        get => phase;
        set
        {
            phase = value;
            Invalidate();
        }
    }

    public ReactiveEqualizerStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        const int bars = 31;
        const float barWidth = 5;
        const float edge = 4;
        var availableWidth = Math.Max(1, Width - (edge * 2));
        var gap = (availableWidth - (bars * barWidth)) / (bars - 1);

        for (var index = 0; index < bars; index++)
        {
            var energy = (Math.Sin(phase + (index * 0.44)) + Math.Cos((phase * 0.68) - (index * 0.31)) + 2) / 4;
            var height = 8 + (float)(energy * 26);
            var x = edge + (index * (barWidth + gap));
            var bounds = new RectangleF(x, (Height - height) / 2, barWidth, height);
            var active = index / (double)(bars - 1) <= progress;
            var color = active ? AccentColor : BaseColor;
            using var brush = new SolidBrush(color);
            using var path = UiGeometry.RoundedRectangle(bounds, barWidth / 2);
            eventArgs.Graphics.FillPath(brush, path);
        }
    }
}

internal sealed class WaveformControl : Control
{
    private double phase;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color AccentColor { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Phase
    {
        get => phase;
        set
        {
            phase = value;
            Invalidate();
        }
    }

    public WaveformControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        const int bars = 5;
        const float barWidth = 5;
        const float gap = 6;
        var totalWidth = (bars * barWidth) + ((bars - 1) * gap);
        var left = (Width - totalWidth) / 2;
        using var brush = new SolidBrush(AccentColor);

        for (var index = 0; index < bars; index++)
        {
            var wave = (Math.Sin(phase + (index * 0.9)) + 1) / 2;
            var height = 16 + (float)(wave * 30);
            var bounds = new RectangleF(left + (index * (barWidth + gap)), (Height - height) / 2, barWidth, height);
            using var path = UiGeometry.RoundedRectangle(bounds, barWidth / 2);
            eventArgs.Graphics.FillPath(brush, path);
        }
    }
}

internal static class UiGeometry
{
    public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal static class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Song Scout",
        "settings.json"
    );

    public static bool WindowsNotifications
    {
        get
        {
            try
            {
                return JsonSerializer.Deserialize<SettingsModel>(File.ReadAllText(SettingsPath))?.WindowsNotifications ?? false;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void SaveWindowsNotifications(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new SettingsModel(enabled)));
        }
        catch
        {
            // A read-only profile should not prevent recognition from working.
        }
    }

    private sealed record SettingsModel(bool WindowsNotifications);
}

internal static class BrandMark
{
    public static void Draw(Graphics graphics, RectangleF bounds, Color accent, bool tile = false)
    {
        var state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TranslateTransform(bounds.Left, bounds.Top);
        graphics.ScaleTransform(bounds.Width / 256f, bounds.Height / 256f);
        if (tile)
        {
            using var tileBrush = new SolidBrush(Color.FromArgb(21, 23, 24));
            using var tilePath = UiGeometry.RoundedRectangle(new RectangleF(8, 8, 240, 240), 58);
            graphics.FillPath(tileBrush, tilePath);
        }
        using var ring = new GraphicsPath();
        ring.AddBezier(new Point(178, 65), new Point(144, 40), new Point(89, 46), new Point(61, 84));
        ring.AddBezier(new Point(61, 84), new Point(30, 126), new Point(42, 183), new Point(88, 207));
        ring.AddBezier(new Point(88, 207), new Point(119, 223), new Point(153, 214), new Point(179, 191));
        using var ringPen = new Pen(accent, 22) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        graphics.DrawPath(ringPen, ring);
        using var pulseBrush = new SolidBrush(accent);
        using var beaconBrush = new SolidBrush(Color.FromArgb(102, 137, 240));
        using var leftBar = UiGeometry.RoundedRectangle(new RectangleF(94, 109, 19, 38), 9.5f);
        using var middleBar = UiGeometry.RoundedRectangle(new RectangleF(122, 92, 19, 72), 9.5f);
        using var rightBar = UiGeometry.RoundedRectangle(new RectangleF(150, 110, 19, 36), 9.5f);
        graphics.FillPath(pulseBrush, leftBar);
        graphics.FillPath(beaconBrush, middleBar);
        graphics.FillPath(pulseBrush, rightBar);
        graphics.Restore(state);
    }
}

internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        BrandMark.Draw(graphics, new RectangleF(0, 0, 32, 32), Color.FromArgb(227, 232, 235), true);

        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}

internal sealed record RecognitionResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("artist")] string? Artist,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("cover")] string? Cover
);
