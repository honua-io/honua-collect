using System.Net;
using Honua.Collect.Core.Ai;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Tests.Ai;

/// <summary>
/// Verifies <see cref="AnthropicPhotoToFieldsProvider.ExtractAsync"/> propagates
/// cancellation (rather than degrading to an empty result) when handed an
/// already-cancelled token. The provider's contract rethrows
/// <see cref="OperationCanceledException"/>; only genuine failures degrade.
/// </summary>
public class AnthropicPhotoToFieldsProviderCancellationTests
{
    private static FormDefinition Form() => new()
    {
        FormId = "f",
        Name = "f",
        Sections =
        [
            new FormSection
            {
                SectionId = "s",
                Label = "s",
                Fields = [new FormField { FieldId = "species", Label = "Species", Type = FormFieldType.Text }],
            },
        ],
    };

    // Would otherwise succeed, so a thrown cancellation cannot come from transport.
    private sealed class SuccessHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"content":[{"type":"tool_use","name":"provide_fields","input":{"fields":[]}}]}"""),
            });
        }
    }

    [Fact]
    public async Task ExtractAsync_with_cancelled_token_throws_and_does_not_call_api()
    {
        var photo = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(photo, [1, 2, 3, 4]);

            using var handler = new SuccessHandler();
            using var http = new HttpClient(handler);
            var provider = new AnthropicPhotoToFieldsProvider(http, new AnthropicPhotoToFieldsOptions
            {
                ApiKey = "sk-test-key",
            });

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => provider.ExtractAsync(photo, Form(), cts.Token));
            Assert.Equal(0, handler.Calls);
        }
        finally
        {
            File.Delete(photo);
        }
    }
}
