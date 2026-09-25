using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-582 (the JF-579 /simplify T1): the ONE raw-IL call scanner shared by the
/// roster tests (WarmingGateCoverageTests, SessionQueueReaderRosterTests,
/// AudioPlayerPlayConstructionRosterTests), which had each carried a private
/// copy of the walking logic. It walks a method body's IL bytes and reports the
/// metadata token of every call (0x28) / callvirt (0x6F) instruction, and since
/// JF-631 also every newobj (0x73) construction.
/// The operand window is checked at every byte offset, so a
/// coincidental token match inside another instruction's operand could only ADD
/// a type to a discovered set, which fails a roster equality loudly; it can never
/// silently hide a real caller. Uses only MethodBase.GetMethodBody IL bytes and
/// MetadataToken resolution, so no IL disassembler dependency is needed.
/// Since JF-634 it also owns the walk scaffolding the roster scans iterate
/// (the flat assembly walk <see cref="DeclaredMethods"/>, the handler base-chain
/// walk <see cref="HandlerChainMethods"/>) and the open-world by-name target
/// collection <see cref="MethodTokens"/>, which had lived as four + two private
/// copies across the roster tests.
/// </summary>
internal static class IlCallScanner
{
    /// <summary>
    /// Every method/constructor binding flag without <see cref="BindingFlags.DeclaredOnly"/>;
    /// the ONE copy (JF-634: the third duplicate lived in the roster tests' by-name
    /// token collection).
    /// </summary>
    private const BindingFlags AllDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>
    /// The methods and constructors a type declares itself (its callable surface).
    /// Lambdas and local functions compiled to nested types are reached by
    /// enumerating those nested types and calling this per type.
    /// </summary>
    /// <param name="type">The type whose declared methods and constructors to enumerate.</param>
    /// <returns>The declared methods and constructors, including private ones.</returns>
    internal static IEnumerable<MethodBase> DeclaredCallableMethods(Type type)
        => type.GetMethods(AllDeclared | BindingFlags.DeclaredOnly).Cast<MethodBase>()
            .Concat(type.GetConstructors(AllDeclared | BindingFlags.DeclaredOnly));

    /// <summary>
    /// Every declared method/constructor in the assembly, as (declaring type,
    /// method) pairs in assembly/type order (JF-634: the flat assembly walk the
    /// roster scans iterate; the private copies lived in the roster tests).
    /// </summary>
    /// <param name="assembly">The assembly whose types to walk.</param>
    /// <returns>The (type, method) pairs, including nested and private types.</returns>
    internal static IEnumerable<(Type Type, MethodBase Method)> DeclaredMethods(Assembly assembly)
    {
        foreach (Type type in assembly.GetTypes())
        {
            foreach (MethodBase method in DeclaredCallableMethods(type))
            {
                yield return (type, method);
            }
        }
    }

    /// <summary>
    /// Every method a concrete handler reaches through its base chain, up to
    /// (excluding) the shared base: each chain type plus its nested-type closure
    /// (async state machines, closures, local functions), so a call placed on an
    /// intermediate base class is attributed to every concrete handler under it
    /// (the JF-465 review shape; JF-634 hoisted the walk from the warming-gate
    /// roster test).
    /// </summary>
    /// <param name="handlerType">The concrete handler whose chain to walk.</param>
    /// <param name="exclusiveBase">The shared base type at which the chain stops.</param>
    /// <returns>The chain's declared methods and constructors, including nested types'.</returns>
    internal static IEnumerable<MethodBase> HandlerChainMethods(Type handlerType, Type exclusiveBase)
    {
        for (Type? chainType = handlerType; chainType != null && chainType != exclusiveBase; chainType = chainType.BaseType)
        {
            foreach (Type type in NestedTypeClosure(chainType!))
            {
                foreach (MethodBase method in DeclaredCallableMethods(type))
                {
                    yield return method;
                }
            }
        }
    }

