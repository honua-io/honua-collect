using System.Net;
using System.Text;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Forms;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Tests.Forms;

public class FormPackageClientTests
{
    private const string SamplePackage = """
        {
          "formId": "inspection",
          "title": "Site Inspection",
          "version": "3",
          "sections": [
            { "sectionId": "main", "label": "Main", "repeatable": false,
              "minInstances": null, "maxInstances": null,
              "fieldIds": ["name", "status", "pic"] },
            { "sectionId": "samples", "label": "Samples", "repeatable": true,
              "minInstances": 0, "maxInstances": null,
              "fieldIds": ["depth"] }
          ],
          "fields": [
            { "fieldId": "name", "label": "Name", "type": "text",
              "required": true, "sectionId": "main" },
            { "fieldId": "status", "label": "Status", "type": "select_one",
              "required": true, "sectionId": "main",
              "domain": { "choices": [
                { "code": "new", "label": "New" },
                { "code": "done", "label": "Done" } ] } },
            { "fieldId": "pic", "label": "Photo", "type": "photo",
              "required": false, "sectionId": "main" },
            { "fieldId": "depth", "label": "Depth", "type": "decimal",
              "required": false, "sectionId": "samples" }
          ]
        }
        """;

    [Fact]
    public async Task DownloadAsync_requests_expected_url()
    {
        var handler = new StubHandler(SamplePackage);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://server.example") };

        await new FormPackageClient(client).DownloadAsync("svc1", "inspection");

        Assert.NotNull(handler.LastRequestUri);
        Assert.Equal(
            "https://server.example/rest/services/svc1/FormServer/inspection?f=json",
            handler.LastRequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
    }

    [Fact]
    public async Task DownloadAsync_maps_package_to_form_definition()
    {
        var handler = new StubHandler(SamplePackage);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://server.example") };

        var form = await new FormPackageClient(client).DownloadAsync("svc1", "inspection");

        Assert.Equal("inspection", form.FormId);
        Assert.Equal("Site Inspection", form.Name);
        Assert.Equal("3", form.Version);

        Assert.Equal(2, form.Sections.Count);
        var main = form.Sections[0];
        Assert.Equal("main", main.SectionId);
        Assert.False(main.Repeatable);

        var samples = form.Sections[1];
        Assert.Equal("samples", samples.SectionId);
        Assert.True(samples.Repeatable);

        var fields = form.Sections.SelectMany(s => s.Fields).ToDictionary(f => f.FieldId);
        Assert.Equal(FormFieldType.Text, fields["name"].Type);
        Assert.True(fields["name"].Required);
        Assert.Equal(FormFieldType.SingleChoice, fields["status"].Type);
        Assert.True(fields["status"].Required);
        Assert.Equal(FormFieldType.Photo, fields["pic"].Type);
        Assert.False(fields["pic"].Required);
        Assert.Equal(FormFieldType.Numeric, fields["depth"].Type);

        // select_one choices map code -> Value, label -> Label, in order.
        Assert.Equal(["new", "done"], fields["status"].Choices.Select(c => c.Value));
        Assert.Equal(["New", "Done"], fields["status"].Choices.Select(c => c.Label));

        // Field order within a section is preserved.
        Assert.Equal(["name", "status", "pic"], main.Fields.Select(f => f.FieldId));
    }

    [Fact]
    public async Task DownloadAsync_result_drives_a_submittable_form_session()
    {
        var handler = new StubHandler(SamplePackage);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://server.example") };

        var form = await new FormPackageClient(client).DownloadAsync("svc1", "inspection");

        var session = FormSession.CreateForNewRecord(form, "r1");
        Assert.False(session.CanSubmit);

        session.SetValue("name", "Bridge 7");
        session.SetValue("status", "new");

        Assert.True(session.CanSubmit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("{ \"title\": \"No form id\" }")]
    public async Task DownloadAsync_throws_on_empty_or_invalid_body(string body)
    {
        var handler = new StubHandler(body);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://server.example") };

        await Assert.ThrowsAsync<FormPackageException>(
            () => new FormPackageClient(client).DownloadAsync("svc1", "inspection"));
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        public HttpMethod? LastMethod { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastMethod = request.Method;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
