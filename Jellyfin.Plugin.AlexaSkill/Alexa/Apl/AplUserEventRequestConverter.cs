using System;
using System.Globalization;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Apl;

/// <summary>
/// Custom request type converter for Alexa.Presentation.APL.UserEvent.
/// Required because Alexa.NET v1.22.0 doesn't include this request type.
/// </summary>
public class AplUserEventRequestConverter : IDataDrivenRequestTypeConverter
{
    private const string AplUserEventType = "Alexa.Presentation.APL.UserEvent";

    public bool CanConvert(string requestType) =>
        string.Equals(requestType, AplUserEventType, StringComparison.Ordinal);

    public Request Convert(string requestType) => new AplUserEventRequest();

    public Request Convert(JObject data)
    {
        var request = new AplUserEventRequest
        {
            RequestId = data["requestId"]?.ToString(),
            Locale = data["locale"]?.ToString(),
            Token = data["token"]?.ToString(),
            Arguments = data["arguments"] as JArray,
            Source = data["source"] as JObject,
            Components = data["components"] as JObject
        };

        var timestamp = data["timestamp"]?.ToString();
        if (timestamp != null && DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
        {
            request.Timestamp = ts;
        }

        return request;
    }
}
