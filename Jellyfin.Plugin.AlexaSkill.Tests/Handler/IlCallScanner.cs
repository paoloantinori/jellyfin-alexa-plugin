using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-582 (the JF-579 /simplify T1): the ONE raw-IL call scanner shared by the
/// roster tests (WarmingGateCoverageTests, SessionQueueReaderRosterTests), which
/// had each carried a private copy of the walking logic. It walks a method body's
/// IL bytes and reports the metadata token of every call (0x28) / callvirt
/// (0x6F) instruction, and since JF-631 also every newobj (0x73) construction.
/// The operand window is checked at every byte offset, so a
/// coincidental token match inside another instruction's operand could only ADD
/// a type to a discovered set, which fails a roster equality loudly; it can never
/// silently hide a real caller. Uses only MethodBase.GetMethodBody IL bytes and
/// MetadataToken resolution, so no IL disassembler dependency is needed.
/// </summary>
internal static class IlCallScanner
{
    /// <summary>
    /// The methods and constructors a type declares itself (its callable surface).
    /// Lambdas and local functions compiled to nested types are reached by
    /// enumerating those nested types and calling this per type.
    /// </summary>
    /// <param name="type">The type whose declared methods and constructors to enumerate.</param>
    /// <returns>The declared methods and constructors, including private ones.</returns>
    internal static IEnumerable<MethodBase> DeclaredCallableMethods(Type type)
    {
        const BindingFlags allDeclared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        return type.GetMethods(allDeclared | BindingFlags.DeclaredOnly).Cast<MethodBase>()
            .Concat(type.GetConstructors(allDeclared | BindingFlags.DeclaredOnly));
    }

    /// <summary>
    /// The metadata tokens of every call/callvirt instruction in the method body
    /// (empty for abstract/extern methods with no IL body).
    /// </summary>
    /// <param name="method">The method whose IL to walk.</param>
    /// <returns>The call/callvirt operand tokens, in IL order.</returns>
    internal static IEnumerable<int> CallTokens(MethodBase method)
    {
        MethodBody? body = method.GetMethodBody();
        if (body == null)
        {
            yield break;
        }

        byte[] il = body.GetILAsByteArray() ?? Array.Empty<byte>();
        for (int i = 0; i + 5 <= il.Length; i++)
        {
            if (il[i] == 0x28 || il[i] == 0x6F)
            {
                yield return BitConverter.ToInt32(il, i + 1);
            }
        }
    }

    /// <summary>
    /// True when any call/callvirt token equals the given same-assembly methoddef
    /// token (compared verbatim, no resolution needed).
    /// </summary>
    /// <param name="method">The method whose IL to walk.</param>
    /// <param name="targetToken">The metadata token of the call target.</param>
    /// <returns>True when the method calls the token's method.</returns>
    internal static bool ContainsCallToToken(MethodBase method, int targetToken)
        => CallTokens(method).Contains(targetToken);

    /// <summary>
    /// True when any call/callvirt token is one of the given same-assembly
    /// methoddef tokens (compared verbatim, no resolution needed).
    /// </summary>
    /// <param name="method">The method whose IL to walk.</param>
    /// <param name="targetTokens">The metadata tokens of every acceptable call target.</param>
    /// <returns>True when the method calls any token's method.</returns>
    internal static bool ContainsCallToAnyToken(MethodBase method, IReadOnlyCollection<int> targetTokens)
        => CallTokens(method).Any(targetTokens.Contains);

    /// <summary>
    /// The metadata tokens of every newobj (0x73) instruction in the method body
    /// (empty for abstract/extern methods with no IL body), the construction
    /// counterpart of <see cref="CallTokens"/> with the same operand-window
    /// discipline: a coincidental token match inside another instruction's
    /// operand can only ADD a construction site, which fails a roster equality
    /// loudly; it can never silently hide a real site.
    /// </summary>
    /// <param name="method">The method whose IL to walk.</param>
    /// <returns>The newobj operand tokens, in IL order.</returns>
    internal static IEnumerable<int> NewobjTokens(MethodBase method)
    {
        MethodBody? body = method.GetMethodBody();
        if (body == null)
        {
            yield break;
        }

        byte[] il = body.GetILAsByteArray() ?? Array.Empty<byte>();
        for (int i = 0; i + 5 <= il.Length; i++)
        {
            if (il[i] == 0x73)
            {
                yield return BitConverter.ToInt32(il, i + 1);
            }
        }
    }

    /// <summary>
    /// True when any newobj token names a constructor of the given type. The
    /// constructed type is usually plugin-EXTERNAL (JF-631: Alexa.NET's
    /// AudioPlayerPlayDirective), so each candidate token is resolved through the
    /// module like <see cref="CallsGetter"/>: only MethodDef (0x06) and MemberRef
    /// (0x0A) tokens can name it, and a failed resolution can only SKIP a
    /// candidate, which fails the roster equality loudly. Object-initializer
    /// syntax needs no special case: the compiler emits it as a newobj on the
    /// (parameterless) constructor followed by property setcalls, so the newobj
    /// is still detected here.
    /// </summary>
    /// <param name="method">The method whose IL to walk.</param>
    /// <param name="module">The module the IL tokens resolve against.</param>
    /// <param name="constructedType">The type whose construction to look for.</param>
    /// <returns>True when the method constructs the type.</returns>
    internal static bool ConstructsType(MethodBase method, Module module, Type constructedType)
    {
        foreach (int token in NewobjTokens(method))
        {
            int table = unchecked((int)((uint)token >> 24));
            if (table != 0x06 && table != 0x0A)
            {
                continue;
            }

            MemberInfo? resolved;
            try
            {
                resolved = module.ResolveMethod(token, null, null);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (resolved is MethodBase constructor && constructor.DeclaringType == constructedType)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when any call/callvirt token names the getter of the given property.
    /// The getter is usually a memberref into another assembly, so each candidate
    /// token is resolved through the module. Only MethodDef (0x06) and MemberRef
    /// (0x0A) tokens can name it (resolving anything else, e.g. a MethodSpec,
    /// throws), and a failed resolution can only SKIP a candidate, which fails
    /// the roster equality loudly the moment that candidate is the only reader of
    /// a new consumer.
    /// </summary>
    /// <param name="method">The method whose IL to walk.</param>
    /// <param name="module">The module the IL tokens resolve against.</param>
    /// <param name="getter">The property getter to look for.</param>
    /// <returns>True when the method reads the property.</returns>
    internal static bool CallsGetter(MethodBase method, Module module, MethodInfo getter)
    {
        foreach (int token in CallTokens(method))
        {
            int table = unchecked((int)((uint)token >> 24));
            if (table != 0x06 && table != 0x0A)
            {
                continue;
            }

            MemberInfo? resolved;
            try
            {
                resolved = module.ResolveMethod(token, null, null);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (resolved is MethodInfo m
                && m.Name == getter.Name
                && m.DeclaringType == getter.DeclaringType)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A nested type's top-level declaring type (async state machines and
    /// closures are attributed to the type that owns them).
    /// </summary>
    /// <param name="type">The (possibly nested) type.</param>
    /// <returns>The outermost declaring type.</returns>
    internal static Type TopLevelType(Type type)
    {
        Type current = type;
        while (current.IsNested && current.DeclaringType != null)
        {
            current = current.DeclaringType;
        }

        return current;
    }
}
