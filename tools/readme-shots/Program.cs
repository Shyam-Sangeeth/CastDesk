// Renders the README images without showing anything on screen:
// - Assets\logo-64.png, from the app icon.
// - docs\screenshots\settings.png: the Settings window, drawn at an off-screen position, with default settings
//   (not the user's file).
// - docs\screenshots\lock-screen-centred.jpg and lock-screen-filled.jpg: CastDesk's two lock screen looks, made by
//   CastDesk's own code from a made-up album cover (so no real artwork is copied into the repo), with a clock like
//   the one Windows draws on top. The real lock screen isn't touched.
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Reflection;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var screenshots = Path.Combine(root, "docs", "screenshots");
Directory.CreateDirectory(screenshots);
ApplicationConfiguration.Initialize();

using (var icon = new Icon(Path.Combine(root, "Assets", "CastDesk.ico"), 64, 64))
using (var logo = icon.ToBitmap())
    logo.Save(Path.Combine(root, "Assets", "logo-64.png"), ImageFormat.Png);

LockScreenShots.Render(screenshots);

var settingsFormType = typeof(CastDesk.Configuration.CastDeskOptions).Assembly.GetType("CastDesk.UI.SettingsForm")!;
var form = (Form)Activator.CreateInstance(settingsFormType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
    null, new object?[] { new CastDesk.Configuration.CastDeskOptions(), false, null }, null)!;
form.StartPosition = FormStartPosition.Manual;
form.Location = new Point(-5000, -5000);
form.ShowInTaskbar = false;
form.Shown += (_, _) => form.BeginInvoke(() =>
{
    form.ActiveControl = null; // no selected text in the name box
    using var shot = new Bitmap(form.Width, form.Height);
    form.DrawToBitmap(shot, new Rectangle(Point.Empty, form.Size));
    shot.Save(Path.Combine(screenshots, "settings.png"), ImageFormat.Png);
    form.Close();
});
Application.Run(form);

internal static class LockScreenShots
{
    private static readonly Size Screen = new(1920, 1080);
    private static readonly Size Saved = new(1280, 720);

    public static void Render(string folder)
    {
        var artwork = typeof(CastDesk.Configuration.CastDeskOptions).Assembly.GetType("CastDesk.Media.LockScreenArtwork")!;
        // Like YouTube's maxresdefault.jpg for a song: the square cover between dark gray bars, which CastDesk crops off.
        using var thumbnail = MakeThumbnail();
        foreach (var (method, name) in new[] { ("ComposePicture", "lock-screen-centred.jpg"), ("ComposeFilledPicture", "lock-screen-filled.jpg") })
        {
            var compose = artwork.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            using var picture = (Bitmap)compose.Invoke(null, [thumbnail, Screen])!;
            DrawClock(picture);
            using var small = new Bitmap(Saved.Width, Saved.Height);
            using (var graphics = Graphics.FromImage(small))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(picture, new Rectangle(Point.Empty, Saved));
            }
            SaveJpeg(small, Path.Combine(folder, name));
        }
    }

    private static Bitmap MakeThumbnail()
    {
        var thumbnail = new Bitmap(1280, 720);
        using var graphics = Graphics.FromImage(thumbnail);
        graphics.Clear(Color.FromArgb(28, 28, 28));
        using var cover = MakeCover(720);
        graphics.DrawImage(cover, (1280 - 720) / 2, 0, 720, 720);
        return thumbnail;
    }

    /// <summary>A made-up "synthwave" album cover: a striped sun setting behind mountains.</summary>
    private static Bitmap MakeCover(int side)
    {
        var cover = new Bitmap(side, side);
        using var graphics = Graphics.FromImage(cover);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var all = new Rectangle(0, 0, side, side);

        using (var sky = new LinearGradientBrush(all, Color.Black, Color.Black, LinearGradientMode.Vertical))
        {
            sky.InterpolationColors = new ColorBlend
            {
                Colors = [Color.FromArgb(18, 8, 48), Color.FromArgb(92, 24, 112), Color.FromArgb(214, 64, 104), Color.FromArgb(255, 140, 70)],
                Positions = [0f, 0.45f, 0.75f, 1f],
            };
            graphics.FillRectangle(sky, all);
        }

        // Stars.
        var random = new Random(7);
        for (var i = 0; i < 70; i++)
        {
            var size = random.Next(1, 4);
            using var star = new SolidBrush(Color.FromArgb(random.Next(90, 220), 255, 240, 255));
            graphics.FillEllipse(star, random.Next(side), random.Next(side / 2), size, size);
        }

        // The sun, with bands cut out of its lower half.
        var sun = new RectangleF(side * 0.2f, side * 0.22f, side * 0.6f, side * 0.6f);
        using (var sunPath = new GraphicsPath())
        {
            sunPath.AddEllipse(sun);
            using var region = new Region(sunPath);
            for (var i = 0; i < 6; i++)
            {
                var y = sun.Top + sun.Height * (0.55f + i * 0.075f);
                region.Exclude(new RectangleF(0, y, side, 4 + i * 2.2f));
            }
            using var sunBrush = new LinearGradientBrush(sun, Color.FromArgb(255, 236, 120), Color.FromArgb(255, 70, 120), LinearGradientMode.Vertical);
            graphics.SetClip(region, CombineMode.Replace); // the bands; the ellipse keeps a smooth edge
            graphics.FillEllipse(sunBrush, sun);
            graphics.ResetClip();
        }

        // Two rows of mountains.
        FillMountains(graphics, side, side * 0.70f, side * 0.16f, Color.FromArgb(70, 20, 90), 11);
        FillMountains(graphics, side, side * 0.80f, side * 0.12f, Color.FromArgb(26, 10, 46), 5);
        return cover;
    }

    private static void FillMountains(Graphics graphics, int side, float baseLine, float height, Color colour, int seed)
    {
        var random = new Random(seed);
        var points = new List<PointF> { new(0, side) };
        for (var x = 0f; x <= side; x += side / 9f)
            points.Add(new PointF(x, baseLine - (float)random.NextDouble() * height));
        points.Add(new PointF(side, side));
        using var brush = new SolidBrush(colour);
        graphics.FillPolygon(brush, points.ToArray());
    }

    /// <summary>The time and date, placed and sized roughly as Windows 11 draws them over the lock screen picture.</summary>
    private static void DrawClock(Bitmap picture)
    {
        using var graphics = Graphics.FromImage(picture);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var timeFont = new Font("Segoe UI Semibold", 96, GraphicsUnit.Pixel);
        using var dateFont = new Font("Segoe UI", 26, GraphicsUnit.Pixel);
        using var centred = new StringFormat { Alignment = StringAlignment.Center };
        DrawShadowed(graphics, "10:24", timeFont, new RectangleF(0, 56, picture.Width, 140), centred);
        DrawShadowed(graphics, "Monday, 9 November", dateFont, new RectangleF(0, 178, picture.Width, 40), centred);
    }

    private static void DrawShadowed(Graphics graphics, string text, Font font, RectangleF area, StringFormat format)
    {
        using var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0));
        var offset = area;
        offset.Offset(0, 2);
        graphics.DrawString(text, font, shadow, offset, format);
        graphics.DrawString(text, font, Brushes.White, area, format);
    }

    private static void SaveJpeg(Bitmap picture, string path)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(encoder => encoder.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
        picture.Save(path, codec, parameters);
    }
}
