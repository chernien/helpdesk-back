using HelpDesk.API.Services;
using Xunit;

namespace HelpDesk.Tests;

/// <summary>Captures d'écran collées dans le corps d'un e-mail : l'agent IA doit les voir.</summary>
public class ImagesDuCorpsTests
{
    [Fact]
    public void Extrait_les_images_collees_dans_le_corps()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var html = $"""<div dir="ltr"><img src="data:image/png;base64,{Convert.ToBase64String(png)}" width="400"></div>""";

        var image = Assert.Single(AgentEmailService.ImagesDuCorps(html));
        Assert.Equal("image/png", image.Type);
        Assert.Equal(png, image.Data);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("<p>Bonjour</p>")]
    [InlineData("""<img src="https://exemple.com/logo.png">""")]   // image distante : pas de contenu à lire
    [InlineData("""<img src="data:image/png;base64,@@@@">""")]    // contenu illisible
    public void Aucune_image_exploitable(string? html)
        => Assert.Empty(AgentEmailService.ImagesDuCorps(html));
}
