using Honua.Collect.Core.Sync;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Sync;

public class SyncAttributeFilterTests
{
    private static FieldRecord Record(params (string Key, object? Value)[] values)
    {
        var r = new FieldRecord { RecordId = "r", FormId = "f", Status = RecordStatus.Submitted };
        foreach (var (key, value) in values)
        {
            r.Values[key] = value;
        }

        return r;
    }

    [Fact]
    public void Equality_matches_string_value()
    {
        var filter = SyncAttributeFilter.Parse("status = 'open'");

        Assert.True(filter.Matches(Record(("status", "open"))));
        Assert.False(filter.Matches(Record(("status", "closed"))));
        Assert.False(filter.Matches(Record(("other", "open"))));
    }

    [Fact]
    public void Numeric_comparison_uses_numeric_order_not_string_order()
    {
        var filter = SyncAttributeFilter.Parse("priority >= 5");

        Assert.True(filter.Matches(Record(("priority", 10)))); // 10 >= 5 numerically (not "10" < "5")
        Assert.True(filter.Matches(Record(("priority", 5L))));
        Assert.False(filter.Matches(Record(("priority", 4.9))));
    }

    [Fact]
    public void Not_equal_supports_both_spellings()
    {
        Assert.True(SyncAttributeFilter.Parse("status != 'done'").Matches(Record(("status", "open"))));
        Assert.True(SyncAttributeFilter.Parse("status <> 'done'").Matches(Record(("status", "open"))));
        Assert.False(SyncAttributeFilter.Parse("status != 'done'").Matches(Record(("status", "done"))));
    }

    [Fact]
    public void And_binds_tighter_than_or()
    {
        // crew='A' AND priority>3 OR status='urgent'
        var filter = SyncAttributeFilter.Parse("crew = 'A' AND priority > 3 OR status = 'urgent'");

        Assert.True(filter.Matches(Record(("crew", "A"), ("priority", 9))));        // left conjunction
        Assert.True(filter.Matches(Record(("crew", "B"), ("status", "urgent"))));   // right disjunct
        Assert.False(filter.Matches(Record(("crew", "A"), ("priority", 1))));       // neither
    }

    [Fact]
    public void In_list_matches_any_member()
    {
        var filter = SyncAttributeFilter.Parse("kind IN ('tree', 'shrub', 'vine')");

        Assert.True(filter.Matches(Record(("kind", "shrub"))));
        Assert.False(filter.Matches(Record(("kind", "weed"))));
    }

    [Fact]
    public void Like_supports_percent_wildcard()
    {
        var filter = SyncAttributeFilter.Parse("name LIKE 'Oak%'");

        Assert.True(filter.Matches(Record(("name", "Oak Street"))));
        Assert.False(filter.Matches(Record(("name", "Elm Street"))));
    }

    [Fact]
    public void Is_null_and_is_not_null()
    {
        Assert.True(SyncAttributeFilter.Parse("photo IS NULL").Matches(Record(("photo", null))));
        Assert.True(SyncAttributeFilter.Parse("photo IS NULL").Matches(Record())); // absent counts as null
        Assert.True(SyncAttributeFilter.Parse("photo IS NOT NULL").Matches(Record(("photo", "a.jpg"))));
        Assert.False(SyncAttributeFilter.Parse("photo IS NOT NULL").Matches(Record(("photo", null))));
    }

    [Fact]
    public void Quoted_string_with_escaped_quote_round_trips()
    {
        var filter = SyncAttributeFilter.Parse("note = 'O''Brien'");

        Assert.True(filter.Matches(Record(("note", "O'Brien"))));
    }

    [Fact]
    public void Where_text_is_preserved_for_server_clause()
    {
        Assert.Equal("priority >= 5", SyncAttributeFilter.Parse("  priority >= 5  ").Where);
    }

    [Theory]
    [InlineData("priority >=")]
    [InlineData("= 5")]
    [InlineData("kind IN ()")]
    [InlineData("status LIKE")]
    public void Malformed_clause_fails_fast(string where)
    {
        Assert.Throws<FormatException>(() => SyncAttributeFilter.Parse(where));
    }
}
