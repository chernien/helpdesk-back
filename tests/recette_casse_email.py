"""Recette : un email Divalto en majuscules = le meme compte Helpdesk en minuscules."""
from notif_lib import *
res = []
def ok(lib, cond, det=""):
    cond = bool(cond); res.append(cond); print(("  OK    | " if cond else "  ECHEC | ") + lib + ("" if cond else "  -> " + str(det)))

TIERS, DIVALTO = "C0000053", "Ssticker@carrefour.div"   # tel qu'ecrit dans T2
admin = login("test.admin@mediasoft.tn")
prod = {p["nom"]: p["productId"] for p in appel("GET", "/produits", admin)[1]}
appel("POST", "/users", admin, {"role": "ROLE_USER", "email": "val.ammar@mediasoft.tn", "password": MDP,
      "nom": "Guitouni", "prenom": "Ammar", "produitIds": [prod["Divalto"]]})
ammar = login("val.ammar@mediasoft.tn")

s, contacts = appel("GET", f"/clients/societes/{TIERS}/contacts", ammar)
c = next((c for c in contacts if c["email"].lower() == DIVALTO.lower()), None)
ok("Le contact Divalto est renvoye en minuscules", c and c["email"] == DIVALTO.lower(), contacts)

print("\n== Ticket cree AVANT le compte, avec l'email en MAJUSCULES ==")
s, t = appel("POST", "/tickets", ammar, {"intitule": "Test casse email", "description": "x", "produit": "Divalto",
     "version": "v10", "module": "ERP", "typeTicket": "Question", "importance": "Mineur",
     "clientTiers": TIERS, "contactEmail": DIVALTO.upper()})
ok("Le contact est reconnu malgre les majuscules", s == 201, (s, t))
tid = t["id"]
ok("L'email est enregistre en minuscules dans le ticket", t["email"] == DIVALTO.lower(), t["email"])

print("\n== Creation du compte avec l'email en MAJUSCULES ==")
s, u = appel("POST", "/clients/comptes", ammar, {"clientTiers": TIERS, "email": DIVALTO.upper(),
     "nom": "STICKER", "prenom": "Sophie", "password": MDP})
ok("Compte cree, identifiant en minuscules", s == 200 and u["username"] == DIVALTO.lower(), (s, u))
s, r = appel("POST", "/users", admin, {"role": "ROLE_CLIENT", "email": "SSTICKER@CARREFOUR.DIV", "password": MDP,
     "nom": "X", "prenom": "Y", "clientTiers": TIERS})
ok("Un doublon qui ne differe que par la casse est refuse (409)", s == 409, s)
s, contacts = appel("GET", f"/clients/societes/{TIERS}/contacts", ammar)
ok("Le contact apparait 'avec compte' malgre la casse Divalto", any(x["email"] == DIVALTO.lower() and x["aCompte"] for x in contacts))

print("\n== Connexion et visibilite ==")
for saisie in ["SSTICKER@CARREFOUR.DIV", "Ssticker@Carrefour.div", "ssticker@carrefour.div"]:
    s, r = appel("POST", "/auth/login", corps={"username": saisie, "password": MDP})
    ok(f"Connexion avec « {saisie} »", s == 200, s)
client = login("SSTICKER@CARREFOUR.DIV")
s, t = appel("GET", f"/tickets/{tid}", client)
ok("Le client voit le ticket cree avant son compte", s == 200 and t["clientId"] == u["id"], (s, t.get("clientId") if isinstance(t, dict) else t))

s, t2 = appel("POST", "/tickets", ammar, {"intitule": "Test casse 2", "description": "x", "produit": "Divalto",
      "version": "v10", "module": "ERP", "typeTicket": "Question", "importance": "Mineur",
      "clientTiers": TIERS, "contactEmail": "sSTICKER@carrefour.DIV"})
ok("Nouveau ticket : rattache directement au compte", s == 201 and t2["clientId"] == u["id"], (s, t2.get("clientId")))

appel("DELETE", f"/tickets/{tid}", admin); appel("DELETE", f"/tickets/{t2['id']}", admin)
appel("POST", "/users/suppression", admin, {"ids": [u["id"]]})
print(f"\n== {sum(res)}/{len(res)} verifications reussies ==")
