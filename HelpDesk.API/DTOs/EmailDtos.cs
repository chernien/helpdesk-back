namespace HelpDesk.API.DTOs;

// ─── Résumé d'un email pour la liste ───────────────────────────────────────────
public class EmailSummaryDto
{
    public string Id { get; set; } = null!;           // ID Graph (base64url)
    public string? De { get; set; }                   // Nom expéditeur
    public string? DeEmail { get; set; }              // Email expéditeur
    public string? Objet { get; set; }                // Sujet
    public string? Apercu { get; set; }               // Aperçu du corps (150 chars)
    public DateTime? Date { get; set; }               // Date de réception
    public bool EstLu { get; set; }                   // Lu ou non
    public bool HasAttachments { get; set; }          // Pièces jointes ?
    public int? TicketId { get; set; }                // ID du ticket lié (si créé)
    public string? MessageIdInternet { get; set; }    // Message-ID RFC (identique pour tous les destinataires)
    public string? ThreadIdInternet { get; set; }     // Fil (References/In-Reply-To racine, ou conversationId)
}

// ─── Détail complet d'un email ─────────────────────────────────────────────────
public class EmailDetailDto : EmailSummaryDto
{
    public string? CorpsHtml { get; set; }            // Corps HTML complet
    public string? CorpsTexte { get; set; }           // Corps texte brut
    public List<EmailAttachmentDto> PiecesJointes { get; set; } = new();
    public List<EmailContactDto> A { get; set; } = new();   // Destinataires
    public List<EmailContactDto> Cc { get; set; } = new();  // Copie
}

// ─── Destinataire ────────────────────────────────────────────────────────────
public class EmailContactDto
{
    public string? Nom { get; set; }
    public string? Email { get; set; }
}

// ─── Pièce jointe ─────────────────────────────────────────────────────────────
public class EmailAttachmentDto
{
    /// <summary>ID Graph de la pièce jointe (pour la télécharger).</summary>
    public string? Id { get; set; }
    public string? Nom { get; set; }
    public string? TypeMime { get; set; }
    public long Taille { get; set; }
}

// ─── Page de résultats ────────────────────────────────────────────────────────
public class EmailPageDto
{
    public int Total { get; set; }
    public List<EmailSummaryDto> Items { get; set; } = new();
    public string? NextLink { get; set; }             // Token de pagination Graph
}

// ─── Dossier de la boîte (Boîte de réception, Courrier indésirable…) ─────────
public class EmailDossierDto
{
    public string Id { get; set; } = null!;
    public string Nom { get; set; } = null!;
    /// <summary>Nom Graph des dossiers connus (inbox, sentitems, drafts, junkemail, deleteditems, archive).</summary>
    public string? Cle { get; set; }
    public int NonLus { get; set; }
    public int Total { get; set; }
}

// ─── Requêtes ────────────────────────────────────────────────────────────────
public class EmailDeplacerRequest
{
    /// <summary>ID Graph du dossier, ou nom connu (inbox, junkemail, deleteditems…).</summary>
    public string DossierId { get; set; } = null!;
}

public class EmailRepondreRequest
{
    public string Message { get; set; } = null!;
    /// <summary>true : répondre à tous les participants.</summary>
    public bool Tous { get; set; }
}

public class EmailEnvoyerRequest
{
    public List<string> Destinataires { get; set; } = new();
    public List<string> Cc { get; set; } = new();
    /// <summary>Ignoré pour un transfert (Outlook reprend l'objet d'origine).</summary>
    public string? Objet { get; set; }
    public string Message { get; set; } = "";
    /// <summary>Expéditeur choisi (doit être une adresse autorisée). Vide = boîte de l'utilisateur.</summary>
    public string? From { get; set; }
}

public class EmailLierTicketRequest
{
    public int TicketId { get; set; }
}
