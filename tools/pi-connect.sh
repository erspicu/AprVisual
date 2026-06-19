#!/bin/bash
# Fetch the Pi's latest reported LAN IP, then SSH in.
IP=$(curl -fsS "https://baxermux.org/service/ip.php" | python -c "import sys,json;print(json.load(sys.stdin).get('ip',''))" 2>/dev/null)
[ -z "$IP" ] && { echo "no IP reported yet"; exit 1; }
echo "Pi latest IP: $IP"
exec ssh -i ~/.ssh/aprvisual_pi "pi@$IP" "$@"
