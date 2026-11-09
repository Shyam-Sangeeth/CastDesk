using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using CastDesk.Diagnostics;

namespace CastDesk.Dial;

/// <summary>
/// The HTTP half of DIAL (DIscovery And Launch), the protocol a phone's YouTube app uses to start
/// YouTube on a TV. It serves the device description that <see cref="SsdpAdvertiser"/> points phones at,
/// and the "YouTube" app resource the phone reads and launches:
/// <list type="bullet">
/// <item><c>GET /ssdp/device-desc.xml</c>: name, model and ID, plus the Application-URL header.</item>
/// <item><c>GET /apps/YouTube</c>: whether the TV app is running, with the screen details it reported.</item>
/// <item><c>POST /apps/YouTube</c>: launch; the body carries the pairing code and video to open.</item>
/// <item><c>DELETE /apps/YouTube/run</c>: stop.</item>
/// </list>
/// Events are raised on background threads.
/// </summary>
internal sealed partial class DialHttpServer : IDisposable
{
    public const string AppName = "YouTube";
    public const string DeviceDescriptionPath = "/ssdp/device-desc.xml";
    private const string RunningInstancePath = "/apps/" + AppName + "/run";
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private volatile string _friendlyName;
    private readonly string _deviceId;
    private readonly object _stateLock = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _stopping;
    private bool _isRunning;
    private IReadOnlyDictionary<string, string> _additionalData = new Dictionary<string, string>();

    public DialHttpServer(string friendlyName, string deviceId)
    {
        _friendlyName = friendlyName;
        _deviceId = deviceId;
    }

    /// <summary>The name in the device description (the phone's Cast menu).</summary>
    public string FriendlyName
    {
        get => _friendlyName;
        set => _friendlyName = value;
    }

    /// <summary>The port in use after <see cref="Start"/>.</summary>
    public int Port { get; private set; }

    /// <summary>The phone asked to launch YouTube. The argument is the launch data (may be empty).</summary>
    public event EventHandler<string>? LaunchRequested;

    /// <summary>The phone asked to stop YouTube.</summary>
    public event EventHandler? StopRequested;

