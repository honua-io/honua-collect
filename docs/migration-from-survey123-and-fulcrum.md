# Migrating from Survey123 and Fulcrum

This guide lowers the switching cost from the incumbents by mapping their form and
data formats onto Honua Collect's importers and exporters. Everything described
here is implemented and unit-tested in `Honua.Collect.Core`; nothing requires an
Esri licence or a Fulcrum subscription.

> Scope: this covers the data and form layers that Collect implements today. The
> capture widgets that bind those forms to device sensors are tracked separately
> (see issues #1/#35); this guide is about getting your **definitions and records**
> in and out.

## Importing form definitions

### From Survey123 (XLSForm)

Survey123 surveys are authored as **XLSForm** — the same `survey` / `choices`
sheet model the OpenDataKit ecosystem uses. Collect imports it directly:

- `Honua.Collect.Core.Field.Forms.Authoring.XlsFormImporter.Import(formId, name, survey, choices)`
  turns the `survey` and `choices` rows into a `FormDefinition`.
- Field **types**, **labels**, **required** flags, **choice lists** (single/multi
  select), and **relevant** expressions in the common single-comparison form
  (`${field} = value`, `>`, `<`, …) are mapped to Collect's form model and its
  live visibility rules (`FormSession`).
- Repeating groups map to Collect's repeat groups (`RepeatGroup` /
  `RepeatInstance`).

Practical path: export your Survey123 form as XLSForm, read the two sheets into
`XlsFormSurveyRow` / `XlsFormChoiceRow` sequences (any spreadsheet reader), and
call `Import`. Calculated fields and cascading selects are evaluated at runtime by
`FormSession`, so they behave the same as in Survey123 once imported.

### From Fulcrum

Fulcrum apps are not XLSForm, but their field model (text, choice, photo,
signature, date, location, repeatable sections) maps cleanly onto the same
`FormDefinition`. Re-express the app's fields as `survey`/`choices` rows (a
one-time mechanical step) and import them with `XlsFormImporter`, or construct
`FormDefinition` directly. The conditional-visibility and required rules carry
over to `FormSession`.

## Importing existing records

Bring historical records in as `FieldRecord`s:

- **Fulcrum** exports records as **CSV** and **GeoJSON**. Map each row/feature to a
  `FieldRecord` (`RecordId`, `Values` keyed by field id, `Location` from the
  geometry). Field ids should match the imported form's field ids so values land
  in the right place.
- **Survey123 / ArcGIS** feature layers are read over the GeoServices protocol
  Collect already speaks (`FeaturePullService`, `GeoServicesFeatureSync`) — the
  same wire format used to sync, so an existing FeatureServer layer can be pulled
  in without an intermediate file.

## Exporting back out

Collect exports the captured record set to every common interchange format, so you
are never locked in (`Honua.Collect.Core.Export`):

| Format | API | Notes |
| --- | --- | --- |
| CSV | `RecordExporter.ToCsv` | one row per record, form-driven columns |
| GeoJSON | `RecordExporter.ToGeoJson` | RFC 7946 `FeatureCollection` |
| KML | `RecordExporter.ToKml` | OGC KML 2.2, opens in Google Earth |
| GeoPackage | `GeoPackageExporter.Export` | single-file OGC SQLite, reads in QGIS/ArcGIS |
| Shapefile | `ShapefileExporter.Export` | zipped `.shp`/`.shx`/`.dbf` |

Bulk export and the per-record templated report (`RecordReportRenderer`) are Pro
capabilities, gated by the signed-licence entitlement layer
(`CollectFeature.ReportsAndExports`).

## Round-trip to an ArcGIS or self-hosted FeatureServer

Collect reads and writes the **Esri GeoServices** protocol (FeatureServer
`query` / `applyEdits`, `generateToken`) directly, so it can sync against:

- an existing **ArcGIS** FeatureServer (no Survey123/Field Maps app required), or
- a **self-hosted** GeoServices-compatible server, with **no Esri licence**.

Point Collect at the target by configuring `ServerBaseUrl` / `ServiceId` /
`LayerId` in `AppSettings`; sync uses `GeoServicesFeatureSync`. See
[data-residency-and-self-host.md](./data-residency-and-self-host.md) for where
records live and how the connection is secured.
