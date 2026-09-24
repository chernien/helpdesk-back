"""Recette : role developpeur et cycle test interne."""
from notif_lib import *
res = []
def ok(lib, cond, det=""):
    cond = bool(cond); res.append(cond); print(("  OK    | " if cond else "  ECHEC | ") + lib + ("" if cond else "  -> " + str(det)))

admin = login("test.admin@mediasoft.tn")
prod = {p["nom"]: p["productId"] for p in appel("GET", "/produits", admin)[1]}
comptes = [
  {"role": "ROLE_USER", "email": "val.ammar@mediasoft.tn", "password": MDP, "nom": "Guitouni", "prenom": "Ammar", "produitIds": [prod["Divalto"]]},
  {"role": "ROLE_USER", "email": "val.sami@mediasoft.tn", "password": MDP, "nom": "Ben Salah", "prenom": "Sami", "produitIds": [prod["Divalto"]]},
  {"role": "ROLE_DEV", "email": "dev.karim@mediasoft.tn", "password": MDP, "nom": "Trabelsi", "prenom": "Karim", "produitIds": [prod["Divalto"]]},
  {"role": "ROLE_CLIENT", "email": "val.client@agora.div", "password": MDP, "nom": "Moult", "prenom": "Philippine", "clientTiers": "C0000005"}]
ids = {}
for c in comptes:
    s, r = appel("POST", "/users", admin, c)
    if s == 409: r = appel("GET", "/users?recherche=" + c["email"], admin)[1]["items"][0]
    ids[c["email"]] = r["id"]
ammar, sami, dev, client = (login(e) for e in ids)
def cloche(tok): return appel("GET", "/notifications/cloche", tok)[1]

print("\n== Creation du compte developpeur ==")
s, r = appel("GET", "/users?recherche=dev.karim", admin)
u = r["items"][0]
ok("Compte ROLE_DEV cree avec produit Divalto", u["role"] == "ROLE_DEV" and any(p["nom"] == "Divalto" for p in u["produits"]), u)

print("\n== Le dev ne recoit pas les tickets a la creation ==")
for t in (ammar, sami, dev): appel("POST", "/notifications/marquer-lues", t)
s, t = appel("POST", "/tickets", client, {"intitule": "Cycle complet avec developpeur", "description": "Test workflow dev",
            "produit": "Divalto", "version": "v10", "module": "ERP", "typeTicket": "Erreur", "importance": "Grave"})
tid = t["id"]
ok("Les collaborateurs (users) sont notifies", cloche(ammar)["nonLues"] == 1 and cloche(sami)["nonLues"] == 1)
ok("Le developpeur n'est PAS notifie a la creation", cloche(dev)["nonLues"] == 0, cloche(dev)["nonLues"])
s, r = appel("GET", f"/tickets/{tid}", dev)
ok("Le ticket est invisible pour le dev tant qu'il n'est pas assigne -> 404", s == 404, s)
s, r = appel("GET", "/tickets?page=1&pageSize=50", dev)
did_moi = ids["dev.karim@mediasoft.tn"]
ok("La liste du dev ne contient QUE ses tickets assignes",
   s == 200 and all(t["collaborateurId"] == did_moi for t in r["items"]) and not any(t["id"] == tid for t in r["items"]),
   [(t["id"], t["collaborateurId"]) for t in r["items"]])

print("\n== Interdictions du developpeur ==")
s, r = appel("POST", "/tickets", dev, {"intitule": "x", "produit": "Divalto", "importance": "Mineur"})
ok("Un dev ne peut pas creer de ticket -> 403", s == 403, s)

print("\n== Assignation au developpeur ==")
s, t = appel("PUT", f"/tickets/{tid}", ammar, {"collaborateurId": ids["dev.karim@mediasoft.tn"],
      "etat": "En développement", "commentaire": "Karim prend le développement."})
ok("Ammar confie au dev + En développement (un envoi)", s == 200 and t["collaborateurId"] == ids["dev.karim@mediasoft.tn"] and t["etat"] == "En développement", (s, t))
c = cloche(dev)
ok("Le dev est notifie de l'assignation", c["nonLues"] == 1 and c["items"][0]["evenement"]["typeEvent"] == "ASSIGNATION_ETAT", c["nonLues"])
s, r = appel("GET", f"/tickets/{tid}", dev)
ok("Le ticket est maintenant visible pour le dev", s == 200, s)

