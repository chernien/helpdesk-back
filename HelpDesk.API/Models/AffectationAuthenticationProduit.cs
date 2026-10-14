using System;
using System.Collections.Generic;

namespace HelpDesk.API.Models;

public partial class AffectationAuthenticationProduit
{
    public int UserId { get; set; }

    public int ProductId { get; set; }

    /// <summary>Client : version du produit concernée par le périmètre (null = toutes).</summary>
    public string? Version { get; set; }

    public virtual ProduitErp Product { get; set; } = null!;

    public virtual Authentication User { get; set; } = null!;
}
