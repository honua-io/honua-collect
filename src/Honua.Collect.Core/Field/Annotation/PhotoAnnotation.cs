// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Field.Annotation;

public enum AnnotationShape
{
    Arrow,

    Rectangle,

    Ellipse,

    Freehand,

    Text,
}

public readonly record struct AnnotationPoint(double X, double Y);

public sealed record PhotoAnnotation
{
    public required AnnotationShape Shape { get; init; }

    public IReadOnlyList<AnnotationPoint> Points { get; init; } = [];

    public string Color { get; init; } = "#FF0000";

    public double StrokeWidth { get; init; } = 0.005;

    public string? Text { get; init; }
}
