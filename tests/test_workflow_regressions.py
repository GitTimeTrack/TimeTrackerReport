"""Browser regressions for alerts, templates, backups, analytics and CSV exports.

Run: python3 -m unittest discover -s tests -v (requires Playwright and Chromium).
All application data is isolated in a temporary directory.
"""

import csv
from datetime import datetime, timedelta
from http.server import HTTPServer
import io
import json
from pathlib import Path
import tempfile
import threading
import unittest

from playwright.sync_api import expect, sync_playwright

import gui_app


NOW = datetime.fromisoformat('2026-10-21T09:00:00+09:00')


class WorkflowRegressionTests(unittest.TestCase):
    def setUp(self):
        self.data = tempfile.TemporaryDirectory()
        self.original_paths = gui_app.DATA_DIR, gui_app.DATA_FILE
        gui_app.DATA_DIR = self.data.name
        gui_app.DATA_FILE = str(Path(self.data.name) / 'tracker_data.json')
        initial = {
            'selectedDate': '2026-10-21',
            'tasks': [dict(id=f'task{i}', project='検証', name=f'作業{i}',
                           plannedHours=1, actualSeconds=0, isRunning=False, subtasks='')
                      for i in (1, 2)],
            'nextDayTasks': [], 'history': {}, 'settings': {},
            'activeTaskId': None, 'savedAt': int(NOW.timestamp() * 1000)
        }
        Path(gui_app.DATA_FILE).write_text(json.dumps(initial), encoding='utf-8')
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
        self.page = self.browser.new_page(timezone_id='Asia/Tokyo')
        self.errors = []
        self.page.on('pageerror', lambda error: self.errors.append(str(error)))
        self.page.clock.install(time=NOW - timedelta(seconds=1))
        self.page.clock.pause_at(NOW)
        self.page.goto(f'http://127.0.0.1:{self.server.server_port}/index.html')
        expect(self.page.locator('#task-list-container .task-card')).to_have_count(2)

    def tearDown(self):
        self.assertEqual(self.errors, [])

    def stop_server(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()
        gui_app.DATA_DIR, gui_app.DATA_FILE = self.original_paths
        self.data.cleanup()

    def set_minutes(self, selector, value):
        self.page.locator(selector).fill(value)
        self.page.locator(selector).dispatch_event('change')

    def local_data(self):
        return self.page.evaluate("JSON.parse(localStorage.getItem('time_tracker_data_v6'))")

    def wait_for_save(self):
        self.page.wait_for_function("""async () => {
            const local = JSON.parse(localStorage.getItem('time_tracker_data_v6'));
            const saved = await (await fetch('/api/data')).json();
            return JSON.stringify(local) === JSON.stringify(saved);
        }""")

    def test_idle_settings_apply_and_survive_new_browser(self):
        self.page.locator('#nav-btn-settings').click()
        self.set_minutes('#set-idle-minutes', '5')
        self.page.locator('#set-enable-idle-alert').uncheck()
        self.set_minutes('#set-long-minutes', '30')
        self.page.locator('#set-enable-long-alert').uncheck()
        self.assertEqual(self.local_data()['settings'], dict(
            enableIdleAlert=False, idleAlertMinutes=5,
            enableLongAlert=False, longAlertMinutes=30))
        self.page.clock.fast_forward(16 * 60 * 1000)
        expect(self.page.locator('#forget-prevention-banner')).to_be_hidden()
        self.wait_for_save()
        # A fresh profile must restore the saved settings without LocalStorage.
        context = self.browser.new_context(timezone_id='Asia/Tokyo')
        self.page = context.new_page()
        self.page.on('pageerror', lambda error: self.errors.append(str(error)))
        self.page.clock.install(time=NOW - timedelta(seconds=1))
        self.page.clock.pause_at(NOW)
        self.page.goto(f'http://127.0.0.1:{self.server.server_port}/index.html')
        expect(self.page.locator('#task-list-container .task-card')).to_have_count(2)
        self.page.locator('#nav-btn-settings').click()
        expect(self.page.locator('#set-enable-idle-alert')).not_to_be_checked()
        expect(self.page.locator('#set-enable-long-alert')).not_to_be_checked()
        expect(self.page.locator('#set-idle-minutes')).to_have_value('5')
        expect(self.page.locator('#set-long-minutes')).to_have_value('30')
        self.set_minutes('#set-idle-minutes', '')
        expect(self.page.locator('#set-idle-minutes')).to_have_value('5')
        self.set_minutes('#set-long-minutes', '1')
        expect(self.page.locator('#set-long-minutes')).to_have_value('30')
        self.page.locator('#set-enable-idle-alert').check()
        self.page.clock.fast_forward(5 * 60 * 1000)
        expect(self.page.locator('#forget-prevention-banner')).to_be_visible()
        self.page.locator('#set-enable-idle-alert').uncheck()
        expect(self.page.locator('#forget-prevention-banner')).to_be_hidden()

    def test_long_alert_uses_configured_threshold_and_disable(self):
        self.page.locator('#nav-btn-settings').click()
        self.set_minutes('#set-long-minutes', '15')
        self.page.locator('#nav-btn-tracker').click()
        self.page.locator('#task-list-container .btn-toggle-timer').nth(1).click()
        self.page.clock.fast_forward(15 * 60 * 1000)
        expect(self.page.locator('#modal-forget-stop')).to_be_visible()
        self.page.locator('#btn-modal-keep-running').click()
        self.page.locator('#nav-btn-settings').click()
        self.page.locator('#set-enable-long-alert').uncheck()
        self.page.clock.fast_forward(120 * 60 * 1000)
        expect(self.page.locator('#modal-forget-stop')).to_be_hidden()
        self.assertFalse(self.local_data()['settings']['enableLongAlert'])

    def test_template_preserves_zero_and_defaults_only_missing_hours(self):
        projects = '\n'.join([
            '◆ゼロ明細（予定 0h）', '・ゼロ作業A', '・ゼロ作業B',
            '◆ゼロ見出し（予定 0h）',
            '◆未指定明細', '・既定作業A', '・既定作業B',
            '◆未指定見出し',
            '◆小数（予定 0.2h）', '・小数作業A', '・小数作業B',
            '◆丸め（予定 0.1h）', '・丸め作業A', '・丸め作業B', '・丸め作業C'
        ])
        template = '【本日の作業】\n' + projects + '\n【次日の作業予定】\n' + projects
        self.page.locator('#input-template-file').set_input_files(dict(
            name='planned.txt', mimeType='text/plain', buffer=template.encode()))
        expect(self.page.locator('#task-list-container .task-card')).to_have_count(11)
        self.wait_for_save()
        saved = self.local_data()
        for key in ('tasks', 'nextDayTasks'):
            values = {}
            for task in saved[key]:
                values.setdefault(task['project'], []).append(task['plannedHours'])
            self.assertEqual(values, {
                'ゼロ明細': [0, 0], 'ゼロ見出し': [0], '未指定明細': [1, 1],
                '未指定見出し': [1], '小数': [0.1, 0.1], '丸め': [0, 0, 0]
            })

    def test_running_backup_restores_active_timer_and_settings(self):
        self.page.locator('#nav-btn-settings').click()
        self.set_minutes('#set-long-minutes', '30')
        self.page.locator('#set-enable-idle-alert').uncheck()
        self.page.locator('#nav-btn-tracker').click()
        self.page.locator('#task-list-container .btn-toggle-timer').nth(1).click()
        self.page.clock.fast_forward(2000)
        self.page.locator('#nav-btn-settings').click()
        with self.page.expect_download() as event:
            self.page.locator('#btn-export-backup-json').click()
        backup = Path(event.value.path()).read_bytes()
        self.page.locator('#set-enable-idle-alert').check()
        self.set_minutes('#set-long-minutes', '60')
        self.page.once('dialog', lambda dialog: dialog.accept())
        self.page.locator('#input-import-backup-json').set_input_files(dict(
            name='backup.json', mimeType='application/json', buffer=backup))
        expect(self.page.locator('#set-long-minutes')).to_have_value('30')
        expect(self.page.locator('#set-enable-idle-alert')).not_to_be_checked()
        self.page.wait_for_function("JSON.parse(localStorage.getItem('time_tracker_data_v6')).activeTaskId === 'task2'")
        expect(self.page.locator('#timer-toggle-text')).to_have_text('一時停止')
        self.page.locator('#btn-global-toggle').click()
        expect(self.page.locator('.task-card.active-running')).to_have_count(0)
        self.wait_for_save()
        self.assertFalse(any(t['isRunning'] for t in self.local_data()['tasks']))
        self.assertEqual(self.local_data()['tasks'][1]['actualSeconds'], 2)

    def test_month_navigation_handles_month_end_leap_year_and_year_boundary(self):
        for date, next_month, original in [
            ('2026-01-31', '2026/02月 全体', '2026/01月 全体'),
            ('2024-01-31', '2024/02月 全体', '2024/01月 全体'),
            ('2026-12-31', '2027/01月 全体', '2026/12月 全体')
        ]:
            with self.subTest(date=date):
                self.page.locator('#work-date').fill(date)
                self.page.locator('#work-date').dispatch_event('change')
                self.page.locator('#nav-btn-analytics').click()
                self.page.locator('#btn-period-month').click()
                expect(self.page.locator('#analytics-period-range')).to_have_text(original)
                self.page.locator('#btn-period-next').click()
                expect(self.page.locator('#analytics-period-range')).to_have_text(next_month)
                self.page.locator('#btn-period-prev').click()
                expect(self.page.locator('#analytics-period-range')).to_have_text(original)
        self.page.locator('#work-date').fill('2026-03-31')
        self.page.locator('#work-date').dispatch_event('change')
        self.page.locator('#btn-period-prev').click()
        expect(self.page.locator('#analytics-period-range')).to_have_text('2026/02月 全体')

    def test_report_csv_round_trips_quotes_commas_and_multiline_notes(self):
        project, name, notes = 'A",B日本語', 'X"Y,確認', '行1,"引用"\n行2'
        for prefix in ('', 'next-'):
            self.page.locator(f'#input-{prefix}project').fill(project)
            self.page.locator(f'#input-{prefix}task-name').fill(name)
            self.page.locator(f'#input-{prefix}subtasks').fill(notes)
            self.page.locator(f'#btn-add-{prefix}task').click()
        self.page.locator('#nav-btn-report').click()
        with self.page.expect_download() as event:
            self.page.locator('#btn-download-csv').click()
        contents = Path(event.value.path()).read_text(encoding='utf-8-sig')
        rows = list(csv.reader(io.StringIO(contents), strict=True))
        self.assertTrue(all(len(row) == 7 for row in rows))
        added = [row for row in rows if row[2] == project]
        self.assertEqual(len(added), 2)
        self.assertEqual({row[1] for row in added}, {'本日', '次日予定'})
        for row in added:
            self.assertEqual(row[3], name)
            self.assertEqual(row[6], notes)
