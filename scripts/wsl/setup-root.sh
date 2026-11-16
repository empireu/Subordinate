#!/usr/bin/env bash
set -euo pipefail

user_name=""
if [ "$#" -gt 0 ]; then
    user_name="$1"
fi

if [ -z "$user_name" ]; then
    user_name="tester"
fi

apt-get update
DEBIAN_FRONTEND=noninteractive apt-get install -y ca-certificates curl rsync python3 libicu74

if ! id -u "$user_name" >/dev/null 2>&1; then
    useradd --create-home --shell /bin/bash "$user_name"
fi

cat > /etc/wsl.conf <<EOF
[boot]
systemd=false

[interop]
appendWindowsPath=false

[user]
default=$user_name
EOF
