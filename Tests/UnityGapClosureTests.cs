using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using M2C.Checkout.Internal;
using NUnit.Framework;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Networking;

namespace M2C.Checkout.Tests
{
    public class ProtocolConformanceTests
    {
#pragma warning disable 0649 // Populated reflectively by Unity JsonUtility.
        [Serializable]
        private sealed class KatRoot
        {
            public HttpHelpers http_helpers;
            public CheckoutHelpers checkout_helpers;
        }

        [Serializable]
        private sealed class HttpHelpers
        {
            public RetryAfterVector[] retry_after;
            public LoopbackVector[] loopback_hosts;
            public LoopbackUrlVector[] loopback_urls;
        }

        [Serializable]
        private sealed class CheckoutHelpers
        {
            public ReturnVector[] return_classification;
            public ErrorVector[] http_error_mapping;
        }

        [Serializable]
        private sealed class RetryAfterVector
        {
            public string name;
            public string header;
            public long now_ms;
            public int seconds;
        }

        [Serializable]
        private sealed class LoopbackVector
        {
            public string host;
            public bool loopback;
        }

        [Serializable]
        private sealed class LoopbackUrlVector
        {
            public string name;
            public string url;
            public bool allowed;
        }

        [Serializable]
        private sealed class ReturnVector
        {
            public string name;
            public string return_url;
            public string success_url;
            public string cancel_url;
            public string expected_request_id;
            public string verdict;
            public string request_id;
            public string error;
        }

        [Serializable]
        private sealed class ErrorVector
        {
            public string name;
            public int status;
            public string body;
            public string code;
        }
#pragma warning restore 0649

        private static KatRoot LoadVectors()
        {
            PackageInfo package = PackageInfo.FindForAssembly(typeof(M2CCheckoutClient).Assembly);
            Assert.NotNull(package, "M2C package location was not resolved");
            string path = Path.Combine(package.resolvedPath, "Tests", "Fixtures", "m2c-protocol-vectors.json");
            return JsonUtility.FromJson<KatRoot>(File.ReadAllText(path));
        }

        [Test]
        public void Return_classifier_matches_shared_vectors()
        {
            foreach (ReturnVector vector in LoadVectors().checkout_helpers.return_classification)
            {
                ReturnClassification actual = ReturnClassifier.Classify(
                    vector.return_url,
                    vector.success_url,
                    vector.cancel_url,
                    vector.expected_request_id);
                Assert.AreEqual(vector.verdict, actual.Verdict.ToString().ToLowerInvariant(), vector.name);
                Assert.AreEqual(vector.request_id, actual.RequestId, vector.name);
                Assert.AreEqual(vector.error, actual.Error, vector.name);
            }
        }

        [Test]
        public void Error_mapping_matches_shared_vectors_and_all_server_failures()
        {
            foreach (ErrorVector vector in LoadVectors().checkout_helpers.http_error_mapping)
            {
                var response = new HttpResponse
                {
                    TransportOk = true,
                    Status = vector.status,
                    Text = vector.body
                };
                Assert.AreEqual(
                    (M2CErrorCode)Enum.Parse(typeof(M2CErrorCode), vector.code),
                    M2CApi.MapError(response).Code,
                    vector.name);
            }

            foreach (int status in new[] { 500, 502, 504, 599 })
            {
                Assert.AreEqual(
                    M2CErrorCode.ServiceUnavailable,
                    M2CApi.MapError(new HttpResponse { Status = status }).Code);
            }
        }

        [Test]
        public void Retry_after_parser_matches_shared_vectors()
        {
            foreach (RetryAfterVector vector in LoadVectors().http_helpers.retry_after)
            {
                int? expected = vector.name == "zero" || vector.seconds != 0
                    ? (int?)vector.seconds
                    : null;
                var now = DateTimeOffset.FromUnixTimeMilliseconds(vector.now_ms);
                Assert.AreEqual(expected, RetryAfterParser.Parse(vector.header, now), vector.name);
            }
        }

