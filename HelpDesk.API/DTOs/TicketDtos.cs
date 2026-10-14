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
    /// <summary>Client multi-sociétés : nom de la société pour laquelle il crée le ticket (sinon sa société principale).</summary>
    public string? Societe { get; set; }
    /// <summary>Création depuis la boîte mail : signature de l'e-mail d'origine (lien partagé anti-doublon).</summary>
    public string? EmailMessageId { get; set; }
    public string? EmailThreadId { get; set; }
    public string? EmailExpediteur { get; set; }
    public string? EmailObjet { get; set; }
    public DateTime? EmailDate { get; set; }
    public DateTime? DateEstimee { get; set; }
}

/// <summary>Pré-vérification d'un doublon avant création manuelle d'un ticket.</summary>
public class VerifierDoublonRequest
{
    public string? ClientTiers { get; set; }
    public string? ContactEmail { get; set; }
    /// <summary>Client multi-sociétés : société consultée (sinon la société principale du client).</summary>
    public string? Societe { get; set; }
    public string Intitule { get; set; } = "";
    public string? Produit { get; set; }
    public string? Description { get; set; }
}

/// <summary>Fusion de doublons : le ticket courant (source) est fusionné dans la cible.</summary>
public class FusionTicketRequest
{
    public int CibleId { get; set; }
}

public class DoublonTicketDto
{
    public int Id { get; set; }
    public string? Intitule { get; set; }
    public string? Produit { get; set; }
    public string? Etat { get; set; }
    public DateTime? Date { get; set; }
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

/// <summary>Membre de l'équipe (admin, collaborateur, développeur) : suggestions du filtre « traités par ».</summary>
public class MembreEquipeDto
{
    public int Id { get; set; }
    /// <summary>Prénom + nom (ou identifiant s'ils sont vides).</summary>
    public string Nom { get; set; } = "";
    public string? Username { get; set; }
    public string? Role { get; set; }
}

/// <summary>Membre de l'équipe d'un client administrateur de sa société (contact Divalto de la société).</summary>
public class EquipierDto
{
    public string Email { get; set; } = "";
    public string Nom { get; set; } = "";
}

/// <summary>Export des tickets cochés (Ids) ou, à défaut, de tous ceux qui correspondent aux filtres de la liste.</summary>
public class ExportTicketsRequest
{
    /// <summary>« pdf » (rapport avec logo et synthèse, par défaut) ou « excel » (tableau de données seul).</summary>
    public string? Format { get; set; }
    public List<int>? Ids { get; set; }
    public string? Etat { get; set; }
    public string? Produit { get; set; }
    public string? Search { get; set; }
    /// <summary>Admin : nom et/ou prénom d'un membre de l'équipe (tickets traités par lui).</summary>
    public string? Membre { get; set; }
    public DateOnly? Depuis { get; set; }
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
