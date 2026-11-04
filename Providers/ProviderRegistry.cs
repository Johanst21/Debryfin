namespace Debryfin.Providers
{
    using System;
    using System.Collections.Generic;
    using Debryfin.Configuration;

    public sealed class ProviderRegistry
    {
        private readonly Dictionary<DebridProvider, IDebridProvider> _providers = new Dictionary<DebridProvider, IDebridProvider>();

        public void Register(IDebridProvider provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            _providers[provider.Id] = provider;
        }

        public IDebridProvider Get(DebridProvider id)
        {
            _providers.TryGetValue(id, out var p);
            return p;
        }

        public IReadOnlyCollection<IDebridProvider> All => _providers.Values;
    }
}
