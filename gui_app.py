#!/usr/bin/env python3
"""
TimeTrackerReport - Windows Native Desktop GUI Launcher (gui_app.py)
ポータブル実行対応（インストール不要、単一exe化対応）
"""

import os
import sys
import json
import hashlib
import threading
import http.server
import socketserver
import webbrowser
import subprocess

PORT = 8899
DIRECTORY = os.path.dirname(os.path.abspath(__file__))
# onefile の同梱ファイルは一時展開先、ユーザーデータは実行ファイルの隣に置く。
DATA_ROOT = os.path.dirname(os.path.abspath(sys.executable)) if getattr(sys, 'frozen', False) else DIRECTORY
DATA_DIR = os.path.join(DATA_ROOT, "data")
DATA_FILE = os.path.join(DATA_DIR, "tracker_data.json")

DATA_LOCK = threading.Lock()


def file_revision(content):
    return '"' + hashlib.sha256(content).hexdigest() + '"'


class QuietHTTPRequestHandler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=DIRECTORY, **kwargs)
    
    def log_message(self, format, *args):
        pass  # Suppress console logging for clean execution

    def do_GET(self):
        if self.path == '/api/data':
            self.handle_get_data()
        else:
            super().do_GET()

    def do_POST(self):
        if self.path == '/api/data':
            self.handle_post_data()
        elif self.path == '/api/open-data-folder':
            self.handle_open_data_folder()
        else:
            self.send_error(404, "Endpoint not found")

    def handle_get_data(self):
        try:
            if os.path.exists(DATA_FILE):
                with open(DATA_FILE, 'rb') as f:
                    content = f.read()
                self.send_response(200)
                self.send_header('Content-Type', 'application/json; charset=utf-8')
                self.send_header('Access-Control-Allow-Origin', '*')
                self.send_header('ETag', file_revision(content))
                self.send_header('Cache-Control', 'no-store')
                self.end_headers()
                self.wfile.write(content)
            else:
                self.send_response(404)
                self.send_header('Content-Type', 'application/json')
                self.end_headers()
                self.wfile.write(b'{"status":"not_found"}')
        except Exception as e:
            self.send_response(500)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(json.dumps({"error": str(e)}).encode('utf-8'))

    def handle_post_data(self):
        try:
            content_length = int(self.headers.get('Content-Length', 0))
            post_body = self.rfile.read(content_length)
            
            # Validate JSON
            parsed = json.loads(post_body.decode('utf-8'))
            
            # Compare and replace under one lock, including concurrent HTTP handlers.
            with DATA_LOCK:
                exists = os.path.exists(DATA_FILE)
                current = None
                if exists:
                    with open(DATA_FILE, 'rb') as f:
                        current = file_revision(f.read())
                expected = self.headers.get('If-Match')
                create_only = self.headers.get('If-None-Match') == '*'
                if expected is None and not create_only:
                    self.send_error(428, "A file revision is required")
                    return
                if (create_only and exists) or (not create_only and expected != current):
                    self.send_error(412, "The file changed; reload before saving")
                    return
                content = json.dumps(parsed, ensure_ascii=False, indent=2).encode('utf-8')
                os.makedirs(DATA_DIR, exist_ok=True)
                temp_file = DATA_FILE + ".tmp"
                with open(temp_file, 'wb') as f:
                    f.write(content)
                os.replace(temp_file, DATA_FILE)

            self.send_response(200)
            self.send_header('ETag', file_revision(content))
            self.send_header('Content-Type', 'application/json')
            self.send_header('Access-Control-Allow-Origin', '*')
            self.end_headers()
            self.wfile.write(b'{"status":"ok","path":"data/tracker_data.json"}')
        except Exception as e:
            self.send_response(500)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(json.dumps({"error": str(e)}).encode('utf-8'))

    def handle_open_data_folder(self):
        try:
            os.makedirs(DATA_DIR, exist_ok=True)
            if sys.platform == 'win32':
                os.startfile(DATA_DIR)
            elif sys.platform == 'darwin':
                subprocess.Popen(['open', DATA_DIR])
            else:
                subprocess.Popen(['xdg-open', DATA_DIR])
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(b'{"status":"opened"}')
        except Exception as e:
            self.send_response(500)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(json.dumps({"error": str(e)}).encode('utf-8'))

def start_server():
    os.chdir(DIRECTORY)
    with socketserver.TCPServer(("127.0.0.1", PORT), QuietHTTPRequestHandler) as httpd:
        httpd.serve_forever()

def launch_native_window():
    """
    ウィンドウを起動する。
    戻り値: True = ネイティブウィンドウ(pywebview)が閉じられるまでブロックした
            False = 外部ブラウザを起動した（サーバーを維持し続ける必要がある）
    """
    url = f"http://127.0.0.1:{PORT}/index.html"
    
    # Try pywebview for pure native window if installed
    try:
        import webview
        webview.create_window('TimeTrackerReport', url, width=1280, height=850, resizable=True)
        webview.start()
        return True
    except ImportError:
        pass

    # Fallback to Edge App Mode on Windows (Standalone App Window with no browser bar)
    if sys.platform == 'win32':
        import subprocess
        edge_path = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
        if not os.path.exists(edge_path):
            edge_path = r"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
            
        if os.path.exists(edge_path):
            cmd = f'"{edge_path}" --app={url} --window-size=1280,850'
            subprocess.Popen(cmd, shell=True)
            return False

    # Standard browser fallback
    webbrowser.open(url)
    return False

if __name__ == '__main__':
    # Start server in background thread
    server_thread = threading.Thread(target=start_server, daemon=True)
    server_thread.start()
    
    # Launch GUI Window
    blocked_until_closed = launch_native_window()

    # 外部ブラウザ起動時はここで終了すると daemon スレッドのサーバーも停止し、
    # ページの再読み込み（Edge のスリープタブ復帰など）に失敗するため、サーバーを維持する。
    # （既に別プロセスが同ポートで起動済みの場合はサーバースレッドが即終了し、そのまま抜ける）
    if not blocked_until_closed:
        try:
            server_thread.join()
        except KeyboardInterrupt:
            pass
