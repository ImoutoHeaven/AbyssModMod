#nullable enable

using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherFloorMasterBoundsMapperTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 5)]
    [InlineData(5, 10)]
    public void ExactMasterId_ReturnsAuthoritativeMinAndMax(int minimum, int maximum)
    {
        NetherFloorMasterBounds bounds = Map(42, new NetherFloorMasterBoundsRow(42, minimum, maximum));

        Assert.True(bounds.IsKnown);
        Assert.Equal(42, bounds.MasterFloorId);
        Assert.Equal(minimum, bounds.MinimumErosionPoint);
        Assert.Equal(maximum, bounds.MaximumErosionPoint);
        Assert.Equal(string.Empty, bounds.Detail);
    }

    [Fact]
    public void MissingTargetMasterRow_IsUnknownInsteadOfZeroBounds()
    {
        NetherFloorMasterBounds bounds = Map(42, new NetherFloorMasterBoundsRow(9, 0, 5));

        Assert.False(bounds.IsKnown);
        Assert.Null(bounds.MinimumErosionPoint);
        Assert.Null(bounds.MaximumErosionPoint);
        Assert.Contains("missing", bounds.Detail);
    }

    [Fact]
    public void DuplicateMasterRows_AreAmbiguousAndUnknown()
    {
        NetherFloorMasterBounds bounds = Map(
            42,
            new NetherFloorMasterBoundsRow(42, 0, 5),
            new NetherFloorMasterBoundsRow(42, 0, 5)
        );

        Assert.False(bounds.IsKnown);
        Assert.Null(bounds.MinimumErosionPoint);
        Assert.Contains("duplicate", bounds.Detail);
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(6, 5)]
    public void InvalidMasterBounds_AreUnknown(int minimum, int maximum)
    {
        NetherFloorMasterBounds bounds = Map(42, new NetherFloorMasterBoundsRow(42, minimum, maximum));

        Assert.False(bounds.IsKnown);
        Assert.Null(bounds.MinimumErosionPoint);
        Assert.Null(bounds.MaximumErosionPoint);
        Assert.Contains("invalid", bounds.Detail);
    }

    [Fact]
    public void OverflowingRawMasterField_IsUnknownInsteadOfWrapping()
    {
        NetherFloorMasterBounds bounds = Map(
            42,
            new NetherFloorMasterBoundsRow(42, 0, (long)int.MaxValue + 1)
        );

        Assert.False(bounds.IsKnown);
        Assert.Null(bounds.MaximumErosionPoint);
        Assert.Contains("overflow", bounds.Detail);
    }

    [Fact]
    public void MissingRequiredRawField_IsUnknown()
    {
        NetherFloorMasterBounds bounds = Map(
            42,
            new NetherFloorMasterBoundsRow(42, 0, 5) { HasRequiredFields = false }
        );

        Assert.False(bounds.IsKnown);
        Assert.Null(bounds.MinimumErosionPoint);
        Assert.Contains("missing", bounds.Detail);
    }

    private static NetherFloorMasterBounds Map(long runtimeFloorMasterId, params NetherFloorMasterBoundsRow[] rows) =>
        new NetherFloorMasterBoundsMapper().Map(runtimeFloorMasterId, rows);
}
