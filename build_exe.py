#!/usr/bin/env python3
"""
TimeTrackerReport - Single File Portable .exe Build Script (build_exe.py)
PyInstallerを使用して「インストール不要・単一.exeファイル」を生成します。

使用方法:
  python build_exe.py
または PyInstallerがインストールされているWindows環境で:
  pyinstaller --noconfirm --onedir --windowed --name "TimeTrackerReport" gui_app.py
"""

import os
import sys
import subprocess

def build():
    print("=" * 60)
    print(" TimeTrackerReport - ポータブル .exe ビルド処理を開始します...")
    print("=" * 60)

    try:
        import PyInstaller
    except ImportError:
        print("[!] PyInstaller が見つかりません。以下を実行してインストールしてください:")
        print("    pip install pyinstaller")
        return

    script_dir = os.path.dirname(os.path.abspath(__file__))
    os.chdir(script_dir)

    cmd = [
        sys.executable, "-m", "PyInstaller",
        "--noconfirm",
        "--onefile",
        "--windowed",
        "--name", "TimeTrackerReport",
        "--add-data", f"index.html{os.pathsep}.",
        "--add-data", f"styles.css{os.pathsep}.",
        "--add-data", f"app.js{os.pathsep}.",
        "--add-data", f"DailyReportFormat.txt{os.pathsep}.",
        "gui_app.py"
    ]

    print("実行コマンド:", " ".join(cmd))
    result = subprocess.run(cmd)

    if result.returncode == 0:
        print("\n" + "=" * 60)
        print(" [SUCCESS] ビルドが正常に完了しました！")
        print(" 生成されたファイル: dist/TimeTrackerReport.exe")
        print(" この.exeファイル単体でどのWindows端末でも即座に動作します（インストール不要）。")
        print("=" * 60)
    else:
        print("\n[ERROR] ビルド中にエラーが発生しました。")

if __name__ == "__main__":
    build()
