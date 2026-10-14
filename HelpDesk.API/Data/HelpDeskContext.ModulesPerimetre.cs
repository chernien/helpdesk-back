using HelpDesk.API.Models;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.API.Data;

/// <summary>
/// Extension du contexte : modules gérés par l'admin (HELPDESK_MODULE).
/// Table créée à la main, mappée par annotations ([Table]/[Index]).
/// Le périmètre des clients est stocké dans la table d'affectation existante
/// (AffectationAuthenticationProduit : colonnes Version + Modules).
/// </summary>
public partial class HelpDeskContext
{
    public virtual DbSet<Module> Module { get; set; }
}
