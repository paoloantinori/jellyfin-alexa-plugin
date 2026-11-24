#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Alexa.NET.Management.InteractionModel;
using Alexa.NET.Management.Skills;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;
using Jellyfin.Plugin.AlexaSkill.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-960 first slice: the intent-to-media-family pruning table and the prune step
/// applied at model-build time when <c>MediaProfiledModels</c> is on. These tests are
/// pure (no Plugin.Instance): the table is checked against the embedded locale model
/// roster, and the pruner runs against synthetic interaction models with fake census
/// counts.
/// </summary>
public class MediaProfiledModelPrunerTests
{
    // ---------- The table: every row names an intent the committed models declare ----------

    /// <summary>
    /// Every table row must name an intent that exists in at least one embedded locale
    /// model. A row naming a never-declared intent would silently prune nothing, so the
    /// dead row must fail here instead.
    /// </summary>
    [Fact]
    public void Table_EveryRow_ExistsInAtLeastOneEmbeddedModel()
    {
        HashSet<string> declared = new(StringComparer.Ordinal);
        foreach (var model in TestLocales.Models())
        {
            var interaction = Jellyfin.Plugin.AlexaSkill.Util.DeserializeFromFile<SkillInteraction>(model.Item2);
            foreach (IntentType intent in interaction.Language.IntentTypes ?? Array.Empty<IntentType>())
            {
                declared.Add(intent.Name);
            }
        }

        foreach (string intentName in IntentMediaFamilies.Table.Keys)
        {
            Assert.True(
                declared.Contains(intentName),
                $"Pruning table row '{intentName}' does not exist in any committed locale model");
        }
    }

    /// <summary>
    /// The conservative rule pinned: dual-media and media-neutral intents must never
    /// enter the table. Each named intent here carries handler-survey evidence for its
    /// multi-family or neutral shape (see the table's exclusions comment).
    /// </summary>
    [Theory]
    [InlineData("PlayRadioIntent")] // dual: music radio seeding + live-TV radio station tier (JF-474)
    [InlineData("PlayRandomIntent")] // media_type slot, Audio+Movie+Episode
    [InlineData("RecommendIntent")] // media_type slot, Audio+Movie
    [InlineData("BrowseLibraryIntent")] // media_type slot, music+video+audiobook
    [InlineData("SearchMediaIntent")] // cross-family search union
    [InlineData("ContinueWatchingIntent")] // Audio+Movie+Episode union
    [InlineData("InProgressMediaListIntent")] // Audio+Movie+Episode union
    [InlineData("QueryRecentlyAddedIntent")] // Audio+Movie+Episode+MusicAlbum union
    [InlineData("MediaInfoIntent")] // resolves whatever plays, any family
    [InlineData("ResumeIntent")] // multi-family resume
    [InlineData("StartOverIntent")] // multi-family restart
    [InlineData("GoToChapterIntent")] // chapters of the current item, any family
    [InlineData("ShowLyricsIntent")] // bound to the playing track
    [InlineData("PlayIntent")] // generic play router
    [InlineData("AMAZON.StopIntent")] // built-ins never prunable
    [InlineData("AMAZON.FallbackIntent")]
    public void Table_NeverContains_DualMediaOrNeutralIntents(string intentName)
    {
        Assert.False(IntentMediaFamilies.Table.ContainsKey(intentName));
        Assert.Null(IntentMediaFamilies.FamilyOf(intentName));
    }

    /// <summary>
    /// The single-definition guard: the table must be keyed by the IntentNames
    /// constants' exact strings (an ordinal literal that drifted from the constant
    /// would silently stop matching the models).
    /// </summary>
    [Fact]
    public void Table_Keys_AreTheIntentNameConstants()
    {
        HashSet<string> knownConstants = new(IntentNames.AllIntentNames, StringComparer.Ordinal);
        foreach (string key in IntentMediaFamilies.Table.Keys)
        {
            Assert.True(knownConstants.Contains(key), $"Table key '{key}' is not an IntentNames constant");
        }
    }

    // ---------- The prune step with fake census counts ----------

    private const string SyntheticModel = """
        {
          "languageModel": {
            "invocationName": "test skill",
            "intents": [
              { "name": "AMAZON.StopIntent", "samples": [] },
              { "name": "SearchMediaIntent", "samples": ["search {query}"] },
              { "name": "PlaySongIntent", "samples": ["play the song {song}"] },
              { "name": "PlayVideoIntent", "samples": ["play the video {title}"] },
              { "name": "PlayBookIntent", "samples": ["read the book {book}"] },
              { "name": "PlayPodcastIntent", "samples": ["play the podcast {podcast_name}"] },
              { "name": "PlayChannelIntent", "samples": ["play the channel {channel}"] }
            ]
          },
          "types": [],
          "dialog": {
            "intents": [
              { "name": "PlaySongIntent", "slots": [] },
              { "name": "PlayBookIntent", "slots": [] },
              { "name": "PlayPodcastIntent", "slots": [] }
            ]
          }
        }
        """;

    private static (SkillInteraction Interaction, SkillInteraction UntouchedCopy) BuildSyntheticModel()
    {
        var interaction = JsonConvert.DeserializeObject<SkillInteraction>(SyntheticModel)!;
        var untouched = JsonConvert.DeserializeObject<SkillInteraction>(SyntheticModel)!;
        return (interaction, untouched);
    }

