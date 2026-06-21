using System;

namespace M2C.Checkout.Internal
{
    internal enum ReturnVerdict
    {
        Success,
        Cancel,
        Unknown
    }

    /// <summary>
    /// Classifies a return URL against the configured success/cancel URLs and
    /// recovers the request_id. Pure and platform-free so it can be unit-tested
    /// without Unity: every browser strategy funnels its return URL through here.
    /// </summary>
    internal static class ReturnClassifier
    {
        /// <summary>
        /// Decide success vs cancel for <paramref name="returnUrl"/>. Matches the
        /// URL (ignoring its query string) against the cancel URL first, then the
        /// success URL. Anything else is ignored by browser strategies when they can
        /// keep listening, or surfaced as an invalid return by the core. The
        /// <paramref name="fallbackRequestId"/> is returned when the URL carries no
        /// <c>request_id</c> query param.
        /// </summary>
        public static ReturnVerdict Classify(string returnUrl, string successUrl, string cancelUrl, string fallbackRequestId, out string requestId)
        {
            requestId = ExtractRequestId(returnUrl) ?? fallbackRequestId;

            string baseOf(string u) => StripQuery(u);
            string ret = baseOf(returnUrl);

            if (!string.IsNullOrEmpty(cancelUrl) && Matches(ret, baseOf(cancelUrl)))
                return ReturnVerdict.Cancel;
            if (!string.IsNullOrEmpty(successUrl) && Matches(ret, baseOf(successUrl)))
                return ReturnVerdict.Success;
            return ReturnVerdict.Unknown;
        }

        public static bool IsConfiguredReturn(string returnUrl, string successUrl, string cancelUrl)
        {
            return Classify(returnUrl, successUrl, cancelUrl, null, out _) != ReturnVerdict.Unknown;
        }

        public static bool HasMismatchedRequestId(string returnUrl, string expectedRequestId)
        {
            string actual = ExtractRequestId(returnUrl);
            return !string.IsNullOrEmpty(actual)
                   && !string.IsNullOrEmpty(expectedRequestId)
                   && !string.Equals(actual, expectedRequestId, StringComparison.OrdinalIgnoreCase);
        }

        internal static string ExtractRequestId(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            int q = url.IndexOf('?');
            if (q < 0 || q == url.Length - 1) return null;
            string query = url.Substring(q + 1);
            // Strip a fragment if present.
            int hash = query.IndexOf('#');
            if (hash >= 0) query = query.Substring(0, hash);
            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                string key = pair.Substring(0, eq);
                if (key == "request_id")
                {
                    string val = pair.Substring(eq + 1);
                    return Uri.UnescapeDataString(val);
                }
            }
            return null;
        }

        private static string StripQuery(string url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;
            int q = url.IndexOf('?');
            string s = q >= 0 ? url.Substring(0, q) : url;
            int hash = s.IndexOf('#');
            if (hash >= 0) s = s.Substring(0, hash);
            return s.TrimEnd('/');
        }

        private static bool Matches(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (!a.StartsWith(b, StringComparison.OrdinalIgnoreCase)) return false;
            return a.Length == b.Length || a[b.Length] == '/';
        }
    }
}
