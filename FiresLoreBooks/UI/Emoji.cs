using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;

namespace FiresLoreBooks.UI
{
    /// <summary>
    /// Twemoji (CC-BY 4.0, credited in README / THIRDPARTY-NOTICES) as a TMP sprite asset, "FiresTwemoji" in the
    /// fireslorebooks bundle ([worldgen] 10-09). Typed emoji auto-replace by codepoint; the picker inserts
    /// &lt;sprite name="hex"&gt;. The grid's order comes from the embedded twemoji_map.csv (index,unicode_hex,name,...).
    /// </summary>
    internal static class Emoji
    {
        private static TMP_SpriteAsset s_asset;
        private static bool s_tried;
        private static List<string> s_codes;

        internal static TMP_SpriteAsset Asset
        {
            get
            {
                if (s_asset != null || s_tried) return s_asset;
                s_tried = true;
                AssetBundle bundle = Books.LorePieces.Bundle();
                s_asset = bundle != null ? bundle.LoadAsset<TMP_SpriteAsset>("FiresTwemoji") : null;
                if (s_asset != null && s_asset.material != null)
                {
                    // a bundled shader can lose its variants (pink): use the live one
                    Shader live = Shader.Find("TextMeshPro/Sprite");
                    if (live != null) s_asset.material.shader = live;
                }
                FiresLoreBooksPlugin.Log.LogDebug($"[LoreBooks] emoji sprite asset {(s_asset != null ? $"loaded ({s_asset.spriteCharacterTable.Count} sprites)" : "MISSING")}");
                return s_asset;
            }
        }

        /// <summary>Lets this text show emoji (typed or &lt;sprite name=...&gt;).</summary>
        internal static void Apply(TMP_Text text)
        {
            if (text != null && Asset != null) text.spriteAsset = Asset;
        }

        /// <summary>The picker's emoji, in atlas order (lowercase codepoint hex).</summary>
        internal static List<string> Codes
        {
            get
            {
                if (s_codes != null) return s_codes;
                s_codes = new List<string>();
                using (Stream s = typeof(Emoji).Assembly.GetManifestResourceStream("FiresLoreBooks.twemoji_map.csv"))
                {
                    if (s == null) return s_codes;
                    using (var reader = new StreamReader(s))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.StartsWith("index")) continue;
                            string[] f = line.Split(',');
                            if (f.Length > 1 && f[1].Length > 0) s_codes.Add(f[1].Trim().ToLowerInvariant());
                        }
                    }
                }
                return s_codes;
            }
        }
    }
}
