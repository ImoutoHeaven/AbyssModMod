using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AbyssMod.Services;

internal sealed class TextCollectionStore
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static bool Add(string path, string text)
    {
        var entries = Load(path);
        if (entries.ContainsKey(text))
            return false;

        entries[text] = string.Empty;
        Save(path, entries);
        return true;
    }

    private static Dictionary<string, string> Load(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, string>();

        return JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(path, Utf8)
        ) ?? new Dictionary<string, string>();
    }

    private static void Save(string path, Dictionary<string, string> entries)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string tempPath = path + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(entries, JsonOptions), Utf8);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
