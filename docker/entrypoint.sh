#!/bin/sh
# Runs Krakarr as PUID:PGID (linuxserver.io style) so files in mounted volumes get the right owner.
set -e

PUID="${PUID:-1000}"
PGID="${PGID:-1000}"
umask "${UMASK:-002}"

# Only /config and /data are handed to PUID. /app stays root-owned and read-only (the Dockerfile checks it): never chown it.
# Needs only the CHOWN, SETUID and SETGID capabilities, so the container can run with cap_drop: [ALL] plus those three.
if [ "$(id -u)" = "0" ]; then
    mkdir -p /config /data/downloads
    chown -R "$PUID:$PGID" /config
    # Only fix top-level dirs; never recurse into potentially huge media trees.
    chown "$PUID:$PGID" /data /data/downloads 2>/dev/null || true
    export HOME=/config
    exec setpriv --reuid="$PUID" --regid="$PGID" --clear-groups dotnet /app/Krakarr.Host.dll "$@"
fi

exec dotnet /app/Krakarr.Host.dll "$@"
