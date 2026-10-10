using System.Collections.Generic;
using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresPictures
{
    /// <summary>
    /// Camera mode (Fire 10-09, look B): the HUD hides, a frame (4:3 or 1:1, toggled) with a thirds grid sits over the player's own
    /// view, and the mouse looks around. Shoot (LMB / Space) renders the game camera without any overlay, crops the frame and scales
    /// it to PictureLongSide (512) on the long side; a full-size preview then offers Retake / Keep. Cancel: Esc / RMB. The HUD comes
    /// back in every case. Keys stand down while any input field has the keyboard (FiresInputBlock.IsTyping).
    /// </summary>
    public sealed class Camera : MonoBehaviour
    {
        private const float FrameScreenShare = 0.72f;   // the frame's height as a share of the screen's

        private static ConfigEntry<KeyCode> s_shootKey, s_cancelKey;
        private static ConfigEntry<int> s_longSide;
        private static Camera s_active;

        private Action<Texture2D> _onKept;
        private Action _onCancelled;
        private string _aspect = "4:3";
        private bool _hudWasHidden;

        // 0.0.8 (batch shot 10-09 16:15: the HUD stayed up; m_userHidden alone didn't hide it): every screen-space canvas that is
        // on when the camera opens (the vanilla HUD and minimap, companion bars, Fires panels, the book itself) is switched off and
        // exactly those come back on when it closes. The picture is rendered from the world camera, so no canvas is ever in it.
        private readonly List<Canvas> _hiddenCanvases = new List<Canvas>();

        private void HideScreenUi()
        {
            foreach (Canvas c in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (c == null || !c.isRootCanvas || !c.enabled || c.renderMode == RenderMode.WorldSpace) continue;
                c.enabled = false;
                _hiddenCanvases.Add(c);
            }
            FiresLoreBooks.FiresLoreBooksPlugin.Log?.LogDebug($"[Pictures] camera: {_hiddenCanvases.Count} screen canvas(es) hidden");
        }

        private void RestoreScreenUi()
        {
            foreach (Canvas c in _hiddenCanvases) if (c != null) c.enabled = true;
            _hiddenCanvases.Clear();
        }
        private Texture2D _shot;
        private int _shotBytes;
        private bool _shootNextFrame;

        /// <summary>True while camera mode is up: the book UI hides itself meanwhile, so the player sees the world.</summary>
        public static bool Active => s_active != null;

        /// <summary>Starts camera mode. onKept gets the kept picture (512 px on the long side); onCancelled runs on Cancel.</summary>
        public static void Begin(string aspect, Action<Texture2D> onKept, Action onCancelled)
        {
            if (s_active != null || Player.m_localPlayer == null) { onCancelled?.Invoke(); return; }
            BindConfig();
            var go = new GameObject("FiresPictures.Camera");
            DontDestroyOnLoad(go);
            s_active = go.AddComponent<Camera>();
            s_active._aspect = aspect == "1:1" ? "1:1" : "4:3";
            s_active._onKept = onKept;
            s_active._onCancelled = onCancelled;
            if (Hud.instance != null) { s_active._hudWasHidden = Hud.instance.m_userHidden; Hud.instance.m_userHidden = true; }
            s_active.HideScreenUi();
        }

        private static void BindConfig()
        {
            if (s_shootKey != null) return;
            ConfigFile cfg = BepInEx.Bootstrap.Chainloader.PluginInfos["com.Fire.FiresLoreBooks"].Instance.Config;
            s_shootKey = cfg.Bind("Pictures", "Shoot key", KeyCode.Space, "Camera mode: takes the picture (the left mouse button always does too).");
            s_cancelKey = cfg.Bind("Pictures", "Cancel key", KeyCode.Mouse1, "Camera mode: leaves without a picture (Esc always does too; it does not open the game menu while the camera is up).");
            s_longSide = cfg.Bind("Pictures", "Picture size", 512, new ConfigDescription("Pixels on the picture's long side.", new AcceptableValueRange<int>(256, 1024)));
        }

        private void Update()
        {
            if (Player.m_localPlayer == null) { End(null); return; }
            if (FiresLoreBooks.UI.Typing.InTextInput()) return;   // 0.0.7: an open Fires panel (the editor) holds an input-block token; only real text input stops the camera's keys
            if (_shot == null)
            {
                if (Input.GetKeyDown(s_cancelKey.Value) || Input.GetKeyDown(KeyCode.Escape)) End(null);
                else if (Input.GetKeyDown(s_shootKey.Value) || Input.GetMouseButtonDown(0)) _shootNextFrame = true;
                else if (Input.GetKeyDown(KeyCode.Q)) _aspect = _aspect == "4:3" ? "1:1" : "4:3";
            }
            else
            {
                if (Input.GetKeyDown(s_cancelKey.Value) || Input.GetKeyDown(KeyCode.Escape)) Retake();
                else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) Keep();
            }
        }

        private void LateUpdate()
        {
            if (!_shootNextFrame) return;
            _shootNextFrame = false;
            _shot = Capture(FrameRect(), _aspect, s_longSide.Value);
            _shotBytes = _shot != null ? _shot.EncodeToJPG(75).Length : 0;
        }

        // The game camera rendered straight into a texture (no IMGUI overlay, no HUD), cropped to the frame, scaled down.
        private static Texture2D Capture(Rect frame, string aspect, int longSide)
        {
            UnityEngine.Camera cam = GameCamera.instance != null ? GameCamera.instance.GetComponent<UnityEngine.Camera>() : UnityEngine.Camera.main;
            if (cam == null) return null;
            int w = Screen.width, h = Screen.height;
            var full = RenderTexture.GetTemporary(w, h, 24);
            RenderTexture old = cam.targetTexture;
            cam.targetTexture = full;
            cam.Render();
            cam.targetTexture = old;
            int outW = aspect == "1:1" ? longSide : longSide, outH = aspect == "1:1" ? longSide : longSide * 3 / 4;
            var small = RenderTexture.GetTemporary(outW, outH, 0);
            // IMGUI rects run top-down; texture UVs bottom-up
            Vector2 scale = new Vector2(frame.width / w, frame.height / h);
            Vector2 offset = new Vector2(frame.x / w, 1f - (frame.y + frame.height) / h);
            Graphics.Blit(full, small, scale, offset);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = small;
            var tex = new Texture2D(outW, outH, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, outW, outH), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(full);
            RenderTexture.ReleaseTemporary(small);
            return tex;
        }

        private Rect FrameRect()
        {
            float fh = Screen.height * FrameScreenShare;
            float fw = _aspect == "1:1" ? fh : fh * 4f / 3f;
            return new Rect((Screen.width - fw) / 2f, (Screen.height - fh) / 2f, fw, fh);
        }

        private void Retake()
        {
            if (_shot != null) Destroy(_shot);
            _shot = null;
        }

        private void Keep()
        {
            Texture2D kept = _shot;
            _shot = null;
            End(kept);
        }

        private void End(Texture2D kept)
        {
            if (Hud.instance != null) Hud.instance.m_userHidden = _hudWasHidden;
            RestoreScreenUi();
            s_active = null;
            s_closedFrame = Time.frameCount;
            Destroy(gameObject);
            try { if (kept != null) _onKept?.Invoke(kept); else _onCancelled?.Invoke(); }
            catch (Exception ex) { FiresLoreBooks.FiresLoreBooksPlugin.Log?.LogWarning($"[Pictures] camera callback threw: {ex.Message}"); }
        }

        private void OnDestroy()
        {
            if (s_active == this) { if (Hud.instance != null) Hud.instance.m_userHidden = _hudWasHidden; RestoreScreenUi(); s_active = null; }
        }

        // ── drawing ──

        private static Texture2D s_white;
        private static GUIStyle s_button, s_label;

        private void OnGUI()
        {
            if (s_white == null) { s_white = new Texture2D(1, 1); s_white.SetPixel(0, 0, Color.white); s_white.Apply(); }
            if (s_button == null)
            {
                s_button = new GUIStyle(GUI.skin.button) { fontSize = 16, fixedHeight = 34 };
                s_label = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleCenter, richText = true };
            }
            if (_shot == null) DrawAim(); else DrawPreview();
        }

        private void DrawAim()
        {
            Rect f = FrameRect();
            Color dim = new Color(0f, 0f, 0f, 0.45f);
            Fill(new Rect(0, 0, Screen.width, f.y), dim);
            Fill(new Rect(0, f.yMax, Screen.width, Screen.height - f.yMax), dim);
            Fill(new Rect(0, f.y, f.x, f.height), dim);
            Fill(new Rect(f.xMax, f.y, Screen.width - f.xMax, f.height), dim);
            Color line = new Color(0.95f, 0.89f, 0.69f, 1f), third = new Color(0.95f, 0.89f, 0.69f, 0.28f);
            Outline(f, line, 2f);
            for (int i = 1; i <= 2; i++)
            {
                Fill(new Rect(f.x + f.width * i / 3f, f.y, 1f, f.height), third);
                Fill(new Rect(f.x, f.y + f.height * i / 3f, f.width, 1f), third);
            }
            GUI.Label(new Rect(f.x, f.y - 30f, f.width, 26f),
                $"Mouse: look · LMB / {s_shootKey.Value}: shoot · {(s_cancelKey.Value == KeyCode.Mouse1 ? "RMB" : s_cancelKey.Value.ToString())} / Esc: cancel · Q: {(_aspect == "4:3" ? "1:1" : "4:3")}", s_label);
            GUI.Label(new Rect(f.xMax - 120f, f.y + 6f, 112f, 24f), $"{_aspect} · {s_longSide.Value} px", s_label);
            float y = f.yMax + 10f, bw = 120f, gap = 12f, x = Screen.width / 2f - bw * 1.5f - gap;
            if (GUI.Button(new Rect(x, y, bw, 34f), "Cancel", s_button)) End(null);
            if (GUI.Button(new Rect(x + bw + gap, y, bw, 34f), "<b>Shoot</b>", s_button)) _shootNextFrame = true;
            if (GUI.Button(new Rect(x + (bw + gap) * 2f, y, bw, 34f), _aspect == "4:3" ? "4:3 → 1:1" : "1:1 → 4:3", s_button))
                _aspect = _aspect == "4:3" ? "1:1" : "4:3";
        }

        private void DrawPreview()
        {
            Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0.11f, 0.09f, 0.07f, 0.92f));
            Rect f = FrameRect();
            Fill(new Rect(f.x - 4f, f.y - 4f, f.width + 8f, f.height + 8f), new Color(0.94f, 0.89f, 0.77f, 1f));
            GUI.DrawTexture(f, _shot, ScaleMode.ScaleToFit);
            GUI.Label(new Rect(f.x, f.yMax + 6f, f.width, 22f), $"{_shot.width} × {_shot.height} · JPG ~{_shotBytes / 1024} KB", s_label);
            float y = f.yMax + 32f, bw = 120f, gap = 12f, x = Screen.width / 2f - bw - gap / 2f;
            if (GUI.Button(new Rect(x, y, bw, 34f), "Retake", s_button)) Retake();
            if (GUI.Button(new Rect(x + bw + gap, y, bw, 34f), "<b>Keep</b>", s_button)) Keep();
        }

        private static void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, s_white);
            GUI.color = old;
        }

        private static void Outline(Rect r, Color c, float t)
        {
            Fill(new Rect(r.x, r.y, r.width, t), c);
            Fill(new Rect(r.x, r.yMax - t, r.width, t), c);
            Fill(new Rect(r.x, r.y, t, r.height), c);
            Fill(new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        // Esc cancels the camera, so the game menu must not open on the same press (the RPGmaker pattern, UIInitializer.cs).
        // The frame the camera closes on still counts: the menu reads Esc in the same frame.
        private static int s_closedFrame = -1;

        [HarmonyPatch(typeof(Menu), nameof(Menu.Show))]
        private static class NoMenuWhileCamera
        {
            private static bool Prefix() => !Active && Time.frameCount != s_closedFrame;
        }

        // While aiming, the mouse and the shoot/cancel keys belong to the camera: no attack, block, jump, dodge or use.
        [HarmonyPatch(typeof(Player), nameof(Player.SetControls))]
        private static class NoActionsWhileAiming
        {
            private static void Prefix(ref bool attack, ref bool attackHold, ref bool secondaryAttack, ref bool secondaryAttackHold,
                ref bool block, ref bool blockHold, ref bool jump, ref bool dodge)
            {
                if (!Active) return;
                attack = attackHold = secondaryAttack = secondaryAttackHold = block = blockHold = jump = dodge = false;
            }
        }
    }
}
