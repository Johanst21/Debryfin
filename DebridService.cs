using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Debryfin.Configuration;
using Debryfin.Providers;
using MediaBrowser.Model.Logging;

namespace Debryfin
{
    public class DebridService
    {
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private readonly ProviderRegistry _registry;
        // P0-05: adds coalescing of simultaneous requests and backoff on failures.
        // The real clock (UtcNow) is injected here, at the composition point;
        // the LinkResolver/NegativeCache logic receives it injected and is testable.
        private readonly Resolution.LinkResolver _linkResolver;

        public DebridService(ILogger logger)
        {
            _logger = logger;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Debryfin-Emby-Plugin/1.0");
            _registry = new ProviderRegistry();
            _registry.Register(new LegacyProviderAdapter(this, DebridProvider.RealDebrid));
            _registry.Register(new LegacyProviderAdapter(this, DebridProvider.AllDebrid));
            _linkResolver = new Resolution.LinkResolver(() => DateTime.UtcNow);
        }

        public async Task<string> UnrestrictLinkAsync(string sourceLink, CancellationToken ct)
        {
            var config = Plugin.Instance?.SafeGetOptions();

            if (config == null || string.IsNullOrEmpty(config.ApiKey))
            {
                _logger.Error("Debryfin: Configuration or API Key not found.");
                return null;
            }

            var provider = _registry.Get(config.Provider);
            if (provider == null)
            {
                _logger.Warn($"Debryfin[{config.Provider}]: provider not implemented.");
                return null;
            }

            try
            {
                // Resolution goes through LinkResolver (P0-05): applies coalescing by
                // (provider, url) and negative cache with backoff. The public signature
                // (string, null on failure) is kept.
                var result = await _linkResolver.ResolveAsync(provider, sourceLink, config.ApiKey, ct).ConfigureAwait(false);
                return result.IsSuccess ? result.Url : null;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Debryfin: Error connecting to provider.", ex);
                return null;
            }
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

        private async Task<string> CallTorBox(string link, string token, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.torbox.app/v1/api/link/unrestrict");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("link", link) });

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("link", out var dlLink)) return dlLink.GetString();
            return null;
        }

        private async Task<string> CallPremiumize(string link, string token, CancellationToken ct)
        {
            var url = $"https://www.premiumize.me/api/transfer/directdl?apikey={token}&src={Uri.EscapeDataString(link)}";
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("location", out var loc)) return loc.GetString();
            return null;
        }
    }
}