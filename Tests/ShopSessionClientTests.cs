using System;
using System.Threading.Tasks;
using M2C.Checkout.Internal;
using NUnit.Framework;

namespace M2C.Checkout.Tests
{
    public class ShopSessionClientTests
    {
        private const string SessionId = "550e8400-e29b-41d4-a716-446655440000";
        private static readonly DateTimeOffset SessionExpiresAt =
            DateTimeOffset.FromUnixTimeSeconds(1700003600);
        private static readonly DateTimeOffset ShopUrlExpiresAt =
            DateTimeOffset.FromUnixTimeSeconds(1700000300);

        [Test]
        public async Task Create_launch_forwards_request_and_returns_server_handle()
        {
            var request = new ShopSessionRequest
            {
                Currency = "USD",
                Language = "en-US",
                Segments = new[] { "returning" },
                ReturnUrl = "mygame://shop/closed"
            };
            ShopSessionRequest captured = default(ShopSessionRequest);
            int createCalls = 0;
            var browser = new RecordingShopBrowser();
            var client = Client(
                create: value =>
                {
                    createCalls++;
                    captured = value;
                    return Task.FromResult(Created());
                },
                browser: browser,
                mode: M2CBrowserMode.InAppPersistent);

            ShopSessionHandle handle = await client.StartShopSessionAsync(request);

            Assert.AreEqual(1, createCalls);
            Assert.AreEqual(request.Currency, captured.Currency);
            Assert.AreEqual(request.Language, captured.Language);
            CollectionAssert.AreEqual(request.Segments, captured.Segments);
            Assert.AreEqual(request.ReturnUrl, captured.ReturnUrl);
            Assert.AreEqual(1, browser.PrepareCalls);
            Assert.AreEqual(1, browser.LaunchCalls);
            Assert.AreEqual("https://vendor.example/shop", browser.ShopUrl);
            Assert.AreEqual(request.ReturnUrl, browser.ReturnUrl);
            Assert.AreEqual(1, browser.CancelCalls);
            Assert.AreEqual(SessionId, handle.SessionId);
            Assert.AreEqual(SessionExpiresAt, handle.SessionExpiresAt);
            Assert.AreEqual(ShopUrlExpiresAt, handle.ShopUrlExpiresAt);
            Assert.AreEqual("https://vendor.example/shop", handle.ShopUrl);
        }

        [Test]
        public async Task Ttl_expiry_prevents_launch_and_releases_in_flight_guard()
        {
            int clockReads = 0;
            double[] times = { 0, 60 };
            var expiredBrowser = new RecordingShopBrowser();
            var expiredClient = Client(
                create: _ => Task.FromResult(Created(ttl: 60)),
                browser: expiredBrowser,
                monotonicSeconds: () => times[clockReads++]);

            var error = Assert.ThrowsAsync<M2CCheckoutException>(async () =>
                await expiredClient.StartShopSessionAsync(Request()));
            Assert.AreEqual(M2CErrorCode.CheckoutExpired, error.Code);
            Assert.AreEqual(0, expiredBrowser.LaunchCalls);
            Assert.AreEqual(1, expiredBrowser.CancelCalls);

            var nextBrowser = new RecordingShopBrowser();
            ShopSessionHandle next = await Client(browser: nextBrowser)
                .StartShopSessionAsync(Request());
            Assert.AreEqual(SessionId, next.SessionId);
            Assert.AreEqual(1, nextBrowser.LaunchCalls);
        }

