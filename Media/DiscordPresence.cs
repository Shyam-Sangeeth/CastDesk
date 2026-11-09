using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CastDesk.Diagnostics;
using CastDesk.Tv;

namespace CastDesk.Media;

/// <summary>
/// Shows what CastDesk is playing as the user's Discord status (Rich Presence): "Listening to" for music,
/// "Watching" for other videos, with the title, channel, thumbnail, a progress bar and a link to the video.
/// Nothing is shown while paused or away from a video.
/// </summary>
/// <remarks>
/// Talks to the Discord app on this PC over its local IPC pipe (\\.\pipe\discord-ipc-0 to -9), the documented
/// way for a desktop app to set Rich Presence: frames of opcode, length and JSON. Nothing goes through a
/// server, and no Discord sign-in is needed. The name and cover image come from CastDesk's Discord
/// application (<see cref="ApplicationId"/>, Discord Developer Portal). A worker thread does the pipe work, so
/// <see cref="Show"/> never blocks; Discord accepts about 5 updates per 20 seconds, so updates are spaced out
/// and only the latest is sent. If Discord isn't running, CastDesk tries again every 30 seconds.
/// </remarks>
internal sealed partial class DiscordPresence : IDisposable
{
    /// <summary>CastDesk's application in the Discord Developer Portal. Not a secret: every client sends it.</summary>
    public const string ApplicationId = "1569019291867816131";

    private const string IconUrl = "https://raw.githubusercontent.com/Shyam-Sangeeth/CastDesk/main/docs/discord/discord-icon.png";
    private const int MaxTextLength = 128;
    private const int MaxImageLength = 256;

    private const int OpHandshake = 0;
    private const int OpFrame = 1;
    private const int OpClose = 2;
    private const int OpPing = 3;
    private const int OpPong = 4;

    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly SemaphoreSlim _changed = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private string? _wanted; // the activity to show, as JSON; null for none
    private bool _enabled;

    // Worker thread only.
    private NamedPipeClientStream? _pipe;
    private string? _shown;
    private DateTime _lastSent = DateTime.MinValue;
    private bool _reportedUnavailable;

    private readonly Func<int?> _playerProcessId;

    /// <param name="playerProcessId">
    /// The WebView2 browser process, which plays the TV app (null before it starts). Discord needs a process ID
    /// with each status ("nonzero pid required") and treats that process as a running game: it would put its
    /// overlay on CastDesk's window if given CastDesk's own ID. The browser process has no window of its own, so
    /// no overlay appears, and it ends with CastDesk, so Discord still clears the status if CastDesk stops.
    /// </param>
    public DiscordPresence(Func<int?> playerProcessId)
    {
        _playerProcessId = playerProcessId;
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Whether to show anything (Settings → Discord). Turning it off clears the status.</summary>
    public bool Enabled
    {
        set
        {
            lock (_lock)
            {
                if (_enabled == value) return;
                _enabled = value;
            }
            Signal();
        }
    }

    /// <summary>Shows <paramref name="state"/> in Discord (or clears the status if nothing is playing).</summary>
    public void Show(MediaState state)
    {
        var activity = BuildActivity(state);
        lock (_lock)
        {
            if (activity == _wanted) return;
            _wanted = activity;
        }
        Signal();
    }

    private void Signal()
    {
        if (_changed.CurrentCount == 0) _changed.Release();
    }

    // ======================================================================
    //  What to show
    // ======================================================================

    private static string? BuildActivity(MediaState state)
    {
        if (!state.HasVideo || !state.Playing || !state.Watching || !VideoIdPattern().IsMatch(state.VideoId)) return null;

        var title = Fit(state.Title) ?? "YouTube";
        var artwork = Uri.TryCreate(state.Artwork, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            state.Artwork.Length <= MaxImageLength
            ? state.Artwork
            : $"https://i.ytimg.com/vi/{state.VideoId}/hqdefault.jpg";
        var activity = new JsonObject
        {
            ["type"] = state.Music ? 2 : 3, // Listening, Watching
            ["status_display_type"] = 2,    // the member list shows the title ("Listening to <song>")
            ["details"] = title,
            ["assets"] = new JsonObject
            {
                ["large_image"] = artwork,
                ["large_text"] = "YouTube", // Discord shows it under the channel; the title is already above
                ["small_image"] = IconUrl,
                ["small_text"] = "CastDesk",
            },
            ["buttons"] = new JsonArray
            {
                new JsonObject
                {
                    ["label"] = state.Music ? "Listen on YouTube" : "Watch on YouTube",
                    ["url"] = $"https://www.youtube.com/watch?v={state.VideoId}",
                },
            },
        };
        if (Fit(state.Artist) is { } artist) activity["state"] = artist;
        if (state.StartedAt > 0)
        {
            var timestamps = new JsonObject { ["start"] = state.StartedAt };
            if (state.EndsAt > state.StartedAt) timestamps["end"] = state.EndsAt;
            activity["timestamps"] = timestamps;
        }
        return activity.ToJsonString();
    }

    /// <summary>Discord wants 2 to 128 characters; returns null for text too short to show.</summary>
    private static string? Fit(string text)
    {
        text = text.Trim();
        if (text.Length < 2) return null;
        return text.Length <= MaxTextLength ? text : text[..(MaxTextLength - 1)] + "…";
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdPattern();

    // ======================================================================
    //  Talking to Discord (worker thread)
    // ======================================================================

    private async Task RunAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            var retry = false;
            try
            {
                retry = !await UpdateAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AppLog.Write("discord", $"Discord update failed: {exception.Message}");
                Disconnect();
                retry = true;
            }

            try
            {
                if (retry) await _changed.WaitAsync(RetryInterval, token);
                else await _changed.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        Disconnect();
    }

    /// <summary>Brings Discord in line with what should show. False if Discord couldn't be reached.</summary>
    private async Task<bool> UpdateAsync(CancellationToken token)
    {
        string? wanted;
        bool enabled;
        lock (_lock)
        {
            enabled = _enabled;
            wanted = enabled ? _wanted : null;
        }

        if (!enabled)
        {
            // Closing the connection clears the status.
            Disconnect();
            return true;
        }
        if (wanted == _shown) return true;
        if (wanted == null && _pipe == null) return true;

        var wait = _lastSent + MinimumInterval - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, token);
            lock (_lock) wanted = _enabled ? _wanted : null; // send the newest, not what was wanted before the wait
            if (wanted == _shown) return true;
        }

        if (_pipe == null && !await ConnectAsync(token)) return false;
        await SetActivityAsync(wanted, token);
        _lastSent = DateTime.UtcNow;
        _shown = wanted;
        return true;
    }

    private async Task<bool> ConnectAsync(CancellationToken token)
    {
        for (var index = 0; index < 10; index++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{index}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(500, token);
                await WriteFrameAsync(pipe, OpHandshake, new JsonObject { ["v"] = 1, ["client_id"] = ApplicationId }.ToJsonString(), token);
                var (opcode, reply) = await ReadFrameAsync(pipe, token);
                if (opcode == OpFrame && reply["evt"]?.GetValue<string>() == "READY")
                {
                    _pipe = pipe;
                    _reportedUnavailable = false;
                    AppLog.Write("discord", "Connected to Discord.");
                    return true;
                }
                AppLog.Write("discord", $"Discord refused the connection: {reply["message"] ?? reply.ToJsonString()}");
                await pipe.DisposeAsync();
                return false;
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or JsonException or InvalidOperationException ||
                                              (exception is OperationCanceledException && !token.IsCancellationRequested))
            {
                await pipe.DisposeAsync();
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync(); // CastDesk is closing
                throw;
            }
        }

        if (!_reportedUnavailable) AppLog.Write("discord", "Discord isn't running; trying again every 30 seconds.");
        _reportedUnavailable = true;
        return false;
    }

