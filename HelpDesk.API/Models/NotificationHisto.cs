namespace HelpDesk.API.Models;

/// <summary>Evenement de l'historique des notifications (table HELPDESK_NOTIFICATION).</summary>
public partial class NotificationHisto
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    /// <summary>CREATION | ETAT | ASSIGNATION | COMMENTAIRE | MISE_A_JOUR</summary>
    public string TypeEvent { get; set; } = null!;
    public string? Etat { get; set; }
    public int? ClientId { get; set; }
    public string? ClientNom { get; set; }
    public string? ClientEmail { get; set; }
    public int? CollaborateurId { get; set; }
    /// <summary>Nom du responsable au moment de l'evenement.</summary>
    public string? CollaborateurNom { get; set; }
    public int? ParId { get; set; }
    public string? ParNom { get; set; }
    public string? Produit { get; set; }
    /// <summary>Commentaire destine au client, joint a l'action.</summary>
    public string? Commentaire { get; set; }
    /// <summary>Capture jointe a l'action (nom d'origine, pour l'affichage).</summary>
    public string? FichierNom { get; set; }
    /// <summary>Nom du fichier sur le disque du serveur : jamais expose au client.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? FichierStockage { get; set; }
    public string? FichierType { get; set; }
    public DateTime DateEvent { get; set; }
}
