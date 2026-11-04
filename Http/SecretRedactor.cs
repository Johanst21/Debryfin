namespace Debryfin.Http
{
    using System;
    using System.Text.RegularExpressions;

    // P1-04: single choke point through which every log message and exception message
    // must pass before being written to the Emby log. It replaces credential values
    // (API keys in query strings, Bearer tokens in headers) with a fixed mask so a
    // secret can never reach the log even if a future refactor re-introduces one in
    // a URL or an exception message.
    //
    // The mask is deliberately over-eager: a false positive (redacting a harmless
    // value that merely looks like a credential) is acceptable; a false negative
    // (leaking a real key) is not.
    public static class SecretRedactor
    {
        private const string Mask = "***";

        private static readonly RegexOptions PatternOptions = RegexOptions.IgnoreCase | RegexOptions.Compiled;

        // "apikey=<value>" where the value runs until the next parameter or whitespace.
        // An empty value ("apikey=") is left untouched: there is no secret to hide.
        private static readonly Regex ApiKeyPattern = new Regex("(apikey)=([^&\\s]+)", PatternOptions);

        // "token=<value>" (also matches inside "access_token=" etc.; over-redaction is
        // safe, under-redaction is not).
        private static readonly Regex TokenPattern = new Regex("(token)=([^&\\s]+)", PatternOptions);

        // "Authorization: Bearer <value>" as it appears in header dumps; the value runs
        // until whitespace, comma or semicolon.
        private static readonly Regex BearerPattern = new Regex("(authorization\\s*:\\s*bearer\\s+)([^\\s,;]+)", PatternOptions);

        // Replaces every credential value in the input with "***". The input itself is
        // returned unchanged when null or empty (there is nothing to redact).
        public static string Redact(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return input;
            }

            // The first group keeps the key name in its original casing ("apikey=" stays
            // "apikey=") so redacted logs remain readable.
            var result = ApiKeyPattern.Replace(input, m => m.Groups[1].Value + "=" + Mask);
            result = TokenPattern.Replace(result, m => m.Groups[1].Value + "=" + Mask);
            return BearerPattern.Replace(result, m => m.Groups[1].Value + Mask);
        }
    }
}
