using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;

namespace FiresLoreBooks.Books
{
    /// <summary>
    /// Server-side per-player records (design §2, Fire 10-09):
    /// - TAKES: hold E on a placed book asks the server; one copy per player per respawn cycle (the book stays for others).
    ///   Collections/&lt;playerId&gt;.txt: "bookId|lastTakenSeconds" lines; also the player's lore collection.
    /// - REWARDS: reading a book to its last page asks the server to run its hooks; "once" hooks are recorded in
    ///   Claims/&lt;playerId&gt;.txt ("bookId|hook line"), so re-taking a respawned book never re-grants XP or quests.
    /// Times are ZNet seconds; "4d" = 4 game days (EnvMan day length).
    /// </summary>
    public static class Ledger
    {
        internal const string RpcTake = "FLB_Take";            // client -> server: bookId
        internal const string RpcTaken = "FLB_Taken";          // server -> client: bookId, ok, info (wait text or error)
        internal const string RpcFinished = "FLB_Finished";    // client -> server: bookId
        internal const string RpcCollGet = "FLB_CollGet";      // client -> server
        internal const string RpcColl = "FLB_Coll";            // server -> client: ZPackage (total, n, n x title)
        private static Action<int, List<string>> s_collDone;

        /// <summary>The player's lore collection: done(total books on the server, titles collected).</summary>
        public static void RequestCollection(Action<int, List<string>> done)
        {
            s_collDone = done;
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcCollGet);
        }

        private static void OnCollGet(long sender)
        {
            if (!IsServer) return;
            long pid = Access.PlayerIdOfSender(sender);
            var taken = ReadPairs(CollectionsDir, pid);
            int total = Directory.Exists(BookStore.BooksDir) ? Directory.GetFiles(BookStore.BooksDir, "*.txt").Length : 0;
            var pkg = new ZPackage();
            pkg.Write(total);
            pkg.Write(taken.Count);
            foreach (string id in taken.Keys.OrderBy(k => k))
            {
                string text = BookStore.ReadServerText(id);
                string title = text != null ? BookDoc.Parse(id, text).Title : "";
                pkg.Write(string.IsNullOrEmpty(title) ? id : title);
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcColl, pkg);
        }

        private static void OnColl(long sender, ZPackage pkg)
        {
            int total = pkg.ReadInt(), n = pkg.ReadInt();
            var titles = new List<string>();
            for (int i = 0; i < n; i++) titles.Add(pkg.ReadString());
            var d = s_collDone; s_collDone = null;
            d?.Invoke(total, titles);
        }

        /// <summary>Server: a player collected a book for the first time (playerId, bookId, how many they now have).</summary>
        public static event Action<long, string, int> LoreCollected;

        private static string CollectionsDir => Path.Combine(BookStore.Root, "Collections");
        private static string ClaimsDir => Path.Combine(BookStore.Root, "Claims");
        private static ZRoutedRpc s_registeredOn;
        private static Action<string, bool, string> s_takeDone;

        // ================= client =================