    /// <summary>
    /// Listens on <paramref name="preferredPort"/>, or on any free port if that one is reserved by Windows
    /// (Hyper-V and WSL reserve port ranges that can move after a reboot) or used by another app.
    /// Phones find the port through SSDP, so it may change between runs.
    /// </summary>
    public void Start(int preferredPort)
    {
        Stop();
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Any, preferredPort);
            listener.Start();
        }
        catch (SocketException exception)
        {
            AppLog.Write("host", $"Port {preferredPort} is unavailable ({exception.SocketErrorCode}); using a free port instead.");
            listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
        }
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _stopping = new CancellationTokenSource();
        _ = AcceptLoopAsync(listener, _stopping.Token);
        AppLog.Write("host", $"DIAL server listening on port {Port}.");
    }

    public void Stop()
    {
        _stopping?.Cancel();
        _stopping = null;
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
    }

    /// <summary>Stores the screen details the TV app reported; phones read them from GET /apps/YouTube.</summary>
    public void SetAdditionalData(IReadOnlyDictionary<string, string> data)
    {
        lock (_stateLock) _additionalData = data;
    }

    /// <summary>The TV app is no longer running (closed on the PC, or stopped).</summary>
    public void MarkStopped()
    {
        lock (_stateLock)
        {
            _isRunning = false;
            _additionalData = new Dictionary<string, string>();
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken stopping)
    {
        using var _ = client;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            var stream = client.GetStream();
            var request = await HttpRequest.ReadAsync(stream, MaxHeaderBytes, MaxBodyBytes, timeout.Token);
            if (request == null) return;

            // Answer with the address the phone reached us on, so the URLs work on its network.
            var localAddress = ((IPEndPoint)client.Client.LocalEndPoint!).Address.MapToIPv4();
            var baseUrl = $"http://{localAddress}:{Port}";
            var refusal = RefusalReason(request);
            var response = refusal == null ? Route(request, baseUrl) : HttpResponse.Empty(403);
            var origin = request.Origin != null ? $" (Origin {request.Origin})" : "";
            AppLog.Write("dial", $"{request.Method} {request.Path} from {((IPEndPoint)client.Client.RemoteEndPoint!).Address}{origin} -> {response.StatusCode}" +
                (refusal != null ? $": {refusal}" : ""));
            await stream.WriteAsync(response.ToBytes(), timeout.Token);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
        {
            // The phone hung up or took too long.
        }
    }

    /// <summary>
    /// Why a request must be refused, or null to answer it. Phones' YouTube apps send no Origin (or YouTube's own,
    /// or an app's "package:" one), and reach CastDesk by IP address. A web page in a browser on this PC or the
    /// network sends its own Origin, so it can't launch or stop the TV app or read its pairing details. A Host
    /// that isn't an IP address (or localhost) means DNS rebinding: a site's own name pointed at this PC.
    /// This is what the DIAL specification asks of servers.
    /// </summary>
    internal static string? RefusalReason(HttpRequest request)
    {
        if (request.Origin is { } origin && !IsAllowedOrigin(origin)) return "a web page's request";
        if (request.Host is { } host && !IsAddressHost(host)) return $"unexpected host {host}";
        return null;
    }

    private static bool IsAllowedOrigin(string origin)
    {
        if (origin.StartsWith("package:", StringComparison.OrdinalIgnoreCase) ||
            origin.StartsWith("ios-app:", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        return uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAddressHost(string host)
    {
        // "192.168.0.2:8765", "[fe80::1]:8765", "localhost:8765"
        var name = host.StartsWith('[') ? host[1..Math.Max(1, host.IndexOf(']'))]
            : host.LastIndexOf(':') is var colon and > 0 ? host[..colon] : host;
        return IPAddress.TryParse(name, out _) || name.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private HttpResponse Route(HttpRequest request, string baseUrl)
    {
        if (request.Method == "OPTIONS") return HttpResponse.Empty(204);

        var path = request.Path.TrimEnd('/');
        if (request.Method == "GET" && path == DeviceDescriptionPath)
        {
            return HttpResponse.Xml(200, DeviceDescriptionXml(baseUrl), ("Application-URL", baseUrl + "/apps/"));
        }

        if (path == "/apps/" + AppName)
        {
            switch (request.Method)
            {
                case "GET":
                    return HttpResponse.Xml(200, AppStatusXml());
                case "POST":
                    bool wasRunning;
                    lock (_stateLock)
                    {
                        wasRunning = _isRunning;
                        _isRunning = true;
                        _additionalData = new Dictionary<string, string>(); // the new launch reports fresh details
                    }
                    LaunchRequested?.Invoke(this, request.Body.Trim());
                    return HttpResponse.Empty(wasRunning ? 200 : 201, ("LOCATION", baseUrl + RunningInstancePath));
            }
        }

        if (request.Method == "DELETE" && path == RunningInstancePath)
        {
            MarkStopped();
            StopRequested?.Invoke(this, EventArgs.Empty);
            return HttpResponse.Empty(200);
        }

        if (request.Method == "POST" && path == "/apps/" + AppName + "/dial_data")
        {
            // The TV app normally posts here through CastDesk's WebView2 interception, but accept it directly too.
            SetAdditionalData(HttpRequest.ParseForm(request.Query + "&" + request.Body));
            return HttpResponse.Empty(200);
        }

        return HttpResponse.Empty(404);
    }

    private string DeviceDescriptionXml(string baseUrl) => $"""
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <specVersion><major>1</major><minor>0</minor></specVersion>
          <URLBase>{Escape(baseUrl)}</URLBase>
          <device>
            <deviceType>urn:dial-multiscreen-org:device:dial:1</deviceType>
            <friendlyName>{Escape(_friendlyName)}</friendlyName>
            <manufacturer>CastDesk</manufacturer>
            <modelName>CastDesk Receiver</modelName>
            <UDN>uuid:{_deviceId}</UDN>
            <serviceList>
              <service>
                <serviceType>urn:dial-multiscreen-org:service:dial:1</serviceType>
                <serviceId>urn:dial-multiscreen-org:serviceId:dial</serviceId>
                <controlURL>/ssdp/notfound</controlURL>
                <eventSubURL>/ssdp/notfound</eventSubURL>
                <SCPDURL>/ssdp/notfound</SCPDURL>
              </service>
            </serviceList>
          </device>
        </root>
        """;

    private string AppStatusXml()
    {
        bool isRunning;
        IReadOnlyDictionary<string, string> data;
        lock (_stateLock)
        {
            isRunning = _isRunning;
            data = _additionalData;
        }

        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        builder.Append("<service xmlns=\"urn:dial-multiscreen-org:schemas:dial\" dialVer=\"1.7\">\n");
        builder.Append($"  <name>{AppName}</name>\n");
        builder.Append("  <options allowStop=\"true\"/>\n");
        builder.Append($"  <state>{(isRunning ? "running" : "stopped")}</state>\n");
        if (isRunning) builder.Append("  <link rel=\"run\" href=\"run\"/>\n");
        if (data.Count > 0)
        {
            builder.Append("  <additionalData>\n");
            foreach (var (key, value) in data)
            {
                if (XmlElementName().IsMatch(key)) builder.Append($"    <{key}>{Escape(value)}</{key}>\n");
            }
            builder.Append("  </additionalData>\n");
        }
        builder.Append("</service>\n");
        return builder.ToString();
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex XmlElementName();

    public void Dispose() => Stop();
}
