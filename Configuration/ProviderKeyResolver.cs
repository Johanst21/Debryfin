namespace Debryfin.Configuration
{
    public static class ProviderKeyResolver
    {
        public static string Get(DebryfinOptions config, DebridProvider provider)
        {
            if (config == null)
            {
                return null;
            }

            string specificKey = provider switch
            {
                DebridProvider.RealDebrid => config.RealDebridApiKey,
                DebridProvider.AllDebrid => config.AllDebridApiKey,
                DebridProvider.TorBox => config.TorBoxApiKey,
                DebridProvider.Premiumize => config.PremiumizeApiKey,
                _ => null
            };

            if (!string.IsNullOrEmpty(specificKey))
            {
                return specificKey;
            }

            return config.ApiKey;
        }
    }
}
