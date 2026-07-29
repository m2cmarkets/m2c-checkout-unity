using System;

namespace M2C.Checkout.Internal
{
    internal static class UrlValidator
    {
        internal const string RequestIdToken = "{request_id}";

        public static bool IsValidHttpsOrLoopbackHttp(string value)
        {
            Uri parsed;
            if (!Uri.TryCreate(value, UriKind.Absolute, out parsed) || string.IsNullOrEmpty(parsed.Host))
                return false;
            if (string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return true;
            string rawHost;
            return string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                   && TryGetRawHost(value, out rawHost)
                   && IsLoopbackHost(rawHost);
        }

        public static bool IsValidStatusTemplate(string template)
        {
            if (string.IsNullOrEmpty(template) || !template.Contains(RequestIdToken)) return false;
            return IsValidHttpsOrLoopbackHttp(template.Replace(RequestIdToken, "m2c-request-probe"));
        }

        public static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            string normalized = host.Trim('[', ']');
            if (string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "::1", StringComparison.OrdinalIgnoreCase))
                return true;

            string[] parts = normalized.Split('.');
            if (parts.Length != 4 || parts[0] != "127") return false;
            foreach (string part in parts)
            {
                int octet;
                if (part.Length == 0 || !int.TryParse(part, out octet) || octet < 0 || octet > 255)
                    return false;
                foreach (char c in part)
                    if (c < '0' || c > '9') return false;
            }
            return true;
        }

        // System.Uri canonicalizes legacy numeric and shortened IPv4 spellings
        // (for example 2130706433) into 127/8. Inspect the original authority so
        // only the explicit shared-contract spellings receive the HTTP exception.
        internal static bool TryGetRawHost(string value, out string host)
        {
            host = null;
            int scheme = value.IndexOf("://", StringComparison.Ordinal);
            if (scheme < 0) return false;
            int start = scheme + 3;
            int end = value.IndexOfAny(new[] { '/', '?', '#' }, start);
            string authority = end < 0 ? value.Substring(start) : value.Substring(start, end - start);
            int at = authority.LastIndexOf('@');
            if (at >= 0)
            {
                // A literal @ inside userinfo must be percent-encoded. Rejecting
                // additional separators keeps host selection independent of URI
                // parser leniency.
                if (authority.IndexOf('@') != at) return false;
                authority = authority.Substring(at + 1);
            }
            if (authority.StartsWith("[", StringComparison.Ordinal))
            {
                int close = authority.IndexOf(']');
                if (close <= 0) return false;
                string suffix = authority.Substring(close + 1);
                if (suffix.Length > 0 && !suffix.StartsWith(":", StringComparison.Ordinal)) return false;
                host = authority.Substring(1, close - 1);
                return true;
            }

            int colon = authority.LastIndexOf(':');
            host = colon >= 0 ? authority.Substring(0, colon) : authority;
            return !string.IsNullOrEmpty(host);
        }
    }
}
