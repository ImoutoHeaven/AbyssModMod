#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AbyssMod.Services;

/// <summary>
/// A version-characterized native member contract.  A candidate is accepted only when its
/// current managed name or its exact <c>ObfuscatedName</c> attribute matches and every part of
/// its signature is identical.  This deliberately has no arity-only or substring fallback.
/// </summary>
internal sealed record NetherCodePopupInteropMethodBinding(
    string ManagedName,
    string? ObfuscatedName,
    IReadOnlyList<string> ParameterTypeNames,
    string ReturnTypeName
)
{
    public bool IsStatic { get; init; }
}

/// <summary>
/// Resolves the packaged generated Code-offer callbacks without assuming that cpp2il's source
/// names survive BepInEx interop sanitization.  The current package exposes public
/// <c>__c</c>/<c>__9</c> and underscore callback names; a future package may expose the exact
/// original name only through an <c>ObfuscatedNameAttribute</c>.  Both cases are strict and
/// unique, otherwise F12 remains fail-closed.
/// </summary>
internal static class NetherCodePopupInteropResolver
{
    private const string GeneratedHolderManagedName = "__c";
    private const string GeneratedHolderObfuscatedName = "<>c";
    private const string GeneratedSingletonManagedName = "__9";
    private const string GeneratedSingletonObfuscatedName = "<>9";

