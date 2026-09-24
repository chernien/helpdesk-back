using HelpDesk.API.Models;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.API.Data;

/// <summary>
/// Extension du contexte scaffolde : table HELPDESK_NOTIFICATION
/// (creee par nous, hors scaffolding).
/// </summary>
public partial class HelpDeskContext
{
    public virtual DbSet<NotificationHisto> NotificationHisto { get; set; }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NotificationHisto>(entity =>
        {
            entity.ToTable("HELPDESK_NOTIFICATION");
            entity.Property(e => e.TypeEvent).HasMaxLength(30);
            entity.Property(e => e.Etat).HasMaxLength(50);
            entity.Property(e => e.ClientNom).HasMaxLength(200);
            entity.Property(e => e.ClientEmail).HasMaxLength(255).IsUnicode(false);
            entity.Property(e => e.ParNom).HasMaxLength(200);
            entity.Property(e => e.Produit).HasMaxLength(80);
            entity.Property(e => e.Commentaire).HasMaxLength(1000);
            entity.Property(e => e.CollaborateurNom).HasMaxLength(200);
            entity.Property(e => e.FichierNom).HasMaxLength(255);
            entity.Property(e => e.FichierStockage).HasMaxLength(255);
            entity.Property(e => e.FichierType).HasMaxLength(100);
            entity.Property(e => e.DateEvent).HasColumnType("datetime");
        });
    }
}
