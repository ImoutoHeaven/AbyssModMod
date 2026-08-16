using HarmonyLib;

namespace AbyssMod.Patches;

/// <summary>
/// Harmony 补丁管理器。负责初始化所有子补丁类、提供共享工具方法。
/// </summary>
public static class PatchManager
{
    /// <summary>当前加载的剧情 Novel ID。</summary>
    public static string NovelId = string.Empty;

    /// <summary>
    /// 创建并注册所有 Harmony 补丁。
    /// </summary>
    public static void Initialize()
    {
        Harmony.CreateAndPatchAll(typeof(EnhancePatch));
        if (Plugin.Images?.Enabled == true)
            Harmony.CreateAndPatchAll(typeof(ImageReplacementPatch));
        Harmony.CreateAndPatchAll(typeof(TranslationPatch));
        Harmony.CreateAndPatchAll(typeof(ItemPatch));
        Harmony.CreateAndPatchAll(typeof(BattleSessionAutoSLPatch));
        Harmony.CreateAndPatchAll(typeof(TavernFirstCardAutoSLPatch));
        Harmony.CreateAndPatchAll(typeof(BattleSettlementPayloadProbePatch));
        InstallQuestPreviewEquipmentCallbackPatches();
        Harmony.CreateAndPatchAll(typeof(IdleExplorationQuestPreviewEquipmentDirectPatch));
        Harmony.CreateAndPatchAll(typeof(EventQuestPreviewContextPatch));
        Harmony.CreateAndPatchAll(typeof(QuestPreviewEquipmentDropPopupPatch));
        Harmony.CreateAndPatchAll(typeof(GeneralTextPatch));
        Harmony.CreateAndPatchAll(typeof(MasterDataTranslationPatch));
#if DEBUG
        Harmony.CreateAndPatchAll(typeof(DebugPatch));
#endif
    }

    private static void InstallQuestPreviewEquipmentCallbackPatches()
    {
        foreach (
            int actionParameterIndex in Services.QuestPreviewHarmonyPatchPlan.ActionParameterIndices
        )
        {
            switch (actionParameterIndex)
            {
                case 0:
                    Harmony.CreateAndPatchAll(typeof(QuestPreviewEquipmentCallback0Patch));
                    break;
                case 1:
                    Harmony.CreateAndPatchAll(typeof(QuestPreviewEquipmentCallback1Patch));
                    break;
                case 2:
                    Harmony.CreateAndPatchAll(typeof(QuestPreviewEquipmentCallback2Patch));
                    break;
                default:
                    Logger.Warn(
                        $"[F6][EquipmentTarget][Binding] outcome=skipped-unsupported-callback-index "
                            + $"index={actionParameterIndex}"
                    );
                    break;
            }
        }
    }
}
