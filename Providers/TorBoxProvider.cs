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

    // P1-05: native TorBox implementation of IDebridProvider.
    // Resolution is a two-step flow (verified against the official TorBox SDK
    // documentation, https://docs.torbox.app):
    //   1. POST /v1/api/webdl/createwebdownload (Bearer, form "link=") -> data.webdownload_id
    //   2. GET  /v1/api/webdl/requestdl?token=<key>&web_id=<id>       -> data = direct URL (string)
    // The API key unavoidably travels in the query string of step 2 (token=...), so every
    // text coming back from the API is run through SecretRedactor before it can reach a
    // log. If the hoster file is not cached, TorBox downloads it first: the provider polls
    // step 2 with a bounded budget and returns ErrorKind.Pending when it runs out, which
    // the LinkResolver negative cache retries after its pending backoff.
    public sealed class TorBoxProvider : IDebridProvider
    {
        private const string CreateWebDownloadEndpoint = "https://api.torbox.app/v1/api/webdl/createwebdownload";
        private const string RequestDownloadEndpoint = "https://api.torbox.app/v1/api/webdl/requestdl";
        private const string UserEndpoint = "https://api.torbox.app/v1/api/user/me";

        // The direct link opens for about 3 hours according to the SDK docs; 2 hours is
        // used so the link is re-resolved with margin. (TTL to be verified in practice.)
        private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(2);

        // Bounded polling budget/interval for step 2 while the web download is not ready.
        private static readonly TimeSpan DefaultPollBudget = TimeSpan.FromSeconds(25);
        private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

        private readonly HttpClient _httpClient;
        private readonly TimeSpan _pollBudget;
        private readonly TimeSpan _pollInterval;

        public TorBoxProvider(HttpClient httpClient)
            : this(httpClient, DefaultPollBudget, DefaultPollInterval)
        {
        }

        // Test seam (InternalsVisibleTo "Debryfin.Tests"): shrinks the polling budget and
        // interval so the "still downloading -> Pending" path is exercised without waiting.
        internal TorBoxProvider(HttpClient httpClient, TimeSpan pollBudget, TimeSpan pollInterval)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _pollBudget = pollBudget;
            _pollInterval = pollInterval;
        }

        public DebridProvider Id => DebridProvider.TorBox;

        public string DisplayName => "TorBox";

        public TimeSpan DefaultLinkTtl => DefaultTtl;

        public async Task<UnrestrictResult> UnrestrictAsync(string sourceLink, string apiKey, CancellationToken ct)
        {
            // Step 1 of 2: register the link as a "web download". TorBox answers either
            // with a ready webdownload_id (hoster cached) or with an API-level error when
            // it has to download the file first.
            var createRequest = new HttpRequestMessage(HttpMethod.Post, CreateWebDownloadEndpoint);
            createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            createRequest.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("link", sourceLink) });

            HttpResponseMessage createResponse;
            try
            {
                createResponse = await _httpClient.SendAsync(createRequest, ct).ConfigureAwait(false);
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

            using (createResponse)
            {
                var status = (int)createResponse.StatusCode;
                if (status == 429)
                {
                    return UnrestrictResult.Fail(ErrorKind.RateLimited, "Rate limit reached.", ParseRetryAfter(createResponse));
                }

                if (status == 401 || status == 403)
                {
                    return UnrestrictResult.Fail(ErrorKind.AuthFailed, "Authentication failed or the token is invalid.");
                }

                if (!createResponse.IsSuccessStatusCode)
                {
                    // 5xx and any other 4xx: retryable.
                    return UnrestrictResult.Fail(ErrorKind.Transient, "The provider returned an HTTP error.");
                }

                // netstandard2.0: ReadAsStreamAsync has no CancellationToken overload.
                using var stream = await createResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
                var root = doc.RootElement;

                if (IsApiFailure(root))
                {
                    var errorText = SecretRedactor.Redact(ReadErrorText(root));
                    if (LooksLikeNotCached(errorText))
                    {
                        // Hoster not in TorBox's cache: the download is queued. The
                        // resolver's pending backoff will re-create the web download later.
                        return UnrestrictResult.Fail(ErrorKind.Pending, BuildMessage("The provider is still downloading the link.", errorText));
                    }

                    return UnrestrictResult.Fail(ErrorKind.Transient, BuildMessage("The provider rejected the link.", errorText));
                }

                if (root.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("webdownload_id", out var webId)
                    && webId.ValueKind == JsonValueKind.String)
                {
                    var webDownloadId = webId.GetString();
                    if (!string.IsNullOrEmpty(webDownloadId))
                    {
                        // Step 2 of 2: request the direct URL, polling while the web
                        // download is not ready.
                        return await RequestDirectUrlAsync(apiKey, webDownloadId, ct).ConfigureAwait(false);
                    }
                }

                // 2xx success but no "data.webdownload_id": unexpected shape, retryable.
                return UnrestrictResult.Fail(ErrorKind.Transient, "The provider did not return a web download id.");
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
                throw new InvalidOperationException("Unable to reach TorBox to fetch account information.");
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("TorBox returned an error while fetching account information.");
                }

                // netstandard2.0: ReadAsStreamAsync has no CancellationToken overload.
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
                var root = doc.RootElement;

                // The exact response shape (flat vs "data"-wrapped) is to be verified
                // against the official documentation; each field is read from whichever
                // level carries it so the parsing stays resilient.
                string username = null;
                string email = null;
                var isPremium = false;
                DateTime? expirationUtc = null;

                var usernameElement = PickElement(root, "username");
                if (usernameElement.ValueKind == JsonValueKind.String)
                {
                    username = usernameElement.GetString();
                }

                var emailElement = PickElement(root, "email");
                if (emailElement.ValueKind == JsonValueKind.String)
                {
                    email = emailElement.GetString();
                }

                var planElement = PickElement(root, "plan");
                if (planElement.ValueKind == JsonValueKind.String)
                {
                    isPremium = string.Equals(planElement.GetString(), "premium", StringComparison.OrdinalIgnoreCase);
                }

                var expirationElement = PickElement(root, "premium_until");
                if (expirationElement.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(expirationElement.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                {
                    expirationUtc = parsed;
                }

                return new AccountInfo
                {
                    Username = username,
                    Email = email,
                    IsPremium = isPremium,
                    ExpirationUtc = expirationUtc
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

        // Step 2 of the flow. The key unavoidably travels in the query string (token=...);
        // any provider text embedded in the returned message is already redacted by the
        // caller, and the static messages here never contain it.
        private async Task<UnrestrictResult> RequestDirectUrlAsync(string apiKey, string webDownloadId, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + _pollBudget;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{RequestDownloadEndpoint}?token={Uri.EscapeDataString(apiKey)}&web_id={Uri.EscapeDataString(webDownloadId)}");

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
                    if (status == 401 || status == 403)
                    {
                        return UnrestrictResult.Fail(ErrorKind.AuthFailed, "Authentication failed or the token is invalid.");
                    }

                    if (status == 429)
                    {
                        return UnrestrictResult.Fail(ErrorKind.RateLimited, "Rate limit reached.", ParseRetryAfter(response));
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        string errorText = null;
                        try
                        {
                            // netstandard2.0: ReadAsStreamAsync has no CancellationToken overload.
                            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                            using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
                            var root = doc.RootElement;

                            // On success "data" is the direct URL as a plain string.
                            if (root.TryGetProperty("data", out var data)
                                && data.ValueKind == JsonValueKind.String)
                            {
                                var url = data.GetString();
                                if (!string.IsNullOrEmpty(url))
                                {
                                    return UnrestrictResult.Ok(url);
                                }
                            }

                            if (IsApiFailure(root))
                            {
                                errorText = SecretRedactor.Redact(ReadErrorText(root));
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (JsonException)
                        {
                            // Malformed payload: treat as "not ready" and retry until the budget runs out.
                        }

                        // No direct URL yet: the web download is probably still processing.
                        if (DateTime.UtcNow >= deadline)
                        {
                            return UnrestrictResult.Fail(ErrorKind.Pending,
                                BuildMessage("The link is not ready yet; the provider is still processing the download.", errorText));
                        }
                    }
                    else if (DateTime.UtcNow >= deadline)
                    {
                        // Non-success status (e.g. the web download is not ready): retryable.
                        return UnrestrictResult.Fail(ErrorKind.Pending,
                            "The link is not ready yet; the provider is still processing the download.");
                    }
                }

                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
            }
        }

        // TorBox reports API-level problems with HTTP 200 using "success": false and/or a
        // non-null "error"/"detail" text.
        private static bool IsApiFailure(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            {
                return true;
            }

            return !string.IsNullOrEmpty(ReadErrorText(root));
        }

        private static string ReadErrorText(JsonElement root)
        {
            var parts = new List<string>();
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(error.GetString()))
            {
                parts.Add(error.GetString());
            }

            if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(detail.GetString()))
            {
                parts.Add(detail.GetString());
            }

            return parts.Count == 0 ? null : string.Join(" ", parts);
        }

        // The exact "not cached" wording of the API is to be verified against the official
        // documentation; common phrasings are matched so the pending path is taken.
        private static bool LooksLikeNotCached(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var lowered = text.ToLowerInvariant();
            return lowered.Contains("not cached")
                || lowered.Contains("not in cache")
                || lowered.Contains("downloading")
                || lowered.Contains("download in progress")
                || lowered.Contains("in progress")
                || lowered.Contains("pending")
                || lowered.Contains("queued");
        }

        private static string BuildMessage(string baseMessage, string redactedErrorText)
        {
            return string.IsNullOrEmpty(redactedErrorText)
                ? baseMessage
                : $"{baseMessage} {redactedErrorText}";
        }

        // Reads a property from the JSON root, or from its "data" object when the root
        // does not carry it (the API shape varies between endpoints/versions).
        private static JsonElement PickElement(JsonElement root, string propertyName)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            if (root.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null)
            {
                return value;
            }

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty(propertyName, out var inner) && inner.ValueKind != JsonValueKind.Null)
            {
                return inner;
            }

            return default;
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
