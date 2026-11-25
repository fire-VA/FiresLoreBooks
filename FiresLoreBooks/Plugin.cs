using BepInEx;
using BepInEx.Logging;
using FiresCore.Sync;
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
        public const string PluginVersion = "0.0.19";
        // The oldest version whose synced setting (Respawn after taking) a client must match; raised only when it changes.
        public const string SyncFloor = "0.0.14";

        internal static ManualLogSource Log;
        internal static FiresLoreBooksPlugin Instance;
        internal static BepInEx.Configuration.ConfigEntry<string> DefaultRespawn;
        private ConfigSync _sync;
        private Harmony _harmony;

        // 0.0.19 quit guard (Core's pattern): at application quit OnDestroy skips UnpatchSelf. Unpatching then makes Harmony rebuild
        // every shared method while the game exits ([ghost] 11-08, Fire's 28 s quit freeze). A mid-session unload still unpatches.
        private static bool s_quitting;

        private void Awake()
        {
            UnityEngine.Application.quitting += () => s_quitting = true;
            Log = Logger;
            Instance = this;
            // 0.0.14 (config audit 10-13): the respawn time is a world rule, so the server's value is synced to every client and only an
            // admin can change it. The Pictures keys are bound here too, so they show in the config from the start, not on first camera use.
            _sync = new ConfigSync(PluginGUID) { DisplayName = PluginName, CurrentVersion = PluginVersion, MinimumRequiredVersion = SyncFloor, IsLocked = true };
            DefaultRespawn = Config.Bind("Books", "Respawn after taking", "4d",
                "How long until a player can take another copy of the same placed book. Format: a number and a unit - d = game days, "
                + "h = game hours, m = game minutes (e.g. 4d, 12h, 30m) - or never. A plain number means game days. A book's own '# respawn:' "
                + "line wins over this. [Synced with Server; admin only]");
            _sync.AddConfigEntry(DefaultRespawn);
            DefaultRespawn.SettingChanged += (_, __) => CheckRespawn();
            CheckRespawn();
            FiresPictures.Camera.BindConfig(Config);
            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll(typeof(FiresLoreBooksPlugin).Assembly);
            SeedExample();
            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void Update() => Books.BookStore.Tick();

        // A value the parser can't read falls back to 4 game days; say so instead of doing it silently.
        private static void CheckRespawn()
        {
            string v = (DefaultRespawn.Value ?? "").Trim().ToLowerInvariant();
            if (v != "never" && !System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d+(\.\d+)?[dhm]?$"))
                Log.LogWarning($"Respawn after taking = '{DefaultRespawn.Value}' isn't a duration (4d, 12h, 30m or never): 4 game days are used");
        }

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

        private void OnDestroy() { if (!s_quitting) _harmony?.UnpatchSelf(); }
    }
}
