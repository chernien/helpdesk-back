using HelpDesk.API.Services;
using Xunit;

namespace HelpDesk.Tests;

/// <summary>Le produit d'un ticket créé par l'agent est toujours l'un des produits de la base.</summary>
public class ProduitCatalogueTests
{
    private static readonly string[] Catalogue =
        { "BI", "Bitrix", "Ciel", "Divalto", "Helpdesk", "Microsoft", "SAGE", "Technique", "Wavesoft", "Web" };

    [Theory]
    [InlineData("Divalto", "Divalto")]
    [InlineData("divalto", "Divalto")]                 // casse
    [InlineData("Divalto Infinity", "Divalto")]        // nom plus long que celui de la base
    [InlineData("Sage 100 Comptabilité", "SAGE")]
    [InlineData("Microsoft Dynamics", "Microsoft")]
    [InlineData("Power BI", "BI")]
    public void Correspond_a_un_produit_de_la_base(string texte, string attendu)
        => Assert.Equal(attendu, TicketService.ProduitDuCatalogue(texte, Catalogue));

    private static readonly string[] Modules =
        { "Achat", "Administration", "Affaire", "CRM", "Comptabilité", "ERP", "Gestion", "Paie", "Règlement", "Vent" };

    [Theory]
    [InlineData("Règlement", "Règlement")]
    [InlineData("reglement", "Règlement")]          // sans accent
    [InlineData("Comptabilite", "Comptabilité")]
    [InlineData("module Paie", "Paie")]
    public void Module_de_la_base_sans_tenir_compte_des_accents(string texte, string attendu)
        => Assert.Equal(attendu, TicketService.ProduitDuCatalogue(texte, Modules));

    [Fact]
    public void Module_hors_de_la_base_refuse()
    {
        Assert.Null(TicketService.ProduitDuCatalogue("SAGE", Modules));        // un produit n'est pas un module
        Assert.Null(TicketService.ProduitDuCatalogue("Stock", Modules));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Odoo")]                 // produit absent de la base
    [InlineData("Bibliothèque")]         // « BI » n'est pas un mot entier ici
    [InlineData("Webinaire")]            // « Web » non plus
    public void Aucun_produit_hors_de_la_base(string? texte)
        => Assert.Null(TicketService.ProduitDuCatalogue(texte, Catalogue));
}