        [Test]
        public void Loopback_detection_matches_shared_vectors()
        {
            foreach (LoopbackVector vector in LoadVectors().http_helpers.loopback_hosts)
            {
                Assert.AreEqual(vector.loopback, UrlValidator.IsLoopbackHost(vector.host), vector.host);
                Assert.AreEqual(
                    vector.loopback,
                    UrlValidator.IsValidHttpsOrLoopbackHttp("http://" + FormatHost(vector.host) + "/checkout"),
                    vector.host);
            }
        }

        [Test]
        public void Loopback_URL_authorities_match_shared_vectors()
        {
            foreach (LoopbackUrlVector vector in LoadVectors().http_helpers.loopback_urls)
            {
                Assert.AreEqual(
                    vector.allowed,
                    UrlValidator.IsValidHttpsOrLoopbackHttp(vector.url),
                    vector.name);
            }
        }

        [TestCase("http://user@127.0.0.1/checkout", true, "127.0.0.1")]
        [TestCase("http://user%40example@127.0.0.1/checkout", true, "127.0.0.1")]
        [TestCase("http://user@evil.example@127.0.0.1/checkout", false, null)]
        public void Raw_authority_parser_enforces_one_literal_userinfo_separator(
            string url,
            bool expected,
            string expectedHost)
        {
            string host;
            Assert.AreEqual(expected, UrlValidator.TryGetRawHost(url, out host));
            Assert.AreEqual(expectedHost, host);
        }

        private static string FormatHost(string host)
        {
            if (host.Contains(":") && !host.StartsWith("[", StringComparison.Ordinal)) return "[" + host + "]";
            return host;
        }
    }

    public class UnityGapResourceTests
    {
        [Test]
        public void Mobile_browser_bridges_include_persistent_mode_contracts()
        {
            PackageInfo package = PackageInfo.FindForAssembly(typeof(M2CCheckoutClient).Assembly);
            Assert.NotNull(package, "M2C package location was not resolved");

            string android = File.ReadAllText(Path.Combine(
                package.resolvedPath,
                "Plugins",
                "Android",
                "M2CAuthTabActivity.java"));
            StringAssert.Contains("m2c_persistent", android);
            StringAssert.Contains("setEphemeralBrowsingEnabled(true)", android);

            string ios = File.ReadAllText(Path.Combine(
                package.resolvedPath,
                "Plugins",
                "iOS",
                "M2CCheckout.m"));
            StringAssert.Contains("#import <SafariServices/SafariServices.h>", ios);
            StringAssert.Contains("m2c_presentSafariViewController", ios);
            StringAssert.Contains("m2c_dismissSafariViewController", ios);
            StringAssert.Contains("M2CSafariDismissCallback", ios);
            StringAssert.Contains("dismissViewControllerAnimated:YES completion:finish", ios);
            StringAssert.Contains("g_m2cSafariDismissing", ios);

            string iosBrowser = File.ReadAllText(Path.Combine(
                package.resolvedPath,
                "Runtime",
                "Browser",
                "IosPersistentBrowser.cs"));
            StringAssert.Contains("AwaitDismissalAsync", iosBrowser);
            StringAssert.Contains("await dismissalCompletion.Task", iosBrowser);
            StringAssert.Contains("if (_pending != null)", iosBrowser);

            string buildPostProcessor = File.ReadAllText(Path.Combine(
                package.resolvedPath,
                "Editor",
                "M2CBuildPostProcessor.cs"));
            StringAssert.Contains("SafariServices.framework", buildPostProcessor);
        }

        [Test]
        public void Public_error_code_values_preserve_the_0_6_order()
        {
            Assert.AreEqual(0, (int)M2CErrorCode.Network);
            Assert.AreEqual(1, (int)M2CErrorCode.InvalidRequest);
            Assert.AreEqual(2, (int)M2CErrorCode.OriginNotAllowed);
            Assert.AreEqual(3, (int)M2CErrorCode.AccountSuspended);
            Assert.AreEqual(4, (int)M2CErrorCode.NoVendorsAvailable);
            Assert.AreEqual(5, (int)M2CErrorCode.RateLimited);
            Assert.AreEqual(6, (int)M2CErrorCode.ServiceUnavailable);
            Assert.AreEqual(7, (int)M2CErrorCode.CheckoutExpired);
            Assert.AreEqual(8, (int)M2CErrorCode.Unknown);
            Assert.AreEqual(9, (int)M2CErrorCode.AuthenticationFailed);
        }