        [Test]
        public async Task Concurrent_start_is_rejected_while_status_read_remains_independent()
        {
            var createGate = new TaskCompletionSource<ShopSessionResult>();
            int firstCreateCalls = 0;
            int secondCreateCalls = 0;
            int statusCalls = 0;
            string statusSessionId = null;
            var firstClient = Client(
                create: _ =>
                {
                    firstCreateCalls++;
                    return createGate.Task;
                },
                readStatus: value =>
                {
                    statusCalls++;
                    statusSessionId = value;
                    return Task.FromResult(new ShopSessionStatus(value, ShopSessionState.Active, 2));
                });
            var secondClient = Client(create: _ =>
            {
                secondCreateCalls++;
                return Task.FromResult(Created());
            });

            Task<ShopSessionHandle> first = firstClient.StartShopSessionAsync(Request());
            Assert.AreEqual(1, firstCreateCalls);

            var error = Assert.ThrowsAsync<M2CCheckoutException>(async () =>
                await secondClient.StartShopSessionAsync(Request()));
            Assert.AreEqual(M2CErrorCode.InvalidRequest, error.Code);
            Assert.AreEqual(0, secondCreateCalls);

            ShopSessionStatus status = await firstClient.ReadShopSessionStatusAsync(SessionId.ToUpperInvariant());
            Assert.AreEqual(ShopSessionState.Active, status.Status);
            Assert.AreEqual(SessionId, statusSessionId);
            Assert.AreEqual(1, statusCalls);

            createGate.TrySetResult(Created());
            Assert.AreEqual(SessionId, (await first).SessionId);
        }

        [Test]
        public void Invalid_request_and_status_id_fail_before_adapters()
        {
            int createCalls = 0;
            int statusCalls = 0;
            int browserCalls = 0;
            var client = Client(
                create: _ =>
                {
                    createCalls++;
                    return Task.FromResult(Created());
                },
                readStatus: value =>
                {
                    statusCalls++;
                    return Task.FromResult(new ShopSessionStatus(value, ShopSessionState.Active, 0));
                },
                createBrowser: () =>
                {
                    browserCalls++;
                    return new RecordingShopBrowser();
                });

            var requestError = Assert.ThrowsAsync<M2CCheckoutException>(async () =>
                await client.StartShopSessionAsync(new ShopSessionRequest { Currency = "" }));
            Assert.AreEqual(M2CErrorCode.InvalidRequest, requestError.Code);
            Assert.AreEqual(0, createCalls);
            Assert.AreEqual(0, browserCalls);

            var statusError = Assert.ThrowsAsync<M2CCheckoutException>(async () =>
                await client.ReadShopSessionStatusAsync("not-a-session-id"));
            Assert.AreEqual(M2CErrorCode.InvalidRequest, statusError.Code);
            Assert.AreEqual(0, statusCalls);
        }

        [Test]
        public void Start_shop_session_coroutine_returns_handle()
        {
            ShopSessionHandle seen = null;
            M2CCheckoutException seenError = null;
            var routine = Client().StartShopSession(
                Request(),
                value => seen = value,
                error => seenError = error);

            while (routine.MoveNext()) { }

            Assert.NotNull(seen);
            Assert.AreEqual(SessionId, seen.SessionId);
            Assert.IsNull(seenError);
        }

        [Test]
        public void Read_shop_session_status_coroutine_waits_and_returns_status()
        {
            var gate = new TaskCompletionSource<ShopSessionStatus>();
            ShopSessionStatus seen = null;
            M2CCheckoutException seenError = null;
            var client = Client(readStatus: _ => gate.Task);
            var routine = client.ReadShopSessionStatus(
                SessionId,
                value => seen = value,
                error => seenError = error);

            Assert.IsTrue(routine.MoveNext());
            Assert.IsNull(seen);
            gate.SetResult(new ShopSessionStatus(SessionId, ShopSessionState.Active, 3));
            Assert.IsFalse(routine.MoveNext());

            Assert.NotNull(seen);
            Assert.AreEqual(3, seen.CompletedPurchases);
            Assert.IsNull(seenError);
        }

        [Test]
        public void Start_shop_session_coroutine_unwraps_faulted_task_error()
        {
            var original = new M2CCheckoutException(M2CErrorCode.Network, "launch failed");
            ShopSessionHandle seen = null;
            M2CCheckoutException seenError = null;
            var client = Client(create: _ => Task.FromException<ShopSessionResult>(original));
            var routine = client.StartShopSession(
                Request(),
                value => seen = value,
                error => seenError = error);

            while (routine.MoveNext()) { }

            Assert.IsNull(seen);
            Assert.AreSame(original, seenError);
        }

