using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Controller;

/// <summary>
/// The device-facing HTTP-method parity lint (JF-950 lesson, 2026-11-28):
/// the Echo's AudioPlayer fetcher HEAD-probes stream URLs before fetching
/// (undocumented Amazon client behavior, learned from the live round); the
/// legacy Jellyfin static endpoints answer HEAD with 200 + full headers, so a
/// GET-only replacement route breaks the device with
/// MEDIA_ERROR_SERVICE_UNAVAILABLE while unit tests stay green (the 405 fires
/// in the ROUTING layer, which method-level unit tests never execute) and the
/// endpoint logs stay silent (no action runs). THE INVARIANT: every [HttpGet]
/// route on a device-facing controller carries a matching [HttpHead] with the
/// SAME template. The rosters below are typeof-anchored (the
/// WarmingGateCoverageTests convention): renaming or deleting a roster
/// controller breaks THIS project's compile instead of leaving a vacuously
/// green suite behind. There is no mechanical ground truth for "the Echo
/// fetches this controller's URLs" (it is a behavioral fact), so when a NEW
/// controller starts serving URLs the AudioPlayer fetches directly, the same
/// change that introduces it must add it to the roster.
/// </summary>
public class DeviceFacingHttpGetHeadLintTests
{
    /// <summary>
    /// The controllers whose routes the ECHO's AUDIOPLAYER fetches directly
    /// (the fetcher that HEAD-probes). The stream/image proxy is the only
    /// family today: its actions are cheap upstream fetches, so a HEAD
    /// execution costs one loopback request.
    /// </summary>
    private static readonly Type[] AudioPlayerFetchedControllers =
    {
        typeof(Jellyfin.Plugin.AlexaSkill.Controller.StreamProxyController),
    };

    /// <summary>
    /// DOCUMENTED EXEMPTION (the 2026-11-28 live round's second lesson):
    /// VideoAudioController's 11 GET routes have NO HEAD twins ON PURPOSE.
    /// They serve the VideoApp path, which does not HEAD-probe (proven by
    /// years of on-device audiobook/movie playback through these endpoints
    /// with GET-only routing), and their actions LAUNCH FFMPEG (concat,
    /// transcode, segment serving): a HEAD twin would execute the full
    /// encode to discard the body. The exemption is ENFORCED from the other
    /// side by <see cref="VideoAppOnly_Exempt_Controllers_Carry_No_HttpHead"/>.
    /// If any VideoAudio route is ever served to the AudioPlayer path (as the
    /// JF-950 proxy does for static streams), it MUST route through the
    /// proxy instead of gaining a HEAD twin here.
    /// </summary>
    private static readonly Type[] VideoAppOnlyExemptControllers =
    {
        typeof(Jellyfin.Plugin.AlexaSkill.Controller.VideoAudioController),
    };

    /// <summary>
    /// The shared scan: the roster controllers' declared public instance
    /// actions, the single home for the reflection machinery both facts use.
    /// </summary>
    private static IEnumerable<MethodInfo> DeclaredActions(Type[] controllers)
        => controllers.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));

    [Fact]
    public void Every_DeviceFacing_HttpGet_Route_Has_A_Matching_HttpHead()
    {
        var offenders = new List<string>();
        foreach (MethodInfo method in DeclaredActions(AudioPlayerFetchedControllers))
        {
            var gets = method.GetCustomAttributes<HttpGetAttribute>().ToList();
            if (gets.Count == 0)
            {
                continue;
            }

            var heads = method.GetCustomAttributes<HttpHeadAttribute>().ToList();
            foreach (HttpGetAttribute get in gets)
            {
                if (string.IsNullOrEmpty(get.Template))
                {
                    offenders.Add(
                        $"{Name(method)} carries a template-less [HttpGet]; template the GET itself and give it an [HttpHead] with the SAME template "
                        + "(relying on the class-level route is what the 2026-11-28 first fix attempt got wrong)");
                }
                else if (!heads.Any(h => string.Equals(h.Template, get.Template, StringComparison.Ordinal)))
                {
                    offenders.Add($"{Name(method)} [HttpGet(\"{get.Template}\")] has no [HttpHead] with the same template");
                }
            }
        }

        AssertNoOffenders(
            "Device-facing GET routes without a HEAD twin (the Echo HEAD-probes stream URLs; a GET-only route 405s at the routing layer and the device aborts): ",
            offenders);
    }

    /// <summary>
    /// The template-less [HttpHead] does NOT combine with a templated [HttpGet]
    /// (the first fix attempt of the 2026-11-28 round failed exactly there:
    /// the 405 kept answering Allow: GET). Pin that shape so nobody reintroduces
    /// it thinking it is the minimal form.
    /// </summary>
    [Fact]
    public void DeviceFacing_HttpHead_Twins_Are_Always_Templated()
    {
        var offenders = DeclaredActions(AudioPlayerFetchedControllers)
            .SelectMany(m => m.GetCustomAttributes<HttpHeadAttribute>().Select(h => new { Method = m, Head = h }))
            .Where(x => string.IsNullOrEmpty(x.Head.Template))
            .Select(x => $"{Name(x.Method)} carries a template-less [HttpHead]; it does not combine with a templated [HttpGet] route")
            .ToList();

        AssertNoOffenders(
            "Device-facing [HttpHead] attributes must carry an explicit template (a template-less [HttpHead] does NOT combine with a templated [HttpGet]; the route stays GET-only and the Echo's HEAD probe still 405s): ",
            offenders);
    }

    /// <summary>
    /// Enforces the documented <see cref="VideoAppOnlyExemptControllers"/> exemption
    /// from the other side: those controllers must stay HEAD-free, because their
    /// actions launch ffmpeg and the VideoApp path never HEAD-probes.
    /// </summary>
    [Fact]
    public void VideoAppOnly_Exempt_Controllers_Carry_No_HttpHead()
    {
        var offenders = DeclaredActions(VideoAppOnlyExemptControllers)
            .Where(m => m.GetCustomAttributes<HttpHeadAttribute>().Any())
            .Select(m => $"{Name(m)} carries an [HttpHead]")
            .ToList();

        AssertNoOffenders(
            "VideoApp-only exempt controllers must carry no [HttpHead] (their actions launch ffmpeg, so a HEAD twin would execute the full encode to discard the body; if the route must serve the AudioPlayer path, route it through the stream proxy instead): ",
            offenders);
    }

    private static string Name(MethodInfo method)
        => $"{method.DeclaringType!.Name}.{method.Name}";

    /// <summary>
    /// The shared assert tail: every fact collects offender strings and fails
    /// with its invariant context (kept verbatim at the call site) followed by
    /// the offender list.
    /// </summary>
    private static void AssertNoOffenders(string context, List<string> offenders)
        => Assert.True(offenders.Count == 0, context + string.Join("; ", offenders));
}
