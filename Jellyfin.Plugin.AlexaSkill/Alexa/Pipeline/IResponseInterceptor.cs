using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;

/// <summary>
/// Interceptor that runs after the handler executes.
/// Use for saving state, response enrichment, logging, etc.
/// </summary>
public interface IResponseInterceptor
{
    /// <summary>
    /// Process the response after the handler executes.
    /// </summary>
    /// <param name="context">The request context containing the generated response.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task ProcessAsync(RequestContext context, CancellationToken cancellationToken);
}