        [Test]
        public void Read_shop_session_status_coroutine_reports_synchronous_validation_error()
        {
            ShopSessionStatus seen = null;
            M2CCheckoutException seenError = null;
            var routine = Client().ReadShopSessionStatus(
                "not-a-session-id",
                value => seen = value,
                error => seenError = error);

            while (routine.MoveNext()) { }

            Assert.IsNull(seen);
            Assert.NotNull(seenError);
            Assert.AreEqual(M2CErrorCode.InvalidRequest, seenError.Code);
        }

        [Test]
        public void Read_shop_session_status_coroutine_maps_cancellation_to_unknown()
        {
            var canceled = new TaskCompletionSource<ShopSessionStatus>();
            canceled.SetCanceled();
            ShopSessionStatus seen = null;
            M2CCheckoutException seenError = null;
            var client = Client(readStatus: _ => canceled.Task);
            var routine = client.ReadShopSessionStatus(
                SessionId,
                value => seen = value,
                error => seenError = error);

            while (routine.MoveNext()) { }

            Assert.IsNull(seen);
            Assert.NotNull(seenError);
            Assert.AreEqual(M2CErrorCode.Unknown, seenError.Code);
            Assert.AreEqual("shop session operation was canceled", seenError.Message);
        }

        [Test]
        public void Browser_failure_cancels_prepared_launch_and_releases_guard()
        {
            var browser = new RecordingShopBrowser
            {
                LaunchError = new M2CCheckoutException(M2CErrorCode.Network, "launch failed")
            };
            var client = Client(browser: browser);

            var error = Assert.ThrowsAsync<M2CCheckoutException>(async () =>
                await client.StartShopSessionAsync(Request()));
            Assert.AreEqual(M2CErrorCode.Network, error.Code);
            Assert.AreEqual(1, browser.CancelCalls);

            Assert.DoesNotThrowAsync(async () =>
            {
                await Client().StartShopSessionAsync(Request());
            });
        }

        [Test]
        public void Browser_preflight_failure_prevents_session_creation_and_releases_guard()
        {
            int createCalls = 0;
            var browser = new RecordingShopBrowser
            {
                PrepareError = new M2CCheckoutException(
                    M2CErrorCode.InvalidRequest,
                    "an iOS shop browser is already open")
            };
            var client = Client(
                create: _ =>
                {
                    createCalls++;
                    return Task.FromResult(Created());
                },
                browser: browser);

            var error = Assert.ThrowsAsync<M2CCheckoutException>(async () =>
                await client.StartShopSessionAsync(Request()));
            Assert.AreEqual(M2CErrorCode.InvalidRequest, error.Code);
            Assert.AreEqual(0, createCalls);
            Assert.AreEqual(0, browser.LaunchCalls);
            Assert.AreEqual(1, browser.CancelCalls);

            Assert.DoesNotThrowAsync(async () =>
            {
                await Client().StartShopSessionAsync(Request());
            });
        }

        [Test]
        public void Wire_request_adds_detected_metadata_and_omits_absent_fields()
        {
            string full = M2CApi.BuildShopSessionBody(new ShopSessionRequest
            {
                Currency = "USD",
                Language = "en-US",
                Segments = new[] { "returning", "vip" },
                ReturnUrl = "mygame://shop/closed"
            });

            StringAssert.Contains("\"device_type\":\"desktop\"", full);
            StringAssert.Contains("\"platform\":\"desktop\"", full);
            StringAssert.Contains("\"currency\":\"USD\"", full);
            StringAssert.Contains("\"language\":\"en-US\"", full);
            StringAssert.Contains("\"segments\":[\"returning\",\"vip\"]", full);
            StringAssert.Contains("\"return_url\":\"mygame://shop/closed\"", full);

            string minimal = M2CApi.BuildShopSessionBody(Request());
            StringAssert.DoesNotContain("\"language\"", minimal);
            StringAssert.DoesNotContain("\"segments\"", minimal);
            StringAssert.DoesNotContain("\"return_url\"", minimal);
        }

