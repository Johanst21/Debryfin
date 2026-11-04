using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Debryfin.Configuration;
using Debryfin.Providers;
using MediaBrowser.Model.Logging;
using NSubstitute;
using Xunit;

namespace Debryfin.Tests
{
    // P1-01 characterization tests: capture the exact Real-Debrid HTTP request (method,
    // URL, headers, body) so the migration to IDebridProvider can be proven behavior-identical
    // to the legacy DebridService.CallRealDebrid. No real network is used: every request goes
    // through a fake HttpMessageHandler.
    public class RealDebridProviderTests
    {
        private const string UnrestrictUrl = "https://api.real-debrid.com/rest/1.0/unrestrict/link";
        private const string UserUrl = "https://api.real-debrid.com/rest/1.0/user";
        private const string SourceLink = "https://1fichier.com/?abc123";
        private const string ApiKey = "rd-test-token-xyz";
        private const string DirectUrl = "https://real-debrid.com/d/123";

        // --- Characterization: the wire request must be identical before and after migration ---

        [Fact]
        public async Task Unrestrict_SendsIdenticalRequest_ReturnsDirectUrl()
        {
            // Characterization test: drives RealDebridProvider with an injected fake
            // HttpClient. The assertions pin the exact request the legacy method used to send.
            var handler = new CapturingHandler(HttpStatusCode.OK, $"{{\"download\":\"{DirectUrl}\"}}");
            using var client = new HttpClient(handler);
            var provider = new RealDebridProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            // --- Request characterization (identical to DebridService.CallRealDebrid) ---
            AssertUnrestrictRequest(handler);

            // --- Response parsing ---
            Assert.Equal(ErrorKind.None, result.Error);
            Assert.Equal(DirectUrl, result.Url);
        }

