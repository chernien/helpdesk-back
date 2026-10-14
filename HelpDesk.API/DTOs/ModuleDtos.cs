namespace HelpDesk.API.DTOs;

public class ModuleDto
{
    public int Id { get; set; }
    public string Nom { get; set; } = "";
}

public class ModuleCreateRequest
{
    public string Nom { get; set; } = null!;
}

/// <summary>Un produit (+ version) du périmètre d'un client. Les modules sont au niveau du client.</summary>
public class PerimetreLigneDto
{
    public string Produit { get; set; } = null!;
    public string? Version { get; set; }
}
