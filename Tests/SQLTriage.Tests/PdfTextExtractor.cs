/* In the name of God, the Merciful, the Compassionate */
/*
 * PdfTextExtractor — read the human-visible text back out of a QuestPDF/Skia PDF.
 *
 * WHY THIS EXISTS: several honesty invariants in this repo are of the form "a client-facing
 * document must SAY X" (ruling #4, 2026-07-20: a degraded run must show "N checks could not be
 * fully assessed"). Asserting that against the DTO only pins the DTO — the export it names can
 * still drop the row, which is exactly the defect the cold gate found. This extractor lets a test
 * assert against the bytes a client actually receives.
 *
 * HOW: Skia writes text as hex-encoded SUBSET-FONT GLYPH IDS (`<00360034002F> Tj`), not literal
 * characters, so a naive byte scan for "Partial" finds nothing even when the word is on the page —
 * the false-negative that makes a byte scan worse than useless here. Each font carries a
 * /ToUnicode CMap mapping its glyph ids back to Unicode. Glyph ids are font-LOCAL: the same id
 * means different characters in different subset fonts, so the CMaps must be applied PER FONT,
 * tracked through the content stream's `/F7 11 Tf` operators. A merged all-fonts map silently
 * mistranslates; this walks the real object graph instead.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SQLTriage.Tests
{
    internal static class PdfTextExtractor
    {
        /// <summary>All human-visible text in the document, pages in order, space-separated.</summary>
        public static string Extract(byte[] pdf)
        {
            var raw = Encoding.Latin1.GetString(pdf);
            var objects = ParseObjects(pdf, raw);

            // objnum -> (glyph id -> unicode string), built lazily per font.
            var cmapCache = new Dictionary<int, Dictionary<int, string>>();

            var sb = new StringBuilder();
            foreach (var (num, obj) in objects.OrderBy(kv => kv.Key))
            {
                if (!Regex.IsMatch(obj.Dict, @"/Type\s*/Page\b")) continue;

                // /Resources may be inline or an indirect reference.
                var resDict = ResolveDict(obj.Dict, "/Resources", objects);
                var fontMap = ParseFontResources(resDict, objects);

                foreach (var contentNum in ContentRefs(obj.Dict))
                {
                    if (!objects.TryGetValue(contentNum, out var content) || content.Stream == null) continue;
                    sb.Append(DecodeContent(Encoding.Latin1.GetString(content.Stream), fontMap, objects, cmapCache));
                    sb.Append('\n');
                }
            }
            return sb.ToString();
        }

        private sealed record PdfObject(string Dict, byte[]? Stream);

        private static Dictionary<int, PdfObject> ParseObjects(byte[] pdf, string raw)
        {
            var result = new Dictionary<int, PdfObject>();
            foreach (Match m in Regex.Matches(raw, @"(\d+)\s+\d+\s+obj\b"))
            {
                int num = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                int start = m.Index + m.Length;
                int end = raw.IndexOf("endobj", start, StringComparison.Ordinal);
                if (end < 0) continue;

                int streamAt = raw.IndexOf("stream", start, StringComparison.Ordinal);
                string dict;
                byte[]? data = null;

                if (streamAt >= 0 && streamAt < end)
                {
                    dict = raw.Substring(start, streamAt - start);
                    int ds = streamAt + "stream".Length;
                    while (ds < raw.Length && (raw[ds] == '\r' || raw[ds] == '\n')) ds++;
                    int es = raw.IndexOf("endstream", ds, StringComparison.Ordinal);
                    if (es > ds)
                    {
                        var blob = new byte[es - ds];
                        Array.Copy(pdf, ds, blob, 0, blob.Length);
                        data = dict.Contains("/FlateDecode", StringComparison.Ordinal) ? Inflate(blob) : blob;
                    }
                }
                else dict = raw.Substring(start, end - start);

                result[num] = new PdfObject(dict, data);
            }
            return result;
        }

        private static byte[]? Inflate(byte[] blob)
        {
            try
            {
                using var ms = new MemoryStream(blob);
                using var zs = new ZLibStream(ms, CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                zs.CopyTo(outMs);
                return outMs.ToArray();
            }
            catch { return null; }
        }

        /// <summary>Return the dictionary text for <paramref name="key"/>, following one indirect ref.</summary>
        private static string ResolveDict(string dict, string key, Dictionary<int, PdfObject> objects)
        {
            var refM = Regex.Match(dict, Regex.Escape(key) + @"\s+(\d+)\s+\d+\s+R\b");
            if (refM.Success)
            {
                int n = int.Parse(refM.Groups[1].Value, CultureInfo.InvariantCulture);
                return objects.TryGetValue(n, out var o) ? o.Dict : "";
            }
            int at = dict.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return "";
            return BalancedDict(dict, dict.IndexOf("<<", at, StringComparison.Ordinal));
        }

        /// <summary>Extract a &lt;&lt; ... &gt;&gt; block with nesting, starting at <paramref name="open"/>.</summary>
        private static string BalancedDict(string s, int open)
        {
            if (open < 0) return "";
            int depth = 0;
            for (int i = open; i < s.Length - 1; i++)
            {
                if (s[i] == '<' && s[i + 1] == '<') { depth++; i++; continue; }
                if (s[i] == '>' && s[i + 1] == '>')
                {
                    depth--; i++;
                    if (depth == 0) return s.Substring(open, i - open + 1);
                }
            }
            return "";
        }

        /// <summary>/Font &lt;&lt; /F7 12 0 R /F8 15 0 R &gt;&gt;  →  { "F7": 12, "F8": 15 }.</summary>
        private static Dictionary<string, int> ParseFontResources(string resDict, Dictionary<int, PdfObject> objects)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(resDict)) return map;

            string fontDict;
            var refM = Regex.Match(resDict, @"/Font\s+(\d+)\s+\d+\s+R\b");
            if (refM.Success)
            {
                int n = int.Parse(refM.Groups[1].Value, CultureInfo.InvariantCulture);
                fontDict = objects.TryGetValue(n, out var o) ? o.Dict : "";
            }
            else
            {
                int at = resDict.IndexOf("/Font", StringComparison.Ordinal);
                fontDict = at < 0 ? "" : BalancedDict(resDict, resDict.IndexOf("<<", at, StringComparison.Ordinal));
            }

            foreach (Match m in Regex.Matches(fontDict, @"/(\w+)\s+(\d+)\s+\d+\s+R\b"))
                map[m.Groups[1].Value] = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return map;
        }

        private static IEnumerable<int> ContentRefs(string pageDict)
        {
            var single = Regex.Match(pageDict, @"/Contents\s+(\d+)\s+\d+\s+R\b");
            if (single.Success) { yield return int.Parse(single.Groups[1].Value, CultureInfo.InvariantCulture); yield break; }

            var arr = Regex.Match(pageDict, @"/Contents\s*\[(.*?)\]", RegexOptions.Singleline);
            if (!arr.Success) yield break;
            foreach (Match m in Regex.Matches(arr.Groups[1].Value, @"(\d+)\s+\d+\s+R\b"))
                yield return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        /// <summary>Glyph id → unicode, from a font object's /ToUnicode CMap stream.</summary>
        private static Dictionary<int, string> CMapFor(int fontObjNum,
            Dictionary<int, PdfObject> objects, Dictionary<int, Dictionary<int, string>> cache)
        {
            if (cache.TryGetValue(fontObjNum, out var hit)) return hit;
            var map = new Dictionary<int, string>();
            cache[fontObjNum] = map;

            if (!objects.TryGetValue(fontObjNum, out var font)) return map;

            // A Type0 font defers to its descendant; /ToUnicode lives on the parent either way.
            var tu = Regex.Match(font.Dict, @"/ToUnicode\s+(\d+)\s+\d+\s+R\b");
            if (!tu.Success) return map;
            int cmapNum = int.Parse(tu.Groups[1].Value, CultureInfo.InvariantCulture);
            if (!objects.TryGetValue(cmapNum, out var cmapObj) || cmapObj.Stream == null) return map;

            var text = Encoding.Latin1.GetString(cmapObj.Stream);

            foreach (Match block in Regex.Matches(text, @"beginbfchar(.*?)endbfchar", RegexOptions.Singleline))
                foreach (Match p in Regex.Matches(block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>"))
                    map[Convert.ToInt32(p.Groups[1].Value, 16)] = HexToString(p.Groups[2].Value);

            foreach (Match block in Regex.Matches(text, @"beginbfrange(.*?)endbfrange", RegexOptions.Singleline))
                foreach (Match p in Regex.Matches(block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>"))
                {
                    int lo = Convert.ToInt32(p.Groups[1].Value, 16);
                    int hi = Convert.ToInt32(p.Groups[2].Value, 16);
                    int dst = Convert.ToInt32(p.Groups[3].Value, 16);
                    for (int g = lo; g <= hi && g - lo < 512; g++)
                        map[g] = char.ConvertFromUtf32(dst + (g - lo));
                }

            return map;
        }

        private static string HexToString(string hex)
        {
            var sb = new StringBuilder();
            for (int i = 0; i + 3 < hex.Length + 1 && i + 4 <= hex.Length; i += 4)
                sb.Append((char)Convert.ToInt32(hex.Substring(i, 4), 16));
            return sb.ToString();
        }

        /// <summary>Walk a content stream, tracking the current font, decoding every shown string.</summary>
        private static string DecodeContent(string content, Dictionary<string, int> fontMap,
            Dictionary<int, PdfObject> objects, Dictionary<int, Dictionary<int, string>> cache)
        {
            var sb = new StringBuilder();
            Dictionary<int, string>? current = null;

            // Font selections and text-showing operators, in document order.
            var rx = new Regex(@"/(\w+)\s+[\d.]+\s+Tf|<([0-9A-Fa-f\s]+)>\s*Tj|\[(.*?)\]\s*TJ", RegexOptions.Singleline);
            foreach (Match m in rx.Matches(content))
            {
                if (m.Groups[1].Success)
                {
                    current = fontMap.TryGetValue(m.Groups[1].Value, out var objNum)
                        ? CMapFor(objNum, objects, cache) : null;
                }
                else if (m.Groups[2].Success)
                {
                    sb.Append(DecodeHex(m.Groups[2].Value, current));
                    sb.Append(' ');
                }
                else if (m.Groups[3].Success)
                {
                    foreach (Match h in Regex.Matches(m.Groups[3].Value, @"<([0-9A-Fa-f\s]+)>"))
                        sb.Append(DecodeHex(h.Groups[1].Value, current));
                    sb.Append(' ');
                }
            }
            return sb.ToString();
        }

        private static string DecodeHex(string hex, Dictionary<int, string>? cmap)
        {
            hex = Regex.Replace(hex, @"\s", "");
            var sb = new StringBuilder();
            for (int i = 0; i + 4 <= hex.Length; i += 4)
            {
                int glyph = Convert.ToInt32(hex.Substring(i, 4), 16);
                if (cmap != null && cmap.TryGetValue(glyph, out var s)) sb.Append(s);
                else sb.Append('�');   // unmapped: visible, never silently dropped
            }
            return sb.ToString();
        }
    }
}
