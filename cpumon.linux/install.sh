#!/usr/bin/env bash
# cpumon Linux client installer/updater for Debian/Ubuntu
# Usage:  sudo bash install.sh
#         sudo bash install.sh update
#         sudo bash install.sh uninstall

set -euo pipefail

INSTALL_DIR=/opt/cpumon
SERVICE_NAME=cpumon
SERVICE_FILE=/etc/systemd/system/${SERVICE_NAME}.service
DEFAULTS_FILE=/etc/default/${SERVICE_NAME}
STATE_FILE=/var/lib/cpumon/client_auth.json
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

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

require_payload() {
    if [[ ! -f "$SCRIPT_DIR/cpumon.py" ]]; then
        red "cpumon.py not found next to install.sh"
        exit 1
    fi
}

read_existing_config() {
    existing_ip=""
    existing_token=""
    if [[ -f "$DEFAULTS_FILE" ]]; then
        existing_ip=$(grep -Po '(?<=^CPUMON_SERVER_IP=).*' "$DEFAULTS_FILE" || true)
        existing_token=$(grep -Po '(?<=^CPUMON_TOKEN=).*' "$DEFAULTS_FILE" || true)
        blue "Existing config found in $DEFAULTS_FILE"
    fi
}

have_psutil() {
    python3 - <<'PY' >/dev/null 2>&1
import psutil
PY
}

install_dependencies() {
    local required="${1:-yes}"
    if command -v python3 >/dev/null 2>&1 && have_psutil; then
        blue "Python 3 and psutil already installed."
        return 0
    fi

    blue "Installing Python 3 and psutil..."
    if ! apt-get update -qq; then
        if [[ "$required" == "yes" ]]; then
            red "apt-get update failed"
            return 1
        fi
        red "apt-get update failed; keeping existing dependencies"
        return 0
    fi

    if ! apt-get install -y --no-install-recommends python3 python3-pip > /dev/null; then
        if [[ "$required" == "yes" ]]; then
            red "Failed to install python3/python3-pip"
            return 1
        fi
        red "Failed to refresh python packages; keeping existing dependencies"
        return 0
    fi

    if python3 -m pip install --quiet psutil 2>/dev/null; then
        true
    elif pip3 install --quiet psutil 2>/dev/null; then
        true
    else
        apt-get install -y --no-install-recommends python3-psutil > /dev/null || true
    fi
}

write_program_files() {
    blue "Installing files to $INSTALL_DIR..."
    mkdir -p "$INSTALL_DIR"
    cp "$SCRIPT_DIR/cpumon.py" "$INSTALL_DIR/cpumon.py"
    chmod 755 "$INSTALL_DIR/cpumon.py"

    cat > "$INSTALL_DIR/start.sh" <<'WRAPPER'
#!/usr/bin/env bash
source /etc/default/cpumon
ARGS=()
[[ -n "${CPUMON_SERVER_IP:-}" ]] && ARGS+=(--server-ip "$CPUMON_SERVER_IP")
[[ -n "${CPUMON_TOKEN:-}"     ]] && ARGS+=(--token     "$CPUMON_TOKEN")
exec python3 /opt/cpumon/cpumon.py "${ARGS[@]}"
WRAPPER
    chmod 755 "$INSTALL_DIR/start.sh"
}

write_defaults_file() {
    local server_ip="$1"
    local token="$2"

    cat > "$DEFAULTS_FILE" <<EOF
# cpumon client configuration
# Edit this file then run:  systemctl restart cpumon
# Clear stored auth key:    rm $STATE_FILE

CPUMON_SERVER_IP=${server_ip}
CPUMON_TOKEN=${token}
EOF
    chmod 600 "$DEFAULTS_FILE"
}

write_service_file() {
    cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=cpumon remote monitoring client
Documentation=https://github.com/johanbogg/cpumon
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
EnvironmentFile=$DEFAULTS_FILE
ExecStart=$INSTALL_DIR/start.sh
Restart=always
RestartSec=5
# systemd creates /var/lib/cpumon and sets STATE_DIRECTORY
StateDirectory=cpumon
WorkingDirectory=/var/lib/cpumon
# Run as root so shutdown/reboot/kill work without extra config.
# To harden: create a dedicated user and grant passwordless sudo for
# /sbin/shutdown, /bin/kill, /bin/systemctl instead.
User=root

[Install]
WantedBy=multi-user.target
EOF
}