    /// <summary>
    /// A type and every type nested under it, transitively (lambdas and local
    /// functions compiled to nested types are part of the declaring type's body).
    /// </summary>
    /// <param name="root">The type whose nested closure to enumerate.</param>
    /// <returns>The root type and every transitively nested type.</returns>
    private static IEnumerable<Type> NestedTypeClosure(Type root)
    {
        var queue = new Queue<Type>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            Type current = queue.Dequeue();
            yield return current;
            foreach (Type nested in current.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                queue.Enqueue(nested);
            }
        }
    }

    /// <summary>
    /// The metadata tokens of every call/callvirt instruction in the method body
    /// (empty for abstract/extern methods with no IL body).
    /// </summary>
    /// <param name="method">The method whose IL to walk.</param>
    /// <returns>The call/callvirt operand tokens, in IL order.</returns>
    internal static IEnumerable<int> CallTokens(MethodBase method)
        => OperandTokens(method, 0x28, 0x6F);

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
    /// BOUNDARY (JF-631 review): discovery is NEWOBJ-ONLY - late-bound construction
    /// (Activator.CreateInstance, generic new() constraints, Expression.Compile,
    /// deserialized templates) emits no newobj and is invisible to these scans.
    /// </summary>
    /// <param name="method">The method whose IL to walk.</param>
    /// <returns>The newobj operand tokens, in IL order.</returns>
    internal static IEnumerable<int> NewobjTokens(MethodBase method)
        => OperandTokens(method, 0x73);

    /// <summary>
    /// The metadata tokens of every method the given type exposes with the given
    /// name, across every overload (JF-634: the open-world by-name target
    /// collection the roster tests build their same-assembly target sets from,
    /// so a future overload cannot silently escape a scan and leave a stale
    /// roster behind a green suite). GetMethods without DeclaredOnly, so
    /// inherited overloads are included.
    /// </summary>
    /// <param name="type">The type whose methods to enumerate by name.</param>
    /// <param name="methodName">The method name; every overload is included.</param>
    /// <returns>The matching methods' metadata tokens.</returns>
    internal static IEnumerable<int> MethodTokens(Type type, string methodName)
    {
        // SAME-MODULE tokens only (the JF-634 review's latent hazard): a same-named
        // overload inherited from a type in ANOTHER assembly carries that module's
        // token, which collides with an unrelated plugin methoddef when compared
        // against plugin IL bytes - an acceptance-set consumer would then pass a
        // site that has no write at all, the exact silent miss these rosters exist
        // to catch. All current targets declare the scanned names themselves, so
        // this filter removes nothing today and keeps every future token comparable.
        foreach (MethodInfo m in type.GetMethods(AllDeclared).Where(m =>
                 m.Name == methodName && m.DeclaringType?.Module == type.Module))
        {
            yield return m.MetadataToken;
        }
    }

    /// <summary>
    /// The ONE method-token resolver (JF-631 /simplify: the third hand copy of this
    /// block lived in the roster test): only MethodDef (0x06) and MemberRef (0x0A)
    /// tokens can name a method; a failed resolution returns null, which callers
    /// treat as SKIP-a-candidate (loud-only failure: resolution misses can never
    /// hide a real site from the roster-equality fact).
    /// </summary>
    internal static MethodBase? TryResolveMethod(Module module, int token)
    {
        int table = unchecked((int)((uint)token >> 24));
        if (table != 0x06 && table != 0x0A)
        {
            return null;
        }

        try
        {
            return module.ResolveMethod(token, null, null) as MethodBase;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The ONE byte-walk under <see cref="CallTokens"/> and <see cref="NewobjTokens"/>:
    /// operand tokens for the given opcodes, checked at every byte offset so a
    /// coincidental byte match can only ADD a candidate (the loud-only failure
    /// philosophy this scanner follows).
    /// </summary>
    private static IEnumerable<int> OperandTokens(MethodBase method, params byte[] opcodes)
    {
        MethodBody? body = method.GetMethodBody();
        if (body == null)
        {
            yield break;
        }

        byte[] il = body.GetILAsByteArray() ?? Array.Empty<byte>();
        for (int i = 0; i + 5 <= il.Length; i++)
        {
            if (opcodes.Contains(il[i]))
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
            // IsAssignableFrom, not exact equality (the review's probe-confirmed escape):
            // a DERIVED directive (SleepReplayDirective : AudioPlayerPlayDirective)
            // serializes as the base type's directive JSON while escaping an
            // exact-type scan entirely - the guard must see subclass constructions.
            if (constructedType.IsAssignableFrom(TryResolveMethod(module, token)?.DeclaringType))
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
            if (TryResolveMethod(module, token) is not MethodInfo m)
            {
                continue;
            }

            if (m.Name == getter.Name
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
