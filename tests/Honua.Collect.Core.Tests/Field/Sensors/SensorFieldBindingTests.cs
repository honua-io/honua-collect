using Honua.Collect.Core.Field.Forms;
using Honua.Collect.Core.Field.Sensors;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Tests.Field.Sensors;

public class SensorFieldBindingTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);

    private static FormDefinition Form() => new()
    {
        FormId = "inspection",
        Name = "Inspection",
        Sections =
        [
            new FormSection
            {
                SectionId = "readings",
                Label = "Readings",
                Fields =
                [
                    new FormField { FieldId = "tempC", Label = "Temp (C)", Type = FormFieldType.Numeric },
                    new FormField { FieldId = "tempText", Label = "Temp text", Type = FormFieldType.Text },
                    new FormField { FieldId = "overTemp", Label = "Over temp?", Type = FormFieldType.YesNo },
                    new FormField { FieldId = "photo", Label = "Photo", Type = FormFieldType.Photo },
                ],
            },
        ],
    };

    private static ISensorSource SourceWith(params SensorReading[] readings)
    {
        var transport = new ReplaySensorTransport(readings);
        var source = new SensorSource("probe", SensorType.Environmental, transport, windowCapacity: 16);
        transport.Connect();
        transport.EmitAll();
        return source;
    }

    private static SensorReading Temp(double v, string unit, int secondsAgo, SensorQuality q = SensorQuality.Good)
        => new("probe", "temperature", v, unit, Now.AddSeconds(-secondsAgo), q);

    [Fact]
    public void Binding_writes_aggregated_value_into_numeric_field()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var source = SourceWith(Temp(20, "degC", 2), Temp(24, "degC", 0));
        var binding = new SensorFieldBinding("temperature", "tempC", SensorAggregation.Average);

        var result = binding.Apply(source, session, Now);

        Assert.True(result.Applied);
        Assert.Equal(22.0, session.GetValue("tempC"));
    }

    [Fact]
    public void Binding_converts_units_to_the_field_unit()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        // Probe reports Fahrenheit; field stores Celsius.
        var source = SourceWith(Temp(212, "degF", 0));
        var binding = new SensorFieldBinding("temperature", "tempC", SensorAggregation.Latest, fieldUnit: "degC");

        var result = binding.Apply(source, session, Now);

        Assert.True(result.Applied);
        Assert.Equal(100.0, (double)session.GetValue("tempC")!, 6);
    }

    [Fact]
    public void Binding_rejects_incompatible_units()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var source = SourceWith(Temp(5, "m", 0)); // length into a temperature field
        var binding = new SensorFieldBinding("temperature", "tempC", SensorAggregation.Latest, fieldUnit: "degC");

        var result = binding.Apply(source, session, Now);

        Assert.False(result.Applied);
        Assert.Equal(SensorBindingRejection.IncompatibleUnit, result.Rejection);
        Assert.Null(session.GetValue("tempC"));
    }

    [Fact]
    public void Binding_rejects_stale_readings()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var source = SourceWith(Temp(20, "degC", secondsAgo: 120)); // two minutes old
        var binding = new SensorFieldBinding(
            "temperature", "tempC", SensorAggregation.Latest, maxStaleness: TimeSpan.FromSeconds(30));

        var result = binding.Apply(source, session, Now);

        Assert.False(result.Applied);
        Assert.Equal(SensorBindingRejection.Stale, result.Rejection);
        Assert.Null(session.GetValue("tempC"));
    }

    [Fact]
    public void Binding_coerces_to_text_and_yesno_field_types()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var source = SourceWith(Temp(37.5, "degC", 0));

        Assert.True(new SensorFieldBinding("temperature", "tempText").Apply(source, session, Now).Applied);
        Assert.Equal("37.5", session.GetValue("tempText"));

        Assert.True(new SensorFieldBinding("temperature", "overTemp").Apply(source, session, Now).Applied);
        Assert.Equal(true, session.GetValue("overTemp"));
    }

    [Fact]
    public void Binding_rejects_uncoercible_field_type()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var source = SourceWith(Temp(20, "degC", 0));
        var binding = new SensorFieldBinding("temperature", "photo");

        var result = binding.Apply(source, session, Now);

        Assert.False(result.Applied);
        Assert.Equal(SensorBindingRejection.TypeCoercionFailed, result.Rejection);
    }

    [Fact]
    public void Binding_with_no_readings_is_rejected()
    {
        var session = FormSession.CreateForNewRecord(Form(), "r1");
        var source = SourceWith(); // nothing on the channel
        var binding = new SensorFieldBinding("temperature", "tempC");

        var result = binding.Apply(source, session, Now);

        Assert.False(result.Applied);
        Assert.Equal(SensorBindingRejection.NoReadings, result.Rejection);
    }
}
