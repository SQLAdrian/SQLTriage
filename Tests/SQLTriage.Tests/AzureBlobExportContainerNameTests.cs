/* In the name of God, the Merciful, the Compassionate */

// Board #39. The container-name fallback in AzureBlobExportService was "SQLTriage" while the
// value Config/appsettings.json ships is "sqltriage". Azure container names are lowercase-only
// (3-63 chars of [a-z0-9-], first and last alphanumeric), so every upload that fell back to the
// literal was rejected BY THE SERVICE - a failure no local test could see, which is why it
// survived. The triage proved the rejection by differential HTTP against Azure.
//
// What this file pins is the SHAPE, not one literal: every container-name default the class
// exposes must satisfy the Azure naming rule, and the two runtime paths that can reach a
// default (LoadConfiguration with no config, Configure with a blank name) must land on one.
// Re-uppercase AzureBlobExportService.DefaultContainerName and all four facts go red.

using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class AzureBlobExportContainerNameTests
    {
        /// <summary>
        /// Azure Blob container naming rule, verbatim: 3-63 characters, lowercase letters,
        /// digits and hyphens only, first and last character alphanumeric.
        /// </summary>
        private const string AzureContainerNamePattern = "^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])$";

        private static AzureBlobExportService NewService(params (string Key, string? Value)[] settings)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
                .Build();
            return new AzureBlobExportService(NullLogger<AzureBlobExportService>.Instance, config);
        }

        [Fact]
        public void EveryContainerNameDefaultInTheClass_SatisfiesTheAzureNamingRule()
        {
            // Reflection, not a hard-coded list: a second default added later is covered the day
            // it lands. The count assertion stops the loop passing vacuously over zero fields.
            var defaults = typeof(AzureBlobExportService)
                .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
                .Where(f => f.Name.Contains("ContainerName"))
                .ToList();

            Assert.True(defaults.Count >= 1,
                "No container-name default was found on AzureBlobExportService. The guard would " +
                "pass over nothing; if the constant was renamed, rename it here too.");

            foreach (var field in defaults)
            {
                var value = (string?)field.GetRawConstantValue();
                Assert.True(value is not null && Regex.IsMatch(value, AzureContainerNamePattern),
                    $"AzureBlobExportService.{field.Name} = '{value}' is not a legal Azure container " +
                    "name (3-63 chars, lowercase [a-z0-9-], first and last alphanumeric). Azure " +
                    "rejects the upload at the service; nothing local catches it.");
            }
        }

        [Fact]
        public void DefaultContainerName_MatchesTheValueAppsettingsShips()
        {
            // The fallback exists to stand in for the shipped setting. If the two ever differ,
            // an unconfigured install writes to a different container than a configured one.
            Assert.Equal("sqltriage", AzureBlobExportService.DefaultContainerName);
        }

        [Fact]
        public void LoadConfiguration_WithNoContainerConfigured_FallsBackToALegalName()
        {
            var svc = NewService();

            Assert.NotNull(svc.ContainerName);
            Assert.Matches(AzureContainerNamePattern, svc.ContainerName!);
        }

        [Fact]
        public void Configure_WithABlankContainerName_FallsBackToALegalName()
        {
            var svc = NewService(("AzureBlobExport:ContainerName", "someothercontainer"));

            svc.Configure(connectionString: null, sasToken: null, storageAccountName: null,
                          containerName: "   ");

            Assert.NotNull(svc.ContainerName);
            Assert.Matches(AzureContainerNamePattern, svc.ContainerName!);
        }
    }
}
