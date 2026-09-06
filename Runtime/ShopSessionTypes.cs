using System;

namespace M2C.Checkout
{
    /// <summary>Parameters for one vendor-owned shop browsing session.</summary>
    public struct ShopSessionRequest
    {
        public string Currency;
        public string Language;
        public string[] Segments;
        public string ReturnUrl;
    }

    /// <summary>The coarse purchase-reporting window state of a shop session.</summary>
    public enum ShopSessionState
    {
        Active,
        Expired
    }

    /// <summary>One advisory snapshot of a shop session.</summary>
    public sealed class ShopSessionStatus
    {
        public string SessionId { get; }
        public ShopSessionState Status { get; }
        public int CompletedPurchases { get; }

        internal ShopSessionStatus(string sessionId, ShopSessionState status, int completedPurchases)
        {
            SessionId = sessionId;
            Status = status;
            CompletedPurchases = completedPurchases;
        }
    }

    /// <summary>Immutable launch result for a shop session.</summary>
    public sealed class ShopSessionHandle
    {
        public string SessionId { get; }
        public DateTimeOffset SessionExpiresAt { get; }
        public DateTimeOffset ShopUrlExpiresAt { get; }
        public string ShopUrl { get; }

        internal ShopSessionHandle(
            string sessionId,
            DateTimeOffset sessionExpiresAt,
            DateTimeOffset shopUrlExpiresAt,
            string shopUrl)
        {
            SessionId = sessionId;
            SessionExpiresAt = sessionExpiresAt;
            ShopUrlExpiresAt = shopUrlExpiresAt;
            ShopUrl = shopUrl;
        }
    }
}
