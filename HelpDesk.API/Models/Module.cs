using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace HelpDesk.API.Models;

/// <summary>Module géré par l'admin (liste du menu « Module » des tickets).</summary>
[Table("HELPDESK_MODULE")]
[Index(nameof(Nom), IsUnique = true)]
public partial class Module
{
    public int Id { get; set; }
    [MaxLength(100)] public string Nom { get; set; } = null!;
}
