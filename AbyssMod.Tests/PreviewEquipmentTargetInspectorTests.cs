using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class PreviewEquipmentTargetInspectorTests
{
    private static PreviewEquipmentTargetSnapshot ForestCloak() =>
        new(
            BattleSessionAutoSLPolicy.ArmorContentType,
            23010440,
            "森林披风",
            3004,
            4,
            [4]
        );

    [Fact]
    public void Snapshot_formats_canonical_toast_and_structured_log_output()
    {
        PreviewEquipmentTargetSnapshot snapshot = ForestCloak();

        Assert.Equal("Armor:23010440", snapshot.Token);
        Assert.Equal("Armor:23010440\n森林披风 | Rank 4", snapshot.ToastBody);
        Assert.Equal(
            "token=Armor:23010440 contentType=80 contentId=23010440 groupNo=3004 rank=4 rarities=4 name=森林披风",
            snapshot.LogFields
        );
    }

    [Fact]
    public void Resolved_snapshot_recommends_family_at_or_above_and_lists_rank_four_and_five_ids()
    {
        var index = new NormalEquipmentMasterIndex(
            [
                new(80, 23010340, 3004, 3, 4, "森林披风"),
                new(80, 23010440, 3004, 4, 4, "森林披风"),
                new(80, 23010540, 3004, 5, 4, "森林披风"),
            ]
        );
        var snapshot = new PreviewEquipmentTargetSnapshot(
            80,
            23010440,
            "森林披风",
            3004,
            4,
            [4],
            index
        );

        Assert.Equal("Armor:23010440+", snapshot.RecommendedToken);
        Assert.Equal(
            "Armor:23010440+\n森林披风 | Rank 4+\n接受: R4=23010440, R5=23010540",
            snapshot.ToastBody
        );
        Assert.Contains("familyToken=Armor:23010440+", snapshot.LogFields);
        Assert.Contains("familyGroupNo=3004", snapshot.LogFields);
        Assert.Contains("familyRarity=4", snapshot.LogFields);
        Assert.Contains("minimumRank=4", snapshot.LogFields);
        Assert.Contains("members=R4:23010440|R5:23010540", snapshot.LogFields);
    }

    [Fact]
    public void Family_resolution_rejects_preview_metadata_that_disagrees_with_master_data()
    {
        var index = new NormalEquipmentMasterIndex(
            [new NormalEquipmentMasterInfo(80, 23010440, 3004, 4, 4, "森林披风")]
        );
        var snapshot = new PreviewEquipmentTargetSnapshot(
            80,
            23010440,
            "森林披风",
            9999,
            4,
            [4],
            index
        );

        Assert.Equal("Armor:23010440", snapshot.RecommendedToken);
        Assert.Contains("familyError=preview-family-master-mismatch", snapshot.LogFields);
        Assert.Contains("族系不可用: preview-family-master-mismatch", snapshot.ToastBody);
    }

    [Fact]
    public void Explicit_catalog_failure_keeps_the_exact_token_and_surfaces_the_reason()
    {
        var snapshot = new PreviewEquipmentTargetSnapshot(
            80,
            23010440,
            "森林披风",
            3004,
            4,
            [4],
            null,
            "missing-m-armors-cache"
        );

        Assert.Equal("Armor:23010440", snapshot.RecommendedToken);
        Assert.Contains("族系不可用: missing-m-armors-cache", snapshot.ToastBody);
        Assert.Contains("familyError=missing-m-armors-cache", snapshot.LogFields);
    }

    [Fact]
    public void Matching_quest_preview_intent_registers_one_active_popup()
    {
        var inspector = new PreviewEquipmentTargetInspector();
        var popup = new object();

        Assert.True(inspector.RecordQuestPreviewIntent(80, 23010440, 10));
        Assert.True(inspector.TryRegisterPopup(popup, ForestCloak(), 10.5, _ => true));
        Assert.True(inspector.TryGetActive(out PreviewEquipmentTargetSnapshot snapshot));
        Assert.Equal("Armor:23010440", snapshot.Token);

        Assert.False(inspector.TryRegisterPopup(new object(), ForestCloak(), 10.6, _ => true));
    }

    [Fact]
    public void Identical_popup_from_a_non_quest_origin_is_ignored()
    {
        var inspector = new PreviewEquipmentTargetInspector();

        Assert.False(inspector.TryRegisterPopup(new object(), ForestCloak(), 1, _ => true));
        Assert.False(inspector.TryGetActive(out _));
    }

    [Fact]
    public void Mismatch_consumes_the_intent_and_cannot_be_reused_later()
    {
        var inspector = new PreviewEquipmentTargetInspector();
        inspector.RecordQuestPreviewIntent(80, 23010440, 1);
        var otherArmor = new PreviewEquipmentTargetSnapshot(80, 999, "Other", 1, 4, [4]);

        Assert.False(inspector.TryRegisterPopup(new object(), otherArmor, 1.1, _ => true));
        Assert.False(inspector.TryRegisterPopup(new object(), ForestCloak(), 1.2, _ => true));
    }

    [Fact]
    public void Newer_intent_replaces_the_previous_target()
    {
        var inspector = new PreviewEquipmentTargetInspector();
        inspector.RecordQuestPreviewIntent(80, 111, 1);
        inspector.RecordQuestPreviewIntent(80, 23010440, 1.1);

        Assert.True(inspector.TryRegisterPopup(new object(), ForestCloak(), 1.2, _ => true));
    }

    [Fact]
    public void Expired_intent_is_rejected_and_cleared()
    {
        var inspector = new PreviewEquipmentTargetInspector(intentLifetimeSeconds: 2);
        inspector.RecordQuestPreviewIntent(80, 23010440, 1);

        Assert.False(inspector.TryRegisterPopup(new object(), ForestCloak(), 3.01, _ => true));
        Assert.False(inspector.TryRegisterPopup(new object(), ForestCloak(), 3.02, _ => true));
    }

    [Fact]
    public void Default_intent_window_allows_a_slow_five_second_preview_load()
    {
        var inspector = new PreviewEquipmentTargetInspector();
        inspector.RecordQuestPreviewIntent(80, 23010440, 1);

        Assert.True(inspector.TryRegisterPopup(new object(), ForestCloak(), 6, _ => true));
    }

    [Fact]
    public void Unsupported_content_click_clears_an_older_valid_intent()
    {
        var inspector = new PreviewEquipmentTargetInspector();
        inspector.RecordQuestPreviewIntent(80, 23010440, 1);

        Assert.False(inspector.RecordQuestPreviewIntent(30, 55, 1.1));
        Assert.False(inspector.TryRegisterPopup(new object(), ForestCloak(), 1.2, _ => true));
    }

    [Fact]
    public void Inactive_or_destroyed_popup_is_cleared_before_F6_can_read_it()
    {
        var inspector = new PreviewEquipmentTargetInspector();
        var popup = new object();
        bool active = true;
        inspector.RecordQuestPreviewIntent(80, 23010440, 1);
        inspector.TryRegisterPopup(popup, ForestCloak(), 1.1, _ => active);

        active = false;

        Assert.False(inspector.TryGetActive(out _));
        active = true;
        Assert.False(inspector.TryGetActive(out _));
    }

    [Fact]
    public void Liveness_exception_fails_closed_and_clears_the_popup()
    {
        var inspector = new PreviewEquipmentTargetInspector();
        inspector.RecordQuestPreviewIntent(80, 23010440, 1);
        inspector.TryRegisterPopup(
            new object(),
            ForestCloak(),
            1.1,
            _ => throw new InvalidOperationException("destroyed")
        );

        Assert.False(inspector.TryGetActive(out _));
    }

    [Fact]
    public void Binding_catalog_wraps_only_known_quest_preview_view_initializers()
    {
        string[] types = QuestPreviewBindingCatalog.Bindings
            .Select(binding => binding.TypeName)
            .ToArray();

        Assert.Contains(
            "Project.MainStory.ExplorationQuestDetail.ExplorationQuestDetailPopup",
            types
        );
        Assert.Contains("Project.MainStory.StaminaQuestDetailPopup", types);
        Assert.Contains("Project.StoryEvent.StoryEventQuestDetailPopup", types);
        Assert.Contains("Project.Disaster.DisasterQuestDetailPopup", types);
        Assert.Contains("Project.UnionRequest.UnionRequestDetailPopup", types);
        Assert.Contains("Project.UnionRequest.UnionRequestContentView", types);
        Assert.DoesNotContain(
            types,
            type => type.EndsWith("PopupController", StringComparison.Ordinal)
        );
        Assert.DoesNotContain("Project.UnionRequest.Top.SubViewController", types);
        Assert.DoesNotContain("Project.Outgame.ContentDetailPopupService", types);
        Assert.DoesNotContain("Project.Outgame.EquipmentDropPopupController", types);
        Assert.Equal(types.Length, types.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Binding_catalog_identifies_the_exact_content_callback_argument()
    {
        Assert.All(
            QuestPreviewBindingCatalog.Bindings,
            binding =>
            {
                Assert.Contains(binding.MethodName, new[] { "Initialize", "InitializeView" });
                Assert.InRange(binding.ActionParameterIndex, 0, 3);
            }
        );

        Assert.Contains(
            QuestPreviewBindingCatalog.Bindings,
            binding => binding.TypeName == "Project.Disaster.DisasterQuestDetailPopup"
                && binding.MethodName == "InitializeView"
                && binding.ActionParameterIndex == 0
        );
        Assert.Contains(
            QuestPreviewBindingCatalog.Bindings,
            binding => binding.TypeName == "Project.MiningEvent.MiningEventQuestDetailPopup"
                && binding.MethodName == "Initialize"
                && binding.ActionParameterIndex == 3
        );
    }

    [Fact]
    public void Idle_exploration_drop_preview_uses_its_direct_thumbnail_opening_path()
    {
        DirectQuestPreviewBindingDescriptor binding = Assert.Single(
            DirectQuestPreviewBindingCatalog.Bindings
        );

        Assert.Equal(
            "Project.IdleExploration.EncounterQuestList.SubViewController",
            binding.TypeName
        );
        Assert.Equal("OpenContentDetailPopupAsync", binding.MethodName);
        Assert.Equal("Project.Outgame.UI.DropThumbnailModel", binding.ParameterTypeName);
    }
}
