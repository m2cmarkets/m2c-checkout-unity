using System;
using System.Text;
using UnityEngine;

namespace M2C.Checkout.Internal
{
    /// <summary>One bounded, versioned recovery record persisted as one logical write.</summary>
    internal static class ResumeStore
    {
        internal const int MaxBytes = 16 * 1024;
        internal const string RecordKey = "m2c.checkout.resume";
        private static readonly string[] LegacyKeys =
        {
            "m2c.checkout.v",
            "m2c.checkout.request_id",
            "m2c.checkout.mode",
            "m2c.checkout.status_kind",
            "m2c.checkout.status_url_template",
            "m2c.checkout.active"
        };

        public static void Save(string requestId, string mode, StatusSource statusSource)
        {
            ResumeRecord record = ResumeRecord.Create(requestId, mode, statusSource ?? StatusSource.M2C);
            string json = Encode(record);
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "checkout recovery record is too large");

            DeleteLegacyKeys();
            PlayerPrefs.SetString(RecordKey, json);
            PlayerPrefs.Save();
        }

        /// <summary>The unfinished checkout resume record, or null if none is pending.</summary>
        public static ResumeRecord PendingRecord()
        {
            bool removedLegacy = DeleteLegacyKeys();
            if (!PlayerPrefs.HasKey(RecordKey))
            {
                if (removedLegacy) PlayerPrefs.Save();
                return null;
            }

            string json = PlayerPrefs.GetString(RecordKey, string.Empty);
            ResumeRecord record;
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes || !TryDecode(json, out record))
            {
                PlayerPrefs.DeleteKey(RecordKey);
                PlayerPrefs.Save();
                return null;
            }
            if (removedLegacy) PlayerPrefs.Save();
            return record;
        }

        public static void Clear()
        {
            PlayerPrefs.DeleteKey(RecordKey);
            DeleteLegacyKeys();
            PlayerPrefs.Save();
        }

        internal static string Encode(ResumeRecord record)
        {
            return JsonUtility.ToJson(new ResumeRecordDto
            {
                version = 1,
                request_id = record.RequestId,
                mode = record.Mode,
                status_kind = KindName(record.StatusKind),
                status_url_template = record.StatusKind == StatusSourceKind.Url
                    ? record.StatusUrlTemplate
                    : null
            });
        }

        internal static bool TryDecode(string json, out ResumeRecord record)
        {
            record = null;
            if (string.IsNullOrEmpty(json)) return false;
            ResumeRecordDto dto;
            try { dto = JsonUtility.FromJson<ResumeRecordDto>(json); }
            catch { return false; }
            if (dto == null || dto.version != 1 || string.IsNullOrEmpty(dto.request_id)) return false;
            if (dto.mode != "client" && dto.mode != "session") return false;

            StatusSourceKind kind;
            switch (dto.status_kind)
            {
                case "m2c": kind = StatusSourceKind.M2C; break;
                case "url": kind = StatusSourceKind.Url; break;
                case "callback": kind = StatusSourceKind.Callback; break;
                default: return false;
            }
            if (kind == StatusSourceKind.Url && !UrlValidator.IsValidStatusTemplate(dto.status_url_template))
                return false;

            record = new ResumeRecord
            {
                RequestId = dto.request_id,
                Mode = dto.mode,
                StatusKind = kind,
                StatusUrlTemplate = kind == StatusSourceKind.Url ? dto.status_url_template : null
            };
            return true;
        }

        private static string KindName(StatusSourceKind kind)
        {
            switch (kind)
            {
                case StatusSourceKind.Url: return "url";
                case StatusSourceKind.Callback: return "callback";
                default: return "m2c";
            }
        }

        private static bool DeleteLegacyKeys()
        {
            bool changed = false;
            foreach (string key in LegacyKeys)
            {
                if (!PlayerPrefs.HasKey(key)) continue;
                PlayerPrefs.DeleteKey(key);
                changed = true;
            }
            return changed;
        }
    }

    [Serializable]
    internal sealed class ResumeRecordDto
    {
        public int version;
        public string request_id;
        public string mode;
        public string status_kind;
        public string status_url_template;
    }

    internal sealed class ResumeRecord
    {
        public string RequestId;
        public string Mode;
        public StatusSourceKind StatusKind;
        public string StatusUrlTemplate;

        public static ResumeRecord Create(string requestId, string mode, StatusSource source)
        {
            if (string.IsNullOrEmpty(requestId) || (mode != "client" && mode != "session"))
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "invalid checkout recovery record");
            if (source.Kind == StatusSourceKind.Subscribe)
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "subscribe status source is not implemented in v1");
            if (source.Kind == StatusSourceKind.Url && !UrlValidator.IsValidStatusTemplate(source.UrlTemplate))
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "invalid status URL in checkout recovery record");
            return new ResumeRecord
            {
                RequestId = requestId,
                Mode = mode,
                StatusKind = source.Kind,
                StatusUrlTemplate = source.Kind == StatusSourceKind.Url ? source.UrlTemplate : null
            };
        }
    }
}
