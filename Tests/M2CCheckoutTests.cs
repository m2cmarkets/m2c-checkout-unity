using System;
using M2C.Checkout;
using M2C.Checkout.Internal;
using NUnit.Framework;
using UnityEngine;

namespace M2C.Checkout.Tests
{
    public class ReturnClassifierTests
    {
        [Test]
        public void Success_when_url_matches_return()
        {
            var v = ReturnClassifier.Classify(
                "mygame://checkout/return?request_id=abc",
                "mygame://checkout/return", "mygame://checkout/cancel", "fallback", out string id);
            Assert.AreEqual(ReturnVerdict.Success, v);
            Assert.AreEqual("abc", id);
        }

        [Test]
        public void Cancel_when_url_matches_cancel()
        {
            var v = ReturnClassifier.Classify(
                "mygame://checkout/cancel",
                "mygame://checkout/return", "mygame://checkout/cancel", "fallback", out string id);
            Assert.AreEqual(ReturnVerdict.Cancel, v);
            Assert.AreEqual("fallback", id); // no request_id param -> fallback id
        }

        [Test]
        public void Unknown_when_url_does_not_match_return_or_cancel()
        {
            var v = ReturnClassifier.Classify(
                "mygame://something-else",
                "mygame://checkout/return", "mygame://checkout/cancel", "fb", out _);
            Assert.AreEqual(ReturnVerdict.Unknown, v);
        }

        [Test]
        public void Does_not_prefix_match_partial_path_segment()
        {
            var v = ReturnClassifier.Classify(
                "mygame://checkout/cancelled",
                "mygame://checkout/return", "mygame://checkout/cancel", "fb", out _);
            Assert.AreEqual(ReturnVerdict.Unknown, v);
        }

        [Test]
        public void Allows_child_path_under_configured_return()
        {
            var v = ReturnClassifier.Classify(
                "mygame://checkout/return/vendor?request_id=abc",
                "mygame://checkout/return", "mygame://checkout/cancel", "fb", out string id);
            Assert.AreEqual(ReturnVerdict.Success, v);
            Assert.AreEqual("abc", id);
        }

        [Test]
        public void Extracts_request_id_among_params()
        {
            Assert.AreEqual("xyz", ReturnClassifier.ExtractRequestId("app://r?a=1&request_id=xyz&b=2"));
            Assert.IsNull(ReturnClassifier.ExtractRequestId("app://r"));
            Assert.IsNull(ReturnClassifier.ExtractRequestId(null));
        }

        [Test]
        public void Detects_mismatched_return_request_id()
        {
            Assert.IsTrue(ReturnClassifier.HasMismatchedRequestId("mygame://checkout/return?request_id=other", "active"));
            Assert.IsFalse(ReturnClassifier.HasMismatchedRequestId("mygame://checkout/return?request_id=ACTIVE", "active"));
            Assert.IsFalse(ReturnClassifier.HasMismatchedRequestId("mygame://checkout/return", "active"));
        }
    }

    public class StatusParseTests
    {
        [TestCase("completed", ClientStatus.Completed)]
        [TestCase("refunded", ClientStatus.Completed)]
        [TestCase("chargedback", ClientStatus.Completed)]
        [TestCase("failed", ClientStatus.Failed)]
        [TestCase("canceled", ClientStatus.Canceled)]
        [TestCase("abandoned", ClientStatus.Canceled)]
        [TestCase("pending", ClientStatus.Processing)]
        [TestCase("processing", ClientStatus.Processing)]
        [TestCase("unrecognized", ClientStatus.Processing)]
        [TestCase(null, ClientStatus.Processing)]
        public void Maps_client_status(string raw, ClientStatus expected)
        {
            Assert.AreEqual(expected, M2CApi.ParseClientStatus(raw));
        }

        [TestCase(ClientStatus.Completed, CheckoutOutcome.Completed)]
        [TestCase(ClientStatus.Failed, CheckoutOutcome.Failed)]
        [TestCase(ClientStatus.Canceled, CheckoutOutcome.Canceled)]
        [TestCase(ClientStatus.Processing, CheckoutOutcome.PendingTimeout)]
        public void Resume_status_read_resolution_only_cancels_on_backend_canceled(ClientStatus status, CheckoutOutcome expected)
        {
            CheckoutResult result = M2CCheckoutClient.ResultFromStatusRead("req_123", status);

            Assert.AreEqual(expected, result.Outcome);
            Assert.AreEqual("req_123", result.RequestId);
        }

