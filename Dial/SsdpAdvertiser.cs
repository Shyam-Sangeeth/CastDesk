using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using CastDesk.Diagnostics;

namespace CastDesk.Dial;

/// <summary>
/// The discovery half of DIAL: SSDP over UDP multicast (239.255.255.250:1900). Answers phones' M-SEARCH
/// queries for DIAL devices and announces CastDesk (ssdp:alive) every few minutes, on every IPv4 network
/// adapter. Each answer points at the device description served by <see cref="DialHttpServer"/>, using
/// the PC's address on the phone's network.
/// </summary>
internal sealed class SsdpAdvertiser : IDisposable
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.255.250");
    private const int SsdpPort = 1900;
    private const string MaxAge = "max-age=1800";
    private static readonly TimeSpan AliveInterval = TimeSpan.FromMinutes(5);
    private static readonly string ServerHeader = $"Windows/{Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor} UPnP/1.1 CastDesk/2.0";

    private readonly string _deviceId;
    private readonly Func<int> _httpPort;
    private readonly string[] _notificationTypes;
    private readonly HashSet<string> _joinedAddresses = new();
    private Socket? _socket;
    private CancellationTokenSource? _stopping;
    private System.Threading.Timer? _aliveTimer;

    /// <param name="deviceId">The UUID in every USN header.</param>
    /// <param name="httpPort">The DIAL HTTP server's current port.</param>
    public SsdpAdvertiser(string deviceId, Func<int> httpPort)
    {
        _deviceId = deviceId;
        _httpPort = httpPort;
        _notificationTypes = new[]
        {
            "upnp:rootdevice",
            "urn:dial-multiscreen-org:device:dial:1",
            "urn:dial-multiscreen-org:service:dial:1",
            $"uuid:{deviceId}",
        };
    }

    public void Start()
    {
        Stop();
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Windows' own SSDP service also uses port 1900, so share it.
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, SsdpPort));
        _socket = socket;
        JoinMulticastGroups();

        _stopping = new CancellationTokenSource();
        _ = ReceiveLoopAsync(socket, _stopping.Token);
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _aliveTimer = new System.Threading.Timer(_ => OnPoolThread(() => SendNotifications("ssdp:alive")), null, TimeSpan.Zero, AliveInterval);
        AppLog.Write("host", $"SSDP discovery on {string.Join(", ", _joinedAddresses)}.");
    }

    public void Stop()
    {
        if (_socket == null) return;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _aliveTimer?.Dispose();
        _aliveTimer = null;
        SendNotifications("ssdp:byebye"); // lets phones drop CastDesk from their lists straight away
        _stopping?.Cancel();
        _stopping = null;
        _socket.Dispose();
        _socket = null;
        _joinedAddresses.Clear();
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => OnPoolThread(() =>
    {
        // A new Wi-Fi network or adapter: listen on it too and announce CastDesk there.
        JoinMulticastGroups();
        SendNotifications("ssdp:alive");
    });

    /// <summary>
    /// Runs timer and network-change work, which arrives on pool threads where any exception would end CastDesk
    /// (listing the adapters can throw NetworkInformationException while they change, for one).
    /// </summary>
    private static void OnPoolThread(Action work)
    {
        try
        {
            work();
        }
        catch (Exception exception)
        {
            AppLog.Write("host", $"SSDP announcement failed: {exception.Message}");
        }
    }

    private void JoinMulticastGroups()
    {
        var socket = _socket;
        if (socket == null) return;
        foreach (var address in LocalAddresses())
        {
            lock (_joinedAddresses)
            {
                if (!_joinedAddresses.Add(address.ToString())) continue;
            }
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastAddress, address));
            }
            catch (SocketException exception)
            {
                AppLog.Write("host", $"SSDP could not listen on {address}: {exception.SocketErrorCode}");
            }
            catch (ObjectDisposedException)
            {
                return; // stopped meanwhile: a network change arrived (on a pool thread) as the receiver stopped
            }
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // for example ICMP "port unreachable" from an earlier reply
            }

            var message = Encoding.ASCII.GetString(buffer, 0, result.ReceivedBytes);
            if (!message.StartsWith("M-SEARCH", StringComparison.OrdinalIgnoreCase)) continue;
            var searchTarget = ReadHeader(message, "ST");
            if (searchTarget == null) continue;
            AnswerSearch(socket, (IPEndPoint)result.RemoteEndPoint, searchTarget);
        }
    }

    private void AnswerSearch(Socket socket, IPEndPoint phone, string searchTarget)
    {
        var types = searchTarget == "ssdp:all"
            ? _notificationTypes
            : _notificationTypes.Where(type => type.Equals(searchTarget, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (types.Length == 0) return;

        var localAddress = LocalAddressFor(phone.Address);
        if (localAddress == null) return;
        var location = $"http://{localAddress}:{_httpPort()}{DialHttpServer.DeviceDescriptionPath}";
        foreach (var type in types)
        {
            var reply =
                "HTTP/1.1 200 OK\r\n" +
                $"CACHE-CONTROL: {MaxAge}\r\n" +
                $"DATE: {DateTime.UtcNow:R}\r\n" +
                "EXT:\r\n" +
                $"LOCATION: {location}\r\n" +
                $"SERVER: {ServerHeader}\r\n" +
                $"ST: {type}\r\n" +
                $"USN: {UniqueServiceName(type)}\r\n" +
                "BOOTID.UPNP.ORG: 1\r\n" +
                "CONFIGID.UPNP.ORG: 1\r\n" +
                "\r\n";
            try
            {
                socket.SendTo(Encoding.ASCII.GetBytes(reply), phone);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    /// <summary>Multicasts NOTIFY messages (ssdp:alive or ssdp:byebye) on every adapter.</summary>
    private void SendNotifications(string subType)
    {
        foreach (var address in LocalAddresses())
        {
            try
            {
                using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                sender.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
                sender.Bind(new IPEndPoint(address, 0));
                var location = $"http://{address}:{_httpPort()}{DialHttpServer.DeviceDescriptionPath}";
                foreach (var type in _notificationTypes)
                {
                    var notify =
                        "NOTIFY * HTTP/1.1\r\n" +
                        $"HOST: {MulticastAddress}:{SsdpPort}\r\n" +
                        $"CACHE-CONTROL: {MaxAge}\r\n" +
                        $"LOCATION: {location}\r\n" +
                        $"NT: {type}\r\n" +
                        $"NTS: {subType}\r\n" +
                        $"SERVER: {ServerHeader}\r\n" +
                        $"USN: {UniqueServiceName(type)}\r\n" +
                        "BOOTID.UPNP.ORG: 1\r\n" +
                        "CONFIGID.UPNP.ORG: 1\r\n" +
                        "\r\n";
                    sender.SendTo(Encoding.ASCII.GetBytes(notify), new IPEndPoint(MulticastAddress, SsdpPort));
                }
            }
            catch (SocketException)
            {
                // The adapter went away; the next round or a network change will catch up.
            }
        }
    }

    private string UniqueServiceName(string type) =>
        type.StartsWith("uuid:", StringComparison.Ordinal) ? type : $"uuid:{_deviceId}::{type}";

    /// <summary>The PC's address on the route to <paramref name="remote"/> (no packet is sent).</summary>
    private static IPAddress? LocalAddressFor(IPAddress remote)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(remote, SsdpPort));
            return ((IPEndPoint)probe.LocalEndPoint!).Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static IEnumerable<IPAddress> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                              adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                              adapter.SupportsMulticast)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Where(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(unicast => unicast.Address);

    private static string? ReadHeader(string message, string name)
    {
        foreach (var line in message.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }
        return null;
    }

    public void Dispose() => Stop();
}
