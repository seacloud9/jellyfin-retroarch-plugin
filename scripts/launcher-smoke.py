"""Unix CLI integration: fake HTTP server + process substitute; no playable ROM required."""
import hashlib
import http.server
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading

repo = Path(__file__).resolve().parents[1]
dotnet = os.environ.get("DOTNET", "dotnet")
launcher = repo / "src/RetroBridge.Launcher/bin/Release/net10.0/RetroBridge.Launcher.dll"
content = b"Synthetic content for transport tests; not a playable ROM."
checksum = hashlib.sha256(content).hexdigest()
game = dict(Id="nes-" + checksum, Title="Fixture", Platform="nes", Extension=".nes", Size=len(content), Sha256=checksum)
state = dict(downloads=0, corrupt=False)

class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_GET(self):
        if self.headers.get("X-Emby-Token") != "test-only-placeholder":
            self.send_error(401)
            return
        if self.path == "/Users/Me":
            payload = json.dumps(dict(Id="12345678-1234-1234-1234-123456789012")).encode()
        elif self.path == "/RetroBridge/Games":
            payload = json.dumps([game]).encode()
        elif self.path == "/RetroBridge/Games/" + game["Id"] + "/Content":
            state["downloads"] += 1
            payload = b"x" * len(content) if state["corrupt"] else content
        else:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
thread = threading.Thread(target=server.serve_forever, daemon=True)
thread.start()
try:
    with tempfile.TemporaryDirectory(prefix="retrobridge smoke ") as directory:
        root = Path(directory)
        emulator = root / "fake retroarch"
        emulator.write_text('#!/bin/sh\n[ "$1" = "-L" ] || exit 10\n[ "$3" = "--appendconfig" ] || exit 11\n[ -f "$2" ] && [ -f "$5" ] || exit 12\nprintf "started\\n" >> "$(dirname "$4")/fixture.srm"\n')
        emulator.chmod(0o700)
        core = root / "fake core"; core.write_text("substitute")
        command = [dotnet, str(launcher), f"http://127.0.0.1:{server.server_port}", game["Id"], str(emulator), str(core), str(root / "data")]
        environment = dict(os.environ, RETROBRIDGE_TOKEN="test-only-placeholder")
        def run():
            return subprocess.run(command, env=environment, capture_output=True, text=True, timeout=30)
        first = run(); assert first.returncode == 0, first.stderr
        second = run(); assert second.returncode == 0, second.stderr
        assert state["downloads"] == 1, "Second launch must reuse verified cache"
        save = next((root / "data/saves").rglob("fixture.srm"))
        assert save.read_text() == "started\nstarted\n", "Same save location must survive relaunch"
        cached = next((root / "data/cache").glob("*.nes"))
        cached.write_bytes(b"damaged")
        third = run(); assert third.returncode == 0, third.stderr
        assert state["downloads"] == 2 and cached.read_bytes() == content
        cached.unlink(); state["corrupt"] = True
        fourth = run(); assert fourth.returncode == 1 and "checksum" in fourth.stderr, fourth.stderr
        assert not cached.exists() and not list((root / "data/cache").glob("*.partial"))
        assert save.read_text() == "started\nstarted\nstarted\n", "Corrupt download must never start emulator"
        print("PASS: process arguments, verified cache reuse/repair, save path reuse, corrupt download rejection and cleanup")
finally:
    server.shutdown(); server.server_close(); thread.join()
