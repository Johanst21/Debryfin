using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Debryfin
{
    internal static class MovieSiblingSelector
    {
        private static readonly Regex MovieRegex = new Regex(@"^(.*?)(?:\s\(?\d{4}\)?|\[|\()", RegexOptions.IgnoreCase);

        /// <summary>
        /// Returns the paths of the .strm files to process for the current movie item.
        /// Includes the current path and any version that shares the title prefix.
        /// </summary>
        public static IReadOnlyList<string> SelectFilesToProcess(
            string currentPath,
            IEnumerable<string> candidatePaths)
        {
            if (string.IsNullOrEmpty(currentPath))
            {
                return Array.Empty<string>();
            }

            var currentFileNameWithoutExt = Path.GetFileNameWithoutExtension(currentPath);
            var baseName = ExtractBaseName(currentFileNameWithoutExt);

            // If baseName is empty after the regex extraction (e.g. "(500) Days..." or "[4K]..."),
            // the full file name without extension is used to avoid StartsWith("") selecting everything.
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = currentFileNameWithoutExt;
            }

            return candidatePaths
                .Where(p => Path.GetFileNameWithoutExtension(p).StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        internal static string ExtractBaseName(string fileNameWithoutExt)
        {
            if (string.IsNullOrEmpty(fileNameWithoutExt))
            {
                return string.Empty;
            }

            var match = MovieRegex.Match(fileNameWithoutExt);
            return match.Success ? match.Groups[1].Value.Trim() : fileNameWithoutExt;
        }
    }
}
