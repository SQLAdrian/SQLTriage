/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SQLTriage.Data.Models
{
    /// <summary>
    /// Reads a JSON boolean written in any of the shapes a legacy or third-party producer might
    /// use for it: a real JSON bool (<c>true</c>/<c>false</c>), any JSON number (zero is false, anything else is true; 0/1 is the expected shape), or the strings
    /// <c>"true"</c>/<c>"false"</c> or <c>"0"</c>/<c>"1"</c>. Applied ONLY to
    /// <see cref="CheckResult.LegacyIsBad"/> (lane hygiene-tail, 2026-09-08) because that
    /// property reads run files this app did not necessarily write itself: the results-import
    /// path (<c>QuickCheckResultStore.TryParseRunPayload</c>, consumed by
    /// <c>ImportResultsService</c>/the Import page/<c>--import</c>) accepts a file from outside
    /// this process, and <c>QuickCheckResultStore.GetServersWithRuns</c> HAD a bare
    /// <c>catch { /* skip corrupt file */ }</c> around the same deserialization with no log,
    /// so a numeric legacy key (which the plain bool converter rejects with a
    /// <see cref="JsonException"/>) silently dropped that server from every server-with-runs
    /// listing. Both the "external file" and the "silent vanish" conditions were real, so this
    /// converter is warranted, per the decision rule in the same lane's brief. Round 2 of the
    /// same lane (Adrian's ruling, 2026-09-08) made both of that method's catches log a Warning
    /// naming the file; the converter's own messages stay content-free because those warnings
    /// carry the exception text into an operator-readable log.
    ///
    /// This is a read-time tolerance only: <see cref="CheckResult.LegacyIsBad"/> stays set-only,
    /// so nothing this converter accepts is ever written back out.
    /// </summary>
    public sealed class LegacyBoolConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.True:
                    return true;
                case JsonTokenType.False:
                    return false;
                case JsonTokenType.Number:
                    if (reader.TryGetInt64(out var n)) return n != 0;
                    return reader.GetDouble() != 0;
                case JsonTokenType.String:
                    var s = reader.GetString();
                    if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1") return true;
                    if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) || s == "0") return false;
                    // Content-free on purpose. A run file is UNTRUSTED input (see the type doc
                    // above), and QuickCheckResultStore.GetServersWithRuns now writes this
                    // exception to an operator-readable log — interpolating the raw value here
                    // would put a run file's contents (a 10 MB string value, in whole) into that
                    // log. The length and the token type are the failure SHAPE; that is enough to
                    // diagnose, and System.Text.Json's own added "Path/LineNumber/BytePositionInLine"
                    // suffix carries JSON property names and offsets only, never a value.
                    throw new JsonException(
                        $"Cannot convert a string of length {s?.Length ?? 0} to bool for a legacy IsBad value.");
                default:
                    throw new JsonException($"Unexpected token {reader.TokenType} for a legacy IsBad value.");
            }
        }

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        {
            // Never reached: LegacyIsBad has no getter, so System.Text.Json never serializes it.
            // Implemented anyway because JsonConverter<bool> is abstract.
            writer.WriteBooleanValue(value);
        }
    }
}
