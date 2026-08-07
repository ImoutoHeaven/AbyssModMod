using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherCodeOfferSelectionTests
{
    [Fact]
    public void Selected_code_resolves_to_its_exact_native_detail_index_before_receive()
    {
        bool resolved = NetherCodeOfferSelection.TryResolveIndex([30024, 50001, 40024], 50001, out int index);

        Assert.True(resolved);
        Assert.Equal(1, index);
    }

    [Fact]
    public void Missing_or_duplicate_native_offer_id_fails_closed()
    {
        Assert.False(NetherCodeOfferSelection.TryResolveIndex([30024, 40024], 999, out _));
        Assert.False(NetherCodeOfferSelection.TryResolveIndex([30024, 30024], 30024, out _));
    }
}
