using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Debryfin.Configuration;
using Debryfin.Providers;
using MediaBrowser.Model.Logging;
using NSubstitute;
using Xunit;

namespace Debryfin.Tests
{
    // P1-05: TorBox two-step resolution (createwebdownload -> requestdl) simulated offline
    // with a scripted HttpMessageHandler: each test scripts the sequence of responses the
    // provider should see, and the assertions pin the exact wire request of both steps.
    public class TorBoxProviderTests
    {
        private const string CreateUrl = "https://api.torbox.app/v1/api/webdl/createwebdownload";
        private const string RequestdlUrl = "https://api.torbox.app/v1/api/webdl/requestdl";
        private const string UserUrl = "https://api.torbox.app/v1/api/user/me";
        private const string SourceLink = "https://1fichier.com/?abc123";
        private const string ApiKey = "tb-secret-key-001";
        private const string WebDownloadId = "wb-123";
        private const string DirectUrl = "https://torbox.dl/d/123";

        private const string CreateSuccessJson =
            $"{{\"success\":true,\"data\":{{\"auth_id\":\"1\",\"hash\":\"abc\",\"webdownload_id\":\"{WebDownloadId}\"}},\"detail\":null,\"error\":null}}";

        private static readonly string RequestdlSuccessJson =
            $"{{\"success\":true,\"data\":\"{DirectUrl}\",\"detail\":null,\"error\":null}}";

        private const string NotReadyJson =
            "{\"success\":false,\"data\":null,\"detail\":\"Download in progress\",\"error\":\"not ready\"}";

        // --- Full two-step flow (hoster cached: ready on the first requestdl) ---

        [Fact]
        public async Task Unrestrict_CachedHit_CompletesTwoStepFlow_ReturnsDirectUrl()
        {
            var handler = new ScriptedHandler((index, _) =>
            {
                return index == 0
                    ? Json(HttpStatusCode.OK, CreateSuccessJson)
                    : Json(HttpStatusCode.OK, RequestdlSuccessJson);
            });
            using var client = new HttpClient(handler);
            var provider = new TorBoxProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.None, result.Error);
            Assert.Equal(DirectUrl, result.Url);
            Assert.Equal(2, handler.Requests.Count);
            AssertCreateRequest(handler, 0);
            AssertRequestdlRequest(handler, 1);
        }

        // --- Not cached: polling until ready, or Pending when the budget runs out ---

