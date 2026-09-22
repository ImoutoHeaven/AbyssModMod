using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NovelScenarioCandidateCollectorTests
{
    [Fact]
    public void Collects_the_whole_scenes_text_without_names_or_asset_arguments()
    {
        IReadOnlyList<string>[] rows =
        [
            ["message", "ヴェスペラ", "本文です", "chara_asset"],
            ["l2dmessage", "ルディア", "二行目だよ", "l2d_asset"],
            ["dotmessage", "mob", "吹き出しです", "voice"],
            ["asyncdotmessage", "mob", "非同期の吹き出し", "voice"],
            ["messagetextcenter", "", "中央の文章", ""],
            ["messagetextunder", "", "下側の文章", ""],
            ["message", "", "決定", ""],
            ["select", "分岐ラベル", "選択肢ふたつ"],
            ["charaload", "日本語アセット名"],
            ["message", "", "fade_in", ""],
            ["message", "別の名前", "本文です", "chara_asset"],
        ];

        Assert.Equal(
            [
                "本文です",
                "二行目だよ",
                "吹き出しです",
                "非同期の吹き出し",
                "中央の文章",
                "下側の文章",
                "選択肢ふたつ",
            ],
            NovelScenarioCandidateCollector.Collect(rows)
        );
    }
}
