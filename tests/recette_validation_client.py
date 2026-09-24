"""Recette : validation client, justification de cloture, notification unique."""
from notif_lib import *
res = []
def ok(lib, cond, det=""):
    cond = bool(cond); res.append(cond); print(("  OK    | " if cond else "  ECHEC | ") + lib + ("" if cond else "  -> " + str(det)))

admin = login("test.admin@mediasoft.tn")
prod = {p["nom"]: p["productId"] for p in appel("GET", "/produits", admin)[1]}
comptes = [
  {"role": "ROLE_USER", "email": "val.ammar@mediasoft.tn", "password": MDP, "nom": "Guitouni", "prenom": "Ammar", "produitIds": [prod["Divalto"]]},
  {"role": "ROLE_USER", "email": "val.sami@mediasoft.tn", "password": MDP, "nom": "Ben Salah", "prenom": "Sami", "produitIds": [prod["Divalto"]]},
  {"role": "ROLE_CLIENT", "email": "val.client@agora.div", "password": MDP, "nom": "Moult", "prenom": "Philippine", "clientTiers": "C0000005"}]
ids = {}
for c in comptes:
    s, r = appel("POST", "/users", admin, c)
    if s == 409: r = appel("GET", "/users?recherche=" + c["email"], admin)[1]["items"][0]
    ids[c["email"]] = r["id"]
ammar, sami, client = (login(e) for e in ids)
def cloche(tok): return appel("GET", "/notifications/cloche", tok)[1]
def creer():
    return appel("POST", "/tickets", client, {"intitule": "Recette validation client", "description": "Test",
        "produit": "Divalto", "version": "v10", "module": "Comptabilité", "typeTicket": "Erreur", "importance": "Grave"})[1]["id"]

print("\n== 1. Notification unique (confier + etat + message en un envoi) ==")
t1 = creer()
appel("POST", "/notifications/marquer-lues", client)
s, t = appel("PUT", f"/tickets/{t1}", ammar,
    {"collaborateurId": ids["val.sami@mediasoft.tn"], "etat": "En cours", "commentaire": "Sami prend votre dossier en main."})
ok("Confier a Sami + En cours + message : accepte en un seul envoi",
   s == 200 and t["collaborateurId"] == ids["val.sami@mediasoft.tn"] and t["etat"] == "En cours", (s, t))
c = cloche(client)
ev = c["items"][0]["evenement"]
ok("Le client recoit UNE seule notification", c["nonLues"] == 1, c["nonLues"])
ok("Elle reunit assignation + etat + message (ASSIGNATION_ETAT)",
   ev["typeEvent"] == "ASSIGNATION_ETAT" and ev["etat"] == "En cours"
   and ev["collaborateurNom"] == "Sami Ben Salah" and ev["commentaire"] == "Sami prend votre dossier en main.", ev)
s, suivi = appel("GET", f"/tickets/{t1}/suivi", client)
ok("Le suivi ne contient que 2 evenements (creation + action groupee)",
   len(suivi) == 2 and suivi[1]["typeEvent"] == "ASSIGNATION_ETAT", [e["typeEvent"] for e in suivi])

print("\n== 2. Cloturer exige une justification ==")
s, r = appel("PUT", f"/tickets/{t1}", sami, {"etat": "Clôturé"})
ok("Cloture sans justification -> refusee (400)", s == 400 and "justification" in str(r).lower(), (s, r))
s, r = appel("PUT", f"/tickets/{t1}", sami, {"etat": "Clôturé", "commentaire": "   "})
ok("Cloture avec justification vide -> refusee", s == 400, (s, r))
s, t = appel("PUT", f"/tickets/{t1}", sami, {"etat": "Clôturé", "commentaire": "Correctif livre et verifie avec vous."})
ok("Cloture avec justification -> acceptee", s == 200 and t["etat"] == "Clôturé", (s, t))
ev = cloche(client)["items"][0]["evenement"]
ok("Le client recoit la justification", ev["etat"] == "Clôturé" and ev["commentaire"] == "Correctif livre et verifie avec vous.", ev)

print("\n== 3. Validation par le client ==")
t2 = creer()
s, r = appel("POST", f"/tickets/{t2}/validation", client, {"valider": True})
ok("Valider un ticket qui n'est pas en attente -> refuse (400)", s == 400, (s, r))
s, r = appel("POST", f"/tickets/{t2}/validation", ammar, {"valider": True})
ok("Un collaborateur ne peut pas utiliser la validation client -> 403", s == 403, s)

appel("PUT", f"/tickets/{t2}", ammar, {"collaborateurId": ids["val.ammar@mediasoft.tn"],
      "etat": "En attente de validation", "commentaire": "Correctif livre : merci de valider."})
appel("POST", "/notifications/marquer-lues", ammar)
s, t = appel("POST", f"/tickets/{t2}/validation", client, {"valider": True, "commentaire": "Tout fonctionne, merci."})
ok("Le client valide la solution -> ticket Clôturé", s == 200 and t["etat"] == "Clôturé", (s, t))
c = cloche(ammar)
ev = c["items"][0]["evenement"]
ok("Le responsable est notifie de la validation (VALIDATION_CLIENT)",
   c["nonLues"] >= 1 and ev["typeEvent"] == "VALIDATION_CLIENT" and ev["commentaire"] == "Tout fonctionne, merci.", ev)
s, r = appel("POST", f"/tickets/{t2}/validation", client, {"valider": False})
ok("Un ticket deja cloture ne peut plus etre annule -> 400", s == 400, (s, r))
s, r = appel("PUT", f"/tickets/{t2}", sami, {"etat": "En cours"})
ok("Un ticket cloture par le client n'evolue plus (collaborateur) -> 400", s == 400, (s, r))

print("\n== 4. Annulation par le client ==")
t3 = creer()
appel("PUT", f"/tickets/{t3}", ammar, {"collaborateurId": ids["val.ammar@mediasoft.tn"],
      "etat": "En attente de validation", "commentaire": "Merci de valider."})
appel("POST", "/notifications/marquer-lues", ammar)
s, t = appel("POST", f"/tickets/{t3}/validation", client, {"valider": False, "commentaire": "Le probleme persiste sur un autre poste."})
ok("Le client annule le ticket -> Annulé", s == 200 and t["etat"] == "Annulé", (s, t))
ev = cloche(ammar)["items"][0]["evenement"]
ok("Le responsable est notifie de l'annulation (ANNULATION_CLIENT)",
   ev["typeEvent"] == "ANNULATION_CLIENT" and ev["commentaire"] == "Le probleme persiste sur un autre poste.", ev)
s, suivi = appel("GET", f"/tickets/{t3}/suivi", client)
ok("Suivi complet : CREATION, ASSIGNATION_ETAT, ANNULATION_CLIENT",
   [e["typeEvent"] for e in suivi] == ["CREATION", "ASSIGNATION_ETAT", "ANNULATION_CLIENT"], [e["typeEvent"] for e in suivi])

# Nettoyage : suppression des tickets de recette
for tid in (t1, t2, t3): appel("DELETE", f"/tickets/{tid}", admin)
print(f"\n== {sum(res)}/{len(res)} verifications reussies ==")
