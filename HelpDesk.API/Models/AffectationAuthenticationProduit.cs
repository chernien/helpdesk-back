using System;
using System.Collections.Generic;

namespace HelpDesk.API.Models;

public partial class AffectationAuthenticationProduit
{
    public int UserId { get; set; }

    public int ProductId { get; set; }

    public virtual ProduitErp Product { get; set; } = null!;

    public virtual Authentication User { get; set; } = null!;
}
