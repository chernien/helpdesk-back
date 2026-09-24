"""Recette : l'administrateur corrige n'importe quel champ d'un ticket."""
from notif_lib import *
res = []
def ok(lib, cond, det=""):
    cond = bool(cond); res.append(cond); print(("  OK    | " if cond else "  ECHEC | ") + lib + ("" if cond else "  -> " + str(det)))

admin = login("test.admin@mediasoft.tn")
prod = {p["nom"]: p["productId"] for p in appel("GET", "/produits", admin)[1]}
for c in [{"role": "ROLE_USER", "email": "val.ammar@mediasoft.tn", "password": MDP, "nom": "Guitouni", "prenom": "Ammar", "produitIds": [prod["Divalto"]]},
          {"role": "ROLE_CLIENT", "email": "val.client@agora.div", "password": MDP, "nom": "Moult", "prenom": "Philippine", "clientTiers": "C0000005"}]:
    appel("POST", "/users", admin, c)
ammar, client = login("val.ammar@mediasoft.tn"), login("val.client@agora.div")

s, t = appel("POST", "/tickets", client, {"intitule": "Ticket mal saisi", "description": "Erreur de saisie",
     "produit": "Divalto", "version": "v9", "module": "Paie", "typeTicket": "Question", "importance": "Mineur", "dateEstimee": "2026-09-30"})
tid = t["id"]

print("\n== Droits ==")
s, r = appel("PUT", f"/tickets/{tid}", ammar, {"module": "Comptabilité"})
ok("Un collaborateur ne peut pas corriger le ticket -> 403", s == 403, s)
s, r = appel("PUT", f"/tickets/{tid}", client, {"module": "Comptabilité"})
ok("Le client ne peut pas corriger le ticket -> 403", s == 403, s)
s, r = appel("PUT", f"/tickets/{tid}", ammar, {"resolution": "Note interne"})
ok("La note interne reste ouverte aux collaborateurs", s == 200, (s, r))

print("\n== Corrections par l'admin ==")
s, t = appel("PUT", f"/tickets/{tid}", admin, {"intitule": "Calcul de TVA erroné", "module": "Comptabilité",
     "typeTicket": "Erreur", "importance": "Grave", "version": "v10", "dateEstimee": "2026-10-15",
     "description": "Description corrigée", "produit": "SAGE"})
ok("Tous les champs sont corriges en un envoi", s == 200 and t["intitule"] == "Calcul de TVA erroné" and t["module"] == "Comptabilité"
   and t["typeTicket"] == "Erreur" and t["importance"] == "Grave" and t["version"] == "v10"
   and t["dateEstimee"].startswith("2026-10-15") and t["produit"] == "SAGE", t)
s, suivi = appel("GET", f"/tickets/{tid}/suivi", client)
ev = suivi[-1]
ok("Le suivi trace la correction (liste des champs)", ev["typeEvent"] == "MISE_A_JOUR" and "Module" in (ev["commentaire"] or "")
   and "Date estimée" in ev["commentaire"], ev)
s, t2 = appel("GET", f"/tickets/{tid}", admin)
ok("La derniere reponse au client n'est pas ecrasee par le resume", not (t2["reponse"] or "").startswith("Informations corrigées"), t2["reponse"])

print("\n== Validations ==")
for champ, val in [("module", "Inexistant"), ("typeTicket", "Bug"), ("importance", "Critique"), ("intitule", "  "), ("produit", "Oracle")]:
    s, r = appel("PUT", f"/tickets/{tid}", admin, {champ: val})
    ok(f"{champ} = « {val} » refuse", s == 400, (s, r))

print("\n== Changement de client ==")
s, contacts = appel("GET", "/clients/societes/C0000053/contacts", admin)
nouveau = contacts[0]["email"]
s, t = appel("PUT", f"/tickets/{tid}", admin, {"clientTiers": "C0000053", "contactEmail": nouveau.upper()})
ok("Le ticket est reaffecte a un autre client / contact", s == 200 and t["email"] == nouveau.lower() and t["client"] != "AGORA BUREAU", (s, t.get("client"), t.get("email")))
s, r = appel("PUT", f"/tickets/{tid}", admin, {"clientTiers": "C0000053", "contactEmail": "faux@nulle.part"})
ok("Contact hors de la societe -> refuse", s == 400, s)
s, r = appel("GET", f"/tickets/{tid}", client)
ok("L'ancien client ne voit plus le ticket", s == 404, s)

print("\n== Ticket termine ==")
appel("PUT", f"/tickets/{tid}", admin, {"etat": "Annulé", "commentaire": "Test"})
s, t = appel("PUT", f"/tickets/{tid}", admin, {"module": "ERP"})
ok("L'admin peut corriger meme un ticket annule", s == 200 and t["module"] == "ERP", s)

appel("DELETE", f"/tickets/{tid}", admin)
print(f"\n== {sum(res)}/{len(res)} verifications reussies ==")
