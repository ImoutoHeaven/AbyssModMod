using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherPopupOwnershipRegistryTests
{
    [Fact]
    public void Exact_old_close_does_not_clear_new_popup_for_same_owner()
    {
        var registry = new NetherPopupOwnershipRegistry();
        object first = new();
        object second = new();
        long generation = registry.BeginOwner(NetherActionKind.SelectFloor);
        NetherPopupOwnership oldPopup = registry.Register(first, NetherActionKind.SelectFloor, generation);
        NetherPopupOwnership newPopup = registry.Register(second, NetherActionKind.SelectFloor, generation);

        registry.Invalidate(first, oldPopup.Sequence);

        Assert.True(registry.TryGetOwned(NetherActionKind.SelectFloor, generation, out NetherPopupOwnership current));
        Assert.Same(second, current.Popup);
        Assert.Equal(newPopup.Sequence, current.Sequence);
    }

    [Fact]
    public void Parent_terminal_invalidates_only_its_own_generation()
    {
        var registry = new NetherPopupOwnershipRegistry();
        object first = new();
        object second = new();
        long firstGeneration = registry.BeginOwner(NetherActionKind.SelectFloor);
        registry.Register(first, NetherActionKind.SelectFloor, firstGeneration);
        long secondGeneration = registry.BeginOwner(NetherActionKind.SelectFloor);
        registry.Register(second, NetherActionKind.SelectFloor, secondGeneration);

        registry.InvalidateOwner(NetherActionKind.SelectFloor, firstGeneration);

        Assert.True(registry.TryGetOwned(NetherActionKind.SelectFloor, secondGeneration, out NetherPopupOwnership current));
        Assert.Same(second, current.Popup);
    }
}
