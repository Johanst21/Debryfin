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
        /// Devuelve los paths de archivos .strm a procesar para el ítem de película actual.
        /// Incluye el path actual y cualquier versión que comparta el prefijo de título.
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

            // Si baseName queda vacío tras la extracción por regex (p.ej. "(500) Days..." o "[4K]..."),
            // se usa el nombre de fichero completo sin extensión para evitar StartsWith("") que seleccionaría todo.
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
