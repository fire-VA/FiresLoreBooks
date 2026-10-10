using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;

namespace FiresPictures
{
    /// <summary>
    /// The server half of Store: assembles uploads, checks rights (Access.CanAddEntry to upload, CanEditEntry by the picture's author to delete) and caps, writes
    /// &lt;config&gt;/FiresLoreBooks/&lt;folder&gt;/p_&lt;hash&gt;.jpg, and serves, deletes and counts. Runs on a dedicated server or a host.
    /// </summary>
    internal static class Server
    {
        private sealed class Upload { internal string Folder; internal long PlayerId; internal byte[] Data; internal int Chunks, Received; }

        private static readonly Dictionary<string, Upload> s_uploads = new Dictionary<string, Upload>();

        private static string Root => Path.Combine(BepInEx.Paths.ConfigPath, "FiresLoreBooks");

        private static string Dir(string folder) => Path.Combine(Root, folder.Replace('/', Path.DirectorySeparatorChar));

        private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        internal static void OnUploadBegin(long sender, string req, string folder, int total)
        {
            if (!IsServer) return;
            long pid = PlayerIdOf(sender);
            string error = !Store.ValidFolder(folder) || !FiresLoreBooks.Access.CanAddEntry(pid, folder) ? "denied"
                : total <= 0 || total > Store.MaxBytes ? "too_big"
                : CountIn(folder) >= Store.PerJournalCap ? "cap_journal"
                : folder.StartsWith("Journals/") && PlayerBytes(folder) + total > Store.PerPlayerCapBytes ? "cap_player"
                : null;
            if (error != null) { Reply(sender, req, "", error); return; }
            s_uploads[sender + ":" + req] = new Upload { Folder = folder, PlayerId = pid, Data = new byte[total], Chunks = (total + Store.Chunk - 1) / Store.Chunk };
        }

        internal static void OnUploadChunk(long sender, string req, int index, ZPackage pkg)
        {
            if (!IsServer || !s_uploads.TryGetValue(sender + ":" + req, out Upload up)) return;
            byte[] chunk = pkg.GetArray();
            int at = index * Store.Chunk;
            if (at < 0 || at + chunk.Length > up.Data.Length) return;
            Buffer.BlockCopy(chunk, 0, up.Data, at, chunk.Length);
            if (++up.Received < up.Chunks) return;
            s_uploads.Remove(sender + ":" + req);
            string file = "p_" + Store.Hash(up.Data) + ".jpg";
            try
            {
                Directory.CreateDirectory(Dir(up.Folder));
                File.WriteAllBytes(Path.Combine(Dir(up.Folder), file), up.Data);
                Authors.Record(Dir(up.Folder), file, up.PlayerId);
                FiresLoreBooks.FiresLoreBooksPlugin.Log?.LogDebug($"[Pictures] {up.PlayerId} stored {up.Folder}/{file} ({up.Data.Length} bytes)");
                Reply(sender, req, file, "");
            }
            catch (Exception ex)
            {
                FiresLoreBooks.FiresLoreBooksPlugin.Log?.LogWarning($"[Pictures] writing {up.Folder}/{file} failed: {ex.Message}");
                Reply(sender, req, "", "denied");
            }
        }

        // knownHash: the client's cached hash for it; when it matches, nothing is sent again
        internal static void OnGet(long sender, string req, string folder, string file, string knownHash)
        {
            if (!IsServer) return;
            string path = Store.ValidFolder(folder) && Store.ValidFile(file) ? Path.Combine(Dir(folder), file) : null;
            if (path == null || !File.Exists(path)) { ZRoutedRpc.instance.InvokeRoutedRPC(sender, Store.GetMeta, req, "", 0); return; }
            byte[] data = File.ReadAllBytes(path);
            if (data.Length > Store.MaxBytes * 4) { ZRoutedRpc.instance.InvokeRoutedRPC(sender, Store.GetMeta, req, "", 0); return; }
            string hash = Store.Hash(data);
            if (hash == knownHash) { ZRoutedRpc.instance.InvokeRoutedRPC(sender, Store.GetMeta, req, hash, 0); return; }
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, Store.GetMeta, req, hash, data.Length);
            for (int i = 0, n = 0; i < data.Length; i += Store.Chunk, n++)
            {
                byte[] chunk = new byte[Math.Min(Store.Chunk, data.Length - i)];
                Buffer.BlockCopy(data, i, chunk, 0, chunk.Length);
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, Store.GetChunk, req, n, new ZPackage(chunk));
            }
        }

        internal static void OnDelete(long sender, string folder, string file)
        {
            if (!IsServer || !Store.ValidFolder(folder) || !Store.ValidFile(file)) return;
            if (!FiresLoreBooks.Access.CanEditEntry(PlayerIdOf(sender), folder, Authors.Of(Dir(folder), file))) return;
            string path = Path.Combine(Dir(folder), file);
            if (!File.Exists(path)) return;
            File.Delete(path);
            Authors.Forget(Dir(folder), file);
            FiresLoreBooks.FiresLoreBooksPlugin.Log?.LogDebug($"[Pictures] {PlayerIdOf(sender)} deleted {folder}/{file}");
        }

        internal static void OnCount(long sender, string folder)
        {
            if (!IsServer || !Store.ValidFolder(folder)) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, Store.CountAnswer, folder, CountIn(folder));
        }

        private static int CountIn(string folder) =>
            Directory.Exists(Dir(folder)) ? Directory.GetFiles(Dir(folder), "*.jpg").Length : 0;

        // Journals/<creator>/...: all of that creator's journal pictures
        private static long PlayerBytes(string folder)
        {
            string creatorDir = Path.Combine(Root, "Journals", folder.Split('/')[1]);
            return Directory.Exists(creatorDir) ? Directory.GetFiles(creatorDir, "*.jpg", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0L;
        }

        private static long PlayerIdOf(long sender)
        {
            if (sender == ZDOMan.GetSessionID()) return Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            ZDO zdo = peer != null && !peer.m_characterID.IsNone() ? ZDOMan.instance.GetZDO(peer.m_characterID) : null;
            return zdo != null ? zdo.GetLong(ZDOVars.s_playerID, 0L) : 0L;
        }

        private static void Reply(long sender, string req, string file, string error) =>
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, Store.UpDone, req, file, error);

        [HarmonyPatch(typeof(ZNet), "Update")]
        private static class TickOnUpdate { private static void Postfix() => Store.Tick(); }
    }
}