        [Test]
        public void Status_templates_require_secure_absolute_urls_and_token()
        {
            Assert.IsTrue(UrlValidator.IsValidStatusTemplate("https://shop.example/status/{request_id}"));
            Assert.IsTrue(UrlValidator.IsValidStatusTemplate("http://localhost/status/{request_id}"));
            Assert.IsTrue(UrlValidator.IsValidStatusTemplate("http://127.255.255.255/status/{request_id}"));
            Assert.IsFalse(UrlValidator.IsValidStatusTemplate("http://shop.example/status/{request_id}"));
            Assert.IsFalse(UrlValidator.IsValidStatusTemplate("ftp://shop.example/status/{request_id}"));
            Assert.IsFalse(UrlValidator.IsValidStatusTemplate("/status/{request_id}"));
            Assert.IsFalse(UrlValidator.IsValidStatusTemplate("https://shop.example/status"));
        }

        [Test]
        public void M2c_status_response_requires_exact_request_correlation()
        {
            Assert.AreEqual(
                ClientStatus.Completed,
                M2CApi.ParseM2CStatusResponse("{\"request_id\":\"req_1\",\"status\":\"completed\"}", "req_1"));
            Assert.Throws<M2CCheckoutException>(() =>
                M2CApi.ParseM2CStatusResponse("{\"status\":\"completed\"}", "req_1"));
            Assert.Throws<M2CCheckoutException>(() =>
                M2CApi.ParseM2CStatusResponse("{\"request_id\":\"REQ_1\",\"status\":\"completed\"}", "req_1"));
        }

        [Test]
        public void Download_handler_accepts_ceiling_and_rejects_overflow()
        {
            using (var exact = new BoundedDownloadHandler())
            {
                byte[] body = new byte[BoundedDownloadHandler.MaxBytes];
                body[body.Length - 1] = (byte)'x';
                Assert.IsTrue(exact.ReceiveForTest(body));
                Assert.IsFalse(exact.TooLarge);
                Assert.AreEqual(body.Length, Encoding.UTF8.GetByteCount(exact.TextForTest()));
                Assert.IsFalse(exact.ReceiveForTest(new byte[] { 1 }));
                Assert.IsTrue(exact.TooLarge);
            }

            using (var declared = new BoundedDownloadHandler())
            {
                declared.DeclareLengthForTest(BoundedDownloadHandler.MaxBytes + 1UL);
                Assert.IsTrue(declared.TooLarge);
                Assert.IsFalse(declared.ReceiveForTest(new byte[] { 1 }));
            }

            using (var misleading = new BoundedDownloadHandler())
            {
                misleading.DeclareLengthForTest(1);
                Assert.IsTrue(misleading.ReceiveForTest(new byte[BoundedDownloadHandler.MaxBytes]));
                Assert.IsFalse(misleading.ReceiveForTest(new byte[] { 1 }));
                Assert.IsTrue(misleading.TooLarge);
            }
        }

        [Test]
        public void Shared_request_factory_disables_redirects_and_caps_responses()
        {
            using (UnityWebRequest get = Http.CreateGetRequest("https://api.example/status", "pub_test_x", 3))
            using (UnityWebRequest post = Http.CreatePostRequest("https://api.example/auction", "{}", "pub_test_x", 3))
            {
                Assert.AreEqual(0, get.redirectLimit);
                Assert.AreEqual(0, post.redirectLimit);
                Assert.IsInstanceOf<BoundedDownloadHandler>(get.downloadHandler);
                Assert.IsInstanceOf<BoundedDownloadHandler>(post.downloadHandler);
                Assert.AreEqual("pub_test_x", get.GetRequestHeader("X-API-Key"));
            }
        }

