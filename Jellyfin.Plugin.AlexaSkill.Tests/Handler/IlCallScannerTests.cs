using System;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-736 code-review F5: the opcode-aware boundary decode the scanner's whole
/// operand-walk family rides on had no direct test. This pins the decoder
/// ITSELF against every real method body of the plugin and test assemblies:
/// the decoded instruction chain must start at offset 0, each instruction must
/// begin exactly where the previous one's operand ended, and the last one must
/// end exactly at the IL stream's end. Any wrong operand size in the derived
/// table or wrong advance arithmetic desyncs some body and reds here with the
/// body named, instead of surfacing as an opaque roster-equality failure in an
/// unrelated scan. The malformed-input paths (truncated two-byte opcode,
/// unknown opcode, truncated switch count) throw by design and are unreachable
/// from a whole GetMethodBody stream, so their absence on every real body is
/// itself part of the fact being pinned.
/// </summary>
public class IlCallScannerTests
{
    [Fact]
    public void InstructionDecode_ConsumesEveryRealBodyExactlyFromStartToEnd()
    {
        Assembly pluginAssembly = typeof(BaseHandler).Assembly;
        Assembly testAssembly = typeof(IlCallScannerTests).Assembly;
        int bodies = 0;
        int twoByteOpcodeInstructions = 0;
        int switchInstructions = 0;

        foreach (Assembly assembly in new[] { pluginAssembly, testAssembly })
        {
            foreach ((_, MethodBase method) in IlCallScanner.DeclaredMethods(assembly))
            {
                byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
                if (il == null || il.Length == 0)
                {
                    continue;
                }

                bodies++;
                int expectedStart = 0;
                int lastEnd = 0;
                foreach ((int offset, short opcode, int operandStart, int operandBytes)
                    in IlCallScanner.Instructions(il))
                {
                    Assert.True(
                        offset == expectedStart,
                        $"{method.DeclaringType!.FullName}.{method.Name}: instruction at {offset} but the previous one ended at {expectedStart} (a desync names a wrong operand size in the decode)");
                    expectedStart = operandStart + operandBytes;
                    lastEnd = expectedStart;
                    if (opcode < 0)
                    {
                        twoByteOpcodeInstructions++;
                    }

                    if (opcode == 0x45)
                    {
                        switchInstructions++;
                    }
                }

                Assert.True(
                    lastEnd == il.Length,
                    $"{method.DeclaringType!.FullName}.{method.Name}: the decoded chain ends at {lastEnd} but the IL body is {il.Length} bytes");
            }
        }

        // The decode's two variable-length branches must actually have been
        // exercised, not vacuously green: real compiler output in these two
        // assemblies contains both families in volume. COVERAGE BOUNDARY: the
        // real-body walk pins exactly the opcode families real IL uses; the
        // families and malformed shapes real bodies never contain (the 16-bit
        // local/arg forms, the throw paths) are pinned synthetically below.
        Assert.True(bodies > 1000, $"expected to walk thousands of real bodies, found {bodies}");
        Assert.True(
            twoByteOpcodeInstructions > 0,
            "no two-byte (0xFE-prefixed) instruction was decoded; the branch is untested");
        Assert.True(switchInstructions > 0, "no switch instruction was decoded; the branch is untested");
    }

