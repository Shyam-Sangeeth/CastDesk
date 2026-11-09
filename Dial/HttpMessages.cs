using System.Text;
using System.Web;

namespace CastDesk.Dial;

/// <summary>One HTTP/1.1 request, as much of it as the DIAL server needs.</summary>
internal sealed class HttpRequest
{
    private HttpRequest(string method, string path, string query, string body, string? host, string? origin)
    {
        Method = method;
        Path = path;
        Query = query;
        Body = body;
        Host = host;
        Origin = origin;
    }

    public string Method { get; }
    public string Path { get; }
    public string Query { get; }
    public string Body { get; }

    /// <summary>The Host header, or null if there was none.</summary>
    public string? Host { get; }

    /// <summary>The Origin header (browsers send it with requests from web pages), or null if there was none.</summary>
    public string? Origin { get; }

    /// <summary>Reads one request (headers, then a body of Content-Length bytes). Null if the stream ends first.</summary>
    public static async Task<HttpRequest?> ReadAsync(Stream stream, int maxHeaderBytes, int maxBodyBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var received = new MemoryStream();
        int headerEnd;
        while ((headerEnd = IndexOfHeaderEnd(received)) < 0)
        {
            if (received.Length > maxHeaderBytes) return null;
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return null;
            received.Write(buffer, 0, read);
        }

        var all = received.ToArray();
        var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        var contentLength = 0;
        string? host = null, origin = null;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out contentLength);
            else if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) host = value;
            else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) origin = value;
        }
        if (contentLength < 0 || contentLength > maxBodyBytes) return null;

        var body = new MemoryStream();
        body.Write(all, headerEnd + 4, all.Length - headerEnd - 4);
        while (body.Length < contentLength)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            body.Write(buffer, 0, read);
        }

        var target = requestLine[1];
        var questionMark = target.IndexOf('?');
        var path = questionMark >= 0 ? target[..questionMark] : target;
        var query = questionMark >= 0 ? target[(questionMark + 1)..] : "";
        var bodyText = Encoding.UTF8.GetString(body.ToArray(), 0, (int)Math.Min(body.Length, contentLength));
        return new HttpRequest(requestLine[0].ToUpperInvariant(), path, query, bodyText, host, origin);
    }

    /// <summary>Parses "a=1&amp;b=2" (URL-encoded form or query string). Later keys win.</summary>
    public static IReadOnlyDictionary<string, string> ParseForm(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var parsed = HttpUtility.ParseQueryString(text);
        foreach (var key in parsed.AllKeys)
        {
            if (!string.IsNullOrEmpty(key)) result[key] = parsed[key] ?? "";
        }
        return result;
    }

    private static int IndexOfHeaderEnd(MemoryStream stream)
    {
        var data = stream.GetBuffer();
        for (var i = 0; i + 3 < stream.Length; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n') return i;
        }
        return -1;
    }
}

/// <summary>An HTTP/1.1 response; the connection always closes after it.</summary>
internal sealed class HttpResponse
{
    private readonly string _contentType;
    private readonly byte[] _body;
    private readonly (string Name, string Value)[] _headers;

    private HttpResponse(int statusCode, string contentType, byte[] body, (string, string)[] headers)
    {
        StatusCode = statusCode;
        _contentType = contentType;
        _body = body;
        _headers = headers;
    }

    public int StatusCode { get; }

    public static HttpResponse Xml(int statusCode, string xml, params (string, string)[] headers) =>
        new(statusCode, "application/xml; charset=utf-8", Encoding.UTF8.GetBytes(xml), headers);

    public static HttpResponse Empty(int statusCode, params (string, string)[] headers) =>
        new(statusCode, "text/plain", Array.Empty<byte>(), headers);

    public byte[] ToBytes()
    {
        var head = new StringBuilder();
        head.Append($"HTTP/1.1 {StatusCode} {ReasonPhrase(StatusCode)}\r\n");
        head.Append($"Content-Type: {_contentType}\r\n");
        head.Append($"Content-Length: {_body.Length}\r\n");
        head.Append("Connection: close\r\n");
        foreach (var (name, value) in _headers) head.Append($"{name}: {value}\r\n");
        head.Append("\r\n");
        return Encoding.ASCII.GetBytes(head.ToString()).Concat(_body).ToArray();
    }

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        404 => "Not Found",
        _ => "Status",
    };
}
