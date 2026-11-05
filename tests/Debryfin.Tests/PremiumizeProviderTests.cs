using System;
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
    // P1-06: Premiumize resolution (POST /api/transfer/directdl) simulated offline with
    // the shared scripted HttpMessageHandler. The assertions pin the wire request (method,
    // URL, Bearer header, form body) and the parsing of "content[].link". No real network.
    public class PremiumizeProviderTests
    {
        private const string DirectDownloadUrl = "https://www.premiumize.me/api/transfer/directdl";
        private const string AccountInfoUrl = "https://www.premiumize.me/api/account/info";
        private const string SourceLink = "https://1fichier.com/?abc123";
        private const string ApiKey = "pz-secret-key-001";
        private const string DirectUrl = "https://premiumize.dl/d/file.mkv";

        private const string SuccessSingleJson =
            "{\"status\":\"success\",\"content\":[{\"path\":\"/movies/file.mkv\",\"size\":123,\"link\":\"" + DirectUrl + "\"}]}";

        // --- Success flow ---

        [Fact]
        public async Task Unrestrict_SingleContentElement_ReturnsItsLink()
        {
            var handler = new ScriptedHandler((index, _) => Json(HttpStatusCode.OK, SuccessSingleJson));
            using var client = new HttpClient(handler);
            var provider = new PremiumizeProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.None, result.Error);
            Assert.Equal(DirectUrl, result.Url);
            AssertRequest(handler, 0);
        }

        [Fact]
        public async Task Unrestrict_DeprecatedFieldsPresent_IgnoresLocationAndStreamLink()
        {
            // The deprecated "location"/"stream_link" fields carry decoy values: the
            // provider must read the selected element's "link" property only.
            var json = "{\"status\":\"success\",\"content\":[{\"path\":\"/movies/file.mkv\",\"size\":123," +
                       "\"link\":\"" + DirectUrl + "\",\"location\":\"https://premiumize.me/file/DEPRECATED\"," +
                       "\"stream_link\":\"https://premiumize.me/stream/DEPRECATED\"}]}";
            var handler = new ScriptedHandler((index, _) => Json(HttpStatusCode.OK, json));
            using var client = new HttpClient(handler);
            var provider = new PremiumizeProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.None, result.Error);
            Assert.Equal(DirectUrl, result.Url);
        }

        [Fact]
        public async Task Unrestrict_MultipleContentElements_ReturnsLargestSize()
        {
            var json = "{\"status\":\"success\",\"content\":[" +
                       "{\"path\":\"/a/sample.txt\",\"size\":10,\"link\":\"https://premiumize.dl/d/sample\"}," +
                       "{\"path\":\"/a/file.mkv\",\"size\":5000,\"link\":\"" + DirectUrl + "\"}," +
                       "{\"path\":\"/a/thumbnail.jpg\",\"size\":5,\"link\":\"https://premiumize.dl/d/thumb\"}]}";
            var handler = new ScriptedHandler((index, _) => Json(HttpStatusCode.OK, json));
            using var client = new HttpClient(handler);
            var provider = new PremiumizeProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.None, result.Error);
            Assert.Equal(DirectUrl, result.Url);
        }

        [Fact]
        public async Task Unrestrict_ContentElementsWithoutSize_FallsBackToFirstElement()
        {
            var json = "{\"status\":\"success\",\"content\":[" +
                       "{\"path\":\"/a/first.mkv\",\"link\":\"https://premiumize.dl/d/first\"}," +
                       "{\"path\":\"/a/second.mkv\",\"link\":\"https://premiumize.dl/d/second\"}]}";
            var handler = new ScriptedHandler((index, _) => Json(HttpStatusCode.OK, json));
            using var client = new HttpClient(handler);
            var provider = new PremiumizeProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.None, result.Error);
            Assert.Equal("https://premiumize.dl/d/first", result.Url);
        }

        [Fact]
        public async Task Unrestrict_SuccessWithoutContentOrEmptyLinks_ReturnsPermanent()
        {
            var handler = new ScriptedHandler((index, _) =>
                Json(HttpStatusCode.OK, "{\"status\":\"success\",\"content\":[]}"));
            using var client = new HttpClient(handler);
            var provider = new PremiumizeProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Permanent, result.Error);
            Assert.Null(result.Url);
        }

        // --- Error classification (HTTP 200 with status "error") ---

        [Theory]
        [InlineData("authentication_failed", ErrorKind.AuthFailed)]
        [InlineData("permission_denied", ErrorKind.AuthFailed)]
        [InlineData("service_limit_reached", ErrorKind.RateLimited)]
        [InlineData("account_limit_reached", ErrorKind.RateLimited)]
        [InlineData("rate_limit_reached", ErrorKind.RateLimited)]
        [InlineData("link_generation_failed", ErrorKind.Transient)]
        [InlineData("transient_error", ErrorKind.Transient)]
        [InlineData("unknown_error", ErrorKind.Transient)]
        [InlineData("service_down", ErrorKind.Transient)]
        [InlineData("service_unsupported", ErrorKind.Unsupported)]
        [InlineData("not_found", ErrorKind.Permanent)]
        [InlineData("invalid_request", ErrorKind.Permanent)]
        [InlineData("permanent_error", ErrorKind.Permanent)]
        public async Task Unrestrict_KnownErrorCodes_MapToExpectedErrorKind(string code, ErrorKind expected)
        {
            var handler = new ScriptedHandler((index, _) =>
                Json(HttpStatusCode.OK, $"{{\"status\":\"error\",\"message\":\"something went wrong\",\"code\":\"{code}\"}}"));
            using var client = new HttpClient(handler);
            var provider = new PremiumizeProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(expected, result.Error);
            Assert.Null(result.Url);
            Assert.Equal($"Premiumize error: {code} (something went wrong)", result.Message);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("some_brand_new_code")]
        public async Task Unrestrict_UnknownOrMissingErrorCode_ReturnsTransient(string? code)
        {
            var json = code == null
                ? "{\"status\":\"error\",\"message\":\"boom\"}"
                : $"{{\"status\":\"error\",\"message\":\"boom\",\"code\":\"{code}\"}}";
            var handler = new ScriptedHandler((index, _) => Json(HttpStatusCode.OK, json));
            using var client = new HttpClient(handler);
            var provider = new PremiumizeProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Transient, result.Error);
        }

        // --- HTTP-level errors ---

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.Forbidden)]
        public async Task Unrestrict_401Or403_ReturnsAuthFailed(HttpStatusCode status)
        {
            var handler = new ScriptedHandler((index, _) => Json(status, "{}"));
            using var client = new HttpClient(handler);
            var provider = new PremiumizeProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.AuthFailed, result.Error);
            Assert.Null(result.Url);
        }

        [Fact]
        public async Task Unrestrict_429_ReturnsRateLimitedWithRetryAfter()
        {
            var handler = new ScriptedHandler((index, _) =>
                Json(HttpStatusCode.TooManyRequests, "{}", TimeSpan.FromSeconds(30)));
            using var client = new HttpClient(handler);
            var provider = new PremiumizeProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.RateLimited, result.Error);
            Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
        }

        [Fact]
        public async Task Unrestrict_5xx_ReturnsTransient()
        {
            var handler = new ScriptedHandler((index, _) =>
                Json(HttpStatusCode.InternalServerError, "{\"status\":\"error\",\"message\":\"\",\"code\":\"unknown_error\"}"));
            using var client = new HttpClient(handler);
            var provider = new PremiumizeProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Transient, result.Error);
        }

        [Fact]
        public async Task Unrestrict_NetworkError_ReturnsTransientWithoutLeakingSecrets()
        {
            using var client = new HttpClient(new ThrowingHandler());
            var provider = new PremiumizeProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Transient, result.Error);
            Assert.DoesNotContain(ApiKey, result.Message);
        }

        [Fact]
        public async Task Unrestrict_CancelledToken_RethrowsCancellation()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var provider = new PremiumizeProvider(new HttpClient(new ScriptedHandler((index, _) => Json(HttpStatusCode.OK, "{}"))));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                provider.UnrestrictAsync(SourceLink, ApiKey, cts.Token));
        }

        // --- Secrets ---

        [Fact]
        public async Task Unrestrict_RequestUrl_DoesNotContainCredentials()
        {
            // The key travels in the Bearer header only; the directdl URL is fixed and
            // must never gain an apikey=/token= query parameter.
            var handler = new ScriptedHandler((index, _) => Json(HttpStatusCode.OK, SuccessSingleJson));
            using var client = new HttpClient(handler);
            var provider = new PremiumizeProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);
            Assert.Equal(ErrorKind.None, result.Error);

            var url = handler.Requests[0].RequestUri!.ToString();
            var lowered = url.ToLowerInvariant();
            Assert.Equal(DirectDownloadUrl, url);
            Assert.DoesNotContain("apikey=", lowered);
            Assert.DoesNotContain("token=", lowered);
        }

        [Fact]
        public async Task Unrestrict_ProviderErrorEchoingTheKey_LeadsToRedactedMessage()
        {
            // If the API ever echoes credentials in its error text, the provider message
            // that reaches the log must have the key redacted.
            var json = "{\"status\":\"error\",\"message\":\"bad key apikey=" + ApiKey +
                       "\",\"code\":\"authentication_failed\"}";
            var handler = new ScriptedHandler((index, _) => Json(HttpStatusCode.OK, json));
            using var client = new HttpClient(handler);
            var provider = new PremiumizeProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.AuthFailed, result.Error);
            Assert.DoesNotContain(ApiKey, result.Message);
            Assert.Contains("apikey=***", result.Message);
        }

        // --- Account info / validation ---

        [Fact]
        public async Task GetUserInfo_PremiumAccount_ReturnsCustomerIdAndExpiration()
        {
            var handler = new ScriptedHandler((index, _) =>
                Json(HttpStatusCode.OK,
                    "{\"status\":\"success\",\"content\":{\"customer_id\":12345,\"premium_until\":\"2026-12-31T00:00:00+00:00\",\"limit_used\":1}}"));
            using var client = new HttpClient(handler);
            var provider = new PremiumizeProvider(client);

            var info = await provider.GetUserInfoAsync(ApiKey, CancellationToken.None);

            var request = handler.Requests[0];
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(AccountInfoUrl, request.RequestUri!.ToString());
            Assert.Equal("Bearer " + ApiKey, request.Headers.Authorization!.ToString());
            Assert.Equal("12345", info.Username);
            Assert.True(info.IsPremium);
            Assert.Equal(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc), info.ExpirationUtc);
        }

        [Fact]
        public async Task GetUserInfo_FreeAccount_ReturnsNotPremiumWithoutExpiration()
        {
            var handler = new ScriptedHandler((index, _) =>
                Json(HttpStatusCode.OK,
                    "{\"status\":\"success\",\"content\":{\"customer_id\":678,\"premium_until\":null,\"limit_used\":0}}"));
            using var client = new HttpClient(handler);
            var provider = new PremiumizeProvider(client);

            var info = await provider.GetUserInfoAsync(ApiKey, CancellationToken.None);

            Assert.Equal("678", info.Username);
            Assert.False(info.IsPremium);
            Assert.Null(info.ExpirationUtc);
        }

        [Fact]
        public async Task ValidateApiKey_ValidUser_ReturnsValid()
        {
            var handler = new ScriptedHandler((index, _) =>
                Json(HttpStatusCode.OK, "{\"status\":\"success\",\"content\":{\"customer_id\":1}}"));
            using var client = new HttpClient(handler);
            var provider = new PremiumizeProvider(client);

            var validation = await provider.ValidateApiKeyAsync(ApiKey, CancellationToken.None);

            Assert.True(validation.IsValid);
        }

        [Fact]
        public async Task ValidateApiKey_ErrorStatus_ReturnsNotValidWithoutLeakingSecrets()
        {
            var handler = new ScriptedHandler((index, _) => Json(HttpStatusCode.Unauthorized, "{}"));
            using var client = new HttpClient(handler);
            var provider = new PremiumizeProvider(client);

            var validation = await provider.ValidateApiKeyAsync(ApiKey, CancellationToken.None);

            Assert.False(validation.IsValid);
            Assert.DoesNotContain(ApiKey, validation.Message);
        }

        // --- Identity / wiring ---

        [Fact]
        public void Properties_ExposeIdentityAnd12hTtl()
        {
            var provider = new PremiumizeProvider(new HttpClient(new ScriptedHandler((index, _) => Json(HttpStatusCode.OK, "{}"))));

            Assert.Equal(DebridProvider.Premiumize, provider.Id);
            Assert.Equal("Premiumize", provider.DisplayName);
            Assert.Equal(TimeSpan.FromHours(12), provider.DefaultLinkTtl);
        }

        [Fact]
        public void DebridService_RegistersNativePremiumizeProvider()
        {
            // Confirms the composition point wires the native provider, not the removed legacy stub.
            var handler = new ScriptedHandler((index, _) => Json(HttpStatusCode.OK, "{}"));
            using var client = new HttpClient(handler);
            var service = new DebridService(Substitute.For<ILogger>(), client);

            var provider = service.Registry.Get(DebridProvider.Premiumize);

            Assert.IsType<PremiumizeProvider>(provider);
        }

        // --- Helpers ---

        private static void AssertRequest(ScriptedHandler handler, int index)
        {
            var request = handler.Requests[index];
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(DirectDownloadUrl, request.RequestUri!.ToString());
            Assert.Equal("Bearer " + ApiKey, request.Headers.Authorization!.ToString());
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            // FormUrlEncodedContent URL-encodes the value: pin the exact on-wire body.
            Assert.Equal("src=" + Uri.EscapeDataString(SourceLink), handler.Bodies[index]);
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
}
