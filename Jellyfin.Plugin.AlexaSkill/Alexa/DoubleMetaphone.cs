using System;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Double Metaphone phonetic encoding algorithm (Lawrence Phillips, 2000).
/// Produces primary and alternate 4-character codes that represent the
/// English pronunciation of a word. Useful for matching names across
/// languages and ASR variants where spelling differs but pronunciation
/// is similar (e.g. "Schmidt" vs "Smith", or "Bjork" vs "Björk").
/// </summary>
internal static class DoubleMetaphone
{
    /// <summary>
    /// Maximum length of generated Metaphone codes.
    /// </summary>
    private const int MaxCodeLength = 4;

    /// <summary>
    /// Encodes a string into its Double Metaphone primary and alternate codes.
    /// Each code is up to 4 characters long. The alternate code may be null
    /// when it is identical to the primary code.
    /// </summary>
    /// <param name="input">The string to encode (typically an artist name).</param>
    /// <returns>
    /// A tuple containing the primary code and optional alternate code.
    /// Both are uppercase ASCII strings up to 4 characters.
    /// Returns ("", null) for empty or whitespace-only input.
    /// </returns>
    public static (string Primary, string? Alternate) Encode(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return (string.Empty, null);
        }

        // Normalize: uppercase, strip non-alpha, collapse whitespace
        string normalized = Normalize(input);
        if (normalized.Length == 0)
        {
            return (string.Empty, null);
        }

        if (normalized.Length == 1)
        {
            return (normalized, null);
        }

        int length = normalized.Length;
        int last = length - 1;

        var primary = new StringBuilder(MaxCodeLength);
        var alternate = new StringBuilder(MaxCodeLength);

        int current = 0;

        // Skip leading silent letters
        if (StartsWithAny(normalized, "GN", "KN", "PN", "WR", "PS"))
        {
            current += 1;
        }
        else if (normalized[0] == 'X')
        {
            // Initial X → Z
            AddCode(primary, alternate, 'S');
            current = 1;
        }

