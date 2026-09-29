using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-358 structural guard (JF-667 review finding 1): the ArtistIds+MediaTypes
/// combination must never ride one InternalItemsQuery. The original JF-358 fix
/// left per-site comments and per-site pins, and three sites still escaped - the
/// per-site approach is proven insufficient. This test scans every plugin source
/// file's query INITIALIZER blocks for the combination, making the common
/// hand-built shape fail at suite time instead of being re-found by a manual
/// sweep. Post-construction property assignments are covered structurally at the
/// one shared sink (SearchService.SearchItemsFuzzyAsync's if/else-if, pinned by
/// SearchServiceTests); a future property-assignment site outside that sink is
/// out of this guard's reach and owns its own comment. Known blind spot: the
/// regex requires the literal "new InternalItemsQuery" and cannot see
/// target-typed "new()" initializers (SearchMediaIntentHandler's local
/// BuildQuery is the one such site today) - spell the type name there.
/// </summary>
public partial class ArtistQueryShapeGuardTests
{
    // GetFullPath is load-bearing: EnumerateFiles(AllDirectories) on a path that still
    // carries ".." segments yields ZERO entries on .NET Linux, making the guard
    // vacuously green (verified live 2026-09-29 while proving the red case).
    private static readonly string PluginSourceRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "Jellyfin.Plugin.AlexaSkill"));

    private static readonly string ObjSegment = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
    private static readonly string BinSegment = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";

    [GeneratedRegex(@"new\s+InternalItemsQuery\s*(?:\([^)]*\))?\s*\{", RegexOptions.Compiled)]
    private static partial Regex QueryStart();

    [GeneratedRegex(@"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [Fact]
    public void NoQueryInitializerCarriesBothArtistIdsAndMediaTypes()
    {
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(PluginSourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(ObjSegment) || file.Contains(BinSegment))
            {
                continue;
            }

            string source = File.ReadAllText(file);
            foreach (Match start in QueryStart().Matches(source))
            {
                // Walk to the initializer block's matching close brace.
                int depth = 0;
                int i = source.IndexOf('{', start.Index);
                for (; i < source.Length; i++)
                {
                    if (source[i] == '{')
                    {
                        depth++;
                    }
                    else if (source[i] == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            break;
                        }
                    }
                }

                if (i >= source.Length)
                {
                    continue; // never closed (a brace inside a string literal): not our shape
                }

                // Stripping can only delete text, so a raw slice missing either word
                // stays missing: pre-test before paying for the strip. The strip is
                // load-bearing because the fixed sites' rationale comments name the
                // very word "MediaTypes" (first live run flagged three comment-only
                // false positives).
                string raw = source[start.Index..(i + 1)];
                if (!raw.Contains("ArtistIds") || !raw.Contains("MediaTypes"))
                {
                    continue;
                }

                string code = Comments().Replace(raw, string.Empty);
                if (code.Contains("ArtistIds") && code.Contains("MediaTypes"))
                {
                    int line = 1 + source[..start.Index].Count(ch => ch == '\n');
                    offenders.Add($"{Path.GetFileName(file)}:{line}");
                }
            }
        }

        Assert.True(offenders.Count == 0, $"JF-358 violation: InternalItemsQuery initializer carries both ArtistIds and MediaTypes (MediaTypes does not constrain an ArtistIds query on Jellyfin; use IncludeItemTypes): {string.Join(", ", offenders)}");
    }
}
