using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Resolves a playable stream URL for a Live TV channel.
/// </summary>
/// <remarks>
/// Live TV channels cannot be served via the static stream endpoint used for files
/// (it returns HTTP 500 for a live source). This resolver queries Jellyfin's
/// PlaybackInfo (with <c>AutoOpenLiveStream</c>) and picks the right URL strategy:
/// the remote HLS URL played directly for IPTV/M3U sources, or Jellyfin's dynamic
/// HLS master playlist for tuners that require transcoding.
/// </remarks>
public interface ILiveTvStreamResolver
{
    /// <summary>
    /// Resolve a stream URL suitable for a <c>VideoApp.Launch</c> source.
    /// </summary>
    /// <param name="channel">The LiveTvChannel BaseItem to play.</param>
    /// <param name="user">The skill user (provides the Jellyfin user id + api token).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved stream info, or <c>null</c> if playback info could not be resolved.</returns>
    Task<LiveTvStream?> ResolveAsync(BaseItem channel, Entities.User user, CancellationToken cancellationToken);
}

/// <summary>
/// Resolved live TV stream info.
/// </summary>
/// <param name="Url">URL to feed <c>VideoApp.Launch</c> as the video source.</param>
public sealed record LiveTvStream(string Url);
