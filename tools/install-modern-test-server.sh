#!/usr/bin/env bash
# Ephemeral CI fixture only. This external server is NOT part of the shipped client.
# Pinned official release archives (including their published SHA-256 digests).
set -euo pipefail
[[ "${GITHUB_ACTIONS:-}" == true && -n "${GITHUB_ENV:-}" ]] || { echo 'Run only on the disposable CI runner.' >&2; exit 1; }
sudo systemctl stop xrdp xrdp-sesman
sudo apt-get install -y --no-install-recommends build-essential pkg-config autoconf automake libtool nasm systemd-dev \
    libpam0g-dev libssl-dev libx11-dev libxfixes-dev libxrandr-dev xserver-xorg-dev
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
cd "$work"
curl --fail --location --retry 3 -o xrdp.tar.gz https://github.com/neutrinolabs/xrdp/releases/download/v0.10.6.1/xrdp-0.10.6.1.tar.gz
curl --fail --location --retry 3 -o xorgxrdp.tar.gz https://github.com/neutrinolabs/xorgxrdp/releases/download/v0.10.5/xorgxrdp-0.10.5.tar.gz
printf '%s\n' '2f7beb5a3b2529c8d72dc0df9b8cdca31ab0e0c14d1e3421210f5e6ec0ab3b75  xrdp.tar.gz' \
    'a5d03435f0ef48bf3d5010e63d9264f2334e7063cba3ecd8d4c0a15616a4f712  xorgxrdp.tar.gz' | sha256sum -c -
tar -xzf xrdp.tar.gz
tar -xzf xorgxrdp.tar.gz
# Keep the distribution's Xsession integration and PAM rules for the test account.
sudo cp /etc/xrdp/startwm.sh "$work/startwm.sh"
sudo cp /etc/pam.d/xrdp-sesman "$work/xrdp-sesman.pam"
cd xrdp-0.10.6.1
# Use the upstream minimal build: TLS/bitmap desktop/clipboard/display control.
# This job does not claim modern graphics-codec or audio interoperability.
./configure --prefix=/usr --sysconfdir=/etc --localstatedir=/var --enable-ipv6 \
    --disable-jpeg --disable-fuse --disable-mp3lame --disable-fdkaac --disable-opus \
    --disable-rfxcodec --disable-painter --disable-pixman --disable-utmp \
    --without-imlib2 --without-freetype2
make -j2
sudo make install
# Install generated version-matched config files, not the old 0.9 sesman socket settings.
sudo install -m644 xrdp/xrdp.ini /etc/xrdp/xrdp.ini
sudo install -m644 sesman/sesman.ini /etc/xrdp/sesman.ini
sudo sed -i 's|^param=Xorg$|param=/usr/lib/xorg/Xorg|' /etc/xrdp/sesman.ini
sudo install -m755 "$work/startwm.sh" /etc/xrdp/startwm.sh
sudo install -m644 "$work/xrdp-sesman.pam" /etc/pam.d/xrdp-sesman
cd "$work/xorgxrdp-0.10.5"
./configure --prefix=/usr --sysconfdir=/etc
make -j2
sudo make install
sudo ldconfig
sudo systemctl daemon-reload
/usr/sbin/xrdp --version
