/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SqlHealthAssessment.Data
{
    public class ConfigurationValidator
    {
        private readonly IConfiguration _config;
        private readonly ILogger<ConfigurationValidator> _logger;

        public ConfigurationValidator(IConfiguration config, ILogger<ConfigurationValidator> logger)
        {
            _config = config;
            _logger = logger;
        }

        public (bool IsValid, List<string> Errors) Validate()
        {
            var errors = new List<string>();

            // Connection String validation (optional — connections are managed via ServerConnectionManager)
            var connStr = _config.GetConnectionString("SqlServer");
            if (!string.IsNullOrWhiteSpace(connStr))
            {
                try
                {
                    var builder = new SqlConnectionStringBuilder(connStr);

                    var isIntegratedSecurity = builder.IntegratedSecurity;
                    var hasUserID = !string.IsNullOrEmpty(builder.UserID);

                    if (!isIntegratedSecurity && !hasUserID)
                        errors.Add("Connection string must use either Integrated Security or provide User ID");

                    if (string.IsNullOrEmpty(builder.DataSource))
                        errors.Add("Connection string must specify Data Source (Server)");
                }
                catch (Exception ex)
                {
                    errors.Add($"Invalid connection string format: {ex.Message}");
                }
            }

            // Query Timeout (up to 3600s to support long-running diagnostic scripts)
            var timeout = _config.GetValue<int>("QueryTimeoutSeconds", 60);
            if (timeout <= 0 || timeout > 3600)
                errors.Add("QueryTimeoutSeconds must be between 1 and 3600");

            // Max Query Rows
            var maxRows = _config.GetValue<int>("MaxQueryRows", 10000);
            if (maxRows <= 0 || maxRows > 100000)
                errors.Add("MaxQueryRows must be between 1 and 100000");

            // Refresh Interval (optional — may be set in user-settings.json instead)
            var refreshInterval = _config.GetValue<int>("RefreshIntervalSeconds", 0);
            if (refreshInterval != 0 && (refreshInterval < 1 || refreshInterval > 3600))
                errors.Add("RefreshIntervalSeconds must be between 1 and 3600");

            // Log Level
            var logLevel = _config["Logging:LogLevel:Default"];
            if (string.IsNullOrEmpty(logLevel))
                _logger.LogWarning("Logging:LogLevel:Default is not set — Serilog defaults will apply");
            else if (!IsValidLogLevel(logLevel))
                errors.Add($"Invalid log level '{logLevel}'. Valid values: Trace, Debug, Information, Warning, Error, Critical");

            // Cache settings
            var cacheSizeMB = _config.GetValue<int>("MaxCacheSizeMB", 0);
            if (cacheSizeMB != 0 && cacheSizeMB < 10)
                errors.Add("MaxCacheSizeMB must be at least 10 when specified");

            var cacheEvictionHours = _config.GetValue<int>("CacheEvictionHours", 0);
            if (cacheEvictionHours != 0 && (cacheEvictionHours < 1 || cacheEvictionHours > 720))
                errors.Add("CacheEvictionHours must be between 1 and 720");

            // Connection pool
            var poolSize = _config.GetValue<int>("ConnectionPool:MaxSize", 0);
            if (poolSize != 0 && (poolSize < 5 || poolSize > 500))
                errors.Add("ConnectionPool:MaxSize must be between 5 and 500");

            // Audit log retention
            var auditRetention = _config.GetValue<int>("AuditLogRetentionDays", 0);
            if (auditRetention != 0 && (auditRetention < 1 || auditRetention > 365))
                errors.Add("AuditLogRetentionDays must be between 1 and 365");

            // Validate TrustServerCertificate setting
            var trustCert = _config.GetValue<bool>("TrustServerCertificate", false);
            if (trustCert)
                _logger.LogWarning("TrustServerCertificate is enabled — only use in development/test environments");

            var isValid = !errors.Any();
            if (isValid)
                _logger.LogInformation("Configuration validation passed");
            else
                _logger.LogError("Configuration validation failed: {Errors}", string.Join(", ", errors));

            return (isValid, errors);
        }

        private static bool IsValidLogLevel(string level)
        {
            return level?.ToLowerInvariant() switch
            {
                "trace" => true,
                "debug" => true,
                "information" => true,
                "warning" => true,
                "error" => true,
                "critical" => true,
                _ => false
            };
        }
    }
}
