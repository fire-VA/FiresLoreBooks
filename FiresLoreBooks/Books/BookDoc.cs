using System;
using System.Collections.Generic;
using System.Text;

namespace FiresLoreBooks.Books
{
    /// <summary>
    /// One book as a plain text file (Fire 10-09: editable outside the game). Format:
    /// <code>
    /// # title: The Fall of the Elder
    /// # cover: #5a3a1e
    /// # respawn: 4d
    /// # hook: GiveClassXP 200 once
    /// ---
    /// page one text, TMP rich text, &lt;img id="Books/elder/1" ...&gt;
    /// ===
    /// page two ...
    /// </code>
    /// Lines before "---" starting with "#" are the header; "===" on its own line is a page break.
    /// </summary>
    public sealed class BookDoc
    {
        public string Id;
        public string Title = "";
        public string Cover = "#4a2a17";
        public string Respawn = "";
        public readonly List<string> Hooks = new List<string>();
        public readonly List<string> Pages = new List<string>();

        public const string HeaderEnd = "---";
        public const string PageBreak = "===";

        public static BookDoc Parse(string id, string text)
        {
            var doc = new BookDoc { Id = id };
            if (string.IsNullOrEmpty(text)) { doc.Pages.Add(""); return doc; }
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            int i = 0;
            bool hasHeader = Array.Exists(lines, l => l.Trim() == HeaderEnd);
            if (hasHeader)
            {
                for (; i < lines.Length && lines[i].Trim() != HeaderEnd; i++)
                {
                    string l = lines[i].Trim();
                    if (!l.StartsWith("#")) continue;
                    int colon = l.IndexOf(':');
                    if (colon < 0) continue;
                    string key = l.Substring(1, colon - 1).Trim().ToLowerInvariant();
                    string val = l.Substring(colon + 1).Trim();
                    switch (key)
                    {
                        case "title": doc.Title = val; break;
                        case "cover": doc.Cover = val; break;
                        case "respawn": doc.Respawn = val; break;
                        case "hook": if (val.Length > 0) doc.Hooks.Add(val); break;
                    }
                }
                i++;   // past "---"
            }
            var page = new StringBuilder();
            for (; i < lines.Length; i++)
            {
                if (lines[i].Trim() == PageBreak) { doc.Pages.Add(page.ToString().Trim('\n')); page.Clear(); continue; }
                page.Append(lines[i]).Append('\n');
            }
            doc.Pages.Add(page.ToString().Trim('\n'));
            return doc;
        }

        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append("# title: ").Append(Title).Append('\n');
            if (!string.IsNullOrEmpty(Cover)) sb.Append("# cover: ").Append(Cover).Append('\n');
            if (!string.IsNullOrEmpty(Respawn)) sb.Append("# respawn: ").Append(Respawn).Append('\n');
            foreach (string h in Hooks) sb.Append("# hook: ").Append(h).Append('\n');
            sb.Append(HeaderEnd).Append('\n');
            for (int p = 0; p < Pages.Count; p++)
            {
                if (p > 0) sb.Append(PageBreak).Append('\n');
                sb.Append(Pages[p]).Append('\n');
            }
            return sb.ToString();
        }
    }
}
