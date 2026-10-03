using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Win7POS.Core.Operations
{
    public sealed class PortableSettingsProfile
    {
        public string ApplicationVersion { get; }
        public DateTime ExportedUtc { get; }
        public IReadOnlyDictionary<string, string> Settings { get; }
        public string Checksum { get; }
        private PortableSettingsProfile(string version, DateTime utc, IReadOnlyDictionary<string, string> settings)
        {
            if (version == null || version.Length == 0 || version.Length > 64 || version.Any(c => !char.IsLetterOrDigit(c) && c != '.' && c != '-' && c != '+'))
                throw new ArgumentException("Invalid application version.");
            if (utc.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC is required.");
            PortableSettingsPolicy.Validate(settings);
            ApplicationVersion = version;
            ExportedUtc = utc;
            Settings = new ReadOnlyDictionary<string, string>(settings.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
            Checksum = PortableSettingsPolicy.Hash(Canonical(false));
        }
        public static PortableSettingsProfile Create(string version, DateTime utc, IReadOnlyDictionary<string, string> settings) => new PortableSettingsProfile(version, utc, settings);
        public string ToJson() => Canonical(true);
        private string Canonical(bool checksum)
        {
            return "{\"schemaVersion\":1,\"applicationVersion\":" + Quote(ApplicationVersion) + ",\"exportedUtc\":" + Quote(ExportedUtc.ToString("O", CultureInfo.InvariantCulture)) +
                ",\"settings\":{" + string.Join(",", Settings.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => Quote(x.Key) + ":" + Quote(x.Value))) +
                "},\"redactedKeys\":[" + string.Join(",", PortableSettingsPolicy.RedactedKeys.Select(Quote)) + "]" +
                (checksum ? ",\"checksum\":" + Quote(Checksum) : "") + "}";
        }
        private static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            foreach (var c in value)
            {
                if (c == '"' || c == '\\') result.Append('\\').Append(c);
                else if (c < 32 || char.IsSurrogate(c)) result.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else result.Append(c);
            }
            return result.Append('"').ToString();
        }
        public static PortableSettingsProfile Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > PortableSettingsPolicy.MaximumFileBytes) throw new ArgumentException("Profile size is invalid.");
            string json;
            try { json = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { throw new ArgumentException("Profile encoding is invalid."); }
            var parser = new Reader(json);
            var root = parser.Object();
            parser.End();
            var expected = new[] { "schemaVersion", "applicationVersion", "exportedUtc", "settings", "redactedKeys", "checksum" };
            if (root.Count != expected.Length || expected.Any(key => !root.ContainsKey(key)) || !(root["schemaVersion"] is int version) || version != 1 ||
                !(root["applicationVersion"] is string app) || !(root["exportedUtc"] is string utcText) || !(root["settings"] is Dictionary<string, object> raw) ||
                !(root["redactedKeys"] is List<object> redacted) || !(root["checksum"] is string checksum)) throw new ArgumentException("Profile schema is invalid.");
            if (!DateTime.TryParseExact(utcText, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var utc) || utc.Kind != DateTimeKind.Utc ||
                raw.Values.Any(x => !(x is string)) || redacted.Any(x => !(x is string)) ||
                !redacted.Cast<string>().SequenceEqual(PortableSettingsPolicy.RedactedKeys, StringComparer.Ordinal)) throw new ArgumentException("Profile metadata is invalid.");
            var settings = raw.ToDictionary(x => x.Key, x => (string)x.Value, StringComparer.Ordinal);
            var profile = new PortableSettingsProfile(app, utc, settings);
            if (checksum.Length != 64 || !string.Equals(profile.Checksum, checksum, StringComparison.Ordinal)) throw new ArgumentException("Profile checksum is invalid.");
            return profile;
        }

        // A deliberately bounded JSON grammar for this schema. No object is deserialized before
        // duplicate names, depth, trailing input, string length and primitive types are checked.
        private sealed class Reader
        {
            private readonly string _text;
            private int _at;
            private int _depth;
            internal Reader(string text) { _text = text; }
            internal Dictionary<string, object> Object()
            {
                Enter(); Expect('{');
                var values = new Dictionary<string, object>(StringComparer.Ordinal);
                if (!Take('}'))
                {
                    do
                    {
                        var key = String(); Expect(':');
                        var value = Value();
                        if (values.ContainsKey(key) || values.Count >= 128) Fail();
                        values.Add(key, value);
                    } while (Take(','));
                    Expect('}');
                }
                _depth--; return values;
            }
            private object Value()
            {
                White();
                if (_at >= _text.Length) Fail();
                if (_text[_at] == '{') return Object();
                if (_text[_at] == '"') return String();
                if (_text[_at] == '[')
                {
                    Enter(); Expect('['); var list = new List<object>();
                    if (!Take(']'))
                    {
                        do { if (list.Count >= 128) Fail(); list.Add(Value()); } while (Take(','));
                        Expect(']');
                    }
                    _depth--; return list;
                }
                // Schema version is the sole numeric primitive and must be the canonical integer 1.
                if (Take('1')) return 1;
                Fail(); return null;
            }
            private string String()
            {
                Expect('"'); var value = new StringBuilder(); var closed = false;
                while (_at < _text.Length)
                {
                    var c = _text[_at++];
                    if (c == '"') { closed = true; break; }
                    if (c < 32) Fail();
                    if (c == '\\')
                    {
                        if (_at >= _text.Length) Fail();
                        var escape = _text[_at++];
                        switch (escape)
                        {
                            case '"': c = '"'; break; case '\\': c = '\\'; break; case '/': c = '/'; break;
                            case 'b': c = '\b'; break; case 'f': c = '\f'; break; case 'n': c = '\n'; break;
                            case 'r': c = '\r'; break; case 't': c = '\t'; break;
                            case 'u':
                                ushort code = 0;
                                if (_at + 4 > _text.Length || !ushort.TryParse(_text.Substring(_at, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code)) Fail();
                                c = (char)code; _at += 4; break;
                            default: Fail(); break;
                        }
                    }
                    value.Append(c); if (value.Length > 512) Fail();
                }
                if (!closed) Fail();
                var result = value.ToString();
                for (var i = 0; i < result.Length; i++)
                    if (char.IsSurrogate(result[i]))
                    {
                        if (!char.IsHighSurrogate(result[i]) || i + 1 == result.Length || !char.IsLowSurrogate(result[++i])) Fail();
                    }
                return result;
            }
            private void Enter() { if (++_depth > 4) Fail(); }
            private void White() { while (_at < _text.Length && (_text[_at] == ' ' || _text[_at] == '\r' || _text[_at] == '\n' || _text[_at] == '\t')) _at++; }
            private bool Take(char c) { White(); if (_at < _text.Length && _text[_at] == c) { _at++; return true; } return false; }
            private void Expect(char c) { if (!Take(c)) Fail(); }
            internal void End() { White(); if (_at != _text.Length) Fail(); }
            private static void Fail() { throw new ArgumentException("Profile JSON is invalid."); }
        }
    }
}
