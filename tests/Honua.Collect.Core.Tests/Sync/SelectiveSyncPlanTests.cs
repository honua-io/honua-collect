using Honua.Collect.Core.Field.Geometry;
using Honua.Collect.Core.Sync;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Sync;

public class SelectiveSyncPlanTests
{
    private static FieldRecord Record(
        string id = "r",
        FieldGeoPoint? location = null,
        DateTimeOffset? created = null,
        DateTimeOffset? submitted = null,
        params (string Key, object? Value)[] values)
    {
        var r = new FieldRecord
        {
            RecordId = id,
            FormId = "f",
            Status = RecordStatus.Submitted,
            Location = location,
            CreatedAtUtc = created ?? DateTimeOffset.UtcNow,
            SubmittedAtUtc = submitted,
        };
        foreach (var (key, value) in values)
        {
            r.Values[key] = value;
        }

        return r;
    }

    [Fact]
    public void SyncEverything_includes_any_layer_and_record()
    {
        var plan = SelectiveSyncPlan.SyncEverything;

        Assert.True(plan.IncludesLayer("anything"));
        Assert.True(plan.IncludesRecord("anything", null));
        Assert.True(plan.IncludesRecord("anything", new FieldGeoPoint(0, 0)));
    }

    [Fact]
    public void Unlisted_layers_are_excluded_by_default()
    {
        var plan = new SelectiveSyncPlan([new LayerSyncScope { LayerKey = "trees" }]);

        Assert.True(plan.IncludesLayer("trees"));
        Assert.False(plan.IncludesLayer("roads"));
        Assert.False(plan.IncludesRecord("roads", new FieldGeoPoint(0, 0)));
    }

    [Fact]
    public void Disabled_layer_never_syncs()
    {
        var plan = new SelectiveSyncPlan([new LayerSyncScope { LayerKey = "trees", Enabled = false }]);

        Assert.False(plan.IncludesLayer("trees"));
        Assert.False(plan.IncludesRecord("trees", new FieldGeoPoint(0, 0)));
    }

    [Fact]
    public void Whole_layer_scope_includes_records_regardless_of_location()
    {
        var plan = new SelectiveSyncPlan([new LayerSyncScope { LayerKey = "trees" }]);

        Assert.True(plan.IncludesRecord("trees", null));
        Assert.True(plan.IncludesRecord("trees", new FieldGeoPoint(48, 2)));
    }

    [Fact]
    public void Area_restricted_layer_includes_only_records_inside_an_area()
    {
        var plan = new SelectiveSyncPlan(
        [
            new LayerSyncScope
            {
                LayerKey = "trees",
                Areas = [new SyncAreaBounds(45, -123, 46, -122)],
            },
        ]);

        Assert.True(plan.IncludesRecord("trees", new FieldGeoPoint(45.5, -122.5))); // inside
        Assert.False(plan.IncludesRecord("trees", new FieldGeoPoint(40, -122.5)));  // outside
        Assert.False(plan.IncludesRecord("trees", null));                           // unplaceable
    }

    [Fact]
    public void Record_matches_when_inside_any_of_several_areas()
    {
        var plan = new SelectiveSyncPlan(
        [
            new LayerSyncScope
            {
                LayerKey = "trees",
                Areas =
                [
                    new SyncAreaBounds(45, -123, 46, -122),
                    new SyncAreaBounds(0, 0, 1, 1),
                ],
            },
        ]);

        Assert.True(plan.IncludesRecord("trees", new FieldGeoPoint(0.5, 0.5)));
    }

    [Fact]
    public void Bounds_contains_is_inclusive_of_edges()
    {
        var bounds = new SyncAreaBounds(45, -123, 46, -122);

        Assert.True(bounds.Contains(new FieldGeoPoint(45, -123)));  // corner
        Assert.True(bounds.Contains(new FieldGeoPoint(46, -122)));  // opposite corner
        Assert.False(bounds.Contains(new FieldGeoPoint(46.0001, -122)));
    }

    // --- S2: attribute where predicate ---

    [Fact]
    public void Attribute_filter_restricts_records_by_predicate()
    {
        var plan = new SelectiveSyncPlan(
        [
            new LayerSyncScope { LayerKey = "trees", Where = SyncAttributeFilter.Parse("status = 'open'") },
        ]);

        Assert.True(plan.IncludesRecord("trees", Record(values: ("status", "open"))));
        Assert.False(plan.IncludesRecord("trees", Record(values: ("status", "closed"))));
    }

