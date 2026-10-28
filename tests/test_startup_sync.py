"""Startup persistence regressions; run with python3 -m unittest discover -s tests -v.

Requires Playwright and Chromium. The real file API uses a temporary data directory.
"""

from datetime import datetime, timedelta
from http.server import HTTPServer
import json
from pathlib import Path
import tempfile
import threading
import unittest

from playwright.sync_api import expect, sync_playwright

import gui_app


NOW = datetime.fromisoformat('2026-10-21T09:00:00+09:00')
YESTERDAY_SAVED = datetime.fromisoformat('2026-10-20T18:00:00+09:00')
STORAGE_KEY = 'time_tracker_data_v6'


def milliseconds(time):
    return int(time.timestamp() * 1000)


def task(name, started=None):
    result = dict(id=name, project='検証', name=name, plannedHours=1,
                  actualSeconds=3600, subtasks='', isRunning=started is not None)
    if started is not None:
        result.update(startedAt=milliseconds(started), runStartedAt=milliseconds(started),
                      runBaseSeconds=0)
    return result


def saved_data(date, tasks, saved_at):
    return dict(selectedDate=date, tasks=tasks,
                nextDayTasks=[dict(id='next', project='検証', name='次日予定',
                                   plannedHours=1, subtasks='')],
                activeTaskId=next((t['id'] for t in tasks if t['isRunning']), None),
                history={}, settings={}, savedAt=milliseconds(saved_at))


