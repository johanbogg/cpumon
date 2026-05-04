#!/usr/bin/env python3
"""
cpumon Linux client  —  connects to a cpumon server.

Usage:
    cpumon.py [--server-ip IP] [--token TOKEN]

Requires: Python 3.8+
Optional: pip install psutil   (enables CPU/RAM/disk metrics)
"""

import argparse
import base64
import hashlib
import json
import os
import platform
import select
import shutil
import signal
import socket
import ssl
import subprocess
import sys
import threading
import time

try:
    import psutil
    _PSUTIL = True
except ImportError:
    _PSUTIL = False

# ── Constants ─────────────────────────────────────────────────────────────────

_state_dir  = os.environ.get("STATE_DIRECTORY") or os.path.expanduser("~/.cpumon")
STATE_FILE  = os.path.join(_state_dir, "client_auth.json")
DISC_PORT   = 47200
DATA_PORT   = 47201
BEACON      = "CPUMON_V2"
FULL_MS     = 1.0
MONITOR_MS  = 30.0
KA_MS       = 60.0
VERSION     = "1.0.0-linux"

# ── Auth helpers ──────────────────────────────────────────────────────────────

def derive_key(token: str, machine: str) -> str:
    raw = f"{token}:{machine}:cpumon_v2".encode()
    return base64.b64encode(hashlib.sha256(raw).digest()).decode()[:32]

def cert_thumbprint(der: bytes) -> str:
    return hashlib.sha1(der).hexdigest().upper()

# ── State persistence (plaintext; no DPAPI on Linux) ─────────────────────────

def load_state():
    try:
        with open(STATE_FILE) as f:
            d = json.load(f)
        return d.get("t"), d.get("k"), d.get("s")
    except Exception:
        return None, None, None

def save_state(token, key, server_id):
    os.makedirs(os.path.dirname(STATE_FILE), exist_ok=True)
    with open(STATE_FILE, "w") as f:
        json.dump({"t": token, "k": key, "s": server_id}, f)
    os.chmod(STATE_FILE, 0o600)

def clear_state():
    try:
        os.remove(STATE_FILE)
    except FileNotFoundError:
        pass

# ── System info ───────────────────────────────────────────────────────────────

def _cpu_name() -> str:
    try:
        with open("/proc/cpuinfo") as f:
            for line in f:
                if line.startswith("model name"):
                    return line.split(":", 1)[1].strip()
    except Exception:
        pass
    return platform.processor() or "Unknown CPU"

def _uptime_hours() -> float:
    try:
        with open("/proc/uptime") as f:
            return float(f.read().split()[0]) / 3600.0
    except Exception:
        return 0.0

def _ip_addresses():
    try:
        host = socket.gethostname()
        return list({a for a in socket.gethostbyname_ex(host)[2] if not a.startswith("127.")})
    except Exception:
        return []

def _disk_info():
    disks = []
    if not _PSUTIL:
        return disks
    for part in psutil.disk_partitions():
        try:
            u = psutil.disk_usage(part.mountpoint)
            disks.append({
                "name":    part.mountpoint,
                "label":   part.device,
                "totalGB": u.total / 1073741824.0,
                "freeGB":  u.free  / 1073741824.0,
                "format":  part.fstype,
            })
        except PermissionError:
            pass
    return disks

def build_report(machine: str, cpu_name: str) -> dict:
    r: dict = {
        "name":      machine,
        "os":        platform.platform(),
        "cpuName":   cpu_name,
        "coreCount": os.cpu_count() or 1,
        "ts":        int(time.time() * 1000),
        "cores":     [],
        "drvs":      [],
    }
    if _PSUTIL:
        r["load"]     = psutil.cpu_percent(interval=None)
        r["cores"]    = [{"i": i, "l": p} for i, p in enumerate(psutil.cpu_percent(percpu=True))]
        freq = psutil.cpu_freq()
        if freq:
            r["freq"] = freq.current
        mem = psutil.virtual_memory()
        r["ramTotal"] = mem.total    / 1073741824.0
        r["ramUsed"]  = (mem.total - mem.available) / 1073741824.0
        for part in psutil.disk_partitions():
            try:
                u = psutil.disk_usage(part.mountpoint)
                r["drvs"].append({"n": part.mountpoint, "t": u.total / 1073741824.0, "f": u.free / 1073741824.0})
            except PermissionError:
                pass
    return r

