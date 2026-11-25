namespace FiresLoreBooks.UI
{
    /// <summary>
    /// 0.0.18 (Fire 10-21: typing in a mod window started driving the game after 30 s - Core's watchdog frees plain input-block tokens):
    /// Core 0.2.697's add-only FiresInputBlock.Acquire(object token, UnityEngine.Object owner) holds the gate while the owner lives and frees
    /// it when the owner is destroyed or disabled. Reflected, so the book windows still load (plain Acquire) on Core older than 0.2.697.
    /// </summary>
    internal static class InputGate
    {
        static bool s_looked; static System.Reflection.MethodInfo s_owned;

        internal static void Acquire(object token, UnityEngine.Object owner)
        {
            if (!s_looked)
            {
                s_looked = true;
                s_owned = typeof(FiresCore.Input.FiresInputBlock).GetMethod("Acquire", new[] { typeof(object), typeof(UnityEngine.Object) });
                FiresLoreBooksPlugin.Log?.LogInfo("[FiresLoreBooks] input block: " + (s_owned != null ? "owner-held tokens (Core 0.2.697+)" : "plain tokens (Core older than 0.2.697)"));
            }
            if (s_owned != null) { try { s_owned.Invoke(null, new object[] { token, owner }); return; } catch (System.Exception e) { FiresLoreBooksPlugin.Log?.LogWarning("[FiresLoreBooks] owner Acquire failed, plain token: " + (e.InnerException ?? e).Message); } }
            FiresCore.Input.FiresInputBlock.Acquire(token);
        }
    }
}
