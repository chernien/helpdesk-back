"""Recette : creation du compte d'un contact Divalto depuis le formulaire de ticket."""
from notif_lib import *
res = []
def ok(lib, cond, det=""):
    cond = bool(cond); res.append(cond); print(("  OK    | " if cond else "  ECHEC | ") + lib + ("" if cond else "  -> " + str(det)))

admin = login("test.admin@mediasoft.tn")
prod = {p["nom"]: p["productId"] for p in appel("GET", "/produits", admin)[1]}
appel("POST", "/users", admin, {"role": "ROLE_USER", "email": "val.ammar@mediasoft.tn", "password": MDP,
      "nom": "Guitouni", "prenom": "Ammar", "produitIds": [prod["Divalto"]]})
ammar = login("val.ammar@mediasoft.tn")

# Societe de test : on cherche un contact SANS compte
TIERS = "C0000005"
s, contacts = appel("GET", f"/clients/societes/{TIERS}/contacts", ammar)
ok("Chaque contact porte le drapeau aCompte", s == 200 and contacts and all("aCompte" in c for c in contacts), contacts[:2])
sans = [c for c in contacts if not c["aCompte"]]
print(f"   {len(contacts)} contacts, {len(sans)} sans compte")
if not sans:
    print("   (aucun contact sans compte sur cette societe : fin)"); raise SystemExit
cible = sans[0]["email"]

print("\n== Ticket cree pour un contact sans compte ==")
s, t = appel("POST", "/tickets", ammar, {"intitule": "Ticket avant creation du compte", "description": "x",
     "produit": "Divalto", "version": "v10", "module": "ERP", "typeTicket": "Question", "importance": "Mineur",
     "clientTiers": TIERS, "contactEmail": cible})
tid = t["id"]
ok("Ticket cree, sans compte client rattache", s == 201 and t["clientId"] is None, (s, t.get("clientId")))

print("\n== Creation du compte (collaborateur) ==")
s, r = appel("POST", "/clients/comptes", ammar, {"clientTiers": TIERS, "email": "inconnu@nulle.part",
     "nom": "X", "prenom": "Y", "password": MDP})
ok("Email hors de la societe -> refuse", s == 400, (s, r))
s, r = appel("POST", "/clients/comptes", ammar, {"clientTiers": TIERS, "email": cible, "nom": "Test", "prenom": "Contact", "password": "court"})
ok("Mot de passe trop court -> refuse", s == 400, (s, r))
s, u = appel("POST", "/clients/comptes", ammar, {"clientTiers": TIERS, "email": cible, "nom": "Test", "prenom": "Contact", "password": MDP})
ok("Le collaborateur cree le compte du contact", s == 200 and u["role"] == "ROLE_CLIENT" and u["clientTiers"] == TIERS, (s, u))
s, r = appel("POST", "/clients/comptes", ammar, {"clientTiers": TIERS, "email": cible, "nom": "Test", "prenom": "Contact", "password": MDP})
ok("Deuxieme creation -> conflit (409)", s == 409, s)

s, contacts = appel("GET", f"/clients/societes/{TIERS}/contacts", ammar)
ok("Le contact apparait maintenant 'avec compte'", any(c["email"] == cible and c["aCompte"] for c in contacts))

print("\n== Rattachement ==")
s, t = appel("GET", f"/tickets/{tid}", admin)
ok("L'ancien ticket est rattache au nouveau compte", t["clientId"] == u["id"], t["clientId"])
client = login(cible.lower())
s, t = appel("GET", f"/tickets/{tid}", client)
ok("Le client se connecte et voit son ticket", s == 200, s)

s, r = appel("POST", "/clients/comptes", client, {"clientTiers": TIERS, "email": cible, "nom": "a", "prenom": "b", "password": MDP})
ok("Un client ne peut pas creer de comptes -> 403", s == 403, s)

# Nettoyage
appel("DELETE", f"/tickets/{tid}", admin)
appel("POST", "/users/suppression", admin, {"ids": [u["id"]]})
print(f"\n== {sum(res)}/{len(res)} verifications reussies ==")
