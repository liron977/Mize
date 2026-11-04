#!/usr/bin/env python3
"""
Show what is in the service's SQLite database while the docker compose stack is running.

The database lives in a Docker volume inside the API container (/data/reservations.db, WAL mode) and the app image has no
sqlite3 CLI, so this copies the database files out (main file plus -wal/-shm if present, so recent writes are included)
into a temp folder and reads that COPY. It never touches the live database.

    python tools/show_db.py                           # latest rows + supplier stats (can be a lot after load tests)
    python tools/show_db.py --supplier demo-14450     # only suppliers whose id starts with this (case-insensitive)
    python tools/show_db.py --supplier demo --limit 50
    python tools/show_db.py --supplier acme1 --watch 2   # LIVE: refresh every 2 s; run your curls in another terminal
    python tools/show_db.py --container mize-api-2-1

Needs: Python 3 and the Docker CLI. Standard library only.
"""
import argparse
import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time
from datetime import datetime


def run(*cmd):
    return subprocess.run(cmd, capture_output=True, text=True)


def copy_db(container, dest):
    listing = run("docker", "exec", container, "ls", "/data")
    if listing.returncode != 0:
        raise RuntimeError(f"Cannot list /data in container '{container}': {listing.stderr.strip() or listing.stdout.strip()}. "
                           "Is the stack running? (docker compose up -d)")
    files = [f for f in listing.stdout.split() if f.startswith("reservations.db")]
    if "reservations.db" not in files:
        raise RuntimeError(f"No reservations.db in {container}:/data (found: {listing.stdout.split()}). "
                           "Has the API received a request yet?")
    for name in files:  # reservations.db, reservations.db-wal, reservations.db-shm
        cp = run("docker", "cp", f"{container}:/data/{name}", os.path.join(dest, name))
        if cp.returncode != 0:
            raise RuntimeError(f"docker cp failed for {name}: {cp.stderr.strip()}")
    return files


def table(headers, rows):
    if not rows:
        return ["  (no rows)"]
    cells = [[str(c) for c in r] for r in rows]
    widths = [max(len(h), *(len(r[i]) for r in cells)) for i, h in enumerate(headers)]
    out = ["  " + "  ".join(h.ljust(w) for h, w in zip(headers, widths)),
           "  " + "  ".join("-" * w for w in widths)]
    out += ["  " + "  ".join(c.ljust(w) for c, w in zip(r, widths)) for r in cells]
    return out


def snapshot(container, prefix, limit):
    """One consistent read of a copy of the database, as printable lines."""
    tmp = tempfile.mkdtemp(prefix="mize-showdb-")
    try:
        files = copy_db(container, tmp)
        con = sqlite3.connect(os.path.join(tmp, "reservations.db"))
        try:
            like = prefix.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "%"
            total = con.execute("SELECT COUNT(*) FROM Reservations").fetchone()[0]
            matching = con.execute("SELECT COUNT(*) FROM Reservations WHERE SupplierId LIKE ? ESCAPE '\\'", (like,)).fetchone()[0]

            lines = [f"Copied from {container}:/data  ({', '.join(files)})",
                     f"Reservations in the database: {total}   matching '{prefix}*': {matching}", "",
                     f"RESERVATIONS (newest {limit} by creation time; price is stored in cents, shown here in units)"]
            rows = con.execute(
                "SELECT SupplierId, ReservationId, RoomId, substr(CheckIn,1,10), substr(CheckOut,1,10), PriceCents, "
                "substr(UpdatedAtUtc,1,19), substr(CreatedAtUtc,1,19) "
                "FROM Reservations WHERE SupplierId LIKE ? ESCAPE '\\' ORDER BY CreatedAtUtc DESC, SupplierId, ReservationId LIMIT ?",
                (like, limit)).fetchall()
            lines += table(["supplier", "reservation", "room", "check-in", "check-out", "price", "updatedAtUtc", "createdAtUtc"],
                           [(r[0], r[1], r[2], r[3], r[4], f"{r[5] / 100:.2f}", r[6], r[7]) for r in rows])

            lines += ["", "SUPPLIER STATS (lifetime counters, same numbers as GET /api/reservations/stats/{supplierId})"]
            stats = con.execute(
                "SELECT SupplierId, Ingested, Unchanged, Stale, Throttled FROM SupplierStats WHERE SupplierId LIKE ? ESCAPE '\\' "
                "ORDER BY SupplierId LIMIT 50", (like,)).fetchall()
            lines += table(["supplier", "ingested", "unchanged", "stale", "throttled"], stats)
            return lines
        finally:
            con.close()
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--supplier", default="", help="only suppliers whose id starts with this (case-insensitive)")
    ap.add_argument("--limit", type=int, default=20, help="max reservation rows to show (default 20)")
    ap.add_argument("--container", default="mize-api-1-1", help="API container that mounts the database volume")
    ap.add_argument("--watch", type=float, default=0, metavar="SECONDS",
                    help="keep refreshing every SECONDS (Ctrl+C to stop); run your curls in another terminal")
    args = ap.parse_args()
    prefix = args.supplier.strip().lower()  # the service stores supplier ids trimmed and lower-cased

    if args.watch <= 0:
        try:
            print("\n".join(snapshot(args.container, prefix, args.limit)))
        except RuntimeError as e:
            sys.exit(str(e))
        return

    interactive = sys.stdout.isatty()
    if interactive and os.name == "nt":
        os.system("")  # enables ANSI escape codes in the Windows console
    try:
        while True:
            try:
                lines = snapshot(args.container, prefix, args.limit)
            except RuntimeError as e:
                lines = [f"(waiting) {e}"]
            stamp = f"[{datetime.now():%H:%M:%S}] refreshing every {args.watch:g}s for supplier '{prefix}*'. Ctrl+C to stop."
            if interactive:
                sys.stdout.write("\033[2J\033[H")  # clear the screen, cursor to top
            else:
                print("=" * 100)                 # redirected output: separate frames instead of clearing
            print(stamp + "\n")
            print("\n".join(lines), flush=True)
            time.sleep(args.watch)
    except KeyboardInterrupt:
        print("\nstopped.")


if __name__ == "__main__":
    main()
