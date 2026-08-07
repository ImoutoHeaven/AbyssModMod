#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherRuntimeActivePartyHpExtractorTests
{
    [Fact]
    public void NetherModelPartyCharacterModels_AreReadAsTheAuthoritativeHpSurface()
    {
        var netherModel = new FakeNetherModel(
            new FakePartyModel(
                new FakePartyCharacter(10, 0.900d, isAlive: true),
                new FakePartyCharacter(20, 0.299d, isAlive: true)
            )
        );

        NetherActivePartyHpSafety safety = new NetherRuntimeActivePartyHpExtractor().Extract(netherModel);

        Assert.True(safety.IsKnown);
        Assert.Equal(299, safety.MinimumHpPermille);
    }

    [Fact]
    public void MissingPartyOrCharacters_IsUnknown()
    {
        NetherActivePartyHpSafety safety = new NetherRuntimeActivePartyHpExtractor().Extract(
            new FakeNetherModel(null)
        );

        Assert.False(safety.IsKnown);
        Assert.Null(safety.MinimumHpPermille);
        Assert.Contains("party", safety.Detail);
    }

    [Fact]
    public void DuplicateOrNonFiniteRuntimeCharacter_IsUnknown()
    {
        var netherModel = new FakeNetherModel(
            new FakePartyModel(
                new FakePartyCharacter(10, 0.900d, isAlive: true),
                new FakePartyCharacter(10, double.NaN, isAlive: true)
            )
        );

        NetherActivePartyHpSafety safety = new NetherRuntimeActivePartyHpExtractor().Extract(netherModel);

        Assert.False(safety.IsKnown);
        Assert.Null(safety.MinimumHpPermille);
    }

    private sealed class FakeNetherModel
    {
        public FakeNetherModel(FakePartyModel? partyModel) => PartyModel = partyModel;

        public FakePartyModel? PartyModel { get; }
    }

    private sealed class FakePartyModel
    {
        public FakePartyModel(params FakePartyCharacter[] characterModels) => CharacterModels = characterModels;

        public FakePartyCharacter[] CharacterModels { get; }
    }

    private sealed class FakePartyCharacter
    {
        public FakePartyCharacter(long characterId, double hpRatio, bool isAlive)
        {
            MCharacterId = characterId;
            HpRatio = hpRatio;
            IsAlive = isAlive;
        }

        public long MCharacterId { get; }
        public double HpRatio { get; }
        public bool IsAlive { get; }
    }
}
