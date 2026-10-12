using System.IO;
using System.Text.Json;
using System.Threading.Channels;

namespace AimSense;

internal sealed class TraceRecorder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };
    private readonly Channel<object> _queue = Channel.CreateUnbounded<object>(new() { SingleReader = true, SingleWriter = true });
    private readonly Task _writer;
    public string Path { get; }
    public Exception? Error { get; private set; }

    public TraceRecorder(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
        _writer = Task.Run(async () =>
        {
            try
            {
                await using var writer = new StreamWriter(Path);
                await foreach (object record in _queue.Reader.ReadAllAsync())
                    await writer.WriteLineAsync(JsonSerializer.Serialize(record, record.GetType(), JsonOptions));
            }
            catch (Exception ex) { Error = ex; _queue.Writer.TryComplete(ex); }
        });
    }
    public void Add(object record) => _queue.Writer.TryWrite(record);
    public Task CompleteAsync() { _queue.Writer.TryComplete(); return _writer; }
}
