using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.DynamicEntities;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class DynamicEntitiesModelsTests
{
    [Fact]
    public void DynamicEntitiesDirective_Type_ReturnsCorrectValue()
    {
        var directive = new DynamicEntitiesDirective();
        Assert.Equal("Dialog.UpdateDynamicEntities", directive.Type);
    }

    [Fact]
    public void DynamicEntitiesDirective_DefaultUpdateBehavior_IsReplace()
    {
        var directive = new DynamicEntitiesDirective();
        Assert.Equal("REPLACE", directive.UpdateBehavior);
    }

    [Fact]
    public void DynamicEntitiesDirective_Types_DefaultsEmpty()
    {
        var directive = new DynamicEntitiesDirective();
        Assert.Empty(directive.Types);
    }

    [Fact]
    public void DynamicSlotType_Properties_SetCorrectly()
    {
        var slotType = new DynamicSlotType
        {
            Name = "AMAZON.Musician",
            Values = new List<DynamicSlotValue>
            {
                new() { Id = "artist_1", Name = new DynamicSlotValueName { Value = "Queen" } }
            }
        };

        Assert.Equal("AMAZON.Musician", slotType.Name);
        Assert.Single(slotType.Values);
        Assert.Equal("artist_1", slotType.Values[0].Id);
    }

    [Fact]
    public void DynamicSlotValueName_SynonymsNull_NotInSerializedJson()
    {
        var value = new DynamicSlotValue
        {
            Id = "test",
            Name = new DynamicSlotValueName { Value = "Test", Synonyms = null }
        };

        string json = JsonConvert.SerializeObject(value);
        // NullValueHandling.Ignore means synonyms should not appear when null
        Assert.DoesNotContain("ynonym", json);
    }

    [Fact]
    public void DynamicSlotValueName_SynonymsPresent_InSerializedJson()
    {
        var value = new DynamicSlotValue
        {
            Id = "test",
            Name = new DynamicSlotValueName
            {
                Value = "Queen",
                Synonyms = new List<string> { "kuin" }
            }
        };

        string json = JsonConvert.SerializeObject(value);
        Assert.Contains("kuin", json);
    }

    [Fact]
    public void DynamicEntitiesDirective_SerializesRoundTrip()
    {
        var directive = new DynamicEntitiesDirective
        {
            Types = new List<DynamicSlotType>
            {
                new()
                {
                    Name = "AMAZON.Musician",
                    Values = new List<DynamicSlotValue>
                    {
                        new()
                        {
                            Id = "jellyfin_artist_abc",
                            Name = new DynamicSlotValueName
                            {
                                Value = "Queen",
                                Synonyms = new List<string> { "kuin" }
                            }
                        }
                    }
                }
            }
        };

        string json = JsonConvert.SerializeObject(directive);
        var doc = JObject.Parse(json);

        // Verify the structure is preserved regardless of casing
        Assert.NotNull(doc.SelectToken("$.type") ?? doc.SelectToken("$.Type"));
        Assert.Contains("Dialog.UpdateDynamicEntities", json);
        Assert.Contains("REPLACE", json);
        Assert.Contains("AMAZON.Musician", json);
        Assert.Contains("jellyfin_artist_abc", json);
        Assert.Contains("Queen", json);
        Assert.Contains("kuin", json);
    }

    [Fact]
    public void DynamicSlotValue_Defaults_AreSet()
    {
        var value = new DynamicSlotValue();
        Assert.Equal(string.Empty, value.Id);
        Assert.NotNull(value.Name);
    }

    [Fact]
    public void DynamicSlotValueName_DefaultValue_IsEmpty()
    {
        var name = new DynamicSlotValueName();
        Assert.Equal(string.Empty, name.Value);
        Assert.Null(name.Synonyms);
    }
}
