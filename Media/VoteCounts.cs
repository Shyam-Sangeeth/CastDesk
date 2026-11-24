using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CastDesk.Diagnostics;
using CastDesk.Tv;
using CastDesk.Updates;

namespace CastDesk.Media;

/// <summary>The like and dislike counts of one video, from Return YouTube Dislike.</summary>
internal sealed record VideoVotes(string VideoId, long Likes, long Dislikes);

/// <summary>
/// Gets each open video's like and dislike counts from Return YouTube Dislike (RYD), the open API behind the browser
/// extension of that name, for page-bridge.js to show under the TV app's like and dislike buttons. YouTube stopped
/// showing dislikes in 2021; RYD's figure is the archived count from before then plus an estimate from its own
/// users' votes since.
/// </summary>
/// <remarks>
/// Only the video's ID is sent (GET /votes?videoId=…): no user ID, no vote, nothing about the user. RYD's terms for
/// third-party use: credit it with a link (Settings and the README do), and at most 100 requests a minute and 10,000
/// a day per client, which one request per video (cached for <see cref="KeepFor"/>) stays well inside. Runs on the
/// UI thread: <see cref="Show"/> starts a request and <see cref="CountsChanged"/> is raised there too.
/// </remarks>
internal sealed partial class VoteCounts : IDisposable
{
    /// <summary>RYD's site, for the credit in Settings.</summary>
    public const string ProjectUrl = "https://returnyoutubedislike.com";

    private const string ApiUrl = "https://returnyoutubedislikeapi.com/votes?videoId=";
    private const int MaxCached = 200;

    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);
    private static readonly HttpClient Http = CreateClient();

    // Counts by video ID, null when the request failed (not asked again until RetryAfter).
    private readonly Dictionary<string, (VideoVotes? Votes, DateTime At)> _cache = [];
    private CancellationTokenSource? _request;
    private string _requestedId = "";
    private string _shownId = "";
    private bool _enabled;

    public VoteCounts(bool enabled) => _enabled = enabled;

    /// <summary>
    /// Counts to show for the open video, or null to show none. Raised on every <see cref="Show"/> that has counts,
    /// not only when they change, so a TV app that loaded again (and lost them) gets them back.
    /// </summary>
    public event EventHandler<VideoVotes?>? CountsChanged;

    /// <summary>Turning it off clears the counts at once; turning it on shows them from the next <see cref="Show"/>.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled) return;
            _enabled = value;
            if (value) return;
            CancelRequest();
            if (_shownId.Length > 0) CountsChanged?.Invoke(this, null);
            _shownId = "";
        }
    }

    /// <summary>Call with each media report: shows the open video's counts, fetching them if needed.</summary>
    public void Show(MediaState state)
    {
        if (!_enabled) return;
        var id = state.Watching && VideoIdPattern().IsMatch(state.VideoId) ? state.VideoId : "";
        if (id.Length == 0)
        {
            CancelRequest();
            if (_shownId.Length > 0) CountsChanged?.Invoke(this, null);
            _shownId = "";
            return;
        }

        if (_cache.TryGetValue(id, out var cached) && DateTime.UtcNow - cached.At < (cached.Votes == null ? RetryAfter : KeepFor))
        {
            CancelRequest();
            if (cached.Votes != null || _shownId.Length > 0) CountsChanged?.Invoke(this, cached.Votes);
            _shownId = cached.Votes != null ? id : "";
            return;
        }

        if (id == _requestedId) return; // already on its way
        CancelRequest();
        // The previous video's counts would be wrong here; the page hides them by ID anyway, but clear them.
        if (_shownId.Length > 0 && _shownId != id) CountsChanged?.Invoke(this, null);
        _shownId = "";
        _requestedId = id;
        _request = new CancellationTokenSource();
        _ = FetchAsync(id, _request.Token);
    }

    private async Task FetchAsync(string id, CancellationToken cancel)
    {
        VideoVotes? votes = null;
        try
        {
            using var response = await Http.GetAsync(ApiUrl + Uri.EscapeDataString(id), cancel);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                AppLog.Write("votes", "Return YouTube Dislike says CastDesk asked too often (429); trying again later.");
            else if (response.StatusCode == HttpStatusCode.NotFound)
                AppLog.Write("votes", $"Return YouTube Dislike has no counts for {id}.");
            else if (!response.IsSuccessStatusCode)
                AppLog.Write("votes", $"Return YouTube Dislike answered {(int)response.StatusCode} for {id}.");
            else
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
                var root = document.RootElement;
                if (root.TryGetProperty("likes", out var likes) && likes.TryGetInt64(out var likeCount) && likeCount >= 0 &&
                    root.TryGetProperty("dislikes", out var dislikes) && dislikes.TryGetInt64(out var dislikeCount) && dislikeCount >= 0)
                    votes = new VideoVotes(id, likeCount, dislikeCount);
                else
                    AppLog.Write("votes", $"Return YouTube Dislike's answer for {id} had no counts.");
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return; // another video opened, or turned off
        }
        catch (Exception exception) // offline, a timeout, an unexpected answer: no counts, nothing more
        {
            AppLog.Write("votes", $"Could not get the counts for {id}: {exception.Message}");
        }

        if (cancel.IsCancellationRequested) return;
        if (_cache.Count >= MaxCached) _cache.Clear();
        _cache[id] = (votes, DateTime.UtcNow);
        _requestedId = "";
        if (!_enabled) return;
        if (votes != null) CountsChanged?.Invoke(this, votes);
        _shownId = votes != null ? id : "";
    }

    private void CancelRequest()
    {
        _request?.Cancel();
        _request?.Dispose();
        _request = null;
        _requestedId = "";
    }

    public void Dispose() => CancelRequest();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 64 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"CastDesk/{UpdateService.CurrentVersion}");
        return client;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdPattern();
}
