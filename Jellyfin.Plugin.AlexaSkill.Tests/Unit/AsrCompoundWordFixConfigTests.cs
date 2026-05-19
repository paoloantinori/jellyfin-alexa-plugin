using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class AsrCompoundWordFixConfigTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AsrCompoundWordFixEnabled_RoundTripsThroughXmlSerialization(bool value)
    {
        var config = new PluginConfiguration { AsrCompoundWordFixEnabled = value };
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var ms = new MemoryStream();
        serializer.Serialize(ms, config);
        ms.Position = 0;
        var deserialized = (PluginConfiguration)serializer.Deserialize(ms)!;

        Assert.Equal(value, deserialized.AsrCompoundWordFixEnabled);
    }
}
