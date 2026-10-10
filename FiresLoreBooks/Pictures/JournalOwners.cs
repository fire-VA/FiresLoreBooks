using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;

namespace FiresPictures
{
    /// <summary>
    /// Who may write a journal ([lead] 10-09: never trust a client's claim). The server records the owner in
    /// &lt;config&gt;/FiresLoreBooks/Journals/&lt;creatorId&gt;/&lt;journalId&gt;/owner.txt; with no file the creator owns it. Only the
    /// owner or an admin may hand it on, with Give (an explicit RPC the server checks). The new owner then writes and the old one
    /// reads. Everyone else is read-only. Denials are logged at Debug.
    /// </summary>
    public static class JournalOwners
    {
        private const string RpcGive = "FLB_JournalGive", RpcGiveAnswer = "FLB_JournalGiveAnswer";
        private const string OwnerFile = "owner.txt";

        private static ZRoutedRpc s_registeredOn;
        private static readonly Dictionary<string, Action<string>> s_pending = new Dictionary<string, Action<string>>();
        private static int s_seq;

        /// <summary>Client: hands the journal in this folder ("Journals/&lt;creator&gt;/&lt;journal&gt;") to an online player.
        /// done gets "" on success, else "denied" / "no_player" / "timeout".</summary>
        public static void Give(string folder, string toPlayerName, Action<string> done)
        {
            if (ZRoutedRpc.instance == null || ZNet.instance == null || !Store.ValidFolder(folder) || !folder.StartsWith("Journals/"))
            { done?.Invoke("denied"); return; }
            string req = $"{ZDOMan.GetSessionID()}_{++s_seq}";
            s_pending[req] = done;
            long server = ZNet.instance.IsServer() ? ZDOMan.GetSessionID() : (ZNet.instance.GetServerPeer()?.m_uid ?? 0L);
            ZRoutedRpc.instance.InvokeRoutedRPC(server, RpcGive, req, folder, toPlayerName ?? "");
        }

        // ── lock (Fire 10-09): journal.meta beside the journal; a new journal (no file) is LOCKED ──

        private const string MetaFile = "journal.meta";
        private const string RpcLock = "FLB_JournalLock", RpcLockAnswer = "FLB_JournalLockAnswer";

        /// <summary>Client: the creator or an admin locks or unlocks the journal. done gets "" on success, else "denied" / "timeout".</summary>
        public static void SetLocked(string folder, bool locked, Action<string> done)
        {
            if (ZRoutedRpc.instance == null || ZNet.instance == null || !Store.ValidFolder(folder) || !folder.StartsWith("Journals/"))
            { done?.Invoke("denied"); return; }
            string req = $"{ZDOMan.GetSessionID()}_{++s_seq}";
            s_pending[req] = done;
            long server = ZNet.instance.IsServer() ? ZDOMan.GetSessionID() : (ZNet.instance.GetServerPeer()?.m_uid ?? 0L);
            ZRoutedRpc.instance.InvokeRoutedRPC(server, RpcLock, req, folder, locked);
        }

        /// <summary>Server: is the journal locked? (No journal.meta yet: locked.)</summary>
        public static bool IsLocked(string folder)
        {
            try
            {
                string path = Path.Combine(Dir(folder), MetaFile);
                return !File.Exists(path) || !File.ReadAllText(path).Contains("locked=0");
            }
            catch { return true; }
        }