        // --- Error classification ---

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.Forbidden)]
        public async Task Unrestrict_401Or403_ReturnsAuthFailed(HttpStatusCode status)
        {
            var handler = new CapturingHandler(status, "{}");
            using var client = new HttpClient(handler);
            var provider = new RealDebridProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.AuthFailed, result.Error);
            Assert.Null(result.Url);
        }

        [Fact]
        public async Task Unrestrict_429_ReturnsRateLimitedWithRetryAfter()
        {
            var handler = new CapturingHandler(HttpStatusCode.TooManyRequests, "{}", TimeSpan.FromSeconds(30));
            using var client = new HttpClient(handler);
            var provider = new RealDebridProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.RateLimited, result.Error);
            Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
        }

        [Fact]
        public async Task Unrestrict_ServerError_ReturnsTransient()
        {
            var handler = new CapturingHandler(HttpStatusCode.InternalServerError, "{}");
            using var client = new HttpClient(handler);
            var provider = new RealDebridProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Transient, result.Error);
        }

        [Fact]
        public async Task Unrestrict_OtherClientError_ReturnsPermanent()
        {
            var handler = new CapturingHandler(HttpStatusCode.BadRequest, "{}");
            using var client = new HttpClient(handler);
            var provider = new RealDebridProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Permanent, result.Error);
        }

        [Fact]
        public async Task Unrestrict_200WithoutDownloadField_ReturnsPermanent()
        {
            var handler = new CapturingHandler(HttpStatusCode.OK, "{}");
            using var client = new HttpClient(handler);
            var provider = new RealDebridProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Permanent, result.Error);
            Assert.Null(result.Url);
        }

        [Fact]
        public async Task Unrestrict_NetworkError_ReturnsTransientWithoutLeakingSecrets()
        {
            using var client = new HttpClient(new ThrowingHandler());
            var provider = new RealDebridProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Transient, result.Error);
            Assert.DoesNotContain(ApiKey, result.Message);
        }

        [Fact]
        public async Task Unrestrict_CancelledToken_RethrowsCancellation()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var provider = new RealDebridProvider(new HttpClient(new CapturingHandler(HttpStatusCode.OK, "{}")));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                provider.UnrestrictAsync(SourceLink, ApiKey, cts.Token));
        }

        // --- Account info / validation ---

        [Fact]
        public async Task GetUserInfo_PremiumAccount_ReturnsUsernameAndPremium()
        {
            var handler = new CapturingHandler(HttpStatusCode.OK,
                "{\"username\":\"rduser\",\"type\":\"premium\"}");
            using var client = new HttpClient(handler);
            var provider = new RealDebridProvider(client);

            var info = await provider.GetUserInfoAsync(ApiKey, CancellationToken.None);

            Assert.Equal(HttpMethod.Get, handler.Request.Method);
            Assert.Equal(UserUrl, handler.Request.RequestUri.ToString());
            Assert.Equal("Bearer " + ApiKey, handler.Request.Headers.Authorization.ToString());
            Assert.Equal("rduser", info.Username);
            Assert.True(info.IsPremium);
        }

        [Fact]
        public async Task GetUserInfo_NonPremiumAccount_ReturnsIsPremiumFalse()
        {
            var handler = new CapturingHandler(HttpStatusCode.OK,
                "{\"username\":\"rduser\",\"type\":\"basic\"}");
            using var client = new HttpClient(handler);
            var provider = new RealDebridProvider(client);

            var info = await provider.GetUserInfoAsync(ApiKey, CancellationToken.None);

            Assert.Equal("rduser", info.Username);
            Assert.False(info.IsPremium);
        }

        [Fact]
        public async Task ValidateApiKey_ValidUser_ReturnsValid()
        {
            var handler = new CapturingHandler(HttpStatusCode.OK,
                "{\"username\":\"rduser\",\"type\":\"premium\"}");
            using var client = new HttpClient(handler);
            var provider = new RealDebridProvider(client);

            var validation = await provider.ValidateApiKeyAsync(ApiKey, CancellationToken.None);

            Assert.True(validation.IsValid);
        }

        [Fact]
        public async Task ValidateApiKey_ErrorStatus_ReturnsNotValid()
        {
            var handler = new CapturingHandler(HttpStatusCode.Unauthorized, "{}");
            using var client = new HttpClient(handler);
            var provider = new RealDebridProvider(client);

            var validation = await provider.ValidateApiKeyAsync(ApiKey, CancellationToken.None);

            Assert.False(validation.IsValid);
            Assert.DoesNotContain(ApiKey, validation.Message);
        }

        // --- Identity / wiring ---

        [Fact]
        public void Properties_ExposeIdentityAnd72hTtl()
        {
            var provider = new RealDebridProvider(new HttpClient(new CapturingHandler(HttpStatusCode.OK, "{}")));

            Assert.Equal(DebridProvider.RealDebrid, provider.Id);
            Assert.Equal("Real-Debrid", provider.DisplayName);
            Assert.Equal(TimeSpan.FromHours(72), provider.DefaultLinkTtl);
        }

        [Fact]
        public void DebridService_RegistersNativeRealDebridProvider()
        {
            // Confirms the composition point now wires the native provider, not the legacy adapter.
            var handler = new CapturingHandler(HttpStatusCode.OK, $"{{\"download\":\"{DirectUrl}\"}}");
            using var client = new HttpClient(handler);
            var service = new DebridService(Substitute.For<ILogger>(), client);

            var provider = service.Registry.Get(DebridProvider.RealDebrid);

            Assert.IsType<RealDebridProvider>(provider);
        }

        // --- Helpers ---

        private static void AssertUnrestrictRequest(CapturingHandler handler)
        {
            Assert.NotNull(handler.Request);
            Assert.Equal(HttpMethod.Post, handler.Request.Method);
            Assert.Equal(UnrestrictUrl, handler.Request.RequestUri.ToString());
            Assert.Equal("Bearer " + ApiKey, handler.Request.Headers.Authorization.ToString());
            Assert.Equal("application/x-www-form-urlencoded", handler.Request.Content.Headers.ContentType.MediaType);
            // FormUrlEncodedContent URL-encodes the value: pin the exact on-wire body.
            Assert.Equal("link=" + Uri.EscapeDataString(SourceLink), handler.Body);
        }
    }

    // Fake HTTP handler: records the outgoing request (method, URL, headers, body) and
    // replies with a configurable status code, JSON body and optional Retry-After header.
    // Never touches the network.
    internal sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _json;
        private readonly TimeSpan? _retryAfter;

        public HttpRequestMessage Request { get; private set; }
        public string Body { get; private set; }

        public CapturingHandler(HttpStatusCode status, string json, TimeSpan? retryAfter = null)
        {
            _status = status;
            _json = json;
            _retryAfter = retryAfter;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var response = new HttpResponseMessage(_status) { Content = new StringContent(_json) };
            if (_retryAfter.HasValue)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(_retryAfter.Value);
            }

            return response;
        }
    }

    // Handler that always fails at the transport layer, to simulate a network error.
    internal sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("network down"));
        }
    }
}