        /// <summary>Ask the server for a copy of a placed book; done(bookId, ok, info).</summary>
        public static void RequestTake(string bookId, Action<string, bool, string> done)
        {
            s_takeDone = done;
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcTake, bookId);
        }

        /// <summary>The reader reached a book's last page: the server runs its rewards (once per player where marked).</summary>
        public static void ReportFinished(string bookId) =>
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcFinished, bookId);

        // ================= RPCs =================

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class RegisterOnStart
        {
            private static void Postfix()
            {
                ZRoutedRpc rpc = ZRoutedRpc.instance;
                if (rpc == null || rpc == s_registeredOn) return;
                s_registeredOn = rpc;
                rpc.Register<string>(RpcTake, OnTake);
                rpc.Register<string, bool, string>(RpcTaken, (s, id, ok, info) => { var d = s_takeDone; s_takeDone = null; d?.Invoke(id, ok, info); });
                rpc.Register<string>(RpcFinished, OnFinished);
                rpc.Register(RpcCollGet, OnCollGet);
                rpc.Register<ZPackage>(RpcColl, OnColl);
            }
        }

        private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        private static void OnTake(long sender, string bookId)
        {
            if (!IsServer) return;
            long pid = Access.PlayerIdOfSender(sender);
            string info;
            bool ok = TryTake(pid, bookId, out info);
            FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] take '{bookId}' by player {pid}: {(ok ? "granted" : "refused, " + info)}");
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcTaken, bookId, ok, info);
        }

        private static bool TryTake(long pid, string bookId, out string info)
        {
            info = "";
            if (pid == 0L || !BookStore.ValidId(bookId)) { info = "unknown player or book"; return false; }
            var takes = ReadPairs(CollectionsDir, pid);
            double now = ZNet.instance.GetTimeSeconds();
            if (takes.TryGetValue(bookId, out string last) && double.TryParse(last, NumberStyles.Float, CultureInfo.InvariantCulture, out double at))
            {
                double respawn = RespawnSeconds(bookId);
                if (double.IsPositiveInfinity(respawn)) { info = "You already have this book."; return false; }
                if (now - at < respawn) { info = "Already taken, back in " + Span(respawn - (now - at)); return false; }
            }
            bool first = !takes.ContainsKey(bookId);
            takes[bookId] = now.ToString("0", CultureInfo.InvariantCulture);
            WritePairs(CollectionsDir, pid, takes);
            if (first) LoreCollected?.Invoke(pid, bookId, takes.Count);
            return true;
        }

        private static void OnFinished(long sender, string bookId)
        {
            if (!IsServer || !BookStore.ValidId(bookId)) return;
            long pid = Access.PlayerIdOfSender(sender);
            if (pid == 0L) return;
            string text = BookStore.ReadServerText(bookId);
            if (text == null) return;
            BookDoc doc = BookDoc.Parse(bookId, text);
            if (doc.Hooks.Count == 0) return;
            var claims = ReadLines(ClaimsDir, pid);
            bool changed = false;
            foreach (string hook in doc.Hooks)
            {
                Hooks.Parse(hook, out string verb, out string[] args, out bool once);
                string key = bookId + "|" + hook.Trim();
                if (once && claims.Contains(key)) continue;
                if (Hooks.Run(pid, verb, args, bookId) && once) { claims.Add(key); changed = true; }
            }
            if (changed) WriteLines(ClaimsDir, pid, claims);
        }

        // ================= respawn =================

        private static double RespawnSeconds(string bookId)
        {
            string text = BookStore.ReadServerText(bookId);
            string spec = text != null ? BookDoc.Parse(bookId, text).Respawn : "";
            if (string.IsNullOrEmpty(spec)) spec = FiresLoreBooksPlugin.DefaultRespawn?.Value ?? "4d";
            return ParseSpan(spec);
        }

        /// <summary>"4d" game days, "12h" game hours, "30m" game minutes, "never"; plain numbers are game days.</summary>
        internal static double ParseSpan(string spec)
        {
            spec = (spec ?? "").Trim().ToLowerInvariant();
            if (spec == "never") return double.PositiveInfinity;
            double day = EnvMan.instance != null ? EnvMan.instance.m_dayLengthSec : 1800.0;
            char unit = spec.Length > 0 ? spec[spec.Length - 1] : 'd';
            string num = char.IsLetter(unit) ? spec.Substring(0, spec.Length - 1) : spec;
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) v = 4;
            switch (unit)
            {
                case 'h': return v * day / 24.0;
                case 'm': return v * day / 1440.0;
                default: return v * day;
            }
        }

        private static string Span(double seconds)
        {
            double day = EnvMan.instance != null ? EnvMan.instance.m_dayLengthSec : 1800.0;
            double days = seconds / day;
            int d = (int)days, h = (int)((days - d) * 24);
            return d > 0 ? $"{d}d {h}h" : $"{Math.Max(1, h)}h";
        }

        // ================= files =================

        internal static int CollectedCount(long pid) => ReadPairs(CollectionsDir, pid).Count;

        private static Dictionary<string, string> ReadPairs(string dir, long pid)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in ReadLines(dir, pid))
            {
                int bar = line.IndexOf('|');
                if (bar > 0) map[line.Substring(0, bar)] = line.Substring(bar + 1);
            }
            return map;
        }

        private static void WritePairs(string dir, long pid, Dictionary<string, string> map) =>
            WriteLines(dir, pid, new HashSet<string>(map.Select(kv => kv.Key + "|" + kv.Value)));

        private static HashSet<string> ReadLines(string dir, long pid)
        {
            string path = Path.Combine(dir, pid.ToString(CultureInfo.InvariantCulture) + ".txt");
            try { return File.Exists(path) ? new HashSet<string>(File.ReadAllLines(path).Where(l => l.Trim().Length > 0)) : new HashSet<string>(); }
            catch (Exception e) { FiresLoreBooksPlugin.Log.LogWarning($"[LoreBooks] reading {path} threw {e.Message}"); return new HashSet<string>(); }
        }

        private static void WriteLines(string dir, long pid, HashSet<string> lines)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllLines(Path.Combine(dir, pid.ToString(CultureInfo.InvariantCulture) + ".txt"), lines.OrderBy(l => l));
            }
            catch (Exception e) { FiresLoreBooksPlugin.Log.LogWarning($"[LoreBooks] writing the {Path.GetFileName(dir)} ledger threw {e.Message}"); }
        }
    }
}
