"""Recette : piece jointe d'un ticket (upload, telechargement, limites)."""
import urllib.request, urllib.error, uuid, zlib, struct
from notif_lib import *
res = []
def ok(lib, cond, det=""):
    cond = bool(cond); res.append(cond); print(("  OK    | " if cond else "  ECHEC | ") + lib + ("" if cond else "  -> " + str(det)))

def envoyer_fichier(chemin_api, tok, nom, contenu, ctype):
    """POST multipart/form-data avec un champ 'fichier'."""
    front = "----recette" + uuid.uuid4().hex
    corps = ((f'--{front}\r\nContent-Disposition: form-data; name="fichier"; filename="{nom}"\r\n'
              f'Content-Type: {ctype}\r\n\r\n').encode() + contenu + f'\r\n--{front}--\r\n'.encode())
    req = urllib.request.Request(API + chemin_api, data=corps, method="POST")
    req.add_header("Content-Type", f"multipart/form-data; boundary={front}")
    req.add_header("Authorization", "Bearer " + tok)
    try:
        with urllib.request.urlopen(req) as r: return r.status, json.loads(r.read())
    except urllib.error.HTTPError as e: return e.code, e.read().decode()

def telecharger(tid, tok):
    req = urllib.request.Request(f"{API}/tickets/{tid}/fichier")
    req.add_header("Authorization", "Bearer " + tok)
    try:
        with urllib.request.urlopen(req) as r:
            return r.status, r.read(), r.headers.get("Content-Type"), r.headers.get("Content-Disposition")
    except urllib.error.HTTPError as e: return e.code, e.read(), None, None

def petit_png():
    """PNG 1x1 valide, genere sans dependance."""
    def bloc(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data))
    ihdr = struct.pack(">IIBBBBB", 1, 1, 8, 2, 0, 0, 0)
    idat = zlib.compress(b"\x00\x10\x20\x30")
    return b"\x89PNG\r\n\x1a\n" + bloc(b"IHDR", ihdr) + bloc(b"IDAT", idat) + bloc(b"IEND", b"")

admin = login("test.admin@mediasoft.tn")
prod = {p["nom"]: p["productId"] for p in appel("GET", "/produits", admin)[1]}
for c in [
  {"role": "ROLE_USER", "email": "val.ammar@mediasoft.tn", "password": MDP, "nom": "Guitouni", "prenom": "Ammar", "produitIds": [prod["Divalto"]]},
  {"role": "ROLE_CLIENT", "email": "val.client@agora.div", "password": MDP, "nom": "Moult", "prenom": "Philippine", "clientTiers": "C0000005"}]:
    appel("POST", "/users", admin, c)
client = login("val.client@agora.div")
ammar = login("val.ammar@mediasoft.tn")

print("\n== Upload a la creation ==")
s, t = appel("POST", "/tickets", client, {"intitule": "Erreur affichee (capture jointe)", "description": "Voir la capture.",
            "produit": "Divalto", "version": "v10", "module": "Gestion", "typeTicket": "Erreur", "importance": "Mineur"})
tid = t["id"]
png = petit_png()
s, t = envoyer_fichier(f"/tickets/{tid}/fichier", client, "capture écran.png", png, "image/png")
ok("Le client joint une image a son ticket", s == 200 and t["fichierNom"] == "capture écran.png", (s, t))
ok("Type et taille exposes dans le ticket", t["fichierType"] == "image/png" and t["fichierTaille"] == len(png), t)

s, r = envoyer_fichier(f"/tickets/{tid}/fichier", client, "deux.png", png, "image/png")
ok("Un second fichier sur le meme ticket -> refuse", s == 400 and "déjà joint" in str(r), (s, r))

print("\n== Telechargement ==")
s, corps, ctype, dispo = telecharger(tid, ammar)
ok("Le collaborateur telecharge la piece jointe", s == 200 and corps == png, (s, len(corps)))
ok("Type MIME et nom d'origine renvoyes", ctype == "image/png" and "capture" in (dispo or ""), (ctype, dispo))
s, corps, *_ = telecharger(tid, admin)
ok("L'admin y accede aussi", s == 200 and corps == png, s)
s, corps, *_ = telecharger(tid, client)
ok("Le client (proprietaire) y accede aussi", s == 200 and corps == png, s)

sage = None
try: sage = login("act.sage@mediasoft.tn")
except Exception: pass
if sage:
    s, *_ = telecharger(tid, sage)
    ok("Collaborateur d'un autre produit -> 404 (ticket hors perimetre)", s == 404, s)

print("\n== Limites ==")
s, t2 = appel("POST", "/tickets", client, {"intitule": "Sans fichier", "description": "x",
            "produit": "Divalto", "version": "v10", "module": "Gestion", "typeTicket": "Question", "importance": "Mineur"})
s, corps, *_ = telecharger(t2["id"], client)
ok("Ticket sans piece jointe -> 404", s == 404, s)
s, r = envoyer_fichier(f"/tickets/{t2['id']}/fichier", client, "vide.txt", b"", "text/plain")
ok("Fichier vide -> refuse", s == 400, (s, r))
s, r = envoyer_fichier(f"/tickets/{t2['id']}/fichier", client, "gros.bin", b"\x00" * (10 * 1024 * 1024 + 1), "application/octet-stream")
ok("Fichier > 10 Mo -> refuse", s in (400, 413), (s, str(r)[:80]))
s, t2 = envoyer_fichier(f"/tickets/{t2['id']}/fichier", client, "notes.txt", "Contenu de test métier.".encode("utf-8"), "text/plain")
ok("N'importe quel type de fichier est accepte (txt)", s == 200 and t2["fichierNom"] == "notes.txt", (s, t2))

# Suppression admin : le ticket et son fichier disparaissent
s, _ = appel("DELETE", f"/tickets/{tid}", admin)
appel("DELETE", f"/tickets/{t2['id']}", admin)
ok("Suppression du ticket (le fichier disque est nettoye)", s == 204, s)

print(f"\n== {sum(res)}/{len(res)} verifications reussies ==")
