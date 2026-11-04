namespace Debryfin.Resolution
{
    using Debryfin.Providers;

    public sealed class LinkResolutionResult
    {
        public string Url { get; set; }
        public ErrorKind Error { get; set; }
        public string Message { get; set; }
        public bool IsSuccess => !string.IsNullOrEmpty(Url);
    }
}
