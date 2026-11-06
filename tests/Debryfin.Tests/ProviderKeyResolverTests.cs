using Debryfin.Configuration;
using Xunit;

namespace Debryfin.Tests
{
    public class ProviderKeyResolverTests
    {
        [Fact]
        public void Get_NullConfig_ReturnsNull()
        {
            var key = ProviderKeyResolver.Get(null, DebridProvider.RealDebrid);
            Assert.Null(key);
        }

        [Fact]
        public void Get_SpecificKeyPresent_ReturnsSpecificKey_ForAllProviders()
        {
            var config = new DebryfinOptions
            {
                ApiKey = "global-fallback-key",
                RealDebridApiKey = "rd-specific-key",
                AllDebridApiKey = "ad-specific-key",
                TorBoxApiKey = "tb-specific-key",
                PremiumizeApiKey = "pm-specific-key"
            };

            Assert.Equal("rd-specific-key", ProviderKeyResolver.Get(config, DebridProvider.RealDebrid));
            Assert.Equal("ad-specific-key", ProviderKeyResolver.Get(config, DebridProvider.AllDebrid));
            Assert.Equal("tb-specific-key", ProviderKeyResolver.Get(config, DebridProvider.TorBox));
            Assert.Equal("pm-specific-key", ProviderKeyResolver.Get(config, DebridProvider.Premiumize));
        }

        [Fact]
        public void Get_SpecificKeyNullOrEmpty_FallsBackToGlobalApiKey_ForAllProviders()
        {
            var config = new DebryfinOptions
            {
                ApiKey = "global-fallback-key",
                RealDebridApiKey = null,
                AllDebridApiKey = "",
                TorBoxApiKey = null,
                PremiumizeApiKey = ""
            };

            Assert.Equal("global-fallback-key", ProviderKeyResolver.Get(config, DebridProvider.RealDebrid));
            Assert.Equal("global-fallback-key", ProviderKeyResolver.Get(config, DebridProvider.AllDebrid));
            Assert.Equal("global-fallback-key", ProviderKeyResolver.Get(config, DebridProvider.TorBox));
            Assert.Equal("global-fallback-key", ProviderKeyResolver.Get(config, DebridProvider.Premiumize));
        }

        [Fact]
        public void Get_BothSpecificAndGlobalNullOrEmpty_ReturnsFallbackValue()
        {
            var configNull = new DebryfinOptions
            {
                ApiKey = null,
                RealDebridApiKey = null
            };
            Assert.Null(ProviderKeyResolver.Get(configNull, DebridProvider.RealDebrid));

            var configEmpty = new DebryfinOptions
            {
                ApiKey = "",
                RealDebridApiKey = ""
            };
            Assert.Equal("", ProviderKeyResolver.Get(configEmpty, DebridProvider.RealDebrid));
        }

        [Fact]
        public void Get_InvalidProviderEnum_FallsBackToGlobalApiKey()
        {
            var config = new DebryfinOptions
            {
                ApiKey = "global-key"
            };

            var result = ProviderKeyResolver.Get(config, (DebridProvider)999);
            Assert.Equal("global-key", result);
        }
    }
}
