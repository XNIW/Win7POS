using System;
using System.Security.Cryptography;
using System.Text;

namespace Win7POS.Core.Online
{
    /// <summary>
    /// Produces a stable, bounded correlation fingerprint suitable for logs and
    /// support artifacts without disclosing the original identifier.
    /// </summary>
    public static class PosTechnicalIdentifier
    {
        public static string Redact(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            byte[] bytes = null;
            byte[] hash = null;
            try
            {
                bytes = Encoding.UTF8.GetBytes(value.Trim());
                using (var sha = SHA256.Create())
                {
                    hash = sha.ComputeHash(bytes);
                }

                var builder = new StringBuilder(19);
                builder.Append("sha256:");
                for (var index = 0; index < 6; index++)
                {
                    builder.Append(hash[index].ToString("x2"));
                }

                return builder.ToString();
            }
            finally
            {
                if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
                if (hash != null) Array.Clear(hash, 0, hash.Length);
            }
        }
    }
}
