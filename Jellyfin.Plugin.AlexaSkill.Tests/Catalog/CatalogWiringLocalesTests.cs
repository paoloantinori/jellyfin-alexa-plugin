using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Catalog;

public class CatalogWiringLocalesTests
{
    // JF-543: Amazon's ar-SA full build fails (nondeterministically, scaling with the
    // catalog's value count) whenever the model carries a catalog-backed slot type,
    // while the same payload builds in every other locale and the embedded ar-SA model
    // builds reliably. The set is the workaround's single source; these tests keep it
    // from silently growing (a locale here without fresh probe evidence is a bug), pin
    // the lookup's semantics, and cover the whole roster so locale #18 is never skipped.

    [Fact]
    public void UnsupportedSet_IsSubsetOfRoster_AndExactlyTheProbedLocale()
    {
        var roster = TestLocales.AllLocales();
        Assert.Subset(
            new HashSet<string>(roster, System.StringComparer.OrdinalIgnoreCase),
            CatalogManager.CatalogWiringUnsupportedLocales);
        Assert.Equal(
            new[] { "ar-SA" },
            CatalogManager.CatalogWiringUnsupportedLocales.OrderBy(l => l));
    }

    [Theory]
    [MemberData(nameof(TestLocales.LocaleRows), MemberType = typeof(TestLocales))]
    public void IsCatalogWiringSupported_TrueForEveryRosterLocaleExceptTheProbedOne(string locale)
    {
        bool expected = !CatalogManager.CatalogWiringUnsupportedLocales.Contains(locale);
        Assert.Equal(expected, CatalogManager.IsCatalogWiringSupported(locale));
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("ar-sa")] // case-insensitive: config locale casing is not contractual
    public void IsCatalogWiringSupported_FalseForArabic(string locale)
    {
        Assert.False(CatalogManager.IsCatalogWiringSupported(locale));
    }
}
