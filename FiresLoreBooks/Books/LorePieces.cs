using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FiresLoreBooks.Books
{
    /// <summary>
    /// The placed lore books (design §1-2): the 19 FiresBook_* prefabs of the licensed pack, embedded as "fireslorebooks".
    /// LoreBooks is their ONE registrar (ZNetScene + a Piece on the Hammer); FAP leaves prefabs that already carry a Piece
    /// alone. Each piece gets a ZNetView (persistent), a WearNTear (so the hammer can remove it), a collider when it has
    /// none, and LoreBookPiece (E read / hold E take / Shift+E edit). Also the carried item "FiresLoreBook".
    /// </summary>
    internal static class LorePieces
    {
        internal const string ItemName = "FiresLoreBook";
        internal const string ZdoBookId = "flb_id";
        internal const string ItemBookKey = "flb_id";

        private static AssetBundle s_bundle;
        private static readonly List<GameObject> s_pieces = new List<GameObject>();
        private static GameObject s_item;
        private static GameObject s_holder;

        internal static AssetBundle Bundle()
        {
            if (s_bundle != null) return s_bundle;
            using (var stream = typeof(LorePieces).Assembly.GetManifestResourceStream("FiresLoreBooks.fireslorebooks"))
            {
                if (stream == null) { FiresLoreBooksPlugin.Log.LogWarning("[LoreBooks] embedded bundle 'fireslorebooks' missing"); return null; }
                s_bundle = AssetBundle.LoadFromStream(stream);
            }
            return s_bundle;
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class RegisterPieces
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ZNetScene __instance)
            {
                AssetBundle bundle = Bundle();
                if (bundle == null) return;
                if (s_holder == null) { s_holder = new GameObject("FiresLoreBooks_Prefabs"); s_holder.SetActive(false); Object.DontDestroyOnLoad(s_holder); }
                if (s_pieces.Count == 0)
                {
                    foreach (GameObject src in bundle.LoadAllAssets<GameObject>().Where(g => g.name.StartsWith("FiresBook_")).OrderBy(g => g.name))
                    {
                        s_pieces.Add(MakePiece(src));
                        // the journal lectern: the open book's model, holding a player's journal (Fire 10-09)
                        if (src.name == "FiresBook_openedbook") s_pieces.Add(MakePiece(src, "FiresJournalLectern"));
                    }
                    FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] {s_pieces.Count} lore book piece(s) built from the bundle");
                }
                foreach (GameObject p in s_pieces)
                {
                    if (__instance.m_namedPrefabs.ContainsKey(p.name.GetStableHashCode())) continue;
                    __instance.m_prefabs.Add(p);
                    __instance.m_namedPrefabs[p.name.GetStableHashCode()] = p;
                }
                RegisterItem(__instance);
                if (ObjectDB.instance != null) AddToHammer.Apply(ObjectDB.instance);
            }
        }

        [HarmonyPatch(typeof(ObjectDB), "Awake")]
        private static class AddToHammer
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ObjectDB __instance) => Apply(__instance);

            internal static void Apply(ObjectDB __instance)
            {
                if (s_pieces.Count == 0) return;
                GameObject hammer = __instance.GetItemPrefab("Hammer");
                PieceTable table = hammer != null ? hammer.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_buildPieces : null;
                if (table == null) return;
                ItemDrop leather = Item("LeatherScraps");
                foreach (GameObject p in s_pieces)
                {
                    // cost + icon need ObjectDB, so they are set here rather than at ZNetScene.Awake
                    Piece piece = p.GetComponent<Piece>();
                    if (piece.m_icon == null) piece.m_icon = PieceIcon();
                    if (leather != null && (piece.m_resources == null || piece.m_resources.Length == 0))
                        piece.m_resources = new[] { new Piece.Requirement { m_resItem = leather, m_amount = 1, m_recover = true } };
                    if (!table.m_pieces.Contains(p)) table.m_pieces.Add(p);
                }
                AddJournalToDb(__instance);
                if (s_item != null && __instance.GetItemPrefab(ItemName) == null)
                {
                    __instance.m_items.Add(s_item);
                    __instance.m_itemByHash[ItemName.GetStableHashCode()] = s_item;
                }
            }
        }

        private static GameObject MakePiece(GameObject src, string asName = null)
        {
            bool lectern = asName != null;
            GameObject go = Object.Instantiate(src, s_holder.transform, false);
            go.name = asName ?? src.name;
            FixShaders(go);
            if (go.GetComponentInChildren<Collider>(true) == null)
            {
                Bounds b = new Bounds(go.transform.position, Vector3.zero);
                bool any = false;
                foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true)) { if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
                var box = go.AddComponent<BoxCollider>();
                box.center = go.transform.InverseTransformPoint(b.center);
                box.size = any ? b.size : new Vector3(0.3f, 0.1f, 0.2f);
            }
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) c.gameObject.layer = LayerMask.NameToLayer("piece");
            var nview = go.GetComponent<ZNetView>() ?? go.AddComponent<ZNetView>();
            nview.m_persistent = true;
            var piece = go.GetComponent<Piece>() ?? go.AddComponent<Piece>();
            piece.m_name = lectern ? "Journal lectern" : Pretty(src.name);
            piece.m_description = lectern ? "Place your journal here for others to read. Use a journal on it; [E] read."
                                          : "A lore book. [E] read, hold [E] take a copy.";
            piece.m_category = Piece.PieceCategory.Furniture;
            piece.m_groundPiece = false;
            var wnt = go.GetComponent<WearNTear>() ?? go.AddComponent<WearNTear>();
            wnt.m_health = 100f;
            wnt.m_materialType = WearNTear.MaterialType.Wood;
            wnt.m_noRoofWear = true;
            wnt.m_noSupportWear = true;
            if (lectern) go.AddComponent<JournalLecternPiece>();
            else go.AddComponent<LoreBookPiece>();
            return go;
        }

        internal const string JournalName = "FiresJournal";
        internal const string JournalKey = "flb_journal";   // item custom data: the journal's server folder
        private static GameObject s_journal;

        /// <summary>The craftable journal (Fire Q2: workbench). Its folder is assigned on first use.</summary>
        private static void RegisterJournal(ZNetScene scene)
        {
            if (s_journal != null) return;
            GameObject template = scene.GetPrefab("Feathers");
            if (template == null) return;
            s_journal = Object.Instantiate(template, s_holder.transform, false);
            s_journal.name = JournalName;
            var drop = s_journal.GetComponent<ItemDrop>();
            var shared = drop.m_itemData.m_shared;
            shared.m_name = "Journal";
            shared.m_description = "Your own journal: write entries and keep pictures. Use it to open.";
            shared.m_itemType = ItemDrop.ItemData.ItemType.Misc;
            shared.m_maxStackSize = 1;
            shared.m_weight = 1f;
            shared.m_teleportable = true;
            drop.m_itemData.m_dropPrefab = s_journal;
            if (!scene.m_namedPrefabs.ContainsKey(JournalName.GetStableHashCode()))
            {
                scene.m_prefabs.Add(s_journal);
                scene.m_namedPrefabs[JournalName.GetStableHashCode()] = s_journal;
            }
        }

        private static void AddJournalToDb(ObjectDB db)
        {
            if (s_journal == null) return;
            if (db.GetItemPrefab(JournalName) == null)
            {
                db.m_items.Add(s_journal);
                db.m_itemByHash[JournalName.GetStableHashCode()] = s_journal;
            }
            if (db.m_recipes.Exists(r => r != null && r.name == "Recipe_" + JournalName)) return;
            ItemDrop leather = Item("LeatherScraps"), wood = Item("Wood"), resin = Item("Resin");
            CraftingStation bench = db.GetItemPrefab("Hammer") != null ? FindStation("piece_workbench") : null;
            if (leather == null || wood == null || resin == null) return;
            var recipe = ScriptableObject.CreateInstance<Recipe>();
            recipe.name = "Recipe_" + JournalName;
            recipe.m_item = s_journal.GetComponent<ItemDrop>();
            recipe.m_amount = 1;
            recipe.m_craftingStation = bench;
            recipe.m_minStationLevel = 1;
            recipe.m_resources = new[]
            {
                new Piece.Requirement { m_resItem = leather, m_amount = 2 },
                new Piece.Requirement { m_resItem = wood, m_amount = 2 },
                new Piece.Requirement { m_resItem = resin, m_amount = 1 },
            };
            db.m_recipes.Add(recipe);
        }

        private static CraftingStation FindStation(string prefab) =>
            ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab)?.GetComponent<CraftingStation>() : null;

        private static void RegisterItem(ZNetScene scene)
        {
            RegisterJournal(scene);
            if (s_item != null) return;
            GameObject template = scene.GetPrefab("Feathers");
            if (template == null) return;
            s_item = Object.Instantiate(template, s_holder.transform, false);
            s_item.name = ItemName;
            var drop = s_item.GetComponent<ItemDrop>();
            var shared = drop.m_itemData.m_shared;
            shared.m_name = "Lore book";
            shared.m_description = "A copy of a lore book. Use it to read.";
            shared.m_itemType = ItemDrop.ItemData.ItemType.Misc;
            shared.m_maxStackSize = 1;
            shared.m_weight = 0.5f;
            shared.m_teleportable = true;
            shared.m_value = 0;
            drop.m_itemData.m_dropPrefab = s_item;
            if (!scene.m_namedPrefabs.ContainsKey(ItemName.GetStableHashCode()))
            {
                scene.m_prefabs.Add(s_item);
                scene.m_namedPrefabs[ItemName.GetStableHashCode()] = s_item;
            }
            if (ObjectDB.instance != null && ObjectDB.instance.GetItemPrefab(ItemName) == null)
            {
                ObjectDB.instance.m_items.Add(s_item);
                ObjectDB.instance.m_itemByHash[ItemName.GetStableHashCode()] = s_item;
            }
        }

        private static ItemDrop Item(string name) => ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(name)?.GetComponent<ItemDrop>() : null;

        // TODO (art): a proper book icon; the Feathers icon stands in until one is rendered
        private static Sprite PieceIcon() => Item("Feathers")?.m_itemData.GetIcon();

        private static string Pretty(string prefab)
        {
            string n = prefab.Replace("FiresBook_", "").Replace("book", "Book ").Replace("paper", "Loose page ").Replace("openedbook", "Open book");
            return "Lore: " + n.Trim();
        }

        /// <summary>Bundled materials lose shader variants (they render pink): re-point them to the live piece shader.</summary>
        private static void FixShaders(GameObject go)
        {
            Shader live = Shader.Find("Custom/FiresPiece");
            if (live == null || !live.isSupported) live = Shader.Find("Custom/Piece");
            if (live == null) return;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    m.shader = live;
                    // 10-09 ([worldgen]): the pack's materials carried a leftover _MainTex scale (-0.56, 0.12) / offset
                    // (0, 0.014) that scrambles the UV-baked atlas; the baked maps want 1,1 / 0,0.
                    m.mainTextureScale = Vector2.one;
                    m.mainTextureOffset = Vector2.zero;
                    // 10-09 ([worldgen]'s journal finding): a material with its real baked albedo keeps its own look; only
                    // untextured ones are flattened (no gloss / metal) so they don't blow out under the sky.
                    if (m.mainTexture != null) continue;
                    if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0f);
                    if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                }
        }
    }
}
