#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AbyssMod.Services;

/// <summary>
/// Reads both ordinary CLR collections and IL2CPP generic enumerable wrappers. IL2CPP exposes
/// <c>Current</c> on <c>IEnumerator&lt;T&gt;</c>, but exposes <c>MoveNext</c> only through the
/// separate non-generic <c>Il2CppSystem.Collections.IEnumerator</c> wrapper. The two wrappers
/// share one native pointer and are joined through the runtime's inherited <c>TryCast&lt;T&gt;</c>.
/// </summary>
internal static class NetherRuntimeEnumerableReader
{
    private const string Il2CppNonGenericEnumeratorTypeName =
        "Il2CppSystem.Collections.IEnumerator";
    private static readonly BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static bool TryRead(
        object? collection,
        out List<object> values,
        out string detail
    )
    {
        values = new List<object>();
        detail = string.Empty;
        if (collection == null)
        {
            detail = "missing-collection";
            return false;
        }

        if (collection is IEnumerable enumerable)
        {
            try
            {
                foreach (object? value in enumerable)
                {
                    if (value != null)
                        values.Add(value);
                }
                return true;
            }
            catch (Exception ex)
            {
                values.Clear();
                detail = "clr-enumeration-exception:" + Unwrap(ex).GetType().Name;
                return false;
            }
        }

        try
        {
            MethodInfo? getEnumerator = collection.GetType().GetMethod(
                "GetEnumerator",
                InstanceFlags,
                null,
                Type.EmptyTypes,
                null
            );
            if (getEnumerator == null)
            {
                detail = "get-enumerator-unavailable";
                return false;
            }

            object? genericEnumerator = getEnumerator.Invoke(collection, Array.Empty<object>());
            if (genericEnumerator == null)
            {
                detail = "null-enumerator";
                return false;
            }

            object moveNextTarget = genericEnumerator;
            MethodInfo? moveNext = FindMoveNext(moveNextTarget.GetType());
            if (moveNext == null)
            {
                if (!TryCastToIl2CppNonGenericEnumerator(genericEnumerator, out moveNextTarget))
                {
                    detail = "move-next-unavailable";
                    return false;
                }
                moveNext = FindMoveNext(moveNextTarget.GetType());
                if (moveNext == null)
                {
                    detail = "move-next-unavailable";
                    return false;
                }
            }

            while (true)
            {
                object? rawMoveNext = moveNext.Invoke(moveNextTarget, Array.Empty<object>());
                if (rawMoveNext is not bool hasNext)
                {
                    values.Clear();
                    detail = "move-next-returned-non-boolean";
                    return false;
                }
                if (!hasNext)
                    return true;
                if (!TryReadCurrent(genericEnumerator, out object? current))
                {
                    values.Clear();
                    detail = "current-unavailable";
                    return false;
                }
                if (current != null)
                    values.Add(current);
            }
        }
        catch (Exception ex)
        {
            values.Clear();
            detail = "il2cpp-enumeration-exception:" + Unwrap(ex).GetType().Name;
            return false;
        }
    }

    private static MethodInfo? FindMoveNext(Type type) => type.GetMethod(
        "MoveNext",
        InstanceFlags,
        null,
        Type.EmptyTypes,
        null
    );

    private static bool TryCastToIl2CppNonGenericEnumerator(
        object genericEnumerator,
        out object target
    )
    {
        target = genericEnumerator;
        Type? nonGenericType = ResolveType(
            genericEnumerator.GetType().Assembly,
            Il2CppNonGenericEnumeratorTypeName
        );
        if (nonGenericType == null)
            return false;

        MethodInfo? tryCast = genericEnumerator.GetType()
            .GetMethods(InstanceFlags)
            .FirstOrDefault(method =>
                method.Name == "TryCast"
                && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 1
                && method.GetParameters().Length == 0
            );
        if (tryCast == null)
            return false;

        object? cast = tryCast.MakeGenericMethod(nonGenericType).Invoke(
            genericEnumerator,
            Array.Empty<object>()
        );
        if (cast == null)
            return false;
        target = cast;
        return true;
    }

    private static Type? ResolveType(Assembly preferredAssembly, string fullName)
    {
        Type? type = preferredAssembly.GetType(fullName, throwOnError: false);
        if (type != null)
            return type;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(fullName, throwOnError: false);
            if (type != null)
                return type;
        }
        return null;
    }

    private static bool TryReadCurrent(object enumerator, out object? value)
    {
        value = null;
        Type type = enumerator.GetType();
        PropertyInfo? property = type.GetProperty("Current", InstanceFlags);
        if (property != null && property.GetIndexParameters().Length == 0)
        {
            value = property.GetValue(enumerator);
            return true;
        }
        MethodInfo? getter = type.GetMethod(
            "get_Current",
            InstanceFlags,
            null,
            Type.EmptyTypes,
            null
        );
        if (getter == null)
            return false;
        value = getter.Invoke(enumerator, Array.Empty<object>());
        return true;
    }

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException { InnerException: not null } invocation
            ? invocation.InnerException!
            : exception;
}