    private async Task SetActivityAsync(string? activityJson, CancellationToken token)
    {
        var nonce = Guid.NewGuid().ToString();
        var command = new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = new JsonObject
            {
                ["pid"] = _playerProcessId() ?? Environment.ProcessId,
                ["activity"] = activityJson == null ? null : JsonNode.Parse(activityJson),
            },
            ["nonce"] = nonce,
        };
        await WriteFrameAsync(_pipe!, OpFrame, command.ToJsonString(), token);

        // Read until Discord answers this command, answering its pings on the way.
        while (true)
        {
            var (opcode, reply) = await ReadFrameAsync(_pipe!, token);
            if (opcode == OpPing)
            {
                await WriteFrameAsync(_pipe!, OpPong, reply.ToJsonString(), token);
                continue;
            }
            if (opcode == OpClose) throw new IOException($"Discord closed the connection: {reply["message"]}");
            if (reply["nonce"]?.GetValue<string>() != nonce) continue;
            if (reply["evt"]?.GetValue<string>() == "ERROR")
                AppLog.Write("discord", $"Discord rejected the status: {reply["data"]?["message"]}");
            return;
        }
    }

    private static async Task WriteFrameAsync(Stream pipe, int opcode, string json, CancellationToken token)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame, 8);
        await pipe.WriteAsync(frame, token);
        await pipe.FlushAsync(token);
    }

    private static async Task<(int Opcode, JsonObject Body)> ReadFrameAsync(Stream pipe, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ReplyTimeout);
        var header = new byte[8];
        await pipe.ReadExactlyAsync(header, timeout.Token);
        var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > 1_000_000) throw new IOException($"Unexpected frame length {length} from Discord.");
        var payload = new byte[length];
        await pipe.ReadExactlyAsync(payload, timeout.Token);
        return (opcode, JsonNode.Parse(payload) as JsonObject ?? new JsonObject());
    }

    private void Disconnect()
    {
        _shown = null;
        if (_pipe == null) return;
        try { _pipe.Dispose(); } catch (IOException) { /* already gone */ }
        _pipe = null;
        AppLog.Write("discord", "Disconnected from Discord (status cleared).");
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { /* stopped */ }
        _stop.Dispose();
        _changed.Dispose();
    }
}
