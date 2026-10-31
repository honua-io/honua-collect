# Importing forms and field data

This guide describes how to import common form and field-data formats into Honua Collect. Everything described
here is implemented and unit-tested in `Honua.Collect.Core`; nothing requires an
vendor-specific license or subscription.

> Scope: this covers the data and form layers that Collect implements today. The
> capture widgets that bind those forms to device sensors are tracked separately
> (see issues #1/#35); this guide is about getting your **definitions and records**
> in and out.

## Importing form definitions

### Importing XLSForm

XLSForm uses the **XLSForm** format — the same `survey` / `choices`
sheet model the OpenDataKit ecosystem uses. Collect imports it directly:

- `Honua.Collect.Core.Field.Forms.Authoring.XlsFormImporter.Import(formId, name, survey, choices)`
  turns the `survey` and `choices` rows into a `FormDefinition`.
- Field **types**, **labels**, **required** flags, **choice lists** (single/multi
  select), and **relevant** expressions in the common single-comparison form
  (`${field} = value`, `>`, `<`, …) are mapped to Collect's form model and its
  live visibility rules (`FormSession`).
- Repeating groups map to Collect's repeat groups (`RepeatGroup` /
  `RepeatInstance`).

Practical path: export the form as XLSForm, read the two sheets into
`XlsFormSurveyRow` / `XlsFormChoiceRow` sequences (any spreadsheet reader), and
call `Import`. Calculated fields and cascading selects are evaluated at runtime by
`FormSession`, so they behave the as defined by the imported form.

#### Importing a published GeoServices REST feature layer

When the original XLSForm isn't on hand, import the **published feature-layer
schema** instead — the JSON a FeatureServer layer's metadata endpoint returns:

- `Honua.Collect.Core.Migration.Survey123LayerImporter.Import(layerSchemaJson, formId?)`
  maps the layer's `fields` (GeoServices REST field types, `alias` → label, `nullable:false`
  → required) into a `FormDefinition`.
- A field carrying a **coded-value `domain`** becomes a single-choice field whose
  choices are the coded values.
- The layer's **geometry type** (`esriGeometryPoint` / `…Polyline` / `…Polygon`)
  is surfaced as a leading `location` / geo-trace / geo-shape capture field.
- service **system / editor-tracking columns** (`objectid`, `globalid`,
  `created_*`, `last_edited_*`, `Shape_*`) and unsupported field types are dropped
  and reported in `MigratedForm.Skipped`, so the import is auditable.

### Importing app-schema exports

App-schema exports use a different format from XLSForm, but their field model (text, choice, photo,
signature, date, location, repeatable sections) maps cleanly onto the same
`FormDefinition`. Import the app-schema export directly:

- `Honua.Collect.Core.Migration.FulcrumImporter.ImportForm(appSchemaJson, formId?)`
  maps the app's `elements` (`TextField`, `ChoiceField`, `YesNoField`,
  `PhotoField`, …) into a `FormDefinition`, carrying `choices`, `required`, and a
  `location` field for the record position.
- Nested **Sections / Repeatables** are flattened so their contained capture
  fields survive; unknown element types are skipped and reported in
  `MigratedForm.Skipped`.

The conditional-visibility and required rules carry over to `FormSession`.

## Importing existing records

Bring historical records in as `FieldRecord`s:

- CSV and GeoJSON exports records as **CSV** and **GeoJSON**, imported directly against
  the form produced above:
  - `FulcrumImporter.ImportGeoJsonRecords(form, geoJson)` maps each feature's point
    `geometry` to the record `Location` and its `properties` to `Values` (filtered
    to the form's fields; system columns like `fulcrum_id`/`_status` are
    dropped).
  - `FulcrumImporter.ImportCsvRecords(form, csv)` does the same from the CSV export,
    reading the `latitude`/`longitude` columns into `Location`.
  - Both return a `MigratedForm` whose `Records` are ready to persist, with any
    malformed/geometry-less rows reported in `Skipped`.
- GeoServices REST feature layers are read over the GeoServices protocol
  Collect already speaks (`FeaturePullService`, `GeoServicesFeatureSync`) — the
  same wire format used to sync, so an existing FeatureServer layer can be pulled
  in without an intermediate file. The round trip (query → decode → applyEdits
  add/update/delete → attachment) is proven against a reference in-memory
  FeatureServer in `GeoServicesRoundTripTests`, so interop is verified without a
  live GeoServices REST server.

## Exporting back out

Collect exports the captured record set to every common interchange format, so you
are never locked in (`Honua.Collect.Core.Export`):

| Format | API | Notes |
| --- | --- | --- |
| CSV | `RecordExporter.ToCsv` | one row per record, form-driven columns |
| GeoJSON | `RecordExporter.ToGeoJson` | RFC 7946 `FeatureCollection` |
| KML | `RecordExporter.ToKml` | OGC KML 2.2, opens in Google Earth |
| GeoPackage | `GeoPackageExporter.Export` | single-file OGC SQLite, reads in QGIS/GeoServices REST |
| Shapefile | `ShapefileExporter.Export` | zipped `.shp`/`.shx`/`.dbf` |

Bulk export and the per-record templated report (`RecordReportRenderer`) are Pro
capabilities, gated by the signed-licence entitlement layer
(`CollectFeature.ReportsAndExports`).

## Round-trip to a GeoServices REST or self-hosted FeatureServer

Collect reads and writes the **GeoServices REST** protocol (FeatureServer
`query` / `applyEdits`, `generateToken`) directly, so it can sync against:

- an existing **GeoServices REST** FeatureServer (no additional mobile apps required), or
- a **self-hosted** GeoServices-compatible server, with **no vendor-specific license**.

Point Collect at the target by configuring `ServerBaseUrl` / `ServiceId` /
`LayerId` in `AppSettings`; sync uses `GeoServicesFeatureSync`. See
[data-residency-and-self-host.md](./data-residency-and-self-host.md) for where
records live and how the connection is secured.
