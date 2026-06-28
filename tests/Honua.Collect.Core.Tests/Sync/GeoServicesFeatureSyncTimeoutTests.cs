using System.Net;
using Honua.Collect.Core.Sync;

namespace Honua.Collect.Core.Tests.Sync;

/// <summary>
/// Verifies that an HttpClient request <em>timeout</em> (a TaskCanceledException
/// whose token is NOT the caller's) is converted into a failed result on every
/// transport path, rather than escaping as an OperationCanceledException the
/// caller would mistake for a deliberate user cancel.
/// </summary>
public class GeoServicesFeatureSyncTimeoutTests
{
    private static readonly GeoServicesTarget Target = new("https://example.test", "svc", 0);

    /// <summary>
    /// Simulates an HttpClient request timeout: the client cancels the send with
    /// its OWN internal token (not the caller's), surfacing as a TaskCanceledException
    /// even though the caller never cancelled.
    /// </summary>
    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
    }

    [Fact]
    public async Task QueryAsync_request_timeout_returns_failure_not_throw()
    {
        using var handler = new TimeoutHandler();
        using var http = new HttpClient(handler);
        var sync = new GeoServicesFeatureSync(http);

        // Caller token is NOT cancelled, so this must be treated as a timeout.
        var result = await sync.QueryAsync(Target, cancellationToken: CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task AddAttachmentAsync_request_timeout_returns_failure_not_throw()
    {
        var file = Path.GetTempFileName();
        await File.WriteAllBytesAsync(file, new byte[1024]);
        try
        {
            using var handler = new TimeoutHandler();
            using var http = new HttpClient(handler);
            var sync = new GeoServicesFeatureSync(http);

            var result = await sync.AddAttachmentAsync(
                1, file, "application/octet-stream", Target, CancellationToken.None);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task AddAttachmentAsync_honours_caller_cancellation()
    {
        var file = Path.GetTempFileName();
        await File.WriteAllBytesAsync(file, new byte[1024]);
        try
        {
            using var handler = new TimeoutHandler();
            using var http = new HttpClient(handler);
            var sync = new GeoServicesFeatureSync(http);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // A genuinely cancelled caller token must still surface as a cancel,
            // not be swallowed into a failed result.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => sync.AddAttachmentAsync(1, file, "application/octet-stream", Target, cts.Token));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
