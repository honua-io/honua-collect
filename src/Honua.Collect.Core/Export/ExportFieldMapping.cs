// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Forms;

namespace Honua.Collect.Core.Export;

public sealed record ExportColumn(string Source, string? Header = null)
{
    public string ResolvedHeader => string.IsNullOrEmpty(Header) ? Source : Header;
}

public sealed record ExportFieldMapping(IReadOnlyList<ExportColumn> Columns)
{
    public static IReadOnlyList<string> FixedSources { get; } =
        ["record_id", "status", "latitude", "longitude"];

    public static ExportFieldMapping Of(params (string Source, string Header)[] columns)
        => new(columns.Select(c => new ExportColumn(c.Source, c.Header)).ToList());
}
