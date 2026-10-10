using UnityEngine;

namespace FiresLoreBooks.Books
{
    /// <summary>
    /// A journal placed on a lectern (Fire 10-09: "E on a placed journal" reads it; a locked journal is read-only for
    /// others, never hidden). Using your journal item on an empty lectern puts that journal on it (its folder goes into
    /// the ZDO); E opens the journal view for anyone. Rights inside the journal are the server's, as for a carried one.
    /// </summary>
    public sealed class JournalLecternPiece : MonoBehaviour, Hoverable, Interactable
    {
        internal const string ZdoJournal = "flb_journal";
        private ZNetView _nview;

        private void Awake() => _nview = GetComponent<ZNetView>();

        private string Folder => _nview != null && _nview.GetZDO() != null ? _nview.GetZDO().GetString(ZdoJournal) : "";

        public string GetHoverName() => "Journal lectern";

        public string GetHoverText() => Folder.Length == 0
            ? Localization.instance.Localize("Journal lectern\nUse a journal on it to place it here")
            : Localization.instance.Localize("Journal lectern\n[<color=yellow><b>$KEY_Use</b></color>] Read");

        public float GetHoverOffset() => 0f;

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold || user != Player.m_localPlayer) return false;
            if (Folder.Length == 0) { user.Message(MessageHud.MessageType.Center, "Use a journal on the lectern first."); return true; }
            UI.JournalView.Open(Folder);
            return true;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item)
        {
            if (user != Player.m_localPlayer || item?.m_dropPrefab == null || item.m_dropPrefab.name != LorePieces.JournalName) return false;
            if (!item.m_customData.TryGetValue(LorePieces.JournalKey, out string folder) || string.IsNullOrEmpty(folder))
            {
                user.Message(MessageHud.MessageType.Center, "Open the journal once first.");
                return true;
            }
            if (!_nview.IsOwner()) _nview.ClaimOwnership();
            _nview.GetZDO().Set(ZdoJournal, folder);
            user.Message(MessageHud.MessageType.Center, "Journal placed on the lectern.");
            FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] lectern now shows journal {folder}");
            return true;
        }
    }
}
