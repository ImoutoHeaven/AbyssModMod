using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Il2CppInterop.Runtime;

namespace AbyssMod.Services;

public static class MasterMapping
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static Dictionary<string, TableMapping> Tables { get; private set; } = [];

    private const string ResourceName = "AbyssMod.master_mapping.json";

    private const string ResourceNameAlt = "AbyssMod.AbyssMod.master_mapping.json";

    public static void Load()
    {
        try
        {
            using Stream stream = OpenMappingStream();
            if (stream == null)
            {
                Logger.Error(
                    "[MasterMapping] embedded resource not found: "
                        + ResourceName
                        + " (also tried "
                        + ResourceNameAlt
                        + ")"
                );
                Tables = [];
                return;
            }

            using JsonDocument doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("dict_types", out JsonElement dictTypesEl))
            {
                var list = new List<string>();
                foreach (JsonElement item in dictTypesEl.EnumerateArray())
                {
                    string s = item.GetString();
                    if (!string.IsNullOrEmpty(s))
                        list.Add(s);
                }
                TranslationPaths.SetContentTypes(list);
                Logger.Info($"[MasterMapping] registered {list.Count} dict types");
            }
            else
            {
                Logger.Warn("[MasterMapping] missing 'dict_types', no translation tables will load");
                TranslationPaths.SetContentTypes([]);
            }

            if (!doc.RootElement.TryGetProperty("tables", out JsonElement tablesEl))
            {
                Logger.Error("[MasterMapping] missing 'tables' property");
                Tables = [];
                return;
            }

            var tables = new Dictionary<string, TableMapping>();
            foreach (JsonProperty prop in tablesEl.EnumerateObject())
            {
                foreach (
                    string className in MasterDataSyncProtocol.ReadClassNames(
                        prop.Name,
                        prop.Value
                    )
                )
                {
                    TableMapping table = BuildTable(prop.Name, prop.Value, className);
                    if (table.Fields.Count > 0)
                        tables[className] = table;
                }
            }

            Tables = tables;
            Logger.Info(
                $"[MasterMapping] loaded {tables.Count} tables, {CountFields(tables)} fields"
            );
            ValidateDictReferences(tables);
        }
        catch (Exception ex)
        {
            Logger.Error($"[MasterMapping] failed to load: {ex}");
            Tables = [];
        }
    }

    private static Stream OpenMappingStream()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (string name in new[] { ResourceName, ResourceNameAlt })
        {
            Stream stream = asm.GetManifestResourceStream(name);
            if (stream != null)
                return stream;
        }
        return null;
    }

    private static TableMapping BuildTable(
        string tableName,
        JsonElement tableEl,
        string className
    )
    {
        var table = new TableMapping();
        IntPtr classPtr = ResolveClassPtr(className);
        if (classPtr == IntPtr.Zero)
        {
            Logger.Warn($"[MasterMapping] type not resolved: {className}, table skipped");
            return table;
        }

        foreach (JsonProperty prop in tableEl.EnumerateObject())
        {
            if (prop.Name.StartsWith("_", StringComparison.Ordinal))
                continue;

            List<FieldRule> rules = ParseRules(prop.Value);
            if (rules.Count == 0)
                continue;

            IntPtr fieldPtr = IL2CPP.GetIl2CppField(classPtr, prop.Name);
            if (fieldPtr == IntPtr.Zero)
            {
                Logger.Warn($"[MasterMapping] field not found: {className}.{prop.Name}, skipped");
                continue;
            }

            table.Fields.Add(
                new FieldEntry
                {
                    Name = prop.Name,
                    FieldPtr = fieldPtr,
                    Offset = (int)IL2CPP.il2cpp_field_get_offset(fieldPtr),
                    Rules = rules,
                }
            );
        }

        return table;
    }

    private static List<FieldRule> ParseRules(JsonElement el)
    {
        var rules = new List<FieldRule>();
        if (el.ValueKind == JsonValueKind.Object)
            rules.Add(ParseRule(el));
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in el.EnumerateArray())
                rules.Add(ParseRule(item));
        }
        return rules;
    }

    private static FieldRule ParseRule(JsonElement el)
    {
        string dict = "";
        bool seal = false;
        foreach (JsonProperty prop in el.EnumerateObject())
        {
            if (prop.NameEquals("dict"))
                dict = prop.Value.GetString() ?? "";
            else if (prop.NameEquals("seal"))
                seal = prop.Value.GetBoolean();
        }

        return new FieldRule { Dict = dict, Seal = seal };
    }

    private static int CountFields(Dictionary<string, TableMapping> tables)
    {
        int count = 0;
        foreach (TableMapping table in tables.Values)
            count += table.Fields.Count;
        return count;
    }

    private static void ValidateDictReferences(Dictionary<string, TableMapping> tables)
    {
        var known = new HashSet<string>(TranslationPaths.ContentTypes);
        var missing = new HashSet<string>();
        foreach (TableMapping table in tables.Values)
        {
            foreach (FieldEntry field in table.Fields)
            {
                foreach (FieldRule rule in field.Rules)
                {
                    if (!known.Contains(rule.Dict))
                        missing.Add(rule.Dict);
                }
            }
        }

        foreach (string dict in missing)
        {
            Logger.Warn(
                $"[MasterMapping] dict '{dict}' used in tables but not in dict_types; "
                    + "translations referencing it will be skipped"
            );
        }
    }

    private static IntPtr ResolveClassPtr(string tableName)
    {
        try
        {
            return IL2CPP.GetIl2CppClass(
                "Project.dll",
                "Project.Master.NoaMessagePack",
                tableName
            );
        }
        catch (Exception ex)
        {
            Logger.Error($"[MasterMapping] resolve {tableName} failed: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    public static unsafe List<IntPtr> GetArrayElements(IntPtr arrayPtr)
    {
        if (arrayPtr == IntPtr.Zero)
            return null;

        int length = (int)IL2CPP.il2cpp_array_length(arrayPtr);
        var elements = new List<IntPtr>(length);
        IntPtr dataPtr = arrayPtr + 4 * IntPtr.Size;
        for (int i = 0; i < length; i++)
            elements.Add(*(IntPtr*)(void*)(dataPtr + i * IntPtr.Size));
        return elements;
    }

    public static unsafe string ReadField(IntPtr rowPtr, FieldEntry entry)
    {
        IntPtr strPtr = *(IntPtr*)(void*)(rowPtr + entry.Offset);
        return strPtr == IntPtr.Zero ? null : IL2CPP.Il2CppStringToManaged(strPtr);
    }

    public static void WriteField(IntPtr rowPtr, FieldEntry entry, string value)
    {
        IL2CPP.il2cpp_gc_wbarrier_set_field(
            rowPtr,
            rowPtr + entry.Offset,
            IL2CPP.ManagedStringToIl2Cpp(value)
        );
    }
}
