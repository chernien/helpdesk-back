using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HelpDesk.API.Models;

/// <summary>Message de la discussion d'un ticket (table HELPDESK_MESSAGE, append-only).</summary>
[Table("HELPDESK_MESSAGE")]
public partial class Message
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    /// <summary>Auteur (Authentication.Id).</summary>
    public int AuteurId { get; set; }
    [MaxLength(200)] public string? AuteurNom { get; set; }
    /// <summary>Role de l'auteur a l'envoi (ROLE_CLIENT vs equipe) : distingue les deux camps.</summary>
    [MaxLength(20)] public string? AuteurRole { get; set; }
    [MaxLength(4000)] public string Contenu { get; set; } = null!;
    [Column(TypeName = "datetime")] public DateTime DateEnvoi { get; set; }

    // ─── Pièce jointe (facultative, une par message) ───────────────────────────
    /// <summary>Nom d'origine du fichier (affichage).</summary>
    [MaxLength(255)] public string? FichierNom { get; set; }
    /// <summary>Nom de stockage sur disque : jamais exposé au client.</summary>
    [MaxLength(255)]
    [System.Text.Json.Serialization.JsonIgnore]
    public string? FichierStockage { get; set; }
    [MaxLength(100)] public string? FichierType { get; set; }
    public long? FichierTaille { get; set; }
}
