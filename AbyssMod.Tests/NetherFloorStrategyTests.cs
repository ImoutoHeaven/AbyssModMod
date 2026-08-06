using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherFloorStrategyTests
{
    [Theory]
    [InlineData("1-49=Off;50-*=Gold", 1, NetherSlTarget.Off)]
    [InlineData("1-49=Off;50-*=Gold", 49, NetherSlTarget.Off)]
    [InlineData("1-49=Off;50-*=Gold", 50, NetherSlTarget.Gold)]
    [InlineData("1-49=Off;50-*=Gold", 140, NetherSlTarget.Gold)]
    [InlineData("*=Silver", 1, NetherSlTarget.Silver)]
    [InlineData("100,110,120=Red", 120, NetherSlTarget.Red)]
    public void Resolve_matches_each_selector_form_inclusively(string text, int floor, NetherSlTarget expected)
    {
        NetherFloorStrategyDecision decision = Parse(text).Resolve(floor);

        Assert.True(decision.Matched);
        Assert.Equal(expected, decision.Target);
    }

    [Fact]
    public void Resolve_uses_the_last_matching_clause()
    {
        NetherFloorStrategy strategy = Parse("*=Gold;100,110,120,130=Red;110=Off");

        Assert.Equal(NetherSlTarget.Red, strategy.Resolve(100).Target);
        Assert.Equal(NetherSlTarget.Off, strategy.Resolve(110).Target);
        Assert.Equal(NetherSlTarget.Gold, strategy.Resolve(111).Target);
    }

    [Theory]
    [InlineData(1, NetherEncounterKind.Battle)]
    [InlineData(2, NetherEncounterKind.Boss)]
    [InlineData(3, NetherEncounterKind.MiniBoss)]
    [InlineData(4, NetherEncounterKind.Battle)]
    [InlineData(0, NetherEncounterKind.Unknown)]
    [InlineData(8, NetherEncounterKind.Unknown)]
    public void Classify_maps_raw_floor_types(int rawType, NetherEncounterKind expected)
    {
        Assert.Equal(expected, NetherEncounterClassifier.Classify(rawType));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1-49")]
    [InlineData("1=Unknown")]
    [InlineData("1=3")]
    [InlineData("0=Gold")]
    [InlineData("50-49=Gold")]
    [InlineData("50-=Gold")]
    [InlineData("1=Gold;")]
    public void Parse_rejects_the_entire_malformed_value(string text)
    {
        Assert.False(NetherFloorStrategyParser.TryParse(text, out NetherFloorStrategy? strategy, out string error));
        Assert.Null(strategy);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("1=Silver,Purple")]
    [InlineData("1=Off,Silver")]
    [InlineData("1=NoEffect,Gold")]
    [InlineData("1 0=Gold")]
    [InlineData("1\t0=Gold")]
    [InlineData("1 0-20=Gold")]
    [InlineData("10-2 0=Gold")]
    [InlineData("1=Gold;50=Red;1 0=Gold")]
    public void Parse_rejects_malformed_target_or_internal_floor_whitespace_without_partial_rules(string text)
    {
        Assert.False(NetherFloorStrategyParser.TryParse(text, out NetherFloorStrategy? strategy, out string error));
        Assert.Null(strategy);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Resolve_trims_whitespace_and_ignores_target_case()
    {
        NetherFloorStrategyDecision decision = Parse(" 1 - 49 = off ; 50 , 60 - * = gOlD ").Resolve(60);

        Assert.True(decision.Matched);
        Assert.Equal(NetherSlTarget.Gold, decision.Target);
        Assert.Equal("50,60-*", decision.Selector);
    }

    [Fact]
    public void Resolve_returns_explicit_unmatched_off_decision()
    {
        NetherFloorStrategyDecision decision = Parse("50-99=Gold").Resolve(1);

        Assert.False(decision.Matched);
        Assert.Equal(NetherSlTarget.Off, decision.Target);
        Assert.Equal(-1, decision.ClauseIndex);
        Assert.Equal(string.Empty, decision.Selector);
    }

    [Fact]
    public void Runtime_decision_uses_all_production_default_matrix_cells()
    {
        foreach (object[] row in DefaultMatrix)
        {
            int rawType = (int)row[0];
            int floor = (int)row[1];
            NetherSlTarget expected = (NetherSlTarget)row[2];
            NetherRuntimeDecision decision = NetherRuntimeDecisionEngine.Resolve(
                rawType, floor, NetherStrategySettings.Default
            );

            Assert.Equal(expected, decision.Target);
            Assert.Equal(expected == NetherSlTarget.Off ? NetherRuntimeDecisionKind.AcceptOff : NetherRuntimeDecisionKind.Evaluate, decision.Kind);
            Assert.False(decision.ShouldRetry);
        }
    }

    [Fact]
    public void Runtime_decision_selects_real_encounter_config_and_live_values()
    {
        var first = new NetherStrategySettings("*=Silver", "*=Purple", "*=Red");
        NetherRuntimeDecision eventDecision = NetherRuntimeDecisionEngine.Resolve(4, 70, first);
        NetherRuntimeDecision miniBossDecision = NetherRuntimeDecisionEngine.Resolve(3, 70, first);
        NetherRuntimeDecision bossDecision = NetherRuntimeDecisionEngine.Resolve(2, 70, first);

        Assert.Equal("NetherBattleStrategy", eventDecision.ConfigKey);
        Assert.Equal(NetherSlTarget.Silver, eventDecision.Target);
        Assert.Equal("NetherMiniBossStrategy", miniBossDecision.ConfigKey);
        Assert.Equal(NetherSlTarget.Purple, miniBossDecision.Target);
        Assert.Equal("NetherBossStrategy", bossDecision.ConfigKey);
        Assert.Equal(NetherSlTarget.Red, bossDecision.Target);
        Assert.Equal(NetherSlTarget.Gold, NetherRuntimeDecisionEngine.Resolve(4, 70, new NetherStrategySettings("*=Gold", "*=Purple", "*=Red")).Target);
    }

    [Theory]
    [InlineData(0, 50, "*=Gold", NetherRuntimeDecisionKind.AcceptOff, "unknown-floor-type")]
    [InlineData(1, 50, "*=Off", NetherRuntimeDecisionKind.AcceptOff, "strategy-off")]
    [InlineData(1, 50, "1-49=Gold", NetherRuntimeDecisionKind.AcceptOff, "strategy-unmatched")]
    [InlineData(1, 50, "1 0=Gold", NetherRuntimeDecisionKind.AcceptError, "invalid-selector:0")]
    public void Runtime_decision_bypasses_or_fails_open_without_retry(
        int rawType, int floor, string battle, NetherRuntimeDecisionKind expectedKind, string expectedReason
    )
    {
        NetherRuntimeDecision decision = NetherRuntimeDecisionEngine.Resolve(
            rawType, floor, new NetherStrategySettings(battle, "*=Gold", "*=Gold")
        );

        Assert.Equal(expectedKind, decision.Kind);
        Assert.Equal(expectedReason, decision.Reason);
        Assert.False(decision.RequiresDropEvaluation);
        Assert.False(decision.ShouldRetry);
    }

    [Fact]
    public void Runtime_log_context_retains_raw_preserve_values_and_warns_for_bypass_classification()
    {
        NetherRuntimeDecision unmatched = NetherRuntimeDecisionEngine.Resolve(
            1, 50, new NetherStrategySettings("1-49=Gold", "*=Gold", "*=Gold")
        );
        NetherAttemptLogContext context = new("100,invalid", (NetherPreserveMode)99, unmatched);

        Assert.Equal("100,invalid", context.RawPreserveItemIds);
        Assert.Equal((NetherPreserveMode)99, context.PreserveMode);
        Assert.Equal(NetherAttemptLogLevel.Warning, context.Level);
    }

    [Fact]
    public void Runtime_log_context_keeps_known_configured_off_at_info()
    {
        NetherRuntimeDecision decision = NetherRuntimeDecisionEngine.Resolve(
            1, 50, new NetherStrategySettings("*=Off", "*=Gold", "*=Gold")
        );
        NetherAttemptLogContext context = new(string.Empty, NetherPreserveMode.AND, decision);

        Assert.Equal(NetherRuntimeDecisionKind.AcceptOff, decision.Kind);
        Assert.Equal(NetherEncounterKind.Battle, decision.EncounterKind);
        Assert.Equal("strategy-off", decision.Reason);
        Assert.Equal(NetherAttemptLogLevel.Info, context.Level);
    }

    [Theory]
    [InlineData("missing-nether-param")]
    [InlineData("invalid-floor-level:0")]
    [InlineData("invalid-nether-map-floor-id:0")]
    [InlineData("missing-nether-map-floor:123")]
    [InlineData("empty-m-nether-map-floors-cache")]
    [InlineData("nether-floor-master-load-error:InvalidOperationException:boom")]
    public void Runtime_bypass_context_keeps_precise_location_reason_codes(string reason)
    {
        NetherRuntimeDecision decision = NetherRuntimeDecisionEngine.CreateBypass(reason);
        NetherAttemptLogContext context = new("200003", NetherPreserveMode.AND, decision);

        Assert.Equal(reason, decision.Reason);
        Assert.Equal(NetherAttemptLogLevel.Warning, context.Level);
        Assert.Equal("200003", context.RawPreserveItemIds);
    }

    public static IEnumerable<object[]> DefaultMatrix => new[]
    {
        Cell(1, 1, NetherSlTarget.Off), Cell(3, 1, NetherSlTarget.Off), Cell(2, 1, NetherSlTarget.Off),
        Cell(1, 49, NetherSlTarget.Off), Cell(3, 49, NetherSlTarget.Off), Cell(2, 49, NetherSlTarget.Off),
        Cell(1, 50, NetherSlTarget.Gold), Cell(3, 50, NetherSlTarget.Gold), Cell(2, 50, NetherSlTarget.Gold),
        Cell(1, 99, NetherSlTarget.Gold), Cell(3, 99, NetherSlTarget.Gold), Cell(2, 99, NetherSlTarget.Gold),
        Cell(1, 100, NetherSlTarget.Gold), Cell(3, 100, NetherSlTarget.Gold), Cell(2, 100, NetherSlTarget.Red),
        Cell(1, 110, NetherSlTarget.Gold), Cell(3, 110, NetherSlTarget.Gold), Cell(2, 110, NetherSlTarget.Red),
        Cell(1, 140, NetherSlTarget.Gold), Cell(3, 140, NetherSlTarget.Gold), Cell(2, 140, NetherSlTarget.Red),
    };

    private static object[] Cell(int rawType, int floor, NetherSlTarget target) => new object[] { rawType, floor, target };

    private static NetherFloorStrategy Parse(string text)
    {
        bool parsed = NetherFloorStrategyParser.TryParse(text, out NetherFloorStrategy? strategy, out string error);
        Assert.True(parsed, error);
        return Assert.IsType<NetherFloorStrategy>(strategy);
    }
}
