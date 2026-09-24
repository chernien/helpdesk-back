namespace HelpDesk.API.Constants;

public static class Roles
{
    public const string Admin = "ROLE_ADMIN";
    public const string User = "ROLE_USER";
    /// <summary>Developpeur : ne voit que les tickets qui lui sont assignes, developpe puis envoie en test interne.</summary>
    public const string Dev = "ROLE_DEV";
    public const string Client = "ROLE_CLIENT";

    public static readonly string[] Tous = [Admin, User, Dev, Client];

    /// <summary>Roles rattaches aux produits ERP (affectations).</summary>
    public static readonly string[] AvecProduits = [User, Dev];
}