        [Fact]
        public async Task Unrestrict_NotReadyThenReady_PollsAndReturnsDirectUrl()
        {
            var handler = new ScriptedHandler((index, _) =>
            {
                switch (index)
                {
                    case 0:
                        return Json(HttpStatusCode.OK, CreateSuccessJson);
                    case 1:
                        return Json(HttpStatusCode.OK, NotReadyJson);
                    default:
                        return Json(HttpStatusCode.OK, RequestdlSuccessJson);
                }
            });
            using var client = new HttpClient(handler);
            var provider = new TorBoxProvider(client, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10));

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.None, result.Error);
            Assert.Equal(DirectUrl, result.Url);
            Assert.Equal(3, handler.Requests.Count);
        }

        [Fact]
        public async Task Unrestrict_NeverReady_ReturnsPendingWithoutLeakingSecrets()
        {
            var handler = new ScriptedHandler((index, _) =>
            {
                return index == 0
                    ? Json(HttpStatusCode.OK, CreateSuccessJson)
                    : Json(HttpStatusCode.OK, NotReadyJson);
            });
            using var client = new HttpClient(handler);
            var provider = new TorBoxProvider(client, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10));

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Pending, result.Error);
            Assert.Null(result.Url);
            Assert.DoesNotContain(ApiKey, result.Message);
        }

        [Fact]
        public async Task Unrestrict_CreateNotCachedError_ReturnsPendingWithoutRequestdl()
        {
            var handler = new ScriptedHandler((index, _) =>
                Json(HttpStatusCode.OK, "{\"success\":false,\"data\":null,\"detail\":null,\"error\":\"Not cached, download queued\"}"));
            using var client = new HttpClient(handler);
            var provider = new TorBoxProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Pending, result.Error);
            Assert.Single(handler.Requests);
        }

        // --- Error classification (create step) ---

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.Forbidden)]
        public async Task Unrestrict_Create401Or403_ReturnsAuthFailed(HttpStatusCode status)
        {
            var handler = new ScriptedHandler((index, _) => Json(status, "{}"));
            using var client = new HttpClient(handler);
            var provider = new TorBoxProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.AuthFailed, result.Error);
            Assert.Null(result.Url);
        }

        [Fact]
        public async Task Unrestrict_Create429_ReturnsRateLimitedWithRetryAfter()
        {
            var handler = new ScriptedHandler((index, _) =>
                Json(HttpStatusCode.TooManyRequests, "{}", TimeSpan.FromSeconds(30)));
            using var client = new HttpClient(handler);
            var provider = new TorBoxProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.RateLimited, result.Error);
            Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
        }

        [Fact]
        public async Task Unrestrict_Requestdl429_ReturnsRateLimitedWithRetryAfter()
        {
            var handler = new ScriptedHandler((index, _) =>
            {
                return index == 0
                    ? Json(HttpStatusCode.OK, CreateSuccessJson)
                    : Json(HttpStatusCode.TooManyRequests, "{}", TimeSpan.FromSeconds(45));
            });
            using var client = new HttpClient(handler);
            var provider = new TorBoxProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.RateLimited, result.Error);
            Assert.Equal(TimeSpan.FromSeconds(45), result.RetryAfter);
        }

        [Fact]
        public async Task Unrestrict_Create5xx_ReturnsTransient()
        {
            var handler = new ScriptedHandler((index, _) => Json(HttpStatusCode.InternalServerError, "{}"));
            using var client = new HttpClient(handler);
            var provider = new TorBoxProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Transient, result.Error);
        }

        [Fact]
        public async Task Unrestrict_CreateOtherApiError_ReturnsTransient()
        {
            var handler = new ScriptedHandler((index, _) =>
                Json(HttpStatusCode.OK, "{\"success\":false,\"data\":null,\"detail\":null,\"error\":\"link generation failed\"}"));
            using var client = new HttpClient(handler);
            var provider = new TorBoxProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Transient, result.Error);
        }

        [Fact]
        public async Task Unrestrict_NetworkError_ReturnsTransientWithoutLeakingSecrets()
        {
            using var client = new HttpClient(new ThrowingHandler());
            var provider = new TorBoxProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Transient, result.Error);
            Assert.DoesNotContain(ApiKey, result.Message);
        }

        [Fact]
        public async Task Unrestrict_CancelledToken_RethrowsCancellation()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var provider = new TorBoxProvider(new HttpClient(new ScriptedHandler((index, _) => Json(HttpStatusCode.OK, "{}"))));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                provider.UnrestrictAsync(SourceLink, ApiKey, cts.Token));
        }

        // --- Secrets: the key travels in requestdl's query string; provider text must be redacted ---

        [Fact]
        public async Task Unrestrict_ProviderErrorEchoingTheRequest_LeadsToRedactedMessage()
        {
            // The API echoes the failing request (with token=...) in its error text: the
            // provider message that reaches the log must have the key redacted.
            var echoed = $"{RequestdlUrl}?token={ApiKey}&web_id={WebDownloadId}";
            var handler = new ScriptedHandler((index, _) =>
                Json(HttpStatusCode.OK,
                    $"{{\"success\":false,\"data\":null,\"detail\":\"{echoed}\",\"error\":\"download failed\"}}"));
            using var client = new HttpClient(handler);
            var provider = new TorBoxProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Transient, result.Error);
            Assert.DoesNotContain(ApiKey, result.Message);
            Assert.Contains("token=***", result.Message);
        }

        // --- Account info / validation ---

        [Fact]
        public async Task GetUserInfo_PremiumAccount_ReturnsEmailAndExpiration()
        {
            var handler = new ScriptedHandler((index, _) =>
                Json(HttpStatusCode.OK,
                    "{\"success\":true,\"data\":{\"user_id\":7,\"username\":\"tbuser\",\"email\":\"tb@example.com\",\"plan\":\"premium\",\"premium_until\":\"2026-12-31T00:00:00Z\"}}"));
            using var client = new HttpClient(handler);
            var provider = new TorBoxProvider(client);

            var info = await provider.GetUserInfoAsync(ApiKey, CancellationToken.None);

            var request = handler.Requests[0];
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(UserUrl, request.RequestUri!.ToString());
            Assert.Equal("Bearer " + ApiKey, request.Headers.Authorization!.ToString());
            Assert.Equal("tbuser", info.Username);
            Assert.Equal("tb@example.com", info.Email);
            Assert.True(info.IsPremium);
            Assert.Equal(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc), info.ExpirationUtc);
        }

        [Fact]
        public async Task ValidateApiKey_ValidUser_ReturnsValid()
        {
            var handler = new ScriptedHandler((index, _) =>
                Json(HttpStatusCode.OK, "{\"success\":true,\"data\":{\"username\":\"tbuser\",\"plan\":\"premium\"}}"));
            using var client = new HttpClient(handler);
            var provider = new TorBoxProvider(client);

            var validation = await provider.ValidateApiKeyAsync(ApiKey, CancellationToken.None);

            Assert.True(validation.IsValid);
        }

        [Fact]
        public async Task ValidateApiKey_ErrorStatus_ReturnsNotValidWithoutLeakingSecrets()
        {
            var handler = new ScriptedHandler((index, _) => Json(HttpStatusCode.Unauthorized, "{}"));
            using var client = new HttpClient(handler);
            var provider = new TorBoxProvider(client);

            var validation = await provider.ValidateApiKeyAsync(ApiKey, CancellationToken.None);

            Assert.False(validation.IsValid);
            Assert.DoesNotContain(ApiKey, validation.Message);
        }

        // --- Identity / wiring ---

        [Fact]
        public void Properties_ExposeIdentityAnd2hTtl()
        {
            var provider = new TorBoxProvider(new HttpClient(new ScriptedHandler((index, _) => Json(HttpStatusCode.OK, "{}"))));

            Assert.Equal(DebridProvider.TorBox, provider.Id);
            Assert.Equal("TorBox", provider.DisplayName);
            Assert.Equal(TimeSpan.FromHours(2), provider.DefaultLinkTtl);
        }

        [Fact]
        public void DebridService_RegistersNativeTorBoxProvider()
        {
            // Confirms the composition point wires the native provider, not the broken legacy stub.
            var handler = new ScriptedHandler((index, _) => Json(HttpStatusCode.OK, "{}"));
            using var client = new HttpClient(handler);
            var service = new DebridService(Substitute.For<ILogger>(), client);

            var provider = service.Registry.Get(DebridProvider.TorBox);

            Assert.IsType<TorBoxProvider>(provider);
        }

        // --- Helpers ---

        private static void AssertCreateRequest(ScriptedHandler handler, int index)
        {
            var request = handler.Requests[index];
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(CreateUrl, request.RequestUri!.ToString());
            Assert.Equal("Bearer " + ApiKey, request.Headers.Authorization!.ToString());
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            // FormUrlEncodedContent URL-encodes the value: pin the exact on-wire body.
            Assert.Equal("link=" + Uri.EscapeDataString(SourceLink), handler.Bodies[index]);
        }

        private static void AssertRequestdlRequest(ScriptedHandler handler, int index)
        {
            var request = handler.Requests[index];
            Assert.Equal(HttpMethod.Get, request.Method);
            // The key unavoidably travels in the query string (token=...); that is the only
            // sanctioned credential-in-URL exception (see NoSecretsInUrlTests).
            Assert.Equal(
                $"{RequestdlUrl}?token={Uri.EscapeDataString(ApiKey)}&web_id={Uri.EscapeDataString(WebDownloadId)}",
                request.RequestUri!.ToString());
            Assert.Null(request.Content);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json, TimeSpan? retryAfter = null)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(json) };
            if (retryAfter.HasValue)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter.Value);
            }

            return response;
        }
    }

    // Fake HTTP handler for multi-request flows: scripts one response per outgoing request
    // (by request index) and records every request (method, URL, headers, body). Never
    // touches the network.
    internal sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpRequestMessage, HttpResponseMessage> _responder;

        public List<HttpRequestMessage> Requests { get; } = new List<HttpRequestMessage>();
        public List<string> Bodies { get; } = new List<string>();

        public ScriptedHandler(Func<int, HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder ?? throw new ArgumentNullException(nameof(responder));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Requests.Count;
            Requests.Add(request);
            Bodies.Add(request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return _responder(index, request);
        }
    }
}
