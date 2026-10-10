using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace FiresLoreBooks
{
    /// <summary>
    /// FiresLoreBooks: placed, pickable lore books (server text files, admin editor, rewards once per player) and the carried
    /// journal / scrapbook with pictures. Design: Tools\FIRESLOREBOOKS_DESIGN.md. Pictures (camera + server store) live in
    /// Pictures\*.cs ([fgn]), contract Tools\FIRESPICTURES_API.md.
    /// </summary>
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency("com.Fire.FiresUnifiedCore", BepInDependency.DependencyFlags.HardDependency)]
    public class FiresLoreBooksPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.Fire.FiresLoreBooks";
        public const string PluginName = "FiresLoreBooks";
        public const string PluginVersion = "0.0.9";

        internal static ManualLogSource Log;
        internal static BepInEx.Configuration.ConfigEntry<string> DefaultRespawn;
        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            DefaultRespawn = Config.Bind("Books", "Respawn after taking", "4d",
                "Server: how long until a player can take another copy of the same placed book (4d = 4 game days, 12h, 30m, never). A book's own '# respawn:' line wins.");
            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll(typeof(FiresLoreBooksPlugin).Assembly);
            SeedExample();
            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void Update() => Books.BookStore.Tick();

        /// <summary>An empty Books folder gets one example book, so `lorebook_open example_lore` works out of the box.</summary>
        private static void SeedExample()
        {
            try
            {
                string dir = Books.BookStore.BooksDir;
                System.IO.Directory.CreateDirectory(dir);
                if (System.IO.Directory.GetFiles(dir, "*.txt").Length > 0) return;
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "example_lore.txt"),
                    "# title: The Fall of the Elder\n# cover: #4a2a17\n---\n"
                    + "Before the ashlands burned, the Elder stood as the <b>oldest of the swamp trees</b>, and the <i>greydwarves</i> sang to its roots.\n\n"
                    + "When the <color=#7a1f1f>grey fog</color> came up from the black marsh, the singing stopped.\n===\n"
                    + "They say a hunter found the old song carved on a stone near the burial mounds:\n\n<align=center><i>\"Wake the root, and the root wakes you.\"</i></align>\n");
            }
            catch (System.Exception e) { Log.LogWarning($"[LoreBooks] seeding the example book failed: {e.Message}"); }
        }

        private void OnDestroy() => _harmony?.UnpatchSelf();
    }
}
