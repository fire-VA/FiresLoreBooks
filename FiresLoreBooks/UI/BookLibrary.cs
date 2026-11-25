using System.Collections.Generic;
using FiresLoreBooks.Books;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FiresLoreBooks.UI
{
    /// <summary>
    /// The admin book library (Fire 10-12, option a): every book on the server with Open / Delete and a New book button.
    /// Opened on a placed lore book (Shift+E) it is also that book's picker: "Show here" points the placed book at an
    /// existing book, "Write new here" gives it a fresh book and opens the editor, "Edit this book" edits the one it shows.
    /// Admin only; the server checks admin again on every save and delete.
    /// </summary>
    public sealed class BookLibrary : MonoBehaviour
    {
        public static BookLibrary Instance { get; private set; }
        public static bool IsOpen => Instance != null && Instance._open;

        private const int RowsPerPage = 11;

        private GameObject _root;
        private RectTransform _rows;
        private TextMeshProUGUI _head, _status, _pageLabel;
        private Button _editThis, _writeHere;
        private readonly List<BookStore.Entry> _books = new List<BookStore.Entry>();
        private LoreBookPiece _target;          // the placed book being pointed, or null
        private string _armedDelete;            // a Delete needs a second click on the same row
        private int _page;
        private bool _open;
        private readonly object _inputToken = new object();

        /// <summary>The library on its own (console lorebook_library).</summary>
        public static void Open() => OpenFor(null);

        /// <summary>The library as the picker of one placed book (Shift+E on it).</summary>
        public static void OpenFor(LoreBookPiece target)
        {
            if (!BookEditor.IsLocalAdmin())
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Only admins can edit lore books.");
                return;
            }
            if (Instance == null)
            {
                var go = new GameObject("FiresLoreBooks_Library");
                DontDestroyOnLoad(go);
                Instance = go.AddComponent<BookLibrary>();
                Instance.Build();
            }
            BookReader.Close();
            Instance.Show(target);
        }

        public static void Close() { if (Instance != null) Instance.Hide(); }

        /// <summary>The books last listed (tests read it).</summary>
        public static IReadOnlyList<BookStore.Entry> Books => Instance != null ? Instance._books : new List<BookStore.Entry>();

        private void Show(LoreBookPiece target)
        {
            _target = target;
            _open = true;
            _root.SetActive(true);
            InputGate.Acquire(_inputToken, this);   // 0.0.18: owner-held (Core 0.2.697)
            _head.text = target != null ? "Lore books: choose what this book shows (admin)" : "Lore books (admin)";
            _editThis.gameObject.SetActive(target != null);
            _writeHere.gameObject.SetActive(target != null);
            _armedDelete = null;
            _page = 0;
            FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] library opened{(target != null ? " for placed book '" + target.BookId + "'" : "")}");
            Refresh();
        }

        private void Hide()
        {
            if (!_open) return;
            _open = false;
            _root.SetActive(false);
            FiresCore.Input.FiresInputBlock.Release(_inputToken);
        }

        private void Update()
        {
            if (_open && Input.GetKeyDown(KeyCode.Escape)) Hide();
        }

        internal void Refresh()
        {
            _status.text = "loading...";
            BookStore.List(list =>
            {
                if (!_open) return;
                _books.Clear();
                _books.AddRange(list);
                _page = Mathf.Clamp(_page, 0, Mathf.Max(0, (_books.Count - 1) / RowsPerPage));
                _status.text = _books.Count == 0 ? "No books on the server yet: New book writes the first one." : "";
                DrawRows();
            });
        }

        private void DrawRows()
        {
            foreach (Transform c in _rows) Destroy(c.gameObject);
            int first = _page * RowsPerPage;
            for (int i = first; i < Mathf.Min(_books.Count, first + RowsPerPage); i++)
            {
                BookStore.Entry e = _books[i];
                float y = 300f - (i - first) * 62f;
                bool current = _target != null && e.Id == _target.BookId;
                string name = (e.Title.Length > 0 ? e.Title : "(untitled)") + $"   <size=70%><color=#7a6a50>{e.Id} · {e.Pages} page{(e.Pages == 1 ? "" : "s")}</color></size>";
                BookReader.Label(_rows, "Row_" + e.Id, new Vector2(-260f, y), new Vector2(820f, 50f), 24f,
                    current ? new Color(0.55f, 0.11f, 0.11f) : BookReader.Ink, TextAlignmentOptions.MidlineLeft).text = (current ? "» " : "") + name;
                string id = e.Id;
                float x = 260f;
                if (_target != null)
                {
                    BookReader.ButtonAt(_rows, "Here_" + id, new Vector2(x, y), new Vector2(150f, 44f), current ? "Shown" : "Show here")
                        .onClick.AddListener(() => PointTarget(id, false));
                    x += 160f;
                }
                BookReader.ButtonAt(_rows, "Open_" + id, new Vector2(x, y), new Vector2(100f, 44f), "Open").onClick.AddListener(() => OpenEditor(id));
                BookReader.ButtonAt(_rows, "Del_" + id, new Vector2(x + 115f, y), new Vector2(120f, 44f), _armedDelete == id ? "Sure?" : "Delete")
                    .onClick.AddListener(() => DeleteBook(id));
            }
            int pages = Mathf.Max(1, (_books.Count + RowsPerPage - 1) / RowsPerPage);
            _pageLabel.text = pages > 1 ? $"{_page + 1} / {pages}" : "";
        }

        private void GoPage(int d)
        {
            int pages = Mathf.Max(1, (_books.Count + RowsPerPage - 1) / RowsPerPage);
            _page = Mathf.Clamp(_page + d, 0, pages - 1);
            DrawRows();
        }

        /// <summary>New book: a fresh id, straight into the editor (it exists on the server once saved).</summary>
        internal void NewBook()
        {
            string id = BookStore.NewId();
            FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] library: new book '{id}'");
            OpenEditor(id);
        }

        private void OpenEditor(string id)
        {
            Hide();
            BookEditor.Open(id);
        }

        private void DeleteBook(string id)
        {
            if (_armedDelete != id) { _armedDelete = id; DrawRows(); return; }
            _armedDelete = null;
            _status.text = "deleting...";
            BookStore.Delete(id, err =>
            {
                _status.text = string.IsNullOrEmpty(err) ? "deleted" : "not deleted: " + err;
                if (_open) Refresh();
            });
        }

        /// <summary>Points the placed book at a book id (an existing one, or a fresh one when <paramref name="fresh"/>).</summary>
        internal void PointTarget(string id, bool fresh)
        {
            if (_target == null) return;
            string before = _target.BookId;
            if (!_target.SetBookId(id)) { _status.text = "could not change this book (no network view)"; return; }
            FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] placed book now shows '{id}' (was '{before}')");
            if (fresh) OpenEditor(id);
            else { _status.text = "this book now shows it"; DrawRows(); }
        }

        // ================= layout =================

        private void Build()
        {
            _root = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            _root.transform.SetParent(transform, false);
            var canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 523;
            var scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;

            BookReader.Stretch(BookReader.Box(_root.transform, "Dim", Vector2.zero, Vector2.zero, new Color(0f, 0f, 0f, 0.55f)).rectTransform);
            Image cover = BookReader.Box(_root.transform, "Cover", new Vector2(0f, -20f), new Vector2(1500f, 900f), BookReader.Leather);
            var edge = cover.gameObject.AddComponent<Outline>();
            edge.effectColor = BookReader.LeatherEdge; edge.effectDistance = new Vector2(4f, -4f);
            Image page = BookReader.Box(cover.transform, "Page", Vector2.zero, new Vector2(1420f, 852f), BookReader.Parchment);

            _head = BookReader.Label(_root.transform, "Head", new Vector2(-300f, 470f), new Vector2(900f, 50f), 30f, new Color(0.95f, 0.78f, 0.42f), TextAlignmentOptions.MidlineLeft);
            BookReader.ButtonAt(_root.transform, "New", new Vector2(470f, 470f), new Vector2(170f, 44f), "New book").onClick.AddListener(NewBook);
            BookReader.ButtonAt(_root.transform, "Close", new Vector2(650f, 470f), new Vector2(140f, 44f), "Close").onClick.AddListener(Hide);

            _editThis = BookReader.ButtonAt(page.transform, "EditThis", new Vector2(-450f, 370f), new Vector2(260f, 44f), "Edit this book");
            _editThis.onClick.AddListener(() => { if (_target != null) OpenEditor(_target.BookId); });
            _writeHere = BookReader.ButtonAt(page.transform, "WriteHere", new Vector2(-170f, 370f), new Vector2(260f, 44f), "Write new here");
            _writeHere.onClick.AddListener(() => PointTarget(BookStore.NewId(), true));

            var rowsGo = new GameObject("Rows", typeof(RectTransform));
            rowsGo.transform.SetParent(page.transform, false);
            _rows = (RectTransform)rowsGo.transform;

            BookReader.ButtonAt(page.transform, "Prev", new Vector2(-640f, -390f), new Vector2(60f, 44f), "<").onClick.AddListener(() => GoPage(-1));
            BookReader.ButtonAt(page.transform, "Next", new Vector2(640f, -390f), new Vector2(60f, 44f), ">").onClick.AddListener(() => GoPage(1));
            _pageLabel = BookReader.Label(page.transform, "PageNum", new Vector2(0f, -390f), new Vector2(200f, 30f), 20f, BookReader.Ink, TextAlignmentOptions.Center);
            _status = BookReader.Label(page.transform, "Status", new Vector2(0f, -350f), new Vector2(1200f, 30f), 20f, BookReader.Ink, TextAlignmentOptions.Center);

            BookReader.Label(_root.transform, "Hint", new Vector2(0f, -500f), new Vector2(1200f, 30f), 18f, new Color(0.91f, 0.86f, 0.75f),
                TextAlignmentOptions.Center).text = "Delete asks twice   ·   Esc closes";
            _root.SetActive(false);
        }
    }
}