restart_service() {
    systemctl daemon-reload
    systemctl enable "$SERVICE_NAME" >/dev/null
    systemctl restart "$SERVICE_NAME"
    systemctl is-active --quiet "$SERVICE_NAME"
}

uninstall() {
    require_root
    bold "Uninstalling cpumon..."

    systemctl stop "$SERVICE_NAME" 2>/dev/null || true
    systemctl disable "$SERVICE_NAME" 2>/dev/null || true

    rm -f "$SERVICE_FILE"
    rm -f "$DEFAULTS_FILE"
    rm -rf "$INSTALL_DIR"

    systemctl daemon-reload
    green "cpumon uninstalled."
    echo "State directory /var/lib/cpumon was kept (stored auth key)."
    echo "Remove manually with: rm -rf /var/lib/cpumon"
}

install() {
    require_root
    require_payload

    bold "cpumon Linux client - installer"
    echo "Requires Python 3.8+ (installed via apt if missing)"
    echo

    read_existing_config

    read -rp "Server IP [${existing_ip:-auto-discover}]: " input_ip
    SERVER_IP="${input_ip:-$existing_ip}"

    read -rp "Invite token [${existing_token:-prompt on first connect}]: " input_token
    TOKEN="${input_token:-$existing_token}"

    echo
    install_dependencies yes
    write_program_files
    write_defaults_file "$SERVER_IP" "$TOKEN"
    write_service_file
    restart_service

    echo
    green "cpumon installed and started."
    print_useful_commands

    if [[ -z "$TOKEN" ]]; then
        echo
        bold "No token set - on first connect the service will fail auth."
        echo "Set CPUMON_TOKEN in $DEFAULTS_FILE and restart:"
        echo "  systemctl restart cpumon"
    fi
}

update() {
    require_root
    require_payload

    bold "cpumon Linux client - updater"

    if [[ ! -f "$DEFAULTS_FILE" ]]; then
        red "No existing $DEFAULTS_FILE found."
        echo "Run first install instead: sudo bash install.sh"
        exit 1
    fi

    install_dependencies no

    backup=""
    if [[ -f "$INSTALL_DIR/cpumon.py" ]]; then
        backup=$(mktemp)
        cp "$INSTALL_DIR/cpumon.py" "$backup"
    fi

    write_program_files
    write_service_file
    if ! restart_service; then
        red "Updated service failed to start."
        if [[ -n "$backup" && -f "$backup" ]]; then
            red "Rolling back cpumon.py and restarting previous version..."
            cp "$backup" "$INSTALL_DIR/cpumon.py"
            chmod 755 "$INSTALL_DIR/cpumon.py"
            systemctl restart "$SERVICE_NAME" || true
        fi
        rm -f "$backup"
        echo "Check logs with: journalctl -u cpumon -n 100 --no-pager"
        exit 1
    fi
    rm -f "$backup"

    echo
    green "cpumon updated and restarted."
    echo "Kept config: $DEFAULTS_FILE"
    echo "Kept auth:   $STATE_FILE"
    print_useful_commands
}

print_useful_commands() {
    echo
    bold "Useful commands:"
    echo "  systemctl status  cpumon      # check status"
    echo "  systemctl stop    cpumon      # stop"
    echo "  systemctl start   cpumon      # start"
    echo "  journalctl -u cpumon -f       # live logs"
    echo "  nano $DEFAULTS_FILE   # change server IP / token"
    echo "  rm $STATE_FILE   # clear stored auth key"
}

case "${1:-install}" in
    uninstall|remove) uninstall ;;
    update|upgrade)   update ;;
    install)          install ;;
    *)
        red "Unknown command: $1"
        echo "Usage: sudo bash install.sh [install|update|uninstall]"
        exit 1
        ;;
esac
