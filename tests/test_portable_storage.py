"""Verify portable data survives replacement of a onefile extraction directory.

These standard-library tests simulate frozen process paths and use the real HTTP API.
They do not claim to build or execute a Windows binary.
"""

from contextlib import contextmanager
from http.server import HTTPServer
import importlib.util
import json
from pathlib import Path
import shutil
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch
from urllib.request import Request, urlopen

import gui_app


class PortableStorageTests(unittest.TestCase):
    def load_launcher(self, resource_dir, executable, frozen):
        resource_dir.mkdir()
        source = resource_dir / 'gui_app.py'
        shutil.copyfile(gui_app.__file__, source)
        (resource_dir / 'index.html').write_text(resource_dir.name, encoding='utf-8')
        spec = importlib.util.spec_from_file_location('portable_launcher_test', source)
        module = importlib.util.module_from_spec(spec)
        with patch.object(sys, 'frozen', frozen, create=True), patch.object(sys, 'executable', str(executable)):
            spec.loader.exec_module(module)
        return module

    @contextmanager
    def serve(self, module):
        server = HTTPServer(('127.0.0.1', 0), module.QuietHTTPRequestHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            yield f'http://127.0.0.1:{server.server_port}'
        finally:
            server.shutdown()
            server.server_close()
            thread.join()

    def test_source_launch_keeps_data_next_to_source(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            module = self.load_launcher(root / 'source', root / 'python', frozen=False)
            self.assertEqual(Path(module.DATA_DIR), root / 'source' / 'data')

    def test_frozen_data_survives_extraction_cleanup_and_restart(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            executable = root / 'portable' / 'TimeTrackerReport.exe'
            first = self.load_launcher(root / '_MEI_first', executable, frozen=True)
            saved = {'tasks': [{'name': '再起動後も残す作業', 'plannedHours': 0}], 'savedAt': 123}
            expected_file = executable.parent / 'data' / 'tracker_data.json'
            self.assertEqual(Path(first.DATA_FILE), expected_file)
            with self.serve(first) as url:
                with urlopen(url + '/index.html') as response:
                    self.assertEqual(response.read().decode(), '_MEI_first')
                request = Request(url + '/api/data', data=json.dumps(saved).encode(),
                                  headers={'Content-Type': 'application/json', 'If-None-Match': '*'})
                with urlopen(request) as response:
                    self.assertEqual(json.load(response)['status'], 'ok')
            shutil.rmtree(root / '_MEI_first')
            self.assertEqual(json.loads(expected_file.read_text()), saved)

            second = self.load_launcher(root / '_MEI_second', executable, frozen=True)
            with self.serve(second) as url:
                with urlopen(url + '/api/data') as response:
                    self.assertEqual(json.load(response), saved)
                with urlopen(url + '/index.html') as response:
                    self.assertEqual(response.read().decode(), '_MEI_second')
            self.assertFalse((root / '_MEI_second' / 'data').exists())
