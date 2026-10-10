using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FiresPictures;
using HarmonyLib;

namespace FiresLoreBooks.Books
{
    /// <summary>
    /// Journal entries (design §3 + Fire's lock rules 10-09). A journal is a folder "Journals/&lt;creatorId&gt;/&lt;journalId&gt;" on
    /// the server; its entries live in entries.txt, each with the author's player id, name and time, written by the
    /// SERVER. Rights come from [fgn]'s Access (CanAddEntry / CanEditEntry / IsCreator): a new journal is locked; the
    /// creator (= the recorded owner) or an admin locks / unlocks; unlocked, anyone adds entries and authors keep editing
    /// their own; reading is never checked. Each reply tells the reader what THEY may do, so the UI never guesses.
    /// </summary>
    public static class JournalStore
    {
        internal const string RpcGet = "FLB_JGet";         // client -> server: folder
        internal const string RpcData = "FLB_JData";       // server -> client: folder, ZPackage
        internal const string RpcAdd = "FLB_JAdd";         // client -> server: folder, text
        internal const string RpcEdit = "FLB_JEdit";       // client -> server: folder, index, text ("" = delete)

        public sealed class Entry
        {
            public long AuthorId;
            public string AuthorName = "";
            public long Time;           // UTC unix seconds
            public string Text = "";
            public bool CanEdit;        // for the reader who asked
        }

        public sealed class View
        {
            public string Folder;
            public bool Locked;
            public bool IsCreator;
            public bool CanAdd;
            public readonly List<Entry> Entries = new List<Entry>();
        }

        /// <summary>Client: a journal's view arrived or changed (folder).</summary>
        public static event Action<View> Arrived;

        private static ZRoutedRpc s_registeredOn;
        private const string EntrySep = "\u001e";   // record separator inside entries.txt
        private const string FieldSep = "\u001f";

        public static string FolderOf(long creatorId, string journalId) => "Journals/" + creatorId.ToString(CultureInfo.InvariantCulture) + "/" + journalId;

        // ================= client =================

