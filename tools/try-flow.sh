#!/usr/bin/env bash
# Walks through the whole flow against a running stack and prints what comes back.
#   bash tools/try-flow.sh                 # dedup / update / stale / validation / stats
#   bash tools/try-flow.sh --throttle      # also sends 105 requests from one supplier to show the 429
#   bash tools/try-flow.sh --malformed     # also sends badly formatted payloads and shows the answer to each
#   BASE_URL=http://localhost:8080 bash tools/try-flow.sh
# Then look at the rows with:  python tools/show_db.py --supplier <the supplier printed below>
# (use Git Bash or WSL on Windows; plain PowerShell's `curl` is a different program)
set -u
has_flag() { local f="$1"; shift; for a in "$@"; do [ "$a" = "$f" ] && return 0; done; return 1; }
BASE_URL="${BASE_URL:-http://localhost:8080}"
S="demo-$(date +%s | tail -c 6)"      # a fresh supplier each run, so the numbers below are always the same
echo "supplier: $S"

body() { # reservationId roomId price updatedAtUtc
  printf '{"supplierId":"%s","reservationId":"%s","roomId":"%s","checkIn":"2099-08-01T00:00:00Z","checkOut":"2099-08-03T00:00:00Z","price":%s,"updatedAtUtc":"%s"}' "$S" "$1" "$2" "$3" "$4"
}
post() { curl -s -w "   [HTTP %{http_code}]\n" -X POST "$BASE_URL/api/reservations/ingest" -H "Content-Type: application/json" -d "$1"; }

echo; echo "1) new reservation                              -> expect 201 Created";             post "$(body R1 room1 450.00 2026-07-01T10:15:00Z)"
echo "2) exact resend (duplicate)                     -> expect 200 Unchanged, nothing written"; post "$(body R1 room1 450.00 2026-07-01T10:15:00Z)"
echo "3) same details, only a newer timestamp         -> expect 200 Unchanged";                  post "$(body R1 room1 450.00 2026-07-05T09:00:00Z)"
echo "4) price changed, newer timestamp               -> expect 200 Updated";                    post "$(body R1 room1 400.00 2026-07-10T09:00:00Z)"
echo "5) different price but an OLDER timestamp       -> expect 200 Stale, ignored";             post "$(body R1 room1 999.00 2026-06-01T09:00:00Z)"
echo "6) invalid payload (missing fields)             -> expect 400 with per-field errors"
curl -s -w "   [HTTP %{http_code}]\n" -X POST "$BASE_URL/api/reservations/ingest" -H "Content-Type: application/json" -d "{\"supplierId\":\"$S\",\"reservationId\":\"BAD\"}"
echo "7) stats                                        -> expect ingested 2, unchanged 2, stale 1"
curl -s "$BASE_URL/api/reservations/stats/$S"; echo

if has_flag --throttle "$@"; then
  T="throttle-$(date +%s | tail -c 6)"
  echo; echo "8) 105 requests from one supplier ($T)   -> expect 100 x 201 then 429s"
  codes=""
  for i in $(seq 1 105); do
    codes="$codes$(curl -s -o /dev/null -w '%{http_code} ' -X POST "$BASE_URL/api/reservations/ingest" -H 'Content-Type: application/json' \
      -d "{\"supplierId\":\"$T\",\"reservationId\":\"R$i\",\"roomId\":\"room1\",\"checkIn\":\"2099-08-01T00:00:00Z\",\"checkOut\":\"2099-08-03T00:00:00Z\",\"price\":100,\"updatedAtUtc\":\"2026-07-01T10:00:00Z\"}")"
  done
  echo "$codes" | tr ' ' '\n' | grep -v '^$' | sort | uniq -c | sed 's/^/   /'
  echo "   the next request, with headers:"
  curl -si -X POST "$BASE_URL/api/reservations/ingest" -H 'Content-Type: application/json' \
    -d "{\"supplierId\":\"$T\",\"reservationId\":\"R106\",\"roomId\":\"room1\",\"checkIn\":\"2099-08-01T00:00:00Z\",\"checkOut\":\"2099-08-03T00:00:00Z\",\"price\":100,\"updatedAtUtc\":\"2026-07-01T10:00:00Z\"}" \
    | grep -iE "^HTTP|^retry-after|title|detail" | sed 's/^/   /'
  echo "   stats:"; curl -s "$BASE_URL/api/reservations/stats/$T"; echo
  echo; echo "see the rows:  python tools/show_db.py --supplier $T --limit 5"
