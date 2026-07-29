using System;

namespace M2C.Checkout
{
    /// <summary>
    /// The canonical checkout state machine, shared with the other client SDKs.
    /// Surfaced through <see cref="M2CCheckoutClient.OnStateChanged"/> so the
    /// merchant can render progress. The five terminal result outcomes are returned
    /// as a <see cref="CheckoutResult"/>; <see cref="Error"/> corresponds to a
    /// thrown <see cref="M2CCheckoutException"/>.
    /// </summary>
    public enum CheckoutState
    {
        Idle,
        Creating,        // client-initiated only: calling the auction
        Ready,           // have checkout_url + request_id
        Launching,       // opening the vendor checkout
        AwaitingReturn,  // customer is on the vendor page
        Returned,        // back on success_url (or canceled via cancel_url)
        Polling,         // resolving status
        Completed,
        Failed,
        Canceled,
        PendingTimeout,
        FallbackStarted,
        Error
    }

    /// <summary>
    /// Coarse, client-facing conversion status, mapped from the server's internal
    /// status. The only values the status endpoints ever return.
    /// </summary>
    public enum ClientStatus
    {
        Processing,
        Completed,
        Failed,
        Canceled
    }

    /// <summary>Terminal outcome discriminator for ergonomic switching.</summary>
    public enum CheckoutOutcome
    {
        Completed,
        Failed,
        Canceled,
        PendingTimeout,
        FallbackStarted
    }

    /// <summary>Why a definitely-not-launched checkout entered the merchant's fallback flow.</summary>
    public enum FallbackReason
    {
        NoBids,
        Timeout,
        ApiError,
        LaunchFailed
    }

    /// <summary>The merchant fallback handler's immediate responsibility decision.</summary>
    public enum FallbackDecision
    {
        Unavailable,
        Accepted
    }

    /// <summary>Per-call override for the fallback policy configured on the client.</summary>
    public enum FallbackMode
    {
        Inherit,
        Disabled
    }

    /// <summary>Metadata attached to the original checkout exception after fallback cannot take responsibility.</summary>
    public enum FallbackStatus
    {
        Declined,
        HandlerOutcomeUnknown
    }

    /// <summary>Optional data and policy for one checkout start.</summary>
    public sealed class CheckoutStartOptions
    {
        public FallbackMode FallbackMode = FallbackMode.Inherit;
        public string FallbackProductId;
    }

    /// <summary>Context passed to a merchant fallback handler. It never represents payment success.</summary>
    public sealed class FallbackContext
    {
        public string AttemptId { get; internal set; }
        public FallbackReason Reason { get; internal set; }
        public M2CCheckoutException OriginalError { get; internal set; }
        public string RequestId { get; internal set; }
        public string FallbackProductId { get; internal set; }
        public long LatencyMs { get; internal set; }
        public double TransactionValue { get; internal set; }
        public string Currency { get; internal set; }
        public string Description { get; internal set; }
        public string Reference { get; internal set; }
    }

    /// <summary>
    /// Async merchant hook that accepts responsibility for presenting the game's
    /// existing native billing flow. Accepted does not mean the purchase completed.
    /// </summary>
    public delegate System.Threading.Tasks.Task<FallbackDecision> CheckoutFallbackHandler(
        FallbackReason reason,
        FallbackContext context);

    /// <summary>
    /// Terminal result of a checkout. Branch on the concrete subtype, or switch
    /// on <see cref="Outcome"/>. Never carries vendor or pricing detail.
    /// </summary>
    public abstract class CheckoutResult
    {
        /// <summary>
        /// Correlates this checkout with the merchant webhook's <c>reference</c>/<c>request_id</c>.
        /// Null only for <see cref="CheckoutFallbackStarted"/> when no auction response supplied one.
        /// </summary>
        public string RequestId { get; }

        /// <summary>Outcome discriminator.</summary>
        public abstract CheckoutOutcome Outcome { get; }

        protected CheckoutResult(string requestId)
        {
            RequestId = requestId;
        }
    }

    /// <summary>The customer returned and the conversion is recorded complete.</summary>
    public sealed class CheckoutCompleted : CheckoutResult
    {
        public override CheckoutOutcome Outcome => CheckoutOutcome.Completed;
        public CheckoutCompleted(string requestId) : base(requestId) { }
    }

    /// <summary>The vendor reported a failed payment.</summary>
    public sealed class CheckoutFailed : CheckoutResult
    {
        public override CheckoutOutcome Outcome => CheckoutOutcome.Failed;
        public CheckoutFailed(string requestId) : base(requestId) { }
    }

    /// <summary>The customer canceled (returned via cancel_url, or dismissed the in-app browser).</summary>
    public sealed class CheckoutCanceled : CheckoutResult
    {
        public override CheckoutOutcome Outcome => CheckoutOutcome.Canceled;
        public CheckoutCanceled(string requestId) : base(requestId) { }
    }

    /// <summary>
    /// The poll window elapsed while status was still processing. Not an error:
    /// the authoritative answer arrives via the merchant webhook. Show a
    /// "we'll confirm shortly" state.
    /// </summary>
    public sealed class CheckoutPendingTimeout : CheckoutResult
    {
        public override CheckoutOutcome Outcome => CheckoutOutcome.PendingTimeout;
        public CheckoutPendingTimeout(string requestId) : base(requestId) { }
    }

    /// <summary>
    /// The merchant's native billing flow accepted responsibility before any vendor
    /// checkout was exposed. Fulfillment remains entirely in the merchant's IAP path.
    /// </summary>
    public sealed class CheckoutFallbackStarted : CheckoutResult
    {
        public override CheckoutOutcome Outcome => CheckoutOutcome.FallbackStarted;
        public string AttemptId { get; }
        public FallbackReason Reason { get; }

        public CheckoutFallbackStarted(string attemptId, string requestId, FallbackReason reason)
            : base(requestId)
        {
            AttemptId = attemptId;
            Reason = reason;
        }
    }

    /// <summary>
    /// A backend-created checkout session forwarded to the client. The backend ran
    /// the auction with its secret key and sends only these fields (never pricing).
    /// </summary>
    [Serializable]
    public struct CheckoutSession
    {
        /// <summary>The winning vendor's hosted checkout URL.</summary>
        public string CheckoutUrl;
        /// <summary>Correlation id for status reads and the webhook.</summary>
        public string RequestId;
        /// <summary>
        /// Seconds the checkout URL stays valid before launch, or null when the
        /// backend did not provide a TTL. Map backend JSON explicitly because
        /// Unity's JsonUtility does not preserve nullable fields.
        /// </summary>
        public int? Ttl;
    }

    /// <summary>
    /// Parameters for a client-initiated auction (publishable-key mode). Money is
    /// in major currency units (e.g. 4.99). The SDK does not send customer_ip or
    /// country: the server derives geo from the observed connection IP.
    /// </summary>
    [Serializable]
    public struct AuctionRequest
    {
        public double TransactionValue;
        public string Currency;       // ISO 4217; defaults to USD server-side when null/empty
        public string Description;
        public string SuccessUrl;     // mobile custom scheme/app link, or http(s) return page on WebGL
        public string CancelUrl;
        public string Reference;      // your order id; echoed back in the webhook
        public string[] Segments;
        public string Language;
    }
}
