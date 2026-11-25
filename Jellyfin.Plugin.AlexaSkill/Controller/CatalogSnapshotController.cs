#nullable enable

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Controller;

/// <summary>
/// The authoritative catalog snapshot export (JF-954.8): a read-only view of
/// the exact per-locale catalog payloads the sync would upload for one user,
/// served through the SAME payload builder the sync runs, so the JF-954
/// static NLU tools can reason about the EFFECTIVE vocabulary without
/// re-implementing the plugin's phonetic synonyms, seed merges, truncation
/// or value cap in a second language. The response is pure slot values and
/// synonyms: no tokens, no API keys, no skill ids, no catalog ids, no user
/// names, no item ids. NO SMAPI call, no catalog version, no registration in
/// the single-fetch payload cache CatalogController serves, no configuration
/// write: the handler never touches CatalogManager at all. Authorization is
/// the same admin policy every plugin management endpoint carries
/// (RequiresElevation); an unauthenticated or non-admin request never reaches
/// the handler body.
/// </summary>
[ApiController]
[Route("Plugins/AlexaSkill/Diagnostics")]
public class CatalogSnapshotController : ControllerBase
{
    /// <summary>The producer name the snapshot format carries for this export
    /// (the JF-954.1 snapshot format's producer field; the name-level Python
    /// producer answers "jellyfin-names").</summary>
    public const string SnapshotProducerName = "plugin";

    private readonly LibrarySyncService _syncService;
    private readonly IUserManager _userManager;
    private readonly ILogger<CatalogSnapshotController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogSnapshotController"/> class.
    /// </summary>
    /// <param name="syncService">The catalog sync service; the export rides its shared payload builder and fetches.</param>
    /// <param name="userManager">The Jellyfin user manager, the same resolver the scheduled sync task uses.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public CatalogSnapshotController(
        LibrarySyncService syncService,
        IUserManager userManager,
        ILoggerFactory loggerFactory)
    {
        _syncService = syncService;
        _userManager = userManager;
        _logger = loggerFactory.CreateLogger<CatalogSnapshotController>();
    }

    /// <summary>
    /// Export the catalog snapshot for one configured plugin user. Synchronous
    /// by design: the build is the sync's own library-read plus computation
    /// (no SMAPI round trip anywhere), so there is no async work to await and
    /// no sync-over-async; the request's cancellation token (bound by ASP.NET
    /// to RequestAborted) is honored at the roster, per-type and per-locale
    /// boundaries inside the build, which is what bounds a disconnected
    /// client's work.
    /// </summary>
    /// <param name="userId">The configured plugin user's id (required).</param>
    /// <param name="locale">Optional single locale to restrict the export to; a locale the sync would not process contributes nothing.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>The snapshot JSON in the JF-954.1 format with producer "plugin".</returns>
    [HttpGet("CatalogSnapshot")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetCatalogSnapshot([FromQuery] Guid userId, [FromQuery] string? locale, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            return BadRequest(new { error = "userId is required." });
        }

        PluginConfiguration config = Plugin.Instance!.Configuration;
        Entities.User? user = config.GetUserById(userId);
        if (user == null)
        {
            return NotFound(new { error = $"No configured plugin user with id {userId}." });
        }

        Jellyfin.Database.Implementations.Entities.User? jellyfinUser = _userManager.GetUserById(userId);
        if (jellyfinUser == null)
        {
            return NotFound(new { error = $"Jellyfin user {userId} not found." });
        }

        CatalogSnapshotExport export;
        try
        {
            export = _syncService.BuildCatalogSnapshot(jellyfinUser, user, locale, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Catalog snapshot export for user {UserId} was cancelled by the client", userId);
            throw;
        }

        return Content(SerializeSnapshot(export), "application/json");
    }

    /// <summary>
    /// Serialize the export with the project's existing catalog JSON options
    /// (CatalogManager.JsonOptions: camelCase properties, nulls dropped), so
    /// the response bytes do not depend on the host MVC serializer's
    /// configuration. The naming policy lower-cases the anonymous properties
    /// while dictionary keys (locales, slot type names) pass through
    /// verbatim, so the document carries exactly schema, producer, locales,
    /// the type names, and value and synonyms per entry and nothing else;
    /// every synonym list is non-null by construction
    /// (BuildCatalogSnapshot coalesces), so WhenWritingNull drops nothing.
    /// </summary>
    /// <param name="export">The export to serialize.</param>
    /// <returns>The snapshot JSON document.</returns>
    internal static string SerializeSnapshot(CatalogSnapshotExport export) =>
        JsonSerializer.Serialize(
            new { schema = 1, producer = SnapshotProducerName, locales = export.Locales },
            CatalogManager.JsonOptions);
}
