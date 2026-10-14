namespace HelpDesk.API.DTOs;

/// <summary>Connexion d'une boîte mail par l'utilisateur.</summary>
public class MailboxConnectRequest
{
    /// <summary>'gmail', 'outlook' ou 'imap' (serveurs personnalisés).</summary>
    public string Provider { get; set; } = "gmail";
    public string Email { get; set; } = null!;
    /// <summary>Identifiant si différent de l'e-mail (sinon l'e-mail est utilisé).</summary>
    public string? Login { get; set; }
    /// <summary>Mot de passe (ou mot de passe d'application pour Gmail).</summary>
    public string Password { get; set; } = null!;

    // Serveurs : requis seulement pour Provider = 'imap' (sinon préréglés).
    public string? ImapHost { get; set; }
    public int? ImapPort { get; set; }
    public bool? ImapSsl { get; set; }
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public bool? SmtpStartTls { get; set; }
}

/// <summary>Code d'autorisation renvoyé par Microsoft après connexion (OAuth délégué).</summary>
public class OAuthCallbackRequest
{
    public string Code { get; set; } = null!;
    public string? State { get; set; }
}

/// <summary>État de la boîte connectée (sans aucun secret).</summary>
public class MailboxDto
{
    public bool Connectee { get; set; }
    public string? Provider { get; set; }
    public string? Email { get; set; }
    public string? ImapHost { get; set; }
    public string? SmtpHost { get; set; }
    public DateTime? DepuisLe { get; set; }
}
