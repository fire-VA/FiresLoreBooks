using HarmonyLib;
using UnityEngine;

namespace FiresLoreBooks.Books
{
    /// <summary>
    /// A placed lore book (mockup 5, approved by Fire 10-09): tap E reads it in place, HOLD E takes a copy (server-checked,
    /// one per player per respawn; the book stays for everyone else), Shift+E edits it (admins). Its ZDO keeps a stable book
    /// id ("flb_id") given at placement; the text lives on the server in Books/&lt;id&gt;.txt.
    /// A tap and a hold start the same way, so the press arms a read that fires on release unless a hold arrives first.
    /// </summary>
    public sealed class LoreBookPiece : MonoBehaviour, Hoverable, Interactable
    {
        private const float TapWindow = 0.45f;

        private ZNetView _nview;
        private float _pressAt = -1f;
        private bool _tookThisHold;
        private string _title;

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
            if (_nview == null || _nview.GetZDO() == null) return;
            if (_nview.IsOwner() && string.IsNullOrEmpty(_nview.GetZDO().GetString(LorePieces.ZdoBookId)))
                _nview.GetZDO().Set(LorePieces.ZdoBookId, "book_" + System.Guid.NewGuid().ToString("N").Substring(0, 10));
        }

        public string BookId => _nview != null && _nview.GetZDO() != null ? _nview.GetZDO().GetString(LorePieces.ZdoBookId) : "";

        private string _titleFor;     // the id _title was fetched for (the id can change through the library's picker)
        private bool _blank;          // no text on the server yet

        /// <summary>
        /// Admin library (Fire 10-12): points this placed book at another book id. Claims the ZDO (vanilla's ownership
        /// rule for writing a ZDO) and writes flb_id; every client re-reads it on hover.
        /// </summary>
        internal bool SetBookId(string id)
        {
            if (_nview == null || _nview.GetZDO() == null || !BookStore.ValidId(id)) return false;
            if (!_nview.IsOwner()) _nview.ClaimOwnership();
            _nview.GetZDO().Set(LorePieces.ZdoBookId, id);
            _title = null;
            return true;
        }

        public string GetHoverName() => string.IsNullOrEmpty(_title) ? "Lore book" : _title;

        public string GetHoverText()
        {
            string id = BookId;
            if (id.Length > 0 && (_title == null || _titleFor != id))
            {
                _title = ""; _titleFor = id; _blank = false;
                BookStore.Get(id, d =>
                {
                    if (_titleFor != id) return;
                    _blank = d == null || (d.Title.Length == 0 && d.Pages.TrueForAll(p => string.IsNullOrEmpty(p?.Trim())));
                    _title = d != null && d.Title.Length > 0 ? d.Title : "Lore book";
                });
            }
            bool admin = UI.BookEditor.IsLocalAdmin();
            // Fire 10-12: a blank placed book tells admins how to write it; Shift+E opens the library as this book's picker.
            if (admin && _blank)
                return Localization.instance.Localize($"Blank lore book\n[<color=yellow><b>$KEY_AltPlace + $KEY_Use</b></color>] Write this book or pick one");
            string edit = admin ? "  [<color=yellow><b>$KEY_AltPlace + $KEY_Use</b></color>] Books" : "";
            return Localization.instance.Localize($"{GetHoverName()}\n[<color=yellow><b>$KEY_Use</b></color>] Read  [Hold <color=yellow><b>$KEY_Use</b></color>] Take a copy{edit}");
        }

        public float GetHoverOffset() => 0f;

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (user != Player.m_localPlayer || BookId.Length == 0) return false;
            if (alt && !hold) { UI.BookLibrary.OpenFor(this); return true; }
            if (!hold) { _pressAt = Time.time; _tookThisHold = false; return true; }
            if (_tookThisHold) return false;
            _tookThisHold = true;
            _pressAt = -1f;
            string id = BookId;
            Ledger.RequestTake(id, (bookId, ok, info) =>
            {
                Player p = Player.m_localPlayer;
                if (p == null) return;
                if (!ok) { p.Message(MessageHud.MessageType.Center, info); return; }
                ItemDrop.ItemData item = p.GetInventory().AddItem(LorePieces.ItemName, 1, 1, 0, 0L, "", false);
                if (item == null) { p.Message(MessageHud.MessageType.Center, "Your bag is full."); return; }
                item.m_customData[LorePieces.ItemBookKey] = bookId;
                p.Message(MessageHud.MessageType.TopLeft, $"Took a copy of {GetHoverName()}");
            });
            return true;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;

        private void Update()
        {
            if (_pressAt < 0f) return;
            // released before a hold arrived: it was a tap, so read
            if (!ZInput.GetButton("Use") && !ZInput.GetButton("JoyUse"))
            {
                if (Time.time - _pressAt <= TapWindow && !_tookThisHold) UI.BookReader.Open(BookId);
                _pressAt = -1f;
            }
            else if (Time.time - _pressAt > 2f) _pressAt = -1f;
        }
    }

    /// <summary>Using a taken lore book from the inventory opens it in the reader (it isn't consumed).</summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UseItem))]
    internal static class ReadCarriedBook
    {
        private static bool Prefix(Humanoid __instance, ItemDrop.ItemData item)
        {
            if (__instance != Player.m_localPlayer || item?.m_dropPrefab == null) return true;
            string prefab = item.m_dropPrefab.name;
            if (prefab == LorePieces.JournalName)
            {
                // a fresh journal gets its server folder on first use: "Journals/<creator player id>/<journal id>"
                if (!item.m_customData.TryGetValue(LorePieces.JournalKey, out string folder) || string.IsNullOrEmpty(folder))
                {
                    folder = JournalStore.FolderOf(Player.m_localPlayer.GetPlayerID(), "j" + System.Guid.NewGuid().ToString("N").Substring(0, 12));
                    item.m_customData[LorePieces.JournalKey] = folder;
                    FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] new journal {folder}");
                }
                UI.JournalView.Open(folder);
                return false;
            }
            if (prefab != LorePieces.ItemName) return true;
            if (item.m_customData.TryGetValue(LorePieces.ItemBookKey, out string id)) UI.BookReader.Open(id);
            return false;
        }
    }
}
