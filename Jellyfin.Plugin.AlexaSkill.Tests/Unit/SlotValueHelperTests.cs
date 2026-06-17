using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for <see cref="SlotValueHelper.Truncate"/> — the 140-char Alexa slot-value cap
/// (fixes InvalidResponse crashes on long artist fields, e.g. musical cast lists).
/// </summary>
public class SlotValueHelperTests
{
    [Fact]
    public void ShortValue_IsUnchanged()
    {
        Assert.Equal("Smashing Pumpkins", SlotValueHelper.Truncate("Smashing Pumpkins"));
    }

    [Fact]
    public void ValueAtExactLimit_IsUnchanged()
    {
        string value = new string('x', SlotValueHelper.MaxSlotValueLength); // exactly 140
        Assert.Equal(value, SlotValueHelper.Truncate(value));
    }

    [Fact]
    public void ValueWithNoSpace_HardCutsAtLimit()
    {
        string value = new string('x', 200);
        string result = SlotValueHelper.Truncate(value);
        Assert.Equal(SlotValueHelper.MaxSlotValueLength, result.Length);
        Assert.Equal(new string('x', SlotValueHelper.MaxSlotValueLength), result);
    }

    [Fact]
    public void ValueOverLimit_CutsAtLastWordBoundaryBeforeLimit()
    {
        // 100 'a's, a space, then 100 'b's (201 chars total). Last space at index 100
        // is within the first 140 chars, so truncate cuts there (no partial word).
        string value = new string('a', 100) + " " + new string('b', 100);

        string result = SlotValueHelper.Truncate(value);

        Assert.True(result.Length <= SlotValueHelper.MaxSlotValueLength);
        Assert.Equal(new string('a', 100), result); // cut at the space -> 100 'a's
    }

    [Fact]
    public void ValueOverLimit_WithSpaceOnlyAfterLimit_HardCuts()
    {
        // 150 'a's then a space then more: the only space is at index 150, beyond the
        // 140-char window, so LastIndexOf(' ', 139) finds nothing -> hard cut at 140.
        string value = new string('a', 150) + " tail";

        string result = SlotValueHelper.Truncate(value);

        Assert.Equal(SlotValueHelper.MaxSlotValueLength, result.Length);
        Assert.Equal(new string('a', SlotValueHelper.MaxSlotValueLength), result);
    }

    [Fact]
    public void MaxSlotValueLength_IsAlexaLimit()
    {
        // Guard against accidentally changing the documented Alexa hard limit.
        Assert.Equal(140, SlotValueHelper.MaxSlotValueLength);
    }
}
