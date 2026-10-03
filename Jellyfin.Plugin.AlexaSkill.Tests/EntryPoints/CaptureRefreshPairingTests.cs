using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.AlexaSkill.EntryPoints;
using Jellyfin.Plugin.AlexaSkill.Tests.Handler;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.EntryPoints;

/// <summary>
/// JF-722 (code-review F4, rework F3): the structural pairing of the startup
/// capture and its deferred IN_PROGRESS refresh, pinned as a TEST instead of
/// prose. SkillStartup.CaptureAndScheduleStatusRefreshAsync is the only
/// plugin-assembly method allowed to call ScheduleInProgressLocaleStatusRefresh,
/// and the only allowed capture callers are the wrapper itself plus the refresh
/// worker (whose creation-mode recapture IS the deferred half of the pairing and
/// cannot be unpaired by construction), so a future capture call path that
/// hand-schedules (or forgets to schedule) its refresh cannot compile green and
/// silently leave frozen IN_PROGRESS rows gray until the weekly sync (the
/// "missed one" wiring class; same IL-scan discipline as
/// WarmingGateCoverageTests, via the shared <see cref="IlCallScanner"/>).
/// The scan is loud-only: discovery walks every method body of the plugin
/// assembly (compiler-generated state machines and closures included), so a new
/// direct caller always appears and fails this roster. The TEST assembly's own
/// direct capture calls (the InternalsVisibleTo pins) are outside the scanned
/// assembly and stay legal.
/// </summary>
public class CaptureRefreshPairingTests
{
    private const string PairingWrapperName = "CaptureAndScheduleStatusRefreshAsync";

    private const string RefreshWorkerName = "RefreshInProgressLocaleStatusesAsync";

    [Fact]
    public void CaptureAndSchedulerCalls_AllRouteThroughThePairingWrapper()
    {
        Assembly pluginAssembly = typeof(SkillStartup).Assembly;
        var captureTokens = IlCallScanner.MethodTokens(typeof(SkillStartup), "CaptureLocaleModelStatusesAsync").ToList();
        var schedulerTokens = IlCallScanner.MethodTokens(typeof(SkillStartup), "ScheduleInProgressLocaleStatusRefresh").ToList();

        Assert.True(captureTokens.Count == 1, $"expected exactly one capture methoddef token, found {captureTokens.Count}");
        Assert.True(schedulerTokens.Count == 1, $"expected exactly one scheduler methoddef token, found {schedulerTokens.Count}");

        var offenders = new SortedSet<string>();
        foreach (var (type, method) in IlCallScanner.DeclaredMethods(pluginAssembly))
        {
            bool callsCapture = IlCallScanner.ContainsCallToAnyToken(method, captureTokens);
            if (callsCapture || IlCallScanner.ContainsCallToAnyToken(method, schedulerTokens))
            {
                string logical = IlCallScanner.LogicalMethodName(method);
                bool allowed = string.Equals(logical, PairingWrapperName, StringComparison.Ordinal)
                    || (callsCapture && string.Equals(logical, RefreshWorkerName, StringComparison.Ordinal));
                if (!allowed)
                {
                    offenders.Add($"{type.FullName}.{logical}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Capture/scheduler calls must route through {PairingWrapperName} (the JF-722 pairing; the refresh worker {RefreshWorkerName} may call the capture itself, its own recapture mode); direct callers: [{string.Join(", ", offenders)}]. " +
            "Route the new capture site through the wrapper so its deferred refresh cannot be left unscheduled.");
    }
}