fi

if has_flag --malformed "$@"; then
  M="malformed-$(date +%s | tail -c 5)"
  echo; echo "9) badly formatted payloads: each one is rejected with an explanation and NOTHING is stored"
  bad() { # label, content-type, body
    printf "   %-34s" "$1"
    curl -s -o /tmp/try-flow-resp.txt -w "HTTP %{http_code}  " -X POST "$BASE_URL/api/reservations/ingest" -H "Content-Type: $2" --data-binary "$3"
    head -c 230 /tmp/try-flow-resp.txt | tr '\n' ' '; echo
  }
  rest='"roomId":"room1","checkIn":"2026-08-01T00:00:00Z","checkOut":"2026-08-03T00:00:00Z","price":450,"updatedAtUtc":"2026-07-01T10:15:00Z"'
  bad "date without an offset"        application/json "{\"supplierId\":\"$M\",\"reservationId\":\"b1\",\"roomId\":\"r\",\"checkIn\":\"2026-08-01T00:00:00\",\"checkOut\":\"2026-08-03T00:00:00Z\",\"price\":450,\"updatedAtUtc\":\"2026-07-01T10:15:00Z\"}"
  bad "date only"                     application/json "{\"supplierId\":\"$M\",\"reservationId\":\"b2\",\"roomId\":\"r\",\"checkIn\":\"2026-08-01\",\"checkOut\":\"2026-08-03T00:00:00Z\",\"price\":450,\"updatedAtUtc\":\"2026-07-01T10:15:00Z\"}"
  bad "date as an epoch number"       application/json "{\"supplierId\":\"$M\",\"reservationId\":\"b3\",\"roomId\":\"r\",\"checkIn\":1785000000,\"checkOut\":\"2026-08-03T00:00:00Z\",\"price\":450,\"updatedAtUtc\":\"2026-07-01T10:15:00Z\"}"
  bad "price as a string"             application/json "{\"supplierId\":\"$M\",\"reservationId\":\"b4\",\"roomId\":\"r\",\"checkIn\":\"2026-08-01T00:00:00Z\",\"checkOut\":\"2026-08-03T00:00:00Z\",\"price\":\"450\",\"updatedAtUtc\":\"2026-07-01T10:15:00Z\"}"
  bad "supplierId is a number"        application/json "{\"supplierId\":123,\"reservationId\":\"b5\",$rest}"
  bad "several things wrong at once"  application/json "{\"supplierId\":\"$M\",\"reservationId\":true,\"roomId\":\"r\",\"checkIn\":\"nope\",\"checkOut\":5,\"price\":-1,\"updatedAtUtc\":null}"
  bad "body is a JSON array"          application/json "[]"
  bad "body is empty"                 application/json ""
  bad "body is not JSON"              application/json "hello"
  bad "wrong Content-Type"            text/plain       "{\"supplierId\":\"$M\",\"reservationId\":\"b6\",$rest}"
  bad "supplierId 200 characters"     application/json "{\"supplierId\":\"$(printf 'x%.0s' $(seq 1 200))\",\"reservationId\":\"b7\",$rest}"
  echo "   stats for $M (expect all zeros, nothing was stored): $(curl -s "$BASE_URL/api/reservations/stats/$M")"
fi

echo; echo "see the rows:  python tools/show_db.py --supplier $S"
