/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data.Services.FrameworkTree;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Synthesized ancestor nodes ("03", "03.01") were rendering with no name at all, because
    /// the mapping corpus only names the exact control a check maps to. These tests pin the
    /// family-title stand-in — and, more importantly, pin that we resolve the framework label
    /// the way the corpus actually writes it, which is where this kind of table usually rots.
    /// </summary>
    public class FrameworkFamilyNamesTests
    {
        [Theory]
        // The exact labels used elsewhere in the tree tests / corpus.
        [InlineData("NIST SP 800-171 Rev 3", "03.01", "Access Control")]
        [InlineData("NIST SP 800-171 Rev 3", "3.1", "Access Control")]      // rev2 numbering
        [InlineData("NIST SP 800-53 Rev 5", "AC", "Access Control")]
        [InlineData("FedRAMP High", "AU", "Audit and Accountability")]
        [InlineData("ISO/IEC 27001:2022", "8", "Technological Controls")]   // note: "iso/iec", not "iso 27001"
        [InlineData("PCI-DSS v4.0", "8", "Identify Users and Authenticate Access")]
        [InlineData("CIS Controls v8", "5", "Account Management")]
        public void Resolves_family_titles_for_labels_the_corpus_actually_uses(
            string framework, string fullId, string expected)
        {
            Assert.Equal(expected, FrameworkFamilyNames.TryGet(framework, fullId));
        }

        [Fact]
        public void Nist_80053_family_lookup_is_case_insensitive()
        {
            Assert.Equal("Configuration Management", FrameworkFamilyNames.TryGet("FedRAMP Moderate", "cm"));
        }

        /// <summary>
        /// The CIS SQL Server Benchmark numbers product areas, not the CIS Controls v8
        /// families — labelling its section 5 "Account Management" would be plain wrong.
        /// </summary>
        [Fact]
        public void Cis_sql_server_benchmark_does_not_borrow_cis_controls_titles()
        {
            Assert.Null(FrameworkFamilyNames.TryGet("CIS SQL Server Benchmark", "5"));
        }

        [Theory]
        [InlineData("Totally Made Up Framework v9", "3")]
        [InlineData("NIST SP 800-171 Rev 3", "99.99")]
        [InlineData("NIST SP 800-53 Rev 5", "ZZ")]
        [InlineData("", "3")]
        [InlineData("PCI-DSS v4.0", "")]
        public void Returns_null_rather_than_guessing(string framework, string fullId)
        {
            // Null is a good answer — ComplianceTree renders a neutral child-count summary.
            Assert.Null(FrameworkFamilyNames.TryGet(framework, fullId));
        }

        [Fact]
        public void Never_throws_on_null_input()
        {
            Assert.Null(FrameworkFamilyNames.TryGet(null, null));
            Assert.Null(FrameworkFamilyNames.TryGet(null, "03.01"));
            Assert.Null(FrameworkFamilyNames.TryGet("NIST SP 800-171 Rev 3", null));
        }
    }
}
