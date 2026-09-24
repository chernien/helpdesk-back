"""Recette : actions sur un ticket et notifications du client."""
from notif_lib import *
res = []
def ok(lib, cond, det=""):
    cond = bool(cond); res.append(cond); print(("  OK    | " if cond else "  ECHEC | ") + lib + ("" if cond else "  -> " + str(det)))

admin = login("test.admin@mediasoft.tn")
prod = {p["nom"]: p["productId"] for p in appel("GET", "/produits", admin)[1]}
comptes = [
  {"role": "ROLE_USER", "email": "act.ammar@mediasoft.tn", "password": MDP, "nom": "Guitouni", "prenom": "Ammar", "produitIds": [prod["Divalto"]]},
  {"role": "ROLE_USER", "email": "act.sami@mediasoft.tn", "password": MDP, "nom": "Ben Salah", "prenom": "Sami", "produitIds": [prod["Divalto"]]},
  {"role": "ROLE_USER", "email": "act.sage@mediasoft.tn", "password": MDP, "nom": "Sage", "prenom": "Collab", "produitIds": [prod["SAGE"]]},
  {"role": "ROLE_CLIENT", "email": "act.client@agora.div", "password": MDP, "nom": "Moult", "prenom": "Philippine", "clientTiers": "C0000005"}]
ids = {}
for c in comptes:
    s, r = appel("POST", "/users", admin, c)
    if s == 409: r = appel("GET", "/users?recherche=" + c["email"], admin)[1]["items"][0]
    ids[c["email"]] = r["id"]
ammar, sami, sage, client = (login(e) for e in ids)
def cloche(tok): return appel("GET", "/notifications/cloche", tok)[1]
for t in (ammar, sami, sage, client): appel("POST", "/notifications/marquer-lues", t)

print("\n== Reception du ticket ==")
s, t = appel("POST", "/tickets", client, {"intitule": "Ecart de TVA sur les factures de septembre", "description": "Test actions",
            "produit": "Divalto", "version": "v10", "module": "Comptabilité", "typeTicket": "Erreur", "importance": "Grave", "dateEstimee": "2026-09-30"})
tid = t["id"]
ok("Le client cree le ticket (Ouvert, non assigne)", s == 201 and t["etat"] == "Ouvert" and t["collaborateurId"] is None, t)
ok("Les 2 collaborateurs Divalto sont notifies", cloche(ammar)["nonLues"] == 1 and cloche(sami)["nonLues"] == 1)
ok("Le collaborateur SAGE n'est pas notifie", cloche(sage)["nonLues"] == 0)

print("\n== Je m'en occupe / confier ==")
s, collegues = appel("GET", "/users/collaborateurs?produit=Divalto", ammar)
ok("Un collaborateur peut lister ses collegues du produit (pour confier)",
   s == 200 and any(c["id"] == ids["act.sami@mediasoft.tn"] for c in collegues), s)
ok("Un client ne peut pas lister les collaborateurs -> 403",
   appel("GET", "/users/collaborateurs?produit=Divalto", client)[0] == 403)

s, t = appel("PUT", f"/tickets/{tid}", ammar, {"collaborateurId": ids["act.ammar@mediasoft.tn"]})
ok("Ammar prend le ticket : il devient responsable", s == 200 and t["collaborateurId"] == ids["act.ammar@mediasoft.tn"], (s, t))
ok("L'etat reste Ouvert (pas de changement automatique)", t["etat"] == "Ouvert", t["etat"])
c = cloche(client)
ok("Le client est notifie de la prise en charge", c["nonLues"] == 1 and c["items"][0]["evenement"]["typeEvent"] == "ASSIGNATION", c["nonLues"])

s, r = appel("PUT", f"/tickets/{tid}", ammar, {"collaborateurId": ids["act.sage@mediasoft.tn"]})
ok("Confier a un collaborateur d'un autre produit -> refuse", s == 400, (s, r))

s, t = appel("PUT", f"/tickets/{tid}", ammar, {"collaborateurId": ids["act.sami@mediasoft.tn"], "commentaire": "Sami, specialiste TVA, reprend votre dossier."})
ok("Ammar confie le ticket a Sami, avec un message", s == 200 and t["collaborateurId"] == ids["act.sami@mediasoft.tn"], (s, t))
c = cloche(client)
ev = c["items"][0]["evenement"]
ok("Le client recoit UNE SEULE notification (reassignation + message reunis)",
   c["nonLues"] == 2 and ev["typeEvent"] == "ASSIGNATION" and ev["commentaire"] == "Sami, specialiste TVA, reprend votre dossier.",
   (c["nonLues"], ev["typeEvent"], ev["commentaire"]))
