"""Recette : retour arriere interdit + capture jointe aux decisions de test."""
import urllib.request, urllib.error, uuid, zlib, struct
from notif_lib import *
res = []
def ok(lib, cond, det=""):
    cond = bool(cond); res.append(cond); print(("  OK    | " if cond else "  ECHEC | ") + lib + ("" if cond else "  -> " + str(det)))

def envoyer_fichier(chemin_api, tok, nom, contenu, ctype):
    front = "----recette" + uuid.uuid4().hex
    corps = ((f'--{front}\r\nContent-Disposition: form-data; name="fichier"; filename="{nom}"\r\n'
              f'Content-Type: {ctype}\r\n\r\n').encode() + contenu + f'\r\n--{front}--\r\n'.encode())
    req = urllib.request.Request(API + chemin_api, data=corps, method="POST")
    req.add_header("Content-Type", f"multipart/form-data; boundary={front}")
    req.add_header("Authorization", "Bearer " + tok)
    try:
        with urllib.request.urlopen(req) as r: return r.status, (json.loads(r.read()) if r.status != 204 else None)
    except urllib.error.HTTPError as e: return e.code, e.read().decode()

def telecharger(chemin, tok):
    req = urllib.request.Request(API + chemin)
    req.add_header("Authorization", "Bearer " + tok)
    try:
        with urllib.request.urlopen(req) as r: return r.status, r.read()
    except urllib.error.HTTPError as e: return e.code, e.read()

def petit_png():
    def bloc(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data))
    ihdr = struct.pack(">IIBBBBB", 1, 1, 8, 2, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + bloc(b"IHDR", ihdr) + bloc(b"IDAT", zlib.compress(b"\x00\x10\x20\x30")) + bloc(b"IEND", b"")

admin = login("test.admin@mediasoft.tn")
prod = {p["nom"]: p["productId"] for p in appel("GET", "/produits", admin)[1]}
for c in [
  {"role": "ROLE_USER", "email": "val.ammar@mediasoft.tn", "password": MDP, "nom": "Guitouni", "prenom": "Ammar", "produitIds": [prod["Divalto"]]},
  {"role": "ROLE_DEV", "email": "dev.karim@mediasoft.tn", "password": MDP, "nom": "Trabelsi", "prenom": "Karim", "produitIds": [prod["Divalto"]]},
  {"role": "ROLE_CLIENT", "email": "val.client@agora.div", "password": MDP, "nom": "Moult", "prenom": "Philippine", "clientTiers": "C0000005"}]:
    appel("POST", "/users", admin, c)
ammar = login("val.ammar@mediasoft.tn")
dev = login("dev.karim@mediasoft.tn")
client = login("val.client@agora.div")
did = appel("GET", "/users?recherche=dev.karim", admin)[1]["items"][0]["id"]

s, t = appel("POST", "/tickets", client, {"intitule": "Retour arriere et captures", "description": "Recette",
            "produit": "Divalto", "version": "v10", "module": "ERP", "typeTicket": "Erreur", "importance": "Grave"})
tid = t["id"]

print("\n== Retour arriere interdit (sauf admin) ==")
appel("PUT", f"/tickets/{tid}", ammar, {"etat": "En cours"})
s, r = appel("PUT", f"/tickets/{tid}", ammar, {"etat": "Ouvert"})
ok("User : En cours -> Ouvert refuse (400)", s == 400 and "administrateur" in str(r), (s, r))
s, t = appel("PUT", f"/tickets/{tid}", admin, {"etat": "Ouvert"})
ok("Admin : En cours -> Ouvert autorise", s == 200 and t["etat"] == "Ouvert", (s, t.get("etat") if isinstance(t, dict) else t))
appel("PUT", f"/tickets/{tid}", ammar, {"collaborateurId": did, "etat": "En développement", "commentaire": "A toi Karim."})
s, r = appel("PUT", f"/tickets/{tid}", ammar, {"etat": "En cours"})
ok("User : En développement -> En cours refuse", s == 400, (s, r))
appel("PUT", f"/tickets/{tid}", dev, {"etat": "Test interne", "commentaire": "A tester."})
s, r = appel("PUT", f"/tickets/{tid}", dev, {"etat": "En développement", "commentaire": "je reviens"})
ok("Dev : Test interne -> En développement refuse (retour arriere)", s == 400, (s, r))
s, r = appel("PUT", f"/tickets/{tid}", ammar, {"etat": "En cours", "commentaire": "x"})
ok("User : Test interne -> En cours refuse", s == 400, (s, r))
s, t = appel("PUT", f"/tickets/{tid}", ammar, {"etat": "En développement", "commentaire": "Le filtre date ne marche pas."})
ok("User : rejet du test (Test interne -> En développement) reste permis", s == 200 and t["etat"] == "En développement", (s, t))

print("\n== Capture jointe au rejet ==")
png = petit_png()
s, r = envoyer_fichier(f"/tickets/{tid}/suivi/fichier", ammar, "rejet filtre.png", png, "image/png")
ok("La capture s'attache a la derniere action d'Ammar", s == 204, (s, r))
s, suivi = appel("GET", f"/tickets/{tid}/suivi", dev)
evt = next(e for e in reversed(suivi) if e["parId"] and e["etat"] == "En développement")
ok("Le suivi expose la capture (nom + type)", evt["fichierNom"] == "rejet filtre.png" and evt["fichierType"] == "image/png", evt)
ok("Le nom de stockage n'est PAS expose", "fichierStockage" not in evt, list(evt.keys()))
s, corps = telecharger(f"/tickets/{tid}/suivi/{evt['id']}/fichier", dev)
ok("Le dev telecharge la capture du rejet", s == 200 and corps == png, s)
s, r = envoyer_fichier(f"/tickets/{tid}/suivi/fichier", ammar, "deux.png", png, "image/png")
ok("Une seconde capture sur la meme action -> refuse", s == 400, (s, r))

print("\n== Capture jointe a la validation (vers le client) ==")
appel("PUT", f"/tickets/{tid}", dev, {"etat": "Test interne", "commentaire": "Filtre corrige."})
s, t = appel("PUT", f"/tickets/{tid}", ammar, {"etat": "En attente de validation", "commentaire": "Test concluant, voici la preuve."})
s, r = envoyer_fichier(f"/tickets/{tid}/suivi/fichier", ammar, "preuve test.png", png, "image/png")
ok("Capture jointe a la validation", s == 204, (s, r))
s, suivi = appel("GET", f"/tickets/{tid}/suivi", client)
evt = suivi[-1]
ok("Le client voit la capture dans le suivi", evt["fichierNom"] == "preuve test.png", evt)
s, corps = telecharger(f"/tickets/{tid}/suivi/{evt['id']}/fichier", client)
ok("Le client telecharge la preuve du test", s == 200 and corps == png, s)
s, r = envoyer_fichier(f"/tickets/{tid}/suivi/fichier", client, "c.png", png, "image/png")
ok("Un client ne peut pas joindre de capture au suivi -> 403", s == 403, s)

appel("DELETE", f"/tickets/{tid}", admin)
sortants = telecharger(f"/tickets/{tid}/suivi/{evt['id']}/fichier", admin)
ok("Apres suppression du ticket, la capture n'est plus servie", sortants[0] == 404, sortants[0])
print(f"\n== {sum(res)}/{len(res)} verifications reussies ==")