def collect_sysinfo(machine: str, cpu_name: str) -> dict:
    si: dict = {
        "hostname":    machine,
        "domain":      "",
        "osName":      f"{platform.system()} {platform.release()}",
        "osBuild":     platform.version(),
        "cpuName":     cpu_name,
        "cpuCores":    psutil.cpu_count(logical=False) if _PSUTIL else (os.cpu_count() or 1),
        "cpuThreads":  psutil.cpu_count()              if _PSUTIL else (os.cpu_count() or 1),
        "ramTotalGB":  psutil.virtual_memory().total    / 1073741824.0 if _PSUTIL else 0,
        "ramAvailGB":  psutil.virtual_memory().available / 1073741824.0 if _PSUTIL else 0,
        "gpuName":     "",
        "ipAddresses": _ip_addresses(),
        "macAddresses":[],
        "disks":       _disk_info(),
        "uptimeHours": _uptime_hours(),
        "userName":    os.environ.get("USER", ""),
        "dotnetVersion": "",
    }
    return si

def collect_processes() -> list:
    if not _PSUTIL:
        return []
    ncpu    = psutil.cpu_count() or 1
    snap1   = {}
    for p in psutil.process_iter(["pid", "name", "memory_info", "cpu_times"]):
        try:
            snap1[p.pid] = (p.info["name"] or "", p.info["memory_info"].rss, p.info["cpu_times"])
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            pass
    time.sleep(0.5)
    results = []
    elapsed = 0.5
    for pid, (name, mem, t1) in snap1.items():
        cpu = 0.0
        try:
            t2    = psutil.Process(pid).cpu_times()
            delta = (t2.user + t2.system) - (t1.user + t1.system)
            cpu   = max(0.0, delta / elapsed / ncpu * 100.0)
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            pass
        results.append({"pid": pid, "name": name, "mem": mem, "cpu": cpu, "title": ""})
    return sorted(results, key=lambda p: p["mem"], reverse=True)

def list_directory(path: str) -> dict:
    if not path:
        drives = []
        for part in (psutil.disk_partitions() if _PSUTIL else []):
            try:
                u = psutil.disk_usage(part.mountpoint)
                drives.append({"name": part.mountpoint, "label": part.device,
                                "totalGB": u.total / 1073741824.0, "freeGB": u.free / 1073741824.0,
                                "format": part.fstype, "ready": True})
            except Exception:
                pass
        if not drives:
            drives = [{"name": "/", "label": "/", "totalGB": 0, "freeGB": 0, "format": "", "ready": True}]
        return {"path": "", "entries": [], "drives": drives}

    path = os.path.realpath(path)
    entries = []
    try:
        for name in sorted(os.listdir(path)):
            full = os.path.join(path, name)
            try:
                st = os.stat(full)
                is_dir = os.path.isdir(full)
                entries.append({
                    "name":     name,
                    "isDir":    is_dir,
                    "size":     0 if is_dir else st.st_size,
                    "modified": int(st.st_mtime * 1000),
                    "created":  int(getattr(st, "st_birthtime", st.st_mtime) * 1000),
                    "readOnly": not os.access(full, os.W_OK),
                    "hidden":   name.startswith("."),
                })
            except (PermissionError, OSError):
                pass
    except PermissionError as e:
        return {"path": path, "entries": [], "error": str(e)}
    return {"path": path, "entries": entries}

# ── systemd service helpers ───────────────────────────────────────────────────

def list_services() -> list:
    svcs = []
    try:
        r = subprocess.run(
            ["systemctl", "list-units", "--type=service", "--all",
             "--no-pager", "--no-legend", "--plain"],
            capture_output=True, text=True, timeout=10
        )
        for line in r.stdout.splitlines():
            parts = line.split(None, 4)
            if len(parts) < 4:
                continue
            unit, load, active, sub = parts[0], parts[1], parts[2], parts[3]
            desc = parts[4].strip() if len(parts) > 4 else ""
            svcs.append({"n": unit, "d": desc, "s": active, "st": load})
    except Exception:
        pass
    return svcs

def _systemctl(action: str, unit: str):
    subprocess.run(["systemctl", action, unit], check=True, timeout=20)