        [Test]
        public void Session_response_requires_ttl_on_the_winner()
        {
            const string valid =
                "{\"winner\":{\"shop_url\":\"https://vendor.example/shop\",\"ttl\":300," +
                "\"launch_expires_at\":\"2023-11-14T22:18:20Z\"}," +
                "\"session_id\":\"550e8400-e29b-41d4-a716-446655440000\"," +
                "\"expires_at\":\"2023-11-14T23:13:20Z\"}";
            Assert.AreEqual(300, M2CApi.ParseShopSessionResponse(valid).Ttl);

            const string nestedOnly =
                "{\"winner\":{\"shop_url\":\"https://vendor.example/shop\"," +
                "\"launch_expires_at\":\"2023-11-14T22:18:20Z\"}," +
                "\"session_id\":\"550e8400-e29b-41d4-a716-446655440000\"," +
                "\"expires_at\":\"2023-11-14T23:13:20Z\",\"future\":{\"ttl\":300}}";
            Assert.Throws<M2CCheckoutException>(() => M2CApi.ParseShopSessionResponse(nestedOnly));

            const string stringTtl =
                "{\"winner\":{\"shop_url\":\"https://vendor.example/shop\",\"ttl\":\"300\"," +
                "\"launch_expires_at\":\"2023-11-14T22:18:20Z\"}," +
                "\"session_id\":\"550e8400-e29b-41d4-a716-446655440000\"," +
                "\"expires_at\":\"2023-11-14T23:13:20Z\"}";
            Assert.Throws<M2CCheckoutException>(() => M2CApi.ParseShopSessionResponse(stringTtl));
        }

        [TestCase("")]
        [TestCase(".123")]
        [TestCase(".123456")]
        [TestCase(".123456789")]
        public void Session_response_accepts_supported_timestamp_precisions(string fraction)
        {
            string timestamp = "2023-11-14T23:13:20" + fraction + "Z";
            ShopSessionResult result = M2CApi.ParseShopSessionResponse(SessionResponse(
                expiresAt: timestamp,
                launchExpiresAt: timestamp));

            Assert.AreEqual(SessionId, result.SessionId);
            Assert.AreEqual(
                fraction.Length == 0 ? 1_700_003_600_000L : 1_700_003_600_123L,
                result.SessionExpiresAt.ToUnixTimeMilliseconds());
            Assert.AreEqual(result.SessionExpiresAt, result.ShopUrlExpiresAt);
        }

        [Test]
        public void Session_response_rejects_malformed_success_payloads()
        {
            var cases = new[]
            {
                new { Name = "ttl below minimum", Json = SessionResponse(ttl: 59) },
                new { Name = "ttl above maximum", Json = SessionResponse(ttl: 3601) },
                new
                {
                    Name = "non-https shop URL",
                    Json = SessionResponse(shopUrl: "http://vendor.example/shop")
                },
                new { Name = "malformed session ID", Json = SessionResponse(sessionId: "not-a-session-id") },
                new { Name = "malformed session expiry", Json = SessionResponse(expiresAt: "not-a-date") },
                new { Name = "malformed launch expiry", Json = SessionResponse(launchExpiresAt: "not-a-date") }
            };
            foreach (var value in cases)
            {
                Assert.Throws<M2CCheckoutException>(
                    () => M2CApi.ParseShopSessionResponse(value.Json),
                    value.Name);
            }
        }

        [Test]
        public void Session_endpoints_use_endpoint_specific_error_mapping()
        {
            var creationCases = new[]
            {
                new ErrorCase(400, M2CErrorCode.InvalidRequest),
                new ErrorCase(401, M2CErrorCode.AuthenticationFailed),
                new ErrorCase(403, M2CErrorCode.OriginNotAllowed),
                new ErrorCase(404, M2CErrorCode.NoVendorsAvailable),
                new ErrorCase(409, M2CErrorCode.InvalidRequest),
                new ErrorCase(429, M2CErrorCode.RateLimited),
                new ErrorCase(503, M2CErrorCode.ServiceUnavailable),
                new ErrorCase(422, M2CErrorCode.Unknown)
            };
            foreach (ErrorCase value in creationCases)
            {
                M2CCheckoutException error = M2CApi.MapShopSessionCreationError(Response(value.Status));
                Assert.AreEqual(value.Code, error.Code, "HTTP " + value.Status);
                Assert.AreEqual(value.Status, error.HttpStatus, "HTTP " + value.Status);
                if (value.Status == 429 || value.Status == 503)
                    Assert.AreEqual(7, error.RetryAfter, "HTTP " + value.Status);
            }

            Assert.AreEqual(
                M2CErrorCode.SessionNotFound,
                M2CApi.MapShopSessionStatusError(Response(404)).Code);
            Assert.AreEqual(
                M2CErrorCode.ServiceUnavailable,
                M2CApi.MapShopSessionStatusError(Response(503)).Code);
        }

