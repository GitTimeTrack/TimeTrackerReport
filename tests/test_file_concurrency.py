"""Real HTTP preconditions protect files against stale and concurrent writers."""

from concurrent.futures import ThreadPoolExecutor
from http.server import ThreadingHTTPServer
import json
from pathlib import Path
import tempfile
import threading
import unittest
from urllib.error import HTTPError
from urllib.request import Request, urlopen
from unittest.mock import patch

import gui_app


class FileConcurrencyTests(unittest.TestCase):
    def setUp(self):
        self.data = tempfile.TemporaryDirectory()
        self.addCleanup(self.data.cleanup)
        self.file = Path(self.data.name) / 'tracker_data.json'
        for key, value in [('DATA_DIR', self.data.name), ('DATA_FILE', str(self.file))]:
            patcher = patch.object(gui_app, key, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        self.server = ThreadingHTTPServer(('127.0.0.1', 0), gui_app.QuietHTTPRequestHandler)
        thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(self.server.server_close)
        self.addCleanup(self.server.shutdown)
        self.url = f'http://127.0.0.1:{self.server.server_port}/api/data'

    def post(self, data, headers):
        request = Request(self.url, data=json.dumps(data).encode(), headers={
            'Content-Type': 'application/json', **headers})
        try:
            with urlopen(request) as response:
                return response.status, response.headers.get('ETag')
        except HTTPError as error:
            error.close()
            return error.code, None

    def test_create_update_and_reject_missing_or_stale_preconditions(self):
        self.assertEqual(self.post({'v': 0}, {})[0], 428)
        self.assertFalse(self.file.exists())
        status, revision = self.post({'v': 1}, {'If-None-Match': '*'})
        self.assertEqual(status, 200)
        self.assertEqual(self.post({'v': 2}, {'If-None-Match': '*'})[0], 412)
        with urlopen(self.url) as response:
            self.assertEqual(response.headers['ETag'], revision)
            self.assertEqual(response.headers['Cache-Control'], 'no-store')
            self.assertEqual(json.load(response), {'v': 1})
        status, next_revision = self.post({'v': 2}, {'If-Match': revision})
        self.assertEqual(status, 200)
        self.assertNotEqual(next_revision, revision)
        self.assertEqual(self.post({'v': 3}, {'If-Match': revision})[0], 412)
        self.assertEqual(json.loads(self.file.read_text()), {'v': 2})
        # Direct external edits also invalidate the old revision.
        self.file.write_text('{"v": 4}')
        self.assertEqual(self.post({'v': 5}, {'If-Match': next_revision})[0], 412)
        self.assertEqual(json.loads(self.file.read_text()), {'v': 4})

    def test_concurrent_writers_cannot_both_replace_same_revision(self):
        _, revision = self.post({'v': 0}, {'If-None-Match': '*'})
        barrier = threading.Barrier(2)
        def write(value):
            barrier.wait()
            return self.post({'v': value}, {'If-Match': revision})[0]
        with ThreadPoolExecutor(max_workers=2) as executor:
            results = list(executor.map(write, [1, 2]))
        self.assertEqual(sorted(results), [200, 412])
        self.assertEqual(json.loads(self.file.read_text())['v'], 1 if results[0] == 200 else 2)

    def test_disk_failure_does_not_replace_existing_file(self):
        _, revision = self.post({'v': 0}, {'If-None-Match': '*'})
        with patch.object(gui_app.os, 'replace', side_effect=PermissionError('read only')):
            self.assertEqual(self.post({'v': 1}, {'If-Match': revision})[0], 500)
        self.assertEqual(json.loads(self.file.read_text()), {'v': 0})
        self.assertEqual(self.post({'v': 1}, {'If-Match': revision})[0], 200)
