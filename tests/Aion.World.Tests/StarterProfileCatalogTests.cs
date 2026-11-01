using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class StarterProfileCatalogTests
{
    static readonly string[] PlayableClasses =
    [
        "Gladiator", "Templar", "Assassin", "Cleric", "Ranger", "Elementalist", "Sorcerer", "Chanter",
    ];

    [Fact]
    public void Playable_catalog_has_every_class_race_and_gender()
    {
        var data = GameData.Embedded;
        for (var light = 1; light <= GameData.PlayableClassesPerRace; light++)
        {
            var dark = light + GameData.PlayableClassesPerRace;
            foreach (var gender in new[] { 1, 2 })
            {
                var elyos = data.Profile(light, gender);
                var asmodian = data.Profile(dark, gender);
                Assert.NotNull(elyos);
                Assert.NotNull(asmodian);
                Assert.Equal(1, elyos.Race);
                Assert.Equal(2, asmodian.Race);
                Assert.Equal(PlayableClasses[light - 1], elyos.Class);
                Assert.Equal(elyos.Class, asmodian.Class);
            }
        }
    }

    [Fact]
    public void NormalizeCreationId_maps_light_class_index_to_asmodian_PCCreationInfo()
    {
        var data = GameData.Embedded;
        Assert.Equal(9, data.NormalizeCreationId(1, 2));
        Assert.Equal(1, data.NormalizeCreationId(1, 1));
        Assert.Equal(10, data.NormalizeCreationId(2, 2));
        Assert.Equal(9, data.NormalizeCreationId(9, 1));
    }

    [Fact]
    public void Profile_with_account_race_resolves_asmodian_gladiator_from_creation_id_1()
    {
        var profile = GameData.Embedded.Profile(1, 1, accountRace: 2);
        Assert.NotNull(profile);
        Assert.Equal("Gladiator", profile.Class);
        Assert.Equal(2, profile.Race);
        Assert.Equal(7, profile.PcId);
    }
}