s, r = appel("PUT", f"/tickets/{tid}", dev, {"etat": "Clôturé", "commentaire": "fini"})
ok("Le dev ne peut pas cloturer -> 403", s == 403, (s, r))
s, r = appel("PUT", f"/tickets/{tid}", dev, {"collaborateurId": ids["val.sami@mediasoft.tn"]})
ok("Le dev ne peut pas reassigner -> 403", s == 403, (s, r))
s, r = appel("PUT", f"/tickets/{tid}", dev, {"intitule": "hack"})
ok("Le dev ne peut pas modifier le ticket -> 403", s == 403, (s, r))

print("\n== Test interne ==")
for t2 in (ammar, sami): appel("POST", "/notifications/marquer-lues", t2)
s, t = appel("PUT", f"/tickets/{tid}", dev, {"etat": "Test interne", "commentaire": "Correctif livré en interne, à tester."})
ok("Le dev envoie en Test interne", s == 200 and t["etat"] == "Test interne", (s, t))
ok("Tous les users du produit sont notifies", cloche(ammar)["nonLues"] == 1 and cloche(sami)["nonLues"] == 1,
   (cloche(ammar)["nonLues"], cloche(sami)["nonLues"]))

print("\n== Rejet du test ==")
s, r = appel("PUT", f"/tickets/{tid}", sami, {"etat": "En développement"})
ok("Rejet sans explication -> refuse (400)", s == 400 and "développeur" in str(r), (s, r))
appel("POST", "/notifications/marquer-lues", dev)
s, t = appel("PUT", f"/tickets/{tid}", sami, {"etat": "En développement", "commentaire": "Le cas des avoirs n'est pas couvert."})
ok("Rejet avec explication -> le ticket retourne au dev", s == 200 and t["etat"] == "En développement"
   and t["collaborateurId"] == ids["dev.karim@mediasoft.tn"], (s, t))
c = cloche(dev)
ok("Le dev est notifie du rejet avec le motif", c["nonLues"] == 1 and c["items"][0]["evenement"]["commentaire"] == "Le cas des avoirs n'est pas couvert.", c)

print("\n== Deuxieme passe : test valide puis validation client ==")
appel("PUT", f"/tickets/{tid}", dev, {"etat": "Test interne", "commentaire": "Avoirs couverts, nouvelle version."})
s, t = appel("PUT", f"/tickets/{tid}", ammar, {"etat": "En attente de validation", "commentaire": "Test interne concluant : merci de valider."})
ok("Test concluant -> En attente de validation (client sollicite)", s == 200 and t["etat"] == "En attente de validation", (s, t))
c = cloche(client)
ok("Le client est notifie pour valider", any(i["evenement"]["etat"] == "En attente de validation" for i in c["items"][:3]), c["nonLues"])
s, t = appel("POST", f"/tickets/{tid}/validation", client, {"valider": True, "commentaire": "Parfait, merci."})
ok("Le client valide -> Clôturé", s == 200 and t["etat"] == "Clôturé", (s, t))
c = cloche(dev)
ok("Le dev (responsable) est notifie de la cloture", any(i["evenement"]["typeEvent"] == "VALIDATION_CLIENT" for i in c["items"][:2]), c)

print("\n== Suivi complet ==")
s, suivi = appel("GET", f"/tickets/{tid}/suivi", admin)
print("   " + " -> ".join(f'{e["typeEvent"]}({e["etat"] or ""})' for e in suivi))
ok("Le fil retrace tout le cycle", [e["typeEvent"] for e in suivi] ==
   ["CREATION", "ASSIGNATION_ETAT", "ETAT", "ETAT", "ETAT", "ETAT", "VALIDATION_CLIENT"], [e["typeEvent"] for e in suivi])

appel("DELETE", f"/tickets/{tid}", admin)
print(f"\n== {sum(res)}/{len(res)} verifications reussies ==")
