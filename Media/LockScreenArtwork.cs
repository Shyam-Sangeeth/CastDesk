using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using CastDesk.Configuration;
using CastDesk.Diagnostics;
using CastDesk.Tv;
using Microsoft.Win32;
using Windows.Storage;
using Windows.System.UserProfile;

namespace CastDesk.Media;

/// <summary>
/// While a video plays, makes its thumbnail the lock screen picture, and puts the user's lock screen back when
/// playback stops or (after a crash) the next time CastDesk starts.
/// </summary>
/// <remarks>
/// The only way to show a picture there is to replace the lock screen picture (an app's window can't appear over the
/// lock screen). Doing so also turns off Windows Spotlight and slideshow, so <see cref="LockScreenBackup"/> records
/// them and the picture before the first change. Their registry values aren't documented by Microsoft: they were
/// found by comparing the registry before and after a change. All work runs on one background task, so nothing here
/// waits on the UI thread and <see cref="Dispose"/> can wait for the restore.
/// <para>
/// The picture is set while playing, not when the PC locks: Windows takes about 0.8 s to apply one, so setting it on
/// lock shows the user's own lock screen for a moment first. Windows only reads the Spotlight and slideshow switches
/// when the lock screen starts. If playback stops while locked, the screen shows the user's underlying picture, and
/// Spotlight or the slideshow returns on the next lock.
/// </para>
/// </remarks>
internal sealed class LockScreenArtwork : IDisposable
{
    /// <summary>Reports between two songs say "not playing": wait this long before restoring the lock screen.</summary>
    private static readonly TimeSpan StoppedGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RestoreOnExitTimeout = TimeSpan.FromSeconds(4);
    private const int MaxDownloadBytes = 20 * 1024 * 1024;
    private const int MaxScreenSide = 3840;

    /// <summary>
    /// Art with a shorter side below this looks blurred on a full screen, so the lock screen is left alone.
    /// YouTube's largest thumbnails (1280x720, or a 720x720 cover inside bars) pass; its 640x480 and 480x360 ones don't.
    /// </summary>
    private const int MinArtSide = 600;

