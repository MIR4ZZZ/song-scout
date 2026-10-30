using System.Drawing.Imaging;
using SongScout;

internal static class Preview
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var popup = new ResultPopup();
        popup.ShowResult(new RecognitionResult(
            true,
            "Recognized",
            "I Will Survive",
            "Gloria Gaynor",
            "https://www.shazam.com/track/53982678/i-will-survive",
            "https://is1-ssl.mzstatic.com/image/thumb/Music123/v4/0c/72/a1/0c72a174-9208-6c86-e479-79865376e904/19UMGIM66006.rgb.jpg/400x400cc.jpg"
        ));
        popup.Show();
        var albumArt = popup.Controls.OfType<RoundedPanel>()
            .SelectMany(panel => panel.Controls.OfType<PictureBox>()).Single();
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!albumArt.Visible && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(30);
        }
        if (!albumArt.Visible)
            throw new InvalidOperationException("The matched song's cover art did not appear.");
        using var image = new Bitmap(popup.Width, popup.Height);
        popup.DrawToBitmap(image, new Rectangle(Point.Empty, popup.Size));
        image.Save(args[0], ImageFormat.Png);

        popup.ShowListening();
        Application.DoEvents();
        using var listening = new Bitmap(popup.Width, popup.Height);
        popup.DrawToBitmap(listening, new Rectangle(Point.Empty, popup.Size));
        listening.Save(args[1], ImageFormat.Png);

        if (args.Length > 2)
        {
            popup.ShowResult(new RecognitionResult(false, "No match", null, null, null, null));
            Application.DoEvents();
            using var unmatched = new Bitmap(popup.Width, popup.Height);
            popup.DrawToBitmap(unmatched, new Rectangle(Point.Empty, popup.Size));
            unmatched.Save(args[2], ImageFormat.Png);
        }

        if (args.Length > 3)
        {
            var close = popup.Controls.OfType<RoundedButton>().Single(button => button.Glyph == ButtonGlyph.Close);
            close.Focus();
            Application.DoEvents();
            using var focused = new Bitmap(popup.Width, popup.Height);
            popup.DrawToBitmap(focused, new Rectangle(Point.Empty, popup.Size));
            focused.Save(args[3], ImageFormat.Png);
            foreach (var corner in new[]
            {
                new Point(close.Left + 1, close.Top + 1),
                new Point(close.Right - 2, close.Top + 1),
                new Point(close.Left + 1, close.Bottom - 2),
                new Point(close.Right - 2, close.Bottom - 2),
            })
            {
                if (focused.GetPixel(corner.X, corner.Y).ToArgb() != popup.BackColor.ToArgb())
                    throw new InvalidOperationException("The close button left a rectangular corner on the popup.");
            }
        }
    }
}