        private static void OnLock(long sender, string req, string folder, bool locked)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            long pid = FiresLoreBooks.Access.PlayerIdOfSender(sender);
            string result = "denied";
            if (Store.ValidFolder(folder) && folder.StartsWith("Journals/") && FiresLoreBooks.Access.IsCreator(pid, folder))
            {
                try
                {
                    Directory.CreateDirectory(Dir(folder));
                    File.WriteAllText(Path.Combine(Dir(folder), MetaFile), locked ? "locked=1" : "locked=0");
                    FiresLoreBooks.FiresLoreBooksPlugin.Log?.LogDebug($"[Pictures] {pid} {(locked ? "locked" : "unlocked")} {folder}");
                    result = "";
                }
                catch (Exception ex) { FiresLoreBooks.FiresLoreBooksPlugin.Log?.LogWarning($"[Pictures] saving the lock of {folder} failed: {ex.Message}"); }
            }
            else FiresLoreBooks.FiresLoreBooksPlugin.Log?.LogDebug($"[Pictures] {pid} may not lock/unlock {folder}");
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcLockAnswer, req, result);
        }

        /// <summary>Server: the player id that may write this journal.</summary>
        internal static long OwnerOf(long creatorId, string folder)
        {
            try
            {
                string path = Path.Combine(Dir(folder), OwnerFile);
                if (File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long owner))
                    return owner;
            }
            catch { }
            return creatorId;
        }

        /// <summary>Server: may this player write the journal (its recorded owner)? Denials are logged at Debug.</summary>
        internal static bool MayWrite(long playerId, long creatorId, string folder)
        {
            long owner = OwnerOf(creatorId, folder);
            if (owner == playerId) return true;
            FiresLoreBooks.FiresLoreBooksPlugin.Log?.LogDebug($"[Pictures] {playerId} may not write {folder} (owner {owner})");
            return false;
        }

        private static string Dir(string folder) =>
            Path.Combine(BepInEx.Paths.ConfigPath, "FiresLoreBooks", folder.Replace('/', Path.DirectorySeparatorChar));

        private static void OnGive(long sender, string req, string folder, string toName)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            string result;
            long from = FiresLoreBooks.Access.PlayerIdOfSender(sender);
            string[] parts = (folder ?? "").Split('/');
            if (!Store.ValidFolder(folder) || parts.Length < 3 || parts[0] != "Journals" || !long.TryParse(parts[1], out long creator)
                || !FiresLoreBooks.Access.CanWrite(from, folder))
                result = "denied";
            else
            {
                long to = ToPlayerId(toName);
                if (to == 0L) result = "no_player";
                else
                {
                    try
                    {
                        Directory.CreateDirectory(Dir(folder));
                        File.WriteAllText(Path.Combine(Dir(folder), OwnerFile), to.ToString(CultureInfo.InvariantCulture));
                        FiresLoreBooks.FiresLoreBooksPlugin.Log?.LogDebug($"[Pictures] {from} gave {folder} to {to} ({toName})");
                        result = "";
                    }
                    catch (Exception ex)
                    {
                        FiresLoreBooks.FiresLoreBooksPlugin.Log?.LogWarning($"[Pictures] recording the owner of {folder} failed: {ex.Message}");
                        result = "denied";
                    }
                }
            }
            if (result == "denied") FiresLoreBooks.FiresLoreBooksPlugin.Log?.LogDebug($"[Pictures] {from} may not give {folder}");
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcGiveAnswer, req, result);
        }

        // an online player, by name (the server's peer list), or this host's own player
        private static long ToPlayerId(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0L;
            Player local = Player.m_localPlayer;
            if (local != null && local.GetPlayerName() == name) return local.GetPlayerID();
            ZNetPeer peer = ZNet.instance.GetPeers().FirstOrDefault(p => p != null && p.m_playerName == name);
            return FiresLoreBooks.Access.PlayerIdOf(peer);
        }

        private static void OnGiveAnswer(long sender, string req, string result)
        {
            if (!s_pending.TryGetValue(req, out Action<string> done)) return;
            s_pending.Remove(req);
            done?.Invoke(result);
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class RegisterOnStart
        {
            private static void Postfix()
            {
                ZRoutedRpc rpc = ZRoutedRpc.instance;
                if (rpc == null || rpc == s_registeredOn) return;
                s_registeredOn = rpc;
                rpc.Register<string, string, string>(RpcGive, OnGive);
                rpc.Register<string, string>(RpcGiveAnswer, OnGiveAnswer);
                rpc.Register<string, string, bool>(RpcLock, OnLock);
                rpc.Register<string, string>(RpcLockAnswer, OnGiveAnswer);
            }
        }
    }
}
