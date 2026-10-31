using System.Text;
using System.Threading.Channels;

namespace Aion.Host.Logging;

/// <summary>
/// Background UTF-8 line writer. One reader task owns the file: it writes everything queued, flushes to disk whenever the
/// queue runs dry (a log line is on disk right after it is written, with no timer to starve), and survives a file it cannot
/// open or write (another process holding it, a failed write): it reports once on stderr, moves to the next free
/// <c>name-N.ext</c> and keeps going. A silent faulted writer task left both server-*.log and debug-*.log at 0 bytes before.
/// </summary>
public sealed class ChannelFileLogWriter : IAsyncDisposable
{
    const int OpenAttempts = 6;
    static readonly TimeSpan OpenRetry = TimeSpan.FromSeconds(5);

    readonly Channel<string> queue;
    readonly Task writerTask;
    readonly string requestedPath;
    FileStream? file;
    StreamWriter? text;
    DateTime retryOpenAt;

    /// <summary>The file lines go to now (the requested path, or a <c>-N</c> sibling when that one could not be opened).</summary>
    public string Path { get; private set; }

    /// <summary>The last open or write failure, for diagnostics; null while all is well.</summary>
    public Exception? LastError { get; private set; }

    public ChannelFileLogWriter(string path, int capacity = 8192)
    {
        Path = requestedPath = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        queue = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        EnsureOpen();
        writerTask = Task.Run(WriteLoopAsync);
    }

    public void Enqueue(string line)
    {
        if (!queue.Writer.TryWrite(line))
            _ = queue.Writer.WriteAsync(line);
    }

    async Task WriteLoopAsync()
    {
        try
        {
            while (await queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (queue.Reader.TryRead(out var line)) WriteLine(line);
                Flush();
            }
        }
        catch (Exception ex)
        {
            Report("log writer stopped", ex);
        }
        finally
        {
            Flush();
            Close();
        }
    }

    void WriteLine(string line)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!EnsureOpen()) return;
            try { text!.WriteLine(line); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                Report("log write failed", ex);
                Close();
            }
        }
    }

    void Flush()
    {
        if (text is null) return;
        try
        {
            text.Flush();
            file!.Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Report("log flush failed", ex);
            Close();
        }
    }

    bool EnsureOpen()
    {
        if (text is not null) return true;
        if (DateTime.UtcNow < retryOpenAt) return false;
        for (var n = 0; n < OpenAttempts; n++)
        {
            var candidate = n == 0 ? requestedPath : Sibling(requestedPath, n);
            try
            {
                file = new FileStream(candidate, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 65536);
                text = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = false };
                if (!string.Equals(candidate, Path, StringComparison.Ordinal))
                    Console.Error.WriteLine($"[log] {requestedPath} is not writable, logging to {candidate}");
                Path = candidate;
                LastError = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report($"cannot open log file {candidate}", ex);
            }
        }
        retryOpenAt = DateTime.UtcNow + OpenRetry;
        return false;
    }

    static string Sibling(string path, int n) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!,
            $"{System.IO.Path.GetFileNameWithoutExtension(path)}-w{n}{System.IO.Path.GetExtension(path)}");

    void Report(string what, Exception ex)
    {
        if (LastError is null) Console.Error.WriteLine($"[log] {what}: {ex.Message}");
        LastError = ex;
    }

    void Close()
    {
        try { text?.Dispose(); } catch (Exception) { }
        try { file?.Dispose(); } catch (Exception) { }
        text = null;
        file = null;
    }

    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        try { await writerTask.ConfigureAwait(false); } catch (Exception) { }
    }
}
