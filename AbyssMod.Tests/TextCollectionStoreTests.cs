using System.Text.Json;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class TextCollectionStoreTests
{
    [Fact]
    public void Recording_a_new_entry_preserves_existing_dump_entries_and_values()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "dialogue_raw.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, "{\"既存\":\"已有译文\"}");

        try
        {
            Assert.True(TextCollectionStore.Add(path, "新規"));

            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            Assert.NotNull(saved);
            Assert.Equal("已有译文", saved["既存"]);
            Assert.Equal(string.Empty, saved["新規"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Every_append_merges_external_changes_from_disk()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "dialogue_raw.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, "{\"最初\":\"\"}");

        try
        {
            Assert.True(TextCollectionStore.Add(path, "追加一"));
            File.WriteAllText(path, "{\"最初\":\"\",\"外部\":\"人工译文\",\"追加一\":\"\"}");

            Assert.True(TextCollectionStore.Add(path, "追加二"));

            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            Assert.NotNull(saved);
            Assert.Equal("人工译文", saved["外部"]);
            Assert.True(saved.ContainsKey("追加一"));
            Assert.True(saved.ContainsKey("追加二"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
