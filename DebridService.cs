using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Debryfin.Configuration;
using Debryfin.Providers;
using Debryfin.Resolution;
using MediaBrowser.Model.Logging;

namespace Debryfin
{
    public class DebridService
    {
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private readonly ProviderRegistry _registry;
        private readonly ILinkResolver _linkResolver;
        private readonly Func<DebryfinOptions> _optionsAccessor;

        // Test seam (InternalsVisibleTo "Debryfin.Tests"): exposes the composition point so
        // tests can verify which provider is wired for each DebridProvider id.
        internal ProviderRegistry Registry => _registry;

        public DebridService(ILogger logger)
            : this(logger, CreateDefaultHttpClient())
        {
        }

        // Test seam (InternalsVisibleTo "Debryfin.Tests"): allows injecting a fake
        // HttpClient so provider requests can be characterized without any network.
        internal DebridService(ILogger logger, HttpClient httpClient)
            : this(
                logger,
                httpClient,
                new LinkResolver(() => DateTime.UtcNow),
                () => Plugin.Instance?.SafeGetOptions())
        {
        }

        internal DebridService(
            ILogger logger,
            HttpClient httpClient,
            ILinkResolver linkResolver,
            Func<DebryfinOptions> optionsAccessor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _linkResolver = linkResolver ?? throw new ArgumentNullException(nameof(linkResolver));
            _optionsAccessor = optionsAccessor ?? (() => Plugin.Instance?.SafeGetOptions());

            _registry = new ProviderRegistry();
            // P1-01/P1-02/P1-05/P1-06: Real-Debrid, AllDebrid, TorBox and Premiumize use
            // their native IDebridProvider.
            _registry.Register(new RealDebridProvider(_httpClient));
            _registry.Register(new AllDebridProvider(_httpClient));
            _registry.Register(new TorBoxProvider(_httpClient));
            _registry.Register(new PremiumizeProvider(_httpClient));
        }

        private static HttpClient CreateDefaultHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Debryfin-Emby-Plugin/1.0");
            return client;
        }

        public async Task<string> UnrestrictLinkAsync(string sourceLink, CancellationToken ct)
        {
            var config = _optionsAccessor();

            if (config == null)
            {
                _logger.Error("Debryfin: Configuration not found.");
                return null;
            }

            var candidates = new List<(IDebridProvider Provider, string Key)>();

            if (!string.IsNullOrEmpty(config.RealDebridApiKey))
            {
                var provider = _registry.Get(DebridProvider.RealDebrid);
                if (provider != null) candidates.Add((provider, config.RealDebridApiKey));
            }

            if (!string.IsNullOrEmpty(config.AllDebridApiKey))
            {
                var provider = _registry.Get(DebridProvider.AllDebrid);
                if (provider != null) candidates.Add((provider, config.AllDebridApiKey));
            }

            if (!string.IsNullOrEmpty(config.TorBoxApiKey))
            {
                var provider = _registry.Get(DebridProvider.TorBox);
                if (provider != null) candidates.Add((provider, config.TorBoxApiKey));
            }

            if (!string.IsNullOrEmpty(config.PremiumizeApiKey))
            {
                var provider = _registry.Get(DebridProvider.Premiumize);
                if (provider != null) candidates.Add((provider, config.PremiumizeApiKey));
            }

            if (candidates.Count == 0)
            {
                _logger.Warn("No provider API keys configured.");
                return null;
            }

            foreach (var (provider, key) in candidates)
            {
                try
                {
                    var result = await _linkResolver.ResolveAsync(provider, sourceLink, key, ct).ConfigureAwait(false);
                    if (result != null && result.IsSuccess)
                    {
                        return result.Url;
                    }

                    _logger.Warn($"Provider {provider.DisplayName} failed: {result?.Message}. Trying next...");
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"Debryfin: Error connecting to provider {provider.DisplayName}.", ex);
                }

                if (!config.EnableFallback)
                {
                    break;
                }
            }

            return null;
        }

        internal async Task<string> CallRealDebrid(string link, string token, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.real-debrid.com/rest/1.0/unrestrict/link");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("link", link) });

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("download", out var val)) return val.GetString();
            return null;
        }

        internal async Task<string> CallAllDebrid(string link, string token, CancellationToken ct)
        {
            var url = $"https://api.alldebrid.com/v4/link/unlock?agent=Debryfin&apikey={token}&link={Uri.EscapeDataString(link)}";
            using var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("link", out var dlLink)) return dlLink.GetString();
            return null;
        }
    }
}
