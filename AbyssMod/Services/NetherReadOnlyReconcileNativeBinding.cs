#nullable enable

namespace AbyssMod.Services;

/// <summary>
/// Exact public packaged-client binding for the datastore-owned no-Start refresh chain.
/// </summary>
internal static class NetherReadOnlyReconcileNativeBinding
{
    public const string DataStoreTypeName = "Project.User.NetherDataStore";

    public static NetherNativeMethodDescriptor SyncDescriptor { get; } = new(
        "SyncNetherDataAsync",
        new[] { "Il2CppSystem.Threading.CancellationToken" },
        "Cysharp.Threading.Tasks.UniTask"
    );
}
