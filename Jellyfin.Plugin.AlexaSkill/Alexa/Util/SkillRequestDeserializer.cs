using System;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The ONE production deserialization seam for raw Alexa request bodies (JF-931).
/// Alexa.NET deserializes Error.type through a property-level StringEnumConverter
/// over the ErrorType enum with no unknown-value tolerance, so a wire value absent
/// from the enum makes the whole request die at deserialization before any handler
/// runs (no PlaybackFailed log line, no error payload, and the JF-655 flag clear and
/// JF-447 ordering registration silently skipped). Verified against the pinned
/// Alexa.NET 1.22.0 binary: every ErrorType member carries an EnumMember SCREAMING_SNAKE
/// wire name StringEnumConverter honors, so the mapped vocabulary is wire names plus
/// member names, and a value absent from BOTH (Amazon keeps adding error.type values;
/// the enum has grown 4 to 33 members and still lacks whole families such as the Smart
/// Home ENDPOINT_TIMEOUT/DEVICE_NOT_CONNECTED) throws "Requested value ... was not
/// found" out of the deserialization call.
/// The tolerant Error converter below composes Alexa.NET's own StringEnumConverter
/// for known values (byte-identical to the previous behavior, so a future library
/// version that adds tolerance or wire names keeps working without changes here) and
/// falls back to <see cref="ErrorType.InternalError"/> while preserving the raw wire
/// string on an <see cref="UnknownWireTypeError"/> for triage.
/// Known boundary (not this fix): SessionEndedRequest.Reason has the same
/// property-level StringEnumConverter kill mechanism and is still untolerated; an
/// unknown reason value dies at deserialization with the same signature.
/// </summary>
public static class SkillRequestDeserializer
{
    private static readonly JsonSerializerSettings TolerantSettings = new JsonSerializerSettings
    {
        Converters = { new ErrorTypeTolerantConverter() }
    };

    /// <summary>
    /// Deserialize a raw Alexa request body tolerating unknown Error.type wire values.
    /// </summary>
    /// <param name="json">The raw request body JSON.</param>
    /// <returns>The deserialized request, or null when the body deserializes to null.</returns>
    public static SkillRequest? Deserialize(string json)
    {
        return JsonConvert.DeserializeObject<SkillRequest>(json, TolerantSettings);
    }

    /// <summary>
    /// Find a tolerated unknown Error.type value on a deserialized request, so the
    /// controller can log the raw wire value at warning level for triage.
    /// </summary>
    /// <param name="request">The deserialized request.</param>
    /// <returns>The tolerated error, or null when the request carries none.</returns>
    public static UnknownWireTypeError? FindToleratedError(SkillRequest? request)
    {
        return request?.Request switch
        {
            AudioPlayerRequest audio => audio.Error as UnknownWireTypeError,
            SessionEndedRequest sessionEnded => sessionEnded.Error as UnknownWireTypeError,
            SystemExceptionRequest systemException => systemException.Error as UnknownWireTypeError,
            _ => null
        };
    }

}

/// <summary>
/// Reads the Alexa Error payload tolerating error.type values unknown to Alexa.NET's
/// ErrorType enum (JF-931). A settings-level converter for the whole Error TYPE is the
/// reliable seam: the property-level StringEnumConverter attribute on Error.Type wins
/// over settings-level converters for the ENUM type, but the Error object itself
/// carries no member-level converter, so this converter intercepts the object and
/// handles the type value before the default contract ever sees it.
/// </summary>
public sealed class ErrorTypeTolerantConverter : JsonConverter<Error>
{
    /// <summary>
    /// The known-value path: Alexa.NET's own enum converter, so every value it maps
    /// (member names, any casing, integer values) deserializes to the same member it
    /// would have produced before JF-931, including whatever future library versions add.
    /// </summary>
    private static readonly StringEnumConverter KnownEnumReader = new StringEnumConverter();

    /// <summary>
    /// Gets a value indicating whether this converter writes Error payloads. It does not:
    /// writing routes to the default contract, which keeps the property-level
    /// StringEnumConverter serialization of Error.Type untouched.
    /// </summary>
    public override bool CanWrite => false;