        [Test]
        public async Task Callback_capacity_is_process_wide_and_released_only_when_callbacks_settle()
        {
            var callbacks = new List<TaskCompletionSource<ClientStatus>>();
            var running = new List<Task<ClientStatus>>();
            try
            {
                for (int i = 0; i < CallbackLimiter.Capacity; i++)
                {
                    var pending = new TaskCompletionSource<ClientStatus>();
                    callbacks.Add(pending);
                    running.Add(CallbackLimiter.InvokeAsync(() => pending.Task, 5));
                }

                int fifthCalls = 0;
                var error = Assert.ThrowsAsync<M2CCheckoutException>(async () =>
                    await CallbackLimiter.InvokeAsync(() =>
                    {
                        fifthCalls++;
                        return Task.FromResult(ClientStatus.Completed);
                    }, 0.01));
                Assert.AreEqual(M2CErrorCode.Network, error.Code);
                Assert.AreEqual(0, fifthCalls);
            }
            finally
            {
                foreach (TaskCompletionSource<ClientStatus> callback in callbacks)
                    callback.TrySetResult(ClientStatus.Processing);
                await Task.WhenAll(running);
            }

            Assert.AreEqual(
                ClientStatus.Completed,
                await CallbackLimiter.InvokeAsync(() => Task.FromResult(ClientStatus.Completed), 1));
        }
    }

    public class ResumeStoreV1Tests
    {
        [SetUp]
        public void SetUp()
        {
            ResumeStore.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            ResumeStore.Clear();
        }

        [Test]
        public void Single_value_records_round_trip_for_all_supported_sources()
        {
            foreach (StatusSource source in new[]
                     {
                         StatusSource.M2C,
                         StatusSource.Url("https://shop.example/status/{request_id}"),
                         StatusSource.Callback(_ => Task.FromResult(ClientStatus.Processing))
                     })
            {
                ResumeStore.Save("req_1", "session", source);
                ResumeRecord record = ResumeStore.PendingRecord();
                Assert.NotNull(record);
                Assert.AreEqual("req_1", record.RequestId);
                Assert.AreEqual(source.Kind, record.StatusKind);
                Assert.IsTrue(PlayerPrefs.HasKey(ResumeStore.RecordKey));
                ResumeStore.Clear();
            }
        }

        [Test]
        public void Invalid_oversized_and_unknown_records_are_removed()
        {
            foreach (string invalid in new[]
                     {
                         "not json",
                         "{\"version\":2,\"request_id\":\"req\",\"mode\":\"session\",\"status_kind\":\"m2c\"}",
                         "{\"version\":1,\"request_id\":\"\",\"mode\":\"session\",\"status_kind\":\"m2c\"}",
                         "{\"version\":1,\"request_id\":\"req\",\"mode\":\"weird\",\"status_kind\":\"m2c\"}",
                         "{\"version\":1,\"request_id\":\"req\",\"mode\":\"session\",\"status_kind\":\"other\"}",
                         "{\"version\":1,\"request_id\":\"req\",\"mode\":\"session\",\"status_kind\":\"url\",\"status_url_template\":\"http://shop.example/{request_id}\"}"
                     })
            {
                PlayerPrefs.SetString(ResumeStore.RecordKey, invalid);
                Assert.IsNull(ResumeStore.PendingRecord(), invalid);
                Assert.IsFalse(PlayerPrefs.HasKey(ResumeStore.RecordKey), invalid);
            }

            PlayerPrefs.SetString(ResumeStore.RecordKey, new string('x', ResumeStore.MaxBytes + 1));
            Assert.IsNull(ResumeStore.PendingRecord());
            Assert.IsFalse(PlayerPrefs.HasKey(ResumeStore.RecordKey));
        }

