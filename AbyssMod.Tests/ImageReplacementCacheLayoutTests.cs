using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class ImageReplacementCacheLayoutTests
{
    [Fact]
    public void Published_snapshot_does_not_change_active_files_until_next_startup_promotion()
    {
        using var fixture = new LayoutFixture();
        File.WriteAllText(Path.Combine(fixture.Layout.ActiveRoot, "manifest.json"), "old");
        string staging = fixture.Layout.CreateStagingRoot();
        File.WriteAllText(Path.Combine(staging, "manifest.json"), "new");

        fixture.Layout.PublishPending(staging);

        Assert.Equal("old", File.ReadAllText(Path.Combine(fixture.Layout.ActiveRoot, "manifest.json")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(fixture.Layout.PendingRoot, "manifest.json")));

        Assert.True(fixture.Layout.PromotePendingIfReady());
        Assert.Equal("new", File.ReadAllText(Path.Combine(fixture.Layout.ActiveRoot, "manifest.json")));
        Assert.False(Directory.Exists(fixture.Layout.PendingRoot));
    }

    [Fact]
    public void Incomplete_pending_directory_is_never_activated()
    {
        using var fixture = new LayoutFixture();
        File.WriteAllText(Path.Combine(fixture.Layout.ActiveRoot, "manifest.json"), "old");
        Directory.CreateDirectory(fixture.Layout.PendingRoot);
        File.WriteAllText(Path.Combine(fixture.Layout.PendingRoot, "manifest.json"), "partial");

        Assert.False(fixture.Layout.PromotePendingIfReady());
        Assert.Equal("old", File.ReadAllText(Path.Combine(fixture.Layout.ActiveRoot, "manifest.json")));
    }

    private sealed class LayoutFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "AbyssMod-ImageLayoutTests",
            Guid.NewGuid().ToString("N")
        );

        public LayoutFixture()
        {
            Layout = new ImageReplacementCacheLayout(_root);
            Directory.CreateDirectory(Layout.ActiveRoot);
        }

        public ImageReplacementCacheLayout Layout { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
