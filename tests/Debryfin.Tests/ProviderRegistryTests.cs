using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Debryfin.Configuration;
using Debryfin.Providers;
using Xunit;

namespace Debryfin.Tests
{
    public class ProviderRegistryTests
    {
        [Fact]
        public void RegisterAndGet_ReturnsTheRegisteredProvider()
        {
            var registry = new ProviderRegistry();
            var rd = new StubProvider(DebridProvider.RealDebrid);

            registry.Register(rd);

            Assert.Same(rd, registry.Get(DebridProvider.RealDebrid));
        }

        [Fact]
        public void Get_UnregisteredProvider_ReturnsNull()
        {
            var registry = new ProviderRegistry();
            registry.Register(new StubProvider(DebridProvider.RealDebrid));

            Assert.Null(registry.Get(DebridProvider.TorBox));
            Assert.Null(registry.Get(DebridProvider.Premiumize));
        }

        [Fact]
        public void Register_NullProvider_ThrowsArgumentNullException()
        {
            var registry = new ProviderRegistry();

            Assert.Throws<ArgumentNullException>(() => registry.Register(null));
        }

        [Fact]
        public void Register_DuplicateId_ReplacesProvider()
        {
            var registry = new ProviderRegistry();
            var first = new StubProvider(DebridProvider.AllDebrid);
            var second = new StubProvider(DebridProvider.AllDebrid);

            registry.Register(first);
            registry.Register(second);

            Assert.Same(second, registry.Get(DebridProvider.AllDebrid));
            Assert.Single(registry.All);
        }

        [Fact]
        public void All_ReturnsEveryRegisteredProvider()
        {
            var registry = new ProviderRegistry();
            registry.Register(new StubProvider(DebridProvider.RealDebrid));
            registry.Register(new StubProvider(DebridProvider.AllDebrid));

            var ids = registry.All.Select(p => p.Id).OrderBy(i => i).ToList();

            Assert.Equal(new[] { DebridProvider.RealDebrid, DebridProvider.AllDebrid }, ids);
        }

        private sealed class StubProvider : IDebridProvider
        {
            public StubProvider(DebridProvider id)
            {
                Id = id;
            }

            public DebridProvider Id { get; }

            public string DisplayName => Id.ToString();

            public TimeSpan DefaultLinkTtl => TimeSpan.FromHours(72);

            public Task<UnrestrictResult> UnrestrictAsync(string sourceLink, string apiKey, CancellationToken ct)
                => Task.FromResult(UnrestrictResult.Ok("https://example.invalid/direct"));

            public Task<AccountInfo> GetUserInfoAsync(string apiKey, CancellationToken ct)
                => Task.FromResult(new AccountInfo());

            public Task<ValidationResult> ValidateApiKeyAsync(string apiKey, CancellationToken ct)
                => Task.FromResult(new ValidationResult { IsValid = true });
        }
    }
}
