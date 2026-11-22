// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Forms;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Tests.Forms;

[Collection("e2e")]
public class FormPackageE2ETests
{
    private static readonly string? ServerUrl = Environment.GetEnvironmentVariable("HONUA_E2E_SERVER");
    private static readonly string? ApiKey = Environment.GetEnvironmentVariable("HONUA_E2E_APIKEY");
    private const string ServiceId = "mobile_offline_demo";
    private const int LayerId = 68910;

    private static bool Enabled => !string.IsNullOrWhiteSpace(ServerUrl);

    private static HttpClient Authed()
    {
        var http = new HttpClient { BaseAddress = new Uri(ServerUrl!) };
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            http.DefaultRequestHeaders.Add("X-API-Key", ApiKey);
        }

        return http;
    }

    private static object BuildDraftPackage(string formId) => new
    {
        schemaVersion = "honua.form-package.v1",
        formId,
        title = "Live Form Package E2E",
        target = new { serviceId = ServiceId, layerId = LayerId },
        sections = new[]
        {
            new { sectionId = "main", label = "Main", repeatable = false, fieldIds = new[] { "site_name", "status" } },
        },
        fields = new object[]
        {
            new { fieldId = "site_name", label = "Site name", type = "text", targetField = "site_name", sectionId = "main", required = true },
            new
            {
                fieldId = "status",
                label = "Status",
                type = "select_one",
                targetField = "status",
                sectionId = "main",
                required = true,
                domain = new
                {
                    type = "codedValue",
                    choices = new[]
                    {
                        new { code = "new", label = "New" },
                        new { code = "done", label = "Done" },
                    },
                },
            },
        },
    };

    [SkippableFact]
    public async Task DownloadAsync_fetches_published_form_package_from_live_server()
    {
        Skip.IfNot(Enabled, "HONUA_E2E_SERVER is not set; bring a server up with scripts/e2e/up.sh to run E2E tests.");

        using var http = Authed();
        var formId = "collect-e2e-" + Guid.NewGuid().ToString("n")[..10];

        using var createResponse = await http.PostAsJsonAsync("/api/v1/admin/forms/packages", BuildDraftPackage(formId));
        createResponse.EnsureSuccessStatusCode();

        using var publishResponse = await http.PostAsync($"/api/v1/admin/forms/packages/{formId}/versions/1/publish", content: null);
        publishResponse.EnsureSuccessStatusCode();

        var form = await new FormPackageClient(http).DownloadAsync(formId);

        Assert.Equal(formId, form.FormId);
        Assert.Equal("Live Form Package E2E", form.Name);
        Assert.Equal("1", form.Version);
        var section = Assert.Single(form.Sections);
        Assert.Equal(["site_name", "status"], section.Fields.Select(f => f.FieldId));
        Assert.Equal(FormFieldType.SingleChoice, section.Fields[1].Type);
        Assert.Equal(["new", "done"], section.Fields[1].Choices.Select(c => c.Value));

        var session = FormSession.CreateForNewRecord(form, "r1");
        Assert.False(session.CanSubmit);
        session.SetValue("site_name", "Hydrant 42");
        session.SetValue("status", "new");
        Assert.True(session.CanSubmit);
    }
}
