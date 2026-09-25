using System;
using Newtonsoft.Json;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

public class ReminderPayloadProbeTests
{
    private readonly ITestOutputHelper _output;

    public ReminderPayloadProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Dump_RelativeReminder_Payload()
    {
        var reminder = SetReminderIntentHandler.BuildRelativeReminder(TimeSpan.FromSeconds(30), "promemoria", "it-IT");
        _output.WriteLine(JsonConvert.SerializeObject(reminder, Formatting.Indented));
        Assert.NotEmpty("ok");
    }
}
