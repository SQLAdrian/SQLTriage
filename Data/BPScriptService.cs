/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlHealthAssessment.Data.Models;

namespace SqlHealthAssessment.Data
{
    public class BPScriptService
    {
        private readonly ILogger<BPScriptService> _logger;
        private readonly string _scriptsPath;
        private readonly string _configPath;
        private BPScriptConfig _config;
        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        public BPScriptService(ILogger<BPScriptService> logger)
        {
            _logger = logger;
            _scriptsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BPScripts");
            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "bp-scripts.json");
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

        private BPScriptConfig LoadConfig() => ConfigFileHelper.Load<BPScriptConfig>(_configPath, _jsonOptions);

        private void SaveConfig()
        {
            try { ConfigFileHelper.Save(_configPath, _config, _jsonOptions); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to save BP script config"); }
        }
    }
}
