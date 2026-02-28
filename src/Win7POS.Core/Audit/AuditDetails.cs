using System;
using System.Text;

namespace Win7POS.Core.Audit
{
    public static class AuditDetails
    {
        public static string Kv(params (string k, string v)[] items)
        {
            if (items == null || items.Length == 0)
                return string.Empty;

            var sb = new StringBuilder();
            for (var i = 0; i < items.Length; i++)
            {
                if (i > 0) sb.Append("; ");
                var key = Sanitize(items[i].k);
                if (string.IsNullOrWhiteSpace(key)) key = "k";
                var value = Sanitize(items[i].v);
                sb.Append(key);
                sb.Append('=');
                sb.Append(value);
            }
            return sb.ToString();
        }

        private static string Sanitize(string value)
        {
            var text = value ?? string.Empty;
            text = text.Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");
            text = text.Replace(";", ",");
            return text.Trim();
        }
    }
}
