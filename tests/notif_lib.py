import json, urllib.request, urllib.error
API = "http://localhost:5008/api"
MDP = "Test2026!"
def appel(m, p, tok=None, corps=None):
    req = urllib.request.Request(API + p, data=json.dumps(corps).encode() if corps is not None else None, method=m)
    req.add_header("Content-Type", "application/json")
    if tok: req.add_header("Authorization", "Bearer " + tok)
    try:
        with urllib.request.urlopen(req) as r: return r.status, json.loads(r.read() or b"null")
    except urllib.error.HTTPError as e: return e.code, e.read().decode()
def login(email): return appel("POST", "/auth/login", corps={"username": email, "password": MDP})[1]["token"]
