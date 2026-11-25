using System.Collections;
using System.Linq;
using FiresLoreBooks.Books;
using UnityEngine;

namespace FiresLoreBooks
{
    /// <summary>
    /// lorebook_selftest (0.0.10, Fire 10-12 #4): the three checks the lead runs on the test pad, as one command.
    ///   hammer     the lore book pieces are on the Hammer's table (LorePieces.Check, the same line the spawn logs)
    ///   new_book   an admin writes a NEW book and saves it to the server; the library's list then holds it
    ///   rotate     (0.0.11) its 90° picture comes back from the server still rotated, and the journal reserves its rotated height
    ///   placed     a lore book piece placed 2 m ahead, pointed at that book through the library's picker path, shows its title
    ///   cleanup    the placed piece destroyed by its ZDO, the book deleted on the server
    /// SHORTCUTS (logged): the book is saved through BookStore.Save (the editor's own call) instead of typing in the editor; the
    /// piece is spawned in front of the player instead of placed with the Hammer; the picker's SetBookId is called directly.
    /// Lines (0.0.13): FDT's F5 contract (Tools\FIRES_F5_TESTS.md), so fires_test_all launches and judges it:
    /// "[LoreBooksTest] BEGIN FiresLoreBooks &lt;ver&gt; (&lt;hash&gt;) on client: 5 steps, ~40 s", "[LoreBooksTest] n/5 &lt;step&gt; PASS|FAIL - detail",
    /// "[LoreBooksTest] END PASS|FAIL: p pass, f fail, k skip in t s".
    /// </summary>
    internal static class SelfTest
    {
        private const string Title = "Selftest lore book";
        internal const string Tag = "LoreBooksTest";
        private const int Steps = 5;

