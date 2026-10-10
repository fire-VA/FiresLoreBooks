using System.Collections.Generic;
using FiresLoreBooks.Books;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FiresLoreBooks.UI
{
    /// <summary>
    /// The admin editor (mockup 2, approved by Fire 10-09 with one change: WYSIWYG). The page is a rich-text TMP input
    /// field, so bold / italic / colour / size show live and the tags stay hidden; the toolbar wraps the selection in tags.
    /// The right page holds the title, respawn time and reward hooks (one per line). Save sends the whole book to the
    /// server, which checks admin and sanitises before it writes the .txt.
    /// </summary>
    public sealed class BookEditor : MonoBehaviour
    {
        public static BookEditor Instance { get; private set; }
        public static bool IsOpen => Instance != null && Instance._open;

        private static readonly Color[] Swatches =
        {
            new Color(0.23f, 0.15f, 0.08f), new Color(0.48f, 0.12f, 0.12f), new Color(0.15f, 0.30f, 0.55f),
            new Color(0.18f, 0.42f, 0.18f), new Color(0.55f, 0.40f, 0.10f), new Color(0.40f, 0.20f, 0.45f),
        };

        private GameObject _root;
        private TMP_InputField _page, _title, _respawn, _hooks;
        private TextMeshProUGUI _pageLabel, _status;
        private BookDoc _doc;
        private int _pageIndex;
        private bool _open;
        private readonly object _inputToken = new object();

        public static void Open(string bookId)
        {
            if (!BookStore.ValidId(bookId)) { FiresLoreBooksPlugin.Log.LogWarning($"[LoreBooks] bad book id '{bookId}'"); return; }
            // Core's admin check (the server's admin list, synced), as the other Fires admin windows use;
            // ZNet.LocalPlayerIsAdminOrHost can be false for an admin client on a dedicated server. The server checks again on save.
            if (!IsLocalAdmin())
            {
                FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] editor refused for '{bookId}': not admin");
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Only admins can edit lore books.");
                return;
            }
            if (Instance == null)
            {
                var go = new GameObject("FiresLoreBooks_Editor");
                DontDestroyOnLoad(go);
                Instance = go.AddComponent<BookEditor>();
                Instance.Build();
            }
            BookReader.Close();
            Instance.Show(bookId);
        }

        public static void Close() { if (Instance != null) Instance.Hide(); }

        internal static bool IsLocalAdmin() =>
            FiresCore.Sync.AdminSyncing.IsLocalAdmin() || (ZNet.instance != null && ZNet.instance.LocalPlayerIsAdminOrHost());

        private void Show(string bookId)
        {
            FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] editor opened for '{bookId}'");
            _open = true;
            _root.SetActive(true);
            FiresCore.Input.FiresInputBlock.Acquire(_inputToken);
            _status.text = "loading…";
            _doc = new BookDoc { Id = bookId };
            _doc.Pages.Add("");
            _pageIndex = 0;
            LoadFields();
            BookStore.Get(bookId, doc =>
            {
                if (!_open || doc == null || doc.Id != bookId) return;
                _doc = doc;
                if (_doc.Pages.Count == 0) _doc.Pages.Add("");
                _pageIndex = 0;
                LoadFields();
                _status.text = "";
            });
        }

        private void Hide()
        {
            if (!_open) return;
            _open = false;
            _root.SetActive(false);
            FiresCore.Input.FiresInputBlock.Release(_inputToken);
        }

        private void LoadFields()
        {
            _title.text = _doc.Title;
            _respawn.text = _doc.Respawn;
            _hooks.text = string.Join("\n", _doc.Hooks);
            // pictures: each <img> is one anchor character in the field; the specs are kept beside it and written back on save
            var parsed = FiresPictures.PageLayout.Parse(_doc.Pages[_pageIndex]);
            _imgs = parsed.imgs;
            _page.text = parsed.text;
            ShowPictures();
            _pageLabel.text = $"Page {_pageIndex + 1} / {_doc.Pages.Count}";
        }

        private void StorePage()
        {
            if (_doc == null) return;
            _doc.Pages[_pageIndex] = ToSource(_page.text);
        }

        private void GoPage(int delta)
        {
            StorePage();
            _pageIndex = Mathf.Clamp(_pageIndex + delta, 0, _doc.Pages.Count - 1);
            LoadFields();
        }

        private void AddPage()
        {
            StorePage();
            _doc.Pages.Insert(_pageIndex + 1, "");
            _pageIndex++;
            LoadFields();
        }

        private void DeletePage()
        {
            if (_doc.Pages.Count <= 1) { _page.text = ""; return; }
            _doc.Pages.RemoveAt(_pageIndex);
            _pageIndex = Mathf.Clamp(_pageIndex, 0, _doc.Pages.Count - 1);
            LoadFields();
        }

        private void Save()
        {
            StorePage();
            _doc.Title = _title.text.Trim();
            _doc.Respawn = _respawn.text.Trim();
            _doc.Hooks.Clear();
            foreach (string line in _hooks.text.Split('\n'))
                if (line.Trim().Length > 0) _doc.Hooks.Add(line.Trim());
            _status.text = "saving…";
            BookStore.Save(_doc, err =>
            {
                _status.text = string.IsNullOrEmpty(err) ? "saved" : "not saved: " + err;
                FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] editor save '{_doc.Id}': {(string.IsNullOrEmpty(err) ? "ok" : err)}");
            });
        }

        // ---- the toolbar wraps the selection in a tag (string positions: the tags themselves stay hidden) ----

        // ---- emoji picker (Twemoji, 0.0.6): a grid of every sprite; a click inserts <sprite name="hex"> at the caret ----

        private GameObject _picker;

        private void TogglePicker()
        {
            if (_picker == null) BuildPicker();
            _picker.SetActive(!_picker.activeSelf);
        }

        private void BuildPicker()
        {
            const int cols = 15;
            const float cell = 38f;
            var codes = Emoji.Codes;
            int rows = Mathf.Max(1, (codes.Count + cols - 1) / cols);
            Image bg = BookReader.Box(_root.transform, "EmojiPicker", new Vector2(-40f, 400f - rows * cell / 2f - 30f),
                new Vector2(cols * cell + 16f, rows * cell + 16f), new Color(0.12f, 0.08f, 0.05f, 0.97f));
            _picker = bg.gameObject;
            for (int i = 0; i < codes.Count; i++)
            {
                string hex = codes[i];
                float px = -cols * cell / 2f + cell / 2f + (i % cols) * cell;
                float py = rows * cell / 2f - cell / 2f - (i / cols) * cell;
                Button b = BookReader.ButtonAt(bg.transform, "E_" + hex, new Vector2(px, py), new Vector2(cell - 4f, cell - 4f), $"<sprite name=\"{hex}\">");
                Emoji.Apply(b.GetComponentInChildren<TextMeshProUGUI>());
                b.onClick.AddListener(() => InsertEmoji(hex));
            }
            _picker.SetActive(false);
        }

        private void InsertEmoji(string hex)
        {
            string s = _page.text;
            int at = Mathf.Clamp(_page.stringPosition, 0, s.Length);
            string tag = $"<sprite name=\"{hex}\">";
            _page.text = s.Substring(0, at) + tag + s.Substring(at);
            _page.ActivateInputField();
            _page.stringPosition = at + tag.Length;
            _picker.SetActive(false);
        }

        // ---- inline pictures ([fgn]'s PageLayout / PictureList, 0.0.4) ----

        private const char Anchor = '​';
        private List<FiresPictures.ImgSpec> _imgs = new List<FiresPictures.ImgSpec>();

        /// <summary>The field's text with each anchor character turned back into its picture's tag (in order).</summary>
        private string ToSource(string fieldText)
        {
            var sb = new System.Text.StringBuilder();
            int n = 0;
            foreach (char c in fieldText)
            {
                if (c != Anchor) { sb.Append(c); continue; }
                if (n < _imgs.Count) sb.Append(FiresPictures.PageLayout.Write(_imgs[n]));
                n++;
            }
            return sb.ToString();
        }

        /// <summary>The page's pictures with their handles. Wrap-around would rewrite the field's text, so the editor shows them "past".</summary>
        private void ShowPictures()
        {
            if (_doc == null) return;
            var view = new List<FiresPictures.ImgSpec>();
            int n = 0;
            for (int i = 0; i < _page.text.Length && n < _imgs.Count; i++)
                if (_page.text[i] == Anchor)
                {
                    FiresPictures.ImgSpec s = _imgs[n++];
                    view.Add(new FiresPictures.ImgSpec { Src = s.Src, X = s.X, Y = s.Y, W = s.W, Rot = s.Rot, Crop = s.Crop, Caption = s.Caption, WrapAround = false, Anchor = i });
                }
            _page.textComponent.pageToDisplay = 1;
            // slot-98 retest: no picture drew in the editor. The input field's text component lays out its characters on
            // its own schedule, so the anchors had no character info yet when Show looked them up; force the layout first.
            _page.textComponent.ForceMeshUpdate();
            FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] editor page {_pageIndex + 1}: {view.Count} picture(s) to show");
            FiresPictures.PageLayout.Show(_page.textComponent.rectTransform, (TextMeshProUGUI)_page.textComponent, "Books/" + _doc.Id, view, true,
                changed =>
                {
                    int k = view.IndexOf(changed);
                    if (k < 0 || k >= _imgs.Count) return;
                    bool wrap = _imgs[k].WrapAround;
                    _imgs[k] = changed;
                    _imgs[k].WrapAround = wrap;
                    ShowPictures();
                },
                deleted =>
                {
                    int k = view.IndexOf(deleted);
                    if (k < 0 || k >= _imgs.Count) return;
                    _imgs.RemoveAt(k);
                    int seen = -1;
                    var sb = new System.Text.StringBuilder();
                    foreach (char c in _page.text) { if (c == Anchor && ++seen == k) continue; sb.Append(c); }
                    _page.text = sb.ToString();
                    ShowPictures();
                });
        }

        /// <summary>A new picture (from the folder list or the camera) goes in at the caret.</summary>
        private void InsertPicture(FiresPictures.ImgSpec spec)
        {
            if (spec == null) return;
            string s = _page.text;
            int at = Mathf.Clamp(_page.stringPosition, 0, s.Length);
            int before = 0;
            for (int i = 0; i < at; i++) if (s[i] == Anchor) before++;
            _imgs.Insert(Mathf.Min(before, _imgs.Count), spec);
            _page.text = s.Substring(0, at) + Anchor + s.Substring(at);
            ShowPictures();
            FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] editor: picture '{spec.Src}' inserted on page {_pageIndex + 1}");
        }

        private void Wrap(string open, string close)
        {
            string s = _page.text;
            int a = Mathf.Clamp(Mathf.Min(_page.selectionStringAnchorPosition, _page.selectionStringFocusPosition), 0, s.Length);
            int b = Mathf.Clamp(Mathf.Max(_page.selectionStringAnchorPosition, _page.selectionStringFocusPosition), 0, s.Length);
            _page.text = s.Substring(0, a) + open + s.Substring(a, b - a) + close + s.Substring(b);
            _page.ActivateInputField();
            _page.stringPosition = b + open.Length;
        }

        private void Update()
        {
            if (!_open) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (Player.m_localPlayer == null) Hide();
        }

        [HarmonyPatch(typeof(Menu), nameof(Menu.Show))]
        private static class EscClosesEditor
        {
            private static bool Prefix()
            {
                if (!IsOpen) return true;
                if (FiresPictures.Camera.Active || FiresPictures.PictureList.PickerOpen) return true;   // that Esc belongs to the camera / picker
                Close();
                return false;
            }
        }

        // ================= layout =================

        private void Build()
        {
            _root = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            _root.transform.SetParent(transform, false);
            var canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 525;
            var scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;

            BookReader.Stretch(BookReader.Box(_root.transform, "Dim", Vector2.zero, Vector2.zero, new Color(0f, 0f, 0f, 0.55f)).rectTransform);
            Image cover = BookReader.Box(_root.transform, "Cover", new Vector2(0f, -40f), new Vector2(1500f, 860f), BookReader.Leather);
            var edge = cover.gameObject.AddComponent<Outline>();
            edge.effectColor = BookReader.LeatherEdge; edge.effectDistance = new Vector2(4f, -4f);

            BookReader.Label(_root.transform, "Head", new Vector2(-380f, 490f), new Vector2(760f, 44f), 28f, new Color(0.95f, 0.78f, 0.42f), TextAlignmentOptions.MidlineLeft).text = "Editing (admin)";
            BookReader.ButtonAt(_root.transform, "Cancel", new Vector2(560f, 490f), new Vector2(130f, 44f), "Cancel").onClick.AddListener(Hide);
            BookReader.ButtonAt(_root.transform, "Save", new Vector2(690f, 490f), new Vector2(110f, 44f), "Save").onClick.AddListener(Save);

            // toolbar
            float x = -700f, y = 430f;
            Tool("B", "<b>", "</b>", ref x, y);
            Tool("I", "<i>", "</i>", ref x, y);
            Tool("U", "<u>", "</u>", ref x, y);
            Tool("S", "<s>", "</s>", ref x, y);
            x += 16f;
            foreach (Color c in Swatches)
            {
                string hex = "#" + ColorUtility.ToHtmlStringRGB(c);
                Image sw = BookReader.Box(_root.transform, "Swatch", new Vector2(x + 18f, y), new Vector2(34f, 34f), c);
                var b = sw.gameObject.AddComponent<Button>();
                b.targetGraphic = sw;
                b.onClick.AddListener(() => Wrap($"<color={hex}>", "</color>"));
                x += 40f;
            }
            x += 16f;
            Tool("Size−", "<size=85%>", "</size>", ref x, y, 80f);
            Tool("Size+", "<size=130%>", "</size>", ref x, y, 80f);
            Tool("⯇", "<align=left>", "</align>", ref x, y);
            Tool("≡", "<align=center>", "</align>", ref x, y);
            Tool("⯈", "<align=right>", "</align>", ref x, y);
            x += 16f;
            Button emoji = BookReader.ButtonAt(_root.transform, "Tool_Emoji", new Vector2(x + 20f, y), new Vector2(40f, 34f), "<sprite name=\"1f642\">");
            Emoji.Apply(emoji.GetComponentInChildren<TextMeshProUGUI>());
            emoji.onClick.AddListener(TogglePicker);
            x += 46f;
            Button img = BookReader.ButtonAt(_root.transform, "Tool_Image", new Vector2(x + 50f, y), new Vector2(100f, 34f), "Image");
            img.onClick.AddListener(() => FiresPictures.PictureList.OnImage("Books/" + _doc.Id, InsertPicture));
            x += 106f;
            Button shot = BookReader.ButtonAt(_root.transform, "Tool_Shot", new Vector2(x + 45f, y), new Vector2(90f, 34f), "Shot");
            shot.onClick.AddListener(() =>
            {
                string folder = "Books/" + _doc.Id;
                StorePage();
                Hide();
                FiresPictures.PictureList.OnShot(folder, spec => { Show(_doc.Id); InsertPicture(spec); }, err => { Show(_doc.Id); _status.text = "picture: " + err; });
            });

            Image leftPage = BookReader.Box(cover.transform, "LeftPage", new Vector2(-357f, 0f), new Vector2(706f, 812f), BookReader.Parchment);
            Image rightPage = BookReader.Box(cover.transform, "RightPage", new Vector2(357f, 0f), new Vector2(706f, 812f), BookReader.Parchment);

            _page = Input(leftPage.transform, "Page", new Vector2(0f, 20f), new Vector2(620f, 700f), 24f, true, true);
            _pageLabel = BookReader.Label(leftPage.transform, "PageNum", new Vector2(0f, -375f), new Vector2(240f, 30f), 20f, new Color(0.48f, 0.38f, 0.25f), TextAlignmentOptions.Center);
            BookReader.ButtonAt(leftPage.transform, "Prev", new Vector2(-280f, -375f), new Vector2(60f, 40f), "◀").onClick.AddListener(() => GoPage(-1));
            BookReader.ButtonAt(leftPage.transform, "Next", new Vector2(280f, -375f), new Vector2(60f, 40f), "▶").onClick.AddListener(() => GoPage(1));

            Color ink = new Color(0.48f, 0.38f, 0.25f);
            BookReader.Label(rightPage.transform, "TitleLab", new Vector2(0f, 360f), new Vector2(620f, 30f), 20f, ink, TextAlignmentOptions.MidlineLeft).text = "Title";
            _title = Input(rightPage.transform, "Title", new Vector2(0f, 320f), new Vector2(620f, 44f), 24f, false, false);
            BookReader.Label(rightPage.transform, "RespawnLab", new Vector2(0f, 270f), new Vector2(620f, 30f), 20f, ink, TextAlignmentOptions.MidlineLeft).text = "Respawn after taking (e.g. 4d, 12h, never; empty = server default)";
            _respawn = Input(rightPage.transform, "Respawn", new Vector2(0f, 230f), new Vector2(620f, 44f), 22f, false, false);
            BookReader.Label(rightPage.transform, "HooksLab", new Vector2(0f, 180f), new Vector2(620f, 30f), 20f, ink, TextAlignmentOptions.MidlineLeft).text = "Rewards when read to the end (one per line, e.g. GiveClassXP 200 once)";
            _hooks = Input(rightPage.transform, "Hooks", new Vector2(0f, 40f), new Vector2(620f, 240f), 20f, true, false);
            BookReader.ButtonAt(rightPage.transform, "AddPage", new Vector2(-200f, -150f), new Vector2(200f, 44f), "+ Page after").onClick.AddListener(AddPage);
            BookReader.ButtonAt(rightPage.transform, "DelPage", new Vector2(40f, -150f), new Vector2(200f, 44f), "Delete page").onClick.AddListener(DeletePage);
            _status = BookReader.Label(rightPage.transform, "Status", new Vector2(0f, -360f), new Vector2(620f, 30f), 20f, ink, TextAlignmentOptions.Center);

            _root.SetActive(false);
        }

        private void Tool(string label, string open, string close, ref float x, float y, float w = 40f)
        {
            Button b = BookReader.ButtonAt(_root.transform, "Tool_" + label, new Vector2(x + w / 2f, y), new Vector2(w, 34f), label);
            b.onClick.AddListener(() => Wrap(open, close));
            x += w + 6f;
        }

        /// <summary>A TMP input field on parchment; rich text shows formatting live (WYSIWYG) and hides the tags.</summary>
        private static TMP_InputField Input(Transform parent, string name, Vector2 pos, Vector2 size, float fontSize, bool multiLine, bool richText)
        {
            Image bg = BookReader.Box(parent, name, pos, size, new Color(1f, 1f, 1f, 0.25f));
            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewport.transform.SetParent(bg.transform, false);
            var vrt = (RectTransform)viewport.transform;
            BookReader.Stretch(vrt);
            vrt.offsetMin = new Vector2(8f, 6f); vrt.offsetMax = new Vector2(-8f, -6f);
            TextMeshProUGUI text = BookReader.Label(viewport.transform, "Text", Vector2.zero, Vector2.zero, fontSize, BookReader.Ink, multiLine ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft);
            BookReader.Stretch(text.rectTransform);
            text.richText = richText;
            text.enableWordWrapping = multiLine;
            if (richText) Emoji.Apply(text);
            var field = bg.gameObject.AddComponent<TMP_InputField>();
            field.textViewport = vrt;
            field.textComponent = text;
            field.richText = richText;
            field.lineType = multiLine ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
            field.characterLimit = multiLine ? RichText.MaxPageChars : 128;
            field.fontAsset = text.font;
            field.pointSize = fontSize;
            return field;
        }
    }
}
