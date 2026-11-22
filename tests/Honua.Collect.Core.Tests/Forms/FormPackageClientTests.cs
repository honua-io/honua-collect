// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Forms;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Tests.Forms;

public class FormPackageClientTests
{
    private const string SampleEnvelope = """
        {
          "formId": "inspection",
          "version": 3,
          "status": "published",
          "package": {
            "schemaVersion": "honua.form-package.v1",
            "formId": "inspection",
            "title": "Site Inspection",
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
        }
        """;

    [Fact]
    public async Task DownloadAsync_requests_published_form_package_url()
    {
        var handler = new StubHandler(SampleEnvelope);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://server.example") };

        await new FormPackageClient(client).DownloadAsync("inspection");

        Assert.NotNull(handler.LastRequestUri);
        Assert.Equal(
            "https://server.example/api/v1/forms/packages/inspection",
            handler.LastRequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
    }

    [Fact]
    public async Task DownloadAsync_requests_specific_published_version_url()
    {
        var handler = new StubHandler(SampleEnvelope);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://server.example") };

        await new FormPackageClient(client).DownloadAsync("inspection", packageVersion: 3);

        Assert.Equal(
            "https://server.example/api/v1/forms/packages/inspection/versions/3",
            handler.LastRequestUri!.ToString());
    }

    [Fact]
    public async Task DownloadAsync_maps_envelope_to_form_definition()
    {
        var handler = new StubHandler(SampleEnvelope);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://server.example") };

        var form = await new FormPackageClient(client).DownloadAsync("inspection");

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

        Assert.Equal(["new", "done"], fields["status"].Choices.Select(c => c.Value));
        Assert.Equal(["New", "Done"], fields["status"].Choices.Select(c => c.Label));
        Assert.Equal(["name", "status", "pic"], main.Fields.Select(f => f.FieldId));
    }

    [Fact]
    public async Task DownloadAsync_result_drives_a_submittable_form_session()
    {
        var handler = new StubHandler(SampleEnvelope);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://server.example") };

        var form = await new FormPackageClient(client).DownloadAsync("inspection");

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
            () => new FormPackageClient(client).DownloadAsync("inspection"));
    }

    [Fact]
    public async Task DownloadAsync_throws_on_non_success_status()
    {
        var handler = new StubHandler("server exploded", HttpStatusCode.InternalServerError);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://server.example") };

        await Assert.ThrowsAsync<HttpRequestException>(
            () => new FormPackageClient(client).DownloadAsync("inspection"));
    }