        [Test]
        public void Save_rejects_a_record_over_16_kib()
        {
            string template = "https://shop.example/" + new string('x', ResumeStore.MaxBytes) + "/{request_id}";
            var error = Assert.Throws<M2CCheckoutException>(() =>
                ResumeStore.Save("req_large", "session", StatusSource.Url(template)));
            Assert.AreEqual(M2CErrorCode.InvalidRequest, error.Code);
            Assert.IsFalse(PlayerPrefs.HasKey(ResumeStore.RecordKey));
        }

        [Test]
        public void Reading_or_clearing_removes_legacy_multi_key_state()
        {
            PlayerPrefs.SetInt("m2c.checkout.active", 1);
            PlayerPrefs.SetString("m2c.checkout.request_id", "legacy");
            Assert.IsNull(ResumeStore.PendingRecord());
            Assert.IsFalse(PlayerPrefs.HasKey("m2c.checkout.active"));
            Assert.IsFalse(PlayerPrefs.HasKey("m2c.checkout.request_id"));
        }

        [Test]
        public void Missing_callback_configuration_does_not_destroy_recovery()
        {
            ResumeStore.Save(
                "req_callback",
                "session",
                StatusSource.Callback(_ => Task.FromResult(ClientStatus.Processing)));
            var client = new M2CCheckoutClient(new M2CConfig
            {
                StatusSource = StatusSource.M2C
            });

            var error = Assert.ThrowsAsync<M2CCheckoutException>(async () => await client.TryResumeAsync());

            Assert.AreEqual(M2CErrorCode.InvalidRequest, error.Code);
            Assert.AreEqual("req_callback", ResumeStore.PendingRecord()?.RequestId);
        }
    }

    public class UnityGapFlowTests
    {
        [SetUp]
        public void SetUp()
        {
            ResumeStore.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            ResumeStore.Clear();
        }

        [Test]
        public async Task Backend_session_accepts_omitted_or_positive_ttl_and_rejects_expired_values()
        {
            foreach (int? ttl in new int?[] { null, 60 })
            {
                var client = Client(StatusSource.Callback(_ => Task.FromResult(ClientStatus.Processing)), new InspectingBrowser());
                CheckoutResult result = await client.StartFromSessionAsync(Session(ttl));
                Assert.AreEqual(CheckoutOutcome.Canceled, result.Outcome);
            }

            foreach (int? ttl in new int?[] { 0, -1 })
            {
                var client = Client(StatusSource.Callback(_ => Task.FromResult(ClientStatus.Processing)), new InspectingBrowser());
                var error = Assert.ThrowsAsync<M2CCheckoutException>(async () => await client.StartFromSessionAsync(Session(ttl)));
                Assert.AreEqual(M2CErrorCode.CheckoutExpired, error.Code);
            }
        }

        [TestCase(ClientStatus.Completed, CheckoutOutcome.Completed)]
        [TestCase(ClientStatus.Processing, CheckoutOutcome.PendingTimeout)]
        public async Task Mismatched_link_reconciles_only_the_active_request(ClientStatus status, CheckoutOutcome expected)
        {
            var seen = new List<string>();
            bool recoveryPresentDuringRead = false;
            StatusSource source = StatusSource.Callback(id =>
            {
                seen.Add(id);
                recoveryPresentDuringRead = ResumeStore.PendingRecord()?.RequestId == "active";
                return Task.FromResult(status);
            });
            var browser = new InspectingBrowser
            {
                Outcome = BrowserOutcome.Returned("mygame://checkout/return?request_id=forged")
            };
            var client = Client(source, browser);
            client.ConfigForTest.Poll = new PollSchedule(new[] { 0.0, 0.005 }, 0.02);

            CheckoutResult result = await client.StartFromSessionAsync(Session(60));

            Assert.AreEqual(expected, result.Outcome);
            CollectionAssert.IsNotEmpty(seen);
            CollectionAssert.AllItemsAreInstancesOfType(seen, typeof(string));
            Assert.IsTrue(seen.TrueForAll(id => id == "active"));
            Assert.IsTrue(recoveryPresentDuringRead);
            Assert.IsNull(ResumeStore.PendingRecord());
        }

