using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AbyssMod.Tests;

/// <summary>
/// Keeps the exact code-popup lifecycle observers in the production Harmony registration
/// list.  Removing either registration makes the generated native task invisible to F12, so a
/// static source contract is intentional here: the test project cannot instantiate IL2CPP/Harmony
/// at test time, whereas the packaged resolver tests characterize the actual targets separately.
/// </summary>
public class NetherCodeLifecyclePatchRegistrationTests
{
    [Fact]
    public void Patch_manager_registers_each_code_lifecycle_patch_exactly_once_in_observer_order()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "AbyssMod", "Patches", "PatchManager.cs"));

        const string selection = "Harmony.CreateAndPatchAll(typeof(NetherAutoClimbCodeSelectionLifecyclePatch));";
        const string keepCancel = "Harmony.CreateAndPatchAll(typeof(NetherAutoClimbCodeKeepCancelLifecyclePatch));";
        const string transform = "Harmony.CreateAndPatchAll(typeof(NetherAutoClimbCodeTransformLifecyclePatch));";
        Assert.Single(Regex.Matches(source, Regex.Escape(selection)).Cast<Match>());
        Assert.Single(Regex.Matches(source, Regex.Escape(keepCancel)).Cast<Match>());
        Assert.Single(Regex.Matches(source, Regex.Escape(transform)).Cast<Match>());
        Assert.True(source.IndexOf(selection, StringComparison.Ordinal) < source.IndexOf(keepCancel, StringComparison.Ordinal));
        Assert.True(source.IndexOf(keepCancel, StringComparison.Ordinal) < source.IndexOf(transform, StringComparison.Ordinal));
    }

    [Fact]
    public void Lifecycle_patch_targets_delegate_to_the_same_versioned_packaged_bindings()
    {
        string root = FindRepositoryRoot();
        string bridge = File.ReadAllText(Path.Combine(root, "AbyssMod", "Services", "NetherRuntimeBridge.cs"));
        string patch = File.ReadAllText(Path.Combine(root, "AbyssMod", "Patches", "NetherAutoClimbPatch.cs"));

        string confirmTarget = ExtractMethod(bridge, "internal static MethodBase? GetCodeSelectionTaskPatchTarget()");
        string cancelTarget = ExtractMethod(bridge, "internal static MethodBase? GetCodeKeepCancelTaskPatchTarget()");
        string transformTarget = ExtractMethod(bridge, "internal static MethodBase? GetCodeTransformTaskPatchTarget()");
        Assert.Contains("NetherCodePopupInteropResolver.TryResolveStaticMethod", confirmTarget);
        Assert.Contains("NetherCodePopupNativeBinding.ConfirmTaskBinding", confirmTarget);
        Assert.DoesNotContain("System.Threading.CancellationToken", confirmTarget);
        Assert.Contains("NetherCodePopupInteropResolver.TryResolveStaticMethod", cancelTarget);
        Assert.Contains("NetherCodePopupNativeBinding.CancelTaskBinding", cancelTarget);
        Assert.DoesNotContain("System.Threading.CancellationToken", cancelTarget);
        Assert.Contains("NetherCodePopupInteropResolver.TryResolveStaticMethod", transformTarget);
        Assert.Contains("NetherCodeTransformNativeBinding.TransformTaskBinding", transformTarget);
        Assert.DoesNotContain("System.Threading.CancellationToken", transformTarget);

        Assert.Contains(
            "TargetMethod() => NetherRuntimeBridge.GetCodeSelectionTaskPatchTarget()",
            patch
        );
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
