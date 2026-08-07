#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherRuntimeGenerationVisibilityTests
{
    [Fact]
    public void Live_generation_is_absent_between_old_owner_teardown_and_new_owner_registration()
    {
        object oldOwner = new();
        object newOwner = new();

        Assert.Equal(0, NetherRuntimeGenerationVisibility.ForLiveFloorSelection(null, monotonicGeneration: 41));
        Assert.Equal(41, NetherRuntimeGenerationVisibility.ForLiveFloorSelection(oldOwner, monotonicGeneration: 41));
        Assert.Equal(0, NetherRuntimeGenerationVisibility.ForLiveFloorSelection(null, monotonicGeneration: 41));
        Assert.Equal(42, NetherRuntimeGenerationVisibility.ForLiveFloorSelection(newOwner, monotonicGeneration: 42));
    }
}
