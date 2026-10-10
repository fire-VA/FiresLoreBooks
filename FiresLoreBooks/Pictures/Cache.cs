using System.Collections.Generic;
using System.IO;

namespace FiresPictures
{
    /// <summary>The client's copies, one file per content hash under BepInEx/cache/FiresPictures, and which hash each picture had
    /// last time (so a Get only downloads when the server's file changed).</summary>
    internal static class Cache
    {
        private static readonly Dictionary<string, string> s_known = new Dictionary<string, string>();

        private static string Dir => Path.Combine(BepInEx.Paths.CachePath, "FiresPictures");

        internal static void Put(string hash, byte[] jpg)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string path = Path.Combine(Dir, hash + ".jpg");
                if (!File.Exists(path)) File.WriteAllBytes(path, jpg);
            }
            catch { }
        }

        internal static byte[] Read(string hash)
        {
            try
            {
                string path = Path.Combine(Dir, hash + ".jpg");
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch { return null; }
        }

        internal static string KnownHash(string folder, string file) =>
            s_known.TryGetValue(folder + "/" + file, out string hash) && Read(hash) != null ? hash : null;

        internal static void Remember(string folder, string file, string hash) => s_known[folder + "/" + file] = hash;
    }
}