    private const BindingFlags DeclaredInstanceFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private const BindingFlags DeclaredStaticFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static bool TryResolveGeneratedCallback(
        Type controllerType,
        NetherCodePopupInteropMethodBinding binding,
        out string error,
        out object? singleton,
        out MethodInfo? method
    )
    {
        singleton = null;
        if (!TryResolveGeneratedCallbackTarget(
                controllerType,
                binding,
                out error,
                out MemberInfo? singletonMember,
                out method
            ))
            return false;

        try
        {
            singleton = singletonMember switch
            {
                PropertyInfo property => property.GetValue(null),
                FieldInfo field => field.GetValue(null),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            error = "binding-unavailable:"
                + (singletonMember?.DeclaringType?.FullName ?? "generated-singleton")
                + ":generated-singleton-read:"
                + ex.GetType().Name;
            return false;
        }

        if (singleton == null)
        {
            error = "binding-unavailable:"
                + (singletonMember?.DeclaringType?.FullName ?? "generated-singleton")
                + ":generated-singleton:null";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Resolves the exact callback target without touching an IL2CPP static property.  This is
    /// intentionally separate from invocation so read-only packaged-interop characterization
    /// can prove uniqueness outside a running game without attempting to initialize native
    /// singleton state.
    /// </summary>
    public static bool TryResolveGeneratedCallbackTarget(
        Type controllerType,
        NetherCodePopupInteropMethodBinding binding,
        out string error,
        out MemberInfo? singletonMember,
        out MethodInfo? method
    )
    {
        singletonMember = null;
        method = null;
        if (controllerType == null)
        {
            error = "binding-unavailable:null-code-popup-controller";
            return false;
        }
        if (binding == null)
        {
            error = "binding-unavailable:null-code-popup-binding";
            return false;
        }

        Type[] holders = controllerType
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Where(type => MatchesName(type, GeneratedHolderManagedName, GeneratedHolderObfuscatedName))
            .ToArray();
        if (holders.Length != 1)
        {
            error = "binding-unavailable:"
                + (controllerType.FullName ?? controllerType.Name)
                + ":generated-holder:"
                + (holders.Length == 0 ? "no-exact" : "ambiguous");
            return false;
        }

        Type holder = holders[0];
        if (!TryResolveSingletonMember(holder, out error, out singletonMember))
            return false;

        return TryResolveMethod(holder, binding, DeclaredInstanceFlags, out error, out method);
    }

    public static bool TryResolveStaticMethod(
        Type type,
        NetherCodePopupInteropMethodBinding binding,
        out string error,
        out MethodInfo? method
    ) => TryResolveMethod(type, binding, DeclaredStaticFlags, out error, out method);

    private static bool TryResolveSingletonMember(Type holder, out string error, out MemberInfo? singletonMember)
    {
        singletonMember = null;
        var candidates = new List<MemberInfo>();
        candidates.AddRange(
            holder.GetProperties(DeclaredStaticFlags).Where(property =>
                property.GetMethod != null
                && property.GetMethod.IsStatic
                && property.PropertyType == holder
                && MatchesName(property, GeneratedSingletonManagedName, GeneratedSingletonObfuscatedName)
            )
        );
        candidates.AddRange(
            holder.GetFields(DeclaredStaticFlags).Where(field =>
                field.IsStatic
                && field.FieldType == holder
                && MatchesName(field, GeneratedSingletonManagedName, GeneratedSingletonObfuscatedName)
            )
        );

        if (candidates.Count != 1)
        {
            error = "binding-unavailable:"
                + (holder.FullName ?? holder.Name)
                + ":generated-singleton:"
                + (candidates.Count == 0 ? "no-exact" : "ambiguous");
            return false;
        }

        singletonMember = candidates[0];
        error = string.Empty;
        return true;
    }

    private static bool TryResolveMethod(
        Type type,
        NetherCodePopupInteropMethodBinding binding,
        BindingFlags flags,
        out string error,
        out MethodInfo? method
    )
    {
        method = null;
        if (type == null)
        {
            error = "binding-unavailable:null-type";
            return false;
        }
        if (binding == null)
        {
            error = "binding-unavailable:null-binding";
            return false;
        }

        MethodInfo[] candidates = type.GetMethods(flags)
            .Where(candidate => MatchesMethod(candidate, binding))
            .ToArray();
        if (candidates.Length != 1)
        {
            error = "binding-unavailable:"
                + (type.FullName ?? type.Name)
                + ":"
                + binding.ManagedName
                + ":"
                + (candidates.Length == 0 ? "no-exact-versioned-signature" : "ambiguous-exact-versioned-signature");
            return false;
        }

        method = candidates[0];
        error = string.Empty;
        return true;
    }

    private static bool MatchesMethod(MethodInfo candidate, NetherCodePopupInteropMethodBinding binding)
    {
        if (candidate.IsStatic != binding.IsStatic
            || !MatchesName(candidate, binding.ManagedName, binding.ObfuscatedName)
            || !string.Equals(TypeName(candidate.ReturnType), binding.ReturnTypeName, StringComparison.Ordinal))
        {
            return false;
        }

        ParameterInfo[] parameters = candidate.GetParameters();
        if (parameters.Length != binding.ParameterTypeNames.Count)
            return false;

        for (int index = 0; index < parameters.Length; index++)
        {
            if (!string.Equals(
                    TypeName(parameters[index].ParameterType),
                    binding.ParameterTypeNames[index],
                    StringComparison.Ordinal
                ))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesName(MemberInfo member, string managedName, string? obfuscatedName)
    {
        if (string.Equals(member.Name, managedName, StringComparison.Ordinal))
            return true;
        if (string.IsNullOrEmpty(obfuscatedName))
            return false;

        return member.CustomAttributes.Any(attribute =>
            string.Equals(attribute.AttributeType.Name, "ObfuscatedNameAttribute", StringComparison.Ordinal)
            && AttributeContainsExactName(attribute, obfuscatedName)
        );
    }

    private static bool AttributeContainsExactName(CustomAttributeData attribute, string expected)
    {
        foreach (CustomAttributeTypedArgument argument in attribute.ConstructorArguments)
        {
            if (argument.Value is string value && string.Equals(value, expected, StringComparison.Ordinal))
                return true;
        }
        foreach (CustomAttributeNamedArgument argument in attribute.NamedArguments)
        {
            if (argument.TypedValue.Value is string value && string.Equals(value, expected, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string TypeName(Type type) => type.FullName ?? type.Name;
}
