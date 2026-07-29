using System;

namespace M2C.Checkout.Internal
{
    internal enum ReturnVerdict
    {
        Success,
        Cancel,
        Unknown
    }

    internal struct ReturnClassification
    {
        public ReturnVerdict Verdict;
        public string RequestId;
        public string Error;
    }

    /// <summary>Pure return URL classification shared by every browser strategy.</summary>
    internal static class ReturnClassifier
    {
        internal const string MalformedUrl = "malformed_url";
        internal const string RequestIdMismatch = "request_id_mismatch";

        public static ReturnClassification Classify(
            string returnUrl,
            string successUrl,
            string cancelUrl,
            string expectedRequestId)
        {
            Uri returned;
            if (!TryAbsolute(returnUrl, out returned))
            {
                return new ReturnClassification
                {
                    Verdict = ReturnVerdict.Unknown,
                    Error = MalformedUrl
                };
            }

            string requestId;
            try
            {
                requestId = ExtractRequestId(returned);
            }
            catch (UriFormatException)
            {
                return new ReturnClassification
                {
                    Verdict = ReturnVerdict.Unknown,
                    Error = MalformedUrl
                };
            }

            Uri success;
            Uri cancel;
            bool hasSuccess = TryAbsolute(successUrl, out success);
            bool hasCancel = TryAbsolute(cancelUrl, out cancel);
            ReturnVerdict verdict = hasCancel && Matches(returned, cancel)
                ? ReturnVerdict.Cancel
                : hasSuccess && Matches(returned, success)
                    ? ReturnVerdict.Success
                    : ReturnVerdict.Unknown;

            if (verdict != ReturnVerdict.Unknown
                && !string.IsNullOrEmpty(requestId)
                && !string.IsNullOrEmpty(expectedRequestId)
                && !string.Equals(requestId, expectedRequestId, StringComparison.Ordinal))
            {
                return new ReturnClassification
                {
                    Verdict = ReturnVerdict.Unknown,
                    RequestId = requestId,
                    Error = RequestIdMismatch
                };
            }

            return new ReturnClassification
            {
                Verdict = verdict,
                RequestId = requestId ?? (verdict == ReturnVerdict.Unknown ? null : expectedRequestId)
            };
        }

        public static bool IsConfiguredReturn(string returnUrl, string successUrl, string cancelUrl)
        {
            return Classify(returnUrl, successUrl, cancelUrl, null).Verdict != ReturnVerdict.Unknown;
        }

        internal static string ExtractRequestId(string url)
        {
            Uri parsed;
            return TryAbsolute(url, out parsed) ? ExtractRequestId(parsed) : null;
        }

        private static string ExtractRequestId(Uri url)
        {
            string query = url.Query;
            if (string.IsNullOrEmpty(query) || query == "?") return null;
            foreach (string pair in query.Substring(1).Split('&'))
            {
                int equals = pair.IndexOf('=');
                if (equals <= 0 || pair.Substring(0, equals) != "request_id") continue;
                return Uri.UnescapeDataString(pair.Substring(equals + 1));
            }
            return null;
        }

        private static bool TryAbsolute(string value, out Uri parsed)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out parsed)
                   && !string.IsNullOrEmpty(parsed.Scheme);
        }

        private static bool Matches(Uri actual, Uri configured)
        {
            if (!string.Equals(actual.Scheme, configured.Scheme, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(actual.Host, configured.Host, StringComparison.OrdinalIgnoreCase)
                || actual.Port != configured.Port)
                return false;

            string actualPath = NormalizePath(actual.AbsolutePath);
            string configuredPath = NormalizePath(configured.AbsolutePath);
            if (string.Equals(actualPath, configuredPath, StringComparison.Ordinal)) return true;
            string prefix = configuredPath == "/" ? "/" : configuredPath + "/";
            return actualPath.StartsWith(prefix, StringComparison.Ordinal);
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "/";
            return path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal)
                ? path.Substring(0, path.Length - 1)
                : path;
        }
    }
}
