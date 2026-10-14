using HelpDesk.API.Models;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.API.Data;

/// <summary>
/// Extension du contexte : boîtes mail connectées par utilisateur (HELPDESK_MAILBOX,
/// créée à la main). Mappage par annotations ([Table]/[Index]).
/// </summary>
public partial class HelpDeskContext
{
    public virtual DbSet<Mailbox> Mailbox { get; set; }
}
