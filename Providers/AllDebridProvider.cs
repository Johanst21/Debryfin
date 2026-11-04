namespace Debryfin.Providers
{
    using System;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Debryfin.Configuration;

    // P1-02/P1-03: native AllDebrid implementation of IDebridProvider.
    // The unrestrict request is a GET with agent/link in the query string and the API key
    // in the Authorization: Bearer header (the key never appears in the URL).
    public sealed class AllDebridProvider : IDebridProvider
    {
        private const string UnrestrictEndpoint = "https://api.alldebrid.com/v4/link/unlock";
        private const string UserEndpoint = "https://api.alldebrid.com/v4/user";
        private const string Agent = "Debryfin";

        private readonly HttpClient _httpClient;

        public AllDebridProvider(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public DebridProvider Id => DebridProvider.AllDebrid;

        public string DisplayName => "AllDebrid";

        public TimeSpan DefaultLinkTtl => TimeSpan.FromHours(72);

        public async Task<UnrestrictResult> UnrestrictAsync(string sourceLink, string apiKey, CancellationToken ct)
        {
            // GET with agent/link in the query string; the API key goes in the Bearer header.
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{UnrestrictEndpoint}?agent={Agent}&link={Uri.EscapeDataString(sourceLink)}");
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
                // Network-level failure (DNS, connection reset, timeout): retryable.
                return UnrestrictResult.Fail(ErrorKind.Transient, "Communication error with the provider.");
            }

            using (response)
            {
                if ((int)response.StatusCode != 200)
                {
                    // AllDebrid reports API-level errors with HTTP 200; a different status
                    // is an infrastructure failure and is retried.
                    return UnrestrictResult.Fail(ErrorKind.Transient, "The provider returned an HTTP error.");
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

                if (root.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("link", out var dlLink)
                    && dlLink.ValueKind == JsonValueKind.String)
                {
                    var directUrl = dlLink.GetString();
                    if (!string.IsNullOrEmpty(directUrl))
                    {
                        return UnrestrictResult.Ok(directUrl);
                    }
                }

                // 200 with status "success" but no link: AllDebrid is still generating it
                // (delayed response), so the resolver retries it after the pending backoff.
                return UnrestrictResult.Fail(ErrorKind.Pending, "The provider did not return a download link yet.");
            }
        }

        public async Task<AccountInfo> GetUserInfoAsync(string apiKey, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{UserEndpoint}?agent={Agent}");
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
                throw new InvalidOperationException("Unable to reach AllDebrid to fetch account information.");
            }

            using (response)
            {
                if ((int)response.StatusCode != 200)
                {
                    throw new InvalidOperationException("AllDebrid returned an error while fetching account information.");
                }

                // netstandard2.0: ReadAsStreamAsync has no CancellationToken overload.
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
                var root = doc.RootElement;

                if (IsError(root))
                {
                    throw new InvalidOperationException("AllDebrid returned an error while fetching account information.");
                }

                string username = null;
                var isPremium = false;
                if (root.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("user", out var user)
                    && user.ValueKind == JsonValueKind.Object)
                {
                    if (user.TryGetProperty("username", out var usernameProperty)
                        && usernameProperty.ValueKind == JsonValueKind.String)
                    {
                        username = usernameProperty.GetString();
                    }

                    if (user.TryGetProperty("isPremium", out var premiumProperty)
                        && (premiumProperty.ValueKind == JsonValueKind.True || premiumProperty.ValueKind == JsonValueKind.False))
                    {
                        isPremium = premiumProperty.GetBoolean();
                    }
                }

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

        private static bool IsError(JsonElement root)
        {
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), "error", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadErrorCode(JsonElement root)
        {
            if (root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String)
            {
                return code.GetString();
            }

            return null;
        }

        private static string ReadErrorMessage(JsonElement root)
        {
            if (root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }

            return null;
        }

        // Provisional mapping from the AllDebrid error codes (exact codes to be verified
        // against https://docs.alldebrid.com); anything unrecognized is treated as retryable.
        private static ErrorKind ClassifyError(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return ErrorKind.Transient;
            }

            if (code.IndexOf("AUTH_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ErrorKind.AuthFailed;
            }

            if (string.Equals(code, "LINK_HOST_NOT_SUPPORTED", StringComparison.OrdinalIgnoreCase))
            {
                return ErrorKind.Unsupported;
            }

            if (string.Equals(code, "LINK_DOWN", StringComparison.OrdinalIgnoreCase))
            {
                return ErrorKind.NotFound;
            }

            return ErrorKind.Transient;
        }

        private static string BuildErrorMessage(string code, string message)
        {
            if (string.IsNullOrEmpty(code))
            {
                return string.IsNullOrEmpty(message)
                    ? "AllDebrid returned an error."
                    : "AllDebrid error: " + message;
            }

            return string.IsNullOrEmpty(message)
                ? "AllDebrid error: " + code
                : $"AllDebrid error: {code} ({message})";
        }
    }
}
