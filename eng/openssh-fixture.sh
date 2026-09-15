#!/usr/bin/env bash
# Isolated, ephemeral CI-only loopback service. No external hosts, credentials or production accounts.
set -euo pipefail
[[ ${GITHUB_ACTIONS:-} == true ]] || { echo 'Run only on an ephemeral GitHub Actions Linux runner.' >&2; exit 1; }
fixture="$RUNNER_TEMP/tessera-sshd"
mkdir -p "$fixture"
chmod 700 "$fixture"
sudo useradd --create-home --shell /bin/bash tessera-integration
password=$(openssl rand -hex 24)
passphrase=$(openssl rand -hex 24)
echo "::add-mask::$password"
echo "::add-mask::$passphrase"
printf 'tessera-integration:%s\n' "$password" | sudo chpasswd
ssh-keygen -q -t ed25519 -N "$passphrase" -f "$fixture/client_key"
ssh-keygen -q -t ed25519 -N '' -f "$fixture/host_key"
sudo install -d -m 700 -o tessera-integration -g tessera-integration /home/tessera-integration/.ssh
sudo install -m 600 -o tessera-integration -g tessera-integration "$fixture/client_key.pub" /home/tessera-integration/.ssh/authorized_keys
sudo -u tessera-integration bash -c 'mkdir ~/symlink-fixture; printf "keep-target\n" > ~/symlink-fixture/target; ln -s target ~/symlink-fixture/link; ln -s missing ~/symlink-fixture/dangling'
sudo mkdir -p /run/sshd
cat > "$fixture/config" <<CONFIG
Port 23781
ListenAddress 127.0.0.1
HostKey $fixture/host_key
PidFile $fixture/sshd.pid
AuthorizedKeysFile .ssh/authorized_keys
AllowUsers tessera-integration
PermitRootLogin no
PasswordAuthentication no
PubkeyAuthentication yes
KbdInteractiveAuthentication yes
AuthenticationMethods publickey,keyboard-interactive:pam
UsePAM yes
UseDNS no
Subsystem sftp internal-sftp
LogLevel VERBOSE
CONFIG
sudo /usr/sbin/sshd -t -f "$fixture/config"
sudo /usr/sbin/sshd -f "$fixture/config" -E "$fixture/server.log"
{
  echo TESSERA_SSH_TESTS=1
  echo "TESSERA_TEST_SSH_PASSWORD=$password"
  echo "TESSERA_TEST_KEY_PASSPHRASE=$passphrase"
  echo "TESSERA_TEST_SSH_KEY=$fixture/client_key"
} >> "$GITHUB_ENV"
