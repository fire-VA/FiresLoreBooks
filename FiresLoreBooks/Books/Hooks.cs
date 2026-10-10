using System;
using System.Collections.Generic;

namespace FiresLoreBooks.Books
{
    /// <summary>
    /// Reward hooks (design §2). A book's header lists lines like "GiveClassXP 200 once" or "QuestStart elder_trail once".
    /// When a player reads the book to its last page, the SERVER runs each hook once per player (Ledger), through a handler
    /// another mod registered under that verb (RPGClasses: GiveClassXP / RaiseSkill / GrantAttributePoints; RPGmaker:
    /// QuestStart / QuestAdvance / GrantAchievement). LoreBooks references neither mod: a verb nobody registered is logged
    /// and skipped. Handlers run on the server with the reader's player id and the hook's arguments.
    /// </summary>
    public static class Hooks
    {
        private static readonly Dictionary<string, Action<long, string[]>> s_handlers = new Dictionary<string, Action<long, string[]>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Register (or replace) the handler for a verb. Call from the other mod's Awake.</summary>
        public static void Register(string verb, Action<long, string[]> handler)
        {
            if (string.IsNullOrEmpty(verb) || handler == null) return;
            s_handlers[verb] = handler;
            FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] hook verb '{verb}' registered");
        }

        public static bool IsRegistered(string verb) => s_handlers.ContainsKey(verb ?? "");

        /// <summary>Parse "Verb arg1 arg2 [once]" → verb, args, once (default true: rewards are once per player).</summary>
        public static void Parse(string line, out string verb, out string[] args, out bool once)
        {
            string[] parts = (line ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            verb = parts.Length > 0 ? parts[0] : "";
            once = true;
            var list = new List<string>();
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].Equals("once", StringComparison.OrdinalIgnoreCase)) once = true;
                else if (parts[i].Equals("always", StringComparison.OrdinalIgnoreCase)) once = false;
                else list.Add(parts[i]);
            }
            args = list.ToArray();
        }

        /// <summary>Server: run one hook for a player. False when no mod handles the verb.</summary>
        internal static bool Run(long playerId, string verb, string[] args, string bookId)
        {
            if (!s_handlers.TryGetValue(verb, out var handler))
            {
                FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] '{bookId}' hook '{verb}': no mod handles it (skipped)");
                return false;
            }
            try { handler(playerId, args); }
            catch (Exception e) { FiresLoreBooksPlugin.Log.LogWarning($"[LoreBooks] '{bookId}' hook '{verb}' threw {e.GetType().Name}: {e.Message}"); return false; }
            FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] '{bookId}' hook '{verb} {string.Join(" ", args)}' ran for player {playerId}");
            return true;
        }
    }
}
