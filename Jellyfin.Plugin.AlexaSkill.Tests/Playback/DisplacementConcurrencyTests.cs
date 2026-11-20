using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using global::Alexa.NET.Request;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Model.Session;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Playback;

/// <summary>
/// JF-932 (issue #45): a concurrency hammer over the REAL in-memory state the
/// same-item displacement path shares. The reporter's fail/work alternation is
/// only explainable while this state machine stays coherent: a launch replacing
/// the actively-playing same-item stream mints a generation suffix, and a FAILED
/// suffixed play must leave the active-audio flag off so the NEXT launch mints
/// bare (that is the alternation mechanism). A lost update or a wedge under
/// interleaved launches/events would be a plausible plugin-side contributor to
/// the reported pattern, so each piece is hammered in isolation with production
/// types only (no mocks). Scale honesty: 200 tasks x 5 iterations of microsecond
/// in-memory ops prove the INVARIANTS (generation uniqueness, token
/// well-formedness, settle state after quiesce), not load; nothing here measures
/// latency or throughput. The pieces: the Interlocked generation counter in
/// <see cref="PlaybackLaunchBuilder.MintStreamToken"/>, the ConcurrentDictionary
/// active-audio flag on <see cref="DeviceQueueManager"/> (JF-655), and the static
/// <see cref="PlaybackReportOrdering"/> state (JF-447). This file is also the
/// first DIRECT coverage of MintStreamToken (previously covered only
/// transitively). It joins the Plugin collection because the JF-792/JF-800
/// parallel-phase static-surface contract mandates collection membership for ANY
/// PlaybackReportOrdering touch, not merely interference-free device ids; the
/// ordering fact clears its own device state at the end so the file stays
/// self-contained.
/// </summary>
[Collection("Plugin")]
public sealed class DisplacementConcurrencyTests
{
    private const int Tasks = 200;
    private const int Iterations = 5;

    [Fact]
    public async Task ParallelFlagArmDisplacement_MintsUniqueGenerations()
    {
        // Uniqueness here flows from the process-global Interlocked counter, so this
        // fact pins the counter and the flag-arm gate shape; the flapping fact below
        // is the one whose mint decisions race real Mark writers.
        using var queue = TestHelpers.CreateDeviceQueueManager("jf932-flagarm");
        string deviceId = $"dev-{Guid.NewGuid():N}";
        Guid itemId = Guid.NewGuid();
        queue.MarkAudioPlaybackStarted(deviceId);
        var context = TestHelpers.CreateContextWithToken(itemId.ToString(), deviceId, "IDLE");

        var minted = await MintConcurrently(queue, context, itemId.ToString());
        AssertAllSuffixedAndUnique(minted, itemId);
    }

    [Fact]
    public async Task FlappingFlag_ConcurrentMint_EveryTokenWellFormedAndFlagSettles()
    {
        using var queue = TestHelpers.CreateDeviceQueueManager("jf932-flap");
        string deviceId = $"dev-{Guid.NewGuid():N}";
        Guid itemId = Guid.NewGuid();
        var context = TestHelpers.CreateContextWithToken(itemId.ToString(), deviceId, "IDLE");
        var minted = new ConcurrentBag<string>();

        var tasks = Enumerable.Range(0, Tasks).Select(n => Task.Run(() =>
        {
            for (int i = 0; i < Iterations; i++)
            {
                switch (n % 3)
                {
                    case 0:
                        queue.MarkAudioPlaybackStarted(deviceId);
                        break;
                    case 1:
                        queue.MarkAudioPlaybackStopped(deviceId);
                        break;
                    default:
                        minted.Add(PlaybackLaunchBuilder.MintStreamToken(context, itemId.ToString(), queue));
                        break;
                }
            }
        }));
        await Task.WhenAll(tasks);

        var suffixed = new List<long>();
        foreach (string token in minted)
        {
            Assert.True(
                StreamTokenCodec.TryGetItemId(token, out Guid parsed) && parsed == itemId,
                $"token lost the item id: {token}");
            if (StreamTokenCodec.TryGetLaunchGeneration(token, out long generation))
            {
                suffixed.Add(generation);
            }
            else
            {
                Assert.Equal(itemId.ToString(), token);
            }
        }

        Assert.Equal(suffixed.Count, suffixed.Distinct().Count());
        queue.MarkAudioPlaybackStopped(deviceId);
        Assert.False(queue.IsAudioPlaybackActive(deviceId));

        // The alternation mechanism itself (issue #45): flag off means the NEXT
        // launch mints BARE; over-minting suffixes after a stop would silently
        // break the reported fail/work pattern.
        Assert.Equal(itemId.ToString(), PlaybackLaunchBuilder.MintStreamToken(context, itemId.ToString(), queue));
    }

