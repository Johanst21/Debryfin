using Debryfin.Http;
using Xunit;

namespace Debryfin.Tests
{
    // P1-04: regression tests for SecretRedactor. The redactor is the single choke point
    // for anything that reaches the Emby log; these tests pin its contract across the
    // shapes that actually occur: query strings, header dumps and plain log text.
    public class SecretRedactorTests
    {
        // --- Query strings (apikey=, token=) ---

        [Fact]
        public void Redact_ApiKeyInQueryString_ReplacesValueWithMask()
        {
            Assert.Equal("https://api.test?apikey=***", SecretRedactor.Redact("https://api.test?apikey=12345"));
        }

        [Fact]
        public void Redact_ApiKeyAmongOtherParameters_OnlyRedactsTheApiKeyValue()
        {
            const string input = "https://api.alldebrid.com/v4/link/unlock?agent=Debryfin&apikey=ad-secret&link=https%3A%2F%2F1fichier.com%2F%3Fabc";

            Assert.Equal(
                "https://api.alldebrid.com/v4/link/unlock?agent=Debryfin&apikey=***&link=https%3A%2F%2F1fichier.com%2F%3Fabc",
                SecretRedactor.Redact(input));
        }

        [Fact]
        public void Redact_TokenInQueryString_ReplacesValueWithMask()
        {
            // The only sanctioned "token in URL" is TorBox requestdl; it still must not
            // reach the log unmasked.
            const string input = "https://api.torbox.app/v1/api/webdl/requestdl?token=tb-secret&web_id=42";

            Assert.Equal("https://api.torbox.app/v1/api/webdl/requestdl?token=***&web_id=42", SecretRedactor.Redact(input));
        }

        [Fact]
        public void Redact_UrlEncodedCredentialValue_IsFullyRedacted()
        {
            Assert.Equal("https://api.test?apikey=***", SecretRedactor.Redact("https://api.test?apikey=abc%2Fdef%3D%3D"));
        }

        [Fact]
        public void Redact_EmptyValueAfterEquals_LeavesItUntouched()
        {
            // No secret to hide: redacting "apikey=" would only add noise to the log.
            Assert.Equal("https://api.test?apikey=&link=x", SecretRedactor.Redact("https://api.test?apikey=&link=x"));
        }

        [Fact]
        public void Redact_CompositeTokenName_IsRedacted()
        {
            // Over-redaction is acceptable: "access_token=" contains "token=".
            Assert.Equal("Authorization access_token=*** sent", SecretRedactor.Redact("Authorization access_token=gho_123 sent"));
        }

        [Fact]
        public void Redact_KeyNameCasing_IsPreserved()
        {
            Assert.Equal("APIKEY=***", SecretRedactor.Redact("APIKEY=AbC123"));
        }

        // --- Authorization: Bearer headers ---

        [Fact]
        public void Redact_BearerHeader_ReplacesTokenWithMask()
        {
            Assert.Equal("Authorization: Bearer ***", SecretRedactor.Redact("Authorization: Bearer rd-secret-key"));
        }

        [Fact]
        public void Redact_BearerHeader_IsCaseInsensitive()
        {
            // The original casing of the scheme is preserved so header dumps stay readable.
            Assert.Equal("authorization: bearer ***", SecretRedactor.Redact("authorization: bearer rd-secret-key"));
        }

        [Fact]
        public void Redact_BearerTokenWithUrlSafeCharacters_IsFullyRedacted()
        {
            Assert.Equal("Authorization: Bearer ***", SecretRedactor.Redact("Authorization: Bearer abc123.DEF-456_789="));
        }

        [Fact]
        public void Redact_BearerFollowedByComma_StopsAtTheDelimiter()
        {
            Assert.Equal("Authorization: Bearer ***, other header", SecretRedactor.Redact("Authorization: Bearer secret123, other header"));
        }

        [Fact]
        public void Redact_BearerWithoutToken_LeavesItUntouched()
        {
            Assert.Equal("Authorization: Bearer", SecretRedactor.Redact("Authorization: Bearer"));
        }

        // --- Plain log text ---

        [Fact]
        public void Redact_MultipleSecretsInOneLine_RedactsAll()
        {
            const string input = "GET https://api.test?apikey=k1&token=k2 with Authorization: Bearer k3";

            Assert.Equal("GET https://api.test?apikey=***&token=*** with Authorization: Bearer ***", SecretRedactor.Redact(input));
        }

        [Fact]
        public void Redact_TextWithoutSecrets_ReturnsInputUnchanged()
        {
            const string input = "Debryfin[RealDebrid]: unrestrict ok (231 ms)";

            Assert.Equal(input, SecretRedactor.Redact(input));
        }

        [Fact]
        public void Redact_Output_NeverContainsTheOriginalSecret()
        {
            const string secret = "supersecret42";
            const string input = $"https://api.torbox.app/v1/api/webdl/requestdl?token={secret}&web_id=9 Authorization: Bearer {secret} apikey={secret}";

            var output = SecretRedactor.Redact(input);

            Assert.DoesNotContain(secret, output);
            Assert.Contains("***", output);
        }

        // --- Null / empty handling ---

        [Fact]
        public void Redact_Null_ReturnsNull()
        {
            Assert.Null(SecretRedactor.Redact(null));
        }

        [Fact]
        public void Redact_EmptyString_ReturnsEmptyString()
        {
            Assert.Equal(string.Empty, SecretRedactor.Redact(string.Empty));
        }
    }
}
