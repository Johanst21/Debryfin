using System;
using System.Threading;
using System.Threading.Tasks;
using Debryfin.Configuration;
using Debryfin.Providers;
using MediaBrowser.Model.Logging;
using NSubstitute;
using Xunit;

namespace Debryfin.Tests
{
    public class LegacyProviderAdapterTests
    {
        [Fact]
        public async Task UnrestrictAsync_LegacyReturnsUrl_ReturnsOkWithUrl()
        {
            const string directUrl = "https://real-debrid.com/d/direct123";
            var adapter = new LegacyProviderAdapter(
                CreateService(),
                DebridProvider.RealDebrid,
                (link, key, ct) => Task.FromResult(directUrl));

            var result = await adapter.UnrestrictAsync("https://1fichier.com/?abc", "some-key", CancellationToken.None);

            Assert.Equal(ErrorKind.None, result.Error);
            Assert.Equal(directUrl, result.Url);
        }

        [Fact]
        public async Task UnrestrictAsync_LegacyReturnsNull_ReturnsTransient()
        {
            var adapter = new LegacyProviderAdapter(
                CreateService(),
                DebridProvider.RealDebrid,
                (link, key, ct) => Task.FromResult<string?>(null));

            var result = await adapter.UnrestrictAsync("https://1fichier.com/?abc", "some-key", CancellationToken.None);

            Assert.Equal(ErrorKind.Transient, result.Error);
            Assert.Null(result.Url);
        }

        [Fact]
        public async Task UnrestrictAsync_LegacyThrows_ReturnsTransientWithoutLeakingSecrets()
        {
            const string apiKey = "secret-key-abc123";
            // La funcion inyectada simula una excepcion que incluye la API key.
            var adapter = new LegacyProviderAdapter(
                CreateService(),
                DebridProvider.AllDebrid,
                (link, key, ct) => throw new InvalidOperationException(key));

            var result = await adapter.UnrestrictAsync("https://1fichier.com/?abc", apiKey, CancellationToken.None);

            Assert.Equal(ErrorKind.Transient, result.Error);
            Assert.Null(result.Url);
            Assert.DoesNotContain(apiKey, result.Message);
        }

        [Fact]
        public async Task UnrestrictAsync_RealServiceCancelledToken_ReturnsTransient()
        {
            // Sin seam: el constructor publico enruta a CallRealDebrid real; un token ya
            // cancelado hace que SendAsync lance sin realizar ninguna peticion de red.
            var adapter = new LegacyProviderAdapter(CreateService(), DebridProvider.RealDebrid);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var result = await adapter.UnrestrictAsync("https://1fichier.com/?abc", "some-key", cts.Token);

            Assert.Equal(ErrorKind.Transient, result.Error);
        }

        [Fact]
        public async Task GetUserInfoAsync_ReturnsDummyAccountInfo()
        {
            var adapter = new LegacyProviderAdapter(CreateService(), DebridProvider.RealDebrid);

            var info = await adapter.GetUserInfoAsync("some-key", CancellationToken.None);

            Assert.NotNull(info);
        }

        [Fact]
        public async Task ValidateApiKeyAsync_ReturnsNotValidUntilRealImplementation()
        {
            var adapter = new LegacyProviderAdapter(CreateService(), DebridProvider.AllDebrid);

            var validation = await adapter.ValidateApiKeyAsync("some-key", CancellationToken.None);

            Assert.NotNull(validation);
            Assert.False(validation.IsValid);
        }

        [Fact]
        public void Properties_ExposeProviderIdentityAnd72hTtl()
        {
            var rd = new LegacyProviderAdapter(CreateService(), DebridProvider.RealDebrid);
            var ad = new LegacyProviderAdapter(CreateService(), DebridProvider.AllDebrid);

            Assert.Equal(DebridProvider.RealDebrid, rd.Id);
            Assert.Equal(TimeSpan.FromHours(72), rd.DefaultLinkTtl);
            Assert.Equal(DebridProvider.AllDebrid, ad.Id);
            Assert.Equal(TimeSpan.FromHours(72), ad.DefaultLinkTtl);
        }

        private static DebridService CreateService()
        {
            var logger = Substitute.For<ILogger>();
            return new DebridService(logger);
        }
    }
}
