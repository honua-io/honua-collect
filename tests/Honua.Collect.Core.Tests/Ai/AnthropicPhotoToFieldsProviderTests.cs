using System.Net;
using System.Text;
using System.Text.Json;
using Honua.Collect.Core.Ai;
using Honua.Collect.Core.Editions;
using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Tests.Ai;

public class AnthropicPhotoToFieldsProviderTests
{
    private static FormDefinition Form() => new()
    {
        FormId = "tree-survey",
        Name = "Tree Survey",
        Sections =
        [
            new FormSection
            {
                SectionId = "s",
                Label = "s",
                Fields =
                [
                    new FormField { FieldId = "species", Label = "Species", Type = FormFieldType.Text },
                    new FormField { FieldId = "count", Label = "Count", Type = FormFieldType.Numeric },
                    new FormField
                    {
                        FieldId = "health",
                        Label = "Health",
                        Type = FormFieldType.SingleChoice,
                        Choices =
                        [
                            new FieldChoice { Value = "healthy", Label = "Healthy" },
                            new FieldChoice { Value = "diseased", Label = "Diseased" },
                        ],
                    },
                    // Media field must NOT be offered to the model.
                    new FormField { FieldId = "photo", Label = "Photo", Type = FormFieldType.Photo },
                ],
            },
        ],
    };

    private static string ToolUseResponse(params (string id, string value, double confidence)[] fields)
    {
        var items = fields.Select(f =>
            $$"""{"field_id":"{{f.id}}","value":"{{f.value}}","confidence":{{f.confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""");
        var fieldsJson = string.Join(",", items);
        return $$"""
        {
          "id": "msg_1",
          "type": "message",
          "role": "assistant",
          "stop_reason": "tool_use",
          "content": [
            {
              "type": "tool_use",
              "id": "toolu_1",
              "name": "provide_fields",
              "input": { "fields": [ {{fieldsJson}} ] }
            }
          ]
        }
        """;
    }

    private static (AnthropicPhotoToFieldsProvider provider, StubHandler handler) Provider(
        HttpResponseMessage response, string model = "claude-opus-4-8")
    {
        var handler = new StubHandler(response);
        var http = new HttpClient(handler);
        var provider = new AnthropicPhotoToFieldsProvider(http, new AnthropicPhotoToFieldsOptions
        {
            ApiKey = "sk-test-key",
            Model = model,
        });
        return (provider, handler);
    }

