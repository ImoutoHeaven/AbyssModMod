#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace AbyssMod.Services;

/// <summary>
/// Thin reflection adapter for the native path
/// <c>FloorSelection.NetherModel.PartyModel.CharacterModels</c>.  It has no endpoint and only
/// reads the exact RO-evidenced members <c>MCharacterId</c>, <c>HpRatio</c>, and <c>IsAlive</c>.
/// </summary>
internal sealed class NetherRuntimeActivePartyHpExtractor
{
    private static readonly BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly NetherActivePartyHpSafetyMapper _mapper = new();

    public NetherActivePartyHpSafety Extract(object? netherModel)
    {
        if (netherModel == null)
            return NetherActivePartyHpSafetyMapper.Unknown("missing-nether-model");
        if (!TryReadMember(netherModel, "PartyModel", out object? partyModel) || partyModel == null)
            return NetherActivePartyHpSafetyMapper.Unknown("missing-nether-party-model");
        if (!TryReadMember(partyModel, "CharacterModels", out object? rawCharacters) || rawCharacters == null)
            return NetherActivePartyHpSafetyMapper.Unknown("missing-nether-party-character-models");

        var members = new List<NetherActiveBattleMemberHp>();
        foreach (object rawCharacter in Enumerate(rawCharacters))
        {
            if (!TryReadInt64(rawCharacter, "MCharacterId", out long characterId)
                || !TryReadDouble(rawCharacter, "HpRatio", out double hpRatio)
                || !TryReadBoolean(rawCharacter, "IsAlive", out bool isAlive))
            {
                return NetherActivePartyHpSafetyMapper.Unknown("missing-nether-party-character-hp-member");
            }
            members.Add(new NetherActiveBattleMemberHp(characterId, hpRatio, isAlive));
        }

        return _mapper.Map(members);
    }

    private static IEnumerable<object> Enumerate(object collection)
    {
        if (collection is IEnumerable enumerable)
        {
            foreach (object? value in enumerable)
            {
                if (value != null)
                    yield return value;
            }
            yield break;
        }

        // IL2CPP generic collections are not guaranteed to implement the CLR IEnumerable
        // interface visible to this managed assembly.  Use the same exact enumerator pattern
        // as the runtime bridge rather than silently treating such a party as empty.
        MethodInfo? getEnumerator = collection.GetType().GetMethod(
            "GetEnumerator",
            InstanceFlags,
            null,
            Type.EmptyTypes,
            null
        );
        if (getEnumerator == null)
            yield break;
        object? enumerator = getEnumerator.Invoke(collection, Array.Empty<object>());
        if (enumerator == null)
            yield break;
        MethodInfo? moveNext = enumerator.GetType().GetMethod(
            "MoveNext",
            InstanceFlags,
            null,
            Type.EmptyTypes,
            null
        );
        if (moveNext == null)
            yield break;
        while (moveNext.Invoke(enumerator, Array.Empty<object>()) is bool hasNext && hasNext)
        {
            if (TryReadMember(enumerator, "Current", out object? current) && current != null)
                yield return current;
        }
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

    private static bool TryReadDouble(object target, string name, out double value)
    {
        value = 0d;
        if (!TryReadMember(target, name, out object? raw) || raw == null)
            return false;
        try
        {
            value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryReadBoolean(object target, string name, out bool value)
    {
        value = false;
        if (!TryReadMember(target, name, out object? raw) || raw == null)
            return false;
        try
        {
            value = Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
