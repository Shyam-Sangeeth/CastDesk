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

    /// <summary>The art is centred at this share of the screen's width, on a frosted gray background.</summary>
    private const double ArtWidthShare = 0.5;
    private const double ArtMaxHeightShare = 0.6;

    /// <summary>
    /// Filling the screen, art with a shorter side below this looks blurred, so the lock screen is left alone.
    /// YouTube's largest thumbnails (1280x720, or a 720x720 cover inside bars) pass; its 640x480 and 480x360 ones don't.
    /// </summary>
    private const int MinArtSideFilled = 600;

    /// <summary>
    /// Centred, the art is only half the screen wide (960 px on a 1080p screen), so a smaller one still looks fine:
    /// YouTube's 640x480 size (640x358 inside its bars) passes, and so does the 480x360 one of an old 4:3 video,
    /// which has no bars; a 480x360 thumbnail of a widescreen video (480x268 inside its bars) doesn't.
    /// </summary>
    private const int MinArtSideCentred = 340;

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
    private bool _fillScreen;
    private bool _closing;

    // Only the worker uses these.
    private string? _shownKey;
    private string? _failedKey;
    private string? _currentPicture;
    private DateTime? _stoppedAt;
    private bool _blockedLogged;

    /// <param name="enabled">Whether to show anything (Settings → Lock screen).</param>
    /// <param name="fillScreen">"Fill the screen" rather than "Centred" (see <see cref="FillScreen"/>).</param>
    public LockScreenArtwork(bool enabled, bool fillScreen)
    {
        _enabled = enabled;
        _fillScreen = fillScreen;
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

    /// <summary>
    /// The picture's look: true fills the screen with the art (over a blur of it if its shape differs), false puts it
    /// in the centre at half the screen's width on tinted glass. A change redraws what's showing.
    /// </summary>
    public bool FillScreen
    {
        set
        {
            lock (_gate) _fillScreen = value;
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
            bool enabled, fillScreen, closing;
            lock (_gate)
            {
                state = _state;
                enabled = _enabled;
                fillScreen = _fillScreen;
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

                if (enabled && KeyOf(state) is { } video)
                {
                    _stoppedAt = null;
                    // The look is part of the key, so changing it in Settings redraws the picture.
                    var key = $"{video} ({(fillScreen ? "filling the screen" : "centred")})";
                    if (key != _shownKey && key != _failedKey) await ShowArtworkAsync(state, key, fillScreen).ConfigureAwait(false);
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

    private async Task ShowArtworkAsync(MediaState state, string key, bool fillScreen)
    {
        if (BlockedReason() is { } reason)
        {
            if (!_blockedLogged) AppLog.Write("lock", $"Leaving the lock screen alone: {reason}");
            _blockedLogged = true;
            _failedKey = key;
            return;
        }

        var picture = await MakePictureAsync(state, key, fillScreen).ConfigureAwait(false);
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
    private static async Task<string?> MakePictureAsync(MediaState state, string key, bool fillScreen)
    {
        try
        {
            using var source = await DownloadThumbnailAsync(state, fillScreen ? MinArtSideFilled : MinArtSideCentred).ConfigureAwait(false);
            if (source == null)
            {
                AppLog.Write("lock", $"No thumbnail good enough for the lock screen for {key}.");
                return null;
            }
            using var composed = fillScreen ? ComposeFilledPicture(source, ScreenSize()) : ComposePicture(source, ScreenSize());
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
    /// The sharpest art available whose shorter side (inside any bars) is at least <paramref name="minSide"/>
    /// pixels: YouTube's largest thumbnail, the page's own artwork, then YouTube's 640x480 and 480x360 sizes. Null if none is
    /// sharp enough. Not every video has every size.
    /// </summary>
    private static async Task<Bitmap?> DownloadThumbnailAsync(MediaState state, int minSide)
    {
        var urls = new List<string>();
        var id = Uri.EscapeDataString(state.VideoId);
        if (state.VideoId.Length > 0) urls.Add($"https://i.ytimg.com/vi/{id}/maxresdefault.jpg");
        if (Uri.TryCreate(state.Artwork, UriKind.Absolute, out var artwork) && artwork.Scheme == Uri.UriSchemeHttps) urls.Add(state.Artwork);
        if (state.VideoId.Length > 0 && minSide < MinArtSideFilled)
        {
            urls.Add($"https://i.ytimg.com/vi/{id}/sddefault.jpg");
            urls.Add($"https://i.ytimg.com/vi/{id}/hqdefault.jpg"); // passes only for old 4:3 videos (no bars)
        }

        foreach (var url in urls)
        {
            try
            {
                using var response = await Http.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                using var stream = new MemoryStream(bytes);
                using var image = Image.FromStream(stream);
                var bitmap = new Bitmap(image);
                var area = FindPictureArea(bitmap);
                if (Math.Min(area.Width, area.Height) >= minSide) return bitmap;
                bitmap.Dispose();
            }
            catch (Exception exception) when (exception is ArgumentException or HttpRequestException or TaskCanceledException or IOException)
            {
                // Not an image, or a failed download: try the next size.
                AppLog.Write("lock", $"Skipping {url}: {exception.Message}");
            }
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
    /// A picture the size of the screen: the art, whole, in the centre at half the screen's width (at most
    /// <see cref="ArtMaxHeightShare"/> of its height, for square covers), on frosted, glossy gray.
    /// </summary>
    internal static Bitmap ComposePicture(Bitmap source, Size screen)
    {
        var art = FindPictureArea(source);
        var canvas = new Bitmap(screen.Width, screen.Height, PixelFormat.Format24bppRgb);
        DrawFrostedBackground(canvas, MainColor(source, art));
        using var graphics = Graphics.FromImage(canvas);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;

        // Tiling stops the scaler from fading the art's edges towards black.
        using var edges = new ImageAttributes();
        edges.SetWrapMode(WrapMode.TileFlipXY);

        var scale = Math.Min(screen.Width * ArtWidthShare / art.Width, screen.Height * ArtMaxHeightShare / art.Height);
        var width = (int)Math.Round(art.Width * scale);
        var height = (int)Math.Round(art.Height * scale);
        var fitted = new Rectangle((screen.Width - width) / 2, (screen.Height - height) / 2, width, height);
        DrawShadow(graphics, fitted);
        DrawPart(graphics, source, fitted, art, edges);
        return canvas;
    }

    /// <summary>
    /// Frosted, glossy glass tinted with the art's main colour, worked out per pixel (GDI+'s gradient brushes leave
    /// rings): a diagonal gradient, soft glows like light behind frosted glass, a band of gloss, darker edges, and
    /// fine grain against banding.
    /// </summary>
    private static void DrawFrostedBackground(Bitmap canvas, Color tint)
    {
        // The tint as multipliers that keep the brightness: a gray tint changes nothing.
        var tintLight = Math.Max(1, tint.R * 0.299 + tint.G * 0.587 + tint.B * 0.114);
        var tintRed = 0.15 + 0.85 * Math.Min(2.2, tint.R / tintLight);
        var tintGreen = 0.15 + 0.85 * Math.Min(2.2, tint.G / tintLight);
        var tintBlue = 0.15 + 0.85 * Math.Min(2.2, tint.B / tintLight);
        (double X, double Y, double Radius, double Strength)[] glows = [(0.15, 0.10, 0.32, 46), (0.88, 0.22, 0.22, 24), (0.60, 1.00, 0.30, 20)];
        var data = canvas.LockBits(new Rectangle(Point.Empty, canvas.Size), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            int width = data.Width, height = data.Height;
            var bytes = new byte[data.Stride * height];
            var aspect = (double)width / height;

            // Every term splits into a part for the column and a part for the row (a Gaussian glow is the product
            // of one along x and one along y), so they're worked out once per column and row, not per pixel.
            var columnBase = new double[width];
            var columnEdge = new double[width];
            var columnGlow = new double[glows.Length, width];
            for (var x = 0; x < width; x++)
            {
                var u = (double)x / width;
                columnBase[x] = 84 - 46 * u * 0.4;
                columnEdge[x] = (u - 0.5) * (u - 0.5) * 4;
                for (var g = 0; g < glows.Length; g++)
                {
                    var dx = (u - glows[g].X) * aspect;
                    columnGlow[g, x] = glows[g].Strength * Math.Exp(-dx * dx / (2 * glows[g].Radius * glows[g].Radius));
                }
            }
            // The gloss runs along the diagonal: a table by position across it.
            const int sheenSteps = 4096;
            var sheen = new double[sheenSteps + 1];
            for (var i = 0; i <= sheenSteps; i++)
            {
                var diagonal = (double)i / sheenSteps - 0.32;
                sheen[i] = 18 * Math.Exp(-diagonal * diagonal / 0.006);
            }

            var rowGlow = new double[glows.Length];
            var noise = 0x9E3779B9u; // xorshift: the grain needs no better randomness, and Random per pixel is slow
            for (var y = 0; y < height; y++)
            {
                var v = (double)y / height;
                var rowBase = -46 * v * 0.6;
                var rowEdge = (v - 0.5) * (v - 0.5) * 4;
                for (var g = 0; g < glows.Length; g++)
                {
                    var dy = v - glows[g].Y;
                    rowGlow[g] = Math.Exp(-dy * dy / (2 * glows[g].Radius * glows[g].Radius));
                }
                var row = y * data.Stride;
                for (var x = 0; x < width; x++)
                {
                    var value = columnBase[x] + rowBase;
                    for (var g = 0; g < glows.Length; g++) value += columnGlow[g, x] * rowGlow[g];
                    value += sheen[(int)(((double)x / width * aspect + v) / (aspect + 1) * sheenSteps)];
                    value *= 1 - 0.38 * Math.Min(1, (columnEdge[x] + rowEdge) / 2);
                    noise ^= noise << 13;
                    noise ^= noise >> 17;
                    noise ^= noise << 5;
                    value += (int)(noise % 5) - 2;

                    var index = row + x * 3; // blue, green, red
                    bytes[index] = (byte)Math.Clamp(value * tintBlue, 0, 255);
                    bytes[index + 1] = (byte)Math.Clamp(value * tintGreen, 0, 255);
                    bytes[index + 2] = (byte)Math.Clamp(value * tintRed, 0, 255);
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        }
        finally
        {
            canvas.UnlockBits(data);
        }
    }

    /// <summary>
    /// The art's main colour: the average of a grid of samples, weighted towards vivid ones so a colourful cover on
    /// a pale background still gives its colour rather than a muddy gray.
    /// </summary>
    private static Color MainColor(Bitmap image, Rectangle area)
    {
        double red = 0, green = 0, blue = 0, total = 0;
        for (var row = 0; row < 24; row++)
        {
            for (var column = 0; column < 24; column++)
            {
                var pixel = image.GetPixel(area.X + (int)((column + 0.5) * area.Width / 24), area.Y + (int)((row + 0.5) * area.Height / 24));
                var saturation = (Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B))) / 255.0;
                var weight = 0.1 + saturation * saturation * 4;
                red += pixel.R * weight;
                green += pixel.G * weight;
                blue += pixel.B * weight;
                total += weight;
            }
        }
        return Color.FromArgb((int)(red / total), (int)(green / total), (int)(blue / total));
    }

    /// <summary>A soft shadow under the art, so it sits above the glass.</summary>
    private static void DrawShadow(Graphics graphics, Rectangle art)
    {
        var spread = Math.Max(8, art.Width / 24);
        using var brush = new SolidBrush(Color.FromArgb(7, 0, 0, 0));
        for (var step = spread; step > 0; step -= 2)
        {
            var shadow = Rectangle.Inflate(art, step, step);
            shadow.Offset(0, spread / 2);
            // Only the ring outside the art: the art covers the rest, and blending under it ~40 times was slow.
            using var ring = new Region(shadow);
            ring.Exclude(art);
            graphics.FillRegion(brush, ring);
        }
    }

    /// <summary>
    /// "Fill the screen": art with the screen's shape fills it. Any other shape (a square album cover) is fitted
    /// whole over a blurred, darkened copy of itself that fills the gaps.
    /// </summary>
    internal static Bitmap ComposeFilledPicture(Bitmap source, Size screen)
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

    private static void DrawPart(Graphics graphics, Image image, Rectangle to, Rectangle from, ImageAttributes attributes) =>
        graphics.DrawImage(image, to, from.X, from.Y, from.Width, from.Height, GraphicsUnit.Pixel, attributes);

    /// <summary>
    /// The part of a thumbnail inside any bars. YouTube's smaller sizes are 4:3 with black bars above and below a
    /// 16:9 video, and album tracks put the square cover between flat gray bars; left in, they'd show as a frame.
    /// </summary>
    internal static Rectangle FindPictureArea(Bitmap image)
    {
        const double maxBarShare = 0.4;
        var maxBarWidth = (int)(image.Width * maxBarShare);
        var maxBarHeight = (int)(image.Height * maxBarShare);

        var top = BarLength(image, Edge.Top, maxBarHeight, out var topColor);
        var bottom = BarLength(image, Edge.Bottom, maxBarHeight, out var bottomColor);
        var left = BarLength(image, Edge.Left, maxBarWidth, out var leftColor);
        var right = BarLength(image, Edge.Right, maxBarWidth, out var rightColor);
        var (keptTop, keptBottom, colouredRows) = KeepBars(top, topColor, bottom, bottomColor, image.Height, maxBarHeight);
        var (keptLeft, keptRight, colouredColumns) = KeepBars(left, leftColor, right, rightColor, image.Width, maxBarWidth);

        // Coloured bars only frame an album cover, so they go only if a square is left. Otherwise they're probably
        // a plain backdrop around a centred subject: keep it, and crop black bars alone.
        var width = image.Width - keptLeft - keptRight;
        var height = image.Height - keptTop - keptBottom;
        var square = width > 0 && height > 0 && Math.Abs((double)width / height - 1) < 0.1;
        if ((colouredRows || colouredColumns) && !square)
        {
            if (colouredRows) (keptTop, keptBottom) = BlackOnly(top, topColor, bottom, bottomColor);
            if (colouredColumns) (keptLeft, keptRight) = BlackOnly(left, leftColor, right, rightColor);
        }
        (top, bottom, left, right) = (keptTop, keptBottom, keptLeft, keptRight);

        // One more line past each bar: JPEG blends the bar into the art's first line, which would show as a thin frame.
        static int PastBlend(int bar) => bar > 0 ? bar + 1 : 0;
        (top, bottom, left, right) = (PastBlend(top), PastBlend(bottom), PastBlend(left), PastBlend(right));
        return Rectangle.FromLTRB(left, top, image.Width - right, image.Height - bottom);
    }

    private enum Edge { Top, Bottom, Left, Right }

    /// <summary>
    /// How many rows or columns in from <paramref name="edge"/> are one flat colour (up to <paramref name="max"/>),
    /// and that colour.
    /// </summary>
    private static int BarLength(Bitmap image, Edge edge, int max, out Color color)
    {
        var horizontal = edge is Edge.Top or Edge.Bottom;
        var along = horizontal ? image.Width : image.Height;
        var step = Math.Max(1, along / 32);
        Color At(int line, int position) => edge switch
        {
            Edge.Top => image.GetPixel(position, line),
            Edge.Bottom => image.GetPixel(position, image.Height - 1 - line),
            Edge.Left => image.GetPixel(line, position),
            _ => image.GetPixel(image.Width - 1 - line, position),
        };

        color = At(0, along / 2);
        var length = 0;
        while (length < max)
        {
            for (var position = 0; position < along; position += step)
                if (!IsClose(At(length, position), color)) return length;
            length++;
        }
        return length;
    }

    /// <summary>
    /// Which of two opposite bars to crop. Black bars always go. A bar of another flat colour (YouTube puts album
    /// covers between dark gray bars) goes only with a matching one on the other side, so a plain sky at the top of
    /// a photo isn't cut off. Bars reaching the limit mean a dark or plain picture, not bars.
    /// </summary>
    /// <returns>The bars to crop, and whether they are a coloured (not black) matching pair.</returns>
    private static (int First, int Second, bool Coloured) KeepBars(int first, Color firstColor, int second, Color secondColor, int size, int max)
    {
        if (first >= max || second >= max) return (0, 0, false);
        if (first > 0 && second > 0 && Math.Abs(first - second) <= size * 0.02 + 2 && IsClose(firstColor, secondColor))
            return (first, second, !(IsBlack(firstColor) && IsBlack(secondColor)));
        var (blackFirst, blackSecond) = BlackOnly(first, firstColor, second, secondColor);
        return (blackFirst, blackSecond, false);
    }

    private static (int, int) BlackOnly(int first, Color firstColor, int second, Color secondColor) =>
        (first > 0 && IsBlack(firstColor) ? first : 0, second > 0 && IsBlack(secondColor) ? second : 0);

    // Not exact: JPEG compression leaves faint noise in flat areas.
    private static bool IsClose(Color a, Color b) =>
        Math.Abs(a.R - b.R) <= 20 && Math.Abs(a.G - b.G) <= 20 && Math.Abs(a.B - b.B) <= 20;

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
