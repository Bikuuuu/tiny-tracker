using System.Globalization;
using System.Text;

namespace TinyTracker.Core.Logging;

// app.log plus one older file, each under maxBytes. Logging never throws.
public sealed class FileLog(string path, TimeProvider time, long maxBytes = FileLog.MaxBytes)
{
    public const long MaxBytes = 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly string _older = Path.ChangeExtension(path, ".1.log");
    private bool _closed;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? error = null) => Write("ERROR", error is null ? message : $"{message}: {error}");

    // Later lines are dropped, so the log's folder can be deleted.
    public void Close()
    {
        lock (_gate) _closed = true;
    }

    private void Write(string level, string message)
    {
        var stamp = time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var line = $"{stamp} {level} {message}{Environment.NewLine}";
        lock (_gate)
        {
            if (_closed) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var file = new FileInfo(path);
                if (file.Exists && file.Length + Encoding.UTF8.GetByteCount(line) > maxBytes)
                    File.Move(path, _older, overwrite: true);
                File.AppendAllText(path, line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
