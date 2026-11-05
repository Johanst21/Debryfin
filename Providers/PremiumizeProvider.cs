namespace Debryfin.Providers
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Debryfin.Configuration;
    using Debryfin.Http;

    // P1-06: native Premiumize implementation of IDebridProvider (verified against
    // https://www.premiumize.me/api):
    //   POST /api/transfer/directdl (Bearer, form "src=<link>")
    //   -> {"status":"success","content":[{"path","size","link"}]}
    // The direct URL is read from the selected content element's "link" property. The
    // legacy "location"/"stream_link" fields are deprecated and intentionally ignored.
    // API-level errors arrive as HTTP 200 with {"status":"error","message","code"};
    // each code is mapped to an ErrorKind (unknown codes are treated as retryable).
    public sealed class PremiumizeProvider : IDebridProvider
    {
        private const string DirectDownloadEndpoint = "https://www.premiumize.me/api/transfer/directdl";
        private const string AccountInfoEndpoint = "https://www.premiumize.me/api/account/info";

        // Direct links expire in a few hours; 12 hours is the conservative default
        // (to be verified in practice against a real account).
        private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(12);

        private readonly HttpClient _httpClient;

        public PremiumizeProvider(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public DebridProvider Id => DebridProvider.Premiumize;

        public string DisplayName => "Premiumize";

        public TimeSpan DefaultLinkTtl => DefaultTtl;

        public async Task<UnrestrictResult> UnrestrictAsync(string sourceLink, string apiKey, CancellationToken ct)
        {
            // POST with Bearer auth and a form body "src=<link>"; the key never appears in the URL.
            var request = new HttpRequestMessage(HttpMethod.Post, DirectDownloadEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("src", sourceLink) });

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
                    // Premiumize reports API errors with HTTP 200; a 5xx is an
                    // infrastructure failure and is retried (HTTP 500 == unknown_error).
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
                var root = doc.RootElement;

                if (IsError(root))
                {
                    var code = ReadErrorCode(root);
                    var message = ReadErrorMessage(root);
                    return UnrestrictResult.Fail(ClassifyError(code), BuildErrorMessage(code, message));
                }

                if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    var url = PickDirectUrl(content);
                    if (!string.IsNullOrEmpty(url))
                    {
                        return UnrestrictResult.Ok(url);
                    }
                }

                // 200 with "success" but no usable link: the file cannot be unlocked for
                // this account (e.g. the transfer does not exist or is not owned).
                return UnrestrictResult.Fail(ErrorKind.Permanent, "The provider did not return a download link.");
            }
        }

        public async Task<AccountInfo> GetUserInfoAsync(string apiKey, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, AccountInfoEndpoint);
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
                throw new InvalidOperationException("Unable to reach Premiumize to fetch account information.");
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("Premiumize returned an error while fetching account information.");
                }

                // netstandard2.0: ReadAsStreamAsync has no CancellationToken overload.
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
                var root = doc.RootElement;

                if (IsError(root))
                {
                    throw new InvalidOperationException("Premiumize returned an error while fetching account information.");
                }

                var info = new AccountInfo();

                // Account payload: {"status":"success","content":{"customer_id":N,"premium_until":"...", ...}}.
                // "premium_until" is null for a free account.
                if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
                {
                    if (content.TryGetProperty("customer_id", out var customerId)
                        && (customerId.ValueKind == JsonValueKind.Number || customerId.ValueKind == JsonValueKind.String))
                    {
                        info.Username = customerId.ToString();
                    }

                    if (content.TryGetProperty("premium_until", out var premiumUntil)
                        && premiumUntil.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(premiumUntil.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                    {
                        info.ExpirationUtc = parsed;
                        info.IsPremium = true;
                    }
                }

                return info;
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

        private static bool IsError(JsonElement root)
        {
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), "error", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadErrorCode(JsonElement root)
        {
            if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
            {
                return code.GetString();
            }

            return null;
        }

        private static string ReadErrorMessage(JsonElement root)
        {
            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(message.GetString()))
            {
                return SecretRedactor.Redact(message.GetString());
            }

            return null;
        }

        // Mapping of the Premiumize error codes (see https://www.premiumize.me/api);
        // anything unrecognized is treated as retryable.
        private static ErrorKind ClassifyError(string code)
        {
            switch (code)
            {
                case null:
                    return ErrorKind.Transient;
                case "authentication_failed":
                case "permission_denied":
                    return ErrorKind.AuthFailed;
                case "service_limit_reached":
                case "account_limit_reached":
                case "rate_limit_reached":
                    return ErrorKind.RateLimited;
                case "link_generation_failed":
                case "transient_error":
                case "unknown_error":
                case "service_down":
                    return ErrorKind.Transient;
                case "service_unsupported":
                    return ErrorKind.Unsupported;
                case "not_found":
                case "invalid_request":
                case "permanent_error":
                    return ErrorKind.Permanent;
                default:
                    return ErrorKind.Transient;
            }
        }

        private static string BuildErrorMessage(string code, string redactedMessage)
        {
            if (string.IsNullOrEmpty(code))
            {
                return string.IsNullOrEmpty(redactedMessage)
                    ? "Premiumize returned an error."
                    : "Premiumize error: " + redactedMessage;
            }

            return string.IsNullOrEmpty(redactedMessage)
                ? "Premiumize error: " + code
                : $"Premiumize error: {code} ({redactedMessage})";
        }

        // Selects the content element carrying the largest "size" (magnet/torrent links
        // may expand into several files; the biggest one is the video). Falls back to
        // the first element when no element carries a usable size.
        private static string PickDirectUrl(JsonElement content)
        {
            long bestSize = -1;
            string bestUrl = null;
            string firstUrl = null;

            foreach (var element in content.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string url = null;
                if (element.TryGetProperty("link", out var link)
                    && link.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(link.GetString()))
                {
                    url = link.GetString();
                }

                if (url == null)
                {
                    continue;
                }

                if (firstUrl == null)
                {
                    firstUrl = url;
                }

                var size = 0L;
                if (element.TryGetProperty("size", out var sizeProperty)
                    && sizeProperty.ValueKind == JsonValueKind.Number
                    && sizeProperty.TryGetInt64(out var parsed))
                {
                    size = parsed;
                }

                if (bestUrl == null || size > bestSize)
                {
                    bestUrl = url;
                    bestSize = size;
                }
            }

            return bestUrl ?? firstUrl;
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