        while (current <= last && (primary.Length < MaxCodeLength || alternate.Length < MaxCodeLength))
        {
            char c = normalized[current];

            switch (c)
            {
                case 'A':
                case 'E':
                case 'I':
                case 'O':
                case 'U':
                    // All vowels are encoded the same
                    if (current == 0)
                    {
                        AddCode(primary, alternate, 'A');
                    }

                    current += 1;
                    break;

                case 'B':
                    // '-MB' is silent
                    if (current > 0 && normalized[current - 1] == 'M' && IsAt(normalized, current, last, 'B') && !HasNext(normalized, current, last))
                    {
                        // silent: skip
                        current += 1;
                        break;
                    }

                    AddCode(primary, alternate, 'P');
                    current = current < last && normalized[current + 1] == 'B' ? current + 2 : current + 1;
                    break;

                case 'C':
                    current = ProcessC(normalized, current, last, primary, alternate);
                    break;

                case 'D':
                    if (current < last && normalized[current + 1] == 'G')
                    {
                        // DG
                        if (current + 1 < last && IsFrontVowel(normalized[current + 2]))
                        {
                            // DG followed by front vowel → J
                            AddCode(primary, alternate, 'J');
                            current += 3;
                        }
                        else
                        {
                            AddCode(primary, alternate, 'T');
                            current += 2;
                        }
                    }
                    else
                    {
                        AddCode(primary, alternate, 'T');
                        current = current < last && (normalized[current + 1] == 'D' || normalized[current + 1] == 'T')
                            ? current + 2
                            : current + 1;
                    }

                    break;

                case 'F':
                    AddCode(primary, alternate, 'F');
                    current = current < last && normalized[current + 1] == 'F' ? current + 2 : current + 1;
                    break;

                case 'G':
                    current = ProcessG(normalized, current, last, primary, alternate);
                    break;

                case 'H':
                    // Only encode if before a vowel and not after a vowel
                    if (current < last && IsVowel(normalized[current + 1]))
                    {
                        if (current == 0 || IsVowel(normalized[current - 1]))
                        {
                            AddCode(primary, alternate, 'H');
                        }
                    }

                    current += 2;
                    break;

                case 'J':
                    current = ProcessJ(normalized, current, last, primary, alternate);
                    break;

                case 'K':
                    AddCode(primary, alternate, 'K');
                    current = current < last && normalized[current + 1] == 'K' ? current + 2 : current + 1;
                    break;

                case 'L':
                    if (current < last && normalized[current + 1] == 'L')
                    {
                        // Double L
                        if (current + 2 <= last && IsVowel(normalized[current + 2]))
                        {
                            AddCode(primary, 'L');
                            AddCode(alternate, 'L');
                        }
                        else
                        {
                            AddCode(primary, alternate, 'L');
                            current += 2;
                            break;
                        }

                        current += 2;
                    }
                    else
                    {
                        AddCode(primary, alternate, 'L');
                        current += 1;
                    }

                    break;

                case 'M':
                    AddCode(primary, alternate, 'M');
                    current = current < last && normalized[current + 1] == 'M' ? current + 2 : current + 1;
                    break;

                case 'N':
                    AddCode(primary, alternate, 'N');
                    current = current < last && normalized[current + 1] == 'N' ? current + 2 : current + 1;
                    break;

                case 'P':
                    if (current < last && normalized[current + 1] == 'H')
                    {
                        AddCode(primary, alternate, 'F');
                        current += 2;
                    }
                    else
                    {
                        AddCode(primary, alternate, 'P');
                        current = current < last && normalized[current + 1] == 'P' ? current + 2 : current + 1;
                    }

                    break;

                case 'Q':
                    AddCode(primary, alternate, 'K');
                    current = current < last && normalized[current + 1] == 'Q' ? current + 2 : current + 1;
                    break;

                case 'R':
                    if (current < last && normalized[current + 1] == 'R')
                    {
                        // Double R
                        if (current + 2 <= last && IsVowel(normalized[current + 2]))
                        {
                            AddCode(primary, 'R');
                            AddCode(alternate, 'R');
                        }
                        else
                        {
                            AddCode(primary, alternate, 'R');
                            current += 2;
                            break;
                        }

                        current += 2;
                    }
                    else
                    {
                        AddCode(primary, alternate, 'R');
                        current += 1;
                    }

                    break;

                case 'S':
                    current = ProcessS(normalized, current, last, primary, alternate);
                    break;

                case 'T':
                    if (current < last && normalized[current + 1] == 'H')
                    {
                        AddCode(primary, alternate, 'T'); // Theta → T
                        current += 2;
                    }
                    else if (current < last && current + 1 <= last && normalized[current + 1] == 'C' && current + 2 <= last && normalized[current + 2] == 'H')
                    {
                        // TCH → skip (silent)
                        current += 3;
                    }
                    else
                    {
                        AddCode(primary, alternate, 'T');
                        current = current < last && (normalized[current + 1] == 'T' || normalized[current + 1] == 'D')
                            ? current + 2
                            : current + 1;
                    }

                    break;

                case 'V':
                    AddCode(primary, alternate, 'F');
                    current = current < last && normalized[current + 1] == 'V' ? current + 2 : current + 1;
                    break;

                case 'W':
                    // W is only encoded when followed by a vowel
                    if (current < last && IsVowelOrY(normalized[current + 1]))
                    {
                        AddCode(primary, alternate, 'A');
                    }

                    current += 1;
                    break;

                case 'Y':
                    // Y acts as a vowel at the start of a word or when followed by a vowel
                    if (current == 0)
                    {
                        AddCode(primary, alternate, 'A');
                    }
                    else if (current < last && IsVowel(normalized[current + 1]))
                    {
                        AddCode(primary, alternate, 'A');
                    }

                    current += 1;
                    break;

                case 'X':
                    // KS
                    AddCode(primary, alternate, 'K');
                    AddCode(primary, alternate, 'S');
                    current += 1;
                    break;

                case 'Z':
                    AddCode(primary, alternate, 'S');
                    current = current < last && normalized[current + 1] == 'Z' ? current + 2 : current + 1;
                    break;

                default:
                    // Skip unknown characters
                    current += 1;
                    break;
            }
        }