    [Fact]
    public async Task ContextArmPlayingActivity_MintsSuffixedWithoutFlag()
    {
        using var queue = TestHelpers.CreateDeviceQueueManager("jf932-ctxarm");
        string deviceId = $"dev-{Guid.NewGuid():N}";
        Guid itemId = Guid.NewGuid();
        var context = TestHelpers.CreateContextWithToken(itemId.ToString(), deviceId, "PLAYING");
        Assert.False(queue.IsAudioPlaybackActive(deviceId));

        var minted = await MintConcurrently(queue, context, itemId.ToString());
        AssertAllSuffixedAndUnique(minted, itemId);
    }

    [Fact]
    public async Task ReportOrdering_InterleavedStorm_ClassifiesDeterministicallyAfterQuiesce()
    {
        string deviceId = $"dev-{Guid.NewGuid():N}";
        Guid itemId = Guid.NewGuid();
        string oldToken = itemId.ToString();
        string newToken = StreamTokenCodec.WithLaunchGeneration(itemId.ToString());

        var tasks = Enumerable.Range(0, Tasks).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < Iterations; i++)
            {
                string token = (i % 2 == 0) ? newToken : oldToken;
                PlaybackReportOrdering.BeginStart(deviceId, new PlaybackStartInfo { ItemId = itemId }, launchGeneration: null);
                PlaybackReportOrdering.ClassifyStop(deviceId, token);
            }
        }));
        await Task.WhenAll(tasks);

        try
        {
            // Quiesce re-start carries the real generation; the storm above deliberately
            // hammered the generation-less BeginStart path instead.
            Assert.True(StreamTokenCodec.TryGetLaunchGeneration(newToken, out long generation));
            PlaybackReportOrdering.BeginStart(
                deviceId,
                new PlaybackStartInfo { ItemId = itemId },
                launchGeneration: generation);
            Assert.Equal(PlaybackReportOrdering.StopKind.Real, PlaybackReportOrdering.ClassifyStop(deviceId, newToken));
            Assert.Equal(PlaybackReportOrdering.StopKind.DisplacedSameItem, PlaybackReportOrdering.ClassifyStop(deviceId, oldToken));
        }
        finally
        {
            // Clear() wipes the WHOLE process-global ordering state (every device plus
            // the in-flight counters), not just this fact's device; the Plugin
            // collection fixture teardown does the same, and this finally keeps a
            // failing assert from leaking storm state into the serialized siblings.
            PlaybackReportOrdering.Clear();
        }
    }

    private static async Task<ConcurrentBag<string>> MintConcurrently(DeviceQueueManager queue, Context context, string itemId)
    {
        var minted = new ConcurrentBag<string>();
        var tasks = Enumerable.Range(0, Tasks).Select(_ => Task.Run(() =>
            minted.Add(PlaybackLaunchBuilder.MintStreamToken(context, itemId, queue))));
        await Task.WhenAll(tasks);
        return minted;
    }

    private static void AssertAllSuffixedAndUnique(ConcurrentBag<string> minted, Guid itemId)
    {
        Assert.Equal(Tasks, minted.Count);
        var generations = new List<long>(minted.Count);
        foreach (string token in minted)
        {
            Assert.True(StreamTokenCodec.TryGetLaunchGeneration(token, out long generation), $"missing launch suffix: {token}");
            Assert.True(StreamTokenCodec.TryGetItemId(token, out Guid parsed) && parsed == itemId, $"token lost the item id: {token}");
            generations.Add(generation);
        }

        Assert.Equal(minted.Count, generations.Distinct().Count());
    }
}
