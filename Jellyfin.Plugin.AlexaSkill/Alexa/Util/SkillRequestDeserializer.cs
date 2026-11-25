using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

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
/// SessionEndedRequest.Reason carried the IDENTICAL property-level StringEnumConverter
/// kill mechanism (JF-957): an unknown reason wire value died at deserialization with
/// the same signature, so the session-ended request never reached its handler and the
/// teardown side effects were skipped. Its SEAM differs and is owned by
/// <see cref="ReasonTolerantConverter"/>'s doc (a contract resolver property-converter
/// replacement, not a settings-level converter); the tolerance pattern is the same
/// (<see cref="Reason.Error"/> bucket, raw wire string preserved on an
/// <see cref="UnknownWireTypeReason"/>, surfaced through <see cref="FindToleratedReason"/>
/// and <see cref="UnknownWireTypeReason.DescribeReason"/>).
/// </summary>
public static class SkillRequestDeserializer
{
    private static readonly JsonSerializerSettings TolerantSettings = new JsonSerializerSettings
    {
        ContractResolver = new ReasonTolerantContractResolver(),
        Converters = { new ErrorTypeTolerantConverter() }
    };

    /// <summary>
    /// The tolerated unknown reasons keyed on the request instance whose reason was
    /// tolerated (JF-957); the drain that keys them lives in
    /// <see cref="TolerantReasonValueProvider.SetValue"/>. Entries die with the request
    /// instance; there is no cleanup. Internal for the same-file value provider
    /// wrapper, which is the only writer.
    /// </summary>
    internal static readonly ConditionalWeakTable<SessionEndedRequest, UnknownWireTypeReason> ToleratedReasons = new();

    /// <summary>
    /// Deserialize a raw Alexa request body tolerating unknown Error.type and
    /// SessionEndedRequest.Reason wire values.
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

    /// <summary>
    /// Find a tolerated unknown SessionEndedRequest.Reason value on a deserialized
    /// request (JF-957), so the controller can log the raw wire value at warning level
    /// for triage. The lookup is keyed on the request instance, so it is stable across
    /// calls and concurrent requests (unlike the deserialization-order-sensitive reads
    /// a thread-static stash alone would allow).
    /// </summary>
    /// <param name="request">The deserialized request.</param>
    /// <returns>The tolerated reason, or null when the request carries none.</returns>
    public static UnknownWireTypeReason? FindToleratedReason(SkillRequest? request)
    {
        return request?.Request is SessionEndedRequest sessionEnded
            && ToleratedReasons.TryGetValue(sessionEnded, out UnknownWireTypeReason? tolerated) ? tolerated : null;
    }

    /// <summary>
    /// The known-value path shared by both tolerant converters: Alexa.NET's own enum
    /// converter, so every value it maps (EnumMember wire names, member names in any
    /// casing, integer values) deserializes to the same member it would have produced
    /// before the tolerance fixes. Stateless for reads, so one instance serves both.
    /// </summary>
    private static readonly StringEnumConverter KnownEnumReader = new StringEnumConverter();

