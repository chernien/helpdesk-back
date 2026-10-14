namespace HelpDesk.API.DTOs;

// ─── Un message de la discussion ───────────────────────────────────────────────
public class MessageDto
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public int AuteurId { get; set; }
    public string? AuteurNom { get; set; }
    public string? AuteurRole { get; set; }
    /// <summary>true si l'utilisateur courant est l'auteur (bulle a droite).</summary>
    public bool DeMoi { get; set; }
    /// <summary>true si l'auteur est un client (sinon : equipe).</summary>
    public bool DuClient { get; set; }
    public string Contenu { get; set; } = "";
    public DateTime DateEnvoi { get; set; }
    // Pièce jointe (null si aucune).
    public string? FichierNom { get; set; }
    public string? FichierType { get; set; }
    public long? FichierTaille { get; set; }
}

public class EnvoyerMessageRequest
{
    public string Contenu { get; set; } = "";
}

// ─── Aperçu Messenger : une conversation non lue ───────────────────────────────
public class ConversationApercuDto
{
    public int TicketId { get; set; }
    public string? Intitule { get; set; }
    public string? DernierMessage { get; set; }
    public string? DernierAuteur { get; set; }
    public DateTime DerniereDate { get; set; }
    public int NonLus { get; set; }
}

public class MessagesApercuDto
{
    /// <summary>Total des messages non lus (badge Messenger).</summary>
    public int Total { get; set; }
    public List<ConversationApercuDto> Conversations { get; set; } = new();
}
