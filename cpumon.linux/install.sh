#!/usr/bin/env bash
# cpumon Linux client installer for Debian/Ubuntu
# Usage:  sudo bash install.sh
#         sudo bash install.sh uninstall

set -euo pipefail

INSTALL_DIR=/opt/cpumon
SERVICE_NAME=cpumon
SERVICE_FILE=/etc/systemd/system/${SERVICE_NAME}.service
DEFAULTS_FILE=/etc/default/${SERVICE_NAME}
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ── Helpers ───────────────────────────────────────────────────────────────────

red()   { echo -e "\033[1;31m$*\033[0m"; }
green() { echo -e "\033[1;32m$*\033[0m"; }
blue()  { echo -e "\033[1;34m$*\033[0m"; }
bold()  { echo -e "\033[1m$*\033[0m"; }

require_root() {
    if [[ $EUID -ne 0 ]]; then
        red "Run with sudo: sudo bash install.sh"
        exit 1
    fi
}

# ── Uninstall ─────────────────────────────────────────────────────────────────

uninstall() {
    require_root
    bold "Uninstalling cpumon…"

    systemctl stop    "$SERVICE_NAME" 2>/dev/null || true
    systemctl disable "$SERVICE_NAME" 2>/dev/null || true

    rm -f  "$SERVICE_FILE"
    rm -f  "$DEFAULTS_FILE"
    rm -rf "$INSTALL_DIR"

    systemctl daemon-reload
    green "cpumon uninstalled."
    echo  "State directory /var/lib/cpumon was kept (stored auth key)."
    echo  "Remove manually with: rm -rf /var/lib/cpumon"
}

# ── Install ───────────────────────────────────────────────────────────────────

install() {
    require_root

    bold "cpumon Linux client — installer"
    echo "Requires Python 3.8+ (installed via apt if missing)"
    echo

    # ── Gather config ──────────────────────────────────────────────────────

    # Preserve existing values if re-running
    existing_ip=""
    existing_token=""
    if [[ -f "$DEFAULTS_FILE" ]]; then
        existing_ip=$(    grep -Po '(?<=^CPUMON_SERVER_IP=).*' "$DEFAULTS_FILE" || true)
        existing_token=$( grep -Po '(?<=^CPUMON_TOKEN=).*'     "$DEFAULTS_FILE" || true)
        blue "Existing config found in $DEFAULTS_FILE"
    fi

    read -rp "Server IP [${existing_ip:-auto-discover}]: " input_ip
    SERVER_IP="${input_ip:-$existing_ip}"

    read -rp "Invite token [${existing_token:-prompt on first connect}]: " input_token
    TOKEN="${input_token:-$existing_token}"

    echo

    # ── Dependencies ───────────────────────────────────────────────────────

    blue "Installing Python 3 and psutil…"
    apt-get update -qq
    apt-get install -y --no-install-recommends python3 python3-pip > /dev/null

    # Try pipx/pip install; fall back silently if pip is externally managed
    if python3 -m pip install --quiet psutil 2>/dev/null; then
        true
    elif pip3 install --quiet psutil 2>/dev/null; then
        true
    else
        # Debian 12+ with externally-managed pip: use apt
        apt-get install -y --no-install-recommends python3-psutil > /dev/null || true
    fi

    # ── Copy files ─────────────────────────────────────────────────────────

    blue "Installing to $INSTALL_DIR…"
    mkdir -p "$INSTALL_DIR"
    cp "$SCRIPT_DIR/cpumon.py" "$INSTALL_DIR/cpumon.py"
    chmod 755 "$INSTALL_DIR/cpumon.py"

    # ── Wrapper script (handles optional args cleanly) ─────────────────────

    cat > "$INSTALL_DIR/start.sh" <<'WRAPPER'
#!/usr/bin/env bash
source /etc/default/cpumon
ARGS=()
[[ -n "${CPUMON_SERVER_IP:-}" ]] && ARGS+=(--server-ip "$CPUMON_SERVER_IP")
[[ -n "${CPUMON_TOKEN:-}"     ]] && ARGS+=(--token     "$CPUMON_TOKEN")
exec python3 /opt/cpumon/cpumon.py "${ARGS[@]}"
WRAPPER
    chmod 755 "$INSTALL_DIR/start.sh"

    # ── Defaults file ──────────────────────────────────────────────────────

    cat > "$DEFAULTS_FILE" <<EOF
# cpumon client configuration
# Edit this file then run:  systemctl restart cpumon
# Clear stored auth key:    rm /var/lib/cpumon/client_auth.json

CPUMON_SERVER_IP=${SERVER_IP}
CPUMON_TOKEN=${TOKEN}
EOF
    chmod 600 "$DEFAULTS_FILE"

    # ── Systemd unit ───────────────────────────────────────────────────────

    cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=cpumon remote monitoring client
Documentation=https://github.com/johanbogg/cpumon
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
EnvironmentFile=/etc/default/cpumon
ExecStart=/opt/cpumon/start.sh
Restart=always
RestartSec=5
# State directory: systemd creates /var/lib/cpumon and sets STATE_DIRECTORY
StateDirectory=cpumon
WorkingDirectory=/var/lib/cpumon
# Run as root so shutdown/reboot/kill work without extra config.
# To harden: create a dedicated user and grant passwordless sudo for
# /sbin/shutdown, /bin/kill, /bin/systemctl instead.
User=root

[Install]
WantedBy=multi-user.target
EOF

    # ── Enable and start ───────────────────────────────────────────────────

    systemctl daemon-reload
    systemctl enable "$SERVICE_NAME"
    systemctl restart "$SERVICE_NAME"

    echo
    green "cpumon installed and started."
    echo
    bold "Useful commands:"
    echo "  systemctl status  cpumon      # check status"
    echo "  systemctl stop    cpumon      # stop"
    echo "  systemctl start   cpumon      # start"
    echo "  journalctl -u cpumon -f       # live logs"
    echo "  nano $DEFAULTS_FILE   # change server IP / token"
    echo "  rm /var/lib/cpumon/client_auth.json   # clear stored auth key"
    echo
    if [[ -z "$TOKEN" ]]; then
        bold "No token set — on first connect the service will fail auth."
        echo "Set CPUMON_TOKEN in $DEFAULTS_FILE and restart:"
        echo "  systemctl restart cpumon"
    fi
}

# ── Entry point ───────────────────────────────────────────────────────────────

case "${1:-install}" in
    uninstall|remove) uninstall ;;
    install|*)        install   ;;
esac
