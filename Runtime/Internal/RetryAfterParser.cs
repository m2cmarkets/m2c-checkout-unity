using System;
using System.Globalization;

namespace M2C.Checkout.Internal
{
    internal static class RetryAfterParser
    {
        public static int? Parse(string value, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string trimmed = value.Trim();
            bool digitsOnly = true;
            foreach (char c in trimmed)
            {
                if (c < '0' || c > '9')
                {
                    digitsOnly = false;
                    break;
                }
            }

            if (digitsOnly)
            {
                int seconds;
                return int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out seconds)
                    ? (int?)seconds
                    : null;
            }

            // Numeric-looking junk must not fall through to date parsing.
            if (trimmed.IndexOfAny(new[] { '-', '.', '+' }) >= 0
                && trimmed.IndexOf(',') < 0)
                return null;

            DateTimeOffset date;
            if (!DateTimeOffset.TryParseExact(
                    trimmed,
                    "r",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out date))
                return null;

            double delta = (date - now).TotalSeconds;
            if (delta <= 0) return 0;
            if (delta > int.MaxValue) return null;
            return (int)Math.Ceiling(delta);
        }
    }
}
