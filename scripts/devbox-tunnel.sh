#!/usr/bin/env bash
# Keeps the devbox tunnel up.
#
# Ollama on the devbox listens on localhost only, so the game reaches it through this. A single
# dropped SSH session used to take the mind offline for good — the colony keeps running and every
# call falls back, which is the architecture working as designed and is not what anyone wanted.
# So: reconnect rather than exit.
#
#   bash scratch/devbox-tunnel.sh
#
# ServerAliveInterval makes a dead peer show up in seconds rather than hanging a half-open socket,
# and ExitOnForwardFailure stops a reconnect from silently coming up without the forward.
while true; do
  ssh -o BatchMode=yes \
      -o ExitOnForwardFailure=yes \
      -o ServerAliveInterval=15 \
      -o ServerAliveCountMax=3 \
      -N -L 127.0.0.1:11436:127.0.0.1:11434 devbox
  echo "$(date '+%H:%M:%S') tunnel dropped ($?) — reconnecting in 3s"
  sleep 3
done