    private static IReadOnlyDictionary<MediaFamily, int> Counts(
        int video = 1, int music = 1, int audiobook = 1, int podcast = 1, int liveTv = 1)
        => new Dictionary<MediaFamily, int>
        {
            [MediaFamily.Video] = video,
            [MediaFamily.Music] = music,
            [MediaFamily.Audiobook] = audiobook,
            [MediaFamily.Podcast] = podcast,
            [MediaFamily.LiveTv] = liveTv,
        };

    [Fact]
    public void Apply_AllFamiliesPresent_PrunesNothing()
    {
        var (interaction, untouched) = BuildSyntheticModel();

        IReadOnlyList<string> pruned = MediaProfiledModelPruner.Apply(
            interaction, "it-IT", Counts(), NullLogger.Instance);

        Assert.Empty(pruned);
        Assert.Equal(
            untouched.Language.IntentTypes!.Select(i => i.Name),
            interaction.Language.IntentTypes!.Select(i => i.Name));
        Assert.Equal(
            untouched.Dialog!.Intents!.Select(i => i.Name),
            interaction.Dialog!.Intents!.Select(i => i.Name));
    }

    /// <summary>
    /// The core slice behavior: a music-only census prunes the video, audiobook,
    /// podcast, and live-TV intents and keeps every music intent and every
    /// media-neutral intent (including the AMAZON built-ins).
    /// </summary>
    [Fact]
    public void Apply_MusicOnlyCensus_PrunesAbsentFamilies_KeepsMusicAndNeutral()
    {
        var (interaction, _) = BuildSyntheticModel();

        IReadOnlyList<string> pruned = MediaProfiledModelPruner.Apply(
            interaction, "it-IT", Counts(music: 1, video: 0, audiobook: 0, podcast: 0, liveTv: 0), NullLogger.Instance);

        Assert.Equal(
            new[] { "PlayVideoIntent", "PlayBookIntent", "PlayPodcastIntent", "PlayChannelIntent" }.OrderBy(n => n),
            pruned.OrderBy(n => n));

        List<string> remaining = interaction.Language.IntentTypes!.Select(i => i.Name).ToList();
        Assert.Contains("PlaySongIntent", remaining);
        Assert.Contains("SearchMediaIntent", remaining);
        Assert.Contains("AMAZON.StopIntent", remaining);
        Assert.DoesNotContain("PlayVideoIntent", remaining);
        Assert.DoesNotContain("PlayBookIntent", remaining);
        Assert.DoesNotContain("PlayPodcastIntent", remaining);
        Assert.DoesNotContain("PlayChannelIntent", remaining);
    }

    /// <summary>
    /// The dialog consistency rule: a pruned intent's dialog.intents entry is dropped
    /// too; kept intents keep theirs. Slot types are never touched.
    /// </summary>
    [Fact]
    public void Apply_DropsDialogEntriesOfPrunedIntents_KeepsOthers()
    {
        var (interaction, _) = BuildSyntheticModel();

        MediaProfiledModelPruner.Apply(
            interaction, "en-US", Counts(music: 1, video: 0, audiobook: 0, podcast: 0, liveTv: 1), NullLogger.Instance);

        List<string> dialogNames = interaction.Dialog!.Intents!.Select(i => i.Name).ToList();
        Assert.DoesNotContain("PlayBookIntent", dialogNames); // audiobook pruned
        Assert.DoesNotContain("PlayPodcastIntent", dialogNames); // podcast pruned
        Assert.Contains("PlaySongIntent", dialogNames); // music kept

        // video intent has no dialog entry in the fixture; nothing to assert there.
    }

    [Fact]
    public void Apply_EmptyIntents_IsSafe()
    {
        var interaction = new SkillInteraction
        {
            Language = new Language
            {
                InvocationName = "test skill",
                IntentTypes = Array.Empty<IntentType>()
            }
        };

        IReadOnlyList<string> pruned = MediaProfiledModelPruner.Apply(
            interaction, "en-US", Counts(), NullLogger.Instance);

        Assert.Empty(pruned);
    }

    [Fact]
    public void Apply_MissingCensusFamilyRow_TreatsFamilyAsPresent()
    {
        // A count dictionary missing a family key is treated as present (fail open),
        // so a future family added to the table but not the census cannot prune.
        var (interaction, untouched) = BuildSyntheticModel();
        var partialCounts = new Dictionary<MediaFamily, int> { [MediaFamily.Music] = 1 };

        MediaProfiledModelPruner.Apply(interaction, "en-US", partialCounts, NullLogger.Instance);

        Assert.Equal(
            untouched.Language.IntentTypes!.Select(i => i.Name),
            interaction.Language.IntentTypes!.Select(i => i.Name));
    }

    // ---------- Census fail-open constants ----------

    [Fact]
    public void AllFamiliesPresent_IsFullyPresent()
    {
        var counts = MediaCensus.AllFamiliesPresent();
        Assert.Equal(Enum.GetValues<MediaFamily>().Length, counts.Count);
        Assert.All(counts.Values, value => Assert.True(value > 0));
    }

    /// <summary>
    /// The family-derived literals (review round): AllFamiliesPresent must contain
    /// EXACTLY the FamilyKinds keys, so a family added to the one definition cannot
    /// key a stale hand-enumerated row into a silent mismatch.
    /// </summary>
    [Fact]
    public void AllFamiliesPresent_Keys_AreExactlyTheFamilyKinds()
    {
        Assert.Equal(
            IntentMediaFamilies.FamilyKinds.Keys.OrderBy(k => k),
            MediaCensus.AllFamiliesPresent().Keys.OrderBy(k => k));
    }
}
