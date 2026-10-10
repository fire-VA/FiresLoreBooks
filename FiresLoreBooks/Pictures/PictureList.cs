using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FiresPictures
{
    /// <summary>
    /// The editor's two picture buttons (mockup 2: "Image" and "Shot"). Both hand back a new ImgSpec (centred, half the page wide,
    /// text around it) for [classes-ui]'s editor to insert at the caret.
    /// - OnImage: a picker of the .jpg files in the book's folder on the server (files dropped in by hand included).
    /// - OnShot: camera mode (4:3), then the upload into the folder.
    /// </summary>
    public static class PictureList
    {
        private const string RpcList = "FLB_PicList", RpcListAnswer = "FLB_PicListAnswer";
        private static readonly Dictionary<string, Action<string[]>> s_pending = new Dictionary<string, Action<string[]>>();
        private static ZRoutedRpc s_registeredOn;
        private static int s_seq;

        /// <summary>Opens the picker for this folder; picked(spec) runs with the chosen picture, nothing on Cancel.</summary>
        public static void OnImage(string folder, Action<ImgSpec> picked)
        {
            List(folder, files =>
            {
                if (files == null || files.Length == 0) { Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "No pictures in this book's folder yet"); return; }
                Picker.Open(files, file => picked?.Invoke(file != null ? NewSpec(file) : null));
            });
        }

        /// <summary>Camera mode, then the upload. done(spec) on success; failed(code) on a cap / denial / timeout, "cancelled" on Cancel.</summary>
        public static void OnShot(string folder, Action<ImgSpec> done, Action<string> failed = null)
        {
            Camera.Begin("4:3", tex => Store.Upload(folder, tex, file => done?.Invoke(NewSpec(file)), failed), () => failed?.Invoke("cancelled"));
        }

        private static ImgSpec NewSpec(string file) => new ImgSpec { Src = file, X = 0.25f, Y = 0.15f, W = 0.5f, WrapAround = true };

        /// <summary>The .jpg file names in a folder on the server (sorted), or null when the server didn't answer in 10 s.</summary>
        public static void List(string folder, Action<string[]> done)
        {
            if (!Store.ValidFolder(folder) || ZRoutedRpc.instance == null || ZNet.instance == null) { done?.Invoke(null); return; }
            string req = $"{ZDOMan.GetSessionID()}_{++s_seq}";
            s_pending[req] = done;
            long server = ZNet.instance.IsServer() ? ZDOMan.GetSessionID() : (ZNet.instance.GetServerPeer()?.m_uid ?? 0L);
            ZRoutedRpc.instance.InvokeRoutedRPC(server, RpcList, req, folder);
        }

        private static void OnList(long sender, string req, string folder)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || !Store.ValidFolder(folder)) return;
            string dir = Path.Combine(BepInEx.Paths.ConfigPath, "FiresLoreBooks", folder.Replace('/', Path.DirectorySeparatorChar));
            string[] files = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.jpg").Select(Path.GetFileName).Where(Store.ValidFile).OrderBy(f => f).Take(200).ToArray()
                : new string[0];
            var pkg = new ZPackage();
            pkg.Write(files.Length);
            foreach (string f in files) pkg.Write(f);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcListAnswer, req, pkg);
        }

        private static void OnListAnswer(long sender, string req, ZPackage pkg)
        {
            if (!s_pending.TryGetValue(req, out Action<string[]> done)) return;
            s_pending.Remove(req);
            int n = pkg.ReadInt();
            var files = new string[Mathf.Clamp(n, 0, 200)];
            for (int i = 0; i < files.Length; i++) files[i] = pkg.ReadString();
            done?.Invoke(files);
        }

        /// <summary>True while the picture picker is open: Esc belongs to it (the book's own Esc-close should wait).</summary>
        public static bool PickerOpen => Picker.IsOpen;

        /// <summary>For tests: closes the open picker as its Close button does (the callback gets null).</summary>
        public static void PickerCancel() => Picker.Current?.Choose(null);

        /// <summary>For tests: picks a file in the open picker as clicking its button does; false when it isn't listed.</summary>
        public static bool PickerPick(string file)
        {
            if (Picker.Current == null || Array.IndexOf(Picker.Current.Files, file) < 0) return false;
            Picker.Current.Choose(file);
            return true;
        }

        [HarmonyPatch(typeof(Menu), nameof(Menu.Show))]
        private static class NoMenuWhilePicking
        {
            private static bool Prefix() => !Picker.IsOpen;
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class RegisterOnStart
        {
            private static void Postfix()
            {
                ZRoutedRpc rpc = ZRoutedRpc.instance;
                if (rpc == null || rpc == s_registeredOn) return;
                s_registeredOn = rpc;
                rpc.Register<string, string>(RpcList, OnList);
                rpc.Register<string, ZPackage>(RpcListAnswer, OnListAnswer);
            }
        }

        // A small list window over the editor: one button per file, then Cancel.
        private sealed class Picker : MonoBehaviour
        {
            private string[] _files;
            private Action<string> _picked;
            private Vector2 _scroll;

            internal static void Open(string[] files, Action<string> picked)
            {
                var go = new GameObject("FiresPictures.Picker");
                var p = go.AddComponent<Picker>();
                p._files = files;
                p._picked = picked;
            }

            internal static bool IsOpen => s_open != null;
            internal static Picker Current => s_open;
            internal string[] Files => _files;
            internal void Choose(string file) => Close(file);
            private static Picker s_open;

            private void Awake() => s_open = this;

            // 0.0.7 (lorebooks_inline_test 4/7: closed by Esc, no callback): every way out calls back, null when nothing was picked
            private void Close(string file)
            {
                if (_picked == null) return;
                Action<string> picked = _picked;
                _picked = null;
                if (s_open == this) s_open = null;
                Destroy(gameObject);
                FiresLoreBooks.FiresLoreBooksPlugin.Log?.LogDebug($"[Pictures] picker closed: {(file ?? "nothing picked")}");
                picked(file);
            }

            private void Update()
            {
                if (Input.GetKeyDown(KeyCode.Escape)) Close(null);
            }

            private void OnDestroy() { if (s_open == this) { s_open = null; _picked?.Invoke(null); _picked = null; } }

            private void OnGUI()
            {
                GUI.depth = -100;
                var r = new Rect(Screen.width / 2f - 180f, Screen.height / 2f - 200f, 360f, 400f);
                GUI.Box(r, "Pictures in this book");
                GUILayout.BeginArea(new Rect(r.x + 10f, r.y + 28f, r.width - 20f, r.height - 74f));
                _scroll = GUILayout.BeginScrollView(_scroll);
                foreach (string f in _files)
                    if (GUILayout.Button(f)) { Close(f); GUIUtility.ExitGUI(); }
                GUILayout.EndScrollView();
                GUILayout.EndArea();
                if (GUI.Button(new Rect(r.x + r.width / 2f - 60f, r.yMax - 38f, 120f, 28f), "Close")) Close(null);
            }
        }
    }
}
