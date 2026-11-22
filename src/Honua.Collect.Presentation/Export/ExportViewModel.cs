// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Collect.Core.Export;
using Honua.Collect.Presentation.Mvvm;
using Honua.Sdk.Field.Forms;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Presentation.Export;

public sealed class ExportViewModel : ObservableObject
{
    private readonly FormDefinition _form;
    private readonly IReadOnlyList<FieldRecord> _records;

    public ExportViewModel(FormDefinition form, IEnumerable<FieldRecord> records)
    {
        _form = form ?? throw new ArgumentNullException(nameof(form));
        ArgumentNullException.ThrowIfNull(records);
        _records = records.ToList();
    }

    public int RecordCount => _records.Count;

    public bool HasRecords => _records.Count > 0;

    public string Header => $"{RecordCount} record(s) ready to export.";

    public string FileBaseName => string.IsNullOrWhiteSpace(_form.FormId) ? "records" : _form.FormId;

    public string ExportCsv() => RecordExporter.Export(_form, _records, ExportFormat.Csv);

    public string ExportGeoJson() => RecordExporter.Export(_form, _records, ExportFormat.GeoJson);

    public FieldRecord? LatestRecord =>
        _records.Count == 0 ? null : _records.MaxBy(r => r.CreatedAtUtc);
}
