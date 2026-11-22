using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace StardewAI.Companion.Logging;

internal sealed class FileLogger
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    private const long MaxDirectoryBytes = 100L * 1024 * 1024;
    private const long MaxFileBytes = 10L * 1024 * 1024;
    private static readonly Regex LogName = new(
        @"\A(?:desktop|runtime|mod)-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}\.log\z", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly Channel<Entry> _entries = Channel.CreateBounded<Entry>(
        new BoundedChannelOptions(2048) { SingleReader = true });
    private readonly Task _writer;
    private string _directoryPath;
    private readonly string _defaultDirectory;
    private int _dropped;
    private string? _storageError;
    private record struct Entry(DateTimeOffset Time, string Component, string Level, string Event, string Message, object? Details);

    internal FileLogger(string directory, string component = "desktop", string? configuredDirectory = null)
    {
        _defaultDirectory = directory;
        _directoryPath = _defaultDirectory;
        _diagnosticComponent = component;
        _configuredDirectory = configuredDirectory;
        _writer = Task.Run(WriteLoopAsync);
    }

    private readonly string _diagnosticComponent;
    private readonly string? _configuredDirectory;
    internal string DirectoryPath => Volatile.Read(ref _directoryPath);
    internal string? StorageError => Volatile.Read(ref _storageError);

    internal void Write(string component, string level, string eventName, string message, object? details = null)
    {
        if (!_entries.Writer.TryWrite(new(DateTimeOffset.Now, component, level, eventName, message, details)))
            Interlocked.Increment(ref _dropped);
    }

    internal async Task StopAsync()
    {
        _entries.Writer.TryComplete();
        await Task.WhenAny(_writer, Task.Delay(TimeSpan.FromSeconds(2)));
    }

    private async Task WriteLoopAsync()
    {
        var files = new Dictionary<string, (DateOnly Day, FileStream Stream)>();
        var dirty = new HashSet<FileStream>();
        DateTimeOffset nextCleanup = DateTimeOffset.MinValue;
        DateTimeOffset retryAt = DateTimeOffset.MinValue;
        long directoryBytes = 0;
        bool directoryReady = false;
        try
        {
            while (await _entries.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                dirty.Clear();
                for (int count = 0; count < 128 && _entries.Reader.TryRead(out var entry); count++)
                {
                    if (DateTimeOffset.UtcNow < retryAt) continue;
                    try
                    {
                        if (!directoryReady)
                        {
                            Volatile.Write(ref _directoryPath, FindWritableDirectory());
                            directoryBytes = Cleanup();
                            nextCleanup = DateTimeOffset.UtcNow.AddMinutes(10);
                            directoryReady = true;
                        }
                        int dropped = Interlocked.Exchange(ref _dropped, 0);
                        if (dropped > 0)
                            Append(new(entry.Time, _diagnosticComponent, "warn", "log_queue_full", "日志积压，部分记录已跳过。", new { dropped }));
                        Append(entry);
                        if (directoryBytes > MaxDirectoryBytes || DateTimeOffset.UtcNow >= nextCleanup)
                        {
                            directoryBytes = Cleanup();
                            nextCleanup = DateTimeOffset.UtcNow.AddMinutes(10);
                        }
                        Volatile.Write(ref _storageError, null);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        Volatile.Write(ref _storageError, "暂时无法写入日志，请检查目录权限和磁盘空间。");
                        retryAt = DateTimeOffset.UtcNow.AddSeconds(5);
                        foreach (var file in files.Values) Close(file.Stream);
                        files.Clear();
                        dirty.Clear();
                        directoryReady = false;
                    }
                }
                foreach (var stream in dirty)
                {
                    try { stream.Flush(); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        Volatile.Write(ref _storageError, "暂时无法写入日志，请检查目录权限和磁盘空间。");
                        retryAt = DateTimeOffset.UtcNow.AddSeconds(5);
                    }
                }
            }
        }
        finally
        {
            foreach (var file in files.Values) Close(file.Stream);
        }

        void Append(Entry entry)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                time = entry.Time, component = entry.Component, level = entry.Level,
                @event = entry.Event, message = entry.Message, details = entry.Details,
            }, JsonOptions);
            var day = DateOnly.FromDateTime(entry.Time.DateTime);
            if (!files.TryGetValue(entry.Component, out var current)
                || current.Day != day || current.Stream.Position + bytes.Length + 1 > MaxFileBytes)
            {
                if (files.Remove(entry.Component))
                {
                    dirty.Remove(current.Stream);
                    Close(current.Stream);
                    nextCleanup = DateTimeOffset.MinValue;
                }
                Directory.CreateDirectory(DirectoryPath);
                string nonce = Guid.NewGuid().ToString("N")[..8];
                string name = FormattableString.Invariant($"{entry.Component}-{entry.Time:yyyyMMdd-HHmmss}-{nonce}.log");
                var stream = new FileStream(Path.Combine(DirectoryPath, name),
                    FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 1);
                current = (day, stream);
                files[entry.Component] = current;
            }
            Array.Resize(ref bytes, bytes.Length + 1);
            bytes[^1] = (byte)'\n';
            current.Stream.Write(bytes);
            directoryBytes += bytes.Length;
            dirty.Add(current.Stream);
        }
    }

    private string FindWritableDirectory()
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var path in new[] { _configuredDirectory, _defaultDirectory, Path.Combine(localData, "StardewGameCompanion", "logs") })
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) continue;
            try
            {
                Directory.CreateDirectory(path);
                using var probe = new FileStream(Path.Combine(path, $".write-{Guid.NewGuid():N}"), FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
                return Path.GetFullPath(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        throw new IOException("No writable log directory.");
    }

    private long Cleanup()
    {
        var cutoff = DateTime.UtcNow - Retention;
        var logs = new DirectoryInfo(DirectoryPath).EnumerateFiles("*.log")
            .Where(file => LogName.IsMatch(file.Name) && (file.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToArray();
        var sized = new List<(FileInfo File, long Length)>(logs.Length);
        long total = 0;
        foreach (var file in logs)
        {
            try
            {
                long length = file.Length;
                sized.Add((file, length));
                total += length;
            }
            catch (FileNotFoundException) { }
        }
        foreach (var (file, length) in sized)
        {
            if (file.LastWriteTimeUtc >= cutoff && total <= MaxDirectoryBytes) break;
            try
            {
                file.Delete();
                total -= length;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Files open for writing deny deletion, including files from another instance.
            }
        }
        return total;
    }

    private static void Close(FileStream stream)
    {
        try { stream.Dispose(); }
        catch (IOException) { }
    }
}
