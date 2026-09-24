"""Recette des regles metier : gestion utilisateurs + distribution par produit."""
import json
import urllib.request
import urllib.error

API = "http://localhost:5008/api"
MDP = "Test2026!"
resultats = []


def appel(methode, chemin, token=None, corps=None):
    data = json.dumps(corps).encode("utf-8") if corps is not None else None
    req = urllib.request.Request(API + chemin, data=data, method=methode)
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(req) as r:
            brut = r.read().decode("utf-8")
            return r.status, (json.loads(brut) if brut else None)
    except urllib.error.HTTPError as e:
        brut = e.read().decode("utf-8")
        try:
            return e.code, json.loads(brut)
        except Exception:
            return e.code, brut


def verifier(libelle, condition, detail=""):
    resultats.append((condition, libelle))
    print(("  OK   " if condition else "  ECHEC") + " | " + libelle + ("" if condition else "  -> " + str(detail)))


def login(email):
    s, r = appel("POST", "/auth/login", corps={"username": email, "password": MDP})
    return r["token"] if s == 200 else None


def trouver_ou_creer(admin, corps):
    s, r = appel("POST", "/users", admin, corps)
    if s == 409:  # deja cree lors d'un passage precedent
        _, page = appel("GET", "/users?recherche=" + corps["email"], admin)
        return 200, page["items"][0]
    return s, r


admin = login("test.admin@mediasoft.tn")
produits = {p["nom"]: p["productId"] for p in appel("GET", "/produits", admin)[1]}

print("\n== Creation des comptes ==")
s, divalto = trouver_ou_creer(admin, {"role": "ROLE_USER", "email": "collab.divalto@mediasoft.tn", "password": MDP,
                                      "nom": "Recette", "prenom": "Divalto", "produitIds": [produits["Divalto"]]})
verifier("Collaborateur Divalto cree avec son produit", s in (200, 201) and [p["nom"] for p in divalto["produits"]] == ["Divalto"], divalto)

s, sage = trouver_ou_creer(admin, {"role": "ROLE_USER", "email": "collab.sage@mediasoft.tn", "password": MDP,
                                   "nom": "Recette", "prenom": "Sage", "produitIds": [produits["SAGE"]]})
verifier("Collaborateur SAGE cree", s in (200, 201), sage)

s, client = trouver_ou_creer(admin, {"role": "ROLE_CLIENT", "email": "client.agora@agora.div", "password": MDP,
                                     "nom": "Garcia", "prenom": "Jean", "clientTiers": "C0000005"})
verifier("Client cree avec societe relue dans Divalto (AGORA BUREAU)", s in (200, 201) and client["societeNom"] == "AGORA BUREAU", client)

s, adm2 = trouver_ou_creer(admin, {"role": "ROLE_ADMIN", "email": "admin.recette@mediasoft.tn", "password": MDP,
                                   "nom": "Recette", "prenom": "Admin"})
verifier("Administrateur cree", s in (200, 201) and adm2["role"] == "ROLE_ADMIN", adm2)

# Script rejouable : on remet les comptes de recette dans leur etat initial
# (un passage precedent ajoute Divalto au collaborateur SAGE et desactive adm2).
appel("PUT", f"/users/{sage['id']}", admin, {"email": "collab.sage@mediasoft.tn", "nom": "Recette", "prenom": "Sage",
                                              "enabled": True, "produitIds": [produits["SAGE"]]})
appel("PUT", f"/users/{adm2['id']}", admin, {"email": "admin.recette@mediasoft.tn", "nom": "Recette",
                                              "prenom": "Admin", "enabled": True})

print("\n== Regles de validation ==")
s, r = appel("POST", "/users", admin, {"role": "ROLE_USER", "email": "COLLAB.DIVALTO@mediasoft.tn", "password": MDP,
                                        "nom": "X", "prenom": "Y", "produitIds": [produits["BI"]]})
verifier("Email deja utilise (casse differente) -> 409", s == 409, (s, r))

s, r = appel("POST", "/users", admin, {"role": "ROLE_USER", "email": "sans.produit@mediasoft.tn", "password": MDP,
                                        "nom": "X", "prenom": "Y", "produitIds": []})
verifier("Collaborateur sans produit -> 400", s == 400, (s, r))

s, r = appel("POST", "/users", admin, {"role": "ROLE_CLIENT", "email": "faux.client@x.tn", "password": MDP,
                                        "nom": "X", "prenom": "Y", "clientTiers": "ZZZ_INEXISTANT"})
verifier("Client avec societe inexistante -> 400", s == 400, (s, r))

s, r = appel("POST", "/users", admin, {"role": "ROLE_ADMIN", "email": "court@x.tn", "password": "123",
                                        "nom": "X", "prenom": "Y"})
verifier("Mot de passe trop court -> 400", s == 400, (s, r))

s, r = appel("POST", "/users", admin, {"role": "ROLE_ADMIN", "email": "pas-un-email", "password": MDP,
                                        "nom": "X", "prenom": "Y"})
verifier("Email invalide -> 400", s == 400, (s, r))

tok_div, tok_sage, tok_cli = login("collab.divalto@mediasoft.tn"), login("collab.sage@mediasoft.tn"), login("client.agora@agora.div")
s, _ = appel("GET", "/users", tok_div)
verifier("Un collaborateur n'accede pas a la gestion des utilisateurs -> 403", s == 403, s)

print("\n== Distribution par produit ==")
ticket_corps = {"intitule": "Recette distribution produit", "description": "Test", "produit": "Divalto",
                "version": "v10", "module": "Paie", "typeTicket": "Erreur", "importance": "Grave",
                "dateEstimee": "2026-09-30"}
