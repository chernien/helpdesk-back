using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.API.Models;

/// <summary>
/// Repere de lecture INDIVIDUEL d'un ticket (table HELPDESK_MESSAGE_LU) :
/// une ligne par (utilisateur, ticket). Lire n'avance que son propre repere ;
/// le « lu pour toute l'equipe » a la reponse est deduit des messages.
/// </summary>
[Table("HELPDESK_MESSAGE_LU")]
[PrimaryKey(nameof(UserId), nameof(TicketId))]
public partial class MessageLu
{
    public int UserId { get; set; }
    public int TicketId { get; set; }
    [Column(TypeName = "datetime")] public DateTime DerniereLectureLe { get; set; }

    /// <summary>
    /// Jusqu'où cet utilisateur a déjà été notifié par e-mail (résumé quotidien de la discussion).
    /// Évite de renvoyer deux fois les mêmes messages. Null = jamais notifié.
    /// </summary>
    [Column(TypeName = "datetime")] public DateTime? DerniereNotifLe { get; set; }
}