        [TestCase(ClientStatus.Completed, CheckoutOutcome.Completed)]
        [TestCase(ClientStatus.Failed, CheckoutOutcome.Failed)]
        [TestCase(ClientStatus.Canceled, CheckoutOutcome.Canceled)]
        [TestCase(ClientStatus.Processing, CheckoutOutcome.Canceled)]
        public void Browser_cancel_status_read_resolution_cancels_when_backend_still_processing(ClientStatus status, CheckoutOutcome expected)
        {
            CheckoutResult result = M2CCheckoutClient.ResultFromBrowserCancelStatusRead("req_123", status);

            Assert.AreEqual(expected, result.Outcome);
            Assert.AreEqual("req_123", result.RequestId);
        }
    }

    public class PollScheduleTests
    {
        [Test]
        public void Ramps_then_repeats_last()
        {
            var p = PollSchedule.Default;
            Assert.AreEqual(0.0, p.DelayForAttempt(0));
            Assert.AreEqual(1.0, p.DelayForAttempt(1));
            Assert.AreEqual(2.0, p.DelayForAttempt(2));
            Assert.AreEqual(8.0, p.DelayForAttempt(4));
            Assert.AreEqual(8.0, p.DelayForAttempt(50)); // repeats the last ramp value
        }

        [Test]
        public void Rejects_invalid_schedules()
        {
            Assert.Throws<ArgumentException>(() => new PollSchedule(null, 90));
            Assert.Throws<ArgumentException>(() => new PollSchedule(new double[0], 90));
            Assert.Throws<ArgumentException>(() => new PollSchedule(new[] { 1.0 }, 0));
            Assert.Throws<ArgumentException>(() => new PollSchedule(new[] { 1.0 }, double.NaN));
            Assert.Throws<ArgumentException>(() => new PollSchedule(new[] { -1.0, 1.0 }, 90));
            Assert.Throws<ArgumentException>(() => new PollSchedule(new[] { double.PositiveInfinity }, 90));
            Assert.Throws<ArgumentException>(() => new PollSchedule(new[] { 0.0 }, 90));
            Assert.Throws<ArgumentException>(() => new PollSchedule(new[] { 0.0, 1.0, 0.0 }, 90));
        }

        [Test]
        public void Copies_ramp_values_defensively()
        {
            double[] ramp = { 0.0, 1.0 };
            var p = new PollSchedule(ramp, 90);

            ramp[1] = 5.0;
            Assert.AreEqual(1.0, p.DelayForAttempt(1));

            double[] exported = p.RampSeconds;
            exported[1] = 6.0;
            Assert.AreEqual(1.0, p.DelayForAttempt(1));
        }
    }

    public class AuctionBodyTests
    {
        [Test]
        public void Includes_required_and_omits_empty()
        {
            string body = M2CApi.BuildAuctionBody(new AuctionRequest
            {
                TransactionValue = 4.99,
                Currency = "USD",
                SuccessUrl = "mygame://checkout/return"
            });
            StringAssert.Contains("\"transaction_value\":4.99", body);
            StringAssert.Contains("\"currency\":\"USD\"", body);
            StringAssert.Contains("\"success_url\":\"mygame://checkout/return\"", body);
            StringAssert.DoesNotContain("description", body);
            StringAssert.DoesNotContain("cancel_url", body);
        }

        [Test]
        public void Formats_number_invariantly_without_scientific_notation()
        {
            string body = M2CApi.BuildAuctionBody(new AuctionRequest { TransactionValue = 1000.5 });
            StringAssert.Contains("\"transaction_value\":1000.5", body);
        }

        [Test]
        public void Accepts_minimum_transaction_value()
        {
            string body = M2CApi.BuildAuctionBody(new AuctionRequest { TransactionValue = 0.000001 });
            StringAssert.Contains("\"transaction_value\":0.000001", body);
        }

        [Test]
        public void Rejects_invalid_transaction_values()
        {
            AssertInvalidTransactionValue(double.NaN);
            AssertInvalidTransactionValue(double.PositiveInfinity);
            AssertInvalidTransactionValue(double.NegativeInfinity);
            AssertInvalidTransactionValue(0);
            AssertInvalidTransactionValue(-1);
            AssertInvalidTransactionValue(0.0000004);
            AssertInvalidTransactionValue(5000000000.01);
        }

        [Test]
        public void Escapes_string_values()
        {
            string body = M2CApi.BuildAuctionBody(new AuctionRequest { TransactionValue = 1, Description = "a\"b" });
            StringAssert.Contains("a\\\"b", body);
        }