    /// <summary>
    /// The known-value delegation shared by both tolerant converters (JF-931/JF-957):
    /// parse the token through Alexa.NET's own StringEnumConverter, reporting failure
    /// instead of throwing so the caller applies its tolerated fallback. The subtle
    /// invariant lives here once: the delegated converter needs the reader positioned
    /// ON the value token (a fresh JTokenReader starts at None). Everything the
    /// converter maps deserializes to the member it would have produced without the
    /// tolerance, including integer passthrough.
    /// </summary>
    /// <param name="token">The wire token holding the enum value.</param>
    /// <param name="serializer">The serializing JSON serializer.</param>
    /// <param name="value">The mapped member when the token is known to the enum.</param>
    /// <typeparam name="TEnum">The Alexa.NET enum the slot maps to.</typeparam>
    /// <returns>True when the token mapped, false when Alexa.NET's enum did not know it.</returns>
    internal static bool TryReadKnownEnum<TEnum>(JToken token, JsonSerializer serializer, out TEnum value)
        where TEnum : struct, Enum
    {
        try
        {
            JsonReader knownReader = token.CreateReader();
            knownReader.Read();
            value = (TEnum)KnownEnumReader.ReadJson(knownReader, typeof(TEnum), null, serializer)!;
            return true;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    /// <summary>
    /// Render a tolerated wire token for triage: string tokens unquoted (the contract
    /// every tolerant branch keeps for grep parity), anything else as its JSON text.
    /// </summary>
    /// <param name="token">The wire token whose value Alexa.NET's enum did not know.</param>
    /// <returns>The raw wire text.</returns>
    internal static string RenderRawWireToken(JToken token)
    {
        return token.Type == JTokenType.String
            ? token.Value<string>() ?? string.Empty
            : token.ToString(Formatting.None);
    }
}

/// <summary>
/// Reads the Alexa Error payload tolerating error.type values unknown to Alexa.NET's
/// ErrorType enum (JF-931). A settings-level converter for the whole Error TYPE is the
/// reliable seam: the property-level StringEnumConverter attribute on Error.Type wins
/// over settings-level converters for the ENUM type, but the Error object itself
/// carries no member-level converter, so this converter intercepts the object and
/// handles the type value before the default contract ever sees it. Known values are
/// parsed through <see cref="SkillRequestDeserializer.TryReadKnownEnum"/> (byte-identical
/// to the previous behavior); unknown values fall back to
/// <see cref="ErrorType.InternalError"/> with the raw wire string preserved on an
/// <see cref="UnknownWireTypeError"/> for triage.
/// </summary>
public sealed class ErrorTypeTolerantConverter : JsonConverter<Error>
{
    /// <summary>
    /// Gets a value indicating whether this converter writes Error payloads. It does not:
    /// writing routes to the default contract, which keeps the property-level
    /// StringEnumConverter serialization of Error.Type untouched.
    /// </summary>
    public override bool CanWrite => false;

    /// <summary>
    /// Reads the Error payload. The type value is parsed through
    /// <see cref="SkillRequestDeserializer.TryReadKnownEnum"/> and falls back to
    /// <see cref="ErrorType.InternalError"/> with the raw wire string preserved on an
    /// <see cref="UnknownWireTypeError"/> when it is unknown; every other property goes
    /// through the default contract, so the known-value path stays byte-identical to the
    /// previous deserialization.
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
        // payload text preserved for triage; a STRING payload renders unquoted, the same
        // rendering contract the object branch's type token uses (review round: the
        // JSON-serialized form kept its quotes and broke grep parity between branches).
        if (reader.TokenType != JsonToken.StartObject)
        {
            JToken rawToken = JToken.ReadFrom(reader);
            return new UnknownWireTypeError(SkillRequestDeserializer.RenderRawWireToken(rawToken)) { Type = ErrorType.InternalError };
        }

        JObject payload = JObject.Load(reader);

        // The Error.Type JsonProperty name is "type"; match it case-insensitively the way
        // the default contract would, then remove it so Populating the rest of the
        // payload cannot re-enter the throwing property-level conversion.
        JToken? typeToken = payload.Property("type", StringComparison.OrdinalIgnoreCase)?.Value;
        typeToken?.Parent?.Remove();

        ErrorType type = default;
        string? rawWireType = null;
        // An EXPLICIT "type": null rides the tolerated path too (review round): the
        // pre-fix contract failed loudly on it, and silently defaulting to a plain
        // Error with no Warn trace would contradict the fix's own principle that
        // every unrecognized error.type value surfaces for triage. A MISSING type
        // key stays a plain Error (Amazon always sends the field on Error carriers).
        if (typeToken is not null && !SkillRequestDeserializer.TryReadKnownEnum(typeToken, serializer, out type))
        {
            // Unknown to the enum: keep the request alive, bucket the type, and
            // preserve the raw wire value for triage. InternalError is the
            // least-misleading bucket (an unrecognized error of unknown origin);
            // the raw string is the truth rendered beside it.
            type = ErrorType.InternalError;
            rawWireType = SkillRequestDeserializer.RenderRawWireToken(typeToken);
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
/// Reads the SessionEndedRequest.Reason value tolerating wire values unknown to
/// Alexa.NET's Reason enum (JF-957). This class owns the seam decision, so the full
/// essay lives here once: the SEAM is a contract resolver replacement, not a
/// settings-level converter. Alexa.NET's RequestConverter news the SessionEndedRequest
/// and fills it with serializer.Populate, which consults converters neither for the
/// root object type (no converter ever sees the pre-created instance) nor against the
/// property's StringEnumConverter attribute (the attribute wins for the property's own
/// type). Replacing the property's converter after the attribute has been applied is
/// the only seam that fires (verified against Alexa.NET 1.22.0 and Newtonsoft.Json
/// 13.0.1: a settings-level converter on the enum never fires, one on the declaring
/// type never fires, the resolver-replaced property converter fires on every request).
/// Known values are parsed through
/// <see cref="SkillRequestDeserializer.TryReadKnownEnum"/> (byte-identical member
/// mapping, integers included); unknown values fall back to <see cref="Reason.Error"/>
/// with the raw wire string preserved on an <see cref="UnknownWireTypeReason"/> for
/// triage, keyed on the request instance by the value provider drain.
/// </summary>
public sealed class ReasonTolerantConverter : JsonConverter<Reason>
{
    /// <summary>
    /// The tolerated unknown reason stashed by <see cref="ReadJson"/> on the
    /// deserializing thread and drained by
    /// <see cref="TolerantReasonValueProvider.SetValue"/> onto the request instance it
    /// is setting. Newtonsoft runs the property's converter and then its value provider
    /// back-to-back for the same property read on the same thread with nothing in
    /// between, so the stash never crosses a request boundary; the value is nulled on
    /// drain and every tolerated read is followed by its own drain.
    /// </summary>
    [ThreadStatic]
    internal static UnknownWireTypeReason? PendingUnknownWireReason;

    /// <summary>
    /// Gets a value indicating whether this converter writes Reason values. It does not:
    /// writing routes to the default contract, which keeps the property-level
    /// StringEnumConverter serialization of Reason untouched.
    /// </summary>
    public override bool CanWrite => false;

    /// <summary>
    /// Reads the Reason value. Known values are parsed through
    /// <see cref="SkillRequestDeserializer.TryReadKnownEnum"/> (byte-identical to the
    /// previous deserialization); an unknown value or an explicit null falls back to
    /// <see cref="Reason.Error"/> with the raw wire string preserved on an
    /// <see cref="UnknownWireTypeReason"/> for triage. An explicit null rides the
    /// tolerated path too: the pre-fix contract failed loudly on it ("Cannot convert
    /// null value to Reason"), and silently defaulting with no Warn trace would
    /// contradict the fix's own principle that every unrecognized reason surfaces.
    /// </summary>
    /// <param name="reader">The reader positioned at the Reason value.</param>
    /// <param name="objectType">The declared type (Alexa.NET's Reason enum).</param>
    /// <param name="existingValue">The existing value, unused here.</param>
    /// <param name="hasExistingValue">Whether an existing value is present, unused here.</param>
    /// <param name="serializer">The serializing JSON serializer.</param>
    /// <returns>The Reason member; <see cref="Reason.Error"/> for a tolerated unknown.</returns>
    public override Reason ReadJson(JsonReader reader, Type objectType, Reason existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        // Defensive: every read starts from a clean stash, so a stash that survived an
        // aborted population between a past write and its drain (review hardening)
        // can never be drained onto this request by the value provider.
        PendingUnknownWireReason = null;

        JToken token = JToken.ReadFrom(reader);
        if (token.Type == JTokenType.Null)
        {
            // An explicit "reason": null rides the tolerated path (the pre-fix contract
            // failed loudly on it); the raw text comes from the shared renderer, whose
            // Null branch is the single owner of the "null" rendering.
            return Tolerate(SkillRequestDeserializer.RenderRawWireToken(token));
        }

        return SkillRequestDeserializer.TryReadKnownEnum(token, serializer, out Reason known)
            ? known
            : Tolerate(SkillRequestDeserializer.RenderRawWireToken(token));
    }

    /// <summary>
    /// Bucket a tolerated unknown reason and stash it for the value provider to key on
    /// the request instance. Error is the least-misleading bucket (an unrecognized
    /// reason of unknown origin; UserInitiated would falsely claim the user ended the
    /// session); the raw string is the truth rendered beside it.
    /// </summary>
    /// <param name="rawWireReason">The raw wire value Alexa.NET's enum did not know.</param>
    /// <returns>The fallback bucket member.</returns>
    private static Reason Tolerate(string rawWireReason)
    {
        PendingUnknownWireReason = new UnknownWireTypeReason(rawWireReason, Reason.Error);
        return Reason.Error;
    }

    /// <summary>
    /// Not called: <see cref="CanWrite"/> routes writes to the default contract.
    /// </summary>
    /// <param name="writer">The JSON writer.</param>
    /// <param name="value">The Reason value.</param>
    /// <param name="serializer">The serializing JSON serializer.</param>
    public override void WriteJson(JsonWriter writer, Reason value, JsonSerializer serializer)
    {
        throw new NotSupportedException("This converter only reads Reason values; CanWrite=false routes writes to the default contract.");
    }
}

/// <summary>
/// Contract resolver that swaps the SessionEndedRequest.Reason property's converter for
/// the tolerant one (JF-957). Every other property passes through
/// <see cref="DefaultContractResolver"/> unchanged, so the settings' whole contract
/// surface stays byte-identical to the default resolver everywhere else.
/// </summary>
internal sealed class ReasonTolerantContractResolver : DefaultContractResolver
{
    /// <summary>
    /// Create the property contract. For the Reason property of SessionEndedRequest the
    /// attribute-applied StringEnumConverter is replaced with the tolerant converter and
    /// the value provider is wrapped so the tolerated raw wire value can be keyed on the
    /// request instance being set.
    /// </summary>
    /// <param name="member">The member the property is created for.</param>
    /// <param name="memberSerialization">The member's serialization mode.</param>
    /// <returns>The property contract, tolerant on the Reason slot.</returns>
    protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
    {
        JsonProperty property = base.CreateProperty(member, memberSerialization);
        if (property.DeclaringType == typeof(SessionEndedRequest) && property.UnderlyingName == nameof(SessionEndedRequest.Reason))
        {
            property.Converter = new ReasonTolerantConverter();
            // The resolver always attaches a value provider to a settable instance
            // property like Reason; the nullable annotation just cannot prove it.
            property.ValueProvider = new TolerantReasonValueProvider(property.ValueProvider!);
        }

        return property;
    }
}

/// <summary>
/// Value provider wrapper that drains the tolerant converter's thread stash onto the
/// request instance being set (JF-957), keying it in
/// <see cref="SkillRequestDeserializer.ToleratedReasons"/> so the lookup is
/// instance-keyed and race-free across concurrent requests. Everything else delegates
/// to the default provider unchanged.
/// </summary>
internal sealed class TolerantReasonValueProvider : IValueProvider
{
    private readonly IValueProvider _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="TolerantReasonValueProvider"/> class.
    /// </summary>
    /// <param name="inner">The default value provider being wrapped.</param>
    public TolerantReasonValueProvider(IValueProvider inner)
    {
        _inner = inner;
    }

    /// <summary>
    /// Delegates to the wrapped default provider.
    /// </summary>
    /// <param name="target">The object to read the value from.</param>
    /// <returns>The property value.</returns>
    public object? GetValue(object target)
    {
        return _inner.GetValue(target);
    }

    /// <summary>
    /// Drains the converter's thread stash onto the target instance and delegates to
    /// the wrapped default provider. The stash is only written by the tolerant
    /// converter during THIS property's read, so a present stash always belongs here.
    /// The direct cast is deliberate: the resolver attaches this provider to the Reason
    /// property of SessionEndedRequest only, so a non-SessionEndedRequest target would
    /// be a resolver bug, and throwing beats silently dropping the tolerated value.
    /// The insert-only Add cannot collide: RequestConverter's JObject.Load collapses
    /// duplicate JSON keys, and each Populate news a fresh instance.
    /// </summary>
    /// <param name="target">The SessionEndedRequest being populated.</param>
    /// <param name="value">The parsed property value.</param>
    public void SetValue(object target, object? value)
    {
        UnknownWireTypeReason? tolerated = ReasonTolerantConverter.PendingUnknownWireReason;
        if (tolerated is not null)
        {
            ReasonTolerantConverter.PendingUnknownWireReason = null;
            SkillRequestDeserializer.ToleratedReasons.Add((SessionEndedRequest)target, tolerated);
        }

        _inner.SetValue(target, value);
    }
}

/// <summary>
/// A SessionEndedRequest.Reason wire value that was unknown to Alexa.NET's Reason enum
/// and was tolerated at deserialization (JF-957). The raw wire string rides this
/// carrier (keyed on the request instance, since the Reason slot is an enum and cannot
/// carry a value subclass the way the Error carrier does) so the event's own log lines
/// can render the value Amazon actually sent instead of only the fallback enum member.
/// The renderer lives on the carrier class like its error-side sibling
/// <see cref="UnknownWireTypeError.DescribeWireType"/>, while both Find surfaces stay
/// on <see cref="SkillRequestDeserializer"/>.
/// </summary>
public sealed class UnknownWireTypeReason
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UnknownWireTypeReason"/> class.
    /// </summary>
    /// <param name="rawWireReason">The raw reason wire value Alexa.NET's enum did not know.</param>
    /// <param name="reason">The fallback enum member the request was bucketed to.</param>
    public UnknownWireTypeReason(string rawWireReason, Reason reason)
    {
        RawWireReason = rawWireReason;
        Reason = reason;
    }

    /// <summary>
    /// Gets the raw reason wire value.
    /// </summary>
    public string RawWireReason { get; }

    /// <summary>
    /// Gets the fallback enum member the request was bucketed to.
    /// </summary>
    public Reason Reason { get; }

    /// <summary>
    /// Render the SessionEndedRequest.Reason value for a log line (JF-957): the raw wire
    /// value with the fallback member in parentheses for tolerated unknowns, the enum
    /// member name for known values, and UNKNOWN for an absent request. The production
    /// renderer for the handler's Reason lines, mirroring
    /// <see cref="UnknownWireTypeError.DescribeWireType"/> for the error side.
    /// </summary>
    /// <param name="sessionEnded">The deserialized session-ended request.</param>
    /// <returns>The triage-friendly reason rendering.</returns>
    public static string DescribeReason(SessionEndedRequest? sessionEnded)
    {
        if (sessionEnded is null)
        {
            return "UNKNOWN";
        }

        return SkillRequestDeserializer.ToleratedReasons.TryGetValue(sessionEnded, out UnknownWireTypeReason? tolerated)
            ? $"{tolerated.RawWireReason} (mapped to {tolerated.Reason})"
            : sessionEnded.Reason.ToString();
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
