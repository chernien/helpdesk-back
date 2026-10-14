using HelpDesk.API.Models;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.API.Data;

/// <summary>
/// Extension du contexte : tables de la discussion (chat) par ticket,
/// HELPDESK_MESSAGE et HELPDESK_MESSAGE_LU (creees a la main, hors scaffolding).
/// Le mappage est porte par les annotations des entites ([Table]/[PrimaryKey])
/// car OnModelCreatingPartial est deja implemente (HelpDeskContext.Notifications.cs).
/// </summary>
public partial class HelpDeskContext
{
    public virtual DbSet<Message> Message { get; set; }
    public virtual DbSet<MessageLu> MessageLu { get; set; }
}