    /// <summary>
    /// Reads the Error payload. The type value is parsed through
    /// <see cref="KnownEnumReader"/> and falls back to <see cref="ErrorType.InternalError"/>
    /// with the raw wire string preserved on an <see cref="UnknownWireTypeError"/> when it
    /// is unknown; every other property goes through the default contract, so the
    /// known-value path stays byte-identical to the previous deserialization.
    /// </summary>
    /// <param name="reader">The reader positioned at the Error value.</param>
    /// <param name="objectType">The declared type (Alexa.NET's Error).</param>
    /// <param name="existingValue">The existing object value, unused here.</param>
    /// <param name="hasExistingValue">Whether the existing value is null, unused here.</param>
    /// <param name="serializer">The serializing JSON serializer.</param>
    /// <returns>The Error instance; null only for a null payload, a tolerated
    /// <see cref="UnknownWireTypeError"/> for a non-object payload.</returns>
    public override Error? ReadJson(JsonReader reader, Type objectType, Error? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return null;
        }

        // A non-object error payload (never seen on the wire) must not kill the request
        // either, but it must not become a null Error either: ExceptionHandler reads
        // Error.Type unguarded, so null would trade a clean deserialization failure for a
        // NullReferenceException. Tolerate it like an unknown type value, with the raw
        // payload text preserved for triage.
        if (reader.TokenType != JsonToken.StartObject)
        {
            var rawPayloadText = JToken.ReadFrom(reader).ToString(Formatting.None);
            return new UnknownWireTypeError(rawPayloadText) { Type = ErrorType.InternalError };
        }

        JObject payload = JObject.Load(reader);

        // The Error.Type JsonProperty name is "type"; match it case-insensitively the way
        // the default contract would, then remove it so Populating the rest of the
        // payload cannot re-enter the throwing property-level conversion.
        JToken? typeToken = payload.Property("type", StringComparison.OrdinalIgnoreCase)?.Value;
        typeToken?.Parent?.Remove();

        ErrorType type = default;
        string? rawWireType = null;
        if (typeToken is not null && typeToken.Type != JTokenType.Null)
        {
            try
            {
                // The reader must be positioned ON the value token before the delegated
                // enum converter runs (a fresh JTokenReader starts at None).
                JsonReader knownReader = typeToken.CreateReader();
                knownReader.Read();
                type = (ErrorType)KnownEnumReader.ReadJson(knownReader, typeof(ErrorType), null, serializer)!;
            }
            catch (JsonException)
            {
                // Unknown to the enum: keep the request alive, bucket the type, and
                // preserve the raw wire value for triage. InternalError is the
                // least-misleading bucket (an unrecognized error of unknown origin);
                // the raw string is the truth rendered beside it. String tokens render
                // unquoted; anything else keeps its JSON text.
                type = ErrorType.InternalError;
                rawWireType = typeToken.Type == JTokenType.String
                    ? typeToken.Value<string>()
                    : typeToken.ToString(Formatting.None);
            }
        }

        Error error = rawWireType is null ? new Error() : new UnknownWireTypeError(rawWireType);
        serializer.Populate(payload.CreateReader(), error);
        error.Type = type;
        return error;
    }

    /// <summary>
    /// Not called: <see cref="CanWrite"/> routes writes to the default contract.
    /// </summary>
    /// <param name="writer">The JSON writer.</param>
    /// <param name="value">The Error value.</param>
    /// <param name="serializer">The serializing JSON serializer.</param>
    public override void WriteJson(JsonWriter writer, Error? value, JsonSerializer serializer)
    {
        throw new NotSupportedException("This converter only reads Error payloads; CanWrite=false routes writes to the default contract.");
    }
}

/// <summary>
/// An Error payload whose wire type value was unknown to Alexa.NET's ErrorType enum and
/// was tolerated at deserialization (JF-931). The raw wire string rides the instance so
/// the event's own log lines can render the value Amazon actually sent instead of only
/// the fallback enum member.
/// </summary>
public sealed class UnknownWireTypeError : Error
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UnknownWireTypeError"/> class.
    /// </summary>
    /// <param name="rawWireType">The raw error.type wire value Alexa.NET's enum did not know.</param>
    public UnknownWireTypeError(string rawWireType)
    {
        RawWireType = rawWireType;
    }

    /// <summary>
    /// Gets the raw error.type wire value. Never serialized: it exists for triage only.
    /// </summary>
    [JsonIgnore]
    public string RawWireType { get; }

    /// <summary>
    /// Render the error.type value for a log line: the raw wire value with the fallback
    /// member in parentheses for tolerated unknowns, the enum member name for known
    /// values, and UNKNOWN for an absent payload.
    /// </summary>
    /// <param name="error">The Error payload of the request.</param>
    /// <returns>The triage-friendly error.type rendering.</returns>
    public static string DescribeWireType(Error? error)
    {
        return error switch
        {
            null => "UNKNOWN",
            UnknownWireTypeError unknown => $"{unknown.RawWireType} (mapped to {unknown.Type})",
            _ => error.Type.ToString()
        };
    }
}
