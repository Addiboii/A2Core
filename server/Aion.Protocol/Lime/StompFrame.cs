using System.Text;

namespace Aion.Protocol.Lime;

/// <summary>
/// A STOMP 1.2 frame as the client's CCStomp plugin exchanges it over WebSocket text frames:
/// command line, header lines, blank line, body, NUL. Header values are not escaped by the
/// client for the commands it sends (CONNECT, SUBSCRIBE, UNSUBSCRIBE, DISCONNECT).
/// </summary>
public sealed record StompFrame(string Command, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public static StompFrame Create(string command, IReadOnlyDictionary<string, string>? headers = null, string body = "") =>
        new(command, headers ?? new Dictionary<string, string>(), body);

    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : "";

    public byte[] Encode()
    {
        var text = new StringBuilder(Command).Append('\n');
        foreach (var (key, value) in Headers) text.Append(key).Append(':').Append(value).Append('\n');
        text.Append('\n').Append(Body).Append('\0');
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary>Splits buffered WebSocket text into complete frames; heart-beat newlines are skipped.</summary>
    public static IReadOnlyList<StompFrame> ReadAll(StringBuilder buffer)
    {
        var frames = new List<StompFrame>();
        while (true)
        {
            var text = buffer.ToString();
            var end = text.IndexOf('\0');
            if (end < 0)
            {
                if (text.Trim('\r', '\n').Length == 0) buffer.Clear();
                return frames;
            }
            buffer.Remove(0, end + 1);
            var frame = text[..end].TrimStart('\r', '\n');
            if (frame.Length > 0) frames.Add(Parse(frame));
        }
    }

    public static StompFrame Parse(string frame)
    {
        var split = frame.IndexOf("\n\n", StringComparison.Ordinal);
        var head = split < 0 ? frame : frame[..split];
        var body = split < 0 ? "" : frame[(split + 2)..];
        var lines = head.Replace("\r", "").Split('\n');
        var headers = new Dictionary<string, string>();
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers.TryAdd(line[..colon], line[(colon + 1)..]);
        }
        return new StompFrame(lines[0].Trim(), headers, body);
    }
}
