using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for GoToChapterIntent. Navigates to next, previous, or specific chapter number.
/// </summary>
public class GoToChapterIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IChapterManager _chapterManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="GoToChapterIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="chapterManager">Instance of the <see cref="IChapterManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public GoToChapterIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IChapterManager chapterManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _chapterManager = chapterManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.GoToChapter, StringComparison.Ordinal);
    }

    /// <summary>
    /// Navigate to a chapter in the currently playing media.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A skill response.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        Logger.LogDebug("GoToChapter: entered, direction={Direction}, chapterNumber={ChapterNumber}", intentRequest.Intent.Slots?.GetValueOrDefault("direction")?.Value, intentRequest.Intent.Slots?.GetValueOrDefault("chapter_number")?.Value);

        if (session.FullNowPlayingItem == null)
        {
            Logger.LogDebug("GoToChapter: no media currently playing");
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale)));
        }

        Guid itemId = session.FullNowPlayingItem.Id;
        List<ChapterInfo> chapters = _chapterManager.GetChapters(itemId).ToList();

        if (chapters.Count == 0)
        {
            Logger.LogDebug("GoToChapter: item {ItemId} has no chapters", itemId);
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("NoChapters", locale)));
        }

        // Determine navigation direction or specific chapter
        string? directionSlot = null;
        string? chapterNumberSlot = null;

        if (intentRequest.Intent.Slots != null)
        {
            if (intentRequest.Intent.Slots.TryGetValue("direction", out Slot? dirSlot))
            {
                directionSlot = dirSlot.Value;
            }

            if (intentRequest.Intent.Slots.TryGetValue("chapter_number", out Slot? numSlot))
            {
                chapterNumberSlot = numSlot.Value;
            }
        }

        int targetIndex;

        if (!string.IsNullOrEmpty(chapterNumberSlot) && int.TryParse(chapterNumberSlot, out int requestedChapter))
        {
            // "Go to chapter 5" — 1-based from user perspective
            targetIndex = requestedChapter - 1;
            if (targetIndex < 0 || targetIndex >= chapters.Count)
            {
                return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("ChapterNotFound", locale, requestedChapter.ToString(System.Globalization.CultureInfo.InvariantCulture), chapters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            }
        }
        else
        {
            // Determine current chapter from playback position
            long currentTicks = session.PlayState?.PositionTicks ?? 0;
            int currentChapter = FindCurrentChapter(chapters, currentTicks);

            if (string.Equals(directionSlot, "previous", StringComparison.OrdinalIgnoreCase))
            {
                targetIndex = Math.Max(0, currentChapter - 1);
            }
            else
            {
                // Default to next
                targetIndex = Math.Min(chapters.Count - 1, currentChapter + 1);
            }
        }

        long targetTicks = chapters[targetIndex].StartPositionTicks;
        int offsetMs = (int)TimeSpan.FromTicks(targetTicks).TotalMilliseconds;
        string itemIdStr = itemId.ToString();
        string chapterName = chapters[targetIndex].Name ?? $"Chapter {targetIndex + 1}";

        Logger.LogDebug("GoToChapter: navigating to chapter {ChapterIndex} '{ChapterName}' at offset {OffsetMs}ms", targetIndex + 1, chapterName, offsetMs);

        return Task.FromResult<SkillResponse>(BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll,
            GetStreamUrl(itemIdStr, user),
            itemIdStr,
            session.FullNowPlayingItem,
            user,
            context,
            offsetMs));
    }

    /// <summary>
    /// Find the current chapter index based on playback position.
    /// </summary>
    private static int FindCurrentChapter(List<ChapterInfo> chapters, long currentTicks)
    {
        for (int i = chapters.Count - 1; i >= 0; i--)
        {
            if (currentTicks >= chapters[i].StartPositionTicks)
            {
                return i;
            }
        }

        return 0;
    }
}
