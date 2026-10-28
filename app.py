#!/usr/bin/env python3
"""
時間管理 & 日報作成アプリ ローカルサーバー & ブラウザ自動起動スクリプト (app.py)
Python環境が使える場合のスタンドアロン起動ランチャーです。
"""

import http.server
import socketserver
import webbrowser
import os
import sys

from gui_app import QuietHTTPRequestHandler, PORT, DIRECTORY

def run():
    os.chdir(DIRECTORY)
    url = f"http://127.0.0.1:{PORT}/index.html"
    
    print("=" * 60)
    print(" ⏱️ 時間管理 & 日報作成アプリ (Windows Native Web Server) を起動しています...")
    print(f" URL: {url}")
    print(" データ保存先: data/tracker_data.json")
    print(" 終了するには Ctrl+C を押してください。")
    print("=" * 60)

    # Open browser automatically
    webbrowser.open(url)

    try:
        with socketserver.TCPServer(("127.0.0.1", PORT), QuietHTTPRequestHandler) as httpd:
            httpd.serve_forever()
    except KeyboardInterrupt:
        print("\nサーバーを停止しました。")
        sys.exit(0)

if __name__ == "__main__":
    run()
