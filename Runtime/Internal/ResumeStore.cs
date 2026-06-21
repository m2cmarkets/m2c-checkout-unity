using UnityEngine;

namespace M2C.Checkout.Internal
{
    /// <summary>
    /// Persists the in-progress checkout's request id so a process killed during
    /// checkout (common on mobile under memory pressure) can resume the status poll
    /// when a deep link relaunches the app. Callback status sources cannot be
    /// serialized, so only their kind is recorded; the app must recreate the callback
    /// in config before calling TryResumeAsync.
    /// </summary>
    internal static class ResumeStore
    {
        private const string KeyVersion = "m2c.checkout.v";
        private const string KeyRequestId = "m2c.checkout.request_id";
        private const string KeyMode = "m2c.checkout.mode";
        private const string KeyStatusKind = "m2c.checkout.status_kind";
        private const string KeyStatusUrlTemplate = "m2c.checkout.status_url_template";
        private const string KeyActive = "m2c.checkout.active";

        public static void Save(string requestId, string mode, StatusSource statusSource)
        {
            StatusSource src = statusSource ?? StatusSource.M2C;
            PlayerPrefs.SetInt(KeyVersion, 1);
            PlayerPrefs.SetString(KeyRequestId, requestId ?? string.Empty);
            PlayerPrefs.SetString(KeyMode, mode ?? string.Empty);
            PlayerPrefs.SetString(KeyStatusKind, src.Kind.ToString());
            if (src.Kind == StatusSourceKind.Url)
                PlayerPrefs.SetString(KeyStatusUrlTemplate, src.UrlTemplate ?? string.Empty);
            else
                PlayerPrefs.DeleteKey(KeyStatusUrlTemplate);
            PlayerPrefs.SetInt(KeyActive, 1);
            PlayerPrefs.Save();
        }

        /// <summary>The unfinished checkout resume record, or null if none is pending.</summary>
        public static ResumeRecord PendingRecord()
        {
            if (PlayerPrefs.GetInt(KeyActive, 0) != 1) return null;
            string id = PlayerPrefs.GetString(KeyRequestId, string.Empty);
            if (string.IsNullOrEmpty(id)) return null;

            StatusSourceKind kind;
            string rawKind = PlayerPrefs.GetString(KeyStatusKind, StatusSourceKind.M2C.ToString());
            if (!System.Enum.TryParse<StatusSourceKind>(rawKind, out kind))
                kind = StatusSourceKind.M2C;

            return new ResumeRecord
            {
                RequestId = id,
                Mode = PlayerPrefs.GetString(KeyMode, string.Empty),
                StatusKind = kind,
                StatusUrlTemplate = PlayerPrefs.GetString(KeyStatusUrlTemplate, string.Empty),
            };
        }

        public static void Clear()
        {
            PlayerPrefs.DeleteKey(KeyVersion);
            PlayerPrefs.DeleteKey(KeyRequestId);
            PlayerPrefs.DeleteKey(KeyMode);
            PlayerPrefs.DeleteKey(KeyStatusKind);
            PlayerPrefs.DeleteKey(KeyStatusUrlTemplate);
            PlayerPrefs.DeleteKey(KeyActive);
            PlayerPrefs.Save();
        }
    }

    internal sealed class ResumeRecord
    {
        public string RequestId;
        public string Mode;
        public StatusSourceKind StatusKind;
        public string StatusUrlTemplate;
    }
}
