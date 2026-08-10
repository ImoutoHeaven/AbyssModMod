using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AbyssMod.Tests;

/// <summary>
/// Keeps only the lifecycle observers that cannot be captured from their initiating call.
/// Selection owns the exact returned UniTask directly; registering a Harmony observer for that
/// same task creates a reflection -&gt; native callback -&gt; DMD -&gt; native re-entry chain.
/// </summary>
public class NetherCodeLifecyclePatchRegistrationTests
{
    [Fact]
    public void Patch_manager_does_not_detour_directly_owned_selection_task()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "AbyssMod", "Patches", "PatchManager.cs"));

        const string selection = "Harmony.CreateAndPatchAll(typeof(NetherAutoClimbCodeSelectionLifecyclePatch));";
        const string keepCancel = "Harmony.CreateAndPatchAll(typeof(NetherAutoClimbCodeKeepCancelLifecyclePatch));";
        const string transform = "Harmony.CreateAndPatchAll(typeof(NetherAutoClimbCodeTransformLifecyclePatch));";
        Assert.Empty(Regex.Matches(source, Regex.Escape(selection)).Cast<Match>());
        Assert.Single(Regex.Matches(source, Regex.Escape(keepCancel)).Cast<Match>());
        Assert.Single(Regex.Matches(source, Regex.Escape(transform)).Cast<Match>());
        Assert.True(source.IndexOf(keepCancel, StringComparison.Ordinal) < source.IndexOf(transform, StringComparison.Ordinal));
    }

    [Fact]
    public void Patch_manager_registers_exact_start_status_state_machine_observer()
    {
        string root = FindRepositoryRoot();
        string manager = File.ReadAllText(Path.Combine(root, "AbyssMod", "Patches", "PatchManager.cs"));
        string patch = File.ReadAllText(Path.Combine(root, "AbyssMod", "Patches", "NetherAutoClimbPatch.cs"));

        const string registration =
            "Harmony.CreateAndPatchAll(typeof(NetherAutoClimbStartStatusLifecyclePatch));";
        Assert.Single(Regex.Matches(manager, Regex.Escape(registration)).Cast<Match>());
        Assert.Contains("GetStartStatusStateMachinePatchTarget()", patch);
        Assert.Contains("ObserveStartStatusStateMachineEnter(__instance)", patch);
        Assert.Contains("ObserveStartStatusStateMachineExit(__instance)", patch);
    }

    [Fact]
    public void Lifecycle_patch_targets_delegate_to_the_same_versioned_packaged_bindings()
    {
        string root = FindRepositoryRoot();
        string bridge = File.ReadAllText(Path.Combine(root, "AbyssMod", "Services", "NetherRuntimeBridge.cs"));
        string patch = File.ReadAllText(Path.Combine(root, "AbyssMod", "Patches", "NetherAutoClimbPatch.cs"));

        string cancelTarget = ExtractMethod(bridge, "internal static MethodBase? GetCodeKeepCancelTaskPatchTarget()");
        string transformTarget = ExtractMethod(bridge, "internal static MethodBase? GetCodeTransformTaskPatchTarget()");
        Assert.DoesNotContain("GetCodeSelectionTaskPatchTarget", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("NetherAutoClimbCodeSelectionLifecyclePatch", patch, StringComparison.Ordinal);
        Assert.Contains("NetherCodeConfirmTaskInvoker.TryInvoke", bridge, StringComparison.Ordinal);
        Assert.Contains("NetherCodePopupInteropResolver.TryResolveStaticMethod", cancelTarget);
        Assert.Contains("NetherCodePopupNativeBinding.CancelTaskBinding", cancelTarget);
        Assert.DoesNotContain("System.Threading.CancellationToken", cancelTarget);
        Assert.Contains("NetherCodePopupInteropResolver.TryResolveStaticMethod", transformTarget);
        Assert.Contains("NetherCodeTransformNativeBinding.TransformTaskBinding", transformTarget);
        Assert.DoesNotContain("System.Threading.CancellationToken", transformTarget);

        Assert.Contains(
            "TargetMethod() => NetherRuntimeBridge.GetCodeKeepCancelTaskPatchTarget()",
            patch
        );
        Assert.Contains(
            "TargetMethod() => NetherRuntimeBridge.GetCodeTransformTaskPatchTarget()",
            patch
        );
    }

    private static string ExtractMethod(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing method: " + signature);
        int next = source.IndexOf("\n    internal static", start + signature.Length, StringComparison.Ordinal);
        Assert.True(next > start, "unable to bound method: " + signature);
        return source.Substring(start, next - start);
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
