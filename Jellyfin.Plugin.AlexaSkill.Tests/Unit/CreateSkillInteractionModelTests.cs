#nullable enable
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-553 bisect side-finding (2026-09-13): the custom-URL deploy and restore
/// endpoints PUT EMPTY models (intents=0) because CreateSkillInteractionModel
/// deserialized the WRAPPED envelope into SkillInteraction, which binds the
/// INNER model. Both endpoint shapes must deserialize with intents intact.
/// </summary>
public class CreateSkillInteractionModelTests
{
    private const string BareModel = """
        {"languageModel":{"invocationName":"jellyfin player","intents":[{"name":"PlayIntent","samples":["spielen"]}]}}
        """;

    private const string WrappedModel = """
        {"interactionModel":{"languageModel":{"invocationName":"jellyfin player","intents":[{"name":"PlayIntent","samples":["spielen"]}]}}}
        """;

    [Theory]
    [InlineData(BareModel)]
    [InlineData(WrappedModel)]
    public void CreateSkillInteractionModel_CarriesIntents_FromBothEnvelopeShapes(string json)
    {
        var manager = new ModelDeploymentManager(
            new StubHttpClientFactory(),
            Mock.Of<Microsoft.Extensions.Logging.ILogger<ModelDeploymentManager>>());

        var model = manager.ValidateAndCreateForTest(json, "de-DE");

        Assert.NotNull(model.InteractionModel?.Language?.IntentTypes);
        Assert.NotEmpty(model.InteractionModel!.Language!.IntentTypes!);
        Assert.Equal("PlayIntent", model.InteractionModel!.Language!.IntentTypes!.First().Name);
    }
}