    [Fact]
    public async Task DownloadAsync_surfaces_error_envelope_returned_with_http_200()
    {
        const string errorBody = """{"error":{"code":403,"message":"Token required.","details":[]}}""";
        var handler = new StubHandler(errorBody);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://server.example") };

        var ex = await Assert.ThrowsAsync<FormPackageException>(
            () => new FormPackageClient(client).DownloadAsync("inspection"));

        Assert.Equal(403, ex.ErrorCode);
        Assert.Contains("Token required", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public async Task DownloadAsync_validates_form_id(string? bad)
    {
        var handler = new StubHandler(SampleEnvelope);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://server.example") };
        var packageClient = new FormPackageClient(client);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => packageClient.DownloadAsync(bad!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task DownloadAsync_validates_package_version(int badVersion)
    {
        var handler = new StubHandler(SampleEnvelope);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://server.example") };
        var packageClient = new FormPackageClient(client);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => packageClient.DownloadAsync("inspection", badVersion));
    }

    [Fact]
    public async Task Map_unknown_field_type_falls_back_to_text()
    {
        const string json = """
            { "formId": "f", "sections": [ { "sectionId": "s", "label": "S", "fieldIds": ["x"] } ],
              "fields": [ { "fieldId": "x", "label": "X", "type": "totally-unknown", "sectionId": "s" } ] }
            """;

        var form = await MapAsync(json);
        var field = form.Sections.Single().Fields.Single();
        Assert.Equal(FormFieldType.Text, field.Type);
    }

    [Theory]
    [InlineData("integer", FormFieldType.Numeric)]
    [InlineData("number", FormFieldType.Numeric)]
    [InlineData("date", FormFieldType.Date)]
    [InlineData("time", FormFieldType.Time)]
    [InlineData("datetime", FormFieldType.DateTime)]
    [InlineData("yesno", FormFieldType.YesNo)]
    [InlineData("boolean", FormFieldType.YesNo)]
    [InlineData("select_multiple", FormFieldType.MultipleChoice)]
    [InlineData("video", FormFieldType.Video)]
    [InlineData("audio", FormFieldType.Audio)]
    [InlineData("signature", FormFieldType.Signature)]
    [InlineData("barcode", FormFieldType.Barcode)]
    [InlineData("geopoint", FormFieldType.Location)]
    [InlineData("location", FormFieldType.Location)]
    [InlineData("calculate", FormFieldType.Calculated)]
    [InlineData("file", FormFieldType.File)]
    [InlineData("  TEXT  ", FormFieldType.Text)]
    public async Task Map_maps_each_known_type_token(string token, FormFieldType expected)
    {
        var json = $$"""
            { "formId": "f", "sections": [ { "sectionId": "s", "label": "S", "fieldIds": ["x"] } ],
              "fields": [ { "fieldId": "x", "label": "X", "type": "{{token}}", "sectionId": "s" } ] }
            """;

        var form = await MapAsync(json);
        Assert.Equal(expected, form.Sections.Single().Fields.Single().Type);
    }

    [Fact]
    public async Task Map_section_without_matching_fields_is_emitted_empty()
    {
        const string json = """
            { "formId": "f", "sections": [ { "sectionId": "lonely", "label": "Lonely", "fieldIds": [] } ],
              "fields": [] }
            """;

        var form = await MapAsync(json);
        var section = Assert.Single(form.Sections);
        Assert.Equal("lonely", section.SectionId);
        Assert.Empty(section.Fields);
    }

    [Fact]
    public async Task Map_skips_sections_without_a_section_id()
    {
        const string json = """
            { "formId": "f",
              "sections": [ { "label": "No id" }, { "sectionId": "good", "label": "Good", "fieldIds": [] } ],
              "fields": [] }
            """;

        var form = await MapAsync(json);
        var section = Assert.Single(form.Sections);
        Assert.Equal("good", section.SectionId);
    }

    [Fact]
    public async Task Map_skips_fields_without_a_field_id_and_handles_typed_choice_codes()
    {
        const string json = """
            { "formId": "f",
              "sections": [ { "sectionId": "s", "label": "S", "fieldIds": ["keep"] } ],
              "fields": [
                { "label": "no field id", "type": "text", "sectionId": "s" },
                { "fieldId": "keep", "type": "select_one", "sectionId": "s",
                  "domain": { "choices": [
                    { "label": "blank code" },
                    { "code": "a", "label": "A" },
                    { "code": 2, "label": "Two" },
                    { "code": true, "label": "Yes" }
                  ] } }
              ] }
            """;

        var form = await MapAsync(json);
        var field = Assert.Single(form.Sections.Single().Fields);
        Assert.Equal("keep", field.FieldId);
        Assert.Equal(["a", "2", "true"], field.Choices.Select(c => c.Value));
    }

    [Fact]
    public async Task Map_defaults_title_and_section_label_and_version_when_absent()
    {
        const string json = """
            { "formId": "form-7", "version": "  ",
              "sections": [ { "sectionId": "sec", "fieldIds": [] } ],
              "fields": [] }
            """;

        var form = await MapAsync(json);
        Assert.Equal("form-7", form.Name);
        Assert.Null(form.Version);
        Assert.Equal("sec", form.Sections.Single().Label);
    }

    [Fact]
    public async Task Map_handles_null_sections_and_fields_collections()
    {
        const string json = """{ "formId": "f", "title": "T" }""";

        var form = await MapAsync(json);
        Assert.Equal("f", form.FormId);
        Assert.Empty(form.Sections);
    }

    [Fact]
    public async Task Map_honors_section_fieldIds_for_membership_and_order()
    {
        const string package = """
            {
              "formId": "f",
              "sections": [ { "sectionId": "s", "label": "S", "fieldIds": ["b", "a"] } ],
              "fields": [
                { "fieldId": "a", "label": "A", "type": "text", "sectionId": "s" },
                { "fieldId": "b", "label": "B", "type": "text", "sectionId": "other" },
                { "fieldId": "c", "label": "C", "type": "text", "sectionId": "s" }
              ]
            }
            """;
        using var client = new HttpClient(new StubHandler(package)) { BaseAddress = new Uri("https://s.example") };

        var form = await new FormPackageClient(client).DownloadAsync("f");

        var section = Assert.Single(form.Sections);
        Assert.Equal(["b", "a"], section.Fields.Select(f => f.FieldId));
    }

    private static async Task<FormDefinition> MapAsync(string json)
    {
        var handler = new StubHandler(json);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://server.example") };
        return await new FormPackageClient(client).DownloadAsync("form");
    }

    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        public HttpMethod? LastMethod { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastMethod = request.Method;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
