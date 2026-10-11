#!/usr/bin/env bash
# An isolated, authorized loopback desktop for interoperability tests, not a shipped RDP implementation.
set -euo pipefail
sudo apt-get update -qq
sudo apt-get install -y --no-install-recommends xrdp xorgxrdp xterm x11-xserver-utils xclip xinput xfonts-base python3-pil
sudo useradd -m -s /bin/bash rdptest
password="$(openssl rand -hex 16)"
echo "::add-mask::$password"
echo "rdptest:$password" | sudo chpasswd
echo "RDP_TEST_PASSWORD=$password" >> "$GITHUB_ENV"
sudo openssl req -x509 -newkey rsa:2048 -nodes -keyout /etc/xrdp/remote.key -out /etc/xrdp/remote.crt -days 1 -subj /CN=localhost -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1'
sudo chown xrdp:xrdp /etc/xrdp/remote.key /etc/xrdp/remote.crt
sudo chmod 600 /etc/xrdp/remote.key
sudo sed -i 's/^security_layer=.*/security_layer=tls/;s|^certificate=.*|certificate=/etc/xrdp/remote.crt|;s|^key_file=.*|key_file=/etc/xrdp/remote.key|;s/^port=3389/port=tcp:\/\/127.0.0.1:3389/' /etc/xrdp/xrdp.ini
printf '#!/bin/sh\ncd "$HOME"\nxsetroot -solid "#14283c"\nxinput test-xi2 --root > "$HOME/input-events.log" 2>&1 &\nexec xterm -geometry 90x30+20+20 -bg "#173956" -fg white -title "RemoteSpace interoperability" -e /bin/bash --noprofile --norc\n' | sudo tee /home/rdptest/.xsession > /dev/null
sudo chmod +x /home/rdptest/.xsession
sudo chown rdptest:rdptest /home/rdptest/.xsession
echo "RDP_TEST_PIN=$(openssl x509 -in /etc/xrdp/remote.crt -fingerprint -sha256 -noout | cut -d= -f2 | tr -d :)" >> "$GITHUB_ENV"
# Match the IPv4-only loopback binding; localhost may select ::1.
echo 'RDP_TEST_HOST=127.0.0.1' >> "$GITHUB_ENV"
sudo systemctl restart xrdp-sesman xrdp
# systemctl can return before the daemon has bound its listening socket.
# Probe only our loopback fixture, with a deadline and a nonzero failure exit.
python3 - <<'PY'
import socket
import time

deadline = time.monotonic() + 20
while True:
    try:
        with socket.create_connection(('127.0.0.1', 3389), timeout=1):
            print('PASS isolated xrdp fixture is accepting connections')
            break
    except OSError:
        if time.monotonic() >= deadline:
            raise SystemExit('xrdp did not become ready within the startup deadline')
        time.sleep(0.1)
PY
