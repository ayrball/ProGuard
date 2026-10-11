using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProGuard.Engine.IPC;

internal static class PipeLineReader
{
    /// <summary>
    /// Like ReadLineAsync, but a line longer than <paramref name="maxLength"/> throws <see cref="InvalidDataException"/>,
    /// so an unprivileged client can't make the engine buffer unbounded input.
    /// </summary>
    public static async Task<string?> ReadLineAsync(StreamReader reader, int maxLength, CancellationToken token)
    {
        var line = new StringBuilder();
        var buffer = new char[1];
        while (true)
        {
            if (await reader.ReadAsync(buffer.AsMemory(), token) == 0)
                return line.Length > 0 ? line.ToString() : null;
            var c = buffer[0];
            if (c == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= maxLength) throw new InvalidDataException($"Line longer than {maxLength} characters");
            line.Append(c);
        }
    }
}
