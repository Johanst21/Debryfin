namespace Debryfin.Providers
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Debryfin.Configuration;

    // P1-01: native Real-Debrid implementation of IDebridProvider.
    // The unrestrict request is byte-for-byte identical to the legacy DebridService.CallRealDebrid
    // (same method, URL, Authorization header and x-www-form-urlencoded body); only the error
    // classification is richer than the legacy null/exception contract.
    public sealed class RealDebridProvider : IDebridProvider
    {
        private const string UnrestrictEndpoint = "https://api.real-debrid.com/rest/1.0/unrestrict/link";
        private const string UserEndpoint = "https://api.real-debrid.com/rest/1.0/user";

        private readonly HttpClient _httpClient;

        public RealDebridProvider(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public DebridProvider Id => DebridProvider.RealDebrid;

        public string DisplayName => "Real-Debrid";

        public TimeSpan DefaultLinkTtl => TimeSpan.FromHours(72);

        public async Task<UnrestrictResult> UnrestrictAsync(string sourceLink, string apiKey, CancellationToken ct)
        {
            // Identical request to the legacy CallRealDebrid: POST, Bearer auth, form body "link=<link>".
            var request = new HttpRequestMessage(HttpMethod.Post, UnrestrictEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("link", sourceLink) });

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Network-level failure (DNS, connection reset, timeout): retryable.
                return UnrestrictResult.Fail(ErrorKind.Transient, "Communication error with the provider.");
            }

            using (response)
            {
                var status = (int)response.StatusCode;

                if (status == 429)
                {
                    return UnrestrictResult.Fail(ErrorKind.RateLimited, "Rate limit reached.", ParseRetryAfter(response));
                }

                if (status == 401 || status == 403)
                {
                    return UnrestrictResult.Fail(ErrorKind.AuthFailed, "Authentication failed or the token is invalid.");
                }

                if (status >= 500)
                {
                    return UnrestrictResult.Fail(ErrorKind.Transient, "The provider returned a server error.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    // Any other 4xx: the request itself was rejected and retrying is unlikely to help.
                    return UnrestrictResult.Fail(ErrorKind.Permanent, "The provider rejected the link.");
                }

                // netstandard2.0: ReadAsStreamAsync has no CancellationToken overload.
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("download", out var val))
                {
                    var url = val.GetString();
                    if (!string.IsNullOrEmpty(url))
                    {
                        return UnrestrictResult.Ok(url);
                    }
                }

                // 2xx but no "download" field: the link cannot be unlocked for this account.
                return UnrestrictResult.Fail(ErrorKind.Permanent, "The provider did not return a download link.");
            }
        }

        public async Task<AccountInfo> GetUserInfoAsync(string apiKey, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, UserEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                throw new InvalidOperationException("Unable to reach Real-Debrid to fetch account information.");
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("Real-Debrid returned an error while fetching account information.");
                }

                // netstandard2.0: ReadAsStreamAsync has no CancellationToken overload.
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
                var root = doc.RootElement;

                string username = null;
                if (root.TryGetProperty("username", out var u))
                {
                    username = u.GetString();
                }

                var isPremium = root.TryGetProperty("type", out var t)
                    && string.Equals(t.GetString(), "premium", StringComparison.OrdinalIgnoreCase);

                return new AccountInfo
                {
                    Username = username,
                    IsPremium = isPremium
                };
            }
        }

        public async Task<ValidationResult> ValidateApiKeyAsync(string apiKey, CancellationToken ct)
        {
            try
            {
                await GetUserInfoAsync(apiKey, ct).ConfigureAwait(false);
                return new ValidationResult { IsValid = true, Message = "Valid" };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return new ValidationResult { IsValid = false, Message = "The API key could not be validated." };
            }
        }

        private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter?.Delta != null)
            {
                return retryAfter.Delta;
            }

            // Retry-After may also be an HTTP-date.
            if (retryAfter?.Date != null)
            {
                var delta = retryAfter.Date.Value - DateTime.UtcNow;
                return delta > TimeSpan.Zero ? delta : TimeSpan.FromSeconds(1);
            }

            return null;
        }
    }
}
