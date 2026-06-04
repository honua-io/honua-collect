using System.Net;
using Honua.Collect.Core.Sync;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Sync;

/// <summary>
/// Verifies that the feature-sync client honours an already-cancelled token on
/// its submit and query paths rather than performing the network round-trip.
/// </summary>
public class GeoServicesFeatureSyncCancellationTests
{
    private static readonly GeoServicesTarget Target = new("https://example.test", "svc", 0);

    // A handler that would otherwise SUCCEED — so a thrown OperationCanceledException
    // can only come from the cancellation check, not from a transport failure.
    private sealed class SuccessHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var json = request.RequestUri!.AbsolutePath.EndsWith("query", StringComparison.Ordinal)
                ? """{"features":[]}"""
                : """{"addResults":[{"objectId":1,"success":true}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }
    }

    [Fact]
    public async Task SubmitAsync_with_cancelled_token_throws_and_does_not_call_server()
    {
        using var handler = new SuccessHandler();
        using var http = new HttpClient(handler);
        var sync = new GeoServicesFeatureSync(http, FeatureSyncRetryPolicy.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var record = new FieldRecord { RecordId = "r1", FormId = "f" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sync.SubmitAsync(record, Target, cts.Token));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task QueryAsync_with_cancelled_token_throws_before_first_request()
    {
        using var handler = new SuccessHandler();
        using var http = new HttpClient(handler);
        var sync = new GeoServicesFeatureSync(http);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sync.QueryAsync(Target, cancellationToken: cts.Token));
        Assert.Equal(0, handler.Calls);
    }
}
