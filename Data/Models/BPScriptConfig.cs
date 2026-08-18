/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SQLTriage.Data.Models
{
    public class BPScriptConfig
    {
        [JsonPropertyName("scripts")]
        public List<BPScript> Scripts { get; set; } = new();
    }

    public class BPScript
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("fileName")]
        public string FileName { get; set; } = "";

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("order")]
        public int Order { get; set; }

        [JsonPropertyName("parameters")]
        public List<BPScriptParameter> Parameters { get; set; } = new();
    }

    public class BPScriptParameter
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "VARCHAR(100)";

        [JsonPropertyName("defaultValue")]
        public string DefaultValue { get; set; } = "";

        [JsonPropertyName("value")]
        public string Value { get; set; } = "";

        [JsonPropertyName("required")]
        public bool Required { get; set; } = true;
    }
}
