namespace Honua.Collect.Spike.Shared;
public interface ISpikeCapture
{
    bool IsSupported { get; }
    // Timer begins after the native picker returns, before file copy and durable queue write.
    Task<(string Path, long Started)> CaptureAsync();
}
public sealed class BrowserCapture : ISpikeCapture
{
    public bool IsSupported => false;
    public Task<(string Path, long Started)> CaptureAsync() => throw new NotSupportedException();
}
