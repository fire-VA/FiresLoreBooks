using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;

namespace FiresLoreBooks.Books
{
    /// <summary>
    /// Server-authoritative book text (design §2). The server's &lt;config&gt;/FiresLoreBooks/Books/&lt;id&gt;.txt files are the
    /// truth. A FileSystemWatcher picks up edits made outside the game and bumps that book's revision; clients cache
    /// by revision and refetch when a "rev changed" broadcast arrives. Admin saves from the in-game editor are checked
    /// and sanitised on the server, then written to the same file.
    /// </summary>
    public static class BookStore
    {
        internal const string RpcGet = "FLB_BookGet";        // client -> server: id, knownRev
        internal const string RpcBook = "FLB_Book";          // server -> client: id, rev, text ("" = unchanged)
        internal const string RpcSave = "FLB_BookSave";      // client -> server: id, full text
        internal const string RpcSaved = "FLB_BookSaved";    // server -> client: id, error ("" = ok)
        internal const string RpcRev = "FLB_BookRev";        // server -> everybody: id, rev

        public static string Root => Path.Combine(BepInEx.Paths.ConfigPath, "FiresLoreBooks");
        public static string BooksDir => Path.Combine(Root, "Books");

        /// <summary>Raised on the client when a book's text arrives or changes (id).</summary>
        public static event Action<string> Changed;

        // ---- server state ----
        private static readonly Dictionary<string, int> s_revs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentQueue<string> s_touched = new ConcurrentQueue<string>();
        private static FileSystemWatcher s_watcher;
        private static readonly HashSet<string> s_ownWrites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ---- client state ----
        private static readonly Dictionary<string, (int rev, BookDoc doc)> s_cache = new Dictionary<string, (int, BookDoc)>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<Action<BookDoc>>> s_waiting = new Dictionary<string, List<Action<BookDoc>>>(StringComparer.OrdinalIgnoreCase);
        private static Action<string> s_saveDone;
        private static ZRoutedRpc s_registeredOn;

