using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class BattleSessionDropProbeTests
{
    [Fact]
    public void Parse_reads_drop_items_and_rare_drop_count_from_stage_detail()
    {
        const string stageDetail = """
            {
              "drops": [
                {
                  "sid": 11,
                  "content_type": 2,
                  "content_id": 3001,
                  "amount": 2,
                  "rarity_level": 1,
                  "is_rare_drop": 0
                },
                {
                  "sid": 12,
                  "content_type": 2,
                  "content_id": 9001,
                  "amount": 1,
                  "rarity_level": 5,
                  "is_rare_drop": 1
                }
              ]
            }
            """;

        BattleDropProbeReport report = BattleSessionDropProbe.Parse(stageDetail);

        Assert.Equal(2, report.DropCount);
        Assert.Equal(1, report.RareDropCount);
        Assert.Equal(
            new BattleDropItem(11, 2, 3001, 2, 1, false),
            report.Items[0]
        );
        Assert.Equal(
            new BattleDropItem(12, 2, 9001, 1, 5, true),
            report.Items[1]
        );
    }

    [Fact]
    public void Parse_returns_missing_report_when_stage_detail_has_no_drops_array()
    {
        BattleDropProbeReport report = BattleSessionDropProbe.Parse("{\"stage\":1}");

        Assert.Equal(0, report.DropCount);
        Assert.Equal(0, report.RareDropCount);
        Assert.Empty(report.Items);
        Assert.Equal("missing", report.Error);
    }

    [Fact]
    public void FormatItemList_contains_all_fields_needed_for_runtime_probe_log()
    {
        const string stageDetail =
            "{\"drops\":[{\"sid\":7,\"content_type\":3,\"content_id\":88,\"amount\":4,\"rarity_level\":2,\"is_rare_drop\":1}]}";

        BattleDropProbeReport report = BattleSessionDropProbe.Parse(stageDetail);

        Assert.Equal("sid=7 contentType=3 contentId=88 amount=4 rarity=2 isRare=1", report.FormatItemList());
    }

    [Fact]
    public void Parse_stops_on_structurally_invalid_drop_payloads()
    {
        BattleDropProbeReport report = BattleSessionDropProbe.Parse(
            "{\"drops\":[{\"sid\":7,\"is_rare_drop\":\"1\"}]}"
        );

        Assert.Equal("parse-error", report.Error);
        Assert.Empty(report.Items);
    }

    [Fact]
    public void Parse_stops_on_non_object_json_roots()
    {
        BattleDropProbeReport report = BattleSessionDropProbe.Parse("[]");

        Assert.Equal("parse-error", report.Error);
    }
}
