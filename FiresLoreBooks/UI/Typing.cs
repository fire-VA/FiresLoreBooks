namespace FiresLoreBooks.UI
{
    /// <summary>
    /// Core's IsTyping also reports "fires panel" while any Fires window holds an input-block token, and the reader /
    /// journal hold one while open, so they blocked their own page keys (lorebooks_test turn: spread stayed 1, 10-09).
    /// Only a real text input (chat, console, a text field, a map pin...) stops the book's keys.
    /// </summary>
    internal static class Typing
    {
        internal static bool InTextInput() =>
            FiresCore.Input.FiresInputBlock.IsTyping(out string source) && source != "fires panel";
    }
}
