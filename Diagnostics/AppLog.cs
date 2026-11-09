using System.Diagnostics;
using System.Text;
using CastDesk.Configuration;

namespace CastDesk.Diagnostics;

/// <summary>
/// Appends timestamped lines from the app and the bridge to %LOCALAPPDATA%\CastDesk\castdesk.log.
/// </summary>
internal static class AppLog
{
    private const long MaxLogBytes = 1_000_000;

    private static readonly object WriterLock = new();
    private static StreamWriter? _writer;
    private static long _written; // bytes in the current log, so a copy running for weeks still starts a new one

    public static string FilePath { get; } = Path.Combine(CastDeskOptions.DataDirectory, "castdesk.log");

    /// <param name="source">Who wrote the line, such as "host", "dial", "tv", "lock" or "discord".</param>
    public static void Write(string source, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] {message}";
        Debug.WriteLine(line);
        lock (WriterLock)
        {
            try
            {
                if (_writer != null && _written > MaxLogBytes)
                {
                    _writer.Dispose();
                    _writer = null;
                }
                _writer ??= OpenWriter();
                _writer.WriteLine(line);
                _written += Encoding.UTF8.GetByteCount(line) + 2;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down.
            }
        }
    }

    private static StreamWriter OpenWriter()
    {
        Directory.CreateDirectory(CastDeskOptions.DataDirectory);

        // Once the log passes 1 MB, start a new one and keep the previous one as castdesk.log.old.
        var existingLog = new FileInfo(FilePath);
        // This also runs while CastDesk is running, as soon as the log passes 1 MB (see Write).
        if (existingLog.Exists && existingLog.Length > MaxLogBytes)
        {
            try
            {
                File.Move(FilePath, FilePath + ".old", overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // castdesk.log.old is open somewhere: keep writing to this log rather than lose lines.
            }
        }

        var writer = new StreamWriter(FilePath, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        _written = writer.BaseStream.Length;
        return writer;
    }
}
