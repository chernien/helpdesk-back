using System;
using System.Collections.Generic;
using HelpDesk.API.Models;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.API.Data;

public partial class HelpDeskContext : DbContext
{
    public HelpDeskContext(DbContextOptions<HelpDeskContext> options)
        : base(options)
    {
    }

    public virtual DbSet<AffectationAuthenticationProduit> AffectationAuthenticationProduit { get; set; }

    public virtual DbSet<Authentication> Authentication { get; set; }

    public virtual DbSet<ProduitErp> ProduitErp { get; set; }

    public virtual DbSet<TicketList> TicketList { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UseCollation("Latin1_General_BIN");

        modelBuilder.Entity<AffectationAuthenticationProduit>(entity =>
        {
            // La table n'a pas de cle primaire en base : on declare une cle composite
            // cote EF pour pouvoir inserer/supprimer des affectations.
            entity.HasKey(e => new { e.UserId, e.ProductId });

            entity.Property(e => e.ProductId).HasColumnName("ProductID");
            entity.Property(e => e.UserId).HasColumnName("UserID");

            entity.HasOne(d => d.Product).WithMany()
                .HasForeignKey(d => d.ProductId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_AffectationAuthenticationProduit_ProduitERP");

            entity.HasOne(d => d.User).WithMany(u => u.Affectations)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_AffectationAuthenticationProduit_Authentication");
        });

        modelBuilder.Entity<Authentication>(entity =>
        {
            entity.Property(e => e.Nom)
                .HasMaxLength(200)
                .IsUnicode(false);
            entity.Property(e => e.Password)
                .HasMaxLength(100)
                .IsUnicode(false);
            entity.Property(e => e.ResetToken)
                .HasMaxLength(255)
                .IsUnicode(false);
            entity.Property(e => e.ResetTokenExpiry).HasColumnType("datetime");
            entity.Property(e => e.Role)
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.Username).HasMaxLength(255);
            entity.Property(e => e.Prenom).HasMaxLength(100);
            entity.Property(e => e.ClientTiers).HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.SocieteNom).HasMaxLength(100);
            entity.Property(e => e.DateCreation).HasColumnType("datetime");
            entity.Property(e => e.NotificationsLuesLe).HasColumnType("datetime");
            entity.Ignore(e => e.NomComplet);
        });

        modelBuilder.Entity<ProduitErp>(entity =>
        {
            entity.HasKey(e => e.ProductId);

            entity.ToTable("ProduitERP");

            entity.Property(e => e.ProductId).HasColumnName("ProductID");
            entity.Property(e => e.Nom)
                .HasMaxLength(50)
                .IsUnicode(false);
        });

        modelBuilder.Entity<TicketList>(entity =>
        {
            entity.ToTable("TICKET_LIST");

            entity.Property(e => e.Ce1)
                .HasMaxLength(100)
                .IsUnicode(false)
                .HasColumnName("CE1");
            entity.Property(e => e.Ce2)
                .HasMaxLength(100)
                .IsUnicode(false)
                .HasColumnName("CE2");
            entity.Property(e => e.Client)
                .HasMaxLength(100)
                .IsFixedLength();
            entity.Property(e => e.CodeEvt)
                .HasMaxLength(8)
                .IsFixedLength()
                .HasColumnName("CODE_evt");
            entity.Property(e => e.Collaborateur).HasMaxLength(50);
            entity.Property(e => e.DateDemande).HasColumnName("Date_demande");
            entity.Property(e => e.DateDemande2)
                .HasColumnType("datetime")
                .HasColumnName("Date_demande2");
            entity.Property(e => e.DateEnattente)
                .HasColumnType("datetime")
                .HasColumnName("Date_Enattente");
            entity.Property(e => e.DateEstimee).HasColumnName("Date_estimee");
            entity.Property(e => e.DateEstimee2)
                .HasColumnType("datetime")
                .HasColumnName("Date_estimee2");
            entity.Property(e => e.Description).HasColumnType("text");
            entity.Property(e => e.Email)
                .HasMaxLength(80)
                .IsUnicode(false);
            entity.Property(e => e.Etat).HasMaxLength(50);
            entity.Property(e => e.FichierJoint).HasColumnName("Fichier_joint");
            entity.Property(e => e.FichierJoint1).HasColumnName("Fichier_joint1");
            entity.Property(e => e.FichierJoint2).HasColumnName("Fichier_joint2");
            entity.Property(e => e.Heured)
                .HasMaxLength(80)
                .IsUnicode(false)
                .HasColumnName("heured");
            entity.Property(e => e.Heured2)
                .HasColumnType("datetime")
                .HasColumnName("heured2");
            entity.Property(e => e.Heuref)
                .HasMaxLength(80)
                .IsUnicode(false)
                .HasColumnName("heuref");
            entity.Property(e => e.Heuref2)
                .HasColumnType("datetime")
                .HasColumnName("heuref2");
            entity.Property(e => e.Importance).HasMaxLength(50);
            entity.Property(e => e.Intitule).HasColumnName("Intitulé");
            entity.Property(e => e.Module).HasMaxLength(80);
            entity.Property(e => e.Produit).HasMaxLength(80);
            entity.Property(e => e.TypeTicket)
                .HasMaxLength(50)
                .HasColumnName("Type_ticket");
            entity.Property(e => e.Version)
                .HasMaxLength(80)
                .IsUnicode(false);
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
