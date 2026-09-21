/* In the name of God, the Merciful, the Compassionate */

using System.Text.Json;
using SQLTriage.Data.Models;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Pins <see cref="LegacyBoolConverter"/> against the four shapes a run file's "IsBad" key
    /// can arrive in (lane hygiene-tail, 2026-09-08; see CheckResult.LegacyIsBad's doc comment
    /// for why this converter is warranted). Deserializes through <c>CheckResult</c> directly —
    /// the property under test lives there, and <c>QuickCheckResultStore.RunPayload</c> just
    /// wraps a <c>List&lt;CheckResult&gt;</c>.
    /// </summary>
    public class LegacyBoolConverterTests
    {
        private static bool DeserializeFlaggedAdverse(string checkResultJson)
        {
            var result = JsonSerializer.Deserialize<CheckResult>(checkResultJson)!;
            return result.DefinitionFlaggedAdverse;
        }

        [Fact]
        public void Real_json_bool_true_sets_the_flag()
        {
            Assert.True(DeserializeFlaggedAdverse("""{"IsBad": true}"""));
        }

        [Fact]
        public void Real_json_bool_false_clears_the_flag()
        {
            Assert.False(DeserializeFlaggedAdverse("""{"IsBad": false}"""));
        }

        [Fact]
        public void Numeric_one_is_treated_as_true()
        {
            Assert.True(DeserializeFlaggedAdverse("""{"IsBad": 1}"""));
        }

        [Fact]
        public void Numeric_zero_is_treated_as_false()
        {
            Assert.False(DeserializeFlaggedAdverse("""{"IsBad": 0}"""));
        }

        [Fact]
        public void String_true_is_treated_as_true()
        {
            Assert.True(DeserializeFlaggedAdverse("""{"IsBad": "true"}"""));
        }

        [Fact]
        public void String_false_is_treated_as_false()
        {
            Assert.False(DeserializeFlaggedAdverse("""{"IsBad": "false"}"""));
        }

        [Fact]
        public void Both_keys_present_resolves_by_document_order_later_wins()
        {
            // "IsBad" first, "DefinitionFlaggedAdverse" second -> the second (real) property wins,
            // because LegacyIsBad has no getter so it never overwrites what was already set when
            // it runs first; but if DefinitionFlaggedAdverse comes SECOND in the document it is
            // the last write and therefore the one that sticks either way. This exercises the
            // documented "later one wins" behaviour with the legacy key first.
            Assert.False(DeserializeFlaggedAdverse("""{"IsBad": true, "DefinitionFlaggedAdverse": false}"""));
            Assert.True(DeserializeFlaggedAdverse("""{"DefinitionFlaggedAdverse": false, "IsBad": true}"""));
        }
    }
}