        [Test]
        public void Client_auction_rejects_a_missing_ttl_before_browser_launch()
        {
            var browser = new InspectingBrowser();
            var config = new M2CConfig
            {
                PublishableKey = "pub_test_example",
                StatusSource = StatusSource.Callback(_ => Task.FromResult(ClientStatus.Processing)),
                ReturnUrl = "mygame://checkout/return",
                CancelUrl = "mygame://checkout/cancel"
            };
            var client = new M2CCheckoutClient(
                config,
                (_, __, ___) => Task.FromResult(new AuctionResult
                {
                    CheckoutUrl = "https://vendor.example/checkout",
                    RequestId = "active",
                    Ttl = 0
                }),
                createBrowser: _ => browser);

            var error = Assert.ThrowsAsync<M2CCheckoutException>(async () =>
                await client.StartAsync(new AuctionRequest { TransactionValue = 1 }));

            Assert.AreEqual(M2CErrorCode.Unknown, error.Code);
            Assert.AreEqual(0, browser.LaunchCalls);
        }

        [Test]
        public async Task Recovery_is_present_before_launch_and_cleared_before_pre_exposure_fallback()
        {
            bool presentAtLaunch = false;
            bool absentInFallback = false;
            var browser = new InspectingBrowser
            {
                Outcome = BrowserOutcome.PreparedLaunchFailed,
                OnLaunch = () => presentAtLaunch = ResumeStore.PendingRecord()?.RequestId == "active"
            };
            var config = new M2CConfig
            {
                StatusSource = StatusSource.Callback(_ => Task.FromResult(ClientStatus.Processing)),
                ReturnUrl = "mygame://checkout/return",
                CancelUrl = "mygame://checkout/cancel",
                FallbackHandler = (_, __) =>
                {
                    absentInFallback = ResumeStore.PendingRecord() == null;
                    return Task.FromResult(FallbackDecision.Accepted);
                }
            };
            var client = new TestClient(config, browser);

            CheckoutResult result = await client.StartFromSessionAsync(Session(60));

            Assert.IsTrue(presentAtLaunch);
            Assert.IsTrue(absentInFallback);
            Assert.AreEqual(CheckoutOutcome.FallbackStarted, result.Outcome);
        }

        private static CheckoutSession Session(int? ttl)
        {
            return new CheckoutSession
            {
                CheckoutUrl = "https://vendor.example/checkout",
                RequestId = "active",
                Ttl = ttl
            };
        }

        private static TestClient Client(StatusSource source, InspectingBrowser browser)
        {
            return new TestClient(new M2CConfig
            {
                StatusSource = source,
                ReturnUrl = "mygame://checkout/return",
                CancelUrl = "mygame://checkout/cancel"
            }, browser);
        }

        private sealed class TestClient
        {
            public readonly M2CConfig ConfigForTest;
            private readonly M2CCheckoutClient _client;

            public TestClient(M2CConfig config, InspectingBrowser browser)
            {
                ConfigForTest = config;
                _client = new M2CCheckoutClient(
                    config,
                    (_, __, ___) => Task.FromResult(new AuctionResult
                    {
                        CheckoutUrl = "https://vendor.example/checkout",
                        RequestId = "active",
                        Ttl = 60
                    }),
                    (seconds, token) => Task.Delay(TimeSpan.FromSeconds(seconds), token),
                    createBrowser: _ => browser);
            }

            public Task<CheckoutResult> StartFromSessionAsync(CheckoutSession session)
            {
                return _client.StartFromSessionAsync(session);
            }
        }

        private sealed class InspectingBrowser : ICheckoutBrowser
        {
            public BrowserOutcome Outcome = BrowserOutcome.Dismissed;
            public Action OnLaunch;
            public int LaunchCalls;
            public bool RequiresReturnUrl => false;

            public Task<BrowserOutcome> LaunchAsync(string checkoutUrl, string returnUrl, string cancelUrl)
            {
                LaunchCalls++;
                OnLaunch?.Invoke();
                return Task.FromResult(Outcome);
            }
        }
    }
}
