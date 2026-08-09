#nullable enable

using System.Reflection;
using Xunit;

namespace AbyssMod.Tests;

public sealed class NetherBattleTerminalObservationPolicyTests
{
    [Theory]
    [InlineData(110, 1, "Clear")]
    [InlineData(110, 5, "Clear")]
    [InlineData(110, 2, "Close")]
    [InlineData(110, 4, "Close")]
    [InlineData(110, 3, "None")]
    [InlineData(110, 999, "None")]
    [InlineData(2, 1, "None")]
    [InlineData(0, 1, "None")]
    public void Classifies_only_authoritative_nether_clear_and_close_responses(
        int battleQuestType,
        int battleResultType,
        string expected
    )
    {
        Assembly production = typeof(AbyssMod.Services.NetherBattleSettlementCoordinator).Assembly;
        Type? policy = production.GetType(
            "AbyssMod.Services.NetherBattleTerminalObservationPolicy"
        );
        Assert.NotNull(policy);

        MethodInfo? classify = policy!.GetMethod(
            "Classify",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
        );
        Assert.NotNull(classify);

        object? actual = classify!.Invoke(null, new object?[] { battleQuestType, battleResultType });
        Assert.Equal(expected, actual?.ToString());
    }
}