s, ticket = appel("POST", "/tickets", tok_cli, ticket_corps)
verifier("Client cree un ticket Divalto", s == 201, (s, ticket))
tid = ticket["id"]
verifier("Ticket rattache a la societe du client + non assigne",
         ticket["client"] == "AGORA BUREAU" and ticket["collaborateurId"] is None, ticket)

ids_div = [t["id"] for t in appel("GET", "/tickets?pageSize=100", tok_div)[1]["items"]]
ids_sage = [t["id"] for t in appel("GET", "/tickets?pageSize=100", tok_sage)[1]["items"]]
verifier("Le collaborateur Divalto voit le ticket", tid in ids_div, ids_div)
verifier("Le collaborateur SAGE ne le voit PAS", tid not in ids_sage, ids_sage)
s, _ = appel("GET", f"/tickets/{tid}", tok_sage)
verifier("Acces direct par le collaborateur SAGE -> 404", s == 404, s)

s, r = appel("PUT", f"/tickets/{tid}", tok_cli, {"etat": "Clôturé"})
verifier("Le client ne peut pas modifier le traitement -> 403", s == 403, (s, r))

s, r = appel("PUT", f"/tickets/{tid}", tok_div, {"collaborateurId": sage["id"]})
verifier("Assigner a un collaborateur d'un autre produit -> 400", s == 400, (s, r))

s, r = appel("PUT", f"/tickets/{tid}", tok_div, {"etat": "En cours"})
verifier("Prise en charge automatique quand le collaborateur fait avancer le ticket",
         s == 200 and r["collaborateurId"] == divalto["id"], (s, r))

s, r = appel("GET", "/notifications?pageSize=5", tok_sage)
verifier("Historique du collaborateur SAGE sans les evenements Divalto",
         all(n["ticketId"] != tid for n in r["items"]), r)

print("\n== Modification de compte ==")
s, r = appel("PUT", f"/users/{sage['id']}", admin, {"email": "collab.sage@mediasoft.tn", "nom": "Recette",
                                                     "prenom": "Sage", "enabled": True,
                                                     "produitIds": [produits["SAGE"], produits["Divalto"]]})
verifier("Ajout du produit Divalto au collaborateur SAGE", s == 200 and len(r["produits"]) == 2, (s, r))
ids_sage = [t["id"] for t in appel("GET", "/tickets?pageSize=100", login("collab.sage@mediasoft.tn"))[1]["items"]]
verifier("Effet immediat : il voit maintenant le ticket Divalto (sans reconnexion)", tid in ids_sage, ids_sage)

me = appel("GET", "/users?recherche=test.admin@mediasoft.tn", admin)[1]["items"][0]
s, r = appel("PUT", f"/users/{me['id']}", admin, {"email": "test.admin@mediasoft.tn", "nom": me["nom"] or "Admin",
                                                   "prenom": "Test", "enabled": False})
verifier("Un admin ne peut pas desactiver son propre compte -> 400", s == 400, (s, r))

s, r = appel("PUT", f"/users/{adm2['id']}", admin, {"email": "admin.recette@mediasoft.tn", "nom": "Recette",
                                                     "prenom": "Admin", "enabled": False})
verifier("Desactivation d'un autre compte", s == 200 and r["enabled"] is False, (s, r))
s, _ = appel("POST", "/auth/login", corps={"username": "admin.recette@mediasoft.tn", "password": MDP})
verifier("Un compte desactive ne peut plus se connecter -> 401", s == 401, s)

print("\n== Suppression de comptes ==")
s, r = appel("POST", "/users/suppression", admin, {"ids": [me["id"]]})
verifier("Un admin ne peut pas supprimer son propre compte -> 400", s == 400, (s, r))

s, r = appel("POST", "/users/suppression", tok_div, {"ids": [sage["id"]]})
verifier("Un collaborateur ne peut pas supprimer de compte -> 403", s == 403, (s, r))

s, r = appel("POST", "/users/suppression", admin, {"ids": [999999]})
verifier("Compte inexistant -> 404", s == 404, (s, r))

# Compte temporaire : il prend en charge un ticket, puis il est supprime.
s, temp = trouver_ou_creer(admin, {"role": "ROLE_USER", "email": "collab.temporaire@mediasoft.tn", "password": MDP,
                                   "nom": "Temporaire", "prenom": "Compte", "produitIds": [produits["Divalto"]]})
tok_temp = login("collab.temporaire@mediasoft.tn")
s, t2 = appel("POST", "/tickets", tok_cli, ticket_corps)
s, pris = appel("PUT", f"/tickets/{t2['id']}", tok_temp, {"etat": "En cours"})
verifier("Le compte temporaire prend le ticket en charge", pris["collaborateurId"] == temp["id"], pris)

s, supp = appel("POST", "/users/suppression", admin, {"ids": [temp["id"]]})
verifier("Suppression du compte temporaire", s == 200 and supp["comptesSupprimes"] == 1, (s, supp))
verifier("Son ticket non cloture est remis dans la file", supp["ticketsRemisEnFile"] >= 1, supp)

s, apres = appel("GET", f"/tickets/{t2['id']}", admin)
verifier("Le ticket n'a plus de responsable", apres["collaborateurId"] is None and apres["collaborateur"] is None, apres)

s, _ = appel("GET", "/tickets", tok_temp)
verifier("Le jeton du compte supprime est refuse immediatement -> 401", s == 401, s)

s, _ = appel("GET", "/notifications?pageSize=5", tok_div)
verifier("L'equipe Divalto voit toujours l'historique du ticket", s == 200, s)

ok = sum(1 for c, _ in resultats if c)
print(f"\n== {ok}/{len(resultats)} verifications reussies ==")
