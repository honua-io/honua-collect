// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Honua.Collect.Core.Field.Forms;
using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Field.Sensors;

public enum SensorBindingRejection
{
    NoReadings = 0,

    Stale = 1,

    IncompatibleUnit = 2,

    TypeCoercionFailed = 3,
}

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

public sealed class SensorFieldBinding
{
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

    public string Channel { get; }

    public string FieldId { get; }

    public SensorAggregation Aggregation { get; }

    public TimeSpan MaxStaleness { get; }

    public string? FieldUnit { get; }

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
