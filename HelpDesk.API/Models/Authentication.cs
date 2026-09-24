using System;
using System.Collections.Generic;

namespace HelpDesk.API.Models;

public partial class Authentication
{
    public int Id { get; set; }

    /// <summary>Email de connexion.</summary>
    public string? Username { get; set; }

    public string Password { get; set; } = null!;

    public string? Role { get; set; }

    public bool? Enabled { get; set; }

    public bool? Connected { get; set; }

    public string? Nom { get; set; }

    public string? ResetToken { get; set; }

    public DateTime? ResetTokenExpiry { get; set; }

    public int NombreConnexion { get; set; }

    // --- Colonnes ajoutees (database/002_gestion_utilisateurs.sql) ---

    public string? Prenom { get; set; }

    /// <summary>Code tiers Divalto de la societe (CLI.TIERS) — comptes clients.</summary>
    public string? ClientTiers { get; set; }

    /// <summary>Nom de la societe (CLI.NOM) — comptes clients.</summary>
    public string? SocieteNom { get; set; }

    public DateTime? DateCreation { get; set; }

    /// <summary>Derniere consultation des notifications (non lues = evenements posterieurs).</summary>
    public DateTime? NotificationsLuesLe { get; set; }

    public virtual ICollection<AffectationAuthenticationProduit> Affectations { get; set; } = new List<AffectationAuthenticationProduit>();

    /// <summary>"Prenom Nom", ou Nom seul pour les comptes historiques.</summary>
    public string NomComplet =>
        string.Join(' ', new[] { Prenom, Nom }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
}
