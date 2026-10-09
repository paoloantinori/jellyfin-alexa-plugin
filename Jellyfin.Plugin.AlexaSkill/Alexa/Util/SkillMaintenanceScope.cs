using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// JF-846: the maintenance-window scope, the transparency signal for catalog
/// syncs and custom-model rebuilds. An in-flight window is opened by the
/// operation that performs the maintenance work (the catalog-sync entry and the
/// custom-model rebuild endpoint, both via <see cref="Open"/> in a
/// <c>using</c> so every exit path closes it) and read by
/// <see cref="Pipeline.SkillMaintenanceInterceptor"/>, which prepends the
/// localized SkillUpdatingPrefix to spoken responses while a window is open, so
/// a user invoking the skill mid-sync hears that the skill is updating instead
/// of suspecting breakage. Requests mostly still WORK during a window (Amazon
/// serves the previous model until a new build completes; Jellyfin queries do
/// not contend with the sync), so the prefix informs without blocking; the
/// container-restart window during DLL deploys is server-down and not
/// coverable here. Static by design, the <see cref="IndexWarmingGate"/>
/// precedent for an ambient process-wide signal read on the request path: no
/// state persists, and the scope reads as one family with the warming gates
/// rather than as a DI-registered service. Refcounted so concurrent or
/// overlapping operations nest; the window is open while ANY scope is open.
/// </summary>
internal static class SkillMaintenanceScope
{
    private static int _refCount;

    /// <summary>
    /// Gets whether a maintenance window is currently open (any scope alive).
    /// </summary>
    public static bool IsOpen => Volatile.Read(ref _refCount) > 0;

    /// <summary>
    /// Opens a maintenance-window scope. Dispose the returned token to close
    /// it; the window stays open until every opened scope has closed. Opening
    /// and closing log at Information with the operation name so triage can
    /// correlate user complaints with maintenance windows (the debug-logging
    /// policy).
    /// </summary>
    /// <param name="operationName">The maintenance operation in flight (for logs).</param>
    /// <param name="logger">The opener's logger, carrying the window logging.</param>
    /// <returns>A token whose disposal closes this scope.</returns>
    public static IDisposable Open(string operationName, ILogger logger) => new Scope(operationName, logger);

    private sealed class Scope : IDisposable
    {
        private const string OpenMessage = "Skill maintenance window opened: {Operation} (open scopes: {Depth})";
        private const string CloseMessage = "Skill maintenance window closed: {Operation} (open scopes: {Depth})";

        private readonly string _operationName;
        private readonly ILogger _logger;
        private int _disposed;

        public Scope(string operationName, ILogger logger)
        {
            _operationName = operationName;
            _logger = logger;
            _logger.LogInformation(OpenMessage, _operationName, Interlocked.Increment(ref _refCount));
        }

        public void Dispose()
        {
            // Interlocked, not a bool guard: two concurrent disposes of one
            // token must not decrement twice (a negative refcount would leave
            // IsOpen false while a real scope is still open).
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _logger.LogInformation(CloseMessage, _operationName, Interlocked.Decrement(ref _refCount));
        }
    }
}
