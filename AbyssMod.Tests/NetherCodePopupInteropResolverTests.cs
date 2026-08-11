#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherCodePopupInteropResolverTests
{
    [Fact]
    public void Resolver_uses_public_sanitized_holder_property_and_callback_without_cpp2il_name_fallback()
    {
        string controllerName = typeof(SanitizedController).FullName!;

        bool resolved = NetherCodePopupInteropResolver.TryResolveGeneratedCallback(
            typeof(SanitizedController),
            FixtureConfirmBinding(controllerName),
            out string error,
            out object? singleton,
            out MethodInfo? method
        );

        Assert.True(resolved, error);
        Assert.Same(SanitizedController.__c.__9, singleton);
        Assert.Equal("_SetupPopupEvent_b__12_2", method!.Name);
    }

    [Fact]
    public void Resolver_accepts_only_an_exact_obfuscated_name_attribute_with_the_full_signature()
    {
        string controllerName = typeof(AttributeController).FullName!;

        bool resolved = NetherCodePopupInteropResolver.TryResolveGeneratedCallback(
            typeof(AttributeController),
            FixtureConfirmBinding(controllerName),
            out string error,
            out _,
            out MethodInfo? method
        );

        Assert.True(resolved, error);
        Assert.Equal("Method_Internal_Confirmed", method!.Name);
    }

    [Fact]
    public void Resolver_fails_closed_for_ambiguous_or_zero_exact_candidates()
    {
        string ambiguousName = typeof(AmbiguousController).FullName!;
        bool ambiguous = NetherCodePopupInteropResolver.TryResolveGeneratedCallback(
            typeof(AmbiguousController),
            FixtureConfirmBinding(ambiguousName),
            out string ambiguousError,
            out _,
            out _
        );

        bool zero = NetherCodePopupInteropResolver.TryResolveGeneratedCallback(
            typeof(MissingController),
            FixtureConfirmBinding(typeof(MissingController).FullName!),
            out string zeroError,
            out _,
            out _
        );

        Assert.False(ambiguous);
        Assert.Contains("ambiguous", ambiguousError, StringComparison.Ordinal);
        Assert.False(zero);
        Assert.Contains("no-exact", zeroError, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_rejects_system_threading_token_lookalike_for_packaged_cancel_task()
    {
        var expected = new NetherCodePopupInteropMethodBinding(
            "Method_Internal_Static_UniTask_AbyssCodeSelectPopupController_CancellationToken_0",
            null,
            new[] { typeof(SanitizedController).FullName!, "Il2CppSystem.Threading.CancellationToken" },
            typeof(FakeUniTask).FullName!
        ) { IsStatic = true };
        bool resolved = NetherCodePopupInteropResolver.TryResolveStaticMethod(
            typeof(SystemTokenUtility),
            expected,
            out string error,
            out _
        );

        Assert.False(resolved);
        Assert.Contains("no-exact", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_uses_unique_full_signature_when_generated_static_wrapper_name_changes()
    {
        var expected = new NetherCodePopupInteropMethodBinding(
            "OldGeneratedTaskName",
            "<LogicalGeneratedTask>",
            new[] { typeof(SanitizedController).FullName!, typeof(FakeCancellationToken).FullName! },
            typeof(FakeUniTask).FullName!
        ) { IsStatic = true };

        bool resolved = NetherCodePopupInteropResolver.TryResolveStaticMethod(
            typeof(RenamedTaskUtility),
            expected,
            out string error,
            out MethodInfo? method
        );

        Assert.True(resolved, error);
        Assert.Equal("NewGeneratedTaskName_PDM_0", method!.Name);
    }

    [Fact]
    public void Resolver_rejects_ambiguous_full_signature_fallback()
    {
        var expected = new NetherCodePopupInteropMethodBinding(
            "OldGeneratedTaskName",
            "<LogicalGeneratedTask>",
            new[] { typeof(SanitizedController).FullName!, typeof(FakeCancellationToken).FullName! },
            typeof(FakeUniTask).FullName!
        ) { IsStatic = true };

        bool resolved = NetherCodePopupInteropResolver.TryResolveStaticMethod(
            typeof(AmbiguousRenamedTaskUtility),
            expected,
            out string error,
            out MethodInfo? method
        );

        Assert.False(resolved);
        Assert.Null(method);
        Assert.Contains("ambiguous-exact-signature-fallback", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_requires_a_logical_generated_name_before_signature_fallback()
    {
        var expected = new NetherCodePopupInteropMethodBinding(
            "OldGeneratedTaskName",
            null,
            new[] { typeof(SanitizedController).FullName!, typeof(FakeCancellationToken).FullName! },
            typeof(FakeUniTask).FullName!
        ) { IsStatic = true };

        bool resolved = NetherCodePopupInteropResolver.TryResolveStaticMethod(
            typeof(RenamedTaskUtility),
            expected,
            out string error,
            out MethodInfo? method
        );

        Assert.False(resolved);
        Assert.Null(method);
        Assert.Contains("no-exact", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Packaged_interop_has_one_public_sanitized_holder_callbacks_and_task_targets()
    {
        using var packaged = PackagedProjectAssembly.Load();
        Type controller = packaged.RequireType(
            "Project.Nether.AbyssCodeSelectPopup.AbyssCodeSelectPopupController"
        );
        Type utility = packaged.RequireType("Project.Nether.NetherUtility");

        Assert.True(
            NetherCodePopupInteropResolver.TryResolveGeneratedCallbackTarget(
                controller,
                NetherCodePopupNativeBinding.ConfirmCallbackBinding(controller.FullName!),
                out string confirmError,
                out MemberInfo? confirmSingleton,
                out MethodInfo? confirm
            ),
            confirmError
        );
        Assert.True(
            NetherCodePopupInteropResolver.TryResolveGeneratedCallbackTarget(
                controller,
                NetherCodePopupNativeBinding.CancelCallbackBinding(controller.FullName!),
                out string cancelError,
                out MemberInfo? cancelSingleton,
                out MethodInfo? cancel
            ),
            cancelError
        );
        Assert.True(
            NetherCodePopupInteropResolver.TryResolveGeneratedCallbackTarget(
                controller,
                NetherCodePopupNativeBinding.DetailCallbackBinding(controller.FullName!),
                out string detailError,
                out MemberInfo? detailSingleton,
                out MethodInfo? detail
            ),
            detailError
        );
        Assert.NotNull(confirmSingleton);
        Assert.Same(confirmSingleton, cancelSingleton);
        Assert.Same(confirmSingleton, detailSingleton);
        Assert.Equal("__c", confirm!.DeclaringType!.Name);
        Assert.Equal("_SetupPopupEvent_b__12_2", confirm.Name);
        Assert.Equal("_SetupPopupEvent_b__12_0", cancel!.Name);
        Assert.Equal("_SetupPopupEvent_b__12_3", detail!.Name);

        Assert.True(
            NetherCodePopupInteropResolver.TryResolveStaticMethod(
                utility,
                NetherCodePopupNativeBinding.ConfirmTaskBinding(controller.FullName!),
                out string confirmTaskError,
                out MethodInfo? confirmTask
            ),
            confirmTaskError
        );
        Assert.True(
            NetherCodePopupInteropResolver.TryResolveStaticMethod(
                utility,
                NetherCodePopupNativeBinding.CancelTaskBinding(controller.FullName!),
                out string cancelTaskError,
                out MethodInfo? cancelTask
            ),
            cancelTaskError
        );
        Assert.Equal(
            "Method_Internal_Static_UniTask_AbyssCodeSelectPopupController_Int64_NetherPartyModel_CancellationToken_PDM_0",
            confirmTask!.Name
        );
        Assert.Equal(
            "Method_Internal_Static_UniTask_AbyssCodeSelectPopupController_CancellationToken_PDM_0",
            cancelTask!.Name
        );
        Assert.True(
            NetherCodeConfirmTaskInvoker.TryResolve(
                controller,
                utility,
                NetherCodePopupNativeBinding.ConfirmTaskBinding(controller.FullName!),
                out string invocationError,
                out MethodInfo? invocationTask,
                out MemberInfo? partyMember,
                out MemberInfo? cancellationTokenMember
            ),
            invocationError
        );
        Assert.Same(confirmTask, invocationTask);
        PropertyInfo partyProperty = Assert.IsAssignableFrom<PropertyInfo>(partyMember);
        Assert.Equal("_partyModel", partyProperty.Name);
        Assert.Equal("Project.Nether.NetherPartyModel", partyProperty.PropertyType.FullName);
        PropertyInfo tokenProperty = Assert.IsAssignableFrom<PropertyInfo>(cancellationTokenMember);
        Assert.Equal("_cancellationToken", tokenProperty.Name);
        Assert.Equal("Il2CppSystem.Threading.CancellationToken", tokenProperty.PropertyType.FullName);
        Assert.DoesNotContain(confirm.CustomAttributes, attribute =>
            string.Equals(attribute.AttributeType.Name, "ObfuscatedNameAttribute", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(cancel.CustomAttributes, attribute =>
            string.Equals(attribute.AttributeType.Name, "ObfuscatedNameAttribute", StringComparison.Ordinal)
        );
    }

    public sealed class SanitizedController
    {
        public sealed class __c
        {
            public static __c __9 { get; } = new();

            public void _SetupPopupEvent_b__12_2(Unit _, SanitizedController controller)
            {
            }

            public void _SetupPopupEvent_b__12_0(Unit _, SanitizedController controller)
            {
            }
        }
    }

    public sealed class AttributeController
    {
        public sealed class __c
        {
            public static __c __9 { get; } = new();

            [ObfuscatedName("<SetupPopupEvent>b__12_2")]
            public void Method_Internal_Confirmed(Unit _, AttributeController controller)
            {
            }
        }
    }

    public sealed class AmbiguousController
    {
        public sealed class __c
        {
            public static __c __9 { get; } = new();

            public void _SetupPopupEvent_b__12_2(Unit _, AmbiguousController controller)
            {
            }

            [ObfuscatedName("<SetupPopupEvent>b__12_2")]
            public void Method_Internal_Confirmed(Unit _, AmbiguousController controller)
            {
            }
        }
    }

    public sealed class MissingController
    {
        public sealed class __c
        {
            public static __c __9 { get; } = new();
        }
    }

    public sealed class SystemTokenUtility
    {
        public static FakeUniTask Method_Internal_Static_UniTask_AbyssCodeSelectPopupController_CancellationToken_0(
            SanitizedController controller,
            System.Threading.CancellationToken token
        ) => new();
    }

    public sealed class RenamedTaskUtility
    {
        public static FakeUniTask NewGeneratedTaskName_PDM_0(
            SanitizedController controller,
            FakeCancellationToken token
        ) => new();
    }

    public sealed class AmbiguousRenamedTaskUtility
    {
        public static FakeUniTask FirstGeneratedTask(
            SanitizedController controller,
            FakeCancellationToken token
        ) => new();

        public static FakeUniTask SecondGeneratedTask(
            SanitizedController controller,
            FakeCancellationToken token
        ) => new();
    }

    public readonly record struct FakeCancellationToken(int Value);

    public sealed class Unit
    {
    }

    public sealed class FakeUniTask
    {
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class ObfuscatedNameAttribute : Attribute
    {
        public ObfuscatedNameAttribute(string name) => Name = name;

        public string Name { get; }
    }

    private sealed class PackagedProjectAssembly : IDisposable
    {
        private readonly AssemblyLoadContext _context;

        private PackagedProjectAssembly(AssemblyLoadContext context, Assembly assembly)
        {
            _context = context;
            Assembly = assembly;
        }

        public Assembly Assembly { get; }

        public static PackagedProjectAssembly Load()
        {
            const string interopDirectory = "/game/BepInEx/interop";
            const string coreDirectory = "/game/BepInEx/core";
            const string projectPath = interopDirectory + "/Project.dll";
            Assert.True(File.Exists(projectPath), "packaged Project.dll must be mounted read-only at /game");

            var context = new AssemblyLoadContext("round6-packaged-project", isCollectible: true);
            context.Resolving += (_, name) =>
            {
                string candidate = Path.Combine(interopDirectory, name.Name + ".dll");
                if (File.Exists(candidate))
                    return context.LoadFromAssemblyPath(candidate);
                candidate = Path.Combine(coreDirectory, name.Name + ".dll");
                return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
            };
            return new PackagedProjectAssembly(context, context.LoadFromAssemblyPath(projectPath));
        }

        public Type RequireType(string name) => Assembly.GetType(name, throwOnError: false)
            ?? throw new Xunit.Sdk.XunitException("missing packaged type: " + name);

        public void Dispose() => _context.Unload();
    }

    private static NetherCodePopupInteropMethodBinding FixtureConfirmBinding(string controllerName) => new(
        "_SetupPopupEvent_b__12_2",
        "<SetupPopupEvent>b__12_2",
        new[] { typeof(Unit).FullName!, controllerName },
        "System.Void"
    ) { IsStatic = false };
}
