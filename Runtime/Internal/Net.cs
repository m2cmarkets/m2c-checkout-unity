using System;
using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
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

    [Serializable]
    internal sealed class ShopSessionWinnerDto
    {
        public string shop_url;
        public int ttl;
        public string launch_expires_at;
    }

    [Serializable]
    internal sealed class ShopSessionResponseDto
    {
        public ShopSessionWinnerDto winner;
        public string session_id;
        public string expires_at;
    }

    [Serializable]
    internal sealed class ShopSessionStatusDto
    {
        public string session_id;
        public string status;
        public int completed_purchases;
    }

    /// <summary>The fields of an auction the client needs (no pricing).</summary>
    internal struct AuctionResult
    {
        public string CheckoutUrl;
        public string RequestId;
        public int Ttl;
    }

    internal struct ShopSessionResult
    {
        public string SessionId;
        public DateTimeOffset SessionExpiresAt;
        public DateTimeOffset ShopUrlExpiresAt;
        public string ShopUrl;
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

        public async Task<AuctionResult> CreateAuctionAsync(
            AuctionRequest req,
            int timeoutSeconds = DefaultHttpTimeoutSeconds,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            string body = BuildAuctionBody(req);
            HttpResponse res = await Http.PostJsonAsync(
                _baseUrl + "/api/v1/auction",
                body,
                _publishableKey,
                timeoutSeconds,
                cancellationToken);
            if (!res.TransportOk)
                throw new M2CCheckoutException(M2CErrorCode.Network, res.TransportError ?? "network error");
            if (res.Status < 200 || res.Status >= 300)
                throw MapError(res);

            var dto = JsonUtilitySafe.From<AuctionResponseDto>(res.Text);
            if (dto?.winner == null
                || string.IsNullOrEmpty(dto.winner.checkout_url)
                || string.IsNullOrEmpty(dto.request_id)
                || dto.winner.ttl <= 0)
                throw new M2CCheckoutException(M2CErrorCode.Unknown, "malformed auction response");
            return new AuctionResult { CheckoutUrl = dto.winner.checkout_url, RequestId = dto.request_id, Ttl = dto.winner.ttl };
        }

        public async Task<ShopSessionResult> CreateShopSessionAsync(ShopSessionRequest request)
        {
            HttpResponse res = await Http.PostJsonAsync(
                _baseUrl + "/api/v1/session",
                BuildShopSessionBody(request),
                _publishableKey,
                DefaultHttpTimeoutSeconds);
            if (!res.TransportOk)
                throw new M2CCheckoutException(M2CErrorCode.Network, res.TransportError ?? "network error");
            if (res.Status < 200 || res.Status >= 300)
                throw MapShopSessionCreationError(res);
            return ParseShopSessionResponse(res.Text);
        }

        internal static ShopSessionResult ParseShopSessionResponse(string text)
        {
            ShopSessionResponseDto dto = JsonUtilitySafe.From<ShopSessionResponseDto>(text);
            DateTimeOffset sessionExpiresAt;
            DateTimeOffset shopUrlExpiresAt;
            string sessionId;
            int ttl;
            if (dto?.winner == null
                || string.IsNullOrEmpty(dto.winner.shop_url)
                || !JsonFieldParser.TryReadNestedInt32(text, "winner", "ttl", out ttl)
                || ttl < 60 || ttl > 3600
                || !TryCanonicalSessionId(dto.session_id, out sessionId)
                || !TryParseRfc3339(dto.expires_at, out sessionExpiresAt)
                || !TryParseRfc3339(dto.winner.launch_expires_at, out shopUrlExpiresAt)
                || !UrlValidator.IsValidHttpsOrLoopbackHttp(dto.winner.shop_url))
            {
                throw new M2CCheckoutException(M2CErrorCode.Unknown, "malformed shop session response");
            }
            return new ShopSessionResult
            {
                SessionId = sessionId,
                SessionExpiresAt = sessionExpiresAt,
                ShopUrlExpiresAt = shopUrlExpiresAt,
                ShopUrl = dto.winner.shop_url,
                Ttl = ttl
            };
        }

        public async Task<ShopSessionStatus> ReadShopSessionStatusAsync(string sessionId)
        {
            string canonical = CanonicalSessionId(sessionId);
            string url = _baseUrl + "/api/v1/session-status?session_id=" + UnityWebRequest.EscapeURL(canonical);
            HttpResponse res = await Http.GetAsync(url, _publishableKey, DefaultHttpTimeoutSeconds);
            if (!res.TransportOk)
                throw new M2CCheckoutException(M2CErrorCode.Network, res.TransportError ?? "network error");
            if (res.Status < 200 || res.Status >= 300) throw MapShopSessionStatusError(res);
            return ParseShopSessionStatus(res.Text, canonical);
        }

        /// <summary>Reads status from M2C's endpoint. A 404 (row not visible yet) maps to Processing so the caller keeps polling.</summary>
        public async Task<ClientStatus> ReadStatusM2CAsync(string requestId, double timeoutBudgetSeconds = 0)
        {
            string url = _baseUrl + "/api/v1/conversions/" + UnityWebRequest.EscapeURL(requestId);
            HttpResponse res = await Http.GetAsync(url, _publishableKey, RequestTimeoutSeconds(timeoutBudgetSeconds));
            if (!res.TransportOk)
                throw new M2CCheckoutException(M2CErrorCode.Network, res.TransportError ?? "network error");
            if (res.Status == 404) return ClientStatus.Processing;
            if (res.Status < 200 || res.Status >= 300) throw MapError(res);
            return ParseM2CStatusResponse(res.Text, requestId);
        }

        /// <summary>Reads status from a merchant URL template (no API key).</summary>
        public static async Task<ClientStatus> ReadStatusUrlAsync(string template, string requestId, double timeoutBudgetSeconds = 0)
        {
            string url = template.Replace("{request_id}", UnityWebRequest.EscapeURL(requestId));
            HttpResponse res = await Http.GetAsync(url, null, RequestTimeoutSeconds(timeoutBudgetSeconds));
            if (!res.TransportOk)
                throw new M2CCheckoutException(M2CErrorCode.Network, res.TransportError ?? "network error");
            if (res.Status < 200 || res.Status >= 300)
                throw new M2CCheckoutException(
                    M2CErrorCode.ServiceUnavailable,
                    "status read failed: HTTP " + res.Status,
                    (int)res.Status,
                    RetryAfterParser.Parse(res.RetryAfter, DateTimeOffset.UtcNow) ?? 0);
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

        internal static ClientStatus ParseM2CStatusResponse(string text, string expectedRequestId)
        {
            var dto = JsonUtilitySafe.From<StatusResponseDto>(text);
            if (dto == null
                || string.IsNullOrEmpty(dto.status)
                || !string.Equals(dto.request_id, expectedRequestId, StringComparison.Ordinal))
            {
                throw new M2CCheckoutException(M2CErrorCode.Unknown, "status response had an unexpected shape");
            }
            return ParseClientStatus(dto.status);
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

        internal static string BuildShopSessionBody(ShopSessionRequest request)
        {
            return new JsonWriter()
                .String("device_type", DetectDeviceType())
                .String("platform", DetectSessionPlatform())
                .String("currency", request.Currency)
                .String("language", request.Language)
                .StringArray("segments", request.Segments)
                .String("return_url", request.ReturnUrl)
                .Build();
        }

        internal static string DetectSessionPlatform()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return "webgl";
#elif UNITY_IOS && !UNITY_EDITOR
            return "ios";
#elif UNITY_ANDROID && !UNITY_EDITOR
            return "android";
#else
            return "desktop";
#endif
        }

        internal static ShopSessionStatus ParseShopSessionStatus(string text, string requestedSessionId)
        {
            string canonical = CanonicalSessionId(requestedSessionId);
            ShopSessionStatusDto dto = JsonUtilitySafe.From<ShopSessionStatusDto>(text);
            int completedPurchases;
            if (dto == null
                || !string.Equals(dto.session_id, canonical, StringComparison.Ordinal)
                || !JsonFieldParser.TryReadTopLevelInt32(text, "completed_purchases", out completedPurchases)
                || completedPurchases < 0)
            {
                throw new M2CCheckoutException(M2CErrorCode.Unknown, "session status response had an unexpected shape");
            }
            ShopSessionState state;
            if (string.Equals(dto.status, "active", StringComparison.Ordinal)) state = ShopSessionState.Active;
            else if (string.Equals(dto.status, "expired", StringComparison.Ordinal)) state = ShopSessionState.Expired;
            else throw new M2CCheckoutException(M2CErrorCode.Unknown, "session status response had an unexpected shape");
            return new ShopSessionStatus(canonical, state, completedPurchases);
        }

        internal static string CanonicalSessionId(string value)
        {
            string canonical;
            if (!TryCanonicalSessionId(value, out canonical))
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "sessionId must be a canonical UUID");
            return canonical;
        }

        private static bool TryCanonicalSessionId(string value, out string canonical)
        {
            canonical = null;
            Guid parsed;
            if (string.IsNullOrEmpty(value) || !Guid.TryParseExact(value, "D", out parsed)) return false;
            canonical = parsed.ToString("D").ToLowerInvariant();
            return true;
        }

        private static bool TryParseRfc3339(string value, out DateTimeOffset parsed)
        {
            parsed = default(DateTimeOffset);
            if (string.IsNullOrEmpty(value))
                return false;
            Match match = Regex.Match(
                value,
                "^(\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2})(?:\\.(\\d{1,9}))?(Z|[+-]\\d{2}:\\d{2})$");
            if (!match.Success) return false;
            string milliseconds = (match.Groups[2].Value + "000").Substring(0, 3);
            string normalized = match.Groups[1].Value + "." + milliseconds + match.Groups[3].Value;
            return DateTimeOffset.TryParse(
                normalized,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed);
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
            int retryAfter = RetryAfterParser.Parse(res.RetryAfter, DateTimeOffset.UtcNow) ?? 0;
            string msg = ParseErrorMessage(res.Text) ?? ("HTTP " + res.Status);
            switch (res.Status)
            {
                case 400: return new M2CCheckoutException(M2CErrorCode.InvalidRequest, msg, 400);
                case 401: return new M2CCheckoutException(M2CErrorCode.AuthenticationFailed, msg, 401);
                case 403:
                    var code = (msg != null && msg.ToLowerInvariant().Contains("suspend"))
                        ? M2CErrorCode.AccountSuspended
                        : M2CErrorCode.OriginNotAllowed;
                    return new M2CCheckoutException(code, msg, 403);
                case 404: return new M2CCheckoutException(M2CErrorCode.NoVendorsAvailable, msg, 404);
                case 429: return new M2CCheckoutException(M2CErrorCode.RateLimited, msg, 429, retryAfter);
                default:
                    if (res.Status >= 500 && res.Status <= 599)
                        return new M2CCheckoutException(M2CErrorCode.ServiceUnavailable, msg, (int)res.Status, retryAfter);
                    return new M2CCheckoutException(M2CErrorCode.Unknown, msg, (int)res.Status);
            }
        }

        internal static M2CCheckoutException MapShopSessionCreationError(HttpResponse response)
        {
            if (response.Status != 409) return MapError(response);
            M2CCheckoutException mapped = MapError(new HttpResponse
            {
                TransportOk = true,
                Status = 400,
                Text = response.Text,
                RetryAfter = response.RetryAfter
            });
            return new M2CCheckoutException(M2CErrorCode.InvalidRequest, mapped.Message, 409);
        }

        internal static M2CCheckoutException MapShopSessionStatusError(HttpResponse response)
        {
            return response.Status == 404
                ? new M2CCheckoutException(M2CErrorCode.SessionNotFound, "shop session was not found", 404)
                : MapError(response);
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
        public static async Task<HttpResponse> PostJsonAsync(
            string url,
            string body,
            string apiKey,
            int timeoutSeconds = 0,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var req = CreatePostRequest(url, body, apiKey, timeoutSeconds))
            {
                return await SendAsync(req, cancellationToken);
            }
        }

        public static async Task<HttpResponse> GetAsync(string url, string apiKey, int timeoutSeconds = 0)
        {
            using (var req = CreateGetRequest(url, apiKey, timeoutSeconds))
            {
                return await SendAsync(req);
            }
        }

        internal static UnityWebRequest CreatePostRequest(string url, string body, string apiKey, int timeoutSeconds)
        {
            var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            Configure(req, apiKey, timeoutSeconds);
            req.SetRequestHeader("Content-Type", "application/json");
            return req;
        }

        internal static UnityWebRequest CreateGetRequest(string url, string apiKey, int timeoutSeconds)
        {
            var req = new UnityWebRequest(url, "GET");
            Configure(req, apiKey, timeoutSeconds);
            return req;
        }

        private static void Configure(UnityWebRequest req, string apiKey, int timeoutSeconds)
        {
            req.downloadHandler = new BoundedDownloadHandler();
            req.redirectLimit = 0;
            if (!string.IsNullOrEmpty(apiKey)) req.SetRequestHeader("X-API-Key", apiKey);
            if (timeoutSeconds > 0) req.timeout = timeoutSeconds;
        }

        private static Task<HttpResponse> SendAsync(
            UnityWebRequest req,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var tcs = new TaskCompletionSource<HttpResponse>();
            CancellationTokenRegistration cancellationRegistration = default(CancellationTokenRegistration);
            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                cancellationRegistration.Dispose();
                // Result is Unity 2020.2+; the package baseline is 2021.3.
                var bounded = req.downloadHandler as BoundedDownloadHandler;
                bool tooLarge = bounded != null && bounded.TooLarge;
                bool transportError = req.result == UnityWebRequest.Result.ConnectionError
                                      || req.result == UnityWebRequest.Result.DataProcessingError;
                tcs.TrySetResult(new HttpResponse
                {
                    TransportOk = !transportError && !tooLarge,
                    TransportError = tooLarge ? "response body exceeded 65536 bytes" : transportError ? req.error : null,
                    Status = req.responseCode,
                    Text = !tooLarge && req.downloadHandler != null ? req.downloadHandler.text : null,
                    RetryAfter = req.GetResponseHeader("Retry-After"),
                });
            };
            if (cancellationToken.CanBeCanceled)
            {
                cancellationRegistration = cancellationToken.Register(() =>
                {
                    try { req.Abort(); }
                    catch (ObjectDisposedException) { }
                });
            }
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

    internal static class JsonFieldParser
    {
        public static bool TryReadTopLevelInt32(string json, string fieldName, out int value)
        {
            value = 0;
            int start;
            int end;
            if (!TryFindTopLevelField(json, fieldName, out start, out end)) return false;
            string raw = json.Substring(start, end - start).Trim();
            return int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        public static bool TryReadNestedInt32(
            string json,
            string objectFieldName,
            string integerFieldName,
            out int value)
        {
            value = 0;
            int start;
            int end;
            if (!TryFindTopLevelField(json, objectFieldName, out start, out end)) return false;
            string nested = json.Substring(start, end - start).Trim();
            return nested.Length >= 2
                && nested[0] == '{'
                && TryReadTopLevelInt32(nested, integerFieldName, out value);
        }

        private static bool TryFindTopLevelField(
            string json,
            string fieldName,
            out int valueStart,
            out int valueEnd)
        {
            valueStart = 0;
            valueEnd = 0;
            if (string.IsNullOrEmpty(json)) return false;
            int cursor = 0;
            SkipWhitespace(json, ref cursor);
            if (cursor >= json.Length || json[cursor++] != '{') return false;
            bool found = false;
            while (true)
            {
                SkipWhitespace(json, ref cursor);
                if (cursor >= json.Length) return false;
                if (json[cursor] == '}')
                {
                    cursor++;
                    SkipWhitespace(json, ref cursor);
                    return found && cursor == json.Length;
                }
                int keyStart;
                int keyEnd;
                bool keyHasEscape;
                if (!TryReadString(json, ref cursor, out keyStart, out keyEnd, out keyHasEscape)) return false;
                SkipWhitespace(json, ref cursor);
                if (cursor >= json.Length || json[cursor++] != ':') return false;
                SkipWhitespace(json, ref cursor);
                int candidateStart = cursor;
                if (!TrySkipValue(json, ref cursor)) return false;
                int candidateEnd = cursor;
                bool keyMatches = !keyHasEscape
                    && keyEnd - keyStart == fieldName.Length
                    && string.CompareOrdinal(json, keyStart, fieldName, 0, fieldName.Length) == 0;
                if (keyMatches)
                {
                    if (found) return false;
                    found = true;
                    valueStart = candidateStart;
                    valueEnd = candidateEnd;
                }
                SkipWhitespace(json, ref cursor);
                if (cursor >= json.Length) return false;
                if (json[cursor] == '}')
                {
                    cursor++;
                    SkipWhitespace(json, ref cursor);
                    return found && cursor == json.Length;
                }
                if (json[cursor++] != ',') return false;
            }
        }

        private static bool TryReadString(
            string json,
            ref int cursor,
            out int contentStart,
            out int contentEnd,
            out bool hasEscape)
        {
            contentStart = 0;
            contentEnd = 0;
            hasEscape = false;
            if (cursor >= json.Length || json[cursor++] != '"') return false;
            contentStart = cursor;
            while (cursor < json.Length)
            {
                char current = json[cursor++];
                if (current == '"')
                {
                    contentEnd = cursor - 1;
                    return true;
                }
                if (current != '\\') continue;
                hasEscape = true;
                if (cursor >= json.Length) return false;
                cursor++;
            }
            return false;
        }

        private static bool TrySkipValue(string json, ref int cursor)
        {
            int objectDepth = 0;
            int arrayDepth = 0;
            bool inString = false;
            bool escaped = false;
            int start = cursor;
            while (cursor < json.Length)
            {
                char current = json[cursor];
                if (inString)
                {
                    cursor++;
                    if (escaped) escaped = false;
                    else if (current == '\\') escaped = true;
                    else if (current == '"') inString = false;
                    continue;
                }
                if (current == '"') inString = true;
                else if (current == '{') objectDepth++;
                else if (current == '[') arrayDepth++;
                else if (current == '}')
                {
                    if (objectDepth == 0 && arrayDepth == 0) break;
                    if (objectDepth == 0) return false;
                    objectDepth--;
                }
                else if (current == ']')
                {
                    if (arrayDepth == 0) return false;
                    arrayDepth--;
                }
                else if (current == ',' && objectDepth == 0 && arrayDepth == 0) break;
                cursor++;
            }
            return cursor > start && !inString && objectDepth == 0 && arrayDepth == 0;
        }

        private static void SkipWhitespace(string json, ref int cursor)
        {
            while (cursor < json.Length && char.IsWhiteSpace(json[cursor])) cursor++;
        }
    }
}
