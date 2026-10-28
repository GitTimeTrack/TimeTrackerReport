"""Data-loss regressions using isolated HTTP storage and real Chromium profiles."""

import copy
import json
from pathlib import Path
import unittest

from playwright.sync_api import expect

import gui_app
import test_workflow_regressions as workflow


class DataProtectionTests(unittest.TestCase):
    setUp = workflow.WorkflowRegressionTests.setUp
    tearDown = workflow.WorkflowRegressionTests.tearDown
    stop_server = workflow.WorkflowRegressionTests.stop_server
    wait_for_save = workflow.WorkflowRegressionTests.wait_for_save
    local_data = workflow.WorkflowRegressionTests.local_data

    def add(self, name, page=None, project='検証'):
        page = page or self.page
        page.locator('#input-project').fill(project)
        page.locator('#input-task-name').fill(name)
        page.locator('#btn-add-task').click()

    def file_data(self):
        return json.loads(Path(gui_app.DATA_FILE).read_text())

    def new_profile(self):
        from datetime import timedelta
        page = self.browser.new_page(timezone_id='Asia/Tokyo')
        page.on('pageerror', lambda error: self.errors.append(str(error)))
        page.clock.install(time=workflow.NOW - timedelta(seconds=1))
        page.clock.pause_at(workflow.NOW)
        page.goto(f'http://127.0.0.1:{self.server.server_port}/index.html')
        expect(page.locator('#task-list-container .task-card')).to_have_count(2)
        return page

    def test_real_browser_quota_does_not_block_file_save(self):
        data = self.file_data()
        data['history']['2026-10-01'] = {'tasks': [dict(data['tasks'][0], subtasks='x' * (6 * 1024 * 1024))]}
        Path(gui_app.DATA_FILE).write_text(json.dumps(data))
        self.page = self.new_profile()
        self.add('容量超過でも保存')
        expect(self.page.locator('#sync-status-text')).to_contain_text('ファイル保存済み')
        self.assertIn('容量超過でも保存', [t['name'] for t in self.file_data()['tasks']])
        self.assertIsNone(self.local_data())
        expect(self.page.locator('#storage-warning')).to_contain_text('ファイルに保存しました')

    def test_http_failure_reports_browser_only_and_recovers(self):
        original = self.file_data()
        def fail_write(route):
            if route.request.method == 'POST':
                route.fulfill(status=500, body='write failed')
            else:
                route.continue_()
        self.page.route('**/api/data', fail_write)
        self.add('書込失敗')
        expect(self.page.locator('#sync-status-text')).to_contain_text('ブラウザ保存のみ')
        self.assertEqual(self.file_data(), original)
        self.assertEqual(self.local_data()['tasks'][-1]['name'], '書込失敗')
        self.page.unroute('**/api/data', fail_write)
        self.add('復旧後')
        self.wait_for_save()
        expect(self.page.locator('#storage-warning')).to_be_hidden()
        self.assertEqual(len(self.file_data()['tasks']), 4)

    def test_both_storage_failures_offer_backup_of_live_changes(self):
        self.page.evaluate("""() => { Storage.prototype.setItem = () => {
            throw new DOMException('full', 'QuotaExceededError');
        }; }""")
        self.page.route('**/api/data', lambda route: route.fulfill(status=500, body='failed'))
        self.add('未保存の作業')
        expect(self.page.locator('#sync-status-text')).to_contain_text('未保存')
        expect(self.page.locator('#storage-warning')).to_contain_text('画面を閉じる前に')
        with self.page.expect_download() as download:
            self.page.locator('#btn-backup-unsaved').click()
        backup = json.loads(Path(download.value.path()).read_text())
        self.assertEqual(backup['tasks'][-1]['name'], '未保存の作業')

    def test_separate_browsers_conflict_survives_reload_and_can_recover(self):
        other = self.new_profile()
        self.add('ブラウザーA')
        self.wait_for_save()
        saved = self.file_data()
        self.add('ブラウザーB', other)
        expect(other.locator('#sync-status-text')).to_contain_text('保存競合')
        self.assertEqual(self.file_data(), saved)
        for _ in range(2):
            other.reload()
            expect(other.locator('#sync-status-text')).to_contain_text('保存競合')
            expect(other.locator('#task-list-container')).to_contain_text('ブラウザーB')
            self.assertEqual(self.file_data(), saved)
        with other.expect_download() as download:
            other.locator('#btn-backup-unsaved').click()
        self.assertEqual(json.loads(Path(download.value.path()).read_text())['tasks'][-1]['name'], 'ブラウザーB')
        other.once('dialog', lambda dialog: dialog.accept())
        other.locator('#btn-reload-saved').click()
        expect(other.locator('#task-list-container')).to_contain_text('ブラウザーA')
        expect(other.locator('#task-list-container')).not_to_contain_text('ブラウザーB')
        self.page = other
        self.add('復元後の作業')
        self.wait_for_save()
        self.assertEqual([t['name'] for t in self.file_data()['tasks']][-2:], ['ブラウザーA', '復元後の作業'])

    def test_unsaved_browser_changes_survive_restart_and_retry(self):
        original = self.file_data()
        def fail_write(route):
            if route.request.method == 'POST':
                route.fulfill(status=500, body='failed')
            else:
                route.continue_()
        self.page.route('**/api/data', fail_write)
        self.add('再起動後に保存する作業')
        expect(self.page.locator('#sync-status-text')).to_contain_text('ブラウザ保存のみ')
        self.page.reload()
        expect(self.page.locator('#task-list-container')).to_contain_text('再起動後に保存する作業')
        expect(self.page.locator('#sync-status-text')).to_contain_text('ファイル未反映')
        self.assertEqual(self.file_data(), original)
        self.page.unroute('**/api/data', fail_write)
        self.add('復旧後の作業')
        self.wait_for_save()
        expect(self.page.locator('#storage-warning')).to_be_hidden()
        self.assertEqual([task['name'] for task in self.file_data()['tasks']][-2:],
                         ['再起動後に保存する作業', '復旧後の作業'])

    def test_queued_saves_keep_latest_changes_without_self_conflict(self):
        held = []
        def hold_first_write(route):
            if route.request.method == 'POST' and not held:
                held.append(route)
            else:
                route.continue_()
        self.page.route('**/api/data', hold_first_write)
        self.add('最初')
        self.add('二番目')
        self.add('最後')
        self.assertEqual(len(held), 1)
        held[0].continue_()
        self.wait_for_save()
        expect(self.page.locator('#storage-warning')).to_be_hidden()
        self.assertEqual([t['name'] for t in self.file_data()['tasks']][-3:], ['最初', '二番目', '最後'])

    def test_invalid_backups_leave_state_and_file_untouched(self):
        self.page.locator('#nav-btn-settings').click()
        original = self.file_data()
        invalid = [None, [], {}, dict(original, selectedDate='2026-02-30'),
                   dict(original, history={'constructor': {'tasks': []}}),
                   dict(original, settings={'enableIdleAlert': 'false'}),
                   dict(original, settings={'longAlertMinutes': -1})]
        for key, value in [('project', 123), ('subtasks', []), ('name', {}),
                           ('plannedHours', -1), ('actualSeconds', -1),
                           ('startedAt', 'bad'), ('isRunning', 'true')]:
            backup = copy.deepcopy(original)
            backup['tasks'][0][key] = value
            invalid.append(backup)
        backup = copy.deepcopy(original)
        backup['history'] = {'2026-10-01': {'tasks': [dict(backup['tasks'][0], project=123)]}}
        invalid.append(backup)
        original_local = self.local_data()
        dialogs = []
        self.page.on('dialog', lambda dialog: (dialogs.append((dialog.type, dialog.message)), dialog.accept()))
        for backup in invalid:
            with self.subTest(backup_type=type(backup).__name__):
                with self.page.expect_event('dialog'):
                    self.page.locator('#input-import-backup-json').set_input_files(dict(
                        name='invalid.json', mimeType='application/json', buffer=json.dumps(backup).encode()))
                expect(self.page.locator('#set-long-minutes')).to_have_value('90')
                self.assertEqual(dialogs[-1][0], 'alert')
                self.assertIn('現在のデータは変更していません', dialogs[-1][1])
                self.assertEqual(self.file_data(), original)
                self.assertEqual(self.local_data(), original_local)
        self.assertEqual(len(dialogs), len(invalid))
        self.page.locator('#nav-btn-tracker').click()
        expect(self.page.locator('#task-list-container .task-card')).to_have_count(2)

    def test_negative_planned_hours_are_rejected_on_add_and_edit(self):
        for next_day in (False, True):
            prefix = 'next-' if next_day else ''
            container = '#next-task-list-container' if next_day else '#task-list-container'
            count = self.page.locator(container + ' .task-card').count()
            self.page.locator(f'#input-{prefix}task-name').fill('負数検証')
            self.page.locator(f'#input-{prefix}planned').fill('-2')
            self.page.locator(f'#btn-add-{prefix}task').click()
            expect(self.page.locator(container + ' .task-card')).to_have_count(count)
            expect(self.page.locator(f'#input-{prefix}task-name')).to_have_value('負数検証')
            self.page.locator(f'#input-{prefix}planned').fill('0')
            self.page.locator(f'#btn-add-{prefix}task').click()
            card = self.page.locator(container + ' .task-card').filter(has_text='負数検証')
            card.locator('.btn-edit-next-task' if next_day else '.btn-edit-task').click()
            self.page.locator('#edit-planned-hours').fill('-1')
            self.page.locator('#btn-save-edit-task').click()
            expect(self.page.locator('#modal-edit-task')).to_be_visible()
            self.page.locator('#edit-planned-hours').fill('0')
            self.page.locator('#btn-save-edit-task').click()
            expect(card).to_contain_text('予定: 0.0h')
        self.wait_for_save()
        for key in ('tasks', 'nextDayTasks'):
            self.assertTrue(all(t['plannedHours'] >= 0 for t in self.file_data()[key]))

    def test_prototype_names_render_in_report_and_analytics(self):
        names = ['constructor', '__proto__', 'toString']
        for name in names:
            self.add(name, project=name)
        self.wait_for_save()
        self.page.locator('#nav-btn-report').click()
        report = self.page.locator('#report-text-area').input_value()
        for name in names:
            self.assertIn(name, report)
        self.page.locator('#nav-btn-analytics').click()
        for name in names:
            expect(self.page.locator('#analytics-project-list')).to_contain_text(name)
        with self.page.expect_download() as download:
            self.page.locator('#btn-export-analytics-csv').click()
        csv = Path(download.value.path()).read_text(encoding='utf-8-sig')
        for name in names:
            self.assertIn(name, csv)
