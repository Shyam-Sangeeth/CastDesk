using System.Text.Json;
using System.Text.Json.Serialization;
using CastDesk.Diagnostics;

namespace CastDesk.Configuration;

/// <summary>
/// User settings, kept in %LOCALAPPDATA%\CastDesk\settings.json and edited in the Settings window.
/// Every field is optional in the file; missing fields keep the defaults below.
/// </summary>
public sealed class CastDeskOptions
{
    public const int MinimumPort = 1024;
    public const int MaximumPort = 65535;
    public const int MaxFriendlyNameLength = 60;

    /// <summary>Name the phone shows in its Cast menu.</summary>
    public string FriendlyName { get; set; } = DefaultFriendlyName;

    /// <summary>"CastDesk-" plus this PC's name (such as CastDesk-ARCANE), so several PCs are told apart.</summary>
    public static string DefaultFriendlyName { get; } = $"CastDesk-{Environment.MachineName}";

    /// <summary>
    /// TCP port of the DIAL server the phone connects to. If Windows has reserved it or another app
    /// uses it, CastDesk picks a free port instead.
    /// </summary>
    public int DialPort { get; set; } = 8765;

    /// <summary>
    /// The browser identity YouTube's TV app sees. Empty uses <see cref="DefaultUserAgent"/>; set it only
    /// if YouTube stops accepting the default.
    /// </summary>
    public string UserAgent { get; set; } = "";

    /// <summary>Switch the window to fullscreen whenever a phone starts casting.</summary>
    public bool FullscreenOnCast { get; set; }

    /// <summary>Hide the window to the tray a few seconds after the phone stops casting.</summary>
    public bool HideWhenCastingStops { get; set; } = true;

    /// <summary>
    /// Keep playing when the last phone disconnects, as CastDesk always has. Off, the player stops as it does
    /// when the phone asks it to stop.
    /// </summary>
    public bool KeepPlayingWhenPhoneDisconnects { get; set; } = true;

    /// <summary>Check GitHub for a newer release shortly after CastDesk starts, and offer it in a notification.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Show what is playing in Discord (Rich Presence, through the Discord app on this PC). On by default;
    /// it does nothing unless the Discord desktop app is running. It shares the titles with the user's Discord
    /// friends, so Settings and the README say so.
    /// </summary>
    public bool ShowInDiscord { get; set; } = true;

    /// <summary>
    /// While a video plays, make its thumbnail the lock screen picture. On by default (the user chose this for
    /// 2.1.5); the user's own lock screen is put back afterwards. It shows what's playing to anyone who can see the
    /// locked screen, so Settings and the README say so.
    /// </summary>
    public bool ShowOnLockScreen { get; set; } = true;

    /// <summary>
    /// The lock screen picture's look: false (the default, the user's choice) puts the thumbnail in the centre at
    /// half the screen's width on tinted glass; true fills the screen with it (Niranjen99's original design).
    /// </summary>
    public bool LockScreenFillsScreen { get; set; }

    /// <summary>
    /// A Chromecast with Google TV running YouTube's TV engine (Cobalt). youtube.com/tv only serves its TV
    /// app to TV browsers, and this identity was tested on 2026-11-07: sign-in, playback and casting work.
    /// </summary>
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Linux armeabi-v7a; Android 9) Cobalt/23.lts.4.308812-gold (unlike Gecko) v8/8.8.278.17-jit gles Starboard/14, " +
        "Google_ATV_sabrina_2020/STTE.211004.001 (google, Chromecast) com.google.android.youtube.tv/3.04.4";

    [JsonIgnore]
    public string EffectiveUserAgent => string.IsNullOrWhiteSpace(UserAgent) ? DefaultUserAgent : UserAgent;

    /// <summary>%LOCALAPPDATA%\CastDesk: settings, the log, the device ID and the TV app's browser profile.</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CastDesk");

    public static string SettingsFilePath { get; } = Path.Combine(DataDirectory, "settings.json");

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static CastDeskOptions Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var options = JsonSerializer.Deserialize<CastDeskOptions>(File.ReadAllText(SettingsFilePath), ReadOptions);
                if (options != null) return options.Normalized();
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Write("host", $"Ignoring unreadable {SettingsFilePath}: {exception.Message}");
        }
        return new CastDeskOptions();
    }

    /// <summary>Writes the settings file. Returns null on success, or what went wrong.</summary>
    public string? Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(this, WriteOptions));
            AppLog.Write("host", "Settings saved.");
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("host", $"Could not save {SettingsFilePath}: {exception.Message}");
            return exception.Message;
        }
    }

    public CastDeskOptions Clone() => (CastDeskOptions)MemberwiseClone();

    /// <summary>Replaces values that a hand-edited file may have left out of range.</summary>
    private CastDeskOptions Normalized()
    {
        FriendlyName = string.IsNullOrWhiteSpace(FriendlyName) ? DefaultFriendlyName : FriendlyName.Trim();
        if (FriendlyName.Length > MaxFriendlyNameLength) FriendlyName = FriendlyName[..MaxFriendlyNameLength];
        if (DialPort is < MinimumPort or > MaximumPort) DialPort = 8765;
        UserAgent = UserAgent?.Trim() ?? "";
        return this;
    }
}
