using System;

namespace M2C.Checkout
{
    /// <summary>
    /// Typed error codes mirroring the foundations error taxonomy. A
    /// <see cref="M2CCheckoutException"/> carries one of these so callers can
    /// branch without string matching. <c>PendingTimeout</c> is intentionally
    /// absent: a poll window elapsing is a terminal <see cref="CheckoutResult"/>
    /// (<see cref="CheckoutPendingTimeout"/>), not an error.
    /// </summary>
    public enum M2CErrorCode
    {
        /// <summary>Network or transport failure before a response was read.</summary>
        Network,
        /// <summary>Invalid body, unsupported currency, or malformed URL (HTTP 400).</summary>
        InvalidRequest,
        /// <summary>Origin or success_url not allowed for this key (HTTP 403).</summary>
        OriginNotAllowed,
        /// <summary>The account is suspended (HTTP 403).</summary>
        AccountSuspended,
        /// <summary>No vendors linked, or no bids (HTTP 404).</summary>
        NoVendorsAvailable,
        /// <summary>Rate limited (HTTP 429). See <see cref="M2CCheckoutException.RetryAfter"/>.</summary>
        RateLimited,
        /// <summary>Service temporarily unavailable, e.g. the result could not be recorded (HTTP 503). Retryable.</summary>
        ServiceUnavailable,
        /// <summary>The checkout TTL expired before launch; re-create the checkout.</summary>
        CheckoutExpired,
        /// <summary>An unexpected, non-recoverable failure.</summary>
        Unknown
    }

    /// <summary>
    /// Thrown for non-recoverable checkout failures. Outcomes that are part of
    /// the normal flow (completed / failed / canceled / pending-timeout) are
    /// returned as a <see cref="CheckoutResult"/>, not thrown.
    /// </summary>
    [Serializable]
    public sealed class M2CCheckoutException : Exception
    {
        /// <summary>The typed error code.</summary>
        public M2CErrorCode Code { get; }

        /// <summary>
        /// Seconds the caller should wait before retrying, when the server
        /// supplied a <c>Retry-After</c> header (set for <see cref="M2CErrorCode.RateLimited"/>).
        /// Zero when not provided.
        /// </summary>
        public int RetryAfter { get; }

        /// <summary>The HTTP status code, or 0 for transport-level failures.</summary>
        public int HttpStatus { get; }

        public M2CCheckoutException(M2CErrorCode code, string message, int httpStatus = 0, int retryAfter = 0)
            : base(message)
        {
            Code = code;
            HttpStatus = httpStatus;
            RetryAfter = retryAfter;
        }
    }
}
