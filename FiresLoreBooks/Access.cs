namespace FiresLoreBooks
{
    /// <summary>
    /// Server-side rights for a FiresLoreBooks folder (Tools\FIRESPICTURES_API.md; Fire's journal rule, 0.0.3).
    /// "Books/&lt;id&gt;": admins only. "Journals/&lt;creatorId&gt;/&lt;journalId&gt;": its CREATOR is the server-recorded owner (the
    /// player who made it until a Give hands it on; FiresPictures.JournalOwners). The creator and admins may do anything and
    /// lock / unlock it. A LOCKED journal (the default) is read-only for everyone else. An UNLOCKED one lets anyone ADD entries and
    /// pictures, and change or delete their OWN, but not other people's. Readers only fetch, which needs no right. Denials are logged at Debug.
    /// </summary>
    public static class Access
    {
        /// <summary>Fire 10-09: in an UNLOCKED journal an entry's own author may still change or delete it; only other people's
        /// older entries are protected. In a locked one only the creator and admins write.</summary>
        internal const bool AuthorsKeepTheirOwn = true;

        /// <summary>True for the journal's creator (its recorded owner) and admins; admins only for Books. Call on the server.</summary>
        public static bool IsCreator(long playerId, string folder)
        {
            if (string.IsNullOrEmpty(folder) || playerId == 0L) return false;
            if (IsAdmin(playerId)) return true;
            return TryJournal(folder, out long creator) && FiresPictures.JournalOwners.OwnerOf(creator, folder) == playerId;
        }

        /// <summary>May this player add a new entry or picture? The creator and admins always; anyone while the journal is unlocked.</summary>
        public static bool CanAddEntry(long playerId, string folder)
        {
            if (IsCreator(playerId, folder)) return true;
            bool open = playerId != 0L && TryJournal(folder, out _) && !FiresPictures.JournalOwners.IsLocked(folder);
            if (!open) Deny(playerId, "add to", folder);
            return open;
        }

        /// <summary>May this player change or delete an existing entry or picture written by entryAuthorId? The creator and admins.</summary>
        public static bool CanEditEntry(long playerId, string folder, long entryAuthorId)
        {
            if (IsCreator(playerId, folder)) return true;
            bool own = AuthorsKeepTheirOwn && playerId != 0L && playerId == entryAuthorId && TryJournal(folder, out _)
                && !FiresPictures.JournalOwners.IsLocked(folder);
            if (!own) Deny(playerId, $"edit an entry by {entryAuthorId} in", folder);
            return own;
        }

        /// <summary>The 0.0.2 check, kept for callers that write as the creator (Give, lock).</summary>
        public static bool CanWrite(long playerId, string folder)
        {
            bool ok = IsCreator(playerId, folder);
            if (!ok) Deny(playerId, "write", folder);
            return ok;
        }

        private static bool TryJournal(string folder, out long creator)
        {
            creator = 0L;
            if (folder == null || !folder.StartsWith("Journals/", System.StringComparison.Ordinal)) return false;
            string[] parts = folder.Split('/');
            return parts.Length >= 3 && long.TryParse(parts[1], out creator);
        }

        private static void Deny(long playerId, string what, string folder) =>
            FiresLoreBooksPlugin.Log?.LogDebug($"[Pictures] {playerId} may not {what} {folder}");

        // 0.0.2 ([fgn]): the peer is found by its character's player id (ZDOVars.s_playerID). The old test compared the peer's
        // session uid with the player id, which never match, so no admin passed on a dedicated server.
        private static bool IsAdmin(long playerId)
        {
            if (ZNet.instance == null) return false;
            Player local = Player.m_localPlayer;
            if (local != null && local.GetPlayerID() == playerId) return ZNet.instance.LocalPlayerIsAdminOrHost();
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                if (peer != null && PlayerIdOf(peer) == playerId)
                    return ZNet.instance.IsAdmin(peer.m_rpc.GetSocket().GetHostName());
            return false;
        }

        /// <summary>The game player id of the character this peer plays (0 before it spawned).</summary>
        internal static long PlayerIdOf(ZNetPeer peer)
        {
            if (peer == null || peer.m_characterID.IsNone() || ZDOMan.instance == null) return 0L;
            ZDO zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
            return zdo != null ? zdo.GetLong(ZDOVars.s_playerID, 0L) : 0L;
        }

        /// <summary>The player id behind a routed-RPC sender (this machine's own player on a host).</summary>
        public static long PlayerIdOfSender(long sender)
        {
            if (ZNet.instance == null) return 0L;
            if (sender == ZDOMan.GetSessionID()) return Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;
            return PlayerIdOf(ZNet.instance.GetPeer(sender));
        }
    }
}
