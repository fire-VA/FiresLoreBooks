using System;
using System.Collections.Generic;
using System.Text;
using FiresLoreBooks.Books;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FiresLoreBooks.UI
{
    /// <summary>
    /// The journal (mockup 3 + Fire's lock rules 10-09). Left page: the entries, each under its author and date, turned
    /// like the reader. Right page: write a new entry, or edit one of the entries you may edit. Header: the lock toggle
    /// (creator / admin; others see "Locked: read-only" or "Open to all"), Give to… (creator), Take picture.
    /// Anyone can read; the server decides every write and answers with what this reader may do.
    /// </summary>
    public sealed class JournalView : MonoBehaviour
    {
        public static JournalView Instance { get; private set; }
        public static bool IsOpen => Instance != null && Instance._open;

        private GameObject _root;
        private TextMeshProUGUI _entries, _lockLabel, _num, _status, _editLabel;
        private TMP_InputField _input, _giveTo;
        private Button _lockButton, _giveButton, _pictureButton, _addButton, _deleteButton;
        private Transform _editList;
        private JournalStore.View _view;
        private string _folder;
        private int _spread, _pageCount = 1, _editing = -1;
        private bool _open;
        private readonly object _inputToken = new object();

        public static void Open(string folder)
        {
            if (Instance == null)
            {
                var go = new GameObject("FiresLoreBooks_Journal");
                DontDestroyOnLoad(go);
                Instance = go.AddComponent<JournalView>();
                Instance.Build();
            }
            BookReader.Close();
            Instance.Show(folder);
        }

        public static void Close() { if (Instance != null) Instance.Hide(); }

        private void OnEnable() => JournalStore.Arrived += OnArrived;
        private void OnDisable() => JournalStore.Arrived -= OnArrived;

        private void Show(string folder)
        {
            _folder = folder;
            _open = true;
            _editing = -1;
            _spread = 0;
            _root.SetActive(true);
            FiresCore.Input.FiresInputBlock.Acquire(_inputToken);
            _status.text = "loading…";
            JournalStore.Request(folder);
        }

        private void Hide()
        {
            if (!_open) return;
            _open = false;
            _root.SetActive(false);
            FiresCore.Input.FiresInputBlock.Release(_inputToken);
        }

        private void OnArrived(JournalStore.View v)
        {
            if (!_open || v.Folder != _folder) return;
            _view = v;
            _status.text = "";
            var sb = new StringBuilder();
            for (int i = 0; i < v.Entries.Count; i++)
            {
                JournalStore.Entry e = v.Entries[i];
                string when = DateTimeOffset.FromUnixTimeSeconds(e.Time).LocalDateTime.ToString("d MMM yyyy, HH:mm");
                if (i > 0) sb.Append("\n\n");
                sb.Append($"<size=80%><color=#7a6040>#{i + 1} · {e.AuthorName} · {when}</color></size>\n");
                sb.Append(e.Text);
            }
            _jsource = v.Entries.Count == 0 ? "<i>No entries yet.</i>" : sb.ToString();
            _entries.text = FiresPictures.PageLayout.Parse(_jsource).text;
            _entries.ForceMeshUpdate();
            _pageCount = Mathf.Max(1, _entries.textInfo.pageCount);
            _spread = Mathf.Clamp(_spread, 0, _pageCount - 1);
            _entries.pageToDisplay = _spread + 1;
            ShowPictures();
            _num.text = $"{_spread + 1} / {_pageCount}";

            _lockLabel.text = v.IsCreator ? (v.Locked ? "Locked" : "Open to all") : (v.Locked ? "Locked: read-only" : "Open to all: add an entry");
            _lockButton.gameObject.SetActive(v.IsCreator);
            _lockButton.GetComponentInChildren<TextMeshProUGUI>().text = v.Locked ? "Unlock" : "Lock";
            _giveButton.interactable = v.IsCreator;
            _giveTo.interactable = v.IsCreator;
            _pictureButton.interactable = v.CanAdd;
            _input.interactable = v.CanAdd || _editing >= 0;
            _addButton.interactable = v.CanAdd || _editing >= 0;
            RebuildEditList();
        }

        // pictures are their own entries: "<img id=\"Journals/…/p_x.jpg\">"; v1 shows a picture line (inline images: later)
        private string _jsource = "";

        /// <summary>Inline pictures ([fgn]'s PageLayout) on the visible page; the text is reset first (wrap-around rewrites it).</summary>
        private void ShowPictures()
        {
            if (string.IsNullOrEmpty(_folder)) return;
            var parsed = FiresPictures.PageLayout.Parse(_jsource);
            _entries.text = parsed.text;
            FiresPictures.PageLayout.Show(_entries.rectTransform, _entries, _folder, parsed.imgs, false, null);
        }

        private static string PictureText(string text) =>
            System.Text.RegularExpressions.Regex.Replace(text, "<img[^>]*>", "<color=#2f6fb6>[picture]</color>");

        private void RebuildEditList()
        {
            foreach (Transform c in _editList) Destroy(c.gameObject);
            if (_view == null) return;
            float y = 0f;
            for (int i = 0; i < _view.Entries.Count; i++)
            {
                if (!_view.Entries[i].CanEdit) continue;
                int index = i;
                Button b = BookReader.ButtonAt(_editList, "Edit" + i, new Vector2(-250f + (y % 5) * 125f, -Mathf.Floor(y / 5) * 46f), new Vector2(118f, 40f), $"Edit #{i + 1}");
                b.onClick.AddListener(() => BeginEdit(index));
                y++;
            }
        }

        private void BeginEdit(int index)
        {
            _editing = index;
            _input.text = _view.Entries[index].Text;
            _editLabel.text = $"Editing entry #{index + 1}";
            _addButton.GetComponentInChildren<TextMeshProUGUI>().text = "Save entry";
            _deleteButton.gameObject.SetActive(true);
            _input.interactable = _addButton.interactable = true;
        }

        private void EndEdit()
        {
            _editing = -1;
            _input.text = "";
            _editLabel.text = "New entry";
            _addButton.GetComponentInChildren<TextMeshProUGUI>().text = "Add entry";
            _deleteButton.gameObject.SetActive(false);
        }

        private void Submit()
        {
            if (string.IsNullOrWhiteSpace(_input.text)) return;
            if (_editing >= 0) JournalStore.Edit(_folder, _editing, _input.text);
            else JournalStore.Add(_folder, _input.text);
            EndEdit();
            _status.text = "sent…";
        }

        private void Delete()
        {
            if (_editing < 0) return;
            JournalStore.Edit(_folder, _editing, "");
            EndEdit();
        }

        private void ToggleLock()
        {
            if (_view == null) return;
            FiresPictures.JournalOwners.SetLocked(_folder, !_view.Locked, err =>
            {
                _status.text = string.IsNullOrEmpty(err) ? "" : "lock: " + err;
                JournalStore.Request(_folder);
            });
        }

        private void Give()
        {
            string who = _giveTo.text.Trim();
            if (who.Length == 0) return;
            FiresPictures.JournalOwners.Give(_folder, who, err =>
            {
                _status.text = string.IsNullOrEmpty(err) ? $"given to {who}" : "give: " + err;
                JournalStore.Request(_folder);
            });
        }

        private void TakePicture()
        {
            Hide();
            FiresPictures.Camera.Begin("4:3", tex =>
            {
                Open(_folder);
                FiresPictures.Store.Upload(_folder, tex, pic => JournalStore.Add(_folder, FiresPictures.PageLayout.Write(new FiresPictures.ImgSpec { Src = pic, X = 0f, Y = 0f, W = 1f, WrapAround = false })),
                    err => _status.text = "picture: " + err);
            }, () => Open(_folder));
        }

        private void Turn(int d)
        {
            _spread = Mathf.Clamp(_spread + d, 0, _pageCount - 1);
            _entries.pageToDisplay = _spread + 1;
            ShowPictures();
            _num.text = $"{_spread + 1} / {_pageCount}";
        }

        private void Update()
        {
            if (!_open) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (Player.m_localPlayer == null) { Hide(); return; }
            if (Typing.InTextInput()) return;
            if (FiresCore.Input.FiresKeys.GetKeyDown(KeyCode.RightArrow)) Turn(1);
            else if (FiresCore.Input.FiresKeys.GetKeyDown(KeyCode.LeftArrow)) Turn(-1);
        }

        [HarmonyPatch(typeof(Menu), nameof(Menu.Show))]
        private static class EscClosesJournal
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
            canvas.sortingOrder = 522;
            var scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;

            BookReader.Stretch(BookReader.Box(_root.transform, "Dim", Vector2.zero, Vector2.zero, new Color(0f, 0f, 0f, 0.55f)).rectTransform);
            Image cover = BookReader.Box(_root.transform, "Cover", new Vector2(0f, -30f), new Vector2(1500f, 880f), BookReader.Leather);
            var edge = cover.gameObject.AddComponent<Outline>();
            edge.effectColor = BookReader.LeatherEdge; edge.effectDistance = new Vector2(4f, -4f);

            Color gold = new Color(0.95f, 0.78f, 0.42f);
            BookReader.Label(_root.transform, "Head", new Vector2(-560f, 490f), new Vector2(360f, 44f), 30f, gold, TextAlignmentOptions.MidlineLeft).text = "Journal";
            _lockLabel = BookReader.Label(_root.transform, "LockLabel", new Vector2(-250f, 490f), new Vector2(300f, 40f), 22f, gold, TextAlignmentOptions.MidlineLeft);
            _lockButton = BookReader.ButtonAt(_root.transform, "Lock", new Vector2(-40f, 490f), new Vector2(110f, 40f), "Lock");
            _lockButton.onClick.AddListener(ToggleLock);
            _pictureButton = BookReader.ButtonAt(_root.transform, "Picture", new Vector2(110f, 490f), new Vector2(170f, 40f), "Take picture");
            _pictureButton.onClick.AddListener(TakePicture);
            _giveTo = EditorInput(_root.transform, "GiveTo", new Vector2(320f, 490f), new Vector2(220f, 40f), 20f, false);
            _giveButton = BookReader.ButtonAt(_root.transform, "Give", new Vector2(500f, 490f), new Vector2(120f, 40f), "Give to…");
            _giveButton.onClick.AddListener(Give);
            BookReader.ButtonAt(_root.transform, "Close", new Vector2(660f, 490f), new Vector2(110f, 40f), "Close").onClick.AddListener(Hide);

            Image left = BookReader.Box(cover.transform, "LeftPage", new Vector2(-357f, 0f), new Vector2(706f, 832f), BookReader.Parchment);
            Image right = BookReader.Box(cover.transform, "RightPage", new Vector2(357f, 0f), new Vector2(706f, 832f), BookReader.Parchment);
            _entries = BookReader.Label(left.transform, "Entries", new Vector2(0f, 20f), new Vector2(600f, 720f), 22f, BookReader.Ink, TextAlignmentOptions.TopLeft);
            _entries.overflowMode = TextOverflowModes.Page;
            Emoji.Apply(_entries);
            _entries.enableWordWrapping = true;
            _entries.richText = true;
            _num = BookReader.Label(left.transform, "Num", new Vector2(0f, -385f), new Vector2(200f, 30f), 20f, new Color(0.48f, 0.38f, 0.25f), TextAlignmentOptions.Center);
            BookReader.ButtonAt(left.transform, "Prev", new Vector2(-280f, -385f), new Vector2(60f, 40f), "◀").onClick.AddListener(() => Turn(-1));
            BookReader.ButtonAt(left.transform, "Next", new Vector2(280f, -385f), new Vector2(60f, 40f), "▶").onClick.AddListener(() => Turn(1));

            Color ink = new Color(0.48f, 0.38f, 0.25f);
            _editLabel = BookReader.Label(right.transform, "EditLabel", new Vector2(0f, 380f), new Vector2(620f, 30f), 22f, ink, TextAlignmentOptions.MidlineLeft);
            _editLabel.text = "New entry";
            _input = EditorInput(right.transform, "Input", new Vector2(0f, 150f), new Vector2(620f, 420f), 22f, true);
            _addButton = BookReader.ButtonAt(right.transform, "Add", new Vector2(-180f, -95f), new Vector2(200f, 44f), "Add entry");
            _addButton.onClick.AddListener(Submit);
            _deleteButton = BookReader.ButtonAt(right.transform, "Delete", new Vector2(40f, -95f), new Vector2(180f, 44f), "Delete entry");
            _deleteButton.onClick.AddListener(Delete);
            _deleteButton.gameObject.SetActive(false);
            BookReader.ButtonAt(right.transform, "Cancel", new Vector2(240f, -95f), new Vector2(130f, 44f), "Cancel").onClick.AddListener(EndEdit);
            BookReader.Label(right.transform, "EditHint", new Vector2(0f, -150f), new Vector2(620f, 30f), 18f, ink, TextAlignmentOptions.MidlineLeft).text = "Entries you may edit:";
            var list = new GameObject("EditList", typeof(RectTransform));
            list.transform.SetParent(right.transform, false);
            ((RectTransform)list.transform).anchoredPosition = new Vector2(0f, -200f);
            _editList = list.transform;
            _status = BookReader.Label(right.transform, "Status", new Vector2(0f, -385f), new Vector2(620f, 30f), 20f, ink, TextAlignmentOptions.Center);

            _root.SetActive(false);
        }

        private static TMP_InputField EditorInput(Transform parent, string name, Vector2 pos, Vector2 size, float fontSize, bool multiLine)
        {
            Image bg = BookReader.Box(parent, name, pos, size, new Color(1f, 1f, 1f, 0.25f));
            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewport.transform.SetParent(bg.transform, false);
            var vrt = (RectTransform)viewport.transform;
            BookReader.Stretch(vrt);
            vrt.offsetMin = new Vector2(8f, 6f); vrt.offsetMax = new Vector2(-8f, -6f);
            TextMeshProUGUI text = BookReader.Label(viewport.transform, "Text", Vector2.zero, Vector2.zero, fontSize, BookReader.Ink, multiLine ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft);
            BookReader.Stretch(text.rectTransform);
            text.richText = multiLine;
            text.enableWordWrapping = multiLine;
            if (multiLine) Emoji.Apply(text);
            var field = bg.gameObject.AddComponent<TMP_InputField>();
            field.textViewport = vrt;
            field.textComponent = text;
            field.richText = multiLine;
            field.lineType = multiLine ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
            field.characterLimit = multiLine ? RichText.MaxPageChars : 40;
            field.fontAsset = text.font;
            field.pointSize = fontSize;
            return field;
        }
    }
}
