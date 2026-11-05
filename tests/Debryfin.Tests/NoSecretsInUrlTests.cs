using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Debryfin.Providers;
using Xunit;

namespace Debryfin.Tests
{
    // P1-04: regression guard for "no credentials in the URL". Real-Debrid and AllDebrid
    // send the API key in the Authorization: Bearer header; if a future refactor moves
    // the key back into the request URI (apikey=... / token=...), these tests must fail.
    // The TorBox requestdl "token=" query parameter (P1-05) is the only sanctioned
    // exception; it is covered by the dedicated TorBox test below. No real network:
    // the shared CapturingHandler/ScriptedHandler/ThrowingHandler fakes record the
    // request(s) and reply offline.
    public class NoSecretsInUrlTests
    {
        private const string SourceLink = "https://1fichier.com/?abc123";
        private const string ApiKey = "secret-registry-key-000";
        private const string DirectUrl = "https://example.com/d/123";

        [Theory]
        [InlineData(typeof(RealDebridProvider))]
        [InlineData(typeof(AllDebridProvider))]
        public async Task Unrestrict_CapturedUrl_DoesNotContainCredentials(Type providerType)
        {
            var handler = new CapturingHandler(HttpStatusCode.OK, SuccessJson(providerType));
            using var client = new HttpClient(handler);
            var provider = CreateProvider(providerType, client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.None, result.Error);
            Assert.Equal(DirectUrl, result.Url);
            AssertUrlHasNoCredentials(handler);
            // The key must travel somewhere: it goes in the Bearer header, not the URL.
            AssertCredentialsInBearerHeader(handler);
        }

        [Theory]
        [InlineData(typeof(RealDebridProvider))]
        [InlineData(typeof(AllDebridProvider))]
        public async Task GetUserInfo_CapturedUrl_DoesNotContainCredentials(Type providerType)
        {
            var handler = new CapturingHandler(HttpStatusCode.OK, UserInfoJson(providerType));
            using var client = new HttpClient(handler);
            var provider = CreateProvider(providerType, client);

            var info = await provider.GetUserInfoAsync(ApiKey, CancellationToken.None);

            Assert.Equal("testuser", info.Username);
            Assert.True(info.IsPremium);
            AssertUrlHasNoCredentials(handler);
            AssertCredentialsInBearerHeader(handler);
        }

        [Theory]
        [InlineData(typeof(RealDebridProvider))]
        [InlineData(typeof(AllDebridProvider))]
        public async Task Unrestrict_NetworkError_ResultMessage_DoesNotContainTheKey(Type providerType)
        {
            // The error path is where credentials leak in practice (exception messages
            // with the request URL). The provider's failure message must be clean.
            using var client = new HttpClient(new ThrowingHandler());
            var provider = CreateProvider(providerType, client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Transient, result.Error);
            Assert.DoesNotContain(ApiKey, result.Message);
        }

        [Fact]
        public async Task TorBox_Unrestrict_OnlyRequestdlIsWhitelistedForTokenInUrl()
        {
            var createSuccess =
                "{\"success\":true,\"data\":{\"auth_id\":\"1\",\"hash\":\"abc\",\"webdownload_id\":\"wb-1\"},\"detail\":null,\"error\":null}";
            var requestdlSuccess = $"{{\"success\":true,\"data\":\"{DirectUrl}\",\"detail\":null,\"error\":null}}";
            var handler = new ScriptedHandler((index, _) =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(index == 0 ? createSuccess : requestdlSuccess)
                });
            using var client = new HttpClient(handler);
            var provider = new TorBoxProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.None, result.Error);
            Assert.Equal(2, handler.Requests.Count);

            // Step 1 (createwebdownload): the key travels in the Bearer header, not the URL.
            var create = handler.Requests[0];
            var createUrl = create.RequestUri!.ToString().ToLowerInvariant();
            Assert.DoesNotContain("apikey=", createUrl);
            Assert.DoesNotContain("token=", createUrl);
            Assert.Equal("Bearer", create.Headers.Authorization?.Scheme);
            Assert.Equal(ApiKey, create.Headers.Authorization?.Parameter);

            // Step 2 (requestdl): token=<key> is the only sanctioned credential-in-URL
            // exception (the API has no header auth for this endpoint); apikey= stays
            // forbidden in every request.
            var requestdl = handler.Requests[1];
            var requestdlUrl = requestdl.RequestUri!.ToString();
            Assert.Contains($"token={Uri.EscapeDataString(ApiKey)}", requestdlUrl);
            Assert.DoesNotContain("apikey=", requestdlUrl.ToLowerInvariant());
        }

        // --- Helpers ---

        private static void AssertUrlHasNoCredentials(CapturingHandler handler)
        {
            Assert.NotNull(handler.Request);
            var url = handler.Request.RequestUri!.ToString();

            // Case-insensitive check: a future "Apikey=" or "Token=" variant must fail too.
            var lowered = url.ToLowerInvariant();
            Assert.DoesNotContain("apikey=", lowered);
            Assert.DoesNotContain("token=", lowered);
        }

        private static void AssertCredentialsInBearerHeader(CapturingHandler handler)
        {
            Assert.NotNull(handler.Request);
            Assert.Equal("Bearer", handler.Request.Headers.Authorization?.Scheme);
            Assert.Equal(ApiKey, handler.Request.Headers.Authorization?.Parameter);
        }

        private static IDebridProvider CreateProvider(Type providerType, HttpClient client)
        {
            return providerType == typeof(RealDebridProvider)
                ? new RealDebridProvider(client)
                : new AllDebridProvider(client);
        }

        private static string SuccessJson(Type providerType)
        {
            return providerType == typeof(RealDebridProvider)
                ? $"{{\"download\":\"{DirectUrl}\"}}"
                : $"{{\"status\":\"success\",\"data\":{{\"link\":\"{DirectUrl}\",\"filename\":\"movie.mkv\"}}}}";
        }

        private static string UserInfoJson(Type providerType)
        {
            return providerType == typeof(RealDebridProvider)
                ? "{\"username\":\"testuser\",\"type\":\"premium\"}"
                : "{\"status\":\"success\",\"data\":{\"user\":{\"username\":\"testuser\",\"isPremium\":true}}}";
        }
    }
}
