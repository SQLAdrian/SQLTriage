/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data
{
    /// <summary>
    /// Shared JSON config file load/save to eliminate boilerplate across services.
    /// All callers follow the same pattern: read file → deserialize → modify → serialize → write file.
    /// </summary>
    public static class ConfigFileHelper
    {
        private static readonly JsonSerializerOptions DefaultOptions = new() { WriteIndented = true };

        /// <summary>
        /// Loads and deserializes a JSON config file. Returns a new instance of T if the file
        /// doesn't exist, is empty, or can't be parsed.
        /// </summary>
        public static T Load<T>(string filePath, JsonSerializerOptions? options = null) where T : new()
        {
            try
            {
                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    if (!string.IsNullOrWhiteSpace(json))
                        return JsonSerializer.Deserialize<T>(json, options) ?? new T();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConfigFileHelper] Failed to load {filePath}: {ex.Message}");
            }

            return new T();
        }

        /// <summary>
        /// Serializes and writes a config object to a JSON file. Creates the directory if needed.
        /// Uses atomic write (temp file + move) to prevent corruption on crash.
        /// </summary>
        public static void Save<T>(string filePath, T config, JsonSerializerOptions? options = null)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(config, options ?? DefaultOptions);

            // Atomic write: write to temp file, then move to target
            var tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, overwrite: true);
        }
    }
}