        public static void Request(string folder) => ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcGet, folder);
        public static void Add(string folder, string text) => ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcAdd, folder, RichText.Sanitize(text));
        public static void Edit(string folder, int index, string text) => ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcEdit, folder, index, RichText.Sanitize(text ?? ""));

        // ================= RPCs =================

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class RegisterOnStart
        {
            private static void Postfix()
            {
                ZRoutedRpc rpc = ZRoutedRpc.instance;
                if (rpc == null || rpc == s_registeredOn) return;
                s_registeredOn = rpc;
                rpc.Register<string>(RpcGet, OnGet);
                rpc.Register<string, ZPackage>(RpcData, OnData);
                rpc.Register<string, string>(RpcAdd, OnAdd);
                rpc.Register<string, int, string>(RpcEdit, OnEdit);
            }
        }

        private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        private static bool ValidFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || folder.Contains("..") || folder.Contains("\\")) return false;
            string[] p = folder.Split('/');
            return p.Length == 3 && p[0] == "Journals" && long.TryParse(p[1], out _) && BookStore.ValidId(p[2]);
        }

        private static void OnGet(long sender, string folder)
        {
            if (!IsServer || !ValidFolder(folder)) return;
            Send(sender, folder);
        }

        private static void OnAdd(long sender, string folder, string text)
        {
            if (!IsServer || !ValidFolder(folder) || string.IsNullOrWhiteSpace(text)) return;
            long pid = Access.PlayerIdOfSender(sender);
            if (!Access.CanAddEntry(pid, folder)) { FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] journal {folder}: {pid} may not add"); Send(sender, folder); return; }
            List<Entry> entries = Load(folder);
            entries.Add(new Entry { AuthorId = pid, AuthorName = NameOf(sender), Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Text = RichText.Sanitize(text) });
            Save(folder, entries);
            FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] journal {folder}: entry {entries.Count} added by {pid}");
            Broadcast(folder);
        }

        private static void OnEdit(long sender, string folder, int index, string text)
        {
            if (!IsServer || !ValidFolder(folder)) return;
            long pid = Access.PlayerIdOfSender(sender);
            List<Entry> entries = Load(folder);
            if (index < 0 || index >= entries.Count) return;
            if (!Access.CanEditEntry(pid, folder, entries[index].AuthorId)) { FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] journal {folder}: {pid} may not edit entry {index + 1}"); Send(sender, folder); return; }
            if (string.IsNullOrWhiteSpace(text)) entries.RemoveAt(index);
            else entries[index].Text = RichText.Sanitize(text);
            Save(folder, entries);
            Broadcast(folder);
        }

        /// <summary>Everyone currently asking about this journal gets the new view (each with their own rights).</summary>
        private static void Broadcast(string folder)
        {
            foreach (ZNetPeer peer in ZNet.instance.GetPeers()) Send(peer.m_uid, folder);
            if (!ZNet.instance.IsDedicated()) Send(ZDOMan.GetSessionID(), folder);
        }

        private static void Send(long target, string folder)
        {
            long pid = Access.PlayerIdOfSender(target);
            var pkg = new ZPackage();
            pkg.Write(JournalOwners.IsLocked(folder));
            pkg.Write(Access.IsCreator(pid, folder));
            pkg.Write(Access.CanAddEntry(pid, folder));
            List<Entry> entries = Load(folder);
            pkg.Write(entries.Count);
            foreach (Entry e in entries)
            {
                pkg.Write(e.AuthorId); pkg.Write(e.AuthorName); pkg.Write(e.Time); pkg.Write(e.Text);
                pkg.Write(Access.CanEditEntry(pid, folder, e.AuthorId));
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcData, folder, pkg);
        }

        private static void OnData(long sender, string folder, ZPackage pkg)
        {
            var v = new View { Folder = folder, Locked = pkg.ReadBool(), IsCreator = pkg.ReadBool(), CanAdd = pkg.ReadBool() };
            int n = pkg.ReadInt();
            for (int i = 0; i < n; i++)
                v.Entries.Add(new Entry { AuthorId = pkg.ReadLong(), AuthorName = pkg.ReadString(), Time = pkg.ReadLong(), Text = RichText.Sanitize(pkg.ReadString()), CanEdit = pkg.ReadBool() });
            Arrived?.Invoke(v);
        }

        private static string NameOf(long sender)
        {
            if (sender == ZDOMan.GetSessionID()) return Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "?";
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            return peer != null ? peer.m_playerName : "?";
        }

        // ================= files =================

        private static string PathOf(string folder) => Path.Combine(BookStore.Root, folder.Replace('/', Path.DirectorySeparatorChar), "entries.txt");

        private static List<Entry> Load(string folder)
        {
            var list = new List<Entry>();
            try
            {
                string path = PathOf(folder);
                if (!File.Exists(path)) return list;
                foreach (string rec in File.ReadAllText(path, Encoding.UTF8).Split(new[] { EntrySep }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] f = rec.Split(new[] { FieldSep }, 4, StringSplitOptions.None);
                    if (f.Length < 4) continue;
                    list.Add(new Entry
                    {
                        AuthorId = long.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long a) ? a : 0L,
                        AuthorName = f[1],
                        Time = long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long t) ? t : 0L,
                        Text = f[3],
                    });
                }
            }
            catch (Exception e) { FiresLoreBooksPlugin.Log.LogWarning($"[LoreBooks] reading journal {folder} threw {e.Message}"); }
            return list;
        }

        private static void Save(string folder, List<Entry> entries)
        {
            try
            {
                string path = PathOf(folder);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var sb = new StringBuilder();
                foreach (Entry e in entries)
                    sb.Append(e.AuthorId.ToString(CultureInfo.InvariantCulture)).Append(FieldSep).Append(e.AuthorName.Replace(FieldSep, "").Replace(EntrySep, ""))
                      .Append(FieldSep).Append(e.Time.ToString(CultureInfo.InvariantCulture)).Append(FieldSep).Append(e.Text.Replace(EntrySep, "")).Append(EntrySep);
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception e) { FiresLoreBooksPlugin.Log.LogWarning($"[LoreBooks] writing journal {folder} threw {e.Message}"); }
        }
    }
}