# ── Terminal session (pty) ────────────────────────────────────────────────────

class TerminalSession:
    def __init__(self, term_id: str, shell: str, send_fn):
        self._id     = term_id
        self._send   = send_fn
        self._closed = False

        import pty
        master_fd, slave_fd = pty.openpty()
        shell_path = shutil.which(shell) or shutil.which("bash") or "/bin/sh"
        self._master = master_fd
        self._proc   = subprocess.Popen(
            [shell_path], stdin=slave_fd, stdout=slave_fd, stderr=slave_fd,
            close_fds=True, preexec_fn=os.setsid,
        )
        os.close(slave_fd)
        threading.Thread(target=self._reader, daemon=True).start()

    def _reader(self):
        while not self._closed:
            try:
                r, _, _ = select.select([self._master], [], [], 0.1)
                if not r:
                    if self._proc.poll() is not None:
                        break
                    continue
                data = os.read(self._master, 4096)
                if not data:
                    break
                self._send({"type": "terminal_output", "termId": self._id,
                            "output": data.decode("utf-8", errors="replace")})
            except OSError:
                break
        self._closed = True
        try:
            self._send({"type": "terminal_closed", "termId": self._id})
        except Exception:
            pass

    def write_input(self, text: str):
        if not self._closed:
            try:
                os.write(self._master, text.encode("utf-8"))
            except OSError:
                pass

    def dispose(self):
        self._closed = True
        try:
            self._proc.terminate()
        except Exception:
            pass
        try:
            os.close(self._master)
        except Exception:
            pass

# ── File upload accumulator ───────────────────────────────────────────────────

class _Upload:
    def __init__(self, dest: str):
        self._fh  = open(dest, "wb")
        self.dest = dest

    def write(self, b64: str):
        if b64:
            self._fh.write(base64.b64decode(b64))

    def close(self):
        self._fh.flush()
        self._fh.close()

# ── Main client ───────────────────────────────────────────────────────────────

