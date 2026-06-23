using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace M2C.Checkout.Internal
{
    // Wire DTOs. Field names are snake_case to match the JSON keys, because
    // UnityEngine.JsonUtility maps by exact field name (no remapping) and is the
    // one IL2CPP-stripping-safe parser we can rely on.
    [Serializable]
    internal sealed class WinnerDto
    {
        public string checkout_url;
        public int ttl;
    }

    [Serializable]
    internal sealed class AuctionResponseDto
    {
        public WinnerDto winner;
        public string request_id;
    }

    [Serializable]
    internal sealed class StatusResponseDto
    {
        public string request_id;
        public string status;
    }

    [Serializable]
    internal sealed class ErrorDto
    {
        public string error;
    }

    /// <summary>The fields of an auction the client needs (no pricing).</summary>
    internal struct AuctionResult
    {
        public string CheckoutUrl;
        public string RequestId;
        public int Ttl;
    }

    /// <summary>A completed HTTP exchange (transport ok), or a transport failure.</summary>
    internal struct HttpResponse
    {
        public bool TransportOk;
        public string TransportError;
        public long Status;
        public string Text;
        public string RetryAfter;
    }

    /// <summary>
    /// Thin UnityWebRequest wrapper + M2C-specific request builders, error mapping,
    /// and status reads. All continuations resume on Unity's main thread via the
    /// default synchronization context, so callers never touch worker threads.
    /// </summary>
    internal sealed class M2CApi
    {
        private const double MinTransactionValue = 0.000001;
        private const double MaxTransactionValue = 5000000000.0;
        private const string OfficialBaseUrl = "https://api.m2cmarkets.com";
        internal const int DefaultHttpTimeoutSeconds = 30;

        private readonly string _baseUrl;
        private readonly string _publishableKey;

        public M2CApi(string publishableKey)
        {
            _baseUrl = OfficialBaseUrl;
            _publishableKey = publishableKey;
        }

        public async Task<AuctionResult> CreateAuctionAsync(AuctionRequest req)
        {
            string body = BuildAuctionBody(req);
            HttpResponse res = await Http.PostJsonAsync(_baseUrl + "/api/v1/auction", body, _publishableKey, DefaultHttpTimeoutSeconds);
            if (!res.TransportOk)
                throw new M2CCheckoutException(M2CErrorCode.Network, res.TransportError ?? "network error");
            if (res.Status < 200 || res.Status >= 300)
                throw MapError(res);

            var dto = JsonUtilitySafe.From<AuctionResponseDto>(res.Text);
            if (dto?.winner == null || string.IsNullOrEmpty(dto.winner.checkout_url) || string.IsNullOrEmpty(dto.request_id))
                throw new M2CCheckoutException(M2CErrorCode.Unknown, "malformed auction response");
            return new AuctionResult { CheckoutUrl = dto.winner.checkout_url, RequestId = dto.request_id, Ttl = dto.winner.ttl };
        }

        /// <summary>Reads status from M2C's endpoint. A 404 (row not visible yet) maps to Processing so the caller keeps polling.</summary>
        public async Task<ClientStatus> ReadStatusM2CAsync(string requestId, double timeoutBudgetSeconds = 0)
        {
            string url = _baseUrl + "/api/v1/conversions/" + UnityWebRequest.EscapeURL(requestId);
            HttpResponse res = await Http.GetAsync(url, _publishableKey, RequestTimeoutSeconds(timeoutBudgetSeconds));
            if (!res.TransportOk)
                throw new M2CCheckoutException(M2CErrorCode.Network, res.TransportError ?? "network error");
            if (res.Status == 404) return ClientStatus.Processing;
            if (res.Status >= 500)
                throw new M2CCheckoutException(M2CErrorCode.ServiceUnavailable, "status read returned HTTP " + res.Status, (int)res.Status);
            if (res.Status < 200 || res.Status >= 300) throw MapError(res);
            var dto = JsonUtilitySafe.From<StatusResponseDto>(res.Text);
            return ParseClientStatus(dto?.status);
        }

        /// <summary>Reads status from a merchant URL template (no API key).</summary>
        public static async Task<ClientStatus> ReadStatusUrlAsync(string template, string requestId, double timeoutBudgetSeconds = 0)
        {
            string url = template.Replace("{request_id}", UnityWebRequest.EscapeURL(requestId));
            HttpResponse res = await Http.GetAsync(url, null, RequestTimeoutSeconds(timeoutBudgetSeconds));
            if (!res.TransportOk)
                throw new M2CCheckoutException(M2CErrorCode.Network, res.TransportError ?? "network error");
            if (res.Status < 200 || res.Status >= 300)
                throw new M2CCheckoutException(M2CErrorCode.ServiceUnavailable, "status read failed: HTTP " + res.Status, (int)res.Status);
            var dto = JsonUtilitySafe.From<StatusResponseDto>(res.Text);
            return ParseClientStatus(dto?.status);
        }

        internal static int RequestTimeoutSeconds(double timeoutBudgetSeconds)
        {
            if (double.IsNaN(timeoutBudgetSeconds) || double.IsInfinity(timeoutBudgetSeconds) || timeoutBudgetSeconds <= 0)
                return DefaultHttpTimeoutSeconds;
            if (timeoutBudgetSeconds >= int.MaxValue)
                return int.MaxValue;
            return Math.Max(1, (int)Math.Ceiling(timeoutBudgetSeconds));
        }

        internal static ClientStatus ParseClientStatus(string s)
        {
            switch (s)
            {
                case "completed":
                case "refunded":
                case "chargedback":
                    return ClientStatus.Completed;
                case "failed": return ClientStatus.Failed;
                case "canceled":
                case "abandoned":
                    return ClientStatus.Canceled;
                default: return ClientStatus.Processing; // "processing" and anything unrecognized
            }
        }

        internal static string BuildAuctionBody(AuctionRequest req)
        {
            ValidateTransactionValue(req.TransactionValue);

            return new JsonWriter()
                .Number("transaction_value", req.TransactionValue)
                .String("currency", req.Currency)
                .String("description", req.Description)
                .String("success_url", req.SuccessUrl)
                .String("cancel_url", req.CancelUrl)
                .String("reference", req.Reference)
                .StringArray("segments", req.Segments)
                .String("language", req.Language)
                .String("device_type", DetectDeviceType())
                .String("platform", DetectCheckoutPlatform())
                .Build();
        }

        // Coarse device form factor (mobile / desktop), auto-detected at runtime
        // and sent on every auction - same no-override model as DetectCheckoutPlatform.
        // SystemInfo.deviceType works in every build, including WebGL (where it
        // reflects the browser's device), so unlike platform it isn't gated to
        // non-editor builds. Handheld covers phone and tablet (Unity doesn't split
        // them); Console/Unknown send no value (omitted by JsonWriter), recorded as
        // "unknown". Metadata only - never affects auth or fulfillment.
        private static string DetectDeviceType()
        {
            switch (UnityEngine.SystemInfo.deviceType)
            {
                case UnityEngine.DeviceType.Handheld:
                    return "mobile";
                case UnityEngine.DeviceType.Desktop:
                    return "desktop";
                default:
                    return "";
            }
        }

        // Checkout surface metadata, auto-detected from the build target so the
        // dashboard and conversion webhook can attribute by surface. Sent on every
        // auction; the server treats it as metadata only (never auth/fulfillment).
        // Standalone/desktop has no supported checkout flow (CheckoutBrowserFactory
        // rejects it) and the Editor runs the simulated browser, so both send no
        // value (omitted by JsonWriter) and the server records "unknown".
        private static string DetectCheckoutPlatform()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return "webgl";
#elif UNITY_IOS && !UNITY_EDITOR
            return "ios";
#elif UNITY_ANDROID && !UNITY_EDITOR
            return "android";
#else
            return "";
#endif
        }

        private static void ValidateTransactionValue(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "transaction value must be a finite number");
            if (value <= 0)
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "transaction value must be greater than 0");
            if (value < MinTransactionValue)
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "transaction value must be at least 0.000001");
            if (value > MaxTransactionValue)
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "transaction value exceeds the maximum");
        }

        internal static M2CCheckoutException MapError(HttpResponse res)
        {
            int retryAfter = 0;
            int.TryParse(res.RetryAfter, out retryAfter);
            string msg = ParseErrorMessage(res.Text) ?? ("HTTP " + res.Status);
            switch (res.Status)
            {
                case 400: return new M2CCheckoutException(M2CErrorCode.InvalidRequest, msg, 400);
                case 403:
                    var code = (msg != null && msg.ToLowerInvariant().Contains("suspend"))
                        ? M2CErrorCode.AccountSuspended
                        : M2CErrorCode.OriginNotAllowed;
                    return new M2CCheckoutException(code, msg, 403);
                case 404: return new M2CCheckoutException(M2CErrorCode.NoVendorsAvailable, msg, 404);
                case 429: return new M2CCheckoutException(M2CErrorCode.RateLimited, msg, 429, retryAfter);
                case 503: return new M2CCheckoutException(M2CErrorCode.ServiceUnavailable, msg, 503);
                default: return new M2CCheckoutException(M2CErrorCode.Unknown, msg, (int)res.Status);
            }
        }

        private static string ParseErrorMessage(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var dto = JsonUtilitySafe.From<ErrorDto>(text);
            return dto != null && !string.IsNullOrEmpty(dto.error) ? dto.error : null;
        }
    }

    internal static class Http
    {
        public static async Task<HttpResponse> PostJsonAsync(string url, string body, string apiKey, int timeoutSeconds = 0)
        {
            using (var req = new UnityWebRequest(url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(apiKey)) req.SetRequestHeader("X-API-Key", apiKey);
                if (timeoutSeconds > 0) req.timeout = timeoutSeconds;
                return await SendAsync(req);
            }
        }

        public static async Task<HttpResponse> GetAsync(string url, string apiKey, int timeoutSeconds = 0)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                if (!string.IsNullOrEmpty(apiKey)) req.SetRequestHeader("X-API-Key", apiKey);
                if (timeoutSeconds > 0) req.timeout = timeoutSeconds;
                return await SendAsync(req);
            }
        }

        private static Task<HttpResponse> SendAsync(UnityWebRequest req)
        {
            var tcs = new TaskCompletionSource<HttpResponse>();
            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                // Result is Unity 2020.2+; the package baseline is 2021.3.
                bool transportError = req.result == UnityWebRequest.Result.ConnectionError
                                      || req.result == UnityWebRequest.Result.DataProcessingError;
                tcs.TrySetResult(new HttpResponse
                {
                    TransportOk = !transportError,
                    TransportError = transportError ? req.error : null,
                    Status = req.responseCode,
                    Text = req.downloadHandler != null ? req.downloadHandler.text : null,
                    RetryAfter = req.GetResponseHeader("Retry-After"),
                });
            };
            return tcs.Task;
        }
    }

    internal static class JsonUtilitySafe
    {
        public static T From<T>(string json) where T : class
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return UnityEngine.JsonUtility.FromJson<T>(json); }
            catch { return null; }
        }
    }
}
