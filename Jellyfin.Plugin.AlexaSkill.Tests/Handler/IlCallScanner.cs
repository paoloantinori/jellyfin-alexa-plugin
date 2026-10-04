using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-582 (the JF-579 /simplify T1): the ONE raw-IL call scanner shared by the
/// roster tests (WarmingGateCoverageTests, SessionQueueReaderRosterTests,
/// AudioPlayerPlayConstructionRosterTests), which had each carried a private
/// copy of the walking logic. It walks a method body's IL bytes and reports the
/// metadata token of every call (0x28) / callvirt (0x6F) instruction, and since
/// JF-631 also every newobj (0x73) construction.
/// The operand walk is OPCODE-AWARE since JF-736 (it decodes instruction
/// boundaries via the runtime's own <see cref="OpCodes"/> table), so only real
/// instructions of the requested opcode yield; a decode that lost a real site
/// would fail a roster equality loudly, never hide one. Uses only MethodBase.GetMethodBody IL bytes and
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
    /// Internal since JF-741: the tracker's no-inline-parse structural pin needs
    /// the same closure for its whole-type scan, so a re-inline written as a
    /// lambda or capturing local function cannot escape the nested display
    /// class the compiler puts it on.
    /// </summary>
    /// <param name="root">The type whose nested closure to enumerate.</param>
    /// <returns>The root type and every transitively nested type.</returns>
    internal static IEnumerable<Type> NestedTypeClosure(Type root)
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
    /// counterpart of <see cref="CallTokens"/> over the same opcode-aware walk
    /// (<see cref="InstructionOperands"/>): a decode that lost a real
    /// construction site fails a roster equality loudly; it can never silently
    /// hide one.
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
    /// The (offset, operand) pairs of every instruction whose SINGLE-BYTE opcode
    /// is one of the given opcodes and whose operand is a 4-byte int32 (call,
    /// callvirt, newobj, ldc.i4), in IL order (empty for abstract/extern methods
    /// with no IL body). JF-736 graduation: the JF-726 pin carried this walk as a
    /// private near-verbatim copy of <see cref="OperandTokens"/> plus the one
    /// capability the scanner lacked, the OFFSET.
    /// OPCODE-AWARE WINDOW (the JF-736 phantom-window fix): the walk decodes
    /// instruction boundaries (opcode length and operand size from the runtime's
    /// own <see cref="OpCodes"/> table, the switch opcode's variable operand
    /// included) instead of checking the opcode byte at EVERY byte offset. The
    /// old every-offset window could surface a PHANTOM candidate (a token byte
    /// sequence inside another instruction's operand bytes). Roster EQUALITY
    /// consumers were safe by direction: a phantom ADD failed the equality
    /// loudly. Every BOOL/Contains consumer could pass GREEN on one, though:
    /// this scanner's own ContainsCallToToken/CallsGetter/ConstructsType family
    /// AND the JF-726 ldc pin's exact-value Contains alike. The decoded walk
    /// closes that residual for all of them: only a real instruction of the
    /// requested opcode yields. Real instructions cannot be hidden by this
    /// either: a malformed or unknown opcode THROWS (never a silent truncation,
    /// JF-736 code-review F1), and a decode that lost a real site fails the
    /// roster equality loudly, the same loud-only philosophy as before.
    /// </summary>
    /// <param name="method">The method whose IL to walk.</param>
    /// <param name="opcodes">The single-byte opcodes to report; an opcode that does not carry a 4-byte int32 operand simply never yields (the loud-only discipline: the downstream roster equality or Contains fails loudly).</param>
    /// <returns>The (IL byte offset, int32 operand) pairs, in IL order.</returns>
    internal static IEnumerable<(int Offset, int Operand)> InstructionOperands(MethodBase method, params byte[] opcodes)
    {
        MethodBody? body = method.GetMethodBody();
        if (body == null)
        {
            yield break;
        }

        byte[] il = body.GetILAsByteArray() ?? Array.Empty<byte>();
        foreach ((int offset, short opcode, int operandStart, int operandBytes) in Instructions(il))
        {
            // Single-byte opcodes key their own byte (0..0xFF, positive); the
            // 0xFE00 | second-byte form is a negative short, so >= 0 is exactly
            // the single-byte family.
            if (opcode >= 0 && operandBytes == 4 && opcodes.Contains((byte)opcode))
            {
                yield return (offset, BitConverter.ToInt32(il, operandStart));
            }
        }
    }

    /// <summary>
    /// The ONE instruction-boundary decode (JF-736 code-review F5: the walk's
    /// advance arithmetic is directly consumable, so a test can pin that it
    /// consumes every real IL body exactly). Yields one entry per instruction,
    /// in order, from offset 0 to the stream end; the switch opcode's variable
    /// operand is measured per instance. MALFORMED INPUT IS LOUD: a truncated
    /// two-byte opcode, an opcode missing from the runtime's own
    /// <see cref="OpCodes"/> table, a truncated switch count, a switch claiming
    /// more target bytes than remain, or ANY operand overrunning the stream
    /// end THROWS instead of silently truncating the candidate list (JF-736
    /// code-review F1/F3 and the final-pass overrun hole); a whole stream from
    /// GetMethodBody never contains any of them.
    /// </summary>
    /// <param name="il">The raw IL bytes of a method body.</param>
    /// <returns>The (offset, opcode value, operand start, operand byte count) per instruction; two-byte opcodes report 0xFE00 | second byte.</returns>
    internal static IEnumerable<(int Offset, short Opcode, int OperandStart, int OperandBytes)> Instructions(byte[] il)
    {
        int offset = 0;
        while (offset < il.Length)
        {
            short opcodeValue = il[offset];
            int opcodeLength = 1;
            if (opcodeValue == 0xFE)
            {
                if (offset + 1 >= il.Length)
                {
                    throw new InvalidOperationException(
                        $"truncated two-byte opcode at IL offset {offset}; a whole GetMethodBody stream never ends inside one");
                }

                opcodeValue = (short)(0xFE00 | il[offset + 1]);
                opcodeLength = 2;
            }

            if (!OperandByteSizes.TryGetValue(opcodeValue, out int operandBytes))
            {
                throw new InvalidOperationException(
                    $"unknown opcode 0x{(ushort)opcodeValue:X4} at IL offset {offset}: the runtime's own OpCodes table has no entry, so its operand size cannot be decoded (valid GetMethodBody IL never contains one)");
            }

            int operandStart = offset + opcodeLength;
            if (operandBytes < 0)
            {
                // The switch operand is per-instance: 4 * (count + 1) bytes,
                // measured in long so a malformed huge count cannot overflow
                // into a negative advance (which would walk backwards).
                long bytes = 4L * (BitConverter.ToUInt32(il, operandStart) + 1L);
                if (operandStart + bytes > il.Length)
                {
                    throw new InvalidOperationException(
                        $"switch at IL offset {offset} claims {bytes} operand bytes but only {il.Length - operandStart} remain; a whole GetMethodBody stream never ends inside an operand");
                }

                operandBytes = (int)bytes;
            }
            else if (operandStart + operandBytes > il.Length)
            {
                throw new InvalidOperationException(
                    $"truncated operand at IL offset {offset}: {operandBytes} operand bytes claimed, {il.Length - operandStart} remain; a whole GetMethodBody stream never ends inside an operand");
            }

            yield return (offset, opcodeValue, operandStart, operandBytes);
            offset = operandStart + operandBytes;
        }
    }

    /// <summary>
    /// The int32 operands of the ldc.i4 (0x20) instructions in the method body
    /// (JF-736 hoist: the JF-726 pin's private copy), the form an enum-constant
    /// argument compiles to when it exceeds the short encodings. TOTALITY for
    /// the pin's fact (the argument the deleted private helper's doc carried,
    /// restored by the JF-736 final review): scanning only the 0x20 encoding
    /// is complete for any NON-NEGATIVE constant carrying the JF-726
    /// TaskContinuationOptions bits (0x50000 and up), because those exceed the
    /// short encodings (ldc.i4.s tops out at 127) and the compiler must emit
    /// the full form; sign-extended negatives (ldc.i4.m1 and friends) CAN carry
    /// such bits short-encoded, but the pinned combined value is positive
    /// (0xD0000), so a Contains over these operands is total for it.
    /// A thin delegate to <see cref="OperandTokens"/> (gate-marker GM-F4): the
    /// ONE token projection, not a re-implementation beside it.
    /// </summary>
    /// <param name="method">The method whose IL to walk.</param>
    /// <returns>The ldc.i4 operands, in IL order.</returns>
    internal static IEnumerable<int> LdcI4Operands(MethodBase method)
        => OperandTokens(method, 0x20);

    /// <summary>
    /// The operand byte size per opcode value (single-byte opcodes keyed by
    /// their byte, two-byte opcodes as 0xFE00 | second byte), derived from the
    /// runtime's own <see cref="OpCodes"/> table so no hand-maintained ECMA
    /// table can drift; the switch opcode is keyed to -1 and its variable
    /// operand is measured inline in <see cref="Instructions"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<short, int> OperandByteSizes =
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => field.GetValue(null))
            .OfType<OpCode>()
            .ToDictionary(op => op.Value, op => OperandByteSizeOf(op));

    /// <summary>
    /// The operand byte size of one <see cref="OpCode"/> (the runtime exposes
    /// the operand TYPE, not a byte count); InlineSwitch is -1 because its
    /// operand size is per-instance and measured inline in
    /// <see cref="Instructions"/> (the ONE encoding of that fact, JF-736
    /// code-review F4).
    /// </summary>
    private static int OperandByteSizeOf(OpCode op) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget => 1,
        OperandType.ShortInlineI => 1,
        OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 => 8,
        OperandType.InlineR => 8,
        OperandType.ShortInlineR => 4,
        OperandType.InlineSwitch => -1,
        _ => 4
    };

    /// <summary>
    /// The ONE token walk under <see cref="CallTokens"/> and
    /// <see cref="NewobjTokens"/>, expressed over the graduated
    /// <see cref="InstructionOperands"/> (JF-736: the window discipline the
    /// rosters share now lives in one definition).
    /// </summary>
    private static IEnumerable<int> OperandTokens(MethodBase method, params byte[] opcodes)
        => InstructionOperands(method, opcodes).Select(instruction => instruction.Operand);

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
        => CallsNamedMethod(method, module, getter.Name, getter.DeclaringType!);

    /// <summary>
    /// The name+declaringType generalization <see cref="CallsGetter"/> always was
    /// (JF-699 hoist: the DeliveredLaunchOutputSpeechRosterTests needed the SETTER
    /// twin for ResponseBody.OutputSpeech, the third hand copy of the technique).
    /// Candidate tokens are resolved through the module; a failed resolution can
    /// only SKIP a candidate (loud-only failure).
    /// </summary>
    /// <param name="method">The method whose IL to walk.</param>
    /// <param name="module">The module the IL tokens resolve against.</param>
    /// <param name="methodName">The callee name to look for (e.g. a property accessor name).</param>
    /// <param name="declaringType">The type that must declare the callee.</param>
    /// <returns>True when the method calls a method with that name on that type.</returns>
    internal static bool CallsNamedMethod(MethodBase method, Module module, string methodName, Type declaringType)
    {
        foreach (int token in CallTokens(method))
        {
            if (TryResolveMethod(module, token) is not { } callee)
            {
                continue;
            }

            if (callee.Name == methodName
                && callee.DeclaringType == declaringType)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The methods the given method calls that are declared under the SAME
    /// top-level type (the helper-extraction shape, one level: a private helper
    /// pulled out of the same handler). JF-699 hoist: the private copy lived in
    /// AudioPlayerPlayConstructionRosterTests and was duplicated verbatim by the
    /// DeliveredLaunchOutputSpeechRosterTests twin.
    /// </summary>
    /// <param name="method">The method whose direct callees to classify.</param>
    /// <param name="module">The module the IL tokens resolve against.</param>
    /// <returns>The same-top-level-type callees (the method itself excluded).</returns>
    internal static IEnumerable<MethodBase> SameTypeHelpers(MethodBase method, Module module)
    {
        Type owner = TopLevelType(method.DeclaringType!);
        foreach (int token in CallTokens(method))
        {
            if (TryResolveMethod(module, token) is { } callee
                && callee.DeclaringType != null
                && TopLevelType(callee.DeclaringType) == owner
                && callee != method)
            {
                yield return callee;
            }
        }
    }

    /// <summary>
    /// True when the method calls any target token directly, or calls (directly)
    /// a method declared under the SAME top-level type that calls it: the
    /// helper-extraction shape, so pulling a write/gate out into a private helper
    /// of the same handler cannot detach a call site from its guard. MethodSpec
    /// (generic) tokens are skipped because they need type context to resolve; a
    /// skipped candidate can only fail this check loudly, never pass it silently.
    /// JF-699 hoist from AudioPlayerPlayConstructionRosterTests (the second
    /// consumer arrived with DeliveredLaunchOutputSpeechRosterTests).
    /// </summary>
    /// <param name="method">The method to check.</param>
    /// <param name="module">The module the IL tokens resolve against.</param>
    /// <param name="targetTokens">The same-assembly methoddef tokens of the acceptable call targets.</param>
    /// <returns>True when the method (or a same-type helper) calls any target.</returns>
    internal static bool CallsDirectlyOrViaSameTypeHelper(MethodBase method, Module module, IReadOnlyCollection<int> targetTokens)
    {
        if (ContainsCallToAnyToken(method, targetTokens))
        {
            return true;
        }

        foreach (MethodBase helper in SameTypeHelpers(method, module))
        {
            if (ContainsCallToAnyToken(helper, targetTokens))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The snapshot form of <see cref="CallsDirectlyOrViaSameTypeHelper"/> (JF-732
    /// hoist, the JF-582/JF-634/JF-699 precedent at the second consumer: both
    /// delivered-launch roster twins snapshot a method's call tokens and
    /// same-type helpers ONCE and derive several verdicts from that snapshot, so
    /// the direct-or-helper token predicate lives here instead of inline in each
    /// scan). Same semantics: any direct token hit, else any helper hit.
    /// </summary>
    /// <param name="callTokens">The method's pre-materialized call/callvirt tokens.</param>
    /// <param name="sameTypeHelpers">The method's pre-resolved same-top-level-type helpers (deduped by the caller).</param>
    /// <param name="targetTokens">The same-assembly methoddef tokens of the acceptable call targets.</param>
    /// <returns>True when the method (or a same-type helper) calls any target.</returns>
    internal static bool CallsAnyDirectlyOrViaHelpers(int[] callTokens, IReadOnlyList<MethodBase> sameTypeHelpers, IReadOnlyCollection<int> targetTokens)
        => callTokens.Any(targetTokens.Contains)
        || sameTypeHelpers.Any(h => ContainsCallToAnyToken(h, targetTokens));

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

    /// <summary>
    /// The source-level method name an IL method belongs to: compiler-generated
    /// shapes map back to their owner (async state machine type
    /// &lt;Owner&gt;d__N.MoveNext, lambda &lt;Owner&gt;b__12_0, local function
    /// &lt;Owner&gt;g__Name|N); plain methods keep their own name. The AudioPlayerPlay
    /// ConstructionRosterTests private original, hoisted for the JF-699
    /// DeliveredLaunchOutputSpeechRosterTests twin (the JF-634 dedup direction).
    /// </summary>
    /// <param name="method">The (possibly compiler-generated) method.</param>
    /// <returns>The logical source-level method name.</returns>
    internal static string LogicalMethodName(MethodBase method)
    {
        Type declared = method.DeclaringType!;
        string? owner = null;

        if (method.Name == "MoveNext" && declared.IsNested)
        {
            owner = ExtractCompilerGeneratedOwner(declared.Name);
        }

        owner ??= ExtractCompilerGeneratedOwner(method.Name);

        return owner ?? method.Name;
    }

    /// <summary>
    /// The owner name inside a compiler-generated method/type name, or null when
    /// the name is not compiler-generated. Double-nested compiler names (an async
    /// LAMBDA's state machine is <<Owner>b__12_0>d and an async LOCAL FUNCTION's
    /// is <<LocalFn>g__Make|0_1>d) need one bracket stripped before extraction.
    /// </summary>
    /// <param name="name">The candidate compiler-generated name.</param>
    /// <returns>The extracted owner, or null.</returns>
    internal static string? ExtractCompilerGeneratedOwner(string name)
    {
        if (name.StartsWith("<<", StringComparison.Ordinal))
        {
            name = name.Substring(1);
        }

        if (name.Length == 0 || name[0] != '<')
        {
            return null;
        }

        int close = name.IndexOf('>');
        return close > 1 ? name.Substring(1, close - 1) : null;
    }
}
