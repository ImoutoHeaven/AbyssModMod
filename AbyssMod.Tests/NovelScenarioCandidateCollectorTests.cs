using AbyssMod.Services;
using System.Text.Json;
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

    [Fact]
    public void Capture_preserves_the_complete_script_while_candidates_remain_kana_only()
    {
        IReadOnlyList<string>[] rows =
        [
            ["charaload", "vespera", "ヴェスペラ"],
            ["message", "ヴェスペラ", "本文です", "voice_001"],
            ["wait", "500"],
            ["message", "司令官", "纯汉字", ""],
        ];

        NovelScenarioSnapshot scene = NovelScenarioCandidateCollector.Capture(rows, isComplete: true);

        Assert.True(scene.IsComplete);
        Assert.Equal(rows, scene.Rows);
        Assert.Equal(["本文です"], scene.Candidates);
    }

    [Fact]
    public void Script_protocol_round_trips_ids_and_runtime_tokens()
    {
        NovelScenarioSnapshot scene = NovelScenarioCandidateCollector.Capture(
            [
                ["charaload", "vespera", "ヴェスペラ"],
                ["message", "ヴェスペラ", "おかえり、<user><br>待っていたぞ。", "voice_001"],
                ["select", "next", "続ける"],
            ],
            isComplete: true
        );
        var batch = new NovelScriptTranslationBatch(
            "hmr_11120100011",
            scene,
            [
                new NovelScriptTranslationTarget(
                    "おかえり、<user><br>待っていたぞ。",
                    "おかえり、<user><br>待っていたぞ。"
                ),
                new NovelScriptTranslationTarget("続ける", "続ける"),
            ]
        );

        using JsonDocument request = JsonDocument.Parse(batch.Prompt);
        JsonElement root = request.RootElement;
        Assert.Equal("hmr_11120100011", root.GetProperty("sceneId").GetString());
        Assert.Equal("charaload", root.GetProperty("scene")[0].GetProperty("fields")[0].GetString());
        Assert.Equal("ヴェスペラ", root.GetProperty("scene")[1].GetProperty("fields")[1].GetString());
        Assert.Equal(2, root.GetProperty("targets").GetArrayLength());
        Assert.Contains(
            "__ABYSS_TOKEN_0__",
            root.GetProperty("targets")[0].GetProperty("source").GetString()
        );

        const string response = """
            {
              "version": 1,
              "translations": [
                { "id": "t0000", "text": "欢迎回来，__ABYSS_TOKEN_0____ABYSS_TOKEN_1__我一直在等你。" },
                { "id": "t0001", "text": "继续" }
              ]
            }
            """;

        Assert.True(batch.TryParseResponse(response, out var translations));
        Assert.Equal(
            "欢迎回来，<user><br>我一直在等你。",
            translations["おかえり、<user><br>待っていたぞ。"]
        );
        Assert.Equal("继续", translations["続ける"]);
    }

    [Fact]
    public void Script_protocol_rejects_missing_or_changed_structure()
    {
        NovelScenarioSnapshot scene = NovelScenarioCandidateCollector.Capture(
            [["message", "ヴェスペラ", "おかえり、<user>"]],
            isComplete: true
        );
        var batch = new NovelScriptTranslationBatch(
            "scene",
            scene,
            [new NovelScriptTranslationTarget("おかえり、<user>", "おかえり、<user>")]
        );

        Assert.False(batch.TryParseResponse(
            """{"version":1,"translations":[]}""",
            out _,
            out string countReason
        ));
        Assert.Equal("count-mismatch expected=1 actual=0", countReason);
        Assert.False(batch.TryParseResponse(
            """{"version":1,"translations":[{"id":"wrong","text":"欢迎回来"}]}""",
            out _,
            out string idReason
        ));
        Assert.Equal("id-mismatch index=0 expected=t0000 actual=wrong", idReason);
        Assert.False(batch.TryParseResponse(
            """{"version":1,"translations":[{"id":"t0000","text":"欢迎回来"}]}""",
            out _,
            out string tokenReason
        ));
        Assert.Equal("token-mismatch id=t0000", tokenReason);
        Assert.False(batch.TryParseResponse("not-json", out _, out string jsonReason));
        Assert.Equal("invalid-json", jsonReason);
    }

    [Fact]
    public void Script_protocol_normalizes_user_and_line_breaks_without_changing_cache_key()
    {
        const string source = "おかえり、%user%\n待っていたぞ。";
        NovelScenarioSnapshot scene = NovelScenarioCandidateCollector.Capture(
            [["message", "ヴェスペラ", source]],
            isComplete: true
        );
        var batch = new NovelScriptTranslationBatch(
            "scene",
            scene,
            [new NovelScriptTranslationTarget(source, source)]
        );

        using JsonDocument request = JsonDocument.Parse(batch.Prompt);
        Assert.Equal(
            "おかえり、<user><br>待っていたぞ。",
            request.RootElement.GetProperty("scene")[0].GetProperty("fields")[2].GetString()
        );
        Assert.Equal(
            "おかえり、__ABYSS_TOKEN_0____ABYSS_TOKEN_1__待っていたぞ。",
            request.RootElement.GetProperty("targets")[0].GetProperty("source").GetString()
        );

        Assert.True(batch.TryParseResponse(
            """{"version":1,"translations":[{"id":"t0000","text":"欢迎回来，__ABYSS_TOKEN_0____ABYSS_TOKEN_1__我一直在等你。"}]}""",
            out var translations
        ));
        Assert.Equal("欢迎回来，<user><br>我一直在等你。", translations[source]);
    }

    [Fact]
    public void Scene_coalescer_keeps_the_largest_complete_capture_until_the_latest_generation()
    {
        var coalescer = new NovelScriptSceneCoalescer();
        NovelScenarioSnapshot shortScene = NovelScenarioCandidateCollector.Capture(
            [["message", "A", "短い"]],
            isComplete: true
        );
        NovelScenarioSnapshot fullScene = NovelScenarioCandidateCollector.Capture(
            [
                ["message", "A", "短い"],
                ["message", "B", "長い場面です"],
            ],
            isComplete: true
        );
        NovelScenarioSnapshot largerButIncomplete = NovelScenarioCandidateCollector.Capture(
            [
                ["message", "A", "短い"],
                ["message", "B", "長い場面です"],
                ["message", "C", "未完成です"],
            ],
            isComplete: false
        );

        int generation1 = coalescer.Submit("scene", shortScene, shortScene.Candidates, out _);
        int generation2 = coalescer.Submit("scene", fullScene, fullScene.Candidates, out _);
        int generation3 = coalescer.Submit(
            "scene",
            largerButIncomplete,
            largerButIncomplete.Candidates,
            out int selectedRows
        );

        Assert.False(coalescer.TryTake("scene", generation1, out _));
        Assert.False(coalescer.TryTake("scene", generation2, out _));
        Assert.True(coalescer.TryTake("scene", generation3, out var work));
        Assert.NotNull(work);
        Assert.Equal(2, selectedRows);
        Assert.True(work.Scene.IsComplete);
        Assert.Equal(fullScene.Candidates, work.Candidates);
    }

    [Theory]
    [InlineData(NovelMachineTranslationMode.Script, "openai", true, true)]
    [InlineData(NovelMachineTranslationMode.Script, "ollama", true, true)]
    [InlineData(NovelMachineTranslationMode.Sentence, "openai", true, false)]
    [InlineData(NovelMachineTranslationMode.Script, "sugoi", true, false)]
    [InlineData(NovelMachineTranslationMode.Script, "libre", true, false)]
    [InlineData(NovelMachineTranslationMode.Script, "openai", false, false)]
    public void Script_mode_requires_a_complete_scene_and_chat_engine(
        NovelMachineTranslationMode mode,
        string engine,
        bool complete,
        bool expected
    )
    {
        Assert.Equal(expected, NovelScriptTranslationProtocol.CanUse(mode, engine, complete));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(3, 4)]
    [InlineData(5, 6)]
    public void Script_mode_retries_at_least_three_times_before_sentence_fallback(
        int configuredRetries,
        int expectedAttempts
    )
    {
        Assert.Equal(
            expectedAttempts,
            NovelScriptTranslationProtocol.GetMaximumAttempts(configuredRetries)
        );
    }

    [Fact]
    public void Script_retry_prompt_carries_the_last_rejection_reason_and_full_scene()
    {
        const string scenePrompt = "{\"version\":1,\"scene\":[]}";

        Assert.Equal(
            scenePrompt,
            NovelScriptTranslationProtocol.BuildRetryPrompt(scenePrompt, previousFailure: null)
        );
        string retryPrompt = NovelScriptTranslationProtocol.BuildRetryPrompt(
            scenePrompt,
            "token-mismatch id=t0042"
        );
        Assert.Contains("token-mismatch id=t0042", retryPrompt);
        Assert.EndsWith(scenePrompt, retryPrompt);
    }

    [Fact]
    public void Script_retry_registry_survives_restart_until_scene_translation_succeeds()
    {
        var registry = new NovelScriptRetryRegistry();
        Assert.True(registry.Mark("hmr_11120100031"));

        Assert.True(NovelScriptRetryRegistry.TryDeserialize(
            registry.Serialize(),
            out var restored
        ));
        Assert.True(restored.Contains("hmr_11120100031"));
        Assert.True(restored.Complete("hmr_11120100031"));
        Assert.False(restored.Contains("hmr_11120100031"));
    }
}
