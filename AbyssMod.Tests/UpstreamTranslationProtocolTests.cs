using AbyssMod;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class UpstreamTranslationProtocolTests
{
    [Fact]
    public void BuildCachePath_uses_upstream_language_first_layout()
    {
        const string cacheDir = "cache/translations";

        Assert.Equal(
            Path.Combine(cacheDir, "zh_Hans", "static.json"),
            TranslationPaths.BuildCachePath(cacheDir, "static", "zh_Hans")
        );
        Assert.Equal(
            Path.Combine(cacheDir, "zh_Hans", "names.json"),
            TranslationPaths.BuildCachePath(cacheDir, TranslationPaths.Names, "zh_Hans")
        );
        Assert.Equal(
            Path.Combine(cacheDir, "zh_Hans", "novels", "mas_1001000101.json"),
            TranslationPaths.BuildCachePath(
                cacheDir,
                TranslationPaths.Novels,
                "zh_Hans",
                "mas_1001000101"
            )
        );
    }

    [Fact]
    public void Manifest_reads_static_hash_from_extension_data()
    {
        var manifest = System.Text.Json.JsonSerializer.Deserialize<Manifest>(
            "{\"hash\":\"manifest\",\"static\":\"bundle\"}"
        );

        Assert.Equal("bundle", manifest!.GetFileHash("static"));
    }

    [Fact]
    public void Local_overlays_are_not_remote_translation_types()
    {
        Assert.False(TranslationPaths.IsCdnFlatType(TranslationPaths.AddOn));
        Assert.False(TranslationPaths.IsCdnFlatType(TranslationPaths.Other));
        Assert.False(TranslationPaths.IsCdnFlatType(TranslationPaths.Titles));
        Assert.False(TranslationPaths.IsCdnFlatType("m_items"));
        Assert.True(TranslationPaths.IsCdnFlatType(TranslationPaths.Static));
        Assert.True(TranslationPaths.IsCdnFlatType(TranslationPaths.UiTexts));
    }
}
