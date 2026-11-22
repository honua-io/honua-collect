// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Honua.Collect.Core.Sync;

namespace Honua.Collect.Core.Tests.Sync;

public class GeoServicesFeatureSyncTimeoutTests
{
    private static readonly GeoServicesTarget Target = new("https://example.test", "svc", 0);

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
