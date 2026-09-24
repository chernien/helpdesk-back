namespace HelpDesk.API.DTOs;

public class TicketDto
{
    public int Id { get; set; }
    public string? Intitule { get; set; }
    public string? Description { get; set; }
    public string? Produit { get; set; }
    public string? Version { get; set; }
    public string? Module { get; set; }
    public string? TypeTicket { get; set; }
    public string Importance { get; set; } = null!;
    public string? Etat { get; set; }
    public string? Email { get; set; }
    public string? Client { get; set; }
    public int? ClientId { get; set; }
    public string? Collaborateur { get; set; }
    public int? CollaborateurId { get; set; }
    public DateTime? DateDemande { get; set; }
    public DateTime? DateEstimee { get; set; }
    public DateTime? DateEnattente { get; set; }
    public string? Reponse { get; set; }
    public string? Resolution { get; set; }
    /// <summary>Piece jointe : nom d'origine, type MIME et taille en octets (null si aucune).</summary>
    public string? FichierNom { get; set; }
    public string? FichierType { get; set; }
    public long? FichierTaille { get; set; }
}

public class CreateTicketRequest
{
    public string Intitule { get; set; } = null!;
    public string? Description { get; set; }
    public string? Produit { get; set; }
    public string? Version { get; set; }
    public string? Module { get; set; }
    public string? TypeTicket { get; set; }
    public string Importance { get; set; } = "Mineur";
    /// <summary>Admin/collaborateur : code tiers de la societe (CLI.TIERS). Ignore pour un client.</summary>
    public string? ClientTiers { get; set; }
    /// <summary>Admin/collaborateur : email du contact (T2.EMAIL). Ignore pour un client.</summary>
    public string? ContactEmail { get; set; }
    public DateTime? DateEstimee { get; set; }
}

public class UpdateTicketRequest
{
    public string? Intitule { get; set; }
    public string? Description { get; set; }
    public string? Produit { get; set; }
    public string? Version { get; set; }
    public string? Module { get; set; }
    public string? TypeTicket { get; set; }
    public string? Importance { get; set; }
    public string? Etat { get; set; }
    public int? CollaborateurId { get; set; }
    public DateTime? DateEstimee { get; set; }
    public string? Reponse { get; set; }
    public string? Resolution { get; set; }
    /// <summary>Commentaire destine au client, joint a cette action (1000 caracteres max).</summary>
    public string? Commentaire { get; set; }
    /// <summary>Admin : correction du client (CLI.TIERS) et du contact (T2.EMAIL), toujours ensemble.</summary>
    public string? ClientTiers { get; set; }
    public string? ContactEmail { get; set; }
}

public class TicketPageDto
{
    public int Total { get; set; }
    public List<TicketDto> Items { get; set; } = new();
}

public class ClientValidationRequest
{
    /// <summary>true : le client valide la solution (le ticket est clôturé) ; false : il annule le ticket.</summary>
    public bool Valider { get; set; }
    /// <summary>Remarque facultative du client, jointe à sa décision (1000 caractères max).</summary>
    public string? Commentaire { get; set; }
}