    [Fact]
    public void Where_clause_for_layer_is_the_server_query_clause()
    {
        var plan = new SelectiveSyncPlan(
        [
            new LayerSyncScope { LayerKey = "trees", Where = SyncAttributeFilter.Parse("priority >= 5") },
            new LayerSyncScope { LayerKey = "roads" },
        ]);

        Assert.Equal("priority >= 5", plan.WhereClauseFor("trees")); // sent to the server
        Assert.Equal("1=1", plan.WhereClauseFor("roads"));           // no attribute filter
        Assert.Equal("1=1", plan.WhereClauseFor("unlisted"));
    }

    // --- S2: projected-CRS extent (the #302 lesson) ---

    [Fact]
    public void Projected_extent_filters_in_layer_crs_units_not_latlon()
    {
        // Extent in metres; the projector hands the plan projected easting/northing
        // straight through, so nothing is funnelled through WGS84 lat/lon.
        var plan = new SelectiveSyncPlan(
        [
            new LayerSyncScope
            {
                LayerKey = "parcels",
                Extent = new SyncExtent(500_000, 4_100_000, 510_000, 4_110_000),
            },
        ]);

        // Project each record to a fixed CRS point keyed off an attribute (stand-in
        // for a real geographic→layer-CRS transform).
        PlanarPoint? Project(FieldRecord r) =>
            r.Values.TryGetValue("easting", out var e) && r.Values.TryGetValue("northing", out var n)
                ? new PlanarPoint(Convert.ToDouble(e), Convert.ToDouble(n))
                : null;

        var inside = Record("a", values: [("easting", 505_000.0), ("northing", 4_105_000.0)]);
        var outside = Record("b", values: [("easting", 600_000.0), ("northing", 4_105_000.0)]);
        var unplaceable = Record("c");

        Assert.True(plan.IncludesRecord("parcels", inside, Project));
        Assert.False(plan.IncludesRecord("parcels", outside, Project));
        Assert.False(plan.IncludesRecord("parcels", unplaceable, Project)); // no projection → excluded
    }

    // --- S2: record age / date filter ---

    [Fact]
    public void Date_window_excludes_records_outside_max_age()
    {
        var now = new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero);
        var plan = new SelectiveSyncPlan(
        [
            new LayerSyncScope { LayerKey = "obs", DateWindow = SyncDateWindow.WithinLast(TimeSpan.FromDays(7)) },
        ]);

        var fresh = Record("a", created: now.AddDays(-2));
        var stale = Record("b", created: now.AddDays(-30));

        Assert.True(plan.IncludesRecord("obs", fresh, now: now));
        Assert.False(plan.IncludesRecord("obs", stale, now: now));
    }

    [Fact]
    public void Date_window_respects_absolute_bounds_and_prefers_submitted_stamp()
    {
        var since = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var plan = new SelectiveSyncPlan(
        [
            new LayerSyncScope { LayerKey = "obs", DateWindow = SyncDateWindow.Since(since) },
        ]);

        // Created last year but submitted this year → in window (submitted wins).
        var resubmitted = Record("a", created: since.AddYears(-1), submitted: since.AddMonths(2));
        var old = Record("b", created: since.AddDays(-1));

        Assert.True(plan.IncludesRecord("obs", resubmitted));
        Assert.False(plan.IncludesRecord("obs", old));
    }

    // --- S2: explicit selection set ---

    [Fact]
    public void Explicit_selection_set_includes_only_listed_records()
    {
        var plan = new SelectiveSyncPlan(
        [
            new LayerSyncScope
            {
                LayerKey = "trees",
                RecordIds = new HashSet<string>(["keep-1", "keep-2"], StringComparer.Ordinal),
            },
        ]);

        Assert.True(plan.IncludesRecord("trees", Record("keep-1")));
        Assert.True(plan.IncludesRecord("trees", Record("keep-2")));
        Assert.False(plan.IncludesRecord("trees", Record("skip-3")));
    }

    // --- S2: constraints compose (AND) ---

    [Fact]
    public void Multiple_constraints_must_all_hold()
    {
        var now = new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero);
        var plan = new SelectiveSyncPlan(
        [
            new LayerSyncScope
            {
                LayerKey = "trees",
                Where = SyncAttributeFilter.Parse("status = 'open'"),
                DateWindow = SyncDateWindow.WithinLast(TimeSpan.FromDays(7)),
            },
        ]);

        var both = Record("a", created: now.AddDays(-1), values: ("status", "open"));
        var rightAttrWrongDate = Record("b", created: now.AddDays(-30), values: ("status", "open"));
        var wrongAttrRightDate = Record("c", created: now.AddDays(-1), values: ("status", "closed"));

        Assert.True(plan.IncludesRecord("trees", both, now: now));
        Assert.False(plan.IncludesRecord("trees", rightAttrWrongDate, now: now));
        Assert.False(plan.IncludesRecord("trees", wrongAttrRightDate, now: now));
    }
}
