#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace AbyssMod.Patches;

/// <summary>
/// Harmony 补丁管理器。启动前先校验 <see cref="PatchInventory"/> 中登记的全部补丁目标，
/// 通过后才安装补丁；任何目标缺失都会逐项记录并中止加载，避免出现半安装状态。
/// </summary>
public static class PatchManager
{
    /// <summary>启动前 precheck 结果。Failures 为空时才会进入实际安装。</summary>
    public sealed class PreflightOutcome
    {
        internal PreflightOutcome(
            int classCount,
            int siteCount,
            IReadOnlyList<string> failures,
            IReadOnlyList<MethodBase> resolvedTargets
        )
        {
            ClassCount = classCount;
            SiteCount = siteCount;
            Failures = failures;
            ResolvedTargets = resolvedTargets;
        }

        public int ClassCount { get; }
        public int SiteCount { get; }
        public IReadOnlyList<string> Failures { get; }
        public IReadOnlyList<MethodBase> ResolvedTargets { get; }
        public bool Ok => Failures.Count == 0;
    }

    /// <summary>
    /// 解析全部补丁目标：注解声明的目标用声明类型 + 方法名精确解析，
    /// 目录驱动的目标通过各自的 TargetMethods/TargetMethod 实际执行一次。
    /// </summary>
    public static PreflightOutcome Preflight()
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

        PatchPreflightPolicy.Report attributeReport = PatchPreflightPolicy.Check(
            PatchInventory.AttributeSpecs(),
            assemblies
        );

        var failures = new List<string>(attributeReport.Failures);
        var resolved = new List<MethodBase>(attributeReport.Targets);

        foreach (Type patchClass in PatchInventory.DynamicTargetClasses)
        {
            var dynamicTargets = new List<MethodBase?>();
            try
            {
                dynamicTargets.AddRange(RunDynamicTargetProvider(patchClass, "TargetMethods"));
                dynamicTargets.AddRange(RunDynamicTargetProvider(patchClass, "TargetMethod"));
            }
            catch (Exception exception)
            {
                failures.Add(
                    patchClass.Name + " => " + PatchPreflightPolicy.TargetError + ":"
                        + PatchPreflightPolicy.Describe(exception)
                );
                continue;
            }

            failures.AddRange(
                PatchPreflightPolicy.CheckResolvedLikeTargets(patchClass.Name, dynamicTargets)
            );

            if (dynamicTargets.Count == 0)
            {
                // 目录为空意味着补丁装不上去，与“目标缺失”同类，必须视为失败。
                failures.Add(patchClass.Name + " => " + PatchPreflightPolicy.MissingMethod + ":empty-target-set");
                continue;
            }

            foreach (MethodBase? target in dynamicTargets)
            {
                if (target != null && !resolved.Contains(target))
                    resolved.Add(target);
            }
        }

        return new PreflightOutcome(
            PatchInventory.PatchClasses.Count,
            attributeReport.SiteCount,
            failures,
            resolved
        );
    }

    /// <summary>创建并注册所有 Harmony 补丁。只在 Preflight 通过后调用。</summary>
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

    private static IEnumerable<MethodBase?> RunDynamicTargetProvider(Type patchClass, string name)
    {
        MethodInfo? provider = patchClass.GetMethod(
            name,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
        );
        if (provider == null)
            return Array.Empty<MethodBase?>();

        object? result = provider.Invoke(null, null);
        return result switch
        {
            IEnumerable<MethodBase> sequence => sequence.Cast<MethodBase?>(),
            MethodBase single => new MethodBase?[] { single },
            _ => Array.Empty<MethodBase?>(),
        };
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
