/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SQLTriage.Data.Services.Licensing;

/// <summary>
/// Extension methods on <see cref="SQLTriage.Data.UserSettingsService"/> for reading and
/// writing the license section of user-settings.json.
///
/// The license is stored under a top-level "License" key in user-settings.json:
/// <code>
/// {
///   "License": {
///     "ClientName": "Acme Corp",
///     "EncryptedKey": "&lt;base64-DPAPI-wrapped-32-bytes&gt;"
///   }
/// }
/// </code>
///
/// Because <see cref="UserSettingsService"/> does not expose a generic property-bag API,
/// these extensions read and rewrite the settings file directly via <see cref="JsonNode"/>.
/// They acquire no lock on the service's internal state — callers should treat this as a
/// low-frequency operation (startup + activation only).
/// </summary>
public static class UserSettingsLicenseExtensions
{
    private const string LicenseSectionKey = "License";
    private const string ClientNameKey = "ClientName";
    private const string EncryptedKeyKey = "EncryptedKey";

    /// <summary>
    /// Reads the saved license from user-settings.json.
    /// Returns (null, null) if no license section is present or if either field is missing.
    /// </summary>
    public static (string? ClientName, byte[]? EncryptedKey) GetSavedLicense(
        this SQLTriage.Data.UserSettingsService userSettings)
    {
        var filePath = ResolveFilePath(userSettings);
        if (!File.Exists(filePath)) return (null, null);

        try
        {
            var json = File.ReadAllText(filePath);
            var root = JsonNode.Parse(json);
            if (root is null) return (null, null);

            var section = root[LicenseSectionKey];
            if (section is null) return (null, null);

            var clientName = section[ClientNameKey]?.GetValue<string>();
            var encKeyBase64 = section[EncryptedKeyKey]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(clientName) || string.IsNullOrWhiteSpace(encKeyBase64))
                return (null, null);

            byte[]? encKey = null;
            try { encKey = Convert.FromBase64String(encKeyBase64); }
            catch (FormatException) { return (null, null); }

            return (clientName, encKey);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UserSettingsLicense] GetSavedLicense failed: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Persists the license to user-settings.json, merging into any existing content.
    /// </summary>
    public static void SaveLicense(
        this SQLTriage.Data.UserSettingsService userSettings,
        string clientName,
        byte[] dpapiWrappedKey)
    {
        if (string.IsNullOrWhiteSpace(clientName))
            throw new ArgumentException("clientName is required", nameof(clientName));
        if (dpapiWrappedKey is null || dpapiWrappedKey.Length == 0)
            throw new ArgumentException("dpapiWrappedKey is required", nameof(dpapiWrappedKey));

        UpdateLicenseSection(userSettings, section =>
        {
            section[ClientNameKey] = clientName;
            section[EncryptedKeyKey] = Convert.ToBase64String(dpapiWrappedKey);
        });
    }

    /// <summary>
    /// Removes the License section from user-settings.json entirely.
    /// </summary>
    public static void ClearLicense(this SQLTriage.Data.UserSettingsService userSettings)
    {
        var filePath = ResolveFilePath(userSettings);
        if (!File.Exists(filePath)) return;

        try
        {
            var json = File.ReadAllText(filePath);
            var root = JsonNode.Parse(json);
            if (root is null) return;

            var obj = root.AsObject();
            obj.Remove(LicenseSectionKey);

            WriteAtomic(filePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UserSettingsLicense] ClearLicense failed: {ex.Message}");
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static void UpdateLicenseSection(
        SQLTriage.Data.UserSettingsService userSettings,
        Action<JsonObject> mutate)
    {
        var filePath = ResolveFilePath(userSettings);

        try
        {
            JsonNode root;
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                root = JsonNode.Parse(json) ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var rootObj = root.AsObject();

            JsonObject section;
            if (rootObj[LicenseSectionKey] is JsonObject existing)
            {
                section = existing;
            }
            else
            {
                section = new JsonObject();
                rootObj[LicenseSectionKey] = section;
            }

            mutate(section);

            var dir = Path.GetDirectoryName(filePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            WriteAtomic(filePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UserSettingsLicense] UpdateLicenseSection failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Resolves the settings file path from a UserSettingsService instance.
    /// Mirrors the path logic in UserSettingsService's constructor:
    /// %AppData%\SQLTriage\user-settings.json
    /// </summary>
    private static string ResolveFilePath(SQLTriage.Data.UserSettingsService _)
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SQLTriage");
        return Path.Combine(appDataDir, "user-settings.json");
    }

    private static void WriteAtomic(string filePath, string json)
    {
        var temp = filePath + ".lic.tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, filePath, overwrite: true);
    }
}
