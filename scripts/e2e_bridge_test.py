"""End-to-end bridge API test mirroring Brembana BridgeApiClient flow."""
import sys

import requests

BASE = "http://127.0.0.1:8000"
USERNAME = "tablet"
PASSWORD = "secret"


def main() -> int:
    session = requests.Session()

    r = session.post(
        f"{BASE}/api/bridge/auth/login/",
        json={"username": USERNAME, "password": PASSWORD},
        timeout=30,
    )
    print("login", r.status_code, r.text[:200])
    if r.status_code != 200 or not r.json().get("ok"):
        return 1

    r = session.get(f"{BASE}/api/bridge/attivita/", timeout=60)
    print("attivita", r.status_code)
    if r.status_code != 200:
        print(r.text[:500])
        return 1
    attivita = r.json()["data"]
    print("activity count", len(attivita))
    if not attivita:
        print("no activities in dataset")
        return 1

    first = attivita[0]
    print(
        "first activity",
        first.get("activity_id"),
        "orm_id",
        first.get("orm_id"),
        "plant",
        first.get("plant"),
    )

    r = session.get(f"{BASE}/api/bridge/dataset/", timeout=120)
    if r.status_code != 200:
        print("dataset failed", r.status_code, r.text[:500])
        return 1
    dataset = r.json()["data"]
    inventario = dataset.get("inventario", {})
    print("dataset activities", len(dataset.get("attivita", [])), "inventario keys", list(inventario.keys()))

    raw = first.get("resources") or ""
    uids = [u.strip() for u in raw.replace(",", ";").split(";") if u.strip()]
    tipo_lookup: dict[str, str] = {}
    for tipo, ids in (first.get("resources_by_tipo") or {}).items():
        for uid in ids:
            tipo_lookup[uid] = tipo
    components = [(uid, tipo_lookup.get(uid, "")) for uid in uids]
    print("components", len(components), components[:8])

    pk = first["orm_id"]
    if components and components[0][1]:
        uid, tipo = components[0]
        r = session.post(
            f"{BASE}/api/bridge/attivita/{pk}/scan/",
            json={"tipo": tipo, "codice": uid},
            timeout=30,
        )
        print("scan", r.status_code, r.text[:200])
        if r.status_code != 200 or r.json().get("data", {}).get("esito") != "corrispondente":
            print("scan of the assigned component should be 'corrispondente'")
            return 1

    r = session.put(
        f"{BASE}/api/bridge/attivita/{pk}/update/",
        json={"actual_start": "2026-06-12"},
        timeout=30,
    )
    print("update", r.status_code, r.text[:200])
    body = r.json()
    if r.status_code == 409:
        # Rifiuto: il testo per l'operatore è in "error"; "conflicts" (pezzo in uso in
        # un'attività in corso) o "missing_resources" (kit minimo) dicono il perché.
        kind = "conflicts" if body.get("conflicts") else "missing_resources" if body.get("missing_resources") else "?"
        print("start refused", kind, body.get("error"))
        return 1
    if r.status_code != 200 or not body.get("ok"):
        return 1
    if body.get("warning_message"):
        print("start warning", body["warning_message"], body.get("warnings"))

    r = session.get(f"{BASE}/api/bridge/attivita/{pk}/", timeout=30)
    actual_start = r.json()["data"].get("actual_start")
    print("verified actual_start", actual_start)
    if actual_start != "2026-06-12":
        print("actual_start mismatch")
        return 1

    print("E2E OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
