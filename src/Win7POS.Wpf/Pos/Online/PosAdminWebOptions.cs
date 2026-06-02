using System;
using System.Collections.Generic;
using System.IO;
using Win7POS.Core;

namespace Win7POS.Wpf.Pos.Online
{
    public sealed class PosAdminWebOptions
    {
        public const string BaseUrlEnvironmentVariable = "WIN7POS_ADMIN_WEB_BASE_URL";
        public const string ConfigFileName = "pos-admin-web.config";
        public const string ConfigBaseUrlKey = "AdminWebBaseUrl";

        public PosAdminWebOptions(Uri baseUri)
        {
            BaseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
        }

        public Uri BaseUri { get; }

        public static string ConfigFilePath => Path.Combine(AppPaths.DataDirectory, ConfigFileName);

        public static bool TryLoad(out PosAdminWebOptions options, out string reason)
        {
            options = null;
            reason = null;

            var rawBaseUrl = Environment.GetEnvironmentVariable(BaseUrlEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(rawBaseUrl))
            {
                rawBaseUrl = TryReadBaseUrlFromConfig();
            }

            if (string.IsNullOrWhiteSpace(rawBaseUrl))
            {
                reason = "Configura " + BaseUrlEnvironmentVariable + " oppure " + ConfigFilePath + ".";
                return false;
            }

            if (!TryCreateBaseUri(rawBaseUrl, out var baseUri))
            {
                reason = "La configurazione Admin Web POS non contiene un URL valido.";
                return false;
            }

            options = new PosAdminWebOptions(baseUri);
            return true;
        }

        public static bool TryCreate(string value, out PosAdminWebOptions options, out string reason)
        {
            options = null;
            reason = null;

            if (string.IsNullOrWhiteSpace(value))
            {
                reason = "Inserisci l'URL Admin Web POS.";
                return false;
            }

            if (!TryCreateBaseUri(value, out var baseUri))
            {
                reason = "L'URL Admin Web POS non e valido.";
                return false;
            }

            options = new PosAdminWebOptions(baseUri);
            return true;
        }

        public static void SaveBaseUrl(Uri baseUri)
        {
            if (baseUri == null)
            {
                throw new ArgumentNullException(nameof(baseUri));
            }

            AppPaths.EnsureDataDirectories();
            File.WriteAllText(
                ConfigFilePath,
                ConfigBaseUrlKey + "=" + baseUri.ToString().TrimEnd('/') + Environment.NewLine);
        }

        private static bool TryCreateBaseUri(string value, out Uri uri)
        {
            uri = null;
            var normalized = (value ?? string.Empty).Trim().TrimEnd('/');

            if (!Uri.TryCreate(normalized + "/", UriKind.Absolute, out var parsed))
            {
                return false;
            }

            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            uri = parsed;
            return true;
        }

        private static string TryReadBaseUrlFromConfig()
        {
            try
            {
                var path = ConfigFilePath;
                if (!File.Exists(path))
                {
                    return null;
                }

                foreach (var pair in ReadKeyValueFile(path))
                {
                    if (string.Equals(pair.Key, ConfigBaseUrlKey, StringComparison.OrdinalIgnoreCase))
                    {
                        return pair.Value;
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static IEnumerable<KeyValuePair<string, string>> ReadKeyValueFile(string path)
        {
            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = (rawLine ?? string.Empty).Trim();

                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();

                if (key.Length > 0)
                {
                    yield return new KeyValuePair<string, string>(key, value);
                }
            }
        }
    }
}
