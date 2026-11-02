"""Loopback preview with an explicit download allowlist. Never serves the repository."""
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import argparse
import os
from urllib.parse import unquote, urlsplit

REPO = Path(__file__).resolve().parent.parent
ROOT = REPO / 'artifacts/website/dist'
DOWNLOAD = REPO / 'artifacts/website/release/Mani-Beta-0.1-Windows-x64-Setup.exe'

class Handler(SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=str(ROOT), **kwargs)

    def send_head(self):
        request_path = unquote(urlsplit(self.path).path)
        if request_path == '/downloads/' + DOWNLOAD.name:
            if not DOWNLOAD.is_file():
                self.send_error(404)
                return None
            stream = DOWNLOAD.open('rb')
            self.send_response(200)
            self.send_header('Content-Type', 'application/octet-stream')
            self.send_header('Content-Length', str(os.fstat(stream.fileno()).st_size))
            self.send_header('Content-Disposition', 'attachment; filename="' + DOWNLOAD.name + '"')
            self.end_headers()
            return stream
        return super().send_head()

    def end_headers(self):
        self.send_header('X-Content-Type-Options', 'nosniff')
        self.send_header('Referrer-Policy', 'strict-origin-when-cross-origin')
        super().end_headers()

    def list_directory(self, path):
        self.send_error(404)
        return None

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=4173)
    args = parser.parse_args()
    server = ThreadingHTTPServer(('127.0.0.1', args.port), Handler)
    print(f'Local: http://127.0.0.1:{args.port}', flush=True)
    server.serve_forever()
