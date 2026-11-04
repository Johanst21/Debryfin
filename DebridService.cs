using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Debryfin.Configuration;
using MediaBrowser.Model.Logging;

namespace Debryfin
{
    public class DebridService
    {
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;

        public DebridService(ILogger logger)
        {
            _logger = logger;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Debryfin-Emby-Plugin/1.0");
        }

        public async Task<string> UnrestrictLinkAsync(string sourceLink, CancellationToken ct)
        {
            var config = Plugin.Instance?.Configuration;
            
            if (config == null || string.IsNullOrEmpty(config.ApiKey))
            {
                _logger.Error("Debryfin: Configuración o API Key no encontrada.");
                return null;
            }

            try
            {
                switch (config.Provider)
                {
                    case DebridProvider.RealDebrid:
                        return await CallRealDebrid(sourceLink, config.ApiKey, ct);
                    case DebridProvider.AllDebrid:
                        return await CallAllDebrid(sourceLink, config.ApiKey, ct);
                    case DebridProvider.TorBox:
                        return await CallTorBox(sourceLink, config.ApiKey, ct);
                    case DebridProvider.Premiumize:
                        return await CallPremiumize(sourceLink, config.ApiKey, ct);
                    default:
                        return null;
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Debryfin: Error conectando con el proveedor.", ex);
                return null;
            }
        }

        private async Task<string> CallRealDebrid(string link, string token, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.real-debrid.com/rest/1.0/unrestrict/link");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("link", link) });

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            
            // CORREGIDO: ReadAsStringAsync() sin argumentos
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("download", out var val)) return val.GetString();
            return null;
        }

        private async Task<string> CallAllDebrid(string link, string token, CancellationToken ct)
        {
            var url = $"https://api.alldebrid.com/v4/link/unlock?agent=Debryfin&apikey={token}&link={Uri.EscapeDataString(link)}";
            using var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            // CORREGIDO: ReadAsStringAsync() sin argumentos
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

            // CORREGIDO: ReadAsStringAsync() sin argumentos
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

            // CORREGIDO: ReadAsStringAsync() sin argumentos
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("location", out var loc)) return loc.GetString();
            return null;
        }
    }
}