using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Playlists;
using Xunit;

using Audio = MediaBrowser.Controller.Entities.Audio.Audio;
using BaseItem = MediaBrowser.Controller.Entities.BaseItem;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for <see cref="PlaylistTrackResolver"/>. Covers the pure filtering contract that
/// fixes issue #10 (PlayPlaylist always reported empty): playlist members are linked children,
/// and only visible audio items — in stable order — must survive.
/// </summary>
public class PlaylistTrackResolverTests
{
    private static Tuple<LinkedChild, BaseItem> Pair(BaseItem item)
        => new(new LinkedChild(), item);

    private static Audio NewAudio(string name)
        => new() { Name = name };  // Audio.MediaType is read-only and already returns Audio

    private static Movie NewVideo(string name)
        => new() { Name = name };

    [Fact]
    public void Filters_out_non_audio_and_unresolved()
    {
        var input = new List<Tuple<LinkedChild, BaseItem>>
        {
            Pair(NewAudio("song-1")),
            Pair(NewVideo("clip")),          // non-audio: excluded
            new(new LinkedChild(), null!),    // unresolved link: excluded, must not NRE
            Pair(NewAudio("song-2"))
        };

        IReadOnlyList<BaseItem> result = PlaylistTrackResolver.FilterAudioTracks(input, user: null);

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "song-1", "song-2" }, result.Select(i => i.Name).ToArray());
    }

    [Fact]
    public void Preserves_order()
    {
        // Order is the pagination contract: the handler takes the first page, the continuation
        // fetcher Skip()s past it — any reordering here would drop/duplicate tracks across batches.
        var input = Enumerable.Range(0, 10)
            .Select(i => Pair(NewAudio($"track-{i:D2}")))
            .ToList();

        IReadOnlyList<BaseItem> result = PlaylistTrackResolver.FilterAudioTracks(input, user: null);

        Assert.Equal(10, result.Count);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"track-{i:D2}").ToArray(),
            result.Select(i => i.Name).ToArray());
    }

    [Fact]
    public void GetAudioTracks_null_safe()
    {
        // The handler downcasts the matched BaseItem with `as Playlist`; a null must not throw.
        Assert.Empty(PlaylistTrackResolver.GetAudioTracks(playlist: null, user: null));
    }
}