class Client:
    def __init__(self, server_ip=None, token=None):
        self._machine  = socket.gethostname()
        self._cpu_name = _cpu_name()
        self._mode     = "full"

        self._server_ip   = server_ip
        self._server_port = DATA_PORT
        self._ssl: ssl.SSLSocket | None = None
        self._recv_buf = b""
        self._send_lock = threading.Lock()

        self._authenticated = False
        self._running       = True

        self._terminals: dict[str, TerminalSession] = {}
        self._uploads:   dict[str, _Upload]         = {}

        stored_tok, stored_key, stored_sid = load_state()
        self._tok = token or stored_tok
        self._ak  = stored_key or ""
        self._sid = stored_sid or ""

    # ── Transport ──────────────────────────────────────────────────────────

    def _discover(self) -> tuple[str, int]:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.bind(("", DISC_PORT))
        sock.settimeout(5)
        print(f"Searching for server on UDP :{DISC_PORT}…", flush=True)
        while self._running:
            try:
                data, addr = sock.recvfrom(256)
                msg = data.decode(errors="replace")
                if not msg.startswith(BEACON):
                    continue
                parts      = msg.split("|")
                port       = int(parts[1]) if len(parts) >= 2 and parts[1].isdigit() else DATA_PORT
                beacon_sid = parts[2] if len(parts) >= 3 else None
                if self._sid and beacon_sid and beacon_sid.upper() != self._sid.upper():
                    continue
                sock.close()
                print(f"Found server at {addr[0]}:{port}", flush=True)
                return addr[0], port
            except socket.timeout:
                pass
        sock.close()
        raise SystemExit("Stopped")

    def _connect(self, host: str, port: int):
        raw = socket.create_connection((host, port), timeout=10)
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        ctx.check_hostname = False
        ctx.verify_mode    = ssl.CERT_NONE  # self-signed; TOFU via thumbprint
        s = ctx.wrap_socket(raw, server_hostname="cpumon-server")
        der = s.getpeercert(binary_form=True)
        if not der:
            s.close()
            raise ConnectionError("Server did not present a TLS certificate")
        thumb = cert_thumbprint(der)
        if self._sid and thumb.upper() != self._sid.upper():
            s.close()
            raise ConnectionError(f"Cert mismatch (TOFU): expected {self._sid}, got {thumb}")
        self._seen_thumb = thumb
        self._ssl        = s
        self._recv_buf   = b""
        self._mode       = "full"

    def _send(self, obj: dict):
        line = json.dumps(obj, separators=(",", ":")) + "\n"
        with self._send_lock:
            if self._ssl:
                self._ssl.sendall(line.encode())

    def _recv_line(self) -> str:
        while b"\n" not in self._recv_buf:
            chunk = self._ssl.recv(65536)
            if not chunk:
                raise ConnectionError("Connection closed")
            self._recv_buf += chunk
        idx          = self._recv_buf.index(b"\n")
        line         = self._recv_buf[:idx].decode()
        self._recv_buf = self._recv_buf[idx + 1:]
        return line

    def _close(self):
        self._authenticated = False
        if self._ssl:
            try:
                self._ssl.close()
            except Exception:
                pass
            self._ssl = None

    # ── Auth ───────────────────────────────────────────────────────────────

    def _send_auth(self):
        msg: dict = {"type": "auth", "machine": self._machine, "appVersion": VERSION}
        if self._ak:
            msg["authKey"] = self._ak
        if self._tok:
            msg["token"] = self._tok
        self._send(msg)

    def _prompt_token(self):
        if sys.stdin.isatty():
            try:
                t = input("Enter invite token from server: ").strip()
                if t:
                    self._tok = t
                    return
            except (EOFError, KeyboardInterrupt):
                pass
        print("No token available — set --token or enter interactively.", file=sys.stderr)
        raise SystemExit(1)

    # ── Command dispatch ───────────────────────────────────────────────────

    def _res(self, cmd_id, ok: bool, msg: str):
        self._send({"type": "cmdresult", "cmdId": cmd_id,
                    "success": ok, "message": msg, "machine": self._machine})

    def _handle(self, cmd: dict):
        c      = cmd.get("cmd", "")
        cid    = cmd.get("cmdId")

        if c == "auth_response":
            if self._authenticated:
                return  # Only accept auth_response once per connection
            if cmd.get("authOk"):
                sid = cmd.get("serverId")
                if sid and getattr(self, "_seen_thumb", None):
                    if sid.upper() != self._seen_thumb.upper():
                        print("✗ ServerId mismatch — possible MITM relay, dropping", flush=True)
                        self._close()
                        return
                self._ak  = cmd.get("authKey") or self._ak
                if sid:
                    self._sid = sid
                if self._tok:
                    save_state(self._tok, self._ak, self._sid)
                self._authenticated = True
                print("✓ Authenticated", flush=True)
            else:
                print("✗ Auth failed — clearing stored credentials", flush=True)
                self._handle_auth_rejected("Auth failed")

        elif c == "mode":
            self._mode = cmd.get("mode") or "full"

        elif c == "send_message":
            print(f"\n[Server] {cmd.get('message', '')}\n", flush=True)

        elif c == "restart":
            self._res(cid, True, "Restarting…")
            threading.Timer(0.5, lambda: subprocess.run(["shutdown", "-r", "now"])).start()

        elif c == "shutdown":
            self._res(cid, True, "Shutting down…")
            threading.Timer(0.5, lambda: subprocess.run(["shutdown", "-h", "now"])).start()

        elif c == "listprocesses":
            threading.Thread(target=self._send_processes, daemon=True).start()

        elif c == "kill":
            pid = cmd.get("pid")
            try:
                os.kill(pid, signal.SIGKILL)
                self._res(cid, True, f"Killed {pid}")
            except Exception as e:
                self._res(cid, False, str(e))

        elif c == "start":
            fname = cmd.get("fileName") or ""
            args  = cmd.get("args")  or ""
            try:
                p = subprocess.Popen(fname.split() + ([args] if args else []),
                                     start_new_session=True)
                self._res(cid, True, f"PID {p.pid}")
            except Exception as e:
                self._res(cid, False, str(e))

        elif c == "sysinfo":
            try:
                self._send({"type": "sysinfo", "sysinfo": collect_sysinfo(self._machine, self._cpu_name)})
            except Exception as e:
                self._res(cid, False, str(e))

        elif c == "terminal_open":
            tid   = cmd.get("termId")
            shell = cmd.get("shell") or "bash"
            if tid:
                if tid in self._terminals:
                    self._terminals.pop(tid).dispose()
                try:
                    self._terminals[tid] = TerminalSession(tid, shell, self._send)
                except Exception as e:
                    self._res(cid, False, f"Terminal: {e}")

        elif c == "terminal_input":
            tid = cmd.get("termId")
            inp = cmd.get("input")
            if tid and inp and tid in self._terminals:
                self._terminals[tid].write_input(inp)

        elif c == "terminal_close":
            tid = cmd.get("termId")
            if tid and tid in self._terminals:
                self._terminals.pop(tid).dispose()

        elif c == "list_services":
            try:
                self._send({"type": "servicelist", "serviceList": list_services(),
                            "machine": self._machine})
            except Exception as e:
                self._res(cid, False, str(e))

        elif c == "service_start":
            unit = cmd.get("fileName")
            def _do():
                try:
                    _systemctl("start", unit)
                    self._res(cid, True, f"Started: {unit}")
                except Exception as e:
                    self._res(cid, False, str(e))
            threading.Thread(target=_do, daemon=True).start()

        elif c == "service_stop":
            unit = cmd.get("fileName")
            def _do():
                try:
                    _systemctl("stop", unit)
                    self._res(cid, True, f"Stopped: {unit}")
                except Exception as e:
                    self._res(cid, False, str(e))
            threading.Thread(target=_do, daemon=True).start()

        elif c == "service_restart":
            unit = cmd.get("fileName")
            def _do():
                try:
                    _systemctl("restart", unit)
                    self._res(cid, True, f"Restarted: {unit}")
                except Exception as e:
                    self._res(cid, False, str(e))
            threading.Thread(target=_do, daemon=True).start()

        elif c == "file_list":
            path = cmd.get("path") or ""
            try:
                self._send({"type": "file_listing", "fileListing": list_directory(path), "cmdId": cid})
            except Exception as e:
                self._res(cid, False, str(e))

        elif c == "file_download":
            path = cmd.get("path")
            tid  = cmd.get("transferId")
            if path and tid:
                threading.Thread(target=self._send_file, args=(path, tid), daemon=True).start()

        elif c == "file_upload_chunk":
            chunk = cmd.get("fileChunk")
            dest  = cmd.get("destPath")
            if chunk and dest:
                result = self._recv_chunk(chunk, dest)
                if result:
                    self._res(cid, not result.startswith("Upload error"), result)

        elif c == "file_delete":
            path      = cmd.get("path")
            recursive = cmd.get("recursive", False)
            try:
                if os.path.isdir(path):
                    shutil.rmtree(path) if recursive else os.rmdir(path)
                else:
                    os.remove(path)
                self._res(cid, True, f"Deleted: {path}")
            except Exception as e:
                self._res(cid, False, f"Delete error: {e}")

        elif c == "file_mkdir":
            path = cmd.get("path")
            try:
                os.makedirs(path, exist_ok=True)
                self._res(cid, True, f"Created: {path}")
            except Exception as e:
                self._res(cid, False, f"Error: {e}")

        elif c == "file_rename":
            path     = cmd.get("path")
            new_name = cmd.get("newName")
            try:
                new_path = os.path.join(os.path.dirname(path), new_name)
                os.rename(path, new_path)
                self._res(cid, True, f"Renamed to {new_name}")
            except Exception as e:
                self._res(cid, False, f"Rename error: {e}")

    # ── Helpers for file/process ops ───────────────────────────────────────

    def _send_processes(self):
        try:
            procs = collect_processes()
            self._send({"type": "processlist", "processes": procs, "machine": self._machine})
        except Exception as e:
            print(f"processes error: {e}", flush=True)

    def _send_file(self, path: str, transfer_id: str):
        chunk_size = 65536
        try:
            total = os.path.getsize(path)
            fname = os.path.basename(path)
            offset = 0
            with open(path, "rb") as fh:
                while True:
                    data    = fh.read(chunk_size)
                    is_last = len(data) < chunk_size
                    self._send({"type": "file_chunk", "transferId": transfer_id, "fileChunk": {
                        "transferId": transfer_id,
                        "fileName":   fname,
                        "data":       base64.b64encode(data).decode() if data else "",
                        "offset":     offset,
                        "totalSize":  total,
                        "isLast":     is_last or not data,
                    }})
                    offset += len(data)
                    if not data or is_last:
                        break
                    time.sleep(0.005)
        except Exception as e:
            self._send({"type": "file_chunk", "transferId": transfer_id, "fileChunk": {
                "transferId": transfer_id, "fileName": "", "data": "",
                "offset": 0, "totalSize": 0, "isLast": True, "error": str(e),
            }})

    def _recv_chunk(self, chunk: dict, dest_dir: str) -> str:
        tid    = chunk.get("transferId", "")
        fname  = os.path.basename(chunk.get("fileName") or "upload")
        offset = chunk.get("offset", 0)
        data   = chunk.get("data", "")
        last   = chunk.get("isLast", False)
        try:
            if offset == 0:
                if tid in self._uploads:
                    self._uploads[tid].close()
                os.makedirs(dest_dir, exist_ok=True)
                self._uploads[tid] = _Upload(os.path.join(dest_dir, fname))
            if tid in self._uploads:
                self._uploads[tid].write(data)
                if last:
                    self._uploads.pop(tid).close()
                    return f"Upload complete: {fname}"
            return ""
        except Exception as e:
            if tid in self._uploads:
                self._uploads.pop(tid).close()
            return f"Upload error: {e}"

    def _handle_auth_rejected(self, reason: str):
        print(f"{reason} - clearing stored credentials", flush=True)
        clear_state()
        self._ak  = ""
        self._tok = None
        self._authenticated = False
        self._close()

    # ── Main loops ─────────────────────────────────────────────────────────

    def _send_loop(self):
        _primed = False
        while self._running:
            if not self._authenticated or not self._ssl:
                _primed = False
                time.sleep(0.2)
                continue
            if not _primed and _PSUTIL:
                psutil.cpu_percent(interval=None)
                _primed = True
            try:
                if self._mode == "keepalive":
                    self._send({"type": "keepalive", "machine": self._machine, "authKey": self._ak})
                    time.sleep(KA_MS)
                else:
                    report = build_report(self._machine, self._cpu_name)
                    self._send({"type": "report", "report": report,
                                "machine": self._machine, "authKey": self._ak})
                    time.sleep(MONITOR_MS if self._mode == "monitor" else FULL_MS)
            except Exception as e:
                print(f"Send error: {e}", flush=True)
                time.sleep(1)

    def run(self):
        threading.Thread(target=self._send_loop, daemon=True).start()

        while self._running:
            host = self._server_ip
            port = self._server_port

            if not host:
                try:
                    host, port = self._discover()
                except SystemExit:
                    break

            if not self._tok and not self._ak:
                self._prompt_token()

            try:
                self._connect(host, port)
                self._send_auth()

                while self._running:
                    try:
                        line = self._recv_line()
                    except ConnectionError as e:
                        if not self._authenticated and self._ak:
                            self._handle_auth_rejected("Connection closed before saved auth was accepted")
                        raise e
                    try:
                        self._handle(json.loads(line))
                    except Exception as e:
                        print(f"Handle error: {e}", flush=True)

            except KeyboardInterrupt:
                break
            except Exception as e:
                print(f"Connection error: {e}", flush=True)
            finally:
                self._close()
                for t in list(self._terminals.values()):
                    t.dispose()
                self._terminals.clear()

            if not self._running:
                break

            # If auth failed, prompt for a new token before retrying
            if not self._tok and not self._ak:
                self._prompt_token()
            time.sleep(3)


# ── Entry point ───────────────────────────────────────────────────────────────

def main():
    ap = argparse.ArgumentParser(description="cpumon Linux client")
    ap.add_argument("--server-ip", "-ip",  metavar="IP",    help="Skip discovery, connect directly")
    ap.add_argument("--token",     "-t",   metavar="TOKEN", help="Invite token (first-time auth)")
    args = ap.parse_args()

    if not _PSUTIL:
        print("Warning: psutil not installed — metrics will be empty. Run: pip install psutil",
              file=sys.stderr)

    client = Client(server_ip=args.server_ip, token=args.token)

    def _sig(signum, frame):
        client._running = False

    signal.signal(signal.SIGTERM, _sig)
    signal.signal(signal.SIGINT,  _sig)

    try:
        client.run()
    except SystemExit:
        pass


if __name__ == "__main__":
    main()