class StartupSyncTests(unittest.TestCase):
    def setUp(self):
        self.data = tempfile.TemporaryDirectory()
        self.original_paths = gui_app.DATA_DIR, gui_app.DATA_FILE
        gui_app.DATA_DIR = self.data.name
        gui_app.DATA_FILE = str(Path(self.data.name) / 'tracker_data.json')
        self.file = Path(gui_app.DATA_FILE)
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
        self.context = self.browser.new_context(timezone_id='Asia/Tokyo')
        self.page = self.context.new_page()
        self.page.clock.install(time=NOW - timedelta(seconds=1))
        self.page.clock.pause_at(NOW)
        self.url = f'http://127.0.0.1:{self.server.server_port}/index.html'

    def stop_server(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()
        gui_app.DATA_DIR, gui_app.DATA_FILE = self.original_paths
        self.data.cleanup()

    def seed(self, server=None, browser=None):
        if server is not None:
            self.file.write_text(json.dumps(server), encoding='utf-8')
        if browser is not None:
            self.context.add_init_script(
                f'localStorage.setItem({json.dumps(STORAGE_KEY)}, {json.dumps(json.dumps(browser))});')

    def expect_task(self, name):
        expect(self.page.locator('#task-list-container .task-card').filter(has_text=name)).to_be_visible()

    def persist_and_read(self):
        self.page.evaluate("window.dispatchEvent(new Event('pagehide'))")
        self.page.wait_for_function("""async () => {
            const local = JSON.parse(localStorage.getItem('time_tracker_data_v6'));
            const response = await fetch('/api/data');
            return response.ok && JSON.stringify(local) === JSON.stringify(await response.json());
        }""")
        return json.loads(self.file.read_text(encoding='utf-8'))

    def test_stale_browser_cannot_overwrite_newer_file_while_loading(self):
        newer = saved_data('2026-10-21', [task('新しいファイルの作業')], NOW - timedelta(minutes=1))
        newer['history']['2026-10-19'] = {'tasks': [task('保存済み履歴')], 'updatedAt': 1}
        stale = saved_data('2026-10-20', [task('古いブラウザーの作業')], YESTERDAY_SAVED)
        self.seed(server=newer, browser=stale)
        pending = []
        posts = []

        def hold_first_read(route):
            if route.request.method == 'GET' and not pending:
                pending.append(route)
            else:
                if route.request.method == 'POST':
                    posts.append(route.request.post_data)
                route.continue_()

        self.page.route('**/api/data', hold_first_read)
        with self.page.expect_request(lambda r: r.url.endswith('/api/data') and r.method == 'GET'):
            self.page.goto(self.url, wait_until='domcontentloaded')
        try:
            self.page.clock.fast_forward(20000)
            self.page.evaluate("""() => {
                Object.defineProperty(document, 'visibilityState', {configurable: true, value: 'hidden'});
                document.dispatchEvent(new Event('visibilitychange'));
                window.dispatchEvent(new Event('pagehide'));
            }""")
            self.assertEqual(posts, [], 'Startup must not save before reading the existing file')
            self.assertEqual(json.loads(self.file.read_text(encoding='utf-8')), newer)
        finally:
            pending[0].continue_()
        self.expect_task('新しいファイルの作業')
        saved = self.persist_and_read()
        self.assertEqual([t['name'] for t in saved['tasks']], ['新しいファイルの作業'])
        self.assertEqual(saved['history']['2026-10-19'], newer['history']['2026-10-19'])

    def test_previous_day_file_stops_timer_at_last_save(self):
        running = task('前日の作業', YESTERDAY_SAVED - timedelta(hours=1))
        self.seed(server=saved_data('2026-10-20', [running], YESTERDAY_SAVED))
        self.page.goto(self.url)
        expect(self.page.locator('#work-date')).to_have_value('2026-10-21')
        expect(self.page.locator('.task-card.active-running')).to_have_count(0)
        saved = self.persist_and_read()
        self.assertEqual(saved['selectedDate'], '2026-10-21')
        self.assertIsNone(saved['activeTaskId'])
        self.assertEqual(saved['history']['2026-10-20']['tasks'][0]['actualSeconds'], 3600)
        self.assertFalse(any(t['isRunning'] for t in saved['tasks']))

    def test_newer_browser_data_wins_over_older_file(self):
        server = saved_data('2026-10-21', [task('古いファイルの作業')], NOW - timedelta(hours=1))
        local = saved_data('2026-10-21', [task('新しいブラウザーの作業')], NOW - timedelta(minutes=1))
        self.seed(server=server, browser=local)
        self.page.goto(self.url)
        self.expect_task('新しいブラウザーの作業')
        saved = self.persist_and_read()
        self.assertEqual([t['name'] for t in saved['tasks']], ['新しいブラウザーの作業'])

    def test_missing_file_keeps_browser_data_and_can_save(self):
        self.seed(browser=saved_data('2026-10-21', [task('ブラウザーだけの作業')], NOW))
        self.page.goto(self.url)
        self.expect_task('ブラウザーだけの作業')
        saved = self.persist_and_read()
        self.assertEqual([t['name'] for t in saved['tasks']], ['ブラウザーだけの作業'])

    def test_newer_browser_deletion_is_not_undone_by_older_file(self):
        server = saved_data('2026-10-21', [task('削除済みの作業')], NOW - timedelta(hours=1))
        local = saved_data('2026-10-21', [], NOW - timedelta(minutes=1))
        self.seed(server=server, browser=local)
        self.page.goto(self.url)
        expect(self.page.locator('#work-date')).to_have_value('2026-10-21')
        expect(self.page.locator('#task-list-container .task-card')).to_have_count(0)
        saved = self.persist_and_read()
        self.assertEqual(saved['tasks'], [])
        self.assertEqual(saved['nextDayTasks'], local['nextDayTasks'])

    def test_unavailable_api_keeps_browser_workflow_usable(self):
        self.seed(browser=saved_data('2026-10-21', [task('オフライン作業')], NOW))
        self.page.route('**/api/data', lambda route: route.abort())
        self.page.goto(self.url)
        self.expect_task('オフライン作業')
        self.page.locator('#input-task-name').fill('オフラインで追加')
        self.page.locator('#btn-add-task').click()
        self.expect_task('オフラインで追加')
        saved = self.page.evaluate(f'JSON.parse(localStorage.getItem({json.dumps(STORAGE_KEY)}))')
        self.assertEqual([t['name'] for t in saved['tasks']], ['オフライン作業', 'オフラインで追加'])

    def test_same_day_running_timer_continues(self):
        running = task('当日の計測', NOW - timedelta(hours=1))
        running['actualSeconds'] = 1800
        self.seed(server=saved_data('2026-10-21', [running], NOW - timedelta(minutes=30)))
        self.page.goto(self.url)
        self.expect_task('当日の計測')
        expect(self.page.locator('#active-timer-clock')).to_have_text('01:00:00')
        self.page.clock.fast_forward(1000)
        expect(self.page.locator('#active-timer-clock')).to_have_text('01:00:01')
        saved = self.persist_and_read()
        self.assertTrue(saved['tasks'][0]['isRunning'])
        self.assertEqual(saved['tasks'][0]['actualSeconds'], 3601)
        self.assertEqual(saved['activeTaskId'], running['id'])

    def check_other_tab_update_during_sync(self, fail_read=False):
        original = saved_data('2026-10-21', [task('起動時のブラウザー状態')], NOW - timedelta(minutes=20))
        server = saved_data('2026-10-21', [task('ファイルの作業')], NOW - timedelta(minutes=10))
        updated = saved_data('2026-10-21', [task('別タブで更新した作業')], NOW - timedelta(minutes=1))
        self.seed(server=server, browser=original)
        pending = []

        def hold_first_read(route):
            if route.request.method == 'GET' and not pending:
                pending.append(route)
            else:
                route.continue_()

        self.page.route('**/api/data', hold_first_read)
        with self.page.expect_request(lambda r: r.url.endswith('/api/data') and r.method == 'GET'):
            self.page.goto(self.url, wait_until='domcontentloaded')
        other_page = self.context.new_page()
        # A same-origin page writes storage without starting another application timer.
        other_page.goto(self.url.replace('/index.html', '/styles.css'))
        other_page.evaluate("data => localStorage.setItem('time_tracker_data_v6', JSON.stringify(data))", updated)
        other_page.close()
        if fail_read:
            pending[0].abort()
        else:
            pending[0].continue_()
        self.expect_task('別タブで更新した作業')
        if fail_read:
            # Without a known file revision, local edits are preserved but must
            # not overwrite a file whose current contents could not be read.
            self.page.evaluate("window.dispatchEvent(new Event('pagehide'))")
            saved = self.page.evaluate(f'JSON.parse(localStorage.getItem({json.dumps(STORAGE_KEY)}))')
            self.assertEqual(json.loads(self.file.read_text()), server)
            expect(self.page.locator('#sync-status-text')).to_contain_text('ブラウザ保存のみ')
        else:
            saved = self.persist_and_read()
        self.assertEqual([t['name'] for t in saved['tasks']], ['別タブで更新した作業'])

    def test_other_tab_update_wins_while_file_read_is_pending(self):
        self.check_other_tab_update_during_sync()

    def test_other_tab_update_is_kept_when_pending_file_read_fails(self):
        self.check_other_tab_update_during_sync(fail_read=True)

    def test_existing_file_is_loaded_before_default_template(self):
        self.seed(server=saved_data('2026-10-21', [task('ファイルから復元')], NOW))
        requests = []
        pending = []
        self.page.on('request', lambda request: requests.append(request.url))
        self.page.route('**/api/data', lambda route: pending.append(route))
        with self.page.expect_request(lambda r: r.url.endswith('/api/data')):
            self.page.goto(self.url, wait_until='domcontentloaded')
        try:
            self.page.clock.fast_forward(1000)
            self.assertFalse(any(url.endswith('/DailyReportFormat.txt') for url in requests))
        finally:
            for route in pending:
                route.continue_()
            self.page.unroute('**/api/data')
        self.expect_task('ファイルから復元')
        expect(self.page.locator('#task-list-container .task-card')).to_have_count(1)