        private static string DllHash()
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var fs = System.IO.File.OpenRead(typeof(SelfTest).Assembly.Location))
                    return System.BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").Substring(0, 12);
            }
            catch { return "unknown"; }
        }

        internal static IEnumerator Run(Terminal console)
        {
            bool allOk = true;
            int n = 0, pass = 0, fail = 0, skip = 0;
            float started = Time.realtimeSinceStartup;
            void Emit(string line)
            {
                FiresLoreBooksPlugin.Log.LogMessage(line);
                console?.AddString(line);
            }
            void Report(string step, bool ok, string detail)
            {
                allOk &= ok;
                n++;
                if (ok) pass++; else fail++;
                Emit($"[{Tag}] {n}/{Steps} {step} {(ok ? "PASS" : "FAIL")} - {detail}");
            }
            void End()
            {
                skip = Mathf.Max(0, Steps - pass - fail);
                Emit($"[{Tag}] END {(allOk && fail == 0 ? "PASS" : "FAIL")}: {pass} pass, {fail} fail, {skip} skip in {Time.realtimeSinceStartup - started:0.0} s");
            }

            Emit($"[{Tag}] BEGIN {FiresLoreBooksPlugin.PluginName} {FiresLoreBooksPlugin.PluginVersion} ({DllHash()}) on client: {Steps} steps, ~40 s");
            Player me = Player.m_localPlayer;
            if (me == null) { Report("hammer", false, "setup: no local player"); End(); yield break; }
            if (!UI.BookEditor.IsLocalAdmin()) { Report("hammer", false, "setup: not admin (the library and saves are admin only)"); End(); yield break; }

            // 1. hammer
            int onHammer = LorePieces.Check();
            Report("hammer", onHammer >= 19, $"{onHammer} lore book piece(s) on the Hammer (want 19 books + the journal lectern)");

            // 2. new_book: a fresh id, saved, then listed
            string id = "selftest_" + BookStore.NewId();
            var doc = new BookDoc { Id = id, Title = Title };
            doc.Pages.Add("Written by lorebook_selftest. This book is deleted at the end of the test.\n"
                + "<img src=\"selftest_rot.jpg\" x=0.1 y=0.3 w=0.5 rot=90 wrap=past>");
            string saveErr = null;
            BookStore.Save(doc, err => saveErr = err ?? "");
            float until = Time.time + 6f;
            while (saveErr == null && Time.time < until) yield return null;
            bool listed = false;
            if (saveErr == "")
            {
                System.Collections.Generic.List<BookStore.Entry> books = null;
                BookStore.List(list => books = list);
                until = Time.time + 6f;
                while (books == null && Time.time < until) yield return null;
                listed = books != null && books.Any(b => b.Id == id && b.Title == Title);
            }
            Report("new_book", saveErr == "" && listed, $"SHORTCUT BookStore.Save instead of typing in the editor; save '{id}': "
                + (saveErr == null ? "no answer in 6 s" : saveErr == "" ? "written" : saveErr) + $"; in the library list: {listed}");

            // 2b. rotate (Fire 10-12): the 90° picture comes back from the server still rotated, and the journal's flow reserves
            // its ROTATED height (at 90° the frame's width): a rotated picture never runs into the next text.
            if (saveErr == "")
            {
                yield return new WaitForSeconds(1f);   // the save's revision broadcast clears the cached copy
                BookDoc back = null;
                BookStore.Get(id, d => back = d);
                until = Time.time + 6f;
                while (back == null && Time.time < until) yield return null;
                var imgs = back != null && back.Pages.Count > 0 ? FiresPictures.PageLayout.Parse(back.Pages[0]).imgs : new System.Collections.Generic.List<FiresPictures.ImgSpec>();
                float rot = imgs.Count > 0 ? imgs[0].Rot : float.NaN;
                const float areaW = 600f;
                float flat = imgs.Count > 0 ? FiresPictures.PictureFrame.HeightFraction(imgs[0]) * areaW + FiresPictures.PictureFrame.CaptionSpace(imgs[0]) : 0f;
                float reserved = imgs.Count > 0 ? UI.JournalView.RotatedHeight(imgs[0], areaW) : 0f;
                FiresPictures.ImgSpec flatSpec = imgs.Count > 0 ? imgs[0].Clone() : null;
                if (flatSpec != null) flatSpec.Rot = 0f;
                float want = flatSpec != null ? FiresPictures.PictureFrame.RotatedBox(flatSpec, areaW, 1f).width : 0f;   // at 90° the reserved height is the unrotated frame's width
                bool ok = imgs.Count == 1 && Mathf.Abs(rot - 90f) < 0.5f && Mathf.Abs(reserved - want) < 1f && reserved > flat;
                Report("rotate", ok, $"reloaded from the server: {imgs.Count} picture(s), rot {rot:0.#}° (want 90); journal space at {areaW:0} px width: "
                    + $"{reserved:0} px reserved for the rotated frame (its width {want:0} px; unrotated it would be {flat:0} px)");
            }
            else Report("rotate", false, "skipped: the book wasn't saved");

            // 3. placed: spawn a lore book piece ahead, point it at the book, read its hover title
            GameObject placed = null;
            string hover = "";
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("FiresBook_book1") : null;
            if (prefab != null && saveErr == "")
            {
                Vector3 at = me.transform.position + me.transform.forward * 2f + Vector3.up * 0.5f;
                placed = Object.Instantiate(prefab, at, Quaternion.identity);
                yield return null;
                LoreBookPiece piece = placed.GetComponent<LoreBookPiece>();
                bool pointed = piece != null && piece.SetBookId(id);
                until = Time.time + 6f;
                while (Time.time < until)
                {
                    hover = piece != null ? piece.GetHoverText() : "";
                    if (hover.Contains(Title)) break;
                    yield return new WaitForSeconds(0.25f);
                }
                Report("placed", pointed && hover.Contains(Title), $"SHORTCUT spawned FiresBook_book1 2 m ahead + SetBookId (the picker's call); pointed {pointed}; hover '{hover.Replace("\n", " | ")}'");
            }
            else Report("placed", false, prefab == null ? "no FiresBook_book1 prefab in ZNetScene (the bundle didn't load)" : "skipped: the book wasn't saved");

            // 4. cleanup: by ZDO, never by radius
            string cleanup = "";
            if (placed != null)
            {
                ZNetView nv = placed.GetComponent<ZNetView>();
                if (nv != null && nv.GetZDO() != null) { if (!nv.IsOwner()) nv.ClaimOwnership(); ZNetScene.instance.Destroy(placed); cleanup += "piece destroyed by ZDO; "; }
                else { Object.Destroy(placed); cleanup += "piece destroyed (no ZDO); "; }
            }
            if (saveErr == "")
            {
                string delErr = null;
                BookStore.Delete(id, err => delErr = err ?? "");
                until = Time.time + 6f;
                while (delErr == null && Time.time < until) yield return null;
                cleanup += "book " + (delErr == "" ? "deleted" : "NOT deleted: " + (delErr ?? "no answer"));
                Report("cleanup", delErr == "", cleanup);
            }
            else Report("cleanup", true, cleanup.Length > 0 ? cleanup : "nothing to clean");

            End();
        }
    }
}
