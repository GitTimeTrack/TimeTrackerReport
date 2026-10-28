"""Browser regression tests: python3 -m unittest discover -s tests -v.

Requires Playwright and Chromium (playwright install chromium, or /usr/bin/chromium).
Application data is isolated in a temporary directory.
"""

import json
from pathlib import Path
import tempfile
import threading
import unittest
from http.server import HTTPServer
from urllib.request import urlopen

from playwright.sync_api import expect, sync_playwright

import gui_app


class PlannedHoursTests(unittest.TestCase):
    def setUp(self):
        self.data = tempfile.TemporaryDirectory()
        self.original_paths = gui_app.DATA_DIR, gui_app.DATA_FILE
        gui_app.DATA_DIR = self.data.name
        gui_app.DATA_FILE = str(Path(self.data.name) / 'tracker_data.json')
        self.server = HTTPServer(('127.0.0.1', 0), gui_app.QuietHTTPRequestHandler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.addCleanup(self.stop_server)
        self.playwright = sync_playwright().start()
        self.addCleanup(self.playwright.stop)
        chromium = Path('/usr/bin/chromium')
        options = {'executable_path': str(chromium)} if chromium.exists() else {}
        self.browser = self.playwright.chromium.launch(headless=True, **options)
        self.addCleanup(self.browser.close)
        self.page = self.browser.new_page()
        self.errors = []
        self.page.on('pageerror', lambda error: self.errors.append(str(error)))
        self.url = f'http://127.0.0.1:{self.server.server_port}'
        self.page.goto(self.url + '/index.html')
        expect(self.page.locator('#task-list-container .task-card').first).to_be_visible()

    def stop_server(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()
        gui_app.DATA_DIR, gui_app.DATA_FILE = self.original_paths
        self.data.cleanup()

    def test_planned_hours_add_edit_and_restore(self):
        for next_day in (True, False):
            prefix = 'next-' if next_day else ''
            container = '#next-task-list-container' if next_day else '#task-list-container'
            edit = '.btn-edit-next-task' if next_day else '.btn-edit-task'
            for value, expected in [('0', 0), ('0.0', 0), ('0.5', 0.5), ('', 1)]:
                with self.subTest(next_day=next_day, value=value):
                    name = f'予定時間検証-{next_day}-{value or "empty"}'
                    self.page.locator(f'#input-{prefix}task-name').fill(name)
                    self.page.locator(f'#input-{prefix}planned').fill(value)
                    self.page.locator(f'#btn-add-{prefix}task').click()
                    card = self.page.locator(f'{container} .task-card').filter(has_text=name)
                    expect(card.locator('.task-time-text').first).to_contain_text(f'予定: {expected:.1f}h')
                    card.locator(edit).click()
                    expect(self.page.locator('#edit-planned-hours')).to_have_value(f'{expected:.1f}')
                    self.page.locator('#btn-save-edit-task').click()
                    expect(card.locator('.task-time-text').first).to_contain_text(f'予定: {expected:.1f}h')
                    if expected > 0:
                        card.locator(edit).click()
                        self.page.locator('#edit-planned-hours').fill('0')
                        self.page.locator('#btn-save-edit-task').click()
                        expect(card.locator('.task-time-text').first).to_contain_text('予定: 0.0h')

        # Wait for all UI saves to reach the real file API before reloading.
        self.page.wait_for_function("""async () => {
            const local = JSON.parse(localStorage.getItem('time_tracker_data_v6'));
            const saved = await (await fetch('/api/data')).json();
            return JSON.stringify(local) === JSON.stringify(saved);
        }""")
        with urlopen(self.url + '/api/data') as response:
            saved = json.load(response)
        for key in ('tasks', 'nextDayTasks'):
            added = [t for t in saved[key] if t['name'].startswith('予定時間検証-')]
            self.assertEqual(len(added), 4)
            self.assertTrue(all(t['plannedHours'] == 0 for t in added))
        with self.browser.new_context() as context:
            page = context.new_page()
            page.goto(self.url + '/index.html')
            for container in ('#task-list-container', '#next-task-list-container'):
                cards = page.locator(f'{container} .task-card').filter(has_text='予定時間検証-')
                expect(cards).to_have_count(4)
                for card in cards.all():
                    expect(card.locator('.task-time-text').first).to_contain_text('予定: 0.0h')
        self.assertEqual(self.errors, [])
