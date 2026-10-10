using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace FiresPictures
{
    /// <summary>
    /// Server: who uploaded each picture and when (Fire 10-09: each entry records its author and time). One line per picture in
    /// pictures.idx beside them: "picFile|authorPlayerId|unixSeconds". The FIRST author stays: identical bytes re-uploaded by
    /// someone else map to the same file name and must not hand them the right to delete it. A picture dropped in by hand has no
    /// line and counts as the creator's (author 0: only the creator or an admin may delete it).
    /// </summary>
    internal static class Authors
    {
        private const string IndexFile = "pictures.idx";

        internal static void Record(string dir, string picFile, long authorId)
        {
            try
            {
                var lines = Read(dir);
                if (lines.Any(l => l.File == picFile)) return;
                File.AppendAllText(Path.Combine(dir, IndexFile),
                    $"{picFile}|{authorId.ToString(CultureInfo.InvariantCulture)}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\n");
            }
            catch (Exception ex) { FiresLoreBooks.FiresLoreBooksPlugin.Log?.LogWarning($"[Pictures] recording the author of {picFile} failed: {ex.Message}"); }
        }

        internal static long Of(string dir, string picFile) => Read(dir).FirstOrDefault(l => l.File == picFile).Author;

        internal static void Forget(string dir, string picFile)
        {
            try
            {
                var keep = Read(dir).Where(l => l.File != picFile).Select(l => l.Raw).ToList();
                File.WriteAllLines(Path.Combine(dir, IndexFile), keep);
            }
            catch { }
        }

        private static List<(string File, long Author, string Raw)> Read(string dir)
        {
            var list = new List<(string, long, string)>();
            string path = Path.Combine(dir, IndexFile);
            if (!File.Exists(path)) return list;
            foreach (string raw in File.ReadAllLines(path))
            {
                string[] p = raw.Split('|');
                if (p.Length >= 2 && long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long author))
                    list.Add((p[0], author, raw));
            }
            return list;
        }
    }
}
