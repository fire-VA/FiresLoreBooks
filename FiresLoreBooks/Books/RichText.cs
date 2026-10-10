using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace FiresLoreBooks.Books
{
    /// <summary>
    /// The rich-text sanitiser (design §4), run by the server on every save and by the client before display.
    /// Allowed: b i u s color size(80-150%) align mark indent, the emoji sheet's sprite tag, and our own img tag.
    /// Everything else in angle brackets is dropped; unbalanced allowed tags are closed at the end of the page.
    /// </summary>
    public static class RichText
    {
        public const int MaxPageChars = 4096;
        private static readonly Regex Tag = new Regex(@"<(/?)([a-zA-Z]+)(=[^>]*)?(\s[^>]*)?>", RegexOptions.Compiled);
        private static readonly HashSet<string> Paired = new HashSet<string> { "b", "i", "u", "s", "color", "size", "align", "mark", "indent" };

        public static string Sanitize(string page)
        {
            if (string.IsNullOrEmpty(page)) return "";
            if (page.Length > MaxPageChars) page = page.Substring(0, MaxPageChars);
            var open = new List<string>();
            string result = Tag.Replace(page, m =>
            {
                bool closing = m.Groups[1].Value == "/";
                string name = m.Groups[2].Value.ToLowerInvariant();
                string arg = m.Groups[3].Value;
                if (name == "img") return closing ? "" : m.Value;                    // our picture placeholder, laid out by the reader
                if (name == "sprite") return closing ? "" : (arg.Length > 0 || m.Groups[4].Value.Contains("name") ? m.Value : "");
                if (!Paired.Contains(name)) return "";                                // link, font, material, pos, voffset, space ...
                if (closing)
                {
                    int at = open.LastIndexOf(name);
                    if (at < 0) return "";
                    open.RemoveAt(at);
                    return "</" + name + ">";
                }
                if (name == "size") arg = "=" + ClampSize(arg.TrimStart('='));
                open.Add(name);
                return "<" + name + arg + ">";
            });
            var sb = new StringBuilder(result);
            for (int k = open.Count - 1; k >= 0; k--) sb.Append("</").Append(open[k]).Append('>');
            return sb.ToString();
        }

        private static string ClampSize(string v)
        {
            v = v.Trim().Trim('"');
            float pct = 100f;
            if (v.EndsWith("%")) float.TryParse(v.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out pct);
            else if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float px)) pct = px / 18f * 100f;
            if (pct < 80f) pct = 80f; else if (pct > 150f) pct = 150f;
            return ((int)pct).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
        }
    }
}