        private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        /// <summary>A book id is a plain file name: letters, digits, '_' and '-' only (no paths).</summary>
        public static bool ValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
            foreach (char c in id) if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) return false;
            return true;
        }

        // ================= client API =================

        /// <summary>The book's current text (from the cache when its revision is current, else fetched from the server).</summary>
        public static void Get(string id, Action<BookDoc> done)
        {
            if (!ValidId(id)) { done?.Invoke(null); return; }
            if (s_cache.TryGetValue(id, out var hit) && hit.doc != null && hit.rev >= 0) done?.Invoke(hit.doc);
            if (!s_waiting.TryGetValue(id, out var list)) s_waiting[id] = list = new List<Action<BookDoc>>();
            if (!(s_cache.TryGetValue(id, out hit) && hit.doc != null)) list.Add(done);
            int known = s_cache.TryGetValue(id, out hit) ? hit.rev : -1;
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcGet, id, known);
        }

        /// <summary>Admin: save a book. The server checks admin, sanitises and writes; done(error) with "" on success.</summary>
        public static void Save(BookDoc doc, Action<string> done)
        {
            s_saveDone = done;
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcSave, doc.Id, doc.Serialize());
        }

        // ================= RPCs =================

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class RegisterOnStart
        {
            private static void Postfix() => Register();
        }

        private static void Register()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || rpc == s_registeredOn) return;
            s_registeredOn = rpc;
            rpc.Register<string, int>(RpcGet, OnGet);
            rpc.Register<string, int, string>(RpcBook, OnBook);
            rpc.Register<string, string>(RpcSave, OnSave);
            rpc.Register<string, string>(RpcSaved, (s, id, err) => { var d = s_saveDone; s_saveDone = null; d?.Invoke(err); });
            rpc.Register<string, int>(RpcRev, OnRev);
            if (IsServer) StartWatcher();
        }

        private static void OnGet(long sender, string id, int knownRev)
        {
            if (!IsServer || !ValidId(id)) return;
            int rev = RevOf(id);
            string text = rev == knownRev ? "" : ReadFile(id) ?? "";
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcBook, id, rev, text);
        }

        private static void OnBook(long sender, string id, int rev, string text)
        {
            BookDoc doc;
            if (text.Length == 0 && s_cache.TryGetValue(id, out var hit) && hit.rev == rev) doc = hit.doc;
            else
            {
                doc = BookDoc.Parse(id, text);
                for (int p = 0; p < doc.Pages.Count; p++) doc.Pages[p] = RichText.Sanitize(doc.Pages[p]);
                s_cache[id] = (rev, doc);
            }
            if (s_waiting.TryGetValue(id, out var list)) { s_waiting.Remove(id); foreach (var cb in list) cb?.Invoke(doc); }
            Changed?.Invoke(id);
        }

        private static void OnSave(long sender, string id, string text)
        {
            if (!IsServer) return;
            string err = "";
            if (!ValidId(id)) err = "bad id";
            else if (!FiresCore.Lifecycle.AdminCheck.IsAdmin(sender)) err = "admin only";
            else
            {
                BookDoc doc = BookDoc.Parse(id, text);
                for (int p = 0; p < doc.Pages.Count; p++) doc.Pages[p] = RichText.Sanitize(doc.Pages[p]);
                try
                {
                    Directory.CreateDirectory(BooksDir);
                    lock (s_ownWrites) s_ownWrites.Add(id);
                    File.WriteAllText(PathOf(id), doc.Serialize());
                    Bump(id);
                }
                catch (Exception e) { err = "write failed"; FiresLoreBooksPlugin.Log.LogWarning($"[LoreBooks] saving '{id}' threw {e.GetType().Name}: {e.Message}"); }
            }
            FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] save '{id}' from {sender}: {(err.Length == 0 ? "written" : err)}");
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcSaved, id, err);
        }

        private static void OnRev(long sender, string id, int rev)
        {
            if (s_cache.TryGetValue(id, out var hit) && hit.rev == rev) return;
            s_cache.Remove(id);
            Changed?.Invoke(id);   // an open reader refetches
        }

        // ================= server files =================

        private static string PathOf(string id) => Path.Combine(BooksDir, id + ".txt");

        /// <summary>Server only: the book's raw text file, or null.</summary>
        internal static string ReadServerText(string id) => ValidId(id) ? ReadFile(id) : null;

        private static string ReadFile(string id)
        {
            try { string p = PathOf(id); return File.Exists(p) ? File.ReadAllText(p) : null; }
            catch (Exception e) { FiresLoreBooksPlugin.Log.LogWarning($"[LoreBooks] reading '{id}' threw {e.GetType().Name}: {e.Message}"); return null; }
        }

        private static int RevOf(string id)
        {
            if (!s_revs.TryGetValue(id, out int rev)) s_revs[id] = rev = 1;
            return rev;
        }

        private static void Bump(string id)
        {
            int rev = RevOf(id) + 1;
            s_revs[id] = rev;
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcRev, id, rev);
            FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] '{id}' is now rev {rev}");
        }

        private static void StartWatcher()
        {
            if (s_watcher != null) return;
            try
            {
                Directory.CreateDirectory(BooksDir);
                s_watcher = new FileSystemWatcher(BooksDir, "*.txt") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                FileSystemEventHandler touched = (o, e) => s_touched.Enqueue(Path.GetFileNameWithoutExtension(e.Name));
                s_watcher.Changed += touched;
                s_watcher.Created += touched;
                s_watcher.Deleted += touched;
                s_watcher.Renamed += (o, e) => { s_touched.Enqueue(Path.GetFileNameWithoutExtension(e.OldName)); s_touched.Enqueue(Path.GetFileNameWithoutExtension(e.Name)); };
                s_watcher.EnableRaisingEvents = true;
                FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] watching {BooksDir} for edits made outside the game");
            }
            catch (Exception e) { FiresLoreBooksPlugin.Log.LogWarning($"[LoreBooks] file watcher failed: {e.Message}"); }
        }

        /// <summary>Main thread, every frame (plugin Update): turn watcher events into revision bumps.</summary>
        internal static void Tick()
        {
            if (s_touched.IsEmpty) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (s_touched.TryDequeue(out string id))
            {
                if (!ValidId(id) || !seen.Add(id)) continue;
                lock (s_ownWrites) if (s_ownWrites.Remove(id)) continue;   // our own save already bumped it
                if (IsServer) Bump(id);
            }
        }
    }
}
