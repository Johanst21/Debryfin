namespace Debryfin.Tests
{
    using System;
    using Xunit;

    public class MediaSourceIdTests
    {
        [Fact]
        public void For_WithNonEmptyItemId_ReturnsItemGuidN()
        {
            var guid = Guid.NewGuid();
            var path = "/media/Movies/Test.strm";

            var result = MediaSourceId.For(guid, path);

            Assert.Equal(guid.ToString("N"), result);
            Assert.Equal(result, MediaSourceId.For(guid, path));

            var otherGuid = Guid.NewGuid();
            Assert.NotEqual(MediaSourceId.For(otherGuid, path), result);
        }

        [Fact]
        public void For_WithEmptyItemId_ReturnsStableSha1OfLowercasedPath()
        {
            var path = "/media/Movies/Test.strm";
            // echo -n "/media/movies/test.strm" | sha1sum -> c38e9f17040494b740b3a4b751faba580da19654
            var expectedSha1 = "c38e9f17040494b740b3a4b751faba580da19654";

            var result = MediaSourceId.For(Guid.Empty, path);

            Assert.Equal(expectedSha1, result);
        }

        [Fact]
        public void For_WithEmptyItemId_IsCaseInsensitiveOnPath()
        {
            var id1 = MediaSourceId.For(Guid.Empty, "/media/Movies/X.strm");
            var id2 = MediaSourceId.For(Guid.Empty, "/MEDIA/movies/x.STRM");

            Assert.Equal(id1, id2);
        }

        [Fact]
        public void For_WithEmptyItemId_AndNullPath_DoesNotThrow()
        {
            // Decision: treat null as an empty string (path ?? string.Empty) instead of throwing ArgumentNullException.
            // This prevents unexpected exceptions if Emby passes a BaseItem without a path or a corrupt one, yielding a deterministic hash.
            var result = MediaSourceId.For(Guid.Empty, null!);

            Assert.NotNull(result);
            Assert.NotEmpty(result);
        }
    }
}