        private static M2CCheckoutClient Client(
            Func<ShopSessionRequest, Task<ShopSessionResult>> create = null,
            Func<string, Task<ShopSessionStatus>> readStatus = null,
            RecordingShopBrowser browser = null,
            Func<IShopSessionBrowser> createBrowser = null,
            Func<double> monotonicSeconds = null,
            M2CBrowserMode mode = M2CBrowserMode.InAppPreferred)
        {
            browser = browser ?? new RecordingShopBrowser();
            return new M2CCheckoutClient(
                new M2CConfig
                {
                    PublishableKey = "pub_test_session",
                    BrowserMode = mode
                },
                createAuction: null,
                createShopSession: create ?? (_ => Task.FromResult(Created())),
                readShopSessionStatus: readStatus ?? (value => Task.FromResult(
                    new ShopSessionStatus(value, ShopSessionState.Active, 0))),
                createShopSessionBrowser: createBrowser ?? (() => browser),
                monotonicSeconds: monotonicSeconds ?? (() => 1));
        }

        private static ShopSessionRequest Request()
        {
            return new ShopSessionRequest { Currency = "USD" };
        }

        private static ShopSessionResult Created(int ttl = 300)
        {
            return new ShopSessionResult
            {
                SessionId = SessionId,
                SessionExpiresAt = SessionExpiresAt,
                ShopUrlExpiresAt = ShopUrlExpiresAt,
                ShopUrl = "https://vendor.example/shop",
                Ttl = ttl
            };
        }

        private static HttpResponse Response(int status)
        {
            return new HttpResponse
            {
                TransportOk = true,
                Status = status,
                Text = "{\"error\":\"request rejected\"}",
                RetryAfter = "7"
            };
        }

        private static string SessionResponse(
            string sessionId = SessionId,
            string expiresAt = "2023-11-14T23:13:20Z",
            string shopUrl = "https://vendor.example/shop",
            int ttl = 300,
            string launchExpiresAt = "2023-11-14T22:18:20Z")
        {
            return "{\"session_id\":\"" + sessionId + "\",\"expires_at\":\"" + expiresAt
                + "\",\"winner\":{\"shop_url\":\"" + shopUrl + "\",\"ttl\":" + ttl
                + ",\"launch_expires_at\":\"" + launchExpiresAt + "\"}}";
        }

        private struct ErrorCase
        {
            public readonly int Status;
            public readonly M2CErrorCode Code;

            public ErrorCase(int status, M2CErrorCode code)
            {
                Status = status;
                Code = code;
            }
        }

        private sealed class RecordingShopBrowser : IShopSessionBrowser
        {
            public int PrepareCalls;
            public int LaunchCalls;
            public int CancelCalls;
            public string ShopUrl;
            public string ReturnUrl;
            public Exception PrepareError;
            public Exception LaunchError;

            public void PrepareLaunch()
            {
                PrepareCalls++;
                if (PrepareError != null) throw PrepareError;
            }

            public Task LaunchAsync(string shopUrl, string returnUrl)
            {
                LaunchCalls++;
                ShopUrl = shopUrl;
                ReturnUrl = returnUrl;
                return LaunchError == null
                    ? Task.CompletedTask
                    : Task.FromException(LaunchError);
            }

            public void CancelPreparedLaunch()
            {
                CancelCalls++;
            }
        }
    }
}
