using System.Globalization;
using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Field.Sensors;

/// <summary>Why a sensor-to-field write was rejected.</summary>
public enum SensorBindingRejection
{
    /// <summary>The channel had no usable readings to aggregate.</summary>
    NoReadings = 0,

    /// <summary>The aggregated reading was older than the staleness threshold.</summary>
    Stale = 1,

    /// <summary>The reading's unit could not be converted to the field's unit.</summary>
    IncompatibleUnit = 2,

    /// <summary>The (converted) value could not be coerced to the field's type.</summary>
    TypeCoercionFailed = 3,
}

/// <summary>Outcome of applying a <see cref="SensorFieldBinding"/>.</summary>
/// <param name="Applied">Whether the value was written to the field.</param>
/// <param name="Value">The value written, when <see cref="Applied"/> is true.</param>
/// <param name="Rejection">Why the write was rejected, when not applied.</param>
/// <param name="Aggregate">The aggregate that was evaluated, when one existed.</param>
public readonly record struct SensorBindingResult(
    bool Applied,
    object? Value,
    SensorBindingRejection? Rejection,
    SensorAggregateResult? Aggregate)
{
    internal static SensorBindingResult Reject(SensorBindingRejection reason, SensorAggregateResult? aggregate = null)
        => new(false, null, reason, aggregate);

    internal static SensorBindingResult Accept(object? value, SensorAggregateResult aggregate)
        => new(true, value, null, aggregate);
}

/// <summary>
/// Binds a sensor channel to a form field (BACKLOG I3): it aggregates the
/// channel's rolling window, rejects readings older than a staleness threshold,
/// converts the reading's unit to the field's unit, coerces the result to the
/// field's value type, and writes it into the <see cref="FormSession"/>. This is
/// the seam where IoT telemetry actually becomes record data.
/// </summary>
public sealed class SensorFieldBinding
{
    /// <summary>Creates a binding.</summary>
    /// <param name="channel">Sensor channel to read from.</param>
    /// <param name="fieldId">Target form field id.</param>
    /// <param name="aggregation">How to collapse the channel window.</param>
    /// <param name="maxStaleness">
    /// Reject readings whose newest contributing sample is older than this. Default
    /// 30 s. Use <see cref="TimeSpan.MaxValue"/> to disable the staleness guard.
    /// </param>
    /// <param name="fieldUnit">
    /// Unit the field stores values in (for unit conversion). <see langword="null"/>
    /// keeps the reading's own unit.
    /// </param>
    public SensorFieldBinding(
        string channel,
        string fieldId,
        SensorAggregation aggregation = SensorAggregation.Latest,
        TimeSpan? maxStaleness = null,
        string? fieldUnit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldId);

        Channel = channel;
        FieldId = fieldId;
        Aggregation = aggregation;
        MaxStaleness = maxStaleness ?? TimeSpan.FromSeconds(30);
        FieldUnit = fieldUnit;
    }

    /// <summary>The sensor channel this binding reads from.</summary>
    public string Channel { get; }

    /// <summary>The target field id.</summary>
    public string FieldId { get; }

    /// <summary>How the channel window is aggregated.</summary>
    public SensorAggregation Aggregation { get; }

    /// <summary>Maximum acceptable age of the aggregated reading.</summary>
    public TimeSpan MaxStaleness { get; }

    /// <summary>Unit the field stores values in, or <see langword="null"/>.</summary>
    public string? FieldUnit { get; }

    /// <summary>
    /// Evaluates the binding against a source and (when accepted) writes the value
    /// into the session's field. Pure aggregation + guards; only a successful,
    /// non-stale, unit-compatible, coercible value mutates the session.
    /// </summary>
    /// <param name="source">The sensor source to read the channel from.</param>
    /// <param name="session">The form session to write into.</param>
    /// <param name="nowUtc">Reference instant for the staleness check (defaults to <see cref="DateTimeOffset.UtcNow"/>).</param>
    /// <returns>The binding outcome.</returns>
    public SensorBindingResult Apply(ISensorSource source, FormSession session, DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(session);

        var result = Evaluate(source.Window(Channel), session.GetField(FieldId).Field, nowUtc);
        if (result.Applied)
        {
            session.SetValue(FieldId, result.Value);
        }

        return result;
    }

    /// <summary>
    /// Evaluates the binding without mutating anything — aggregates the window,
    /// applies the staleness guard, converts units, and coerces to the field type.
    /// Exposed so callers (and tests) can preview the outcome.
    /// </summary>
    /// <param name="window">The channel's rolling window (oldest to newest).</param>
    /// <param name="field">The target field definition.</param>
    /// <param name="nowUtc">Reference instant for the staleness check.</param>
    /// <returns>The binding outcome.</returns>
    public SensorBindingResult Evaluate(
        IReadOnlyList<SensorReading> window,
        FormField field,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(field);

        var aggregate = SensorAggregator.Aggregate(window, Aggregation);
        if (aggregate is not { } agg)
        {
            return SensorBindingResult.Reject(SensorBindingRejection.NoReadings);
        }

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        if (MaxStaleness != TimeSpan.MaxValue && now - agg.TimestampUtc > MaxStaleness)
        {
            return SensorBindingResult.Reject(SensorBindingRejection.Stale, agg);
        }

        var numeric = agg.Value;
        if (FieldUnit is not null)
        {
            if (!SensorUnits.TryConvert(numeric, agg.Unit, FieldUnit, out var converted))
            {
                return SensorBindingResult.Reject(SensorBindingRejection.IncompatibleUnit, agg);
            }

            numeric = converted;
        }

        if (!TryCoerce(numeric, agg, field.Type, out var coerced))
        {
            return SensorBindingResult.Reject(SensorBindingRejection.TypeCoercionFailed, agg);
        }

        return SensorBindingResult.Accept(coerced, agg);
    }

    private static bool TryCoerce(double value, SensorAggregateResult agg, FormFieldType type, out object? coerced)
    {
        switch (type)
        {
            case FormFieldType.Numeric:
            case FormFieldType.Calculated:
                coerced = value;
                return true;

            case FormFieldType.Text:
            case FormFieldType.Hyperlink:
            case FormFieldType.Barcode:
                coerced = value.ToString(CultureInfo.InvariantCulture);
                return true;

            case FormFieldType.YesNo:
                // Telemetry threshold: non-zero is "yes".
                coerced = Math.Abs(value) > double.Epsilon;
                return true;

            case FormFieldType.DateTime:
            case FormFieldType.Date:
                // Interpret the value as a Unix-epoch seconds timestamp.
                coerced = DateTimeOffset.FromUnixTimeMilliseconds((long)(value * 1000.0));
                return true;

            default:
                // Choice / media / geometry fields can't take a scalar telemetry value.
                coerced = null;
                _ = agg;
                return false;
        }
    }
}
