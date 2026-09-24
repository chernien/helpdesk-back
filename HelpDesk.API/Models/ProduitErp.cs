using System;
using System.Collections.Generic;

namespace HelpDesk.API.Models;

public partial class ProduitErp
{
    public int ProductId { get; set; }

    public string Nom { get; set; } = null!;
}
