using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class TavernFirstCardAutoSLPolicyTests
{
    [Fact]
    public void Rate_resolution_uses_the_first_ValueRate_effect_like_native_GetValueRate()
    {
        var card = new TavernCardCandidate(
            serverCardId: 9003,
            masterCardId: 7003,
            effects:
            [
                new TavernCardEffect(targetType: 0, effectType: 20, effectParam: 1),
                // First native ValueRate is a 10% floor effect and therefore rejects the card.
                new TavernCardEffect(targetType: 102, effectType: 10, effectParam: 100),
                new TavernCardEffect(targetType: 1, effectType: 10, effectParam: 50),
            ]
        );

        TavernFirstCardEvaluation evaluation = TavernFirstCardAutoSLPolicy.Evaluate(
            [card],
            TavernFirstCardTarget.Cook
        );

        Assert.True(evaluation.ShouldRetry);
        Assert.Empty(evaluation.Matches);
    }

    [Fact]
    public void Category_resolution_uses_the_first_AddCategory_effect_like_native_GetCategory()
    {
        var card = new TavernCardCandidate(
            serverCardId: 9002,
            masterCardId: 7002,
            effects:
            [
                new TavernCardEffect(targetType: 0, effectType: 20, effectParam: 1),
                new TavernCardEffect(targetType: 0, effectType: 20, effectParam: 2),
                new TavernCardEffect(targetType: 1, effectType: 10, effectParam: 50),
            ]
        );

        TavernFirstCardEvaluation evaluation = TavernFirstCardAutoSLPolicy.Evaluate(
            [card],
            TavernFirstCardTarget.Waitress
        );

        Assert.True(evaluation.ShouldRetry);
        Assert.Empty(evaluation.Matches);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public void Only_zero_native_selected_count_is_the_first_card_turn(
        int selectedCount,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            TavernFirstCardAutoSLPolicy.IsFirstCardTurn(selectedCount)
        );
    }

    [Theory]
    [InlineData("off", TavernFirstCardTarget.Off)]
    [InlineData("cook", TavernFirstCardTarget.Cook)]
    [InlineData("waitress", TavernFirstCardTarget.Waitress)]
    [InlineData("drink", TavernFirstCardTarget.Drink)]
    public void Config_uses_the_native_tavern_category_words(
        string configured,
        TavernFirstCardTarget expected
    )
    {
        Assert.True(TavernFirstCardAutoSLPolicy.TryParseTarget(configured, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Off_accepts_the_server_response_without_a_reroll()
    {
        TavernFirstCardEvaluation evaluation = TavernFirstCardAutoSLPolicy.Evaluate(
            [],
            TavernFirstCardTarget.Off
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Empty(evaluation.Matches);
    }

    [Fact]
    public void Fresh_native_cook_five_percent_all_card_stops_the_first_turn_reroll()
    {
        var card = new TavernCardCandidate(
            serverCardId: 9001,
            masterCardId: 7001,
            effects:
            [
                // Fresh native decomp: AddCategory=20 and Cook=1.
                new TavernCardEffect(targetType: 0, effectType: 20, effectParam: 1),
                // Fresh native decomp: All=1, ValueRate=10, and 5%=50/1000.
                new TavernCardEffect(targetType: 1, effectType: 10, effectParam: 50),
            ]
        );

        TavernFirstCardEvaluation evaluation = TavernFirstCardAutoSLPolicy.Evaluate(
            [card],
            TavernFirstCardTarget.Cook
        );

        Assert.False(evaluation.ShouldRetry);
        Assert.Equal(7001, Assert.Single(evaluation.Matches).MasterCardId);
    }
}