        [Test]
        public void Writes_segments_array()
        {
            string body = M2CApi.BuildAuctionBody(new AuctionRequest { TransactionValue = 1, Segments = new[] { "premium", "returning" } });
            StringAssert.Contains("\"segments\":[\"premium\",\"returning\"]", body);
        }

        private static void AssertInvalidTransactionValue(double value)
        {
            var e = Assert.Throws<M2CCheckoutException>(() => M2CApi.BuildAuctionBody(new AuctionRequest { TransactionValue = value }));
            Assert.AreEqual(M2CErrorCode.InvalidRequest, e.Code);
        }
    }

    public class ErrorMappingTests
    {
        private static HttpResponse Res(long status, string text = null, string retryAfter = null)
        {
            return new HttpResponse { TransportOk = true, Status = status, Text = text, RetryAfter = retryAfter };
        }

        [Test]
        public void Maps_status_codes_to_error_codes()
        {
            Assert.AreEqual(M2CErrorCode.InvalidRequest, M2CApi.MapError(Res(400)).Code);
            Assert.AreEqual(M2CErrorCode.OriginNotAllowed, M2CApi.MapError(Res(403, "{\"error\":\"origin not allowed\"}")).Code);
            Assert.AreEqual(M2CErrorCode.AccountSuspended, M2CApi.MapError(Res(403, "{\"error\":\"account is suspended\"}")).Code);
            Assert.AreEqual(M2CErrorCode.NoVendorsAvailable, M2CApi.MapError(Res(404)).Code);
            Assert.AreEqual(M2CErrorCode.ServiceUnavailable, M2CApi.MapError(Res(503)).Code);
        }

        [Test]
        public void Rate_limited_carries_retry_after()
        {
            var e = M2CApi.MapError(Res(429, null, "12"));
            Assert.AreEqual(M2CErrorCode.RateLimited, e.Code);
            Assert.AreEqual(12, e.RetryAfter);
        }
    }

    public class HttpTimeoutTests
    {
        [Test]
        public void Converts_poll_budget_to_request_timeout_seconds()
        {
            Assert.AreEqual(1, M2CApi.RequestTimeoutSeconds(0.1));
            Assert.AreEqual(2, M2CApi.RequestTimeoutSeconds(1.2));
            Assert.AreEqual(M2CApi.DefaultHttpTimeoutSeconds, M2CApi.RequestTimeoutSeconds(0));
            Assert.AreEqual(M2CApi.DefaultHttpTimeoutSeconds, M2CApi.RequestTimeoutSeconds(double.NaN));
        }
    }

