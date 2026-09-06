/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data
{
    public class BPScriptService
    {
        private readonly ILogger<BPScriptService> _logger;
        private readonly string _scriptsPath;
        private readonly string _configPath;
        private BPScriptConfig _config;
        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        public BPScriptService(ILogger<BPScriptService> logger) : this(logger, null, null) { }

        // Test seam: the overrides let a test point at temp directories. Production passes neither.
        public BPScriptService(ILogger<BPScriptService> logger, string? scriptsPathOverride, string? configPathOverride)
        {
            _logger = logger;
            _scriptsPath = scriptsPathOverride ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BPScripts");
            _configPath = configPathOverride ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "bp-scripts.json");
            Directory.CreateDirectory(_scriptsPath);
            _config = LoadConfig();
            SyncScriptsFromFolder();
        }

        public BPScriptConfig GetConfig() => _config;

        public void UpdateScript(BPScript script)
        {
            var existing = _config.Scripts.FirstOrDefault(s => s.Id == script.Id);
            if (existing != null)
            {
                var index = _config.Scripts.IndexOf(existing);
                _config.Scripts[index] = script;
            }
            else
            {
                _config.Scripts.Add(script);
            }
            SaveConfig();
        }

        public void UpdateScriptOrder(List<BPScript> orderedScripts)
        {
            for (int i = 0; i < orderedScripts.Count; i++)
            {
                orderedScripts[i].Order = i;
            }
            _config.Scripts = orderedScripts;
            SaveConfig();
        }

        public string GetScriptContent(string fileName)
        {
            var path = Path.Combine(_scriptsPath, fileName);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        public void SaveScriptContent(string fileName, string content)
        {
            var path = Path.Combine(_scriptsPath, fileName);
            File.WriteAllText(path, content);
        }

        public void SyncScriptsFromFolder()
        {
            var files = Directory.GetFiles(_scriptsPath, "*.sql");
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                if (!_config.Scripts.Any(s => s.FileName == fileName))
                {
                    _config.Scripts.Add(new BPScript
                    {
                        Id = Guid.NewGuid().ToString(),
                        FileName = fileName,
                        DisplayName = Path.GetFileNameWithoutExtension(fileName),
                        Order = _config.Scripts.Count
                    });
                }
            }
            SaveConfig();
        }

        /// <summary>
        /// How bp-scripts.json came off disk.
        ///
        /// <para><b>Warned about, NOT refused — and this is the store where refusing would be most
        /// clearly wrong.</b> What it holds is display names, ordering and generated ids for the
        /// .sql files in BPScripts\. The scripts themselves are those files; none of their CONTENT
        /// is here. <see cref="SyncScriptsFromFolder"/> rebuilds an entry for every file it finds,
        /// so a damaged store self-heals into a working list on the next start — the only real loss
        /// is the operator's chosen labels and running order.</para>
        ///
        /// <para>It is also the one store whose replacing write happens with nobody watching: the
        /// constructor loads, then syncs, then saves, so on a damaged file the overwrite is done
        /// before any page renders. That makes the log line below the ONLY evidence the file was
        /// ever damaged, which is why it is worth writing even though nothing is being protected.</para>
        /// </summary>
        private ConfigLoadOutcome _load = ConfigLoadOutcome.Missing;

        private string? _quarantine;

        private BPScriptConfig LoadConfig()
        {
            var loaded = ConfigFileHelper.Load<BPScriptConfig>(_configPath, _jsonOptions, out _load, out _quarantine);

            if (_load is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty)
                _logger.LogWarning(
                    "[BPScripts] {Path} exists and did not load ({Outcome}). Display names and ordering are lost; "
                    + "the script list is about to be rebuilt from the files in BPScripts\\ and written back over "
                    + "it, at startup, before anyone can intervene. No script content is affected. {Recovery}",
                    _configPath, _load, ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));

            return loaded;
        }

        private void SaveConfig()
        {
            var replacingDamage = _load is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty;
            try
            {
                ConfigFileHelper.Save(_configPath, _config, _jsonOptions);
                if (replacingDamage)
                    _logger.LogWarning(
                        "[BPScripts] {Path} did not load ({Outcome}) and has now been REPLACED by the {Count} "
                        + "script entr(ies) rebuilt from the folder. {Recovery}",
                        _configPath, _load, _config.Scripts.Count,
                        ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));

                _load = ConfigLoadOutcome.Loaded;
                _quarantine = null;
            }
            catch (Exception ex) { _logger.LogError(ex, "Failed to save BP script config"); }
        }
    }
}
