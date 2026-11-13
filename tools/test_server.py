#!/usr/bin/env python3
"""Static file server for local launcher tests, bound to 127.0.0.1 only.

  python tools/test_server.py --root DIR --port 8765 [--rate BYTES_PER_S] [--log FILE]

Unlike `python -m http.server` it answers HTTP Range requests (206 Partial Content), which the
launcher uses to resume a broken pack download. Two extras for tests:
  --rate N       sends at most N bytes per second per response (so a test can kill the server mid-file)
  /cdn/...       any path under /cdn/ answers 302 to the same path without /cdn/, the way GitHub release
                 assets redirect to their CDN
JSON files are gzip-compressed when the client accepts it, as GitHub Pages does.
Each request is logged as one line: method, path, Range header, status, bytes sent, encoding.
"""

import argparse
import gzip
import http.server
import os
import re
import socketserver
import sys
import time
import urllib.parse


class Handler(http.server.BaseHTTPRequestHandler):
    root = "."
    rate = 0
    log_file = None
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass

    def log_line(self, status, sent, enc="-"):
        line = "%s %s range=%s status=%d sent=%d enc=%s\n" % (
            self.command, self.path, self.headers.get("Range", "-"), status, sent, enc)
        if self.log_file:
            with open(self.log_file, "a", encoding="utf-8") as f:
                f.write(line)
        sys.stdout.write(line)
        sys.stdout.flush()

    def do_HEAD(self):
        self.handle_get(head=True)

    def do_GET(self):
        self.handle_get(head=False)

    def handle_get(self, head):
        parsed = urllib.parse.urlsplit(self.path)
        path = urllib.parse.unquote(parsed.path)
        if path.startswith("/cdn/"):
            target = path[4:] + (("?" + parsed.query) if parsed.query else "")
            self.send_response(302)
            self.send_header("Location", urllib.parse.quote(target, safe="/?=&"))
            self.send_header("Content-Length", "0")
            self.end_headers()
            self.log_line(302, 0)
            return
        rel = path.lstrip("/")
        full = os.path.realpath(os.path.join(self.root, rel))
        root = os.path.realpath(self.root)
        if os.path.isdir(full):
            full = os.path.join(full, "index.html")
        if not (full == root or full.startswith(root + os.sep)) or not os.path.isfile(full):
            self.send_response(404)
            self.send_header("Content-Length", "0")
            self.end_headers()
            self.log_line(404, 0)
            return
        rng = self.headers.get("Range")
        if full.endswith(".json") and not rng and "gzip" in self.headers.get("Accept-Encoding", ""):
            # Like GitHub Pages: JSON is sent gzip-compressed when the client accepts it.
            with open(full, "rb") as f:
                body = gzip.compress(f.read())
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Encoding", "gzip")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            if not head:
                self.wfile.write(body)
            self.log_line(200, 0 if head else len(body), "gzip")
            return
        size = os.path.getsize(full)
        start, end, status = 0, size - 1, 200
        if rng:
            m = re.match(r"bytes=(\d*)-(\d*)$", rng.strip())
            if m and m.group(1):
                start = int(m.group(1))
                if m.group(2):
                    end = min(int(m.group(2)), size - 1)
                status = 206
                if start >= size:
                    self.send_response(416)
                    self.send_header("Content-Range", "bytes */%d" % size)
                    self.send_header("Content-Length", "0")
                    self.end_headers()
                    self.log_line(416, 0)
                    return
        length = end - start + 1 if size > 0 else 0
        self.send_response(status)
        ctype = "application/json" if full.endswith(".json") else \
            "text/html; charset=utf-8" if full.endswith(".html") else "application/octet-stream"
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(length))
        self.send_header("Accept-Ranges", "bytes")
        if status == 206:
            self.send_header("Content-Range", "bytes %d-%d/%d" % (start, end, size))
        self.end_headers()
        sent = 0
        if not head and length > 0:
            chunk = 65536 if not self.rate else max(1024, self.rate // 20)
            t0 = time.time()
            try:
                with open(full, "rb") as f:
                    f.seek(start)
                    while sent < length:
                        b = f.read(min(chunk, length - sent))
                        if not b:
                            break
                        self.wfile.write(b)
                        sent += len(b)
                        if self.rate:
                            ahead = sent / self.rate - (time.time() - t0)
                            if ahead > 0:
                                time.sleep(ahead)
            except (ConnectionError, OSError):
                pass
        self.log_line(status, sent)


class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True)
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--rate", type=int, default=0)
    ap.add_argument("--log")
    a = ap.parse_args()
    Handler.root = os.path.abspath(a.root)
    Handler.rate = a.rate
    Handler.log_file = a.log
    srv = Server(("127.0.0.1", a.port), Handler)
    print("serving %s on http://127.0.0.1:%d/" % (Handler.root, a.port), flush=True)
    srv.serve_forever()


if __name__ == "__main__":
    main()