    public class ProjectSettingsTests
    {
        [Test]
        public void Builds_config_from_explicit_settings()
        {
            var settings = ScriptableObject.CreateInstance<M2CCheckoutSettings>();
            try
            {
                settings.PublishableKey = " pub_test_abc ";
                settings.ReturnUrl = " mygame://done ";
                settings.CancelUrl = " mygame://cancel ";
                settings.StatusUrlTemplate = " https://shop.example/status/{request_id} ";
                settings.DeepLinkScheme = "ignored";
                settings.BrowserMode = M2CBrowserMode.ExternalBrowser;
                settings.StatusPollTimeoutSeconds = 45f;

                M2CConfig config = settings.ToConfig();

                Assert.AreEqual("pub_test_abc", config.PublishableKey);
                Assert.AreEqual("mygame://done", config.ReturnUrl);
                Assert.AreEqual("mygame://cancel", config.CancelUrl);
                Assert.AreEqual(StatusSourceKind.Url, config.StatusSource.Kind);
                Assert.AreEqual("https://shop.example/status/{request_id}", config.StatusSource.UrlTemplate);
                Assert.IsTrue(config.UseExternalBrowser);
                Assert.AreEqual(45.0, config.Poll.TotalWindowSeconds);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void Builds_webgl_config_from_webgl_settings()
        {
            var settings = ScriptableObject.CreateInstance<M2CCheckoutSettings>();
            try
            {
                settings.PublishableKey = " pub_test_mobile ";
                settings.WebGLPublishableKey = " pub_test_web ";
                settings.ReturnUrl = " mygame://checkout/return ";
                settings.CancelUrl = " mygame://checkout/cancel ";
                settings.WebGLReturnUrl = " https://game.example/m2c-return ";
                settings.WebGLCancelUrl = " https://game.example/m2c-cancel ";
                settings.WebGLLaunchMode = M2CWebGLLaunchMode.Popup;

                M2CConfig config = settings.ToConfig(M2CCheckoutPlatform.WebGL);

                Assert.AreEqual("pub_test_web", config.PublishableKey);
                Assert.AreEqual("https://game.example/m2c-return", config.ReturnUrl);
                Assert.AreEqual("https://game.example/m2c-cancel", config.CancelUrl);
                Assert.AreEqual(M2CWebGLLaunchMode.Popup, config.WebGLLaunchMode);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void Webgl_config_does_not_fall_back_to_mobile_deep_links()
        {
            var settings = ScriptableObject.CreateInstance<M2CCheckoutSettings>();
            try
            {
                settings.DeepLinkScheme = "mygame";

                M2CConfig config = settings.ToConfig(M2CCheckoutPlatform.WebGL);

                Assert.IsNull(config.ReturnUrl);
                Assert.IsNull(config.CancelUrl);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void Webgl_config_preserves_legacy_http_return_urls()
        {
            var settings = ScriptableObject.CreateInstance<M2CCheckoutSettings>();
            try
            {
                settings.ReturnUrl = " https://game.example/return ";
                settings.CancelUrl = " https://game.example/cancel ";

                M2CConfig config = settings.ToConfig(M2CCheckoutPlatform.WebGL);

                Assert.AreEqual("https://game.example/return", config.ReturnUrl);
                Assert.AreEqual("https://game.example/cancel", config.CancelUrl);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void Mobile_platform_key_overrides_fall_back_to_mobile_key()
        {
            var settings = ScriptableObject.CreateInstance<M2CCheckoutSettings>();
            try
            {
                settings.PublishableKey = " pub_test_mobile ";
                settings.IosPublishableKey = " pub_test_ios ";

                Assert.AreEqual("pub_test_ios", settings.ToConfig(M2CCheckoutPlatform.Ios).PublishableKey);
                Assert.AreEqual("pub_test_mobile", settings.ToConfig(M2CCheckoutPlatform.Android).PublishableKey);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void Webgl_key_does_not_fall_back_to_mobile_key()
        {
            var settings = ScriptableObject.CreateInstance<M2CCheckoutSettings>();
            try
            {
                settings.PublishableKey = " pub_test_mobile ";

                M2CConfig config = settings.ToConfig(M2CCheckoutPlatform.WebGL);

                Assert.IsNull(config.PublishableKey);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void Derives_return_urls_from_deep_link_scheme()
        {
            var settings = ScriptableObject.CreateInstance<M2CCheckoutSettings>();
            try
            {
                settings.DeepLinkScheme = "mygame";

                M2CConfig config = settings.ToConfig();

                Assert.AreEqual("mygame://checkout/return", config.ReturnUrl);
                Assert.AreEqual("mygame://checkout/cancel", config.CancelUrl);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void Mobile_custom_schemes_follow_effective_return_urls()
        {
            var settings = ScriptableObject.CreateInstance<M2CCheckoutSettings>();
            try
            {
                settings.DeepLinkScheme = "ignored";
                settings.ReturnUrl = " paygame://done ";
                settings.CancelUrl = " mygame://cancel ";

                CollectionAssert.AreEqual(
                    new[] { "paygame", "mygame" },
                    settings.EffectiveMobileCustomSchemes);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void Mobile_custom_schemes_ignore_web_urls()
        {
            var settings = ScriptableObject.CreateInstance<M2CCheckoutSettings>();
            try
            {
                settings.DeepLinkScheme = "mygame";
                settings.ReturnUrl = " https://links.example/return ";
                settings.CancelUrl = " MYGAME://checkout/cancel ";

                CollectionAssert.AreEqual(
                    new[] { "mygame" },
                    settings.EffectiveMobileCustomSchemes);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void Trims_mobile_settings_and_ignores_blank_return_overrides()
        {
            var settings = ScriptableObject.CreateInstance<M2CCheckoutSettings>();
            try
            {
                settings.ReturnUrl = " ";
                settings.CancelUrl = "\t";
                settings.DeepLinkScheme = " mygame ";
                settings.AssociatedDomain = " links.mygame.com ";

                M2CConfig config = settings.ToConfig();

                Assert.AreEqual("mygame://checkout/return", config.ReturnUrl);
                Assert.AreEqual("mygame://checkout/cancel", config.CancelUrl);
                Assert.AreEqual("mygame", settings.EffectiveDeepLinkScheme);
                Assert.AreEqual("links.mygame.com", settings.EffectiveAssociatedDomain);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void Rejects_status_url_without_request_id_token()
        {
            var settings = ScriptableObject.CreateInstance<M2CCheckoutSettings>();
            try
            {
                settings.StatusUrlTemplate = "https://shop.example/status";

                Assert.Throws<ArgumentException>(() => settings.ToConfig());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }
    }
}
