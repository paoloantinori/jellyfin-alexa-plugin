using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The one replacement for the string-comparison audiobook type check (JF-567). MediaBrowser.Controller.Entities.AudioBook IS
/// in the controller package (YesIntent's JF-361 routing and the JF-563 resume branches
/// use the <c>is</c> form in production), so the older
/// <c>GetType().Name.Equals("AudioBook", StringComparison.Ordinal)</c> string comparisons
/// were never needed; the type-pattern form also tracks subclasses of AudioBook, which
/// the exact string comparison silently dropped.
/// </summary>
internal static class AudiobookItems
{
    internal static bool IsAudioBook(BaseItem? item) => item is AudioBook;
}
