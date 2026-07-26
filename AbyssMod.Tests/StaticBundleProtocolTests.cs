using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class StaticBundleProtocolTests
{
    [Fact]
    public void ComputeHash_sorts_table_field_and_source_keys()
    {
        var bundle = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>
        {
            ["m_b"] = new()
            {
                ["description"] = new()
                {
                    ["B"] = "two",
                    ["A"] = "one",
                },
            },
            ["m_a"] = new()
            {
                ["name"] = new()
                {
                    ["z"] = "last",
                },
            },
        };

        Assert.Equal("db5b44985c4b5f702c506100f40fe557", StaticBundleProtocol.ComputeHash(bundle));
    }

    [Fact]
    public void Flatten_uses_last_field_value_for_duplicate_sources()
    {
        var fields = new Dictionary<string, Dictionary<string, string>>
        {
            ["description"] = new() { ["same"] = "description" },
            ["name"] = new() { ["same"] = "name" },
        };

        var result = StaticBundleProtocol.Flatten(fields);

        Assert.Equal("name", result["same"]);
    }

    [Fact]
    public void GetFieldTable_prefers_the_requested_field_over_flattened_fallback()
    {
        var bundle = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>
        {
            ["m_items"] = new()
            {
                ["description"] = new() { ["same"] = "description" },
                ["name"] = new() { ["same"] = "name" },
            },
        };

        var result = StaticBundleProtocol.GetFieldTable(bundle, "m_items", "description");

        Assert.Equal("description", result!["same"]);
    }
}
