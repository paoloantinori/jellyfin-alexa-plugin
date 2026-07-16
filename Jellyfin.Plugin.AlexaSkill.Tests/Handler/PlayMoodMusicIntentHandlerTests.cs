using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class PlayMoodMusicIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PlayMoodMusicIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private PlayMoodMusicIntentHandler CreateHandler()
    {
        return new PlayMoodMusicIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateIntentRequest(string? mood = null)
    {
        var intent = new Intent { Name = IntentNames.PlayMoodMusic };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (mood != null)
        {
            intent.Slots["mood"] = new global::Alexa.NET.Request.Slot { Name = "mood", Value = mood };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static Context CreateContext()
    {
        return TestHelpers.CreateTestContext();
    }

    private SessionInfo CreateSession()
    {
        return TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    }

    private static Entities.User CreateUser()
    {
        return TestHelpers.CreateTestUser();
    }

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    [Fact]
    public void CanHandle_PlayMoodMusicIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(mood: "relaxing");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = "PlaySongIntent" },
            RequestId = "test-req"
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_MissingMood_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_RelaxingMood_PlaysFromMappedGenres()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(mood: "relaxing");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var audio = new Audio { Name = "Chill Track", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });

        _libraryManagerMock.Setup(l => l.GetItemById(audio.Id))
            .Returns(audio);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
    }

    [Fact]
    public async Task HandleAsync_UnknownMood_TriesMoodAsGenre()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(mood: "funkyspacerock");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var audio = new Audio { Name = "Space Rock", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });

        _libraryManagerMock.Setup(l => l.GetItemById(audio.Id))
            .Returns(audio);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
    }

    [Fact]
    public async Task HandleAsync_NoMatchingMusic_ReturnsNotFound()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(mood: "relaxing");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Theory]
    [InlineData("morning")]
    [InlineData("evening")]
    [InlineData("dinner")]
    [InlineData("workout")]
    public async Task HandleAsync_TimeOfDayMoods_PlaysFromMappedGenres(string mood)
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(mood: mood);
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var audio = new Audio { Name = "Test Track", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });

        _libraryManagerMock.Setup(l => l.GetItemById(audio.Id))
            .Returns(audio);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
    }

    [Fact]
    public void ResolveGenres_ExactMatch_ReturnsMappedGenres()
    {
        string[] genres = PlayMoodMusicIntentHandler.ResolveGenres("morning", hour: -1);
        Assert.Contains("acoustic", genres);
    }

    [Fact]
    public void ResolveGenres_TimeBiasMorning_ReordersGenres()
    {
        // "happy" maps to [pop, dance, reggae]. Morning bias (hour 7) prefers pop/folk/indie.
        // "pop" is in morning preferred list so it should come first.
        string[] morningBias = PlayMoodMusicIntentHandler.ResolveGenres("happy", hour: 7);
        Assert.Equal(3, morningBias.Length);
        Assert.Equal("pop", morningBias[0], ignoreCase: true);
    }

    [Fact]
    public void ResolveGenres_TimeBiasEvening_ReordersGenres()
    {
        // "relaxing" maps to [ambient, acoustic, jazz, classical, new age].
        // Evening bias (hour 20) prefers jazz, ambient, lounge, soul, classical.
        // Jazz and ambient are in both, so they should be at the front.
        string[] eveningBias = PlayMoodMusicIntentHandler.ResolveGenres("relaxing", hour: 20);
        Assert.Equal(5, eveningBias.Length);

        // Preferred genres (jazz, ambient, classical) should appear before non-preferred (acoustic, new age)
        int jazzIdx = Array.FindIndex(eveningBias, g => g.Equals("jazz", StringComparison.OrdinalIgnoreCase));
        int ambientIdx = Array.FindIndex(eveningBias, g => g.Equals("ambient", StringComparison.OrdinalIgnoreCase));
        int acousticIdx = Array.FindIndex(eveningBias, g => g.Equals("acoustic", StringComparison.OrdinalIgnoreCase));

        // Jazz and ambient should rank ahead of acoustic (not in evening preferred)
        Assert.True(jazzIdx < acousticIdx, "jazz should rank ahead of acoustic in evening bias");
        Assert.True(ambientIdx < acousticIdx, "ambient should rank ahead of acoustic in evening bias");
    }

    [Fact]
    public void ResolveGenres_TimeBiasAfternoon_PrefersHighEnergy()
    {
        // "upbeat" maps to [pop, rock, dance, electronic]. Afternoon (hour 15) prefers rock, electronic, hip hop, dance.
        // rock and electronic should be boosted ahead of pop.
        string[] afternoonBias = PlayMoodMusicIntentHandler.ResolveGenres("upbeat", hour: 15);
        Assert.Equal(4, afternoonBias.Length);

        int rockIdx = Array.FindIndex(afternoonBias, g => g.Equals("rock", StringComparison.OrdinalIgnoreCase));
        int electronicIdx = Array.FindIndex(afternoonBias, g => g.Equals("electronic", StringComparison.OrdinalIgnoreCase));
        int popIdx = Array.FindIndex(afternoonBias, g => g.Equals("pop", StringComparison.OrdinalIgnoreCase));

        Assert.True(rockIdx < popIdx, "rock should rank ahead of pop in afternoon bias");
        Assert.True(electronicIdx < popIdx, "electronic should rank ahead of pop in afternoon bias");
    }

    [Fact]
    public void ResolveGenres_UnknownMood_FallsBackToRawMood()
    {
        string[] genres = PlayMoodMusicIntentHandler.ResolveGenres("funkyspacerock", hour: 12);
        Assert.Single(genres);
        Assert.Equal("funkyspacerock", genres[0]);
    }

    [Fact]
    public void ResolveGenres_ItalianMood_Rilassante_ReturnsRelaxingGenres()
    {
        string[] rilassanteGenres = PlayMoodMusicIntentHandler.ResolveGenres("rilassante", hour: -1);
        string[] relaxingGenres = PlayMoodMusicIntentHandler.ResolveGenres("relaxing", hour: -1);

        Assert.Equal(relaxingGenres, rilassanteGenres);
        Assert.Contains("ambient", rilassanteGenres);
        Assert.Contains("jazz", rilassanteGenres);
    }

    [Fact]
    public void ResolveGenres_ItalianMood_Allegra_ReturnsHappyGenres()
    {
        string[] allegraGenres = PlayMoodMusicIntentHandler.ResolveGenres("allegra", hour: -1);
        string[] happyGenres = PlayMoodMusicIntentHandler.ResolveGenres("happy", hour: -1);

        Assert.Equal(happyGenres, allegraGenres);
        Assert.Contains("pop", allegraGenres);
        Assert.Contains("dance", allegraGenres);
    }

    [Fact]
    public void ResolveGenres_ItalianMood_Calma_ReturnsChillGenres()
    {
        string[] calmaGenres = PlayMoodMusicIntentHandler.ResolveGenres("calma", hour: -1);
        string[] chillGenres = PlayMoodMusicIntentHandler.ResolveGenres("chill", hour: -1);

        Assert.Equal(chillGenres, calmaGenres);
        Assert.Contains("chillout", calmaGenres);
        Assert.Contains("ambient", calmaGenres);
    }

    [Fact]
    public void ResolveGenres_ItalianMood_Unknown_ReturnsRawMood()
    {
        string[] genres = PlayMoodMusicIntentHandler.ResolveGenres("sconosciuta", hour: -1);

        Assert.Single(genres);
        Assert.Equal("sconosciuta", genres[0]);
    }

    [Fact]
    public async Task HandleAsync_ItalianMood_Rilassante_PlaysFromMappedGenres()
    {
        var handler = CreateHandler();
        var intent = new Intent { Name = IntentNames.PlayMoodMusic };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>
        {
            ["mood"] = new global::Alexa.NET.Request.Slot { Name = "mood", Value = "rilassante" }
        };
        var request = new IntentRequest { Intent = intent, Locale = "it-IT", RequestId = "test-req" };
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var audio = new Audio { Name = "Italian Chill Track", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });

        _libraryManagerMock.Setup(l => l.GetItemById(audio.Id))
            .Returns(audio);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
    }

    // --- Italian compound moods ---

    [Fact]
    public void ResolveGenres_ItalianMood_DaFesta_ReturnsPartyGenres()
    {
        string[] compoundGenres = PlayMoodMusicIntentHandler.ResolveGenres("da festa", hour: -1);
        string[] partyGenres = PlayMoodMusicIntentHandler.ResolveGenres("party", hour: -1);

        Assert.Equal(partyGenres, compoundGenres);
        Assert.Contains("dance", compoundGenres);
    }

    [Fact]
    public void ResolveGenres_ItalianMood_DaAllenamento_ReturnsWorkoutGenres()
    {
        string[] compoundGenres = PlayMoodMusicIntentHandler.ResolveGenres("da allenamento", hour: -1);
        string[] workoutGenres = PlayMoodMusicIntentHandler.ResolveGenres("workout", hour: -1);

        Assert.Equal(workoutGenres, compoundGenres);
        Assert.Contains("electronic", compoundGenres);
    }

    [Fact]
    public void ResolveGenres_ItalianMood_DaCena_ReturnsDinnerGenres()
    {
        string[] compoundGenres = PlayMoodMusicIntentHandler.ResolveGenres("da cena", hour: -1);
        string[] dinnerGenres = PlayMoodMusicIntentHandler.ResolveGenres("dinner", hour: -1);

        Assert.Equal(dinnerGenres, compoundGenres);
        Assert.Contains("jazz", compoundGenres);
    }

    // --- German (de-DE) moods ---

    [Fact]
    public void ResolveGenres_GermanMood_Entspannend_ReturnsRelaxingGenres()
    {
        string[] germanGenres = PlayMoodMusicIntentHandler.ResolveGenres("entspannend", hour: -1);
        string[] englishGenres = PlayMoodMusicIntentHandler.ResolveGenres("relaxing", hour: -1);

        Assert.Equal(englishGenres, germanGenres);
        Assert.Contains("ambient", germanGenres);
    }

    [Fact]
    public void ResolveGenres_GermanMood_Traurig_ReturnsSadGenres()
    {
        string[] germanGenres = PlayMoodMusicIntentHandler.ResolveGenres("traurig", hour: -1);
        string[] englishGenres = PlayMoodMusicIntentHandler.ResolveGenres("sad", hour: -1);

        Assert.Equal(englishGenres, germanGenres);
        Assert.Contains("blues", germanGenres);
    }

    [Fact]
    public void ResolveGenres_GermanMood_Feier_ReturnsPartyGenres()
    {
        string[] germanGenres = PlayMoodMusicIntentHandler.ResolveGenres("feier", hour: -1);
        string[] englishGenres = PlayMoodMusicIntentHandler.ResolveGenres("party", hour: -1);

        Assert.Equal(englishGenres, germanGenres);
        Assert.Contains("dance", germanGenres);
    }

    [Fact]
    public void ResolveGenres_GermanMood_Abendessen_ReturnsDinnerGenres()
    {
        string[] germanGenres = PlayMoodMusicIntentHandler.ResolveGenres("abendessen", hour: -1);
        string[] englishGenres = PlayMoodMusicIntentHandler.ResolveGenres("dinner", hour: -1);

        Assert.Equal(englishGenres, germanGenres);
        Assert.Contains("jazz", germanGenres);
    }

    // --- Spanish (es-ES) moods ---

    [Fact]
    public void ResolveGenres_SpanishMood_Relajante_ReturnsRelaxingGenres()
    {
        string[] spanishGenres = PlayMoodMusicIntentHandler.ResolveGenres("relajante", hour: -1);
        string[] englishGenres = PlayMoodMusicIntentHandler.ResolveGenres("relaxing", hour: -1);

        Assert.Equal(englishGenres, spanishGenres);
        Assert.Contains("ambient", spanishGenres);
    }

    [Fact]
    public void ResolveGenres_SpanishMood_Animado_ReturnsUpbeatGenres()
    {
        string[] spanishGenres = PlayMoodMusicIntentHandler.ResolveGenres("animado", hour: -1);
        string[] englishGenres = PlayMoodMusicIntentHandler.ResolveGenres("upbeat", hour: -1);

        Assert.Equal(englishGenres, spanishGenres);
        Assert.Contains("pop", spanishGenres);
    }

    [Fact]
    public void ResolveGenres_SpanishMood_Entrenamiento_ReturnsWorkoutGenres()
    {
        string[] spanishGenres = PlayMoodMusicIntentHandler.ResolveGenres("entrenamiento", hour: -1);
        string[] englishGenres = PlayMoodMusicIntentHandler.ResolveGenres("workout", hour: -1);

        Assert.Equal(englishGenres, spanishGenres);
        Assert.Contains("electronic", spanishGenres);
    }

    [Fact]
    public void ResolveGenres_SpanishMood_Nocturno_ReturnsEveningGenres()
    {
        string[] spanishGenres = PlayMoodMusicIntentHandler.ResolveGenres("nocturno", hour: -1);
        string[] englishGenres = PlayMoodMusicIntentHandler.ResolveGenres("evening", hour: -1);

        Assert.Equal(englishGenres, spanishGenres);
        Assert.Contains("jazz", spanishGenres);
    }

    // --- French (fr-FR) moods ---

    [Fact]
    public void ResolveGenres_FrenchMood_Détendu_ReturnsRelaxingGenres()
    {
        string[] frenchGenres = PlayMoodMusicIntentHandler.ResolveGenres("détendu", hour: -1);
        string[] englishGenres = PlayMoodMusicIntentHandler.ResolveGenres("relaxing", hour: -1);

        Assert.Equal(englishGenres, frenchGenres);
        Assert.Contains("ambient", frenchGenres);
    }

    [Fact]
    public void ResolveGenres_FrenchMood_Énergique_ReturnsEnergeticGenres()
    {
        string[] frenchGenres = PlayMoodMusicIntentHandler.ResolveGenres("énergique", hour: -1);
        string[] englishGenres = PlayMoodMusicIntentHandler.ResolveGenres("energetic", hour: -1);

        Assert.Equal(englishGenres, frenchGenres);
        Assert.Contains("rock", frenchGenres);
    }

    [Fact]
    public void ResolveGenres_FrenchMood_Soirée_ReturnsEveningGenres()
    {
        string[] frenchGenres = PlayMoodMusicIntentHandler.ResolveGenres("soirée", hour: -1);
        string[] englishGenres = PlayMoodMusicIntentHandler.ResolveGenres("evening", hour: -1);

        Assert.Equal(englishGenres, frenchGenres);
        Assert.Contains("jazz", frenchGenres);
    }

    [Fact]
    public void ResolveGenres_FrenchMood_Entraînement_ReturnsWorkoutGenres()
    {
        string[] frenchGenres = PlayMoodMusicIntentHandler.ResolveGenres("entraînement", hour: -1);
        string[] englishGenres = PlayMoodMusicIntentHandler.ResolveGenres("workout", hour: -1);

        Assert.Equal(englishGenres, frenchGenres);
        Assert.Contains("electronic", frenchGenres);
    }

    // --- Portuguese (pt-BR) moods ---

    [Fact]
    public void ResolveGenres_PortugueseMood_Relaxante_ReturnsRelaxingGenres()
    {
        string[] ptGenres = PlayMoodMusicIntentHandler.ResolveGenres("relaxante", hour: -1);
        string[] englishGenres = PlayMoodMusicIntentHandler.ResolveGenres("relaxing", hour: -1);

        Assert.Equal(englishGenres, ptGenres);
        Assert.Contains("ambient", ptGenres);
    }

    [Fact]
    public void ResolveGenres_PortugueseMood_Treino_ReturnsWorkoutGenres()
    {
        string[] ptGenres = PlayMoodMusicIntentHandler.ResolveGenres("treino", hour: -1);
        string[] englishGenres = PlayMoodMusicIntentHandler.ResolveGenres("workout", hour: -1);

        Assert.Equal(englishGenres, ptGenres);
        Assert.Contains("electronic", ptGenres);
    }

    [Fact]
    public void ResolveGenres_PortugueseMood_Noite_ReturnsEveningGenres()
    {
        string[] ptGenres = PlayMoodMusicIntentHandler.ResolveGenres("noite", hour: -1);
        string[] englishGenres = PlayMoodMusicIntentHandler.ResolveGenres("evening", hour: -1);

        Assert.Equal(englishGenres, ptGenres);
        Assert.Contains("jazz", ptGenres);
    }

    [Fact]
    public void ResolveGenres_PortugueseMood_Jantar_ReturnsDinnerGenres()
    {
        string[] ptGenres = PlayMoodMusicIntentHandler.ResolveGenres("jantar", hour: -1);
        string[] englishGenres = PlayMoodMusicIntentHandler.ResolveGenres("dinner", hour: -1);

        Assert.Equal(englishGenres, ptGenres);
        Assert.Contains("jazz", ptGenres);
    }

    // --- Cross-locale and edge cases ---

    [Fact]
    public void ResolveGenres_Calma_SharedByItalianAndPortuguese_ReturnsChillGenres()
    {
        // "calma" exists in both Italian and Portuguese, both map to "chill"
        string[] calmaGenres = PlayMoodMusicIntentHandler.ResolveGenres("calma", hour: -1);
        string[] chillGenres = PlayMoodMusicIntentHandler.ResolveGenres("chill", hour: -1);

        Assert.Equal(chillGenres, calmaGenres);
        Assert.Contains("chillout", calmaGenres);
        Assert.Contains("ambient", calmaGenres);
    }

    [Fact]
    public void ResolveGenres_SubstringMatch_GermanInLongerPhrase()
    {
        // Substring match: "unheimlich entspannend" should match "entspannend"
        string[] phraseGenres = PlayMoodMusicIntentHandler.ResolveGenres("unheimlich entspannend", hour: -1);
        string[] relaxingGenres = PlayMoodMusicIntentHandler.ResolveGenres("relaxing", hour: -1);

        Assert.Equal(relaxingGenres, phraseGenres);
    }

    [Fact]
    public void ResolveGenres_SubstringMatch_SpanishInLongerPhrase()
    {
        // Substring match: "música muy relajante" should match "relajante"
        string[] phraseGenres = PlayMoodMusicIntentHandler.ResolveGenres("música muy relajante", hour: -1);
        string[] relaxingGenres = PlayMoodMusicIntentHandler.ResolveGenres("relaxing", hour: -1);

        Assert.Equal(relaxingGenres, phraseGenres);
    }

    [Fact]
    public async Task HandleAsync_ArtistGenreFallback_FindsTracks()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(mood: "relaxing");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var artist = new MusicArtist { Name = "Ambient Artist", Id = Guid.NewGuid() };
        var audio = new Audio { Name = "Ambient Track", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                // First calls are track-genre search (BaseItemKind.Audio with Genres) → return empty
                // Then artist-genre search (BaseItemKind.MusicArtist with Genres) → return artist
                // Then artist track search (BaseItemKind.Audio with ArtistIds) → return audio
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist))
                {
                    return new List<BaseItem> { artist };
                }

                if (q.ArtistIds != null && q.ArtistIds.Length > 0)
                {
                    return new List<BaseItem> { audio };
                }

                // Track-genre search: return empty to trigger artist fallback
                return new List<BaseItem>();
            });

        _libraryManagerMock.Setup(l => l.GetItemById(audio.Id))
            .Returns(audio);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
    }

    [Fact]
    public async Task HandleAsync_MoodMissWithArtistName_FallsBackToArtist()
    {
        var artistId = Guid.NewGuid();
        var song1 = new Audio { Name = "So What", Id = Guid.NewGuid() };
        var song2 = new Audio { Name = "Blue in Green", Id = Guid.NewGuid() };

        var handler = CreateHandler();
        var intent = new Intent { Name = IntentNames.PlayMoodMusic };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>
        {
            ["mood"] = new global::Alexa.NET.Request.Slot { Name = "mood", Value = "di miles davis" }
        };
        var request = new IntentRequest { Intent = intent, Locale = "it-IT", RequestId = "test-req" };
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                bool isArtistQuery = q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist);
                bool isAudioQuery = q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio);
                bool hasGenres = q.Genres != null && q.Genres.Count > 0;

                // Genre track search → empty
                if (hasGenres && isAudioQuery)
                {
                    return new List<BaseItem>();
                }

                // Artist-genre search → empty
                if (hasGenres && isArtistQuery)
                {
                    return new List<BaseItem>();
                }

                // Entity fallback: artist search via SearchTerm (no Genres)
                if (q.SearchTerm != null && isArtistQuery && !hasGenres)
                {
                    return new List<BaseItem> { new MusicArtist { Name = "Miles Davis", Id = artistId } };
                }

                // Artist songs fallback (ArtistIds + MediaTypes Audio)
                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.MediaTypes != null && q.MediaTypes.Any(t => t == MediaType.Audio))
                {
                    return new List<BaseItem> { song1, song2 };
                }

                return new List<BaseItem>();
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should return audio player directive (artist songs playback), not NotFoundMood
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);

        // Queue should have the artist's songs
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(2, session.NowPlayingQueue.Count);

        // Should include FoundArtistInstead announcement
        Assert.NotNull(response.Response.OutputSpeech);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Miles Davis", speech);
    }

    [Fact]
    public async Task HandleAsync_MoodMissNoArtist_ReturnsNotFoundMood()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(mood: "relaxing");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        // All searches return empty — no genre tracks, no artist-genre, no artist fallback
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should return NotFoundMood tell
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("relaxing", speech, StringComparison.OrdinalIgnoreCase);
    }
}
