using System.IO;
using FiresLoreBooks.Books;
using HarmonyLib;

namespace FiresLoreBooks
{
    /// <summary>Console commands for tests until the placed pieces exist (milestone 3).</summary>
    [HarmonyPatch(typeof(Terminal), "InitTerminal")]
    internal static class Commands
    {
        private static void Postfix()
        {
            new Terminal.ConsoleCommand("lorebook_open", "[id] - open a lore book in the reader (FiresLoreBooks)", args =>
            {
                if (args.Length < 2) { args.Context.AddString("usage: lorebook_open <id>"); return; }
                UI.BookReader.Open(args[1]);
                args.Context.AddString($"[LoreBooks] opening '{args[1]}'");
            });
            new Terminal.ConsoleCommand("lorebook_close", "- close the lore book reader / editor", args => { UI.BookReader.Close(); UI.BookEditor.Close(); });
            new Terminal.ConsoleCommand("lorebook_collection", "- your lore collection (books taken / all books)", args => UI.BookReader.OpenCollection());
            new Terminal.ConsoleCommand("lorebook_edit", "[id] - edit (or create) a lore book (admin)", args =>
            {
                if (args.Length < 2) { args.Context.AddString("usage: lorebook_edit <id>"); return; }
                UI.BookEditor.Open(args[1]);
            });
            new Terminal.ConsoleCommand("lorebook_list", "- list the server's lore books (server / host only)", args =>
            {
                if (!Directory.Exists(BookStore.BooksDir)) { args.Context.AddString("[LoreBooks] no books yet: " + BookStore.BooksDir); return; }
                foreach (string f in Directory.GetFiles(BookStore.BooksDir, "*.txt")) args.Context.AddString("  " + Path.GetFileNameWithoutExtension(f));
            });
        }
    }
}
