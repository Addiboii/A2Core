using AionClientEditor.Editing;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.UObject;
using Xunit;

namespace Aion.ClientEditor.Tests;

public class PropertyValueFormatTests
{
    [Fact]
    public void Int_property_formats_and_applies()
    {
        var tag = new FPropertyTag { PropertyType = new FName("IntProperty"), Tag = new IntProperty(42) };
        Assert.Equal("42", PropertyValueFormat.FormatCell(tag));
        Assert.True(PropertyValueFormat.TryApply(tag, "7", out _));
        Assert.Equal(7, ((IntProperty)tag.Tag!).GenericValue);
    }

    [Fact]
    public void Name_property_formats_and_applies()
    {
        var tag = new FPropertyTag { PropertyType = new FName("NameProperty"), Tag = new NameProperty(new FName("Server_EU")) };
        Assert.Equal("Server_EU", PropertyValueFormat.FormatCell(tag));
        Assert.True(PropertyValueFormat.TryApply(tag, "Server_US", out _));
        Assert.Equal("Server_US", ((NameProperty)tag.Tag!).GenericValue is FName n ? n.Text : "");
    }

    [Fact]
    public void Bool_property_formats_and_applies()
    {
        var tag = new FPropertyTag { PropertyType = new FName("BoolProperty"), Tag = new BoolProperty(true) };
        Assert.Equal("true", PropertyValueFormat.FormatCell(tag));
        Assert.True(PropertyValueFormat.TryApply(tag, "false", out _));
        Assert.False((bool)((BoolProperty)tag.Tag!).GenericValue!);
    }
}
