using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AbyssMod
{
    /// <summary>
    /// 翻译清单数据结构，对应远程 manifest.json 格式。
    /// 支持作者扁平 m_* hash（经 JsonExtensionData）与 legacy 显式字段并存。
    /// </summary>
    public class Manifest
    {
        [JsonPropertyName("hash")]
        public string Hash { get; set; }

        [JsonPropertyName("names")]
        public string Names { get; set; }

        [JsonPropertyName("titles")]
        public string Titles { get; set; }

        [JsonPropertyName("descriptions")]
        public string Descriptions { get; set; }

        [JsonPropertyName("another_name")]
        public string AnotherName { get; set; }

        [JsonPropertyName("ability_descriptions")]
        public string AbilityDescriptions { get; set; }

        [JsonPropertyName("ui_texts")]
        public string UiTexts { get; set; }

        [JsonPropertyName("novels")]
        public Dictionary<string, string> Novels { get; set; }

        [JsonPropertyName("other")]
        public Dictionary<string, string> Other { get; set; }

        [JsonPropertyName("add_on")]
        public Dictionary<string, string> AddOn { get; set; }

        [JsonPropertyName("replacements")]
        public Dictionary<string, string> Replacements { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extra { get; set; }

        /// <summary>取得任意类型（m_* / names / ui_texts 等）的清单哈希。</summary>
        public string GetFileHash(string type)
        {
            string explicitHash = type switch
            {
                "names" => Names,
                "titles" => Titles,
                "descriptions" => Descriptions,
                "another_name" => AnotherName,
                "ability_descriptions" => AbilityDescriptions,
                "ui_texts" => UiTexts,
                _ => null,
            };
            if (!string.IsNullOrEmpty(explicitHash))
                return explicitHash;

            if (Extra == null || !Extra.TryGetValue(type, out JsonElement value))
                return null;

            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
    }
}
