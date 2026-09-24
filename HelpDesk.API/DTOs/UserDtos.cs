namespace HelpDesk.API.DTOs;

public class UserDto
{
    public int Id { get; set; }
    /// <summary>Email de connexion.</summary>
    public string? Username { get; set; }
    public string? Nom { get; set; }
    public string? Prenom { get; set; }
    public string NomComplet { get; set; } = "";
    public string? Role { get; set; }
    public bool? Enabled { get; set; }
    public string? ClientTiers { get; set; }
    public string? SocieteNom { get; set; }
    public int NombreConnexion { get; set; }
    public DateTime? DateCreation { get; set; }
    public List<ProduitDto> Produits { get; set; } = new();
}

public class UserPageDto
{
    public int Total { get; set; }
    public List<UserDto> Items { get; set; } = new();
}

/// <summary>Collaborateur (ROLE_USER) proposable pour l'assignation d'un ticket.</summary>
public class CollaborateurDto
{
    public int Id { get; set; }
    public string NomComplet { get; set; } = "";
    public string? Email { get; set; }
    /// <summary>ROLE_USER ou ROLE_DEV : permet de grouper la liste « Confier à ».</summary>
    public string? Role { get; set; }
}

public class CreateUserRequest
{
    /// <summary>ROLE_ADMIN | ROLE_USER | ROLE_CLIENT</summary>
    public string Role { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string Password { get; set; } = null!;
    public string Nom { get; set; } = null!;
    public string Prenom { get; set; } = null!;
    /// <summary>ROLE_CLIENT : code tiers de la societe (CLI.TIERS). Le nom est relu en base.</summary>
    public string? ClientTiers { get; set; }
    /// <summary>ROLE_USER : produits ERP traites (au moins un).</summary>
    public List<int> ProduitIds { get; set; } = new();
}

public class SuppressionUsersRequest
{
    public List<int> Ids { get; set; } = new();
}

public class SuppressionUsersResultDto
{
    public int ComptesSupprimes { get; set; }
    /// <summary>Tickets non clotures remis dans la file de leur produit.</summary>
    public int TicketsRemisEnFile { get; set; }
}

/// <summary>Le role n'est pas modifiable : on desactive et on recree.</summary>
public class UpdateUserRequest
{
    public string Email { get; set; } = null!;
    public string Nom { get; set; } = null!;
    public string Prenom { get; set; } = null!;
    public bool Enabled { get; set; } = true;
    /// <summary>Vide = mot de passe inchange.</summary>
    public string? Password { get; set; }
    public string? ClientTiers { get; set; }
    public List<int> ProduitIds { get; set; } = new();
}