        string primaryCode = Truncate(primary);
        string alternateCode = Truncate(alternate);

        return (primaryCode, alternateCode != primaryCode ? alternateCode : null);
    }

    private static int ProcessC(string s, int current, int last, StringBuilder primary, StringBuilder alternate)
    {
        // Various C cases
        if (current > 0 && current + 1 <= last && s[current + 1] == 'I' && s[current - 1] == 'S')
        {
            // -SCI- -SCE- -SCY- → silent
            return current + 2;
        }

        if (current + 1 <= last && s[current + 1] == 'H')
        {
            // CH
            if (current == 0)
            {
                if (current + 2 <= last && IsVowel(s[current + 2]))
                {
                    AddCode(primary, 'K');
                    AddCode(alternate, 'X');
                }
                else
                {
                    AddCode(primary, alternate, 'X');
                }
            }
            else
            {
                // -CHR- (Greek roots) → K
                if (current + 2 <= last && s[current + 2] == 'R')
                {
                    AddCode(primary, alternate, 'K');
                }
                else
                {
                    AddCode(primary, 'X');
                    AddCode(alternate, 'K');
                }
            }

            return current + 2;
        }

        if (current + 1 <= last && s[current + 1] == 'Z')
        {
            // CZ
            if (current == 0)
            {
                AddCode(primary, 'S');
                AddCode(alternate, 'X');
            }
            else
            {
                AddCode(primary, 'X');
                AddCode(alternate, 'S');
            }

            return current + 2;
        }

        if (current + 1 <= last && s[current + 1] == 'I' && current + 2 <= last && s[current + 2] == 'A')
        {
            // -CIA-
            AddCode(primary, alternate, 'X');
            return current + 3;
        }

        if (current + 1 <= last && IsFrontVowel(s[current + 1]))
        {
            // CI, CE, CY
            if (current == 0)
            {
                AddCode(primary, 'S');
                AddCode(alternate, 'X');
            }
            else if (current > 1 && s[current - 1] == 'M')
            {
                // -MC- (Mac/Mc) → K
                AddCode(primary, alternate, 'K');
            }
            else
            {
                AddCode(primary, 'S');
                AddCode(alternate, 'X');
            }

            return current + 2;
        }

        // Hard C → K
        AddCode(primary, alternate, 'K');
        return current < last && (s[current + 1] == 'C' || s[current + 1] == 'K' || s[current + 1] == 'Q')
            ? current + 2
            : current + 1;
    }

    private static int ProcessG(string s, int current, int last, StringBuilder primary, StringBuilder alternate)
    {
        if (current + 1 > last)
        {
            AddCode(primary, alternate, 'K');
            return current + 1;
        }

        char next = s[current + 1];

        if (next == 'H')
        {
            if (current > 0 && !IsVowel(s[current - 1]) && current + 2 <= last && IsVowel(s[current + 2]))
            {
                AddCode(primary, alternate, 'K');
                return current + 2;
            }

            if (current + 2 > last)
            {
                // GH at end → silent
                return current + 2;
            }

            if (current + 2 <= last && (s[current + 2] == 'I' || s[current + 2] == 'E' || s[current + 2] == 'Y'))
            {
                // GHI, GHE, GHY → J
                AddCode(primary, alternate, 'J');
                return current + 2;
            }

            // GH otherwise → K
            AddCode(primary, alternate, 'K');
            return current + 2;
        }

        if (next == 'N')
        {
            if (current == 0 || (current == 1 && s[0] == 'G'))
            {
                // GN at start → N
                if (current + 2 <= last && IsVowel(s[current + 2]))
                {
                    AddCode(primary, 'N');
                    AddCode(alternate, 'K');
                }
                else
                {
                    AddCode(primary, alternate, 'N');
                }
            }
            else
            {
                AddCode(primary, 'K');
                AddCode(alternate, 'J');
            }

            return current + 2;
        }

        if (next == 'E' || next == 'I' || next == 'Y')
        {
            // Soft G → J/K
            AddCode(primary, 'J');
            AddCode(alternate, 'K');
            return current + 2;
        }

        // Hard G → K
        AddCode(primary, alternate, 'K');
        return current < last && (s[current + 1] == 'G' || s[current + 1] == 'Q')
            ? current + 2
            : current + 1;
    }

    private static int ProcessJ(string s, int current, int last, StringBuilder primary, StringBuilder alternate)
    {
        // J
        if (current > 0 && current + 1 <= last && s[current + 1] == 'J')
        {
            // Double J
            AddCode(primary, alternate, 'J');
            return current + 2;
        }

        if (current == 0)
        {
            AddCode(primary, 'J');
            AddCode(alternate, 'A');
        }
        else if (IsVowel(s[current - 1]) && current + 1 <= last && IsVowel(s[current + 1]))
        {
            AddCode(primary, alternate, 'J');
        }
        else if (current == last)
        {
            AddCode(primary, alternate, 'J');
        }
        else
        {
            AddCode(primary, 'J');
            AddCode(alternate, 'H');
        }

        return current + 1;
    }

    private static int ProcessS(string s, int current, int last, StringBuilder primary, StringBuilder alternate)
    {
        // SCH → skip S, let C handler produce X/K for CH
        if (current + 2 <= last && s[current + 1] == 'C' && s[current + 2] == 'H')
        {
            // Don't encode S; advance to C which will handle CH
            return current + 1;
        }

        if (current + 1 <= last && s[current + 1] == 'H')
        {
            // SH → X
            AddCode(primary, alternate, 'X');
            return current + 2;
        }

        if (current + 2 <= last && s[current + 1] == 'I' && (s[current + 2] == 'O' || s[current + 2] == 'A'))
        {
            // SIO, SIA → X (S)
            AddCode(primary, 'S');
            AddCode(alternate, 'X');
            return current + 3;
        }

        if (current + 1 <= last && s[current + 1] == 'Z')
        {
            AddCode(primary, 'S');
            AddCode(alternate, 'X');
            return current + 2;
        }

        AddCode(primary, alternate, 'S');
        return current < last && (s[current + 1] == 'S' || s[current + 1] == 'Z')
            ? current + 2
            : current + 1;
    }

    private static void AddCode(StringBuilder primary, StringBuilder alternate, char code)
    {
        if (primary.Length < MaxCodeLength)
        {
            primary.Append(code);
        }

        if (alternate.Length < MaxCodeLength)
        {
            alternate.Append(code);
        }
    }

    private static void AddCode(StringBuilder sb, char code)
    {
        if (sb.Length < MaxCodeLength)
        {
            sb.Append(code);
        }
    }

    private static string Truncate(StringBuilder sb)
    {
        return sb.Length > MaxCodeLength ? sb.ToString(0, MaxCodeLength) : sb.ToString();
    }

    private static bool IsVowel(char c) => c is 'A' or 'E' or 'I' or 'O' or 'U';

    private static bool IsVowelOrY(char c) => c is 'A' or 'E' or 'I' or 'O' or 'U' or 'Y';

    private static bool IsFrontVowel(char c) => c is 'E' or 'I' or 'Y';

    private static bool IsAt(string s, int index, int last, char c)
    {
        return index <= last && s[index] == c;
    }

    private static bool HasNext(string s, int index, int last)
    {
        return index < last;
    }

    /// <summary>
    /// Checks if the string starts with any of the given prefixes.
    /// </summary>
    private static bool StartsWithAny(string s, params string[] prefixes) =>
        prefixes.Any(p => s.StartsWith(p, StringComparison.Ordinal));

    /// <summary>
    /// Normalize input for Metaphone processing: uppercase, strip non-alpha.
    /// Multi-word inputs (e.g. "Daft Punk") are processed as a single token with
    /// spaces removed so the phonetic code captures the full name's pronunciation.
    /// </summary>
    private static string Normalize(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            if (char.IsLetter(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }

        return sb.ToString();
    }
}
