using System;
using System.Collections.Generic;
using System.IO;
using Win7POS.Core;

namespace Win7POS.Wpf.Pos.Online
{
    public sealed class PosAdminWebOptions
    {
        public const string BaseUrlEnvironmentVariable = "WIN7POS_ADMIN_WEB_BASE_URL";
        public const string AllowInsecureLanEnvironmentVariable = "WIN7POS_ALLOW_INSECURE_LAN_ADMIN_WEB";
        public const string ConfigFileName = "pos-admin-web.config";
        public const string ConfigBaseUrlKey = "AdminWebBaseUrl";
        public const string ReasonMissingBaseUrl = "missing_base_url";
        public const string ReasonInvalidUrl = "invalid_url";
        public const string ReasonInvalidScheme = "invalid_scheme";
        public const string ReasonUrlIncludesCredentials = "url_includes_credentials";
        public const string ReasonUrlBaseOnly = "url_base_only";
        public const string ReasonHttpLoopbackOnly = "http_loopback_only";

        public PosAdminWebOptions(Uri baseUri)
        {
            BaseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
        }

        public Uri BaseUri { get; }

        public static string ConfigFilePath => Path.Combine(AppPaths.DataDirectory, ConfigFileName);

        public static bool TryLoad(out PosAdminWebOptions options, out string reason)
        {
            return TryLoad(out options, out reason, out _);
        }

        public static bool TryLoad(out PosAdminWebOptions options, out string reason, out string reasonCode)
        {
            options = null;
            reason = null;
            reasonCode = null;

            var rawBaseUrl = Environment.GetEnvironmentVariable(BaseUrlEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(rawBaseUrl))
            {
                rawBaseUrl = TryReadBaseUrlFromConfig();
            }

            if (string.IsNullOrWhiteSpace(rawBaseUrl))
            {
                reasonCode = ReasonMissingBaseUrl;
                reason = "URL Admin Web non configurato. Configura il server nelle impostazioni avanzate o tramite " + BaseUrlEnvironmentVariable + ".";
                return false;
            }

            if (!TryCreateBaseUri(rawBaseUrl, out var baseUri, out reason, out reasonCode))
            {
                return false;
            }

            options = new PosAdminWebOptions(baseUri);
            return true;
        }

        public static bool TryCreate(string value, out PosAdminWebOptions options, out string reason)
        {
            return TryCreate(value, out options, out reason, out _);
        }

        public static bool TryCreate(string value, out PosAdminWebOptions options, out string reason, out string reasonCode)
        {
            options = null;
            reason = null;
            reasonCode = null;

            if (string.IsNullOrWhiteSpace(value))
            {
                reasonCode = ReasonMissingBaseUrl;
                reason = "URL Admin Web non configurato. Configura il server nelle impostazioni avanzate o tramite " + BaseUrlEnvironmentVariable + ".";
                return false;
            }

            if (!TryCreateBaseUri(value, out var baseUri, out reason, out reasonCode))
            {
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

        public static bool TryCreateBaseUri(string value, out Uri uri, out string reason)
        {
            return TryCreateBaseUri(value, out uri, out reason, out _);
        }

        public static bool TryCreateBaseUri(string value, out Uri uri, out string reason, out string reasonCode)
        {
            uri = null;
            reason = null;
            reasonCode = null;
            var normalized = (value ?? string.Empty).Trim().TrimEnd('/');

            if (!Uri.TryCreate(normalized + "/", UriKind.Absolute, out var parsed))
            {
                reasonCode = ReasonInvalidUrl;
                reason = "L'URL Admin Web POS non e valido.";
                return false;
            }

            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            {
                reasonCode = ReasonInvalidScheme;
                reason = "L'URL Admin Web POS deve usare HTTPS oppure HTTP loopback.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(parsed.UserInfo))
            {
                reasonCode = ReasonUrlIncludesCredentials;
                reason = "Inserisci solo l'URL base HTTPS del pannello, senza username o password nell'indirizzo.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(parsed.Query) || !string.IsNullOrWhiteSpace(parsed.Fragment))
            {
                reasonCode = ReasonUrlBaseOnly;
                reason = "Inserisci solo l'URL base HTTPS del pannello senza /auth/login o /shop.";
                return false;
            }

            var path = parsed.AbsolutePath ?? "/";
            if (path != "/")
            {
                reasonCode = ReasonUrlBaseOnly;
                reason = "Inserisci solo l'URL base HTTPS del pannello senza /auth/login o /shop.";
                return false;
            }

            if (parsed.Scheme == Uri.UriSchemeHttp && !parsed.IsLoopback && !AllowInsecureLanAdminWeb())
            {
                reasonCode = ReasonHttpLoopbackOnly;
                reason = "HTTP e consentito solo per localhost/127.0.0.1. Usa HTTPS per workers.dev/staging.";
                return false;
            }

            uri = parsed;
            return true;
        }

        public static bool AllowInsecureLanAdminWeb()
        {
            return string.Equals(
                Environment.GetEnvironmentVariable(AllowInsecureLanEnvironmentVariable),
                "1",
                StringComparison.Ordinal);
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
