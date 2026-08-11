using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public static class PosProductImageStorageOrigin
    {
        public const string EnvironmentVariable = "WIN7POS_PRODUCT_IMAGE_STORAGE_ORIGIN";
        public const string ConfigKey = "ProductImageStorageOrigin";
        public const string AssemblyMetadataKey = "ProductImageStorageOrigin";

        public static bool TryLoad(out Uri origin, out string code)
        {
            origin = null;
            code = "product_image_storage_origin_missing";
            var raw = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(raw)) raw = ReadConfigValue();
            if (string.IsNullOrWhiteSpace(raw)) raw = ReadPackagedValue();
            if (string.IsNullOrWhiteSpace(raw)) return false;
            Uri parsed;
            if (!Uri.TryCreate(raw.Trim().TrimEnd('/') + "/", UriKind.Absolute, out parsed) ||
                (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                 !(parsed.IsLoopback &&
                   string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))) ||
                !string.IsNullOrEmpty(parsed.UserInfo) ||
                parsed.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(parsed.Query) ||
                !string.IsNullOrEmpty(parsed.Fragment))
            {
                code = "product_image_storage_origin_invalid";
                return false;
            }
            origin = parsed;
            code = "success";
            return true;
        }

        private static string ReadConfigValue()
        {
            try
            {
                if (!File.Exists(PosAdminWebOptions.ConfigFilePath)) return null;
                foreach (var line in File.ReadAllLines(PosAdminWebOptions.ConfigFilePath))
                {
                    var trimmed = (line ?? string.Empty).Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                        continue;
                    var separator = trimmed.IndexOf('=');
                    if (separator <= 0) continue;
                    if (string.Equals(
                        trimmed.Substring(0, separator).Trim(),
                        ConfigKey,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return trimmed.Substring(separator + 1).Trim();
                    }
                }
            }
            catch
            {
                return null;
            }
            return null;
        }

        private static string ReadPackagedValue()
        {
            try
            {
                return ReadAssembly(Assembly.GetEntryAssembly()) ??
                       ReadAssembly(Assembly.GetExecutingAssembly());
            }
            catch
            {
                return null;
            }
        }

        private static string ReadAssembly(Assembly assembly)
        {
            if (assembly == null) return null;
            return assembly
                .GetCustomAttributes(typeof(AssemblyMetadataAttribute), false)
                .OfType<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => string.Equals(
                    attribute.Key,
                    AssemblyMetadataKey,
                    StringComparison.Ordinal))
                ?.Value;
        }
    }
}
