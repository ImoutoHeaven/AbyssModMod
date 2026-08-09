#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace AbyssMod.Services;

/// <summary>
/// Reflection-only adapter for the exact live code path: possession <c>NetherCodeData</c>
/// supplies <c>MNetherCodeId</c>/<c>Amount</c>, while <c>MNetherCodes</c> supplies
/// <c>id</c>, <c>effect_type</c>, and all three <c>effect_parameter_*</c> fields.
/// </summary>
internal sealed class NetherRuntimeActiveCodeErosionExtractor
{
    private static readonly BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly NetherActiveCodeErosionProjectionMapper _mapper = new();

    public NetherActiveCodeErosionProjection Extract(object? rawPossessionCodes, object? rawMasterRows)
    {
        if (rawPossessionCodes == null)
            return NetherActiveCodeErosionProjectionMapper.Unknown("missing-possession-nether-codes");
        if (!NetherRuntimeEnumerableReader.TryRead(rawPossessionCodes, out List<object> rawPossessions, out string possessionError))
        {
            return NetherActiveCodeErosionProjectionMapper.Unknown(
                "invalid-possession-nether-code-collection:" + possessionError
            );
        }

        var possessions = new List<NetherPossessionCodeErosionInput>(rawPossessions!.Count);
        foreach (object rawPossession in rawPossessions)
        {
            if (!TryReadInt64(rawPossession, "MNetherCodeId", out long codeId)
                || !TryReadInt64(rawPossession, "Amount", out long amount))
            {
                return NetherActiveCodeErosionProjectionMapper.Unknown("missing-possession-nether-code-member");
            }
            possessions.Add(new NetherPossessionCodeErosionInput(codeId, amount));
        }

        if (rawMasterRows == null)
            return _mapper.Map(possessions, null);
        if (!NetherRuntimeEnumerableReader.TryRead(rawMasterRows, out List<object> rawMasters, out string masterError))
        {
            return NetherActiveCodeErosionProjectionMapper.Unknown(
                "invalid-m-nether-code-collection:" + masterError
            );
        }

        var masters = new List<NetherCodeErosionMasterInput>(rawMasters!.Count);
        foreach (object rawMaster in rawMasters)
        {
            if (!TryReadInt64(rawMaster, "id", out long codeId)
                || !TryReadInt32(rawMaster, "effect_type", out int effectType)
                || !TryReadInt64(rawMaster, "effect_parameter_1", out long parameter1)
                || !TryReadInt64(rawMaster, "effect_parameter_2", out long parameter2)
                || !TryReadInt64(rawMaster, "effect_parameter_3", out long parameter3))
            {
                return NetherActiveCodeErosionProjectionMapper.Unknown("missing-m-nether-code-effect-member");
            }
            masters.Add(new NetherCodeErosionMasterInput(
                codeId,
                effectType,
                parameter1,
                parameter2,
                parameter3
            ));
        }

        return _mapper.Map(possessions, masters);
    }

    private static bool TryReadMember(object target, string name, out object? value)
    {
        value = null;
        Type type = target.GetType();
        PropertyInfo? property = type.GetProperty(name, InstanceFlags);
        if (property != null && property.GetIndexParameters().Length == 0)
        {
            value = property.GetValue(target);
            return true;
        }
        MethodInfo? getter = type.GetMethod("get_" + name, InstanceFlags, null, Type.EmptyTypes, null);
        if (getter != null)
        {
            value = getter.Invoke(target, Array.Empty<object>());
            return true;
        }
        FieldInfo? field = type.GetField(name, InstanceFlags)
            ?? type.GetField("<" + name + ">k__BackingField", InstanceFlags);
        if (field == null)
            return false;
        value = field.GetValue(target);
        return true;
    }

    private static bool TryReadInt64(object target, string name, out long value)
    {
        value = 0;
        if (!TryReadMember(target, name, out object? raw) || raw == null)
            return false;
        try
        {
            value = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryReadInt32(object target, string name, out int value)
    {
        value = 0;
        if (!TryReadInt64(target, name, out long raw) || raw is < int.MinValue or > int.MaxValue)
            return false;
        value = (int)raw;
        return true;
    }
}
