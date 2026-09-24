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
}
