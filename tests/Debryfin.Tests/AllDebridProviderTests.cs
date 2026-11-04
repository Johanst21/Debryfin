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
    // P1-02 tests. The first block characterizes the legacy DebridService.CallAllDebrid
    // (method, URL, query) and the second drives the native IDebridProvider through the same
    // assertion helper, proving both send an identical request. No real network is used:
    // every request goes through the CapturingHandler fake (shared with the Real-Debrid tests).
    public class AllDebridProviderTests
    {
        private const string UnrestrictBaseUrl = "https://api.alldebrid.com/v4/link/unlock";
        private const string UserBaseUrl = "https://api.alldebrid.com/v4/user";
        private const string SourceLink = "https://1fichier.com/?abc123";
        private const string ApiKey = "ad-test-token-xyz";
        private const string DirectUrl = "https://alldebrid.com/dl/123";

        // --- Characterization of the legacy behavior (passes before the migration) ---

        [Fact]
        public async Task LegacyCallAllDebrid_SendsIdenticalGetRequest_ReturnsDirectUrl()
        {
            // Drives the legacy method with an injected fake HttpClient and pins the exact
            // request it sends today; the native provider must send the same one.
            var handler = new CapturingHandler(HttpStatusCode.OK, SuccessJson());
            using var client = new HttpClient(handler);
            var service = new DebridService(Substitute.For<ILogger>(), client);

            var result = await service.CallAllDebrid(SourceLink, ApiKey, CancellationToken.None);

            AssertUnrestrictRequest(handler);
            Assert.Equal(DirectUrl, result);
        }

        [Fact]
        public async Task LegacyCallAllDebrid_HttpError_ReturnsNull()
        {
            var handler = new CapturingHandler(HttpStatusCode.InternalServerError, "{}");
            using var client = new HttpClient(handler);
            var service = new DebridService(Substitute.For<ILogger>(), client);

            var result = await service.CallAllDebrid(SourceLink, ApiKey, CancellationToken.None);

            Assert.Null(result);
        }

        [Fact]
        public async Task LegacyCallAllDebrid_SuccessWithoutDataLink_ReturnsNull()
        {
            var handler = new CapturingHandler(HttpStatusCode.OK, "{\"status\":\"success\",\"data\":{}}");
            using var client = new HttpClient(handler);
            var service = new DebridService(Substitute.For<ILogger>(), client);

            var result = await service.CallAllDebrid(SourceLink, ApiKey, CancellationToken.None);

            Assert.Null(result);
        }

        // --- Native provider: the wire request must be identical to the legacy one ---

        [Fact]
        public async Task Unrestrict_SendsIdenticalRequest_ReturnsDirectUrl()
        {
            var handler = new CapturingHandler(HttpStatusCode.OK, SuccessJson());
            using var client = new HttpClient(handler);
            var provider = new AllDebridProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            AssertUnrestrictRequest(handler);
            Assert.Equal(ErrorKind.None, result.Error);
            Assert.Equal(DirectUrl, result.Url);
        }

        // --- Error classification: HTTP != 200 -> Transient ---

        [Theory]
        [InlineData(HttpStatusCode.BadRequest)]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.Forbidden)]
        [InlineData(HttpStatusCode.NotFound)]
        [InlineData(HttpStatusCode.TooManyRequests)]
        [InlineData(HttpStatusCode.InternalServerError)]
        public async Task Unrestrict_Non200_ReturnsTransient(HttpStatusCode status)
        {
            var handler = new CapturingHandler(status, "{}");
            using var client = new HttpClient(handler);
            var provider = new AllDebridProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Transient, result.Error);
            Assert.Null(result.Url);
        }

        // --- Error classification: JSON status "error" -> code mapping ---

        [Theory]
        [InlineData("AUTH_BAD_APIKEY", ErrorKind.AuthFailed)]
        [InlineData("AUTH_USER_BANNED", ErrorKind.AuthFailed)]
        [InlineData("LINK_HOST_NOT_SUPPORTED", ErrorKind.Unsupported)]
        [InlineData("LINK_DOWN", ErrorKind.NotFound)]
        [InlineData("LINK_TOO_MANY_REQUESTS", ErrorKind.Transient)]
        [InlineData("MAINTENANCE", ErrorKind.Transient)]
        public async Task Unrestrict_ErrorStatus_MapsCodeToErrorKind(string code, ErrorKind expected)
        {
            var handler = new CapturingHandler(HttpStatusCode.OK, ErrorJson(code));
            using var client = new HttpClient(handler);
            var provider = new AllDebridProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(expected, result.Error);
            Assert.Null(result.Url);
            Assert.DoesNotContain(ApiKey, result.Message);
        }

        [Fact]
        public async Task Unrestrict_ErrorStatusWithoutCode_ReturnsTransient()
        {
            var handler = new CapturingHandler(HttpStatusCode.OK, "{\"status\":\"error\",\"error\":{}}");
            using var client = new HttpClient(handler);
            var provider = new AllDebridProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Transient, result.Error);
        }

        // --- Error classification: success without data.link -> Pending (delayed) ---

        [Theory]
        [InlineData("{\"status\":\"success\"}")]
        [InlineData("{\"status\":\"success\",\"data\":{}}")]
        [InlineData("{\"status\":\"success\",\"data\":{\"link\":\"\"}}")]
        public async Task Unrestrict_SuccessWithoutDataLink_ReturnsPending(string json)
        {
            var handler = new CapturingHandler(HttpStatusCode.OK, json);
            using var client = new HttpClient(handler);
            var provider = new AllDebridProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Pending, result.Error);
            Assert.Null(result.Url);
        }

        [Fact]
        public async Task Unrestrict_NetworkError_ReturnsTransientWithoutLeakingSecrets()
        {
            using var client = new HttpClient(new ThrowingHandler());
            var provider = new AllDebridProvider(client);

            var result = await provider.UnrestrictAsync(SourceLink, ApiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Transient, result.Error);
            Assert.DoesNotContain(ApiKey, result.Message);
        }

        [Fact]
        public async Task Unrestrict_CancelledToken_RethrowsCancellation()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var provider = new AllDebridProvider(new HttpClient(new CapturingHandler(HttpStatusCode.OK, "{}")));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                provider.UnrestrictAsync(SourceLink, ApiKey, cts.Token));
        }

        // --- Account info / validation ---

        [Fact]
        public async Task GetUserInfo_PremiumAccount_ReturnsUsernameAndPremium()
        {
            var handler = new CapturingHandler(HttpStatusCode.OK,
                "{\"status\":\"success\",\"data\":{\"user\":{\"username\":\"aduser\",\"isPremium\":true}}}");
            using var client = new HttpClient(handler);
            var provider = new AllDebridProvider(client);

            var info = await provider.GetUserInfoAsync(ApiKey, CancellationToken.None);

            var request = handler.Request;
            Assert.NotNull(request);
            Assert.Equal(HttpMethod.Get, request!.Method);
            Assert.Equal(
                $"{UserBaseUrl}?agent=Debryfin&apikey={ApiKey}",
                request!.RequestUri!.ToString());
            Assert.Equal("aduser", info.Username);
            Assert.True(info.IsPremium);
        }

        [Fact]
        public async Task GetUserInfo_NonPremiumAccount_ReturnsIsPremiumFalse()
        {
            var handler = new CapturingHandler(HttpStatusCode.OK,
                "{\"status\":\"success\",\"data\":{\"user\":{\"username\":\"aduser\",\"isPremium\":false}}}");
            using var client = new HttpClient(handler);
            var provider = new AllDebridProvider(client);

            var info = await provider.GetUserInfoAsync(ApiKey, CancellationToken.None);

            Assert.Equal("aduser", info.Username);
            Assert.False(info.IsPremium);
        }

        [Fact]
        public async Task ValidateApiKey_ValidUser_ReturnsValid()
        {
            var handler = new CapturingHandler(HttpStatusCode.OK,
                "{\"status\":\"success\",\"data\":{\"user\":{\"username\":\"aduser\",\"isPremium\":true}}}");
            using var client = new HttpClient(handler);
            var provider = new AllDebridProvider(client);

            var validation = await provider.ValidateApiKeyAsync(ApiKey, CancellationToken.None);

            Assert.True(validation.IsValid);
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized, "{}")]
        [InlineData(HttpStatusCode.OK, "{\"status\":\"error\",\"error\":{\"code\":\"AUTH_BAD_APIKEY\"}}")]
        public async Task ValidateApiKey_InvalidKey_ReturnsNotValid(HttpStatusCode status, string json)
        {
            var handler = new CapturingHandler(status, json);
            using var client = new HttpClient(handler);
            var provider = new AllDebridProvider(client);

            var validation = await provider.ValidateApiKeyAsync(ApiKey, CancellationToken.None);

            Assert.False(validation.IsValid);
            Assert.DoesNotContain(ApiKey, validation.Message);
        }

        // --- Identity / wiring ---

        [Fact]
        public void Properties_ExposeIdentityAnd72hTtl()
        {
            var provider = new AllDebridProvider(new HttpClient(new CapturingHandler(HttpStatusCode.OK, "{}")));

            Assert.Equal(DebridProvider.AllDebrid, provider.Id);
            Assert.Equal("AllDebrid", provider.DisplayName);
            Assert.Equal(TimeSpan.FromHours(72), provider.DefaultLinkTtl);
        }

        [Fact]
        public void DebridService_RegistersNativeAllDebridProvider()
        {
            // Confirms the composition point now wires the native provider, not the legacy adapter.
            var handler = new CapturingHandler(HttpStatusCode.OK, SuccessJson());
            using var client = new HttpClient(handler);
            var service = new DebridService(Substitute.For<ILogger>(), client);

            var provider = service.Registry.Get(DebridProvider.AllDebrid);

            Assert.IsType<AllDebridProvider>(provider);
        }

        // --- Helpers ---

        // Shared by the legacy characterization and the native provider tests: the on-wire
        // request must be exactly this GET with agent/apikey/link in the query string.
        private static void AssertUnrestrictRequest(CapturingHandler handler)
        {
            var request = handler.Request;
            Assert.NotNull(request);
            Assert.Equal(HttpMethod.Get, request!.Method);
            Assert.Equal(
                $"{UnrestrictBaseUrl}?agent=Debryfin&apikey={ApiKey}&link={Uri.EscapeDataString(SourceLink)}",
                request!.RequestUri!.ToString());
            Assert.Null(request!.Content);
        }

        private static string SuccessJson()
        {
            return $"{{\"status\":\"success\",\"data\":{{\"link\":\"{DirectUrl}\",\"filename\":\"movie.mkv\"}}}}";
        }

        private static string ErrorJson(string code)
        {
            return $"{{\"status\":\"error\",\"error\":{{\"code\":\"{code}\",\"message\":\"error\"}}}}";
        }
    }
}