    private const string SpotlightKey = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string SubscriptionsKey = SpotlightKey + @"\Subscriptions";
    private const string LockScreenKey = @"Software\Microsoft\Windows\CurrentVersion\Lock Screen";
    private const string SpotlightEnabledValue = "RotatingLockScreenEnabled";
    private const string SpotlightOverlayValue = "RotatingLockScreenOverlayEnabled";
    private const string SlideshowEnabledValue = "SlideshowEnabled";
    private const string SubscriptionContextValue = "SubscriptionContext";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = MaxDownloadBytes };

    private static string Folder { get; } = Path.Combine(CastDeskOptions.DataDirectory, "LockScreen");
    private static string BackupFile { get; } = Path.Combine(Folder, "backup.json");

    // Shared with the UI thread.
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Task _worker;
    private MediaState _state = MediaState.Nothing;
    private bool _enabled;
    private bool _closing;

    // Only the worker uses these.
    private string? _shownKey;
    private string? _failedKey;
    private string? _currentPicture;
    private DateTime? _stoppedAt;
    private bool _blockedLogged;

    public LockScreenArtwork(bool enabled)
    {
        _enabled = enabled;
        _worker = Task.Run(RunAsync);
    }

    public bool Enabled
    {
        set
        {
            lock (_gate) _enabled = value;
            Wake();
        }
    }

    public void Show(MediaState state)
    {
        lock (_gate) _state = state;
        Wake();
    }

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0) _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already woken.
        }
    }

    // ======================================================================
    //  The worker: compares what should be shown with what is, and acts
    // ======================================================================

    private async Task RunAsync()
    {
        // A backup left by a crash, or by a restore that failed last time.
        try
        {
            await RestoreAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLog.Write("lock", $"Lock screen restore failed: {exception.Message}");
        }

        while (true)
        {
            MediaState state;
            bool enabled, closing;
            lock (_gate)
            {
                state = _state;
                enabled = _enabled;
                closing = _closing;
            }

            var wait = Timeout.InfiniteTimeSpan;
            try
            {
                if (closing)
                {
                    await RestoreAsync().ConfigureAwait(false);
                    return;
                }

                if (enabled && KeyOf(state) is { } key)
                {
                    _stoppedAt = null;
                    if (key != _shownKey && key != _failedKey) await ShowArtworkAsync(state, key).ConfigureAwait(false);
                }
                else
                {
                    _failedKey = null; // a video that failed (a lost connection, say) gets another try when it plays again
                    if (_shownKey != null)
                    {
                        _stoppedAt ??= DateTime.UtcNow;
                        var left = StoppedGrace - (DateTime.UtcNow - _stoppedAt.Value);
                        if (enabled && left > TimeSpan.Zero)
                        {
                            wait = left;
                        }
                        else
                        {
                            _stoppedAt = null;
                            await RestoreAsync().ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                // A background task must not take CastDesk down over a picture.
                AppLog.Write("lock", $"Lock screen update failed: {exception.Message}");
            }

            await _wake.WaitAsync(wait).ConfigureAwait(false);
        }
    }

    /// <summary>What identifies the picture for this state, or null if nothing should be shown.</summary>
    private static string? KeyOf(MediaState state)
    {
        if (!state.HasVideo || !state.Playing || !state.Watching) return null;
        if (state.VideoId.Length > 0) return state.VideoId;
        return state.Artwork.Length > 0 ? state.Artwork : null;
    }

    private async Task ShowArtworkAsync(MediaState state, string key)
    {
        if (BlockedReason() is { } reason)
        {
            if (!_blockedLogged) AppLog.Write("lock", $"Leaving the lock screen alone: {reason}");
            _blockedLogged = true;
            _failedKey = key;
            return;
        }

        var picture = await MakePictureAsync(state, key).ConfigureAwait(false);
        if (picture == null)
        {
            // No good picture for this video. Don't leave the last video's thumbnail on the lock screen.
            _failedKey = key;
            if (_shownKey != null) await RestoreAsync().ConfigureAwait(false);
            return;
        }

        // Before anything changes, and only once: a second backup would record CastDesk's own picture.
        if (!File.Exists(BackupFile) && !await WriteBackupAsync().ConfigureAwait(false))
        {
            AppLog.Write("lock", "Leaving the lock screen alone: could not back it up.");
            TryDelete(picture);
            _failedKey = key;
            return;
        }

        _failedKey = key; // until it has worked: reports keep coming, and a refused picture shouldn't be retried for each
        var file = await StorageFile.GetFileFromPathAsync(picture).AsTask().ConfigureAwait(false);
        await LockScreen.SetImageFileAsync(file).AsTask().ConfigureAwait(false);
        _failedKey = null;
        AppLog.Write("lock", $"Showing the thumbnail of {key}.");

        var previous = _currentPicture;
        _currentPicture = picture;
        _shownKey = key;
        if (previous != null) TryDelete(previous);
    }

    /// <summary>Saves the picture for a video and returns its path, or null if there's no good one.</summary>
    private static async Task<string?> MakePictureAsync(MediaState state, string key)
    {
        try
        {
            using var source = await DownloadThumbnailAsync(state).ConfigureAwait(false);
            if (source == null)
            {
                AppLog.Write("lock", $"No thumbnail good enough for the lock screen for {key}.");
                return null;
            }
            using var composed = ComposePicture(source, ScreenSize());
            Directory.CreateDirectory(Folder);
            // A new name each time: Windows may refuse a file named like the previous one.
            var picture = Path.Combine(Folder, $"now-{DateTime.UtcNow.Ticks}.jpg");
            SaveJpeg(composed, picture);
            return picture;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Write("lock", $"Could not prepare the thumbnail: {exception.Message}");
            return null;
        }
    }

    /// <summary>Why CastDesk mustn't change the lock screen on this PC, or null if it may.</summary>
    private static string? BlockedReason()
    {
        if (!UserProfilePersonalizationSettings.IsSupported()) return "Windows says it can't be changed on this PC.";
        using (var policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Personalization"))
        {
            if (policy?.GetValue("LockScreenImage") != null || policy?.GetValue("NoChangingLockScreen") is int and not 0)
                return "a policy on this PC sets it.";
        }
        using var managed = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP");
        return managed?.GetValue("LockScreenImagePath") != null ? "a policy on this PC sets it." : null;
    }

    // ======================================================================
    //  Backing up and restoring the user's lock screen
    // ======================================================================

    private static async Task<bool> WriteBackupAsync()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var original = CurrentPicturePath();
            var copy = await CopyCurrentPictureAsync(original).ConfigureAwait(false);

            int? spotlight, overlay;
            var subscriptions = new Dictionary<string, string>();
            using (var key = Registry.CurrentUser.OpenSubKey(SpotlightKey))
            {
                spotlight = key?.GetValue(SpotlightEnabledValue) as int?;
                overlay = key?.GetValue(SpotlightOverlayValue) as int?;
            }
            using (var key = Registry.CurrentUser.OpenSubKey(SubscriptionsKey))
            {
                foreach (var name in key?.GetSubKeyNames() ?? [])
                {
                    using var subscription = key!.OpenSubKey(name);
                    if (subscription?.GetValue(SubscriptionContextValue) is string context && context.StartsWith("sc-mode=", StringComparison.Ordinal))
                        subscriptions[name] = context;
                }
            }
            int? slideshow;
            using (var key = Registry.CurrentUser.OpenSubKey(LockScreenKey))
                slideshow = key?.GetValue(SlideshowEnabledValue) as int?;

            // A plain picture can only come back from a copy; don't go on without one.
            if (copy == null && spotlight != 1 && slideshow != 1) return false;

            var backup = new LockScreenBackup(original, copy, spotlight, overlay, slideshow, subscriptions);
            var temporary = BackupFile + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(backup)).ConfigureAwait(false);
            File.Move(temporary, BackupFile, overwrite: true);
            AppLog.Write("lock", $"Backed up the lock screen (Spotlight {spotlight?.ToString() ?? "unset"}, slideshow {slideshow?.ToString() ?? "unset"}).");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Write("lock", $"Could not back up the lock screen: {exception.Message}");
            return false;
        }
    }

    /// <summary>The file Windows says the lock screen picture came from, unless it's gone or one of CastDesk's own.</summary>
    private static string? CurrentPicturePath()
    {
        try
        {
            if (LockScreen.OriginalImageFile is not { IsFile: true } uri) return null;
            var path = uri.LocalPath;
            return File.Exists(path) && !path.StartsWith(Folder, StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>Saves a copy of the current picture, in case its file is moved or deleted. Returns its path, or null.</summary>
    private static async Task<string?> CopyCurrentPictureAsync(string? original)
    {
        var extension = original != null ? Path.GetExtension(original) : ".jpg";
        var copy = Path.Combine(Folder, $"yours-{DateTime.UtcNow.Ticks}{extension}");
        try
        {
            if (original != null)
            {
                File.Copy(original, copy, overwrite: true);
                return copy;
            }
            using var windowsStream = LockScreen.GetImageStream();
            if (windowsStream == null) return null;
            using var picture = windowsStream.AsStreamForRead();
            using var output = File.Create(copy);
            await picture.CopyToAsync(output).ConfigureAwait(false);
            return copy;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Write("lock", $"Could not copy the lock screen picture: {exception.Message}");
            TryDelete(copy);
            return null;
        }
    }

    /// <summary>Puts the user's lock screen back if a backup exists. Keeps the backup if that fails, to retry later.</summary>
    private async Task RestoreAsync()
    {
        _shownKey = null;
        if (!File.Exists(BackupFile)) return;
        try
        {
            // The picture isn't CastDesk's: the user chose another in Windows Settings during playback, so keep theirs.
            if (OtherPictureIsSet())
            {
                AppLog.Write("lock", "The lock screen was changed during playback; keeping that.");
                DeleteOurFiles();
                return;
            }

            var backup = JsonSerializer.Deserialize<LockScreenBackup>(await File.ReadAllTextAsync(BackupFile).ConfigureAwait(false));
            if (backup == null) throw new InvalidDataException("The backup is empty.");

            // The picture first: setting one turns Spotlight and slideshow off, and the values below turn them back on.
            var picture = backup.Picture != null && File.Exists(backup.Picture) ? backup.Picture
                : backup.PictureCopy != null && File.Exists(backup.PictureCopy) ? backup.PictureCopy : null;
            if (picture != null)
            {
                var file = await StorageFile.GetFileFromPathAsync(picture).AsTask().ConfigureAwait(false);
                await LockScreen.SetImageFileAsync(file).AsTask().ConfigureAwait(false);
            }

            using (var key = Registry.CurrentUser.CreateSubKey(SpotlightKey))
            {
                if (backup.Spotlight is { } spotlight) key.SetValue(SpotlightEnabledValue, spotlight, RegistryValueKind.DWord);
                if (backup.SpotlightOverlay is { } overlay) key.SetValue(SpotlightOverlayValue, overlay, RegistryValueKind.DWord);
            }
            foreach (var (name, context) in backup.Subscriptions ?? [])
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"{SubscriptionsKey}\{name}", writable: true);
                key?.SetValue(SubscriptionContextValue, context, RegistryValueKind.String);
            }
            if (backup.Slideshow is { } slideshow)
            {
                using var key = Registry.CurrentUser.CreateSubKey(LockScreenKey);
                key.SetValue(SlideshowEnabledValue, slideshow, RegistryValueKind.DWord);
            }

            DeleteOurFiles();
            AppLog.Write("lock", "Restored the lock screen.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.Write("lock", $"Could not restore the lock screen, will try again: {exception.Message}");
        }
    }

    private static bool OtherPictureIsSet()
    {
        try
        {
            return LockScreen.OriginalImageFile is { IsFile: true } uri &&
                !uri.LocalPath.StartsWith(Folder, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    private void DeleteOurFiles()
    {
        File.Delete(BackupFile);
        _currentPicture = null;
        foreach (var leftover in Directory.GetFiles(Folder)) TryDelete(leftover);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover file does no harm; the next restore tries again.
        }
    }

    // ======================================================================
    //  The picture
    // ======================================================================

    /// <summary>
    /// YouTube's largest thumbnail, or the page's own artwork, if it's sharp enough for a full screen (see
    /// <see cref="MinArtSide"/>); otherwise null. Not every video has the largest size.
    /// </summary>
    private static async Task<Bitmap?> DownloadThumbnailAsync(MediaState state)
    {
        var urls = new List<string>();
        if (state.VideoId.Length > 0) urls.Add($"https://i.ytimg.com/vi/{Uri.EscapeDataString(state.VideoId)}/maxresdefault.jpg");
        if (Uri.TryCreate(state.Artwork, UriKind.Absolute, out var artwork) && artwork.Scheme == Uri.UriSchemeHttps) urls.Add(state.Artwork);

        foreach (var url in urls)
        {
            using var response = await Http.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) continue;
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            using var stream = new MemoryStream(bytes);
            using var image = Image.FromStream(stream);
            var bitmap = new Bitmap(image);
            var area = FindPictureArea(bitmap);
            if (Math.Min(area.Width, area.Height) >= MinArtSide) return bitmap;
            bitmap.Dispose();
        }
        return null;
    }

    private static Size ScreenSize()
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        var scale = Math.Min(1.0, (double)MaxScreenSide / Math.Max(bounds.Width, bounds.Height));
        return new Size(Math.Max(1, (int)(bounds.Width * scale)), Math.Max(1, (int)(bounds.Height * scale)));
    }

    /// <summary>
    /// A picture the size of the screen. Art with the screen's shape fills it. Any other shape (a square album
    /// cover) is fitted whole over a blurred, darkened copy of itself that fills the gaps.
    /// </summary>
    internal static Bitmap ComposePicture(Bitmap source, Size screen)
    {
        var art = FindPictureArea(source);
        var canvas = new Bitmap(screen.Width, screen.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(canvas);
        graphics.Clear(Color.Black);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;

        // Tiling stops the scaler from fading the edges towards black.
        using var edges = new ImageAttributes();
        edges.SetWrapMode(WrapMode.TileFlipXY);
        var whole = new Rectangle(Point.Empty, screen);

        var artShape = (double)art.Width / art.Height;
        var screenShape = (double)screen.Width / screen.Height;
        if (Math.Abs(artShape / screenShape - 1) < 0.03)
        {
            DrawPart(graphics, source, whole, CropToShape(art, screenShape), edges);
            return canvas;
        }

        // The blur: shrink to a few pixels, then let the scaler stretch them back smoothly.
        const int blurWidth = 40;
        using (var tiny = new Bitmap(blurWidth, Math.Max(1, (int)(blurWidth / screenShape)), PixelFormat.Format24bppRgb))
        {
            using (var tinyGraphics = Graphics.FromImage(tiny))
            {
                tinyGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                tinyGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                DrawPart(tinyGraphics, source, new Rectangle(Point.Empty, tiny.Size), CropToShape(art, screenShape), edges);
            }
            DrawSmoothly(graphics, tiny, screen, edges);
        }
        using (var shade = new SolidBrush(Color.FromArgb(110, Color.Black))) graphics.FillRectangle(shade, whole);

        var scale = Math.Min((double)screen.Width / art.Width, (double)screen.Height / art.Height);
        var width = (int)Math.Round(art.Width * scale);
        var height = (int)Math.Round(art.Height * scale);
        var fitted = new Rectangle((screen.Width - width) / 2, (screen.Height - height) / 2, width, height);
        DrawPart(graphics, source, fitted, art, edges);
        return canvas;
    }

    /// <summary>Enlarges in steps: one big stretch of a few pixels leaves visible streaks.</summary>
    private static void DrawSmoothly(Graphics graphics, Bitmap tiny, Size screen, ImageAttributes attributes)
    {
        var current = tiny;
        try
        {
            while (current.Width * 3 < screen.Width)
            {
                var larger = new Bitmap(current.Width * 3, current.Height * 3, PixelFormat.Format24bppRgb);
                using (var largerGraphics = Graphics.FromImage(larger))
                {
                    largerGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    largerGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    DrawPart(largerGraphics, current, new Rectangle(Point.Empty, larger.Size), new Rectangle(Point.Empty, current.Size), attributes);
                }
                if (current != tiny) current.Dispose();
                current = larger;
            }
            DrawPart(graphics, current, new Rectangle(Point.Empty, screen), new Rectangle(Point.Empty, current.Size), attributes);
        }
        finally
        {
            if (current != tiny) current.Dispose();
        }
    }

    private static void DrawPart(Graphics graphics, Image image, Rectangle to, Rectangle from, ImageAttributes attributes) =>
        graphics.DrawImage(image, to, from.X, from.Y, from.Width, from.Height, GraphicsUnit.Pixel, attributes);

    /// <summary>The centre of <paramref name="area"/> with the given width/height shape.</summary>
    private static Rectangle CropToShape(Rectangle area, double shape)
    {
        if ((double)area.Width / area.Height > shape)
        {
            var width = Math.Max(1, (int)Math.Round(area.Height * shape));
            return new Rectangle(area.X + (area.Width - width) / 2, area.Y, width, area.Height);
        }
        var height = Math.Max(1, (int)Math.Round(area.Width / shape));
        return new Rectangle(area.X, area.Y + (area.Height - height) / 2, area.Width, height);
    }

    /// <summary>
    /// The part of a thumbnail inside any black bars. YouTube's smaller sizes are 4:3 with bars above and below a
    /// 16:9 video, and some album covers have bars at the sides; left in, they'd stop the art from filling the screen.
    /// </summary>
    internal static Rectangle FindPictureArea(Bitmap image)
    {
        const double maxBarShare = 0.4;
        var maxBarWidth = (int)(image.Width * maxBarShare);
        var maxBarHeight = (int)(image.Height * maxBarShare);

        int top = 0, bottom = 0, left = 0, right = 0;
        while (top < maxBarHeight && IsBlackRow(image, top)) top++;
        while (bottom < maxBarHeight && IsBlackRow(image, image.Height - 1 - bottom)) bottom++;
        while (left < maxBarWidth && IsBlackColumn(image, left)) left++;
        while (right < maxBarWidth && IsBlackColumn(image, image.Width - 1 - right)) right++;

        // Reaching the limit means a dark picture, not a bar.
        if (top == maxBarHeight || bottom == maxBarHeight) top = bottom = 0;
        if (left == maxBarWidth || right == maxBarWidth) left = right = 0;
        return Rectangle.FromLTRB(left, top, image.Width - right, image.Height - bottom);
    }

    private static bool IsBlackRow(Bitmap image, int y)
    {
        var step = Math.Max(1, image.Width / 32);
        for (var x = 0; x < image.Width; x += step)
            if (!IsBlack(image.GetPixel(x, y))) return false;
        return true;
    }

    private static bool IsBlackColumn(Bitmap image, int x)
    {
        var step = Math.Max(1, image.Height / 32);
        for (var y = 0; y < image.Height; y += step)
            if (!IsBlack(image.GetPixel(x, y))) return false;
        return true;
    }

    // Not exactly zero: JPEG compression leaves faint noise in black areas.
    private static bool IsBlack(Color color) => Math.Max(color.R, Math.Max(color.G, color.B)) <= 24;

    private static void SaveJpeg(Bitmap picture, string path)
    {
        var encoder = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
        picture.Save(path, encoder, parameters);
    }

    public void Dispose()
    {
        lock (_gate) _closing = true;
        Wake();
        // On exit the worker restores the lock screen. If this times out, the next start does it.
        if (!_worker.Wait(RestoreOnExitTimeout)) AppLog.Write("lock", "Closing before the lock screen was restored.");
    }

    /// <summary>What's needed to put the user's lock screen back.</summary>
    /// <param name="Picture">The file the picture came from, if known.</param>
    /// <param name="PictureCopy">CastDesk's copy of the picture, in case that file is moved or deleted.</param>
    /// <param name="Spotlight">The Spotlight switch (1 = on), or null if it wasn't set.</param>
    /// <param name="SpotlightOverlay">Spotlight's "fun facts and tips" switch, or null if it wasn't set.</param>
    /// <param name="Slideshow">The slideshow switch (1 = on), or null if it wasn't set.</param>
    /// <param name="Subscriptions">Spotlight's content subscriptions and their modes.</param>
    private sealed record LockScreenBackup(string? Picture, string? PictureCopy, int? Spotlight, int? SpotlightOverlay, int? Slideshow,
        Dictionary<string, string>? Subscriptions);
}
