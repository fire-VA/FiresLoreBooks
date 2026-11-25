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
    /// The journal (mockup 3 + Fire's lock rules 10-09). Header: the lock toggle (creator / admin; others see "Locked: read-only"
    /// or "Open to all"), the Edit pill, Close. Anyone can read; the server decides every write and answers with what this
    /// reader may do.
    /// 0.0.16 (Fire's journal asks): by default the journal is a clean book, both pages filled with the entries, no handles. The
    /// Edit pill (a toggle) brings the old layout back: entries on the left, the entry editor on the right, Take picture and Give
    /// in the header, and the pictures of entries you may edit get small corner buttons on double-click. A picture can be pinned
    /// anywhere on any page (tag attribute page=, x / y free), pictures of different entries can share a page, and the text
    /// flows around pinned pictures (each line takes the widest free span beside the pictures on its band; a line with no
    /// useful span is pushed below them). Pictures without page= keep the 0.0.15 in-flow layout until they are moved.
    /// While open the cursor is free and the camera takes no mouse look (Core's InputBlock, as FiresGuilds / FDM do).
    /// </summary>
    public sealed class JournalView : MonoBehaviour
    {
        public static JournalView Instance { get; private set; }
        public static bool IsOpen => Instance != null && Instance._open;

        private const string RegistryKey = "FiresLoreBooks_Journal";

        private GameObject _root, _editor;
        private TextMeshProUGUI _entries, _entriesRight, _measure, _lockLabel, _num, _status, _editLabel, _editPillLabel;
        private TMP_InputField _input, _giveTo;
        private Button _lockButton, _giveButton, _pictureButton, _addButton, _deleteButton, _editPill;
        private Transform _editList;
        private JournalStore.View _view;
        private string _folder;
        private int _page, _pageCount = 1, _textPages = 1, _editing = -1;
        private bool _open, _editMode, _blocked, _relayoutQueued;
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

        private void OnEnable()
        {
            JournalStore.Arrived += OnArrived;
            FiresPictures.PictureFrame.AspectLearned += OnAspectLearned;
        }

        private void OnDisable()
        {
            JournalStore.Arrived -= OnArrived;
            FiresPictures.PictureFrame.AspectLearned -= OnAspectLearned;
        }

        private void Show(string folder)
        {
            if (folder != _folder) _page = 0;
            _folder = folder;
            _open = true;
            _editing = -1;
            _editMode = false;   // Fire 0.0.16: always opens as the clean book
            _root.SetActive(true);
            ApplyMode();
            InputGate.Acquire(_inputToken, this);   // 0.0.18: owner-held (Core 0.2.697)
            // Fire 0.0.16: a free, visible cursor and no mouse look while the journal is open. Core's InputBlock is the one keeper
            // (its GameCamera.UpdateMouseCapture gate frees the cursor every frame, PlayerController.TakeInput stops the look);
            // the same Block(true) / Block(false) pair FiresGuilds' GuildScreenController and FDM's panels use.
            try { if (!FiresCore.UI.InputBlock.IsBlocked) { FiresCore.UI.InputBlock.Block(true); _blocked = true; } }
            catch (Exception e) { FiresLoreBooksPlugin.Log.LogWarning($"[LoreBooks] journal: InputBlock.Block threw {e.Message}"); }
            _status.text = "loading...";
            JournalStore.Request(folder);
        }

        private void Hide()
        {
            if (!_open) return;
            _open = false;
            _root.SetActive(false);
            FiresCore.Input.FiresInputBlock.Release(_inputToken);
            if (_blocked) { try { FiresCore.UI.InputBlock.Block(false); } catch { } _blocked = false; }
        }

        private void OnArrived(JournalStore.View v)
        {
            if (!_open || v.Folder != _folder) return;
            _view = v;
            _status.text = "";
            Relayout();

            bool canEditAny = v.CanAdd;
            foreach (JournalStore.Entry e in v.Entries) canEditAny |= e.CanEdit;
            _editPill.gameObject.SetActive(canEditAny || _editMode);
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

        private void OnAspectLearned(string src)
        {
            if (!_open) return;
            foreach (Pic p in _pics) if (p.Spec.Src == src) { _relayoutQueued = true; return; }
        }

        // ================= layout =================

        private string _jsource = "";

        /// <summary>One picture of the journal: which entry holds it, which of that entry's pictures it is, its stored spec.</summary>
        private struct Pic { public int Entry, Nth; public FiresPictures.ImgSpec Spec; }
        private readonly List<Pic> _pics = new List<Pic>();
        /// <summary>Where each picture (same index as _pics) is drawn after the layout: its page and top (fraction of the area).</summary>
        private readonly List<(int page, float y)> _placed = new List<(int, float)>();
        private static readonly System.Text.RegularExpressions.Regex ImgTag = new System.Text.RegularExpressions.Regex(@"<img\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// Fire 10-12 #5: a journal is read top to bottom, so its in-flow pictures (no page=) take their place IN the flow: the
        /// tag is followed by blank lines as tall as the picture. 0.0.16: a pinned picture (page=) reserves nothing; the text
        /// wraps around it instead (WrapAroundPinned).
        /// </summary>
        private string BuildSource(JournalStore.View v)
        {
            _pics.Clear();
            float lineH = LineHeight();
            float areaW = _measure.rectTransform.rect.width;
            var sb = new StringBuilder();
            for (int i = 0; i < v.Entries.Count; i++)
            {
                JournalStore.Entry e = v.Entries[i];
                string when = DateTimeOffset.FromUnixTimeSeconds(e.Time).LocalDateTime.ToString("d MMM yyyy, HH:mm");
                if (i > 0) sb.Append("\n\n");
                sb.Append($"<size=80%><color=#7a6040>#{i + 1} · {e.AuthorName} · {when}</color></size>\n");
                int nth = 0, entry = i;
                sb.Append(ImgTag.Replace(e.Text ?? "", m =>
                {
                    var parsed = FiresPictures.PageLayout.Parse(m.Value).imgs;
                    if (parsed.Count == 0) return m.Value;
                    FiresPictures.ImgSpec spec = parsed[0];
                    _pics.Add(new Pic { Entry = entry, Nth = nth++, Spec = spec });
                    if (spec.Page >= 0) return m.Value;   // pinned: the text flows around it
                    float h = RotatedHeight(spec, areaW);
                    int lines = Mathf.Max(1, Mathf.CeilToInt(h / Mathf.Max(1f, lineH)));
                    return m.Value + new string('\n', lines + 1);
                }));
            }
            return sb.ToString();
        }

        /// <summary>
        /// The height a picture really covers on the page: its frame (picture + caption) turned by its rotation, as the
        /// bounding box of the rotated rectangle. Fire 10-12: rotated pictures; at 90° the frame's WIDTH becomes its height.
        /// </summary>
        internal static float RotatedHeight(FiresPictures.ImgSpec spec, float areaW) =>
            FiresPictures.PictureFrame.RotatedBox(spec, areaW, 1f).height;   // the drawn frame's box (photo + border + caption)

        private float _lineH;

        /// <summary>The entries text's line height in pixels (measured once).</summary>
        private float LineHeight()
        {
            if (_lineH > 0f) return _lineH;
            float w = _measure.rectTransform.rect.width;
            _lineH = _measure.GetPreferredValues("A\nA", w, 0f).y - _measure.GetPreferredValues("A", w, 0f).y;
            if (_lineH <= 1f) _lineH = _measure.fontSize * 1.25f;
            return _lineH;
        }

        private void Measure(string text)
        {
            _measure.text = text;
            _measure.ForceMeshUpdate();
        }

        /// <summary>Builds the journal's text, wraps it around the pinned pictures, pages it and places every picture.</summary>
        private void Relayout()
        {
            _relayoutQueued = false;
            if (_view == null) return;
            _jsource = _view.Entries.Count == 0 ? "<i>No entries yet.</i>" : BuildSource(_view);
            string text = FiresPictures.PageLayout.Parse(_jsource).text;
            text = WrapAroundPinned(text);
            Measure(text);
            TMP_TextInfo info = _measure.textInfo;
            _textPages = Mathf.Max(1, info.pageCount);

            // the k-th anchor character is the k-th picture (Parse and BuildSource walk the tags in the same order)
            var anchors = new List<int>();
            for (int c = 0; c < info.characterCount; c++)
                if (info.characterInfo[c].character == FiresPictures.PageLayout.AnchorChar) anchors.Add(c);
            float areaW = _measure.rectTransform.rect.width, areaH = Mathf.Max(1f, _measure.rectTransform.rect.height);
            _placed.Clear();
            int lastPage = _textPages - 1;
            for (int j = 0; j < _pics.Count; j++)
            {
                FiresPictures.ImgSpec s = _pics[j].Spec;
                if (s.Page >= 0) { _placed.Add((s.Page, Mathf.Clamp(s.Y, 0f, 0.95f))); lastPage = Mathf.Max(lastPage, s.Page); continue; }
                int page = 0;
                float y = 0f;
                if (j < anchors.Count)
                {
                    TMP_CharacterInfo ci = info.characterInfo[anchors[j]];
                    page = ci.pageNumber;
                    y = (_measure.rectTransform.rect.yMax - info.lineInfo[ci.lineNumber].descender) / areaH;
                }
                // The frame turns about its centre, so a rotated picture's box also grows upward by half the extra height:
                // start it that much lower, so the whole rotated box sits under its anchor line, inside the reserved space.
                float flat = FiresPictures.PictureFrame.HeightFraction(s) * areaW + FiresPictures.PictureFrame.CaptionSpace(s);
                y += Mathf.Max(0f, RotatedHeight(s, areaW) - flat) * 0.5f / areaH;
                _placed.Add((page, Mathf.Clamp01(y)));
            }
            _pageCount = Mathf.Max(_textPages, lastPage + 1);
            _page = Mathf.Clamp(_page, 0, _pageCount - 1);
            _entries.text = text;
            _entriesRight.text = text;
            ShowPage();
        }

        // ---- text around pinned pictures (Fire 0.0.16 #3) ----

        private const float WrapGap = 8f, MinSpan = 140f;

        /// <summary>
        /// Walks the pages in order; on each page with pinned pictures, every line whose band meets a picture gets TMP margins
        /// that leave it the widest free span beside them (one &lt;margin&gt; run per band where the same pictures are met). A line
        /// with no span of at least MinSpan is pushed below the pictures. Each change is laid out again before the next, so
        /// the page breaks that follow stay true; lines above a change never move.
        /// </summary>
        private string WrapAroundPinned(string text)
        {
            var byPage = new SortedDictionary<int, List<Rect>>();
            float areaW = _measure.rectTransform.rect.width, areaH = Mathf.Max(1f, _measure.rectTransform.rect.height);
            foreach (Pic p in _pics)
            {
                if (p.Spec.Page < 0) continue;
                Rect box = FiresPictures.PictureFrame.RotatedBox(p.Spec, areaW, areaH);
                box = Rect.MinMaxRect(Mathf.Clamp(box.xMin, 0f, areaW), Mathf.Clamp(box.yMin, 0f, areaH), Mathf.Clamp(box.xMax, 0f, areaW), Mathf.Clamp(box.yMax, 0f, areaH));
                if (box.width <= 1f || box.height <= 1f) continue;
                if (!byPage.TryGetValue(p.Spec.Page, out var list)) byPage[p.Spec.Page] = list = new List<Rect>();
                if (list.Count < 31) list.Add(box);
            }
            if (byPage.Count == 0) return text;
            float lineH = LineHeight();
            int layouts = 0;
            foreach (var kv in byPage)
            {
                int page = kv.Key;
                List<Rect> boxes = kv.Value;
                int cursor = 0;
                for (int guard = 0; guard < 300; guard++)
                {
                    Measure(text); layouts++;
                    TMP_TextInfo info = _measure.textInfo;
                    if (info.pageCount > 0 && page >= info.pageCount) break;   // the text ends before this page
                    int line = NextBandLine(info, page, cursor, boxes, out int mask);
                    if (line < 0) break;
                    int at = BackOverTags(text, info.characterInfo[info.lineInfo[line].firstCharacterIndex].index);
                    LineBand(info, line, out float lineTop, out _);
                    if (!FreeSpan(boxes, mask, areaW, out float left, out float right))
                    {
                        float bottom = 0f;
                        for (int b = 0; b < boxes.Count; b++) if ((mask & (1 << b)) != 0) bottom = Mathf.Max(bottom, boxes[b].yMax);
                        int n = Mathf.Max(1, Mathf.CeilToInt((bottom + WrapGap - lineTop) / Mathf.Max(1f, lineH))) + (SoftBreak(text, at) ? 1 : 0);
                        text = text.Insert(at, new string('\n', n));
                        cursor = at + n;
                        continue;
                    }
                    string open = (SoftBreak(text, at) ? "\n" : "") + $"<margin-left={left:0}><margin-right={right:0}>";
                    text = text.Insert(at, open);
                    int after = at + open.Length;
                    Measure(text); layouts++;
                    info = _measure.textInfo;
                    int close = CloseAt(info, page, after, boxes, mask);
                    if (close < 0) { cursor = int.MaxValue; break; }   // the band runs to the end of the text
                    close = BackOverTags(text, close);
                    string shut = (SoftBreak(text, close) ? "\n" : "") + "</margin>";
                    text = text.Insert(close, shut);
                    cursor = close + shut.Length;
                }
            }
            FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] journal {_folder}: text wrapped around pinned pictures on {byPage.Count} page(s), {layouts} layout passes");
            return text;
        }

        private void LineBand(TMP_TextInfo info, int line, out float top, out float bottom)
        {
            float yMax = _measure.rectTransform.rect.yMax;
            top = yMax - info.lineInfo[line].ascender;      // TMP's line metrics are y up; ours are down from the area's top
            bottom = yMax - info.lineInfo[line].descender;
        }

        private int Overlaps(TMP_TextInfo info, int line, List<Rect> boxes)
        {
            LineBand(info, line, out float top, out float bottom);
            int mask = 0;
            for (int b = 0; b < boxes.Count; b++)
                if (bottom > boxes[b].yMin + 1f && top < boxes[b].yMax - 1f) mask |= 1 << b;
            return mask;
        }

        private static bool Visible(TMP_TextInfo info, int line)
        {
            TMP_LineInfo li = info.lineInfo[line];
            for (int c = li.firstCharacterIndex; c <= li.lastCharacterIndex && c < info.characterCount; c++)
                if (info.characterInfo[c].isVisible) return true;
            return false;
        }

        /// <summary>The first line on the page, starting at or after cursor (a text index), with ink that meets a picture.</summary>
        private int NextBandLine(TMP_TextInfo info, int page, int cursor, List<Rect> boxes, out int mask)
        {
            mask = 0;
            for (int l = 0; l < info.lineCount; l++)
            {
                TMP_LineInfo li = info.lineInfo[l];
                if (li.characterCount == 0 || li.firstCharacterIndex >= info.characterCount) continue;
                TMP_CharacterInfo first = info.characterInfo[li.firstCharacterIndex];
                if (first.pageNumber < page) continue;
                if (first.pageNumber > page) return -1;
                if (first.index < cursor || !Visible(info, l)) continue;
                mask = Overlaps(info, l, boxes);
                if (mask != 0) return l;
            }
            return -1;
        }

        /// <summary>Where the margin run opened at text index 'after' ends: the first later line with ink that meets a different
        /// set of pictures, or is on another page. -1 = the text ends inside the run.</summary>
        private int CloseAt(TMP_TextInfo info, int page, int after, List<Rect> boxes, int mask)
        {
            bool inRun = false;
            for (int l = 0; l < info.lineCount; l++)
            {
                TMP_LineInfo li = info.lineInfo[l];
                if (li.characterCount == 0 || li.firstCharacterIndex >= info.characterCount) continue;
                TMP_CharacterInfo first = info.characterInfo[li.firstCharacterIndex];
                if (!inRun) { if (info.characterInfo[li.lastCharacterIndex].index >= after) inRun = true; continue; }   // the run's first line
                if (first.pageNumber != page) return first.index;
                if (!Visible(info, l)) continue;
                if (Overlaps(info, l, boxes) != mask) return first.index;
            }
            return -1;
        }

        /// <summary>The widest span of the line's width left free by the pictures in mask, as TMP margins (px from each side).</summary>
        private static bool FreeSpan(List<Rect> boxes, int mask, float areaW, out float marginLeft, out float marginRight)
        {
            var blocked = new List<Vector2>();
            for (int b = 0; b < boxes.Count; b++)
                if ((mask & (1 << b)) != 0) blocked.Add(new Vector2(Mathf.Max(0f, boxes[b].xMin - WrapGap), Mathf.Min(areaW, boxes[b].xMax + WrapGap)));
            blocked.Sort((a, c) => a.x.CompareTo(c.x));
            float bestA = 0f, bestB = 0f, x = 0f;
            foreach (Vector2 r in blocked)
            {
                if (r.x - x > bestB - bestA) { bestA = x; bestB = r.x; }
                x = Mathf.Max(x, r.y);
            }
            if (areaW - x > bestB - bestA) { bestA = x; bestB = areaW; }
            marginLeft = bestA;
            marginRight = areaW - bestB;
            return bestB - bestA >= MinSpan;
        }

        // A line that starts after a soft wrap (not after a newline) needs a newline of its own before a margin change, or TMP
        // would apply the change to the end of the line above.
        private static bool SoftBreak(string text, int at) => at > 0 && text[at - 1] != '\n';

        // Moves an insertion point back over the tags right before it on the same line (the header's size / color), so the
        // newline test sees the real line start.
        private static int BackOverTags(string text, int at)
        {
            at = Mathf.Clamp(at, 0, text.Length);
            while (at > 0 && text[at - 1] == '>')
            {
                int lt = text.LastIndexOf('<', at - 1);
                if (lt < 0 || text.IndexOf('\n', lt, at - lt) >= 0) break;
                at = lt;
            }
            return at;
        }

        // ---- pages and pictures ----

        /// <summary>The clean book shows two pages side by side; edit mode shows one page and the editor.</summary>
        private void ShowPage()
        {
            int left = _page, right = _editMode ? -1 : _page + 1;
            _entries.pageToDisplay = left + 1;
            _entries.enabled = left < _textPages;
            _entriesRight.gameObject.SetActive(!_editMode);
            if (right >= 0)
            {
                _entriesRight.pageToDisplay = right + 1;
                _entriesRight.enabled = right < _textPages;
            }
            _shownToPic.Clear();
            DrawFrames(_entries.rectTransform, left);
            DrawFrames(_entriesRight.rectTransform, right);
            _num.text = right >= 0 && right < _pageCount ? $"{left + 1}-{right + 1} / {_pageCount}" : $"{left + 1} / {_pageCount}";
        }

        /// <summary>The pictures drawn on one page. Only in edit mode, and only for entries the reader may edit, do they get the
        /// corner buttons (on double-click); a change pins the picture where it now is (page + x / y).</summary>
        private void DrawFrames(RectTransform area, int page)
        {
            for (int i = area.childCount - 1; i >= 0; i--)
                if (area.GetChild(i).GetComponent<FiresPictures.PictureFrame>() != null) Destroy(area.GetChild(i).gameObject);
            if (page < 0 || string.IsNullOrEmpty(_folder)) return;
            for (int j = 0; j < _pics.Count && j < _placed.Count; j++)
            {
                if (_placed[j].page != page) continue;
                FiresPictures.ImgSpec stored = _pics[j].Spec;
                var d = new FiresPictures.ImgSpec
                {
                    Src = stored.Src, X = stored.X, W = stored.W, Rot = stored.Rot, Crop = stored.Crop, Caption = stored.Caption,
                    Y = _placed[j].y, Page = page, WrapAround = true,
                };
                _shownToPic[d] = j;
                FiresPictures.PictureFrame.Create(area, _folder, d, _editMode && CanEditPic(j), _entries.font, OnPictureChanged, OnPictureDeleted, true);
            }
        }

        private readonly Dictionary<FiresPictures.ImgSpec, int> _shownToPic = new Dictionary<FiresPictures.ImgSpec, int>();

        private void OnPictureChanged(FiresPictures.ImgSpec s)
        {
            if (!_shownToPic.TryGetValue(s, out int j) || !CanEditPic(j)) { ShowPage(); return; }
            Pic p = _pics[j];
            var spec = new FiresPictures.ImgSpec
            {
                Src = p.Spec.Src, X = Mathf.Clamp(s.X, 0f, 1f - s.W), Y = Mathf.Clamp(s.Y, 0f, 0.95f), W = s.W, Rot = s.Rot, Crop = s.Crop,
                Caption = p.Spec.Caption, WrapAround = true, Page = Mathf.Max(0, s.Page),
            };
            if (_editMode && spec.Page != _page) _page = spec.Page;   // follow a picture sent to another page
            RewritePic(j, FiresPictures.PageLayout.Write(spec));
            FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] journal picture {spec.Src} in entry #{p.Entry + 1}: page {spec.Page + 1}, x {spec.X:0.00}, y {spec.Y:0.00}, w {spec.W:0.00}");
        }

        private void OnPictureDeleted(FiresPictures.ImgSpec s)
        {
            if (!_shownToPic.TryGetValue(s, out int j) || !CanEditPic(j)) { ShowPage(); return; }
            RewritePic(j, "");
        }

        private bool CanEditPic(int j) =>
            _view != null && j < _pics.Count && _pics[j].Entry < _view.Entries.Count && _view.Entries[_pics[j].Entry].CanEdit;

        /// <summary>Replaces the Nth picture tag of its entry and saves the entry (an entry left empty is deleted).</summary>
        private void RewritePic(int j, string replacement)
        {
            Pic p = _pics[j];
            string text = _view.Entries[p.Entry].Text ?? "";
            int seen = 0;
            string updated = ImgTag.Replace(text, m => seen++ == p.Nth ? replacement : m.Value);
            if (string.IsNullOrWhiteSpace(updated)) updated = "";   // nothing left: the entry is deleted
            JournalStore.Edit(_folder, p.Entry, updated);
            _status.text = "saved...";
        }

        // ================= edit mode =================

        private void ToggleEdit()
        {
            _editMode = !_editMode;
            if (!_editMode) EndEdit();
            ApplyMode();
            ShowPage();
            FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] journal {_folder}: edit mode {(_editMode ? "on" : "off")}");
        }

        /// <summary>Clean book (default) or edit mode: the editor page and the authoring buttons show only while editing.</summary>
        private void ApplyMode()
        {
            _editor.SetActive(_editMode);
            _entriesRight.gameObject.SetActive(!_editMode);
            _pictureButton.gameObject.SetActive(_editMode);
            _giveTo.gameObject.SetActive(_editMode);
            _giveButton.gameObject.SetActive(_editMode);
            _editPillLabel.text = _editMode ? "Done" : "Edit";
        }

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
            _status.text = "sent...";
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
                FiresPictures.Store.Upload(_folder, tex, pic => JournalStore.Add(_folder, FiresPictures.PageLayout.Write(new FiresPictures.ImgSpec { Src = pic, X = 0.1f, Y = 0f, W = 0.8f, WrapAround = false })),
                    err => _status.text = "picture: " + err);
            }, () => Open(_folder));
        }

        private void Turn(int d)
        {
            int step = _editMode ? 1 : 2;
            _page = Mathf.Clamp(_page + d * step, 0, _pageCount - 1);
            ShowPage();
        }

        private void Update()
        {
            if (!_open) return;
            // 0.0.16: no per-frame cursor writes here any more; Core's InputBlock keeps the cursor free (one keeper, no strobe)
            if (Player.m_localPlayer == null) { Hide(); return; }
            if (_relayoutQueued) Relayout();
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

        // ================= build =================

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
            // 0.0.16: the Edit pill (rounded, kit radius) right of the title; it toggles the editor page and the picture buttons
            _editPill = BookReader.ButtonAt(_root.transform, "EditPill", new Vector2(-572f, 490f), new Vector2(96f, 38f), "Edit");
            try
            {
                var pill = (Image)_editPill.targetGraphic;
                pill.sprite = FiresCore.UI.Shell.Kit.KitSprites.RoundedFill(FiresCore.UI.Shell.Kit.FiresTokens.Radius);
                pill.type = Image.Type.Sliced;
            }
            catch { }
            _editPillLabel = _editPill.GetComponentInChildren<TextMeshProUGUI>();
            _editPill.onClick.AddListener(ToggleEdit);
            _editPill.gameObject.SetActive(false);
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
            // the measuring copy: the same text box, never seen; every wrap / page decision is laid out on it
            _measure = EntriesText(left.transform, "Measure");
            _measure.color = new Color(0f, 0f, 0f, 0f);
            _entries = EntriesText(left.transform, "Entries");
            _entriesRight = EntriesText(right.transform, "EntriesRight");
            _num = BookReader.Label(left.transform, "Num", new Vector2(0f, -385f), new Vector2(200f, 30f), 20f, new Color(0.48f, 0.38f, 0.25f), TextAlignmentOptions.Center);
            BookReader.ButtonAt(left.transform, "Prev", new Vector2(-280f, -385f), new Vector2(60f, 40f), "◀").onClick.AddListener(() => Turn(-1));
            BookReader.ButtonAt(left.transform, "Next", new Vector2(280f, -385f), new Vector2(60f, 40f), "▶").onClick.AddListener(() => Turn(1));

            Color ink = new Color(0.48f, 0.38f, 0.25f);
            _editor = new GameObject("Editor", typeof(RectTransform));
            _editor.transform.SetParent(right.transform, false);
            BookReader.Stretch((RectTransform)_editor.transform);
            Transform ed = _editor.transform;
            _editLabel = BookReader.Label(ed, "EditLabel", new Vector2(0f, 380f), new Vector2(620f, 30f), 22f, ink, TextAlignmentOptions.MidlineLeft);
            _editLabel.text = "New entry";
            _input = EditorInput(ed, "Input", new Vector2(0f, 150f), new Vector2(620f, 420f), 22f, true);
            _addButton = BookReader.ButtonAt(ed, "Add", new Vector2(-180f, -95f), new Vector2(200f, 44f), "Add entry");
            _addButton.onClick.AddListener(Submit);
            _deleteButton = BookReader.ButtonAt(ed, "Delete", new Vector2(40f, -95f), new Vector2(180f, 44f), "Delete entry");
            _deleteButton.onClick.AddListener(Delete);
            _deleteButton.gameObject.SetActive(false);
            BookReader.ButtonAt(ed, "Cancel", new Vector2(240f, -95f), new Vector2(130f, 44f), "Cancel").onClick.AddListener(EndEdit);
            BookReader.Label(ed, "EditHint", new Vector2(0f, -150f), new Vector2(620f, 30f), 18f, ink, TextAlignmentOptions.MidlineLeft).text = "Entries you may edit:";
            var list = new GameObject("EditList", typeof(RectTransform));
            list.transform.SetParent(ed, false);
            ((RectTransform)list.transform).anchoredPosition = new Vector2(0f, -200f);
            _editList = list.transform;
            _status = BookReader.Label(right.transform, "Status", new Vector2(0f, -385f), new Vector2(620f, 30f), 20f, ink, TextAlignmentOptions.Center);

            try { FiresCore.Bridge.ModUiRegistry.Register(RegistryKey, () => IsOpen, Close, () => _root); } catch { }
            _root.SetActive(false);
        }

        private static TextMeshProUGUI EntriesText(Transform page, string name)
        {
            TextMeshProUGUI t = BookReader.Label(page, name, new Vector2(0f, 20f), new Vector2(600f, 720f), 22f, BookReader.Ink, TextAlignmentOptions.TopLeft);
            t.overflowMode = TextOverflowModes.Page;
            Emoji.Apply(t);
            t.enableWordWrapping = true;
            t.richText = true;
            return t;
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
