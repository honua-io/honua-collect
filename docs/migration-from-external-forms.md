# Importing forms and field data

Use Honua Server to publish a versioned XLSForm for field workers. For applications that
already have schema rows or exported records, Honua Field also provides in-process importers.

## Importing form definitions

### Publishing an XLSForm for offline inspections

1. Import the `.xlsx` workbook into Honua Server against the target service and layer. Enable offline collection on the draft package and publish that version. The [integration fixture](../scripts/e2e/publish-field-runtime-fixture.sh) shows the import and publish requests against a disposable server.
2. Configure Field's `server.formId` for the published form. While connected, tap **Download Latest Form**, then **Start New Inspection**. A worker with access to the target layer can now capture answers, repeat rows, a position and photos offline.
3. Tap **Save draft** to continue later from **Records**. The draft keeps the version the worker started with, including when a newer version is published. Submit the inspection and sync when connected. A worker can reopen a validation rejection, correct the answer offline and sync again.

Use a WGS 84 point layer for the form's single top-level geopoint. Place photo questions at the top level. A repeat section supports up to 100 ordered rows of scalar or multiple-choice answers and stores them in a writable text attribute matching the section ID. Relevance and calculated expressions can refer to earlier answers, and repeat rows can also use parent answers. When a question becomes hidden, Field keeps its previous answer in the draft and omits it from submission.

### Importing XLSForm sheet rows in an application

XLSForm definitions use `survey` and `choices` sheets, the same sheet model the
OpenDataKit ecosystem uses. Field imports them directly:

- `Honua.Field.Core.Field.Forms.Authoring.XlsFormImporter.Import(formId, name, survey, choices)`
  turns the `survey` and `choices` rows into a `FormDefinition`.
- Field **types**, **labels**, **required** flags, **choice lists** (single/multi
  select), and **relevant** expressions in the common single-comparison form
  (`${field} = value`, `>`, `<`, …) are mapped to Field's form model and its
  live visibility rules (`FormSession`).
- Repeating groups map to Field's repeat groups (`RepeatGroup` /
  `RepeatInstance`).

Practical path: export the form as XLSForm, read the two sheets into
`XlsFormSurveyRow` / `XlsFormChoiceRow` sequences (any spreadsheet reader), and
call `Import`. Calculated fields and cascading selects are evaluated at runtime by
`FormSession`, so they behave as defined by the imported form.

#### Importing a published GeoServices REST feature layer

When the original XLSForm isn't on hand, import the **published feature-layer
schema** instead — the JSON a FeatureServer layer's metadata endpoint returns:

- `Honua.Field.Core.Migration.Survey123LayerImporter.Import(layerSchemaJson, formId?)`
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

- `Honua.Field.Core.Migration.FulcrumImporter.ImportForm(appSchemaJson, formId?)`
  maps the app's `elements` (`TextField`, `ChoiceField`, `YesNoField`,
  `PhotoField`, …) into a `FormDefinition`, carrying `choices`, `required`, and a
  `location` field for the record position.
- Nested **Sections / Repeatables** are flattened so their contained capture
  fields survive; unknown element types are skipped and reported in
  `MigratedForm.Skipped`.

The conditional-visibility and required rules carry over to `FormSession`.

## Importing existing records

Bring historical records in as `FieldRecord`s:

- Import CSV and GeoJSON record exports against the form produced above:
  - `FulcrumImporter.ImportGeoJsonRecords(form, geoJson)` maps each feature's point
    `geometry` to the record `Location` and its `properties` to `Values` (filtered
    to the form's fields; system columns like `fulcrum_id`/`_status` are
    dropped).
  - `FulcrumImporter.ImportCsvRecords(form, csv)` does the same from the CSV export,
    reading the `latitude`/`longitude` columns into `Location`.
  - Both return a `MigratedForm` whose `Records` are ready to persist, with any
    malformed/geometry-less rows reported in `Skipped`.
- GeoServices REST feature layers are read over the GeoServices protocol
  Field already speaks (`FeaturePullService`, `GeoServicesFeatureSync`) — the
  same wire format used to sync, so an existing FeatureServer layer can be pulled
  in without an intermediate file. The round trip (query → decode → applyEdits
  add/update/delete → attachment) is proven against a reference in-memory
  FeatureServer in `GeoServicesRoundTripTests`, so interop is verified without a
  live GeoServices REST server.

## Exporting back out

Field exports the captured record set to every common interchange format, so you
are never locked in (`Honua.Field.Core.Export`):

| Format | API | Notes |
| --- | --- | --- |
| CSV | `RecordExporter.ToCsv` | one row per record, form-driven columns |
| GeoJSON | `RecordExporter.ToGeoJson` | RFC 7946 `FeatureCollection` |
| KML | `RecordExporter.ToKml` | OGC KML 2.2, opens in Google Earth |
| GeoPackage | `GeoPackageExporter.Export` | single-file OGC SQLite format supported by compatible GIS software |
| Shapefile | `ShapefileExporter.Export` | zipped `.shp`/`.shx`/`.dbf` |

Bulk export and the per-record templated report (`RecordReportRenderer`) are
available to every user.

## Round-trip to a GeoServices REST or self-hosted FeatureServer

Field reads and writes the **GeoServices REST** protocol (FeatureServer
`query` / `applyEdits`, `generateToken`) directly, so it can sync against:

- an existing **GeoServices REST** FeatureServer (no additional mobile apps required), or
- a **self-hosted** GeoServices-compatible server, with **no vendor-specific license**.

Point Field at the target by configuring `ServerBaseUrl` / `ServiceId` /
`LayerId` in `AppSettings`; sync uses `GeoServicesFeatureSync`. See
[data-residency-and-self-host.md](./data-residency-and-self-host.md) for where
records live and how the connection is secured.
