using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace FiresPictures
{
    /// <summary>
    /// Pictures for lore books and journals (Tools\FIRESPICTURES_API.md). The server holds the files at
    /// &lt;config&gt;/FiresLoreBooks/&lt;folder&gt;/&lt;picFile&gt;; clients upload and fetch them in chunks over routed RPCs and keep a copy
    /// per content hash under BepInEx/cache/FiresPictures, so a picture already seen is never downloaded again. A .jpg dropped into a
    /// folder by hand is served too: the server hashes the file on every request.
    /// </summary>
    public static class Store
    {
        private const string RpcUpBegin = "FLB_PicUpBegin", RpcUpChunk = "FLB_PicUpChunk", RpcUpDone = "FLB_PicUpDone";
        private const string RpcGet = "FLB_PicGet", RpcGetMeta = "FLB_PicGetMeta", RpcGetChunk = "FLB_PicGetChunk";
        private const string RpcDelete = "FLB_PicDelete", RpcCount = "FLB_PicCount", RpcCountAnswer = "FLB_PicCountAnswer";
        private const int ChunkBytes = 16 * 1024;
        private const int MaxPictureBytes = 256 * 1024;
        private const int JpgQuality = 75;
        private const float TimeoutSeconds = 30f;
        public const int PerJournalCap = 24;
        public const long PerPlayerCapBytes = 20L * 1024 * 1024;

        private static readonly Regex FolderRx = new Regex(@"^(Books/[A-Za-z0-9_\-]{1,64}|Journals/-?\d{1,20}/[A-Za-z0-9_\-]{1,64})$");
        private static readonly Regex FileRx = new Regex(@"^[A-Za-z0-9_\-]{1,64}\.jpg$");

        /// <summary>Raised on a client when a fetch completes (folder, picFile), so an open page can redraw.</summary>
        public static event Action<string, string> Arrived;

        /// <summary>For tests: Gets answered from the local cache (the server said the hash is unchanged) and Gets that downloaded.</summary>
        public static int CacheHits { get; private set; }
        public static int Downloads { get; private set; }

        private static readonly Dictionary<string, int> s_counts = new Dictionary<string, int>();
        private static int s_seq;
        private static ZRoutedRpc s_registeredOn;

        // ── client API ──

        /// <summary>Encodes the texture as JPG (quality 75) and uploads it. done gets the stored file name; failed gets one of
        /// cap_journal, cap_player, denied, timeout, too_big.</summary>
        public static void Upload(string folder, Texture2D tex, Action<string> done, Action<string> failed)
        {
            if (!FolderRx.IsMatch(folder ?? "") || tex == null) { failed?.Invoke("denied"); return; }
            byte[] jpg = tex.EncodeToJPG(JpgQuality);
            if (jpg.Length > MaxPictureBytes) { failed?.Invoke("too_big"); return; }
            if (!Ready()) { failed?.Invoke("timeout"); return; }
            string req = NextRequest();
            Pending.Uploads[req] = new PendingUpload { Done = done, Failed = failed, Started = Time.realtimeSinceStartup };
            ZRoutedRpc.instance.InvokeRoutedRPC(ServerUid(), RpcUpBegin, req, folder, jpg.Length);
            for (int i = 0, n = 0; i < jpg.Length; i += ChunkBytes, n++)
            {
                byte[] chunk = new byte[Math.Min(ChunkBytes, jpg.Length - i)];
                Buffer.BlockCopy(jpg, i, chunk, 0, chunk.Length);
                ZRoutedRpc.instance.InvokeRoutedRPC(ServerUid(), RpcUpChunk, req, n, new ZPackage(chunk));
            }
            Cache.Put(Hash(jpg), jpg);
        }

        /// <summary>The picture as a texture: from the local cache when the server's copy has the same hash, else downloaded.
        /// done(null) when it doesn't exist.</summary>
        public static void Get(string folder, string picFile, Action<Texture2D> done)
        {
            if (!FolderRx.IsMatch(folder ?? "") || !FileRx.IsMatch(picFile ?? "")) { done?.Invoke(null); return; }
            if (!Ready()) { done?.Invoke(null); return; }
            string req = NextRequest();
            Pending.Gets[req] = new PendingGet { Folder = folder, File = picFile, Done = done, Started = Time.realtimeSinceStartup };
            ZRoutedRpc.instance.InvokeRoutedRPC(ServerUid(), RpcGet, req, folder, picFile, Cache.KnownHash(folder, picFile) ?? "");
        }

        /// <summary>Removes the picture on the server (the journal's holder or an admin; Books: admins).</summary>
        public static void Delete(string folder, string picFile)
        {
            if (!FolderRx.IsMatch(folder ?? "") || !FileRx.IsMatch(picFile ?? "") || !Ready()) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(ServerUid(), RpcDelete, folder, picFile);
        }

        /// <summary>How many pictures the folder holds, as last reported by the server (-1 until known); asks for a fresh count.</summary>
        public static int Count(string folder)
        {
            if (FolderRx.IsMatch(folder ?? "") && Ready()) ZRoutedRpc.instance.InvokeRoutedRPC(ServerUid(), RpcCount, folder);
            return s_counts.TryGetValue(folder ?? "", out int n) ? n : -1;
        }

        // ── RPC registration ──

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class RegisterOnStart { private static void Postfix() => RegisterRpcs(); }

        private static void RegisterRpcs()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || rpc == s_registeredOn) return;
            s_registeredOn = rpc;
            rpc.Register<string, string, int>(RpcUpBegin, Server.OnUploadBegin);
            rpc.Register<string, int, ZPackage>(RpcUpChunk, Server.OnUploadChunk);
            rpc.Register<string, string, string>(RpcUpDone, OnUploadDone);
            rpc.Register<string, string, string, string>(RpcGet, Server.OnGet);
            rpc.Register<string, string, int>(RpcGetMeta, OnGetMeta);
            rpc.Register<string, int, ZPackage>(RpcGetChunk, OnGetChunk);
            rpc.Register<string, string>(RpcDelete, Server.OnDelete);
            rpc.Register<string>(RpcCount, Server.OnCount);
            rpc.Register<string, int>(RpcCountAnswer, (sender, folder, n) => s_counts[folder] = n);
        }

        // ── client side of the replies ──

        private static void OnUploadDone(long sender, string req, string picFile, string error)
        {
            if (!Pending.Uploads.TryGetValue(req, out PendingUpload up)) return;
            Pending.Uploads.Remove(req);
            if (string.IsNullOrEmpty(error)) up.Done?.Invoke(picFile);
            else up.Failed?.Invoke(error);
        }

        // hash "" = no such picture; total 0 with a hash = the cached copy is current
        private static void OnGetMeta(long sender, string req, string hash, int total)
        {
            if (!Pending.Gets.TryGetValue(req, out PendingGet get)) return;
            if (string.IsNullOrEmpty(hash)) { Finish(req, get, null); return; }
            get.Hash = hash;
            if (total == 0) { CacheHits++; Finish(req, get, Cache.Read(hash)); return; }
            get.Buffer = new byte[total];
            get.Chunks = (total + ChunkBytes - 1) / ChunkBytes;
        }

        private static void OnGetChunk(long sender, string req, int index, ZPackage pkg)
        {
            if (!Pending.Gets.TryGetValue(req, out PendingGet get) || get.Buffer == null) return;
            byte[] chunk = pkg.GetArray();
            int at = index * ChunkBytes;
            if (at < 0 || at + chunk.Length > get.Buffer.Length) return;
            Buffer.BlockCopy(chunk, 0, get.Buffer, at, chunk.Length);
            if (++get.Received < get.Chunks) return;
            if (Hash(get.Buffer) != get.Hash) { Finish(req, get, null); return; }
            Cache.Put(get.Hash, get.Buffer);
            Downloads++;
            Finish(req, get, get.Buffer);
        }

        private static void Finish(string req, PendingGet get, byte[] jpg)
        {
            Pending.Gets.Remove(req);
            if (jpg != null && get.Hash != null) Cache.Remember(get.Folder, get.File, get.Hash);
            Texture2D tex = null;
            if (jpg != null)
            {
                tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (!Decode(tex, jpg)) { UnityEngine.Object.Destroy(tex); tex = null; }
            }
            get.Done?.Invoke(tex);
            if (tex != null) Arrived?.Invoke(get.Folder, get.File);
        }

        /// <summary>Times out requests the server never answered (called from the plugin's Update).</summary>
        internal static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            foreach (var kv in Pending.Uploads.Where(kv => now - kv.Value.Started > TimeoutSeconds).ToList())
            { Pending.Uploads.Remove(kv.Key); kv.Value.Failed?.Invoke("timeout"); }
            foreach (var kv in Pending.Gets.Where(kv => now - kv.Value.Started > TimeoutSeconds).ToList())
                Finish(kv.Key, kv.Value, null);
        }

        // ── helpers ──

        // ImageConversion.LoadImage(Texture2D, byte[], bool): bound by reflection, because netstandard 2.1 adds a ReadOnlySpan overload
        // this net48 build cannot compile against (CS0518).
        private static readonly Func<Texture2D, byte[], bool, bool> s_loadImage = AccessTools.MethodDelegate<Func<Texture2D, byte[], bool, bool>>(
            AccessTools.Method(typeof(ImageConversion), "LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) }));

        private static bool Decode(Texture2D tex, byte[] jpg) => s_loadImage != null && s_loadImage(tex, jpg, false);

        private static bool Ready() => ZRoutedRpc.instance != null && ZNet.instance != null;

        private static long ServerUid() =>
            ZNet.instance.IsServer() ? ZDOMan.GetSessionID() : (ZNet.instance.GetServerPeer()?.m_uid ?? 0L);

        private static string NextRequest() => $"{ZDOMan.GetSessionID()}_{++s_seq}";

        internal static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data), 0, 8).Replace("-", "").ToLowerInvariant();
        }

        internal static bool ValidFolder(string folder) => FolderRx.IsMatch(folder ?? "");
        internal static bool ValidFile(string file) => FileRx.IsMatch(file ?? "");

        internal sealed class PendingUpload { internal Action<string> Done; internal Action<string> Failed; internal float Started; }

        internal sealed class PendingGet
        {
            internal string Folder, File, Hash;
            internal Action<Texture2D> Done;
            internal float Started;
            internal byte[] Buffer;
            internal int Chunks, Received;
        }

        private static class Pending
        {
            internal static readonly Dictionary<string, PendingUpload> Uploads = new Dictionary<string, PendingUpload>();
            internal static readonly Dictionary<string, PendingGet> Gets = new Dictionary<string, PendingGet>();
        }

        internal const int Chunk = ChunkBytes;
        internal const int MaxBytes = MaxPictureBytes;
        internal const string UpDone = RpcUpDone, GetMeta = RpcGetMeta, GetChunk = RpcGetChunk, CountAnswer = RpcCountAnswer;
    }
}
