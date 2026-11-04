namespace Debryfin.Providers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Debryfin.Configuration;

    public interface IDebridProvider
    {
        DebridProvider Id { get; }
        string DisplayName { get; }
        TimeSpan DefaultLinkTtl { get; }
        Task<UnrestrictResult> UnrestrictAsync(string sourceLink, string apiKey, CancellationToken ct);
        Task<AccountInfo> GetUserInfoAsync(string apiKey, CancellationToken ct);
        Task<ValidationResult> ValidateApiKeyAsync(string apiKey, CancellationToken ct);
    }

    public enum ErrorKind
    {
        None, AuthFailed, RateLimited, Unsupported, NotFound, Transient, Pending, Permanent
    }

    public sealed class UnrestrictResult
    {
        public string Url { get; set; }
        public ErrorKind Error { get; set; }
        public string Message { get; set; }
        public TimeSpan? RetryAfter { get; set; }

        public static UnrestrictResult Ok(string url) => new UnrestrictResult { Url = url, Error = ErrorKind.None };
        public static UnrestrictResult Fail(ErrorKind kind, string message, TimeSpan? retryAfter = null) => new UnrestrictResult { Error = kind, Message = message, RetryAfter = retryAfter };
    }

    public sealed class AccountInfo
    {
        public string Username { get; set; }
        public string Email { get; set; }
        public bool IsPremium { get; set; }
        public DateTime? ExpirationUtc { get; set; }
    }

    public sealed class ValidationResult
    {
        public bool IsValid { get; set; }
        public string Message { get; set; }
    }
}
