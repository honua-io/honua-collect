using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Field.Forms.Cascade;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Tests.Field.Forms;

public class CascadingChoiceTests
{
    // country -> region -> city, modelled with FieldChoice.ParentValue and
    // Collect-side ChoiceCascadeRule linkage (BACKLOG F3).
    private static FormDefinition GeoForm() => new()
    {
        FormId = "geo",
        Name = "Geo",
        Sections =
        [
            new FormSection
            {
                SectionId = "s",
                Label = "s",
                Fields =
                [
                    new FormField
                    {
                        FieldId = "country",
                        Label = "Country",
                        Type = FormFieldType.SingleChoice,
                        Choices =
                        [
                            new FieldChoice { Value = "us", Label = "United States" },
                            new FieldChoice { Value = "ca", Label = "Canada" },
                        ],
                    },
                    new FormField
                    {
                        FieldId = "region",
                        Label = "Region",
                        Type = FormFieldType.SingleChoice,
                        Choices =
                        [
                            new FieldChoice { Value = "wa", Label = "Washington", ParentValue = "us" },
                            new FieldChoice { Value = "or", Label = "Oregon", ParentValue = "us" },
                            new FieldChoice { Value = "bc", Label = "British Columbia", ParentValue = "ca" },
                        ],
                    },
                    new FormField
                    {
                        FieldId = "city",
                        Label = "City",
                        Type = FormFieldType.SingleChoice,
                        Choices =
                        [
                            new FieldChoice { Value = "sea", Label = "Seattle", ParentValue = "wa" },
                            new FieldChoice { Value = "pdx", Label = "Portland", ParentValue = "or" },
                            new FieldChoice { Value = "yvr", Label = "Vancouver", ParentValue = "bc" },
                        ],
                    },
                ],
            },
        ],
    };

    private static IEnumerable<ChoiceCascadeRule> GeoCascades() =>
    [
        new ChoiceCascadeRule("region", "country"),
        new ChoiceCascadeRule("city", "region"),
    ];

    [Fact]
    public void Child_choices_are_filtered_by_parent_value()
    {
        var session = FormSession.CreateForNewRecord(GeoForm(), "r1", cascadeRules: GeoCascades());

        // With no country chosen, the dependent region select offers nothing.
        Assert.Empty(session.GetField("region").AvailableChoices);

        session.SetValue("country", "us");

        var regions = session.GetField("region").AvailableChoices;
        Assert.Equal(["wa", "or"], regions.Select(c => c.Value));
    }

    [Fact]
    public void Cascade_filters_through_multiple_levels()
    {
        var session = FormSession.CreateForNewRecord(GeoForm(), "r1", cascadeRules: GeoCascades());

        session.SetValue("country", "us");
        session.SetValue("region", "wa");

        Assert.Equal(["sea"], session.GetField("city").AvailableChoices.Select(c => c.Value));

        // Re-target the middle level: city options follow region, not country.
        session.SetValue("region", "or");
        Assert.Equal(["pdx"], session.GetField("city").AvailableChoices.Select(c => c.Value));
    }

    [Fact]
    public void Changing_parent_clears_a_now_invalid_child_value()
    {
        var session = FormSession.CreateForNewRecord(GeoForm(), "r1", cascadeRules: GeoCascades());

        session.SetValue("country", "us");
        session.SetValue("region", "wa");
        Assert.Equal("wa", session.GetValue("region"));

        // Switching country to Canada makes "wa" invalid for region -> cleared.
        session.SetValue("country", "ca");

        Assert.Null(session.GetValue("region"));
        Assert.Equal(["bc"], session.GetField("region").AvailableChoices.Select(c => c.Value));
    }

    [Fact]
    public void Clearing_a_parent_cascades_clearing_down_the_chain()
    {
        var session = FormSession.CreateForNewRecord(GeoForm(), "r1", cascadeRules: GeoCascades());

        session.SetValue("country", "us");
        session.SetValue("region", "wa");
        session.SetValue("city", "sea");
        Assert.Equal("sea", session.GetValue("city"));

        // Clearing the top of the chain invalidates region (empty options) which in
        // turn invalidates city.
        session.SetValue("country", null);

        Assert.Null(session.GetValue("region"));
        Assert.Null(session.GetValue("city"));
        Assert.Empty(session.GetField("city").AvailableChoices);
    }

    [Fact]
    public void Plain_choice_field_without_a_cascade_keeps_all_options()
    {
        var session = FormSession.CreateForNewRecord(GeoForm(), "r1");

        // No cascade rules supplied: every choice field offers its full list.
        Assert.Equal(["wa", "or", "bc"], session.GetField("region").AvailableChoices.Select(c => c.Value));
    }

    [Fact]
    public void Still_valid_child_value_is_retained_when_parent_unchanged()
    {
        var session = FormSession.CreateForNewRecord(GeoForm(), "r1", cascadeRules: GeoCascades());
        session.SetValue("country", "us");
        session.SetValue("region", "or");

        // An unrelated edit must not disturb a still-valid child selection.
        session.SetValue("country", "us");
        Assert.Equal("or", session.GetValue("region"));
    }
}
