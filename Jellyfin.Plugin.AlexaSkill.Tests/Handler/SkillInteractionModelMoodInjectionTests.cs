using System;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.InteractionModel;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for the JF-355 admin mood-override slot-type injection
/// (<see cref="SkillInteractionModel.InjectMoodSlotValues"/>).
/// </summary>
public class SkillInteractionModelMoodInjectionTests
{
    private static SkillInteractionModel LoadItItModel()
    {
        var models = Util.GetLocalInteractionModels();
        var it = models.First(m => m.Item1 == "it-IT");
        return new SkillInteractionModel(it.Item1, it.Item2, "mia collezione");
    }

    [Fact]
    public void Inject_AppendsNewMoodToMoodSlotType()
    {
        var model = LoadItItModel();
        var moodType = model.InteractionModel.Language.SlotTypes.First(t => t.Name == "Mood");
        int before = moodType.Values!.Length;

        model.InjectMoodSlotValues(new[] { "coding", "letargo" });

        Assert.Equal(before + 2, moodType.Values!.Length);
        var values = moodType.Values!.Select(v => v.Name.Value).ToList();
        Assert.Contains("coding", values);
        Assert.Contains("letargo", values);
    }

    [Fact]
    public void Inject_DoesNotDuplicateExistingValue()
    {
        var model = LoadItItModel();
        var moodType = model.InteractionModel.Language.SlotTypes.First(t => t.Name == "Mood");
        int before = moodType.Values!.Length;

        // "rilassante" already exists in the it-IT Mood type.
        model.InjectMoodSlotValues(new[] { "rilassante", "coding" });

        Assert.Equal(before + 1, moodType.Values!.Length);
    }

    [Fact]
    public void Inject_NoOpForEmptyWords()
    {
        var model = LoadItItModel();
        var moodType = model.InteractionModel.Language.SlotTypes.First(t => t.Name == "Mood");
        int before = moodType.Values!.Length;

        model.InjectMoodSlotValues(Array.Empty<string>());
        model.InjectMoodSlotValues(new[] { "  ", "" });

        Assert.Equal(before, moodType.Values!.Length);
    }

    [Fact]
    public void Inject_NullIsSafe()
    {
        var model = LoadItItModel();
        var ex = Record.Exception(() => model.InjectMoodSlotValues(null!));
        Assert.Null(ex);
    }
}
