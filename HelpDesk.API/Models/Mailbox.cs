using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.API.Models;

/// <summary>Boîte mail connectée par un utilisateur (IMAP/SMTP). Mot de passe chiffré.</summary>
[Table("HELPDESK_MAILBOX")]
[Index(nameof(UserId), IsUnique = true)]
public partial class Mailbox
{
    public int Id { get; set; }
    public int UserId { get; set; }
    [MaxLength(20)] public string Provider { get; set; } = null!;
    [MaxLength(255)] public string Email { get; set; } = null!;
    [MaxLength(255)] public string Login { get; set; } = null!;
    /// <summary>Secret chiffré (Data Protection) : mot de passe IMAP ou refresh token OAuth. Jamais exposé.</summary>
    [Column(TypeName = "nvarchar(max)")]
    [System.Text.Json.Serialization.JsonIgnore]
    public string SecretChiffre { get; set; } = null!;
    [MaxLength(255)] public string ImapHost { get; set; } = null!;
    public int ImapPort { get; set; }
    public bool ImapSsl { get; set; }
    [MaxLength(255)] public string SmtpHost { get; set; } = null!;
    public int SmtpPort { get; set; }
    public bool SmtpStartTls { get; set; }
    [Column(TypeName = "datetime")] public DateTime DateCreation { get; set; }

    /// <summary>Agent IA actif : les e-mails reçus sur cette boîte sont transformés en tickets automatiquement.</summary>
    public bool AgentActif { get; set; }

    /// <summary>Les tickets créés par l'agent sont automatiquement affectés au propriétaire de la boîte.</summary>
    public bool AgentAutoAssign { get; set; }

    /// <summary>Modèle IA choisi par l'utilisateur (ex. « gemini-2.5-flash ») ; null = automatique (Gemini puis Groq).</summary>
    [MaxLength(100)] public string? AgentModele { get; set; }
}
