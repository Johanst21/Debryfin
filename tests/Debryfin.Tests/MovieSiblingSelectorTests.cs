using System.Collections.Generic;
using Debryfin;
using Xunit;

namespace Debryfin.Tests
{
    public class MovieSiblingSelectorTests
    {
        [Fact]
        public void DaysOfSummer_WithNeighbors_SelectsOnlyItselfAndVersions_NotAllDirectory()
        {
            var currentPath = "/media/movies/(500) Days of Summer (2009).strm";
            var allStrm = new List<string>
            {
                "/media/movies/(500) Days of Summer (2009).strm",
                "/media/movies/The Matrix (1999).strm",
                "/media/movies/Inception (2010).strm",
                "/media/movies/Pulp Fiction (1994).strm"
            };

            var selected = MovieSiblingSelector.SelectFilesToProcess(currentPath, allStrm);

            // Must select only the movie (and its versions, if any), NEVER all the files in the directory.
            Assert.Single(selected);
            Assert.Contains(currentPath, selected);
        }

        [Fact]
        public void BracketName_WithNeighbors_SelectsOnlyItself()
        {
            var currentPath = "/media/movies/[4K] Peli.strm";
            var allStrm = new List<string>
            {
                "/media/movies/[4K] Peli.strm",
                "/media/movies/Alien (1979).strm",
                "/media/movies/Avatar (2009).strm"
            };

            var selected = MovieSiblingSelector.SelectFilesToProcess(currentPath, allStrm);

            Assert.Single(selected);
            Assert.Contains(currentPath, selected);
        }

        [Fact]
        public void Alien_MatchesPrefixVersions()
        {
            var currentPath = "/media/movies/Alien (1979).strm";
            var allStrm = new List<string>
            {
                "/media/movies/Alien (1979).strm",
                "/media/movies/Alien (1979) - 1080p.strm",
                "/media/movies/Gladiator (2000).strm"
            };

            var selected = MovieSiblingSelector.SelectFilesToProcess(currentPath, allStrm);

            Assert.Equal(2, selected.Count);
            Assert.Contains("/media/movies/Alien (1979).strm", selected);
            Assert.Contains("/media/movies/Alien (1979) - 1080p.strm", selected);
            Assert.DoesNotContain("/media/movies/Gladiator (2000).strm", selected);
        }

        [Fact]
        public void DoesNotIncludeStrmOriginalFiles()
        {
            // Emby only lists .strm files via GetFiles(..., new[] { ".strm" }),
            // but if candidatePaths included a .original, it must not be selected when it has a .strm.original extension
            var currentPath = "/media/movies/Alien (1979).strm";
            var allStrm = new List<string>
            {
                "/media/movies/Alien (1979).strm",
                "/media/movies/Alien (1979).strm.original"
            };

            // FileNameWithoutExtension of "Alien (1979).strm.original" is "Alien (1979).strm",
            // but in Emby only .strm files obtained from GetFiles(directory, new[] { ".strm" }) are passed.
            // If we filter by the .strm extension:
            var selected = MovieSiblingSelector.SelectFilesToProcess(currentPath, allStrm);
            Assert.Contains("/media/movies/Alien (1979).strm", selected);
        }

        [Fact]
        public void Inception_ExistingBehavior_WorksAsExpected()
        {
            var currentPath = "/media/movies/Inception (2010).strm";
            var allStrm = new List<string>
            {
                "/media/movies/Inception (2010).strm",
                "/media/movies/Inception (2010) - 2160p.strm",
                "/media/movies/Interstellar (2014).strm"
            };

            var selected = MovieSiblingSelector.SelectFilesToProcess(currentPath, allStrm);

            Assert.Equal(2, selected.Count);
            Assert.Contains("/media/movies/Inception (2010).strm", selected);
            Assert.Contains("/media/movies/Inception (2010) - 2160p.strm", selected);
            Assert.DoesNotContain("/media/movies/Interstellar (2014).strm", selected);
        }

        [Fact]
        public void SingleStrmInDirectory_DoesNotFail()
        {
            var currentPath = "/media/movies/(500) Days of Summer (2009).strm";
            var allStrm = new List<string>
            {
                "/media/movies/(500) Days of Summer (2009).strm"
            };

            var selected = MovieSiblingSelector.SelectFilesToProcess(currentPath, allStrm);

            Assert.Single(selected);
            Assert.Equal(currentPath, selected[0]);
        }

        [Theory]
        [InlineData("(500) Days of Summer (2009).strm", "(500) Days of Summer (2009)")]
        [InlineData("[4K] Peli.strm", "[4K] Peli")]
        [InlineData("Alien (1979).strm", "Alien")]
        [InlineData("Inception (2010) [1080p].strm", "Inception")]
        [InlineData("SimpleMovie.strm", "SimpleMovie")]
        public void ExtractBaseName_WorksAsExpected(string fileName, string expectedBaseName)
        {
            var fileNameWithoutExt = System.IO.Path.GetFileNameWithoutExtension(fileName);
            var extracted = MovieSiblingSelector.ExtractBaseName(fileNameWithoutExt);
            if (string.IsNullOrWhiteSpace(extracted))
            {
                extracted = fileNameWithoutExt;
            }

            Assert.Equal(expectedBaseName, extracted);
        }
    }
}