assign = next(i["evenement"] for i in c["items"] if i["evenement"]["typeEvent"] == "ASSIGNATION")
ok("L'evenement indique a qui le ticket est confie", assign["collaborateurNom"] == "Sami Ben Salah" and assign["parNom"] == "Ammar Guitouni", assign)
ok("Sami (nouveau responsable) est notifie", cloche(sami)["nonLues"] >= 1)

print("\n== Actions d'etat ==")
appel("POST", "/notifications/marquer-lues", client)
s, t = appel("PUT", f"/tickets/{tid}", sami, {"etat": "En développement", "commentaire": "Correctif du calcul de TVA en cours."})
ok("Sami : En developpement avec message", s == 200 and t["etat"] == "En développement", (s, t))
ev = cloche(client)["items"][0]["evenement"]
ok("Le client est notifie avec le message", ev["typeEvent"] == "ETAT" and ev["commentaire"] == "Correctif du calcul de TVA en cours.", ev)

s, t = appel("PUT", f"/tickets/{tid}", sami, {"etat": "En développement"})
nb = cloche(client)["nonLues"]
ok("Meme etat renvoye : aucune notification en double", s == 200 and nb == 1, nb)

s, t = appel("PUT", f"/tickets/{tid}", sami, {"commentaire": "Pouvez-vous nous envoyer la facture FC-0915 ?"})
ok("Message seul, sans changer l'etat -> notifie", cloche(client)["items"][0]["evenement"]["typeEvent"] == "COMMENTAIRE")

s, r = appel("PUT", f"/tickets/{tid}", sami, {"version": "v10.2"})
ok("Correction d'une information par un collaborateur -> refusee (admin seulement)", s == 403, s)
s, t = appel("PUT", f"/tickets/{tid}", admin, {"version": "v10.2"})
ok("Correction par l'admin -> client notifie (mise a jour)", s == 200 and cloche(client)["items"][0]["evenement"]["typeEvent"] == "MISE_A_JOUR")

s, t = appel("PUT", f"/tickets/{tid}", sami, {"etat": "En attente de validation"})
ok("Sami : En attente de validation", s == 200 and t["etat"] == "En attente de validation" and t["dateEnattente"], t)

s, r = appel("PUT", f"/tickets/{tid}", sami, {"etat": "Ouvert"})
ok("Retour a Ouvert refuse pour un collaborateur (reserve a l'admin)", s == 400, (s, r))
s, t = appel("PUT", f"/tickets/{tid}", admin, {"etat": "Ouvert"})
ok("L'admin, lui, peut revenir a Ouvert", s == 200 and t["etat"] == "Ouvert", (s, t))

s, t = appel("PUT", f"/tickets/{tid}", sami, {"etat": "Annulé", "commentaire": "Doublon du ticket 1290."})
ok("Annulation avec motif", s == 200 and t["etat"] == "Annulé", (s, t))
s, r = appel("PUT", f"/tickets/{tid}", sami, {"etat": "En cours"})
ok("Un ticket annule ne peut plus evoluer -> refuse", s == 400, (s, r))

print("\n== Cote client ==")
s, r = appel("PUT", f"/tickets/{tid}", client, {"commentaire": "test"})
ok("Le client ne peut pas agir sur le traitement -> 403", s == 403, s)
c = cloche(client)
ok("Le client a " + str(c["nonLues"]) + " notifications non lues (toutes les evolutions)", c["nonLues"] >= 6, c["nonLues"])
s, suivi = appel("GET", f"/tickets/{tid}/suivi", client)
print("   Suivi vu par le client :")
for e in suivi:
    print("     -", e["typeEvent"], e["etat"], "| par", e["parNom"], "| resp.", e["collaborateurNom"], ("| « " + e["commentaire"] + " »") if e["commentaire"] else "")
ok("Le client voit le suivi complet, dans l'ordre", s == 200 and suivi[0]["typeEvent"] == "CREATION" and suivi[-1]["etat"] == "Annulé", len(suivi))
s, _ = appel("GET", f"/tickets/{tid}/suivi", sage)
ok("Suivi inaccessible au collaborateur SAGE -> 404", s == 404, s)

open("act_ids.json", "w").write(json.dumps(ids))
print(f"\n== {sum(res)}/{len(res)} verifications reussies ==")
