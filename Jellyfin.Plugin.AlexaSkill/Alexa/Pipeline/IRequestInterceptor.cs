using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;

/// <summary>
/// Interceptor that runs before the handler executes.
/// Use for loading state, validation, logging, etc.
/// </summary>
public interface IRequestInterceptor
{
    /// <summary>
    /// Process the request before the handler executes.
    /// </summary>
    /// <param name="context">The request context containing all pipeline state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True to continue processing, false to short-circuit (use Response from context).</returns>
    Task<bool> ProcessAsync(RequestContext context, CancellationToken cancellationToken);
}
