#!/usr/bin/env python3
# fakehost.py — stand-in for the C# app's HttpListener on :38964.
# Mirrors TokenInjectionService.HandleRequestAsync: accepts POST /client/online
# with {"pid","port"}, records the pid->port map, then fires POST
# /handshake/init back at the payload exactly like the real host does.

import json, http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

LOG = "/tmp/poc_host.log"
PID_PORT = {}

def log(msg):
    with open(LOG, "a") as f:
        f.write(msg + "\n")
    print(msg, flush=True)

class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):  # silence default stderr spam
        pass

    def do_POST(self):
        if self.path != "/client/online":
            self.send_response(404); self.end_headers(); return
        n = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(n).decode("utf-8", "replace") if n else ""
        try:
            data = json.loads(body)
        except Exception:
            self.send_response(400); self.end_headers(); self.wfile.write(b"bad json"); return

        pid = data.get("pid"); port = data.get("port"); err = data.get("error")
        log(f"/client/online <- pid={pid} port={port} error={err}")
        if err:
            log(f"  DLL error: {err}")
        elif port:
            first = pid not in PID_PORT
            PID_PORT[pid] = port
            if first:
                self.handshake(port)

        self.send_response(200); self.end_headers(); self.wfile.write(b"OK")

    def handshake(self, port):
        try:
            c = http.client.HTTPConnection("127.0.0.1", port, timeout=3)
            c.request("POST", "/handshake/init", "{}",
                      {"Content-Type": "application/json"})
            r = c.getresponse(); resp = r.read().decode()
            log(f"  -> /handshake/init reply: {resp}")
            c.close()
        except Exception as e:
            log(f"  handshake error on :{port}: {e}")

if __name__ == "__main__":
    open(LOG, "w").close()
    log("fake host listening on 127.0.0.1:38964")
    ThreadingHTTPServer(("127.0.0.1", 38964), Handler).serve_forever()
