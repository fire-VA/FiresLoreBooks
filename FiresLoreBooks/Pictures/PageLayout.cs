using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;

namespace FiresPictures
{
    /// <summary>One picture placed on a page (LoreBooks 0.0.4; the format agreed with [classes-ui]).
    /// &lt;img src="p_3f2a.jpg" x=0.10 y=0.20 w=0.50 rot=-4 crop=0,0,1,1 wrap=around cap="our longhouse"&gt;
    /// x / y: the frame's top-left as a fraction of the page's text area; w: its width as a fraction (the height follows the
    /// cropped picture's aspect); rot in degrees; crop u0,v0,u1,v1 in 0..1; wrap "around" (the text runs beside it) or "past"
    /// (above and below). Anchor: the character index, in the text Parse returns, where the tag stood.</summary>
    public sealed class ImgSpec
    {
        public string Src = "";
        public float X = 0.25f, Y = 0.15f, W = 0.5f, Rot;
        public Rect Crop = new Rect(0f, 0f, 1f, 1f);
        public bool WrapAround = true;
        public string Caption = "";
        public int Anchor;

        public ImgSpec Clone() => (ImgSpec)MemberwiseClone();
    }

    /// <summary>
    /// Inline pictures on a book or journal page. The reader / editor ([classes-ui]) calls Parse on a page's text, renders the
    /// returned text, then Show for each laid-out page; when the editor's handles change a picture, Write gives the tag back.
    /// </summary>
    public static class PageLayout
    {
        // Each &lt;img&gt; is replaced by one zero-width space, so the anchor survives TMP's layout and its character index.
        internal const char AnchorChar = '​';

