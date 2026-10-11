namespace Jellyfin.Plugin.AlexaSkill.Alexa.Apl;

/// <summary>
/// One row of the tappable paginated picker (JF-624 item 4, AlexaPaginatedList).
/// <para>
/// <see cref="TapArguments"/> null (the default): the row rides the template-level
/// primaryAction, whose documented <c>${ordinal}</c> binding identifies the tapped
/// row by position (the chapter picker: rows are the contiguous 1-based chapter
/// list, so the ordinal IS the chapter number). Non-null: the row carries its own
/// per-item primaryAction override with EXACT tap arguments (the episode picker:
/// ordinal-to-episode-number is not identity when seasons have gaps or specials,
/// so each row speaks its own season/episode/series strings; the per-item override
/// is the documented AlexaPaginatedList mechanism).
/// </para>
/// </summary>
/// <param name="PrimaryText">Row title (required).</param>
/// <param name="SecondaryText">Optional row subtitle.</param>
/// <param name="TapArguments">Optional exact SendEvent arguments for this row (first element is the action name).</param>
internal sealed record PaginatedListRow(
    string PrimaryText,
    string? SecondaryText = null,
    string[]? TapArguments = null);
