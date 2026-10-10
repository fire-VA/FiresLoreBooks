using System.Collections.Generic;
using System.Text.RegularExpressions;
using FiresLoreBooks.Books;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FiresLoreBooks.UI
{
    /// <summary>
    /// The reading book (mockup 1, approved by Fire 10-09): a classic leather two-page spread, parchment pages, a ribbon
    /// bookmark, turned by A / D, arrows, the mouse wheel or the page corners; Esc closes (the game menu does not open).
    /// Pagination is TMP's own page overflow: each "===" page break becomes &lt;page&gt;, and a long page flows on, so the
    /// left side shows TMP page 2k+1 and the right side 2k+2.
    /// </summary>
    public sealed class BookReader : MonoBehaviour
    {
        public static BookReader Instance { get; private set; }
        public static bool IsOpen => Instance != null && Instance._open;

        internal static readonly Color Leather = new Color(0.29f, 0.16f, 0.09f, 1f);
        internal static readonly Color LeatherEdge = new Color(0.85f, 0.66f, 0.30f, 0.45f);
        internal static readonly Color Parchment = new Color(0.92f, 0.85f, 0.71f, 1f);
        internal static readonly Color Ink = new Color(0.23f, 0.15f, 0.08f, 1f);
        private static readonly Regex ImgTag = new Regex(@"<img[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // the spread each book was left open at this session (the ribbon bookmark)
        private static readonly Dictionary<string, int> s_bookmarks = new Dictionary<string, int>();

        private GameObject _root;
        private TextMeshProUGUI _left, _right, _title, _leftNum, _rightNum;
        private bool _open;
        private string _bookId;
        private int _spread;            // 0-based; shows TMP pages 2*spread+1 and 2*spread+2
        private int _pageCount = 1;
        private readonly object _inputToken = new object();

        /// <summary>Open a book by id; the text comes from the server (cached by revision).</summary>
        public static void Open(string bookId)
        {
            if (!BookStore.ValidId(bookId)) { FiresLoreBooksPlugin.Log.LogWarning($"[LoreBooks] bad book id '{bookId}'"); return; }
            if (Instance == null)
            {
                var go = new GameObject("FiresLoreBooks_Reader");
                DontDestroyOnLoad(go);
                Instance = go.AddComponent<BookReader>();
                Instance.Build();
            }
            Instance.Show(bookId);
        }

        public static void Close() { if (Instance != null) Instance.Hide(); }

        /// <summary>
        /// The Lore collection page (Fire 10-09): the books this player has taken, out of every book on the server, shown
        /// in the reader. Opened by `lorebook_collection` or the reader's "Collection" button.
        /// </summary>
        public static void OpenCollection()
        {
            if (Instance == null)
            {
                var go = new GameObject("FiresLoreBooks_Reader");
                DontDestroyOnLoad(go);
                Instance = go.AddComponent<BookReader>();
                Instance.Build();
            }
            Instance.ShowCollection();
        }

        private void ShowCollection()
        {
            if (_bookId != null) s_bookmarks[_bookId] = _spread;
            _bookId = null;
            _spread = 0;
            _title.text = "Lore collection";
            _left.text = _right.text = "";
            _open = true;
            _root.SetActive(true);
            FiresCore.Input.FiresInputBlock.Acquire(_inputToken);
            Books.Ledger.RequestCollection((total, titles) =>
            {
                if (!_open || _bookId != null) return;
                var sb = new System.Text.StringBuilder();
                sb.Append($"<size=130%><b>Lore collected: {titles.Count} / {total}</b></size>\n\n");
                if (titles.Count == 0) sb.Append("<i>No lore books yet. Hold E on a lore book to take a copy.</i>");
                foreach (string t in titles) sb.Append("• ").Append(t).Append('\n');
                int missing = Mathf.Max(0, total - titles.Count);
                if (missing > 0) sb.Append($"\n<color=#7a6040>… and {missing} more to find.</color>");
                FillText(sb.ToString());
                FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] collection page: {titles.Count} / {total}");
            });
        }

        /// <summary>Plain content (no book id: no rewards, no pictures) laid out like a book.</summary>
        private void FillText(string text)
        {
            _source = text;
            _layoutText = RichText.Sanitize(text);
            _imgs = new List<FiresPictures.ImgSpec>();
            _left.text = _right.text = _layoutText;
            _left.ForceMeshUpdate();
            _pageCount = Mathf.Max(1, _left.textInfo.pageCount);
            ShowSpread();
        }

        private void Show(string bookId)
        {
            _bookId = bookId;
            _spread = s_bookmarks.TryGetValue(bookId, out int s) ? s : 0;
            _reportedFinish = false;
            _title.text = "…";
            _left.text = _right.text = "";
            _open = true;
            _root.SetActive(true);
            FiresCore.Input.FiresInputBlock.Acquire(_inputToken);
            BookStore.Get(bookId, Fill);
        }

        private void Hide()
        {
            if (!_open) return;
            if (_bookId != null) s_bookmarks[_bookId] = _spread;
            _open = false;
            _root.SetActive(false);
            FiresCore.Input.FiresInputBlock.Release(_inputToken);
        }

        private void OnEnable() => BookStore.Changed += OnBookChanged;
        private void OnDisable() => BookStore.Changed -= OnBookChanged;

        private void OnBookChanged(string id)
        {
            if (_open && string.Equals(id, _bookId, System.StringComparison.OrdinalIgnoreCase)) BookStore.Get(id, Fill);
        }

        private void Fill(BookDoc doc)
        {
            if (!_open || doc == null || !string.Equals(doc.Id, _bookId, System.StringComparison.OrdinalIgnoreCase)) return;
            _title.text = string.IsNullOrEmpty(doc.Title) ? doc.Id : doc.Title;
            // pictures are laid out by the editor milestone; until then the reader leaves their tags out
            // 0.0.4: inline pictures ([fgn]'s PageLayout): each <img> becomes one anchor character; the frames are drawn
            // over the page that anchor lands on, after every page turn (ShowSpread).
            _source = string.Join("<page>", doc.Pages);
            var parsed = FiresPictures.PageLayout.Parse(_source);
            _layoutText = parsed.text;
            _imgs = parsed.imgs;
            _left.text = _layoutText;
            _right.text = _layoutText;
            _left.ForceMeshUpdate();
            _pageCount = Mathf.Max(1, _left.textInfo.pageCount);
            _spread = Mathf.Clamp(_spread, 0, (_pageCount - 1) / 2);
            ShowSpread();
            FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] reading '{doc.Id}': {doc.Pages.Count} written page(s), {_pageCount} laid-out page(s), spread {_spread + 1}");
        }

        private bool _reportedFinish;
        private string _source = "", _layoutText = "";
        private List<FiresPictures.ImgSpec> _imgs = new List<FiresPictures.ImgSpec>();

        /// <summary>The pictures anchored on the two visible pages (wrap-around rewrites the text, so it is reset first).</summary>
        private void ShowPictures()
        {
            if (_bookId == null) return;
            string folder = "Books/" + _bookId;
            foreach (TextMeshProUGUI side in new[] { _left, _right })
            {
                if (!side.enabled) continue;
                side.text = _layoutText;
                var fresh = FiresPictures.PageLayout.Parse(_source).imgs;   // Show may mutate its specs' layout state
                FiresPictures.PageLayout.Show(side.rectTransform, side, folder, fresh, false, null);
            }
        }

        private void ShowSpread()
        {
            int leftPage = _spread * 2 + 1, rightPage = leftPage + 1;
            // the last spread is on screen: the book was read to its end, so the server may grant its rewards (once per open)
            if (!_reportedFinish && rightPage >= _pageCount && _bookId != null)
            {
                _reportedFinish = true;
                Books.Ledger.ReportFinished(_bookId);
            }
            _left.pageToDisplay = leftPage;
            _right.pageToDisplay = rightPage;
            _right.enabled = rightPage <= _pageCount;
            ShowPictures();
            _leftNum.text = leftPage.ToString();
            _rightNum.text = rightPage <= _pageCount ? rightPage.ToString() : "";
        }

        private void Turn(int delta)
        {
            int last = (_pageCount - 1) / 2;
            int next = Mathf.Clamp(_spread + delta, 0, last);
            if (next == _spread) return;
            _spread = next;
            ShowSpread();
        }

        private void Update()
        {
            if (!_open) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (Player.m_localPlayer == null) { Hide(); return; }
            if (Typing.InTextInput()) return;
            if (FiresCore.Input.FiresKeys.GetKeyDown(KeyCode.D) || FiresCore.Input.FiresKeys.GetKeyDown(KeyCode.RightArrow)) Turn(1);
            else if (FiresCore.Input.FiresKeys.GetKeyDown(KeyCode.A) || FiresCore.Input.FiresKeys.GetKeyDown(KeyCode.LeftArrow)) Turn(-1);
            float wheel = Input.mouseScrollDelta.y;
            if (wheel < -0.1f) Turn(1); else if (wheel > 0.1f) Turn(-1);
        }

        // Esc: vanilla opens its menu on Esc; while the book is up that Esc closes the book instead.
        [HarmonyPatch(typeof(Menu), nameof(Menu.Show))]
        private static class EscClosesBook
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
            canvas.sortingOrder = 520;
            var scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;

            Image dim = Box(_root.transform, "Dim", Vector2.zero, Vector2.zero, new Color(0f, 0f, 0f, 0.55f));
            Stretch(dim.rectTransform);

            Image cover = Box(_root.transform, "Cover", new Vector2(0f, -20f), new Vector2(1500f, 900f), Leather);
            var edge = cover.gameObject.AddComponent<Outline>();
            edge.effectColor = LeatherEdge; edge.effectDistance = new Vector2(4f, -4f);

            _title = Label(_root.transform, "Title", new Vector2(-300f, 470f), new Vector2(900f, 50f), 34f, new Color(0.95f, 0.78f, 0.42f), TextAlignmentOptions.MidlineLeft);
            ButtonAt(_root.transform, "Collection", new Vector2(520f, 470f), new Vector2(170f, 44f), "Collection").onClick.AddListener(ShowCollection);
            Button close = ButtonAt(_root.transform, "Close", new Vector2(680f, 470f), new Vector2(120f, 44f), "Close");
            close.onClick.AddListener(Hide);

            Image leftPage = Box(cover.transform, "LeftPage", new Vector2(-357f, 0f), new Vector2(706f, 852f), Parchment);
            Image rightPage = Box(cover.transform, "RightPage", new Vector2(357f, 0f), new Vector2(706f, 852f), Parchment);
            Image spine = Box(cover.transform, "Spine", Vector2.zero, new Vector2(16f, 852f), new Color(0.55f, 0.45f, 0.30f, 0.6f));
            Image ribbon = Box(cover.transform, "Ribbon", new Vector2(0f, 230f), new Vector2(22f, 460f), new Color(0.55f, 0.11f, 0.11f, 1f));
            ribbon.raycastTarget = false; spine.raycastTarget = false;

            _left = PageText(leftPage.transform);
            _right = PageText(rightPage.transform);
            _leftNum = Label(leftPage.transform, "Num", new Vector2(0f, -400f), new Vector2(120f, 30f), 20f, new Color(0.48f, 0.38f, 0.25f), TextAlignmentOptions.Center);
            _rightNum = Label(rightPage.transform, "Num", new Vector2(0f, -400f), new Vector2(120f, 30f), 20f, new Color(0.48f, 0.38f, 0.25f), TextAlignmentOptions.Center);

            // the page corners turn too
            Button prev = ButtonAt(leftPage.transform, "TurnBack", new Vector2(-318f, -390f), new Vector2(60f, 60f), "◀");
            prev.onClick.AddListener(() => Turn(-1));
            Button next = ButtonAt(rightPage.transform, "TurnOn", new Vector2(318f, -390f), new Vector2(60f, 60f), "▶");
            next.onClick.AddListener(() => Turn(1));

            Label(_root.transform, "Hint", new Vector2(0f, -500f), new Vector2(1200f, 30f), 18f, new Color(0.91f, 0.86f, 0.75f),
                TextAlignmentOptions.Center).text = "A / D, arrows, wheel or the corners to turn   ·   Esc closes";

            _root.SetActive(false);
        }

        private static TextMeshProUGUI PageText(Transform page)
        {
            TextMeshProUGUI t = Label(page, "Text", new Vector2(0f, 20f), new Vector2(600f, 740f), 24f, Ink, TextAlignmentOptions.TopLeft);
            t.overflowMode = TextOverflowModes.Page;
            t.enableWordWrapping = true;
            t.richText = true;
            Emoji.Apply(t);
            return t;
        }

        internal static Image Box(Transform parent, string name, Vector2 pos, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var img = go.GetComponent<Image>();
            img.color = color;
            return img;
        }

        internal static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        internal static TextMeshProUGUI Label(Transform parent, string name, Vector2 pos, Vector2 size, float fontSize, Color color, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var t = go.GetComponent<TextMeshProUGUI>();
            FiresCore.UI.UIFontConfig.ApplyGameFont(t);
            t.fontSize = fontSize; t.color = color; t.alignment = align; t.raycastTarget = false;
            return t;
        }

        internal static Button ButtonAt(Transform parent, string name, Vector2 pos, Vector2 size, string text)
        {
            Image bg = Box(parent, name, pos, size, new Color(0.35f, 0.23f, 0.12f, 0.95f));
            var b = bg.gameObject.AddComponent<Button>();
            b.targetGraphic = bg;
            TextMeshProUGUI l = Label(bg.transform, "Label", Vector2.zero, size, 22f, new Color(0.94f, 0.89f, 0.77f), TextAlignmentOptions.Center);
            l.text = text;
            return b;
        }
    }
}
