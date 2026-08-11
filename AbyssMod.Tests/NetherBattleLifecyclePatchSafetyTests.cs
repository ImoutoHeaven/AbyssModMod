#nullable enable

using System.Text.RegularExpressions;
using Xunit;

namespace AbyssMod.Tests;

/// <summary>
/// Guards the IL2CPP/Harmony ABI boundary that cannot be executed inside the pure test host.
/// Harmony DMD wrappers for the concrete NetherAPIService task methods crash before their
/// postfixes run. Start is proven safe on the preserve wrapper, while clear/close completion
/// must be observed only after BattleResultUtility has awaited the native request.
/// </summary>
public sealed class NetherBattleLifecyclePatchSafetyTests
{
    [Fact]
    public void Clear_and_close_are_not_patched_at_the_crash_prone_preserve_wrapper()
    {
        string root = FindRepositoryRoot();
        string autoClimbPatch = File.ReadAllText(
            Path.Combine(root, "AbyssMod", "Patches", "NetherAutoClimbPatch.cs")
        );
        string battleSessionPatch = File.ReadAllText(
            Path.Combine(root, "AbyssMod", "Patches", "BattleSessionAutoSLPatch.cs")
        );
        string manager = File.ReadAllText(
            Path.Combine(root, "AbyssMod", "Patches", "PatchManager.cs")
        );
        string settlementPatch = File.ReadAllText(
            Path.Combine(root, "AbyssMod", "Patches", "BattleSettlementPayloadProbePatch.cs")
        );
        string bridge = File.ReadAllText(
            Path.Combine(root, "AbyssMod", "Services", "NetherRuntimeBridge.cs")
        );
        string plugin = File.ReadAllText(
            Path.Combine(root, "AbyssMod", "Core", "Plugin.cs")
        );

        Assert.DoesNotContain("NetherAutoClimbBattleStartLifecyclePatch", autoClimbPatch);
        Assert.DoesNotContain("NetherAutoClimbBattleClearLifecyclePatch", autoClimbPatch);
        Assert.DoesNotContain("NetherAutoClimbBattleCloseLifecyclePatch", autoClimbPatch);
        Assert.DoesNotContain("GetBattleTaskPatchTarget", autoClimbPatch);
        Assert.DoesNotContain("NetherAutoClimbBattleStartLifecyclePatch", manager);
        Assert.DoesNotContain("NetherAutoClimbBattleClearLifecyclePatch", manager);
        Assert.DoesNotContain("NetherAutoClimbBattleCloseLifecyclePatch", manager);

        Assert.Contains(
            "Project_Ingame_Exploration_IExplorationQuestAPIService_StartQuestAsync",
            battleSessionPatch
        );
        Assert.DoesNotContain(
            "Project_Ingame_Exploration_IExplorationQuestAPIService_ClearQuestAsync",
            battleSessionPatch
        );
        Assert.DoesNotContain(
            "Project_Ingame_Exploration_IExplorationQuestAPIService_CloseQuestAsync",
            battleSessionPatch
        );
        Assert.Single(
            Regex.Matches(
                battleSessionPatch,
                Regex.Escape("NetherRuntimeBridge.ObserveBattleStartTask(__result)")
            ).Cast<Match>()
        );
        Assert.DoesNotContain("NetherRuntimeBridge.ObserveBattleClearTask", battleSessionPatch);
        Assert.DoesNotContain("NetherRuntimeBridge.ObserveBattleCloseTask", battleSessionPatch);
        Assert.Contains("ref UniTask<BattleSessionStatusResponseEntity> __result", battleSessionPatch);
        Assert.DoesNotContain("ref UniTask<IFinishQuestResponseEntity> __result", battleSessionPatch);

        Assert.Contains("BattleResultUtility.CreateBattleResultModel", settlementPatch);
        Assert.Contains("NetherBattleTerminalObservationPolicy.Classify", settlementPatch);
        Assert.Contains("NetherRuntimeBridge.ObserveBattleClear()", settlementPatch);
        Assert.Contains("NetherRuntimeBridge.ObserveBattleClose()", settlementPatch);
        Assert.Contains("NetherAutoClimbBattleResultLifecyclePatch", autoClimbPatch);
        Assert.Single(
            Regex.Matches(
                manager,
                Regex.Escape(
                    "Harmony.CreateAndPatchAll(typeof(NetherAutoClimbBattleResultLifecyclePatch));"
                )
            ).Cast<Match>()
        );

        Assert.Contains(
            "foreach (NetherInteropPatchBinding binding in NetherLifecycleInteropBindings.All)",
            bridge
        );
        Assert.DoesNotContain(
            "NetherLifecycleInteropBindings.All.Concat(BattlePatchBindings)",
            bridge
        );
        Assert.DoesNotContain("BattlePatchBindings", bridge);
        Assert.DoesNotContain("GetBattleTaskPatchTarget", bridge);
        Assert.Contains("autonether-session-depth-cap-v27", plugin);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AbyssMod.Tests", "AbyssMod.Tests.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
