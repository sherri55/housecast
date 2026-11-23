#!/bin/bash
# Builds and starts the AirPlay engine (OwnTone) inside the "airplay" WSL system.
# Run as root:  wsl -d airplay -u root -- bash /mnt/d/Projects/2026/Extra/airplay-tray/engine/setup-engine.sh
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
OWNTONE_COMMIT=e2a3915

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq build-essential git autotools-dev autoconf automake libtool gettext gawk gperf \
  bison flex libconfuse-dev libunistring-dev libsqlite3-dev libavcodec-dev libavformat-dev \
  libavfilter-dev libswscale-dev libavutil-dev libasound2-dev libxml2-dev libgcrypt20-dev \
  libavahi-client-dev zlib1g-dev libevent-dev libplist-dev libsodium-dev libjson-c-dev \
  libwebsockets-dev libcurl4-openssl-dev libprotobuf-c-dev avahi-daemon libnss-mdns curl

if [ ! -d /opt/owntone-server ]; then
  git clone https://github.com/owntone/owntone-server.git /opt/owntone-server
  git -C /opt/owntone-server checkout "$OWNTONE_COMMIT"
  # Samsung TVs only accept the pairing PIN on the connection that asked for it
  git -C /opt/owntone-server apply "$HERE/owntone-samsung-pin-pairing.patch"
  # A small input buffer, so a live pipe can't build up seconds of lasting delay
  git -C /opt/owntone-server apply "$HERE/owntone-small-input-buffer.patch"
fi

cd /opt/owntone-server
autoreconf -i
./configure --prefix=/usr --sysconfdir=/etc --localstatedir=/var --enable-install-user \
  --disable-spotify --disable-chromecast
make -j"$(nproc)"
make install

id owntone >/dev/null 2>&1 || useradd --system --no-create-home owntone
mkdir -p /srv/music /var/cache/owntone
[ -p /srv/music/pc-audio ] || mkfifo /srv/music/pc-audio
chmod 666 /srv/music/pc-audio
chown -R owntone /srv/music /var/cache/owntone

systemctl daemon-reload
systemctl enable --now avahi-daemon owntone
echo "OwnTone is running: http://localhost:3689"
