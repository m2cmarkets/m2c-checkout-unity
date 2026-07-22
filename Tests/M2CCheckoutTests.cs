using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

    public class StatusFallbackTests
    {
        private const string RequestId = "11111111-1111-1111-1111-111111111111";

        [Test]
        public void Fallback_is_off_by_default()
        {
            var settings = ScriptableObject.CreateInstance<M2CCheckoutSettings>();
            try
            {
                Assert.IsFalse(settings.ToConfig().UseM2CStatusFallback);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void Enabled_fallback_maps_and_clamps_delay_to_5_60()
        {
            AssertFallbackSeconds(10f, 10.0);      // within range, unchanged
            AssertFallbackSeconds(2f, 5.0);        // below min -> clamped up
            AssertFallbackSeconds(120f, 60.0);     // above max -> clamped down
            AssertFallbackSeconds(float.NaN, 5.0); // non-finite -> min
        }

        [Test]
        public void Enabled_fallback_without_publishable_key_rejects_with_validation_error()
        {
            var config = new M2CConfig
            {
                StatusSource = StatusSource.Url("https://shop.example/status/{request_id}"),
                UseM2CStatusFallback = true,
            };
            var client = new M2CCheckoutClient(config);

            // CheckStatusAsync validates the status source synchronously before any
            // network read, so the fallback-without-key error surfaces here.
            var e = Assert.Throws<M2CCheckoutException>(() => { _ = client.CheckStatusAsync(RequestId); });
            Assert.AreEqual(M2CErrorCode.InvalidRequest, e.Code);
        }

        private static void AssertFallbackSeconds(float input, double expected)
        {
            var settings = ScriptableObject.CreateInstance<M2CCheckoutSettings>();
            try
            {
                settings.UseM2CStatusFallback = true;
                settings.M2CFallbackAfterSeconds = input;

                M2CConfig config = settings.ToConfig();

                Assert.IsTrue(config.UseM2CStatusFallback);
                Assert.AreEqual(expected, config.M2CFallbackAfterSeconds, 1e-6);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }
    }
    public class NativeFallbackTests
    {
        private static readonly AuctionRequest Request = new AuctionRequest
        {
            TransactionValue = 4.99,
            Currency = "USD",
            Description = "coins",
            Reference = "order-123",
            SuccessUrl = "mygame://checkout/return",
            CancelUrl = "mygame://checkout/cancel"
        };

        [Test]
        public async Task Accepted_fallback_is_terminal_without_error_state()
        {
            int calls = 0;
            FallbackContext seen = null;
            var config = Config(async (reason, context) =>
            {
                calls++;
                seen = context;
                await Task.Yield();
                return FallbackDecision.Accepted;
            });
            var original = new M2CCheckoutException(M2CErrorCode.NoVendorsAvailable, "no bids", 404);
            var client = Client(config, (_, __, ___) => Task.FromException<AuctionResult>(original));
            var states = new List<CheckoutState>();
            client.OnStateChanged += states.Add;

            CheckoutResult result = await client.StartAsync(Request, new CheckoutStartOptions
            {
                FallbackProductId = "coins_5"
            });

            var fallback = result as CheckoutFallbackStarted;
            Assert.NotNull(fallback);
            Assert.AreEqual(CheckoutOutcome.FallbackStarted, fallback.Outcome);
            Assert.AreEqual(FallbackReason.NoBids, fallback.Reason);
            Assert.IsFalse(string.IsNullOrEmpty(fallback.AttemptId));
            Assert.IsNull(fallback.RequestId);
            Assert.AreEqual(1, calls);
            Assert.AreSame(original, seen.OriginalError);
            Assert.AreEqual("coins_5", seen.FallbackProductId);
            Assert.AreEqual(Request.TransactionValue, seen.TransactionValue);
            Assert.AreEqual(Request.Reference, seen.Reference);
            CollectionAssert.DoesNotContain(states, CheckoutState.Error);
            Assert.AreEqual(CheckoutState.FallbackStarted, client.State);
        }

        [Test]
        public void Declined_fallback_rethrows_the_original_exception()
        {
            var original = new M2CCheckoutException(M2CErrorCode.ServiceUnavailable, "down", 503);
            var config = Config((_, __) => Task.FromResult(FallbackDecision.Unavailable));
            var client = Client(config, (_, __, ___) => Task.FromException<AuctionResult>(original));

            var thrown = Assert.ThrowsAsync<M2CCheckoutException>(async () => await client.StartAsync(Request));

            Assert.AreSame(original, thrown);
            Assert.AreEqual(FallbackStatus.Declined, thrown.FallbackStatus);
            Assert.AreEqual(CheckoutState.Error, client.State);
        }

        [Test]
        public void Handler_failure_keeps_original_authoritative_and_marks_outcome_unknown()
        {
            var original = new M2CCheckoutException(M2CErrorCode.Network, "offline");
            var handlerError = new InvalidOperationException("IAP launch failed");
            var config = Config((_, __) => Task.FromException<FallbackDecision>(handlerError));
            var client = Client(config, (_, __, ___) => Task.FromException<AuctionResult>(original));

            var thrown = Assert.ThrowsAsync<M2CCheckoutException>(async () => await client.StartAsync(Request));

            Assert.AreSame(original, thrown);
            Assert.AreEqual(FallbackStatus.HandlerOutcomeUnknown, thrown.FallbackStatus);
            Assert.AreSame(handlerError, thrown.FallbackError);
        }

        [Test]
        public void Per_call_disabled_preserves_the_original_error_path()
        {
            int handlerCalls = 0;
            var original = new M2CCheckoutException(M2CErrorCode.NoVendorsAvailable, "no bids", 404);
            var config = Config((_, __) =>
            {
                handlerCalls++;
                return Task.FromResult(FallbackDecision.Accepted);
            });
            int timeoutSeconds = 0;
            var client = Client(config, (_, timeout, ___) =>
            {
                timeoutSeconds = timeout;
                return Task.FromException<AuctionResult>(original);
            });

            var thrown = Assert.ThrowsAsync<M2CCheckoutException>(async () =>
                await client.StartAsync(Request, new CheckoutStartOptions { FallbackMode = FallbackMode.Disabled }));

            Assert.AreSame(original, thrown);
            Assert.IsNull(thrown.FallbackStatus);
            Assert.AreEqual(0, handlerCalls);
            Assert.AreEqual(M2CApi.DefaultHttpTimeoutSeconds, timeoutSeconds);
        }

        [TestCase(7999)]
        [TestCase(30001)]
        public void Fallback_auction_timeout_outside_bounds_fails_before_the_request(int timeoutMs)
        {
            int auctionCalls = 0;
            var config = Config((_, __) => Task.FromResult(FallbackDecision.Accepted));
            config.FallbackAuctionTimeoutMs = timeoutMs;
            var client = Client(config, (_, __, ___) =>
            {
                auctionCalls++;
                return Task.FromResult(new AuctionResult());
            });

            var thrown = Assert.ThrowsAsync<M2CCheckoutException>(async () => await client.StartAsync(Request));

            Assert.AreEqual(M2CErrorCode.InvalidRequest, thrown.Code);
            Assert.AreEqual(0, auctionCalls);
        }

        [Test]
        public void Invalid_backend_session_url_is_a_contract_error_without_fallback()
        {
            int handlerCalls = 0;
            var config = Config((_, __) =>
            {
                handlerCalls++;
                return Task.FromResult(FallbackDecision.Accepted);
            });
            var client = Client(config, (_, __, ___) => Task.FromResult(new AuctionResult()));

            var thrown = Assert.ThrowsAsync<M2CCheckoutException>(async () =>
                await client.StartFromSessionAsync(new CheckoutSession
                {
                    CheckoutUrl = "javascript:alert(1)",
                    RequestId = "req_123",
                    Ttl = 60
                }));

            Assert.AreEqual(M2CErrorCode.InvalidRequest, thrown.Code);
            Assert.AreEqual(0, handlerCalls);
        }

        [Test]
        public async Task Deadline_aborts_and_observes_the_request_before_fallback()
        {
            bool requestCanceled = false;
            var requestTask = new TaskCompletionSource<AuctionResult>();
            var config = Config((_, __) => Task.FromResult(FallbackDecision.Accepted));
            var client = new M2CCheckoutClient(
                config,
                (_, __, token) =>
                {
                    token.Register(() =>
                    {
                        requestCanceled = true;
                        requestTask.TrySetCanceled();
                    });
                    return requestTask.Task;
                },
                (_, __) => Task.FromResult(true));

            CheckoutResult result = await client.StartAsync(Request);

            Assert.IsTrue(requestCanceled);
            Assert.AreEqual(CheckoutOutcome.FallbackStarted, result.Outcome);
            Assert.AreEqual(FallbackReason.Timeout, ((CheckoutFallbackStarted)result).Reason);
        }

        [Test]
        public async Task Invalid_client_auction_checkout_url_is_an_api_fallback()
        {
            FallbackReason seen = default(FallbackReason);
            var config = Config((reason, _) =>
            {
                seen = reason;
                return Task.FromResult(FallbackDecision.Accepted);
            });
            var client = Client(config, (_, __, ___) => Task.FromResult(new AuctionResult
            {
                CheckoutUrl = "javascript:alert(1)",
                RequestId = "req_bad_url"
            }));

            CheckoutResult result = await client.StartAsync(Request);

            Assert.AreEqual(CheckoutOutcome.FallbackStarted, result.Outcome);
            Assert.AreEqual(FallbackReason.ApiError, seen);
        }

        [Test]
        public void No_handler_keeps_the_existing_error_path_without_requiring_a_timeout()
        {
            var original = new M2CCheckoutException(M2CErrorCode.NoVendorsAvailable, "no bids", 404);
            var config = new M2CConfig
            {
                PublishableKey = "pub_test_example",
                ReturnUrl = Request.SuccessUrl,
                CancelUrl = Request.CancelUrl
            };
            int timeoutSeconds = 0;
            var client = Client(config, (_, timeout, ___) =>
            {
                timeoutSeconds = timeout;
                return Task.FromException<AuctionResult>(original);
            });

            var thrown = Assert.ThrowsAsync<M2CCheckoutException>(async () => await client.StartAsync(Request));

            Assert.AreSame(original, thrown);
            Assert.IsNull(thrown.FallbackStatus);
            Assert.AreEqual(M2CApi.DefaultHttpTimeoutSeconds, timeoutSeconds);
        }

        [TestCase("sec_example")]
        [TestCase("sec_test_example")]
        public void Client_configuration_rejects_secret_keys_before_auction_request(string secretKey)
        {
            int requests = 0;
            var config = new M2CConfig
            {
                PublishableKey = secretKey,
                ReturnUrl = Request.SuccessUrl,
                CancelUrl = Request.CancelUrl
            };

            var thrown = Assert.Throws<M2CCheckoutException>(() => Client(
                config,
                (_, __, ___) =>
                {
                    requests++;
                    return Task.FromResult(ValidAuction());
                }));

            Assert.AreEqual(M2CErrorCode.InvalidRequest, thrown.Code);
            StringAssert.Contains("never embed a secret key", thrown.Message);
            Assert.AreEqual(0, requests);
        }

        [Test]
        public void Trigger_classification_excludes_auth_and_configuration_errors()
        {
            FallbackReason reason;
            Assert.IsTrue(M2CCheckoutClient.TryClassifyAuctionFailure(
                new M2CCheckoutException(M2CErrorCode.NoVendorsAvailable, "none", 404), out reason));
            Assert.AreEqual(FallbackReason.NoBids, reason);
            Assert.IsTrue(M2CCheckoutClient.TryClassifyAuctionFailure(
                new M2CCheckoutException(M2CErrorCode.RateLimited, "slow", 429), out reason));
            Assert.AreEqual(FallbackReason.ApiError, reason);
            Assert.IsFalse(M2CCheckoutClient.TryClassifyAuctionFailure(
                new M2CCheckoutException(M2CErrorCode.Unknown, "unauthorized", 401), out reason));
            Assert.IsFalse(M2CCheckoutClient.TryClassifyAuctionFailure(
                new M2CCheckoutException(M2CErrorCode.InvalidRequest, "bad input", 400), out reason));
        }

        [Test]
        public void Launch_latch_is_per_attempt_and_one_way()
        {
            CheckoutFallbackHandler handler = (_, __) => Task.FromResult(FallbackDecision.Accepted);
            var first = new M2CCheckoutClient.FallbackAttempt(handler, null, Request, null);
            Assert.IsTrue(first.CanFallback);
            first.MarkLaunchedOrUnknown();
            first.MarkLaunchedOrUnknown();
            Assert.IsFalse(first.CanFallback);

            var second = new M2CCheckoutClient.FallbackAttempt(handler, null, Request, null);
            Assert.IsTrue(second.CanFallback);
        }

        [TestCase("https://vendor.example/checkout", true)]
        [TestCase("http://localhost:8090/checkout", true)]
        [TestCase("http://127.0.0.1:8090/checkout", true)]
        [TestCase("http://[::1]:8090/checkout", true)]
        [TestCase("http://vendor.example/checkout", false)]
        [TestCase("http://10.0.0.1/checkout", false)]
        [TestCase("javascript:alert(1)", false)]
        [TestCase("/relative", false)]
        [TestCase("https:///missing-host", false)]
        public void Checkout_url_validation_requires_https_except_for_loopback_http(string url, bool expected)
        {
            Assert.AreEqual(expected, M2CCheckoutClient.IsValidCheckoutUrl(url));
        }

        [Test]
        public async Task Session_fallback_does_not_require_an_auction_timeout()
        {
            int handlerCalls = 0;
            var config = Config((_, __) =>
            {
                handlerCalls++;
                return Task.FromResult(FallbackDecision.Accepted);
            });
            config.FallbackAuctionTimeoutMs = 0;
            var browser = new FallbackTestBrowser
            {
                PreparationError = new CheckoutPreparationException(
                    new M2CCheckoutException(M2CErrorCode.InvalidRequest, "checkout window was blocked"))
            };
            var client = Client(
                config,
                (_, __, ___) => Task.FromResult(new AuctionResult()),
                createBrowser: _ => browser);

            CheckoutResult result = await client.StartFromSessionAsync(ValidSession());

            Assert.AreEqual(CheckoutOutcome.FallbackStarted, result.Outcome);
            Assert.AreEqual(FallbackReason.LaunchFailed, ((CheckoutFallbackStarted)result).Reason);
            Assert.AreEqual(1, handlerCalls);
        }

        [Test]
        public void Failure_after_launch_boundary_never_invokes_fallback()
        {
            int handlerCalls = 0;
            var original = new M2CCheckoutException(M2CErrorCode.Unknown, "launch outcome failed");
            var launch = new TaskCompletionSource<BrowserOutcome>();
            var config = Config((_, __) =>
            {
                handlerCalls++;
                return Task.FromResult(FallbackDecision.Accepted);
            });
            var browser = new FallbackTestBrowser { LaunchTask = launch.Task };
            var client = Client(
                config,
                (_, __, ___) => Task.FromResult(new AuctionResult()),
                createBrowser: _ => browser);

            Task<CheckoutResult> checkout = client.StartFromSessionAsync(ValidSession());
            Assert.IsFalse(checkout.IsCompleted);
            launch.TrySetException(original);
            var thrown = Assert.ThrowsAsync<M2CCheckoutException>(async () => await checkout);

            Assert.AreSame(original, thrown);
            Assert.AreEqual(0, handlerCalls);
            Assert.AreEqual(CheckoutState.Error, client.State);
        }

        [Test]
        public async Task Prelaunch_preparation_failure_defaults_to_launch_failed()
        {
            int handlerCalls = 0;
            var config = Config((_, __) =>
            {
                handlerCalls++;
                return Task.FromResult(FallbackDecision.Accepted);
            });
            var browser = new FallbackTestBrowser
            {
                PreparationError = new CheckoutPreparationException(
                    new M2CCheckoutException(M2CErrorCode.InvalidRequest, "checkout window was blocked"))
            };
            var client = Client(
                config,
                (_, __, ___) => Task.FromResult(new AuctionResult()),
                createBrowser: _ => browser);

            CheckoutResult result = await client.StartAsync(Request);

            Assert.AreEqual(CheckoutOutcome.FallbackStarted, result.Outcome);
            Assert.AreEqual(FallbackReason.LaunchFailed, ((CheckoutFallbackStarted)result).Reason);
            Assert.AreEqual(1, handlerCalls);
        }

        [Test]
        public async Task Prepared_window_closed_before_navigation_can_fallback()
        {
            int handlerCalls = 0;
            var config = Config((_, __) =>
            {
                handlerCalls++;
                return Task.FromResult(FallbackDecision.Accepted);
            });
            var browser = new FallbackTestBrowser
            {
                LaunchTask = Task.FromResult(BrowserOutcome.PreparedLaunchFailed)
            };
            var client = Client(
                config,
                (_, __, ___) => Task.FromResult(new AuctionResult
                {
                    CheckoutUrl = "https://vendor.example/checkout",
                    RequestId = "req_launch_fence"
                }),
                createBrowser: _ => browser);

            ResumeStore.Clear();
            var states = new List<CheckoutState>();
            client.OnStateChanged += states.Add;

            CheckoutResult result = await client.StartAsync(Request);

            Assert.AreEqual(CheckoutOutcome.FallbackStarted, result.Outcome);
            Assert.AreEqual(FallbackReason.LaunchFailed, ((CheckoutFallbackStarted)result).Reason);
            Assert.AreEqual(1, handlerCalls);
            Assert.IsNull(ResumeStore.PendingRecord());
            CollectionAssert.DoesNotContain(states, CheckoutState.Polling);
        }

        private static CheckoutSession ValidSession()
        {
            return new CheckoutSession
            {
                CheckoutUrl = "https://vendor.example/checkout",
                RequestId = "req_launch_fence",
                Ttl = 60
            };
        }

        private sealed class FallbackTestBrowser : ICheckoutBrowser, ICheckoutBrowserPrelauncher
        {
            public CheckoutPreparationException PreparationError;
            public Task<BrowserOutcome> LaunchTask = Task.FromResult(BrowserOutcome.Launched);
            public int CancelPreparedCalls;

            public bool RequiresReturnUrl => false;

            public void PrepareLaunch()
            {
                if (PreparationError != null) throw PreparationError;
            }

            public void CancelPreparedLaunch()
            {
                CancelPreparedCalls++;
            }

            public Task<BrowserOutcome> LaunchAsync(string checkoutUrl, string returnUrl, string cancelUrl)
            {
                return LaunchTask;
            }
        }

        [Test]
        public void Coroutine_wrapper_returns_fallback_started()
        {
            CheckoutResult seen = null;
            var original = new M2CCheckoutException(M2CErrorCode.NoVendorsAvailable, "no bids", 404);
            var config = Config((_, __) => Task.FromResult(FallbackDecision.Accepted));
            var client = Client(config, (_, __, ___) => Task.FromException<AuctionResult>(original));

            var routine = client.Start(Request, onResult: result => seen = result);
            while (routine.MoveNext()) { }

            Assert.NotNull(seen);
            Assert.AreEqual(CheckoutOutcome.FallbackStarted, seen.Outcome);
        }

        [Test]
        public async Task Auction_winner_cancels_deadline_without_invoking_fallback()
        {
            int handlerCalls = 0;
            bool deadlineCanceled = false;
            var auction = new TaskCompletionSource<AuctionResult>();
            var deadline = new TaskCompletionSource<bool>();
            var config = Config((_, __) =>
            {
                handlerCalls++;
                return Task.FromResult(FallbackDecision.Accepted);
            });
            var browser = new FallbackTestBrowser { LaunchTask = Task.FromResult(BrowserOutcome.Dismissed) };
            var client = Client(
                config,
                (_, __, ___) => auction.Task,
                (_, token) =>
                {
                    token.Register(() =>
                    {
                        deadlineCanceled = true;
                        deadline.TrySetCanceled();
                    });
                    return deadline.Task;
                },
                _ => browser);

            Task<CheckoutResult> checkout = client.StartAsync(Request);
            Assert.IsFalse(checkout.IsCompleted);
            auction.TrySetResult(ValidAuction());
            CheckoutResult result = await checkout;

            Assert.AreEqual(CheckoutOutcome.Canceled, result.Outcome);
            Assert.IsTrue(deadlineCanceled);
            Assert.AreEqual(0, handlerCalls);
        }

        [Test]
        public async Task Simultaneous_auction_and_deadline_completion_prefers_the_auction()
        {
            int handlerCalls = 0;
            var auction = new TaskCompletionSource<AuctionResult>();
            var config = Config((_, __) =>
            {
                handlerCalls++;
                return Task.FromResult(FallbackDecision.Accepted);
            });
            var browser = new FallbackTestBrowser { LaunchTask = Task.FromResult(BrowserOutcome.Dismissed) };
            var client = Client(
                config,
                (_, __, ___) => auction.Task,
                (_, __) =>
                {
                    auction.TrySetResult(ValidAuction());
                    return Task.FromResult(true);
                },
                _ => browser);

            CheckoutResult result = await client.StartAsync(Request);

            Assert.AreEqual(CheckoutOutcome.Canceled, result.Outcome);
            Assert.AreEqual(0, handlerCalls);
        }

        [Test]
        public async Task Sequential_attempts_on_the_same_client_use_independent_launch_latches()
        {
            int handlerCalls = 0;
            int browserCalls = 0;
            var firstLaunch = new TaskCompletionSource<BrowserOutcome>();
            var firstBrowser = new FallbackTestBrowser { LaunchTask = firstLaunch.Task };
            var secondBrowser = new FallbackTestBrowser
            {
                PreparationError = new CheckoutPreparationException(
                    new M2CCheckoutException(M2CErrorCode.InvalidRequest, "checkout window was blocked"))
            };
            var config = Config((_, __) =>
            {
                handlerCalls++;
                return Task.FromResult(FallbackDecision.Accepted);
            });
            var client = Client(
                config,
                (_, __, ___) => Task.FromResult(ValidAuction()),
                createBrowser: _ => browserCalls++ == 0 ? firstBrowser : secondBrowser);

            Task<CheckoutResult> first = client.StartFromSessionAsync(ValidSession());
            firstLaunch.TrySetException(new M2CCheckoutException(M2CErrorCode.Unknown, "post-launch failure"));
            Assert.ThrowsAsync<M2CCheckoutException>(async () => await first);
            CheckoutResult second = await client.StartFromSessionAsync(ValidSession());

            Assert.AreEqual(CheckoutOutcome.FallbackStarted, second.Outcome);
            Assert.AreEqual(1, handlerCalls);
            Assert.AreEqual(2, browserCalls);
        }

        private static AuctionResult ValidAuction()
        {
            return new AuctionResult
            {
                CheckoutUrl = "https://vendor.example/checkout",
                RequestId = "req_fallback_test"
            };
        }

        private static M2CConfig Config(CheckoutFallbackHandler handler)
        {
            return new M2CConfig
            {
                PublishableKey = "pub_test_example",
                ReturnUrl = Request.SuccessUrl,
                CancelUrl = Request.CancelUrl,
                FallbackHandler = handler,
                FallbackAuctionTimeoutMs = 10000
            };
        }

        private static M2CCheckoutClient Client(
            M2CConfig config,
            Func<AuctionRequest, int, CancellationToken, Task<AuctionResult>> createAuction,
            Func<double, CancellationToken, Task> delay = null,
            Func<string, ICheckoutBrowser> createBrowser = null)
        {
            return new M2CCheckoutClient(config, createAuction, delay, createBrowser);
        }
    }
}
