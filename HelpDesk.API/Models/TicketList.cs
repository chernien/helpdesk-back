using System;
using System.Collections.Generic;

namespace HelpDesk.API.Models;

public partial class TicketList
{
    public string? Email { get; set; }

    public string? Produit { get; set; }

    public string? Version { get; set; }

    public string? Module { get; set; }

    public DateOnly? DateDemande { get; set; }

    public DateOnly? DateEstimee { get; set; }

    public string? Intitule { get; set; }

    public string? TypeTicket { get; set; }

    public string Importance { get; set; } = null!;

    public string? Reponse { get; set; }

    public string? Reponse1 { get; set; }

    public string? Reponse2 { get; set; }

    public string? FichierJoint { get; set; }

    public string? FichierJoint1 { get; set; }

    public string? FichierJoint2 { get; set; }

    public string? Description { get; set; }

    public string? Collaborateur { get; set; }

    public int Id { get; set; }

    public string? Etat { get; set; }

    public string? Ce1 { get; set; }

    public string? Ce2 { get; set; }

    public string? Heured { get; set; }

    public string? Heuref { get; set; }

    public string? CodeEvt { get; set; }

    public int? ClientId { get; set; }

    public int? CollaborateurId { get; set; }

    public DateTime? DateDemande2 { get; set; }

    public DateTime? DateEstimee2 { get; set; }

    public DateTime? Heured2 { get; set; }

    public DateTime? Heuref2 { get; set; }

    public string? Glpi { get; set; }

    public string? Client { get; set; }

    public string? Resolution { get; set; }

    public DateTime? DateEnattente { get; set; }

    /// <summary>
    /// Message-ID RFC de l'e-mail d'origine (quand le ticket est créé depuis la boîte mail).
    /// Identique pour tous les destinataires To/CC : permet à toute l'équipe de voir que
    /// cet e-mail est déjà transformé en ticket et d'éviter les doublons.
    /// </summary>
    public string? EmailMessageId { get; set; }

    /// <summary>Identifiant du fil (References/In-Reply-To racine) : dédoublonne réponses et transferts.</summary>
    public string? EmailThreadId { get; set; }

    /// <summary>Expéditeur de l'e-mail d'origine (rapprochement heuristique si Message-ID différent).</summary>
    public string? EmailExpediteur { get; set; }

    /// <summary>Objet normalisé de l'e-mail d'origine (rapprochement heuristique).</summary>
    public string? EmailObjet { get; set; }

    /// <summary>Date d'envoi de l'e-mail d'origine (identique pour toutes les copies d'un même envoi).</summary>
    public DateTime? EmailDate { get; set; }
}
