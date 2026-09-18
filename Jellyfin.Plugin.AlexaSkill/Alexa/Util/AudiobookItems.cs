using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The one replacement for the string-comparison audiobook type check (JF-567). MediaBrowser.Controller.Entities.AudioBook IS
/// in the controller package (since JF-584 every production site routes through this
/// wrapper; the bare <c>is</c> form lives only here), so the older
/// <c>GetType().Name.Equals("AudioBook", StringComparison.Ordinal)</c> string comparisons
/// were never needed; the type-pattern form also tracks subclasses of AudioBook, which
/// the exact string comparison silently dropped.
/// </summary>
internal static class AudiobookItems
{
    internal static bool IsAudioBook(BaseItem? item) => item is AudioBook;
}
