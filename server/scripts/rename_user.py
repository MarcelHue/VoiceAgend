#!/usr/bin/env python3
"""Einmal-Werkzeug: Benutzer umbenennen (z. B. den Default-Admin).

Bewusst KEIN API-/UI-Feature — nur ein separates Skript gegen die SQLite-DB.
Nur stdlib (sqlite3), läuft daher auch im laufenden Container ohne App-Imports.
Der Username ist `unique`; ein Rename ändert nichts an Passwort/Rechten/API-Keys.

Verwendung (im Container):
    python scripts/rename_user.py admin Marcel
    python scripts/rename_user.py Marcel        # Quelle defaultet auf "admin"

Der DB-Pfad kommt aus VOICEAGEND_DATA_DIR (Default /data), wie der Server.
Hinweis: Nach dem Rename werden bestehende Login-Tokens ungültig (das JWT `sub`
trägt den alten Namen) — einfach neu einloggen.
"""
import os
import sqlite3
import sys
from pathlib import Path


def main() -> int:
    args = sys.argv[1:]
    if not args or args[0] in ("-h", "--help"):
        print(__doc__)
        return 0
    if len(args) == 1:
        old, new = "admin", args[0]
    else:
        old, new = args[0], args[1]

    data_dir = Path(os.getenv("VOICEAGEND_DATA_DIR", "/data"))
    db_path = data_dir / "voiceagend.db"
    if not db_path.exists():
        print(f"DB nicht gefunden: {db_path}", file=sys.stderr)
        return 2

    con = sqlite3.connect(str(db_path))
    try:
        cur = con.cursor()
        if cur.execute("SELECT 1 FROM users WHERE username = ?", (old,)).fetchone() is None:
            print(f"Benutzer '{old}' existiert nicht.", file=sys.stderr)
            return 3
        if old != new and cur.execute(
            "SELECT 1 FROM users WHERE username = ?", (new,)
        ).fetchone() is not None:
            print(f"Benutzername '{new}' ist bereits vergeben.", file=sys.stderr)
            return 4
        cur.execute("UPDATE users SET username = ? WHERE username = ?", (new, old))
        con.commit()
        print(f"OK: '{old}' -> '{new}' ({cur.rowcount} Zeile aktualisiert).")
        return 0
    finally:
        con.close()


if __name__ == "__main__":
    raise SystemExit(main())
