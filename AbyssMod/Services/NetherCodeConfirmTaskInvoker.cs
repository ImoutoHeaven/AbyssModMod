#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;

namespace AbyssMod.Services;

/// <summary>
/// Starts the game's complete code-confirmation UniTask directly from the live popup state.
/// Calling the generated Receive lambda through reflection is unsafe here: that native lambda
/// synchronously enters the same task through a Harmony DMD and nests IL2CPP runtime invokes.
/// The task itself is authoritative (replacement UI plus fix-code request), so retaining its
/// boxed return value preserves both the mutation and its completion boundary.
/// </summary>
internal static class NetherCodeConfirmTaskInvoker
{
    private const string PartyModelFieldName = "_partyModel";
    private const string CancellationTokenFieldName = "_cancellationToken";
    private const BindingFlags DeclaredInstanceFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    public static bool TryResolve(
        Type controllerType,
        Type utilityType,
        NetherCodePopupInteropMethodBinding binding,
        out string error,
        out MethodInfo? taskMethod,
        out MemberInfo? partyModelMember,
        out MemberInfo? cancellationTokenMember
    )
    {
        taskMethod = null;
        partyModelMember = null;
        cancellationTokenMember = null;
        if (controllerType == null || utilityType == null || binding == null)
        {
            error = "binding-unavailable:code-confirm:null-input";
            return false;
        }

        if (!NetherCodePopupInteropResolver.TryResolveStaticMethod(
                utilityType,
                binding,
                out error,
                out taskMethod
            ))
        {
            return false;
        }

        ParameterInfo[] parameters = taskMethod!.GetParameters();
        if (parameters.Length != 4
            || parameters[0].ParameterType != controllerType
            || parameters[1].ParameterType != typeof(long))
        {
            error = "binding-unavailable:code-confirm:unexpected-task-signature";
            taskMethod = null;
            return false;
        }

        if (!TryResolveExactMember(
                controllerType,
                PartyModelFieldName,
                parameters[2].ParameterType,
                out error,
                out partyModelMember
            )
            || !TryResolveExactMember(
                controllerType,
                CancellationTokenFieldName,
                parameters[3].ParameterType,
                out error,
                out cancellationTokenMember
            ))
        {
            taskMethod = null;
            partyModelMember = null;
            cancellationTokenMember = null;
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryInvoke(
        object controller,
        Type utilityType,
        long selectedCodeId,
        NetherCodePopupInteropMethodBinding binding,
        out object? task,
        out string error
    )
    {
        task = null;
        if (controller == null || selectedCodeId <= 0)
        {
            error = "binding-unavailable:code-confirm:invalid-input";
            return false;
        }

        if (!TryResolve(
                controller.GetType(),
                utilityType,
                binding,
                out error,
                out MethodInfo? taskMethod,
                out MemberInfo? partyModelMember,
                out MemberInfo? cancellationTokenMember
            ))
        {
            return false;
        }

        try
        {
            object? partyModel = ReadValue(partyModelMember!, controller);
            object? cancellationToken = ReadValue(cancellationTokenMember!, controller);
            if (partyModel == null)
            {
                error = "binding-unavailable:code-confirm:null-party-model";
                return false;
            }
            if (cancellationToken == null)
            {
                error = "binding-unavailable:code-confirm:null-cancellation-token";
                return false;
            }

            task = taskMethod!.Invoke(
                null,
                new[] { controller, (object)selectedCodeId, partyModel, cancellationToken }
            );
            if (task == null)
            {
                error = "binding-unavailable:code-confirm:null-task";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (TargetInvocationException ex)
        {
            task = null;
            Exception cause = ex.InnerException ?? ex;
            error = "native-code-confirm-task-exception:"
                + cause.GetType().Name
                + ":"
                + cause.Message;
            return false;
        }
        catch (Exception ex)
        {
            task = null;
            error = "native-code-confirm-task-exception:"
                + ex.GetType().Name
                + ":"
                + ex.Message;
            return false;
        }
    }

    private static bool TryResolveExactMember(
        Type controllerType,
        string memberName,
        Type expectedMemberType,
        out string error,
        out MemberInfo? member
    )
    {
        var candidates = new List<MemberInfo>();
        for (Type? current = controllerType; current != null; current = current.BaseType)
        {
            foreach (FieldInfo candidate in current.GetFields(DeclaredInstanceFlags))
            {
                if (string.Equals(candidate.Name, memberName, StringComparison.Ordinal))
                    candidates.Add(candidate);
            }
            foreach (PropertyInfo candidate in current.GetProperties(DeclaredInstanceFlags))
            {
                if (string.Equals(candidate.Name, memberName, StringComparison.Ordinal)
                    && candidate.GetMethod != null
                    && !candidate.GetMethod.IsStatic
                    && candidate.GetIndexParameters().Length == 0)
                {
                    candidates.Add(candidate);
                }
            }
        }

        if (candidates.Count != 1)
        {
            member = null;
            error = "binding-unavailable:code-confirm:"
                + memberName
                + ":"
                + (candidates.Count == 0 ? "no-exact" : "ambiguous")
                + ":available="
                + DescribeInstanceFields(controllerType);
            return false;
        }
        Type actualType = candidates[0] switch
        {
            FieldInfo field => field.FieldType,
            PropertyInfo property => property.PropertyType,
            _ => typeof(void),
        };
        if (actualType != expectedMemberType)
        {
            member = null;
            error = "binding-unavailable:code-confirm:"
                + memberName
                + ":type-mismatch:"
                + (actualType.FullName ?? actualType.Name);
            return false;
        }

        member = candidates[0];
        error = string.Empty;
        return true;
    }

    private static object? ReadValue(MemberInfo member, object target) => member switch
    {
        FieldInfo field => field.GetValue(target),
        PropertyInfo property => property.GetValue(target),
        _ => null,
    };

    private static string DescribeInstanceFields(Type controllerType)
    {
        var fields = new List<string>();
        for (Type? current = controllerType; current != null && fields.Count < 32; current = current.BaseType)
        {
            foreach (FieldInfo candidate in current.GetFields(DeclaredInstanceFlags))
            {
                if (fields.Count >= 32)
                    break;
                fields.Add(
                    (current.FullName ?? current.Name)
                    + "."
                    + candidate.Name
                    + ":"
                    + (candidate.FieldType.FullName ?? candidate.FieldType.Name)
                );
            }
        }
        for (Type? current = controllerType; current != null && fields.Count < 32; current = current.BaseType)
        {
            foreach (PropertyInfo candidate in current.GetProperties(DeclaredInstanceFlags))
            {
                if (fields.Count >= 32)
                    break;
                fields.Add(
                    (current.FullName ?? current.Name)
                    + "."
                    + candidate.Name
                    + ":"
                    + (candidate.PropertyType.FullName ?? candidate.PropertyType.Name)
                    + "(property)"
                );
            }
        }
        return fields.Count == 0 ? "none" : string.Join("|", fields);
    }
}