        private static readonly Regex ImgRx = new Regex(@"<img\b([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AttrRx = new Regex(@"(\w+)\s*=\s*(""[^""]*""|[^\s>]+)", RegexOptions.Compiled);

        /// <summary>Splits a page's text into the text to render (each tag replaced by a zero-width anchor) and its pictures.
        /// The 0.0.3 form &lt;img id="Journals/c/j/p_x.jpg"&gt; still works: its file name is taken after the last '/'.</summary>
        public static (string text, List<ImgSpec> imgs) Parse(string pageText)
        {
            var imgs = new List<ImgSpec>();
            if (string.IsNullOrEmpty(pageText)) return (pageText ?? "", imgs);
            var sb = new StringBuilder(pageText.Length);
            int last = 0;
            foreach (Match m in ImgRx.Matches(pageText))
            {
                sb.Append(pageText, last, m.Index - last);
                ImgSpec spec = FromAttributes(m.Groups[1].Value);
                spec.Anchor = sb.Length;
                if (spec.Src.Length > 0) { imgs.Add(spec); sb.Append(AnchorChar); }
                last = m.Index + m.Length;
            }
            sb.Append(pageText, last, pageText.Length - last);
            return (sb.ToString(), imgs);
        }

        /// <summary>The tag for a picture, as it goes back into the page's text.</summary>
        public static string Write(ImgSpec s)
        {
            string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            var sb = new StringBuilder("<img src=\"").Append(s.Src).Append('"');
            sb.Append(" x=").Append(F(s.X)).Append(" y=").Append(F(s.Y)).Append(" w=").Append(F(s.W));
            if (Mathf.Abs(s.Rot) > 0.01f) sb.Append(" rot=").Append(F(s.Rot));
            if (s.Crop != new Rect(0f, 0f, 1f, 1f))
                sb.Append(" crop=").Append(F(s.Crop.xMin)).Append(',').Append(F(s.Crop.yMin)).Append(',').Append(F(s.Crop.xMax)).Append(',').Append(F(s.Crop.yMax));
            sb.Append(" wrap=").Append(s.WrapAround ? "around" : "past");
            if (!string.IsNullOrEmpty(s.Caption)) sb.Append(" cap=\"").Append(s.Caption.Replace("\"", "'")).Append('"');
            return sb.Append('>').ToString();
        }

        private static ImgSpec FromAttributes(string attrs)
        {
            var s = new ImgSpec();
            bool sized = false;
            foreach (Match a in AttrRx.Matches(attrs))
            {
                string key = a.Groups[1].Value.ToLowerInvariant(), val = a.Groups[2].Value.Trim('"');
                switch (key)
                {
                    case "src": s.Src = val; break;
                    case "id": if (s.Src.Length == 0) s.Src = val.Substring(val.LastIndexOf('/') + 1); break;   // 0.0.3 entries
                    case "x": s.X = Num(val, s.X); break;
                    case "y": s.Y = Num(val, s.Y); break;
                    case "w": s.W = Mathf.Clamp(Num(val, s.W), 0.05f, 1f); sized = true; break;
                    case "rot": s.Rot = Num(val, 0f); break;
                    case "wrap": s.WrapAround = !val.Equals("past", StringComparison.OrdinalIgnoreCase); break;
                    case "cap": s.Caption = val; break;
                    case "crop":
                        string[] p = val.Split(',');
                        if (p.Length == 4)
                        {
                            float u0 = Mathf.Clamp01(Num(p[0], 0f)), v0 = Mathf.Clamp01(Num(p[1], 0f));
                            float u1 = Mathf.Clamp01(Num(p[2], 1f)), v1 = Mathf.Clamp01(Num(p[3], 1f));
                            if (u1 - u0 > 0.02f && v1 - v0 > 0.02f) s.Crop = Rect.MinMaxRect(u0, v0, u1, v1);
                        }
                        break;
                }
            }
            if (!sized && !attrs.Contains(" x=")) { s.X = 0f; s.W = 1f; s.WrapAround = false; }   // a bare 0.0.3 entry: full width
            return s;
        }

        private static float Num(string v, float fallback) =>
            float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;

        /// <summary>
        /// Draws the pictures anchored on the page TMP is showing (pageText.pageToDisplay), inside pageArea (the text's rect), with
        /// the editing handles when editable. "around" pictures push the lines beside them aside with TMP &lt;margin&gt; tags, a
        /// measured second layout pass. changed(spec) runs when a handle edit ends; deleted(spec) when its ✕ is pressed.
        /// Call again whenever the page or its text changes; the previous frames are removed.
        /// </summary>
        public static void Show(RectTransform pageArea, TextMeshProUGUI pageText, string folder, List<ImgSpec> imgs, bool editable,
            Action<ImgSpec> changed, Action<ImgSpec> deleted = null)
        {
            if (pageArea == null || pageText == null) return;
            for (int i = pageArea.childCount - 1; i >= 0; i--)
                if (pageArea.GetChild(i).GetComponent<PictureFrame>() != null) UnityEngine.Object.Destroy(pageArea.GetChild(i).gameObject);
            if (imgs == null || imgs.Count == 0) return;

            pageText.ForceMeshUpdate();
            var onPage = new List<ImgSpec>();
            foreach (ImgSpec s in imgs) if (OnShownPage(pageText, s.Anchor)) onPage.Add(s);
            if (onPage.Count == 0) return;

            Wrap(pageText, pageArea, onPage);
            foreach (ImgSpec s in onPage) PictureFrame.Create(pageArea, folder, s, editable, pageText.font, changed, deleted);
        }

        private static bool OnShownPage(TextMeshProUGUI t, int anchor)
        {
            TMP_TextInfo info = t.textInfo;
            int page = Mathf.Clamp(t.pageToDisplay - 1, 0, Math.Max(0, info.pageCount - 1));
            if (info.pageCount == 0) return true;
            TMP_PageInfo p = info.pageInfo[page];
            for (int c = p.firstCharacterIndex; c <= p.lastCharacterIndex && c < info.characterCount; c++)
                if (info.characterInfo[c].index == anchor) return true;
            return false;
        }

        // The lines whose height overlaps an "around" picture get a margin on the picture's side: tags inserted around exactly
        // those lines (from the end, so earlier indices stay valid), then one more layout. Long text may flow onto the next page.
        private static void Wrap(TextMeshProUGUI t, RectTransform area, List<ImgSpec> imgs)
        {
            string text = t.text;
            TMP_TextInfo info = t.textInfo;
            float areaW = area.rect.width, areaH = area.rect.height;
            var cuts = new List<(int at, string tag)>();
            foreach (ImgSpec s in imgs)
            {
                if (!s.WrapAround) continue;
                float top = s.Y * areaH, bottom = top + PictureFrame.HeightFraction(s) * areaW + PictureFrame.CaptionSpace(s);
                bool leftSide = s.X + s.W * 0.5f < 0.5f;
                float marginPx = leftSide ? (s.X + s.W) * areaW + 8f : (1f - s.X) * areaW + 8f;
                int first = -1, lastChar = -1;
                for (int l = 0; l < info.lineCount; l++)
                {
                    TMP_LineInfo line = info.lineInfo[l];
                    if (line.characterCount == 0) continue;
                    // TMP's line metrics are in the text's local space (y up); the picture's are down from the area's top
                    RectTransform tr = t.rectTransform;
                    float lineTop = tr.rect.yMax - line.ascender, lineBottom = tr.rect.yMax - line.descender;
                    if (lineBottom < top || lineTop > bottom) continue;
                    if (first < 0) first = info.characterInfo[line.firstCharacterIndex].index;
                    lastChar = info.characterInfo[line.lastCharacterIndex].index;
                }
                if (first < 0) continue;
                string open = leftSide ? $"<margin-left={marginPx:0}>" : $"<margin-right={marginPx:0}>";
                cuts.Add((first, open));
                cuts.Add((Math.Min(text.Length, lastChar + 1), "</margin>"));
            }
            if (cuts.Count == 0) return;
            cuts.Sort((a, b) => b.at.CompareTo(a.at));
            var sb = new StringBuilder(text);
            foreach (var cut in cuts) sb.Insert(Mathf.Clamp(cut.at, 0, sb.Length), cut.tag);
            t.text = sb.ToString();
            t.ForceMeshUpdate();
        }
    }
}
