---
name: new-intent
description: Guide for developing new Alexa intent handlers. Covers the handler pattern, required files, registration, interaction model updates, and test structure. Use when creating a new intent handler or modifying an existing one. Triggers: "create a new handler", "add an intent", "new intent handler", "implement intent X".
---

# Handler Development Guide

All intent handlers follow the same pattern. This guide ensures new handlers are complete and consistent.

## Handler Checklist

A new intent requires changes in 5 areas:

### 1. Handler Class

Create in `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/`. Inherit `BaseHandler`.

```csharp
public class MyIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    public MyIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(
            intentRequest.Intent.Name, IntentNames.MyIntent, StringComparison.Ordinal);
    }

    public override async Task<SkillResponse> HandleAsync(
        Request request, Context context, Entities.User user,
        SessionInfo session, CancellationToken cancellationToken)
    {
        // Implementation
    }
}
```

### 2. Intent Name Constant

Add to `Jellyfin.Plugin.AlexaSkill/Alexa/IntentNames.cs`:
```csharp
public const string MyIntent = "MyIntent";
```

### 3. Handler Registration

Register in `Jellyfin.Plugin.AlexaSkill/Alexa/Pipeline/RequestPipeline.cs` — add to the handler list in constructor order (later handlers have lower priority).

### 4. Interaction Model Samples

For each of the 17 locale files in `Alexa/InteractionModel/model_*.json`:
- Add the intent definition with sample utterances
- Add any custom slot types needed
- **CAUTION**: `AMAZON.SearchQuery` cannot coexist with other slot types in the same utterance
- **CAUTION**: Same slot name must use the same slot type across all intents in a locale

### 5. Locale Response Strings

If the handler speaks to the user:
1. Add a const key in `Alexa/Locale/ResponseStrings.cs`
2. Add translations in ALL 12+ locale JSON files in `Alexa/Locale/`
3. Use `ResponseStrings.Get("Key", locale)` — missing locale throws at runtime

## Available BaseHandler Utilities

| Utility | Purpose |
|---------|---------|
| `FuzzyMatch(query, candidates, selector, user)` | Best-match selection with configurable threshold |
| `HandleFuzzyMiss(query, candidates, ...)` | Borderline score handling (confirm/auto-play) |
| `DisambiguationHelper.AskFirstMatch(matches, mediaType, locale)` | Voice prompt for ambiguous results |
| `RetryAsync(() => fn, "label", ct)` | Library query with retry and logging |
| `ApplyLibraryFilter(query, user)` | Apply per-user library restrictions |
| `FilterByContentAccess(types)` | Filter types by config feature flags |
| `ResolveJellyfinUser(userManager, sessionId, locale)` | Get Jellyfin user from session |
| `GetStreamUrl(itemId, user)` | Audio stream URL |
| `GetVideoStreamUrl(itemId, user)` | Video stream URL |
| `BuildAudioPlayerResponse(...)` | Full audio response with metadata |
| `PopularitySort` | Sort by favorite/playcount/rating/name |

## Artist Lookup Pattern

When a handler needs to find items by artist name, use this established pattern:

```csharp
// Step 1: Find artist by name
var artistSearchQuery = new InternalItemsQuery()
{
    SearchTerm = artistName,
    IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
    DtoOptions = new DtoOptions(true)
};
ApplyLibraryFilter(artistSearchQuery, user);
IReadOnlyList<BaseItem> artists = await RetryAsync(
    () => _libraryManager.GetItemList(artistSearchQuery),
    "GetArtists", cancellationToken).ConfigureAwait(false);

// Step 2: Query items by artist ID
var artistItemsQuery = new InternalItemsQuery()
{
    User = jellyfinUser,
    Recursive = true,
    MediaTypes = new[] { MediaType.Audio },
    ArtistIds = new[] { artists[0].Id },
    DtoOptions = new DtoOptions(true)
};
ApplyLibraryFilter(artistItemsQuery, user);
```

This pattern is used in 10+ handlers. Do not invent new artist query approaches.

## Gotchas

- **Stop vs Pause**: `AMAZON.StopIntent` returns `ResponseBuilder.Empty()`. `AMAZON.PauseIntent` returns `AudioPlayerStop()`. Wrong response type = device ignores request.
- **Stream URLs**: Audio uses `/Audio/{id}/stream?static=true`, video uses `/Videos/{id}/stream?static=true`. Never use `/Download`.
- **Session attributes**: Use proper DTOs (e.g., `DisambiguationHelper.MatchInfo`), never raw `ValueTuple` — Newtonsoft.Json serializes them as `Item1`/`Item2`.

## Testing

Create test file in `Jellyfin.Plugin.AlexaSkill.Tests/Handler/`:

```csharp
public class MyIntentHandlerTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    // Constructor: init mocks, config with TestHelpers.SetServerAddress

    // Mock sequential GetItemList calls with int callCount + switch expression

    // Assert patterns:
    // - response.HasDirective<AudioPlayerPlayDirective>()
    // - response.Tells<PlainTextOutputSpeech>() for Tell responses
    // - response.Asks() for Ask responses
    // - TestHelpers.GetSpeechText(response) for speech text
    // - response.SessionAttributes for disambiguation state
}
```