    /// <summary>
    /// The decode branches real compiler output in these assemblies never
    /// contains, pinned against hand-built byte streams: the 16-bit local form
    /// (InlineVar, needs a method with more than 255 locals to occur for real),
    /// a two-byte token operand (ldftn), a switch operand, and the
    /// malformed-input THROWS (truncated two-byte opcode, unknown opcode,
    /// truncated switch count, a switch claiming more target bytes than remain,
    /// any operand overrunning the stream end) that a whole GetMethodBody
    /// stream can never produce. A wrong operand size for the 16-bit form
    /// desyncs exactly here.
    /// </summary>
    [Fact]
    public void InstructionDecode_AdvancesAndThrowsOnSyntheticStreams()
    {
        // ldc.i4 <int32> then ret: a 5-byte instruction plus a 1-byte one.
        Assert.Equal(
            new[] { (0, (short)0x20, 1, 4), (5, (short)0x2A, 6, 0) },
            IlCallScanner.Instructions(new byte[] { 0x20, 1, 0, 0, 0, 0x2A }));

        // ldloc <uint16> (0xFE 0x0C, InlineVar): a two-byte opcode with a
        // two-byte operand, 4 bytes total.
        Assert.Equal(
            new[] { (0, unchecked((short)(0xFE00 | 0x0C)), 2, 2) },
            IlCallScanner.Instructions(new byte[] { 0xFE, 0x0C, 0x01, 0x00 }));

        // ldftn <token> (0xFE 0x06): a two-byte opcode with a 4-byte operand.
        Assert.Equal(
            new[] { (0, unchecked((short)(0xFE00 | 0x06)), 2, 4) },
            IlCallScanner.Instructions(new byte[] { 0xFE, 0x06, 9, 0, 0, 0 }));

        // switch with two targets: 1 opcode byte + 4 count bytes + 2 * 4 target
        // bytes = 13 bytes total, then a ret.
        Assert.Equal(
            new[] { (0, (short)0x45, 1, 12), (13, (short)0x2A, 14, 0) },
            IlCallScanner.Instructions(new byte[] { 0x45, 2, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0x2A }));

        // Malformed streams are LOUD (JF-736 code-review F1/F3 + the final
        // pass's overrun hole): every path throws instead of silently
        // truncating the candidate list. The truncated switch COUNT is the one
        // path that throws from the BitConverter read itself (ArgumentException,
        // of which ArgumentOutOfRangeException is a subtype), pinned exactly so
        // an accidental exception type cannot pass as the designed loudness.
        Assert.Throws<InvalidOperationException>(
            () => IlCallScanner.Instructions(new byte[] { 0xFE }).ToList());
        Assert.Throws<InvalidOperationException>(
            () => IlCallScanner.Instructions(new byte[] { 0x24 }).ToList());
        Assert.Throws<ArgumentException>(
            () => IlCallScanner.Instructions(new byte[] { 0x45, 2, 0, 0 }).ToList());
        Assert.Throws<InvalidOperationException>(
            () => IlCallScanner.Instructions(new byte[] { 0x45, 9, 0, 0, 0, 0x2A }).ToList());
        Assert.Throws<InvalidOperationException>(
            () => IlCallScanner.Instructions(new byte[] { 0x20, 1, 0, 0 }).ToList());
    }

    /// <summary>
    /// Returns the constant whose four bytes are each the call (0x28) opcode:
    /// compiled to exactly `ldc.i4 0x28282828; ret`, so the body contains
    /// four 0x28 bytes that are OPERAND bytes, never instruction boundaries.
    /// </summary>
    private static int PhantomOpcodeBaitConstant() => 0x28282828;

    /// <summary>
    /// THE phantom-immunity pin (gate-marker GM-F1: the JF-736 headline
    /// deliverable had no test; on real IL the old every-offset walk and the
    /// opcode-aware walk agree, because real bodies contain no phantom
    /// candidates). A revert of InstructionOperands to a naive byte scan
    /// turns these four operand bytes into a phantom call candidate; the
    /// decoded walk must reject them. RED-PROVEN by reverting the walk to an
    /// i+5 byte scan: the empty-call assertion below fails.
    /// </summary>
    [Fact]
    public void InstructionOperands_RejectsOpcodeBytesInsideAnotherInstructionsOperand()
    {
        MethodBase bait = typeof(IlCallScannerTests)
            .GetMethod(nameof(PhantomOpcodeBaitConstant), BindingFlags.NonPublic | BindingFlags.Static)!;

        // The real ldc.i4 is still found (the decode did not over-reject).
        Assert.Equal(new[] { 0x28282828 }, IlCallScanner.LdcI4Operands(bait));

        // The four 0x28 operand bytes are NOT call candidates: a byte-scan
        // walk would surface one here (its every-offset window reads the
        // operand of the first 0x28 as a call token).
        Assert.Empty(IlCallScanner.InstructionOperands(bait, 0x28));
    }
}
