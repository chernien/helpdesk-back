using HelpDesk.API.Services;
using Xunit;

namespace HelpDesk.Tests;

/// <summary>
/// Tests du cœur de l'anti-doublon e-mail ↔ ticket (normalisation + rapprochement).
/// Logique pure, sans base de données.
/// </summary>
public class LienEmailTicketTests
{
    // ─── NettoyerId : Message-ID / fil ────────────────────────────────────────
    [Theory]
    [InlineData("<ABC@mail>", "abc@mail")]
    [InlineData("  <ABC@mail>  ", "abc@mail")]
    [InlineData("ABC@MAIL", "abc@mail")]
    [InlineData("<Abc@Mail>", "abc@mail")]
    public void NettoyerId_normalise_chevrons_espaces_casse(string entree, string attendu)
        => Assert.Equal(attendu, LienEmailTicket.NettoyerId(entree));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NettoyerId_vide_renvoie_null(string? entree)
        => Assert.Null(LienEmailTicket.NettoyerId(entree));

    // ─── NormaliserObjet : préfixes RE:/TR:/FW:, espaces, casse ───────────────
    [Theory]
    [InlineData("RE: Problème facture", "problème facture")]
    [InlineData("TR: Problème facture", "problème facture")]
    [InlineData("Fwd: Problème facture", "problème facture")]
    [InlineData("Ré : Problème facture", "problème facture")]
    [InlineData("RE: TR: Problème facture", "problème facture")]          // préfixes empilés
    [InlineData("  Problème   facture  ", "problème facture")]            // espaces compactés
    [InlineData("PROBLÈME FACTURE", "problème facture")]                   // casse
    public void NormaliserObjet_retire_prefixes_et_compacte(string entree, string attendu)
        => Assert.Equal(attendu, LienEmailTicket.NormaliserObjet(entree));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormaliserObjet_vide_renvoie_null(string? entree)
        => Assert.Null(LienEmailTicket.NormaliserObjet(entree));

    // ─── Signature : le fil retombe sur le Message-ID si absent ───────────────
    [Fact]
    public void Signature_sans_fil_utilise_le_messageId()
    {
        var s = LienEmailTicket.Signature("<M1@x>", null, "A@x", "Sujet", DateTime.Now);
        Assert.Equal("m1@x", s.MessageId);
        Assert.Equal("m1@x", s.ThreadId);        // repli sur le message lui-même
        Assert.Equal("a@x", s.Expediteur);
        Assert.Equal("sujet", s.Objet);
    }

    [Fact]
    public void EstExploitable_vrai_si_messageId_ou_heuristique_complete()
    {
        Assert.True(LienEmailTicket.Signature("<M1@x>", null, null, null, null).EstExploitable);
        Assert.True(LienEmailTicket.Signature(null, null, "a@x", "sujet", DateTime.Now).EstExploitable);
        Assert.False(LienEmailTicket.Signature(null, null, "a@x", null, DateTime.Now).EstExploitable); // objet manquant
        Assert.False(LienEmailTicket.Signature(null, null, null, null, null).EstExploitable);
    }

    // ─── Correspond : cas de rapprochement ────────────────────────────────────
    [Fact]
    public void Correspond_messageId_exact() // même e-mail reçu par plusieurs destinataires
    {
        var e = LienEmailTicket.Signature("<M1@x>", "<T1@x>", "a@x", "Sujet", DateTime.Now);
        Assert.True(LienEmailTicket.Correspond(e,
            tMessageId: "m1@x", tThreadId: "autre", tExpediteur: "autre@x", tObjet: "autre", tDate: null));
    }

    [Fact]
    public void Correspond_meme_fil_messageId_different() // réponse / transfert dans le même fil
    {
        var e = LienEmailTicket.Signature("<M2@x>", "<T1@x>", "a@x", "RE: Sujet", DateTime.Now);
        Assert.True(LienEmailTicket.Correspond(e,
            tMessageId: "m1@x", tThreadId: "t1@x", tExpediteur: null, tObjet: null, tDate: null));
    }

    [Fact]
    public void Correspond_heuristique_copies_separees_dans_la_fenetre() // envoi séparé, Message-ID différent
    {
        var date = new DateTime(2026, 10, 6, 10, 0, 0);
        var e = LienEmailTicket.Signature("<COPIE-A@x>", null, "Support@X", " Sujet  commun ", date);
        // Ticket créé depuis une autre copie (Message-ID différent) mais même objet+expéditeur, 2 min d'écart.
        Assert.True(LienEmailTicket.Correspond(e,
            tMessageId: "copie-b@x", tThreadId: "copie-b@x",
            tExpediteur: "support@x", tObjet: "sujet commun", tDate: date.AddMinutes(2)));
    }

    [Fact]
    public void Correspond_heuristique_hors_fenetre_echoue()
    {
        var date = new DateTime(2026, 10, 6, 10, 0, 0);
        var e = LienEmailTicket.Signature(null, null, "support@x", "sujet commun", date);
        Assert.False(LienEmailTicket.Correspond(e,
            tMessageId: null, tThreadId: null,
            tExpediteur: "support@x", tObjet: "sujet commun", tDate: date.AddMinutes(30))); // 30 min > fenêtre (15)
    }

    [Fact]
    public void Correspond_objet_trop_court_pas_heuristique() // évite les faux positifs sur objets triviaux
    {
        var date = DateTime.Now;
        var e = LienEmailTicket.Signature(null, null, "a@x", "ok", date); // objet < 4 car.
        Assert.False(LienEmailTicket.Correspond(e,
            tMessageId: null, tThreadId: null, tExpediteur: "a@x", tObjet: "ok", tDate: date));
    }

    [Fact]
    public void Correspond_ancien_ticket_sans_date_match_sur_objet_et_expediteur() // cas 3 : rétro-rempli
    {
        var e = LienEmailTicket.Signature("<NEW@x>", null, "client@societe.tn", "Problème de facture", DateTime.Now);
        // Ticket ancien : objet + expéditeur rétro-remplis, mais EmailDate inconnue (null).
        Assert.True(LienEmailTicket.Correspond(e,
            tMessageId: null, tThreadId: null,
            tExpediteur: "client@societe.tn", tObjet: "problème de facture", tDate: null));
    }

    [Fact]
    public void Correspond_sans_messageId_via_heuristique() // e-mail sans Message-ID
    {
        var date = DateTime.Now;
        var e = LienEmailTicket.Signature(null, null, "a@x", "Sujet", date);
        Assert.True(LienEmailTicket.Correspond(e,
            tMessageId: null, tThreadId: null, tExpediteur: "a@x", tObjet: "sujet", tDate: date));
    }

    [Fact]
    public void Correspond_aucune_cle_commune_echoue()
    {
        var e = LienEmailTicket.Signature("<M1@x>", "<T1@x>", "a@x", "Sujet A", new DateTime(2026, 1, 1));
        Assert.False(LienEmailTicket.Correspond(e,
            tMessageId: "m2@x", tThreadId: "t2@x", tExpediteur: "b@x", tObjet: "sujet b", tDate: new DateTime(2026, 2, 2)));
    }

    [Fact]
    public void Correspond_objet_egal_mais_expediteur_different_echoue() // évite les faux positifs
    {
        var date = DateTime.Now;
        var e = LienEmailTicket.Signature(null, null, "a@x", "Sujet", date);
        Assert.False(LienEmailTicket.Correspond(e,
            tMessageId: null, tThreadId: null, tExpediteur: "b@x", tObjet: "sujet", tDate: date));
    }
}
