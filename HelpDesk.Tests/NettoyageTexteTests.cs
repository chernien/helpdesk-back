using HelpDesk.API.Services;
using Xunit;

namespace HelpDesk.Tests;

/// <summary>Description d'un ticket créé depuis un e-mail : ni « ? ? ? », ni [Image], ni lignes de séparation.</summary>
public class NettoyageTexteTests
{
    [Fact]
    public void Retire_le_remplissage_invisible_des_e_mails_publicitaires()
        => Assert.Equal("Here’s where you stand.",
            NettoyageTexte.Nettoyer("Here’s where you stand. ͏‌ ͏‌ ͏‌ ͏‌"));

    [Fact]
    public void Retire_les_points_d_interrogation_deja_enregistres_en_base()
        => Assert.Equal("Here’s where you stand.",
            NettoyageTexte.Nettoyer("Here’s where you stand. ? ? ? ? ? ? ? ?? ? ? ? ? ? ? ??"));

    [Fact]
    public void Retire_les_emplacements_d_images_et_les_separateurs()
        => Assert.Equal("Réunion Microsoft Teams\nRejoindre : https://teams.microsoft.com/meet/1",
            NettoyageTexte.Nettoyer("[Image][cid:image001.png@01DB]\n______________________\nRéunion Microsoft Teams\nRejoindre : https://teams.microsoft.com/meet/1"));

    [Fact]
    public void Garde_le_texte_normal_et_les_vraies_questions()
        => Assert.Equal("Bonjour,\n\nPouvez-vous vérifier la TVA ? Merci ?!",
            NettoyageTexte.Nettoyer("Bonjour,\r\n\r\n\r\n\r\nPouvez-vous  vérifier la TVA ?   Merci ?!"));
}