    private static string WriteTempImage(string ext = ".jpg")
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        File.WriteAllBytes(path, [0x01, 0x02, 0x03, 0x04]);
        return path;
    }

    [Fact]
    public async Task Maps_tool_output_to_field_extraction_with_confidence()
    {
        var ok = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                ToolUseResponse(("species", "Oak", 0.92), ("count", "3", 0.8)), Encoding.UTF8, "application/json"),
        };
        var (provider, _) = Provider(ok);
        var path = WriteTempImage();
        try
        {
            var result = await provider.ExtractAsync(path, Form());

            Assert.Null(result.Unmapped);
            Assert.Equal(2, result.Fields.Count);

            var species = result.Fields.Single(f => f.FieldId == "species");
            Assert.Equal("Oak", species.Value);
            Assert.Equal(0.92, species.Confidence, 3);

            var count = result.Fields.Single(f => f.FieldId == "count");
            Assert.Equal(3L, count.Value); // Numeric coerced from "3"
            Assert.Equal(0.8, count.Confidence, 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Sends_correct_url_headers_image_block_and_target_fields()
    {
        var ok = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ToolUseResponse(("species", "Oak", 0.9)), Encoding.UTF8, "application/json"),
        };
        var (provider, handler) = Provider(ok, model: "claude-opus-4-8");
        var path = WriteTempImage(".png");
        try
        {
            await provider.ExtractAsync(path, Form());

            var req = handler.LastRequest!;
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("https://api.anthropic.com/v1/messages", req.RequestUri!.ToString());
            Assert.Equal("sk-test-key", req.Headers.GetValues("x-api-key").Single());
            Assert.Equal("2023-06-01", req.Headers.GetValues("anthropic-version").Single());
            Assert.Equal("application/json", req.Content!.Headers.ContentType!.MediaType);

            using var doc = JsonDocument.Parse(handler.LastBody!);
            var root = doc.RootElement;
            Assert.Equal("claude-opus-4-8", root.GetProperty("model").GetString());

            // Image block present with png media type and base64 data.
            var content = root.GetProperty("messages")[0].GetProperty("content");
            var image = content.EnumerateArray().Single(b => b.GetProperty("type").GetString() == "image");
            var source = image.GetProperty("source");
            Assert.Equal("base64", source.GetProperty("type").GetString());
            Assert.Equal("image/png", source.GetProperty("media_type").GetString());
            Assert.False(string.IsNullOrEmpty(source.GetProperty("data").GetString()));

            // Prompt names the target fields and excludes the media field.
            var text = content.EnumerateArray().Single(b => b.GetProperty("type").GetString() == "text")
                .GetProperty("text").GetString()!;
            Assert.Contains("species", text);
            Assert.Contains("count", text);
            Assert.Contains("health", text);
            Assert.Contains("healthy", text); // choices listed
            Assert.DoesNotContain("- photo |", text); // media field excluded from the target list

            // Strict tool-use contract.
            Assert.Equal("provide_fields", root.GetProperty("tools")[0].GetProperty("name").GetString());
            Assert.Equal("tool", root.GetProperty("tool_choice").GetProperty("type").GetString());
            Assert.Equal("provide_fields", root.GetProperty("tool_choice").GetProperty("name").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Unknown_field_ids_from_model_are_dropped()
    {
        var ok = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                ToolUseResponse(("species", "Oak", 0.9), ("ghost", "x", 0.99)), Encoding.UTF8, "application/json"),
        };
        var (provider, _) = Provider(ok);
        var path = WriteTempImage();
        try
        {
            var result = await provider.ExtractAsync(path, Form());
            Assert.Single(result.Fields);
            Assert.Equal("species", result.Fields[0].FieldId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Api_error_returns_failed_result_without_throwing()
    {
        var error = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(
                """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""",
                Encoding.UTF8, "application/json"),
        };
        var (provider, _) = Provider(error);
        var path = WriteTempImage();
        try
        {
            var result = await provider.ExtractAsync(path, Form());
            Assert.Empty(result.Fields);
            Assert.NotNull(result.Unmapped);
            Assert.Contains("429", result.Unmapped!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Malformed_body_returns_failed_result_without_throwing()
    {
        var bad = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json at all", Encoding.UTF8, "application/json"),
        };
        var (provider, _) = Provider(bad);
        var path = WriteTempImage();
        try
        {
            var result = await provider.ExtractAsync(path, Form());
            Assert.Empty(result.Fields);
            Assert.NotNull(result.Unmapped);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Missing_photo_returns_failed_result_without_calling_api()
    {
        var ok = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ToolUseResponse(("species", "Oak", 0.9)), Encoding.UTF8, "application/json"),
        };
        var (provider, handler) = Provider(ok);

        var result = await provider.ExtractAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jpg"), Form());

        Assert.Empty(result.Fields);
        Assert.NotNull(result.Unmapped);
        Assert.Null(handler.LastRequest); // never reached the network
    }

    [Fact]
    public async Task Extraction_is_pro_gated_when_applied()
    {
        // The provider extracts; AiCaptureService enforces the Pro entitlement on apply.
        var ok = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ToolUseResponse(("species", "Oak", 0.95)), Encoding.UTF8, "application/json"),
        };
        var (provider, _) = Provider(ok);
        var path = WriteTempImage();
        try
        {
            var result = await provider.ExtractAsync(path, Form());
            var session = FormSession.CreateForNewRecord(Form(), "r1");

            // Community is denied.
            var community = new AiCaptureService(CollectEntitlements.Community);
            Assert.Throws<FeatureNotEntitledException>(() => community.Apply(session, result));

            // Pro applies the extracted value.
            var pro = new AiCaptureService(new CollectEntitlements(CollectEdition.Pro));
            var outcome = pro.Apply(session, result);
            Assert.Equal(["species"], outcome.Applied);
            Assert.Equal("Oak", session.GetValue("species"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHandler(HttpResponseMessage response) => _response = response;

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return _response;
        }
    }
}
