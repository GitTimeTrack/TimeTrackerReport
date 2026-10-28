/**
 * TimeTrackerReport - Application Core Logic (app.js)
 * Windows Native & Portable Compatible Application Core
 */

(function () {
  'use strict';

  const STORAGE_KEY = 'time_tracker_data_v6';
  const FILE_ACK_KEY = STORAGE_KEY + '_file_ack';
  const SETTINGS_INPUTS = {
    enableLongAlert: 'set-enable-long-alert',
    longAlertMinutes: 'set-long-minutes',
    enableIdleAlert: 'set-enable-idle-alert',
    idleAlertMinutes: 'set-idle-minutes'
  };

  // Application Global State
  let state = {
    selectedDate: getTodayString(),
    // isRunning 中は startedAt(ms) / runBaseSeconds / runStartedAt(ms) を併せて保持し、
    // 経過時間は「実時刻の差分」から算出する（setInterval のカウントアップに依存しない）
    tasks: [], // [{ id, project, name, plannedHours, actualSeconds, subtasks, isRunning, startedAt, runBaseSeconds, runStartedAt }]
    nextDayTasks: [], // [{ id, project, name, plannedHours, subtasks }]
    activeTaskId: null,
    templateRawText: '',
    // 日付ごとの作業履歴（予実分析の集計元）
    history: {}, // { 'YYYY-MM-DD': { tasks: [{ id, project, name, plannedHours, actualSeconds, subtasks }], updatedAt } }
    settings: {
      enableLongAlert: true,
      longAlertMinutes: 90,
      enableIdleAlert: true,
      idleAlertMinutes: 15
    }
  };

  // Analytics State
  let analyticsState = {
    periodMode: 'day', // 'day' | 'week' | 'month'
    targetDate: getTodayString(),
    openDrawers: Object.create(null)
  };

  const AUTOSAVE_INTERVAL_MS = 15000;

  let serverRevision; // undefined = unknown, null = file does not exist
  let fileConflict = false;
  let localSaveFailed = false;
  let pendingFileSave = null;
  let fileSaveInFlight = false;
  let discardOnReload = false;
  let loadedSnapshot = null;
  let timerInterval = null;
  let idleSinceMs = Date.now();
  let longAlertTriggeredForTaskId = null;
  let draggedTaskInfo = null;
  let lastAutoSaveAt = 0;
  let lastPersistedAt = 0; // 前回セッションで最後に保存された時刻
  let lastKnownToday = getTodayString();

  // --- Initializer ---
  document.addEventListener('DOMContentLoaded', async () => {
    loadFromLocalStorage();
    // 最新の保存内容を確定するまで、日付切替・自動保存・操作の受付を開始しない。
    // 古いブラウザー情報で新しいファイルを上書きすることを防ぐ。
    await syncWithLocalFileServer();

    initNavigation();
    initThemeToggle();
    initTitlebarControls();
    // ファイルから復元した前日の計測にも、最終保存時刻での打ち切りを適用する。
    checkDayRollover(true);
    initDateInput();
    bindEvents();
    bindLifecycleEvents();
    bindStorageAndBackupEvents();
    startTimerEngine();

    if (state.tasks.length === 0 && state.nextDayTasks.length === 0) {
      fetchAndApplyTemplateFile(false);
    }

    renderAll();

    window.addEventListener('resize', () => {
      const activeNav = document.querySelector('.nav-item.active');
      if (activeNav && activeNav.getAttribute('data-view') === 'view-analytics') {
        renderAnalytics();
      }
    });
  });

  // --- Date & Calendar Utilities ---
  function getTodayString() {
    const today = new Date();
    const yyyy = today.getFullYear();
    const mm = String(today.getMonth() + 1).padStart(2, '0');
    const dd = String(today.getDate()).padStart(2, '0');
    return `${yyyy}-${mm}-${dd}`;
  }

  function getDayNameJP(dateStr) {
    const days = ['日', '月', '火', '水', '木', '金', '土'];
    const d = new Date(dateStr);
    return isNaN(d.getTime()) ? '' : days[d.getDay()];
  }

  function initDateInput() {
    const dateInput = document.getElementById('work-date');
    if (dateInput) {
      dateInput.value = state.selectedDate;
      updateDayBadge();
    }
  }

  function updateDayBadge() {
    const dayBadge = document.getElementById('header-day-name');
    if (dayBadge && state.selectedDate) {
      dayBadge.textContent = getDayNameJP(state.selectedDate);
    }
  }

  // --- Navigation & View Switcher ---
  function initNavigation() {
    const navItems = document.querySelectorAll('.nav-item');
    navItems.forEach(item => {
      item.addEventListener('click', () => {
        const targetViewId = item.getAttribute('data-view');
        if (!targetViewId) return;

        navItems.forEach(nav => nav.classList.remove('active'));
        item.classList.add('active');

        const viewPanes = document.querySelectorAll('.view-pane');
        viewPanes.forEach(pane => {
          if (pane.id === targetViewId) {
            pane.classList.add('active');
          } else {
            pane.classList.remove('active');
          }
        });

        if (targetViewId === 'view-report') {
          renderDailyReport();
        } else if (targetViewId === 'view-analytics') {
          renderAnalytics();
        }
      });
    });
  }

  // --- Theme Toggle ---
  function initThemeToggle() {
    const btnTheme = document.getElementById('btn-theme-toggle');
    if (btnTheme) {
      const isDark = document.body.classList.contains('dark-theme');
      btnTheme.textContent = isDark ? '🌙' : '☀️';

      btnTheme.addEventListener('click', () => {
        const currentlyDark = document.body.classList.contains('dark-theme');
        if (currentlyDark) {
          document.body.classList.remove('dark-theme');
          document.body.classList.add('light-theme');
          btnTheme.textContent = '☀️';
        } else {
          document.body.classList.remove('light-theme');
          document.body.classList.add('dark-theme');
          btnTheme.textContent = '🌙';
        }

        if (document.getElementById('view-analytics').classList.contains('active')) {
          renderAnalytics();
        }
      });
    }
  }

  // --- Template Parser ---
  function parseTemplateText(rawText) {
    if (!rawText || !rawText.trim()) return null;

    const lines = rawText.split(/\r?\n/);
    let currentSection = 'today';

    const rawTodayProjects = [];
    const rawNextProjects = [];

    let currentProjObj = null;
    let currentTaskObj = null;

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      const trimmed = line.trim();

      if (!trimmed) continue;

      if (trimmed.includes('【本日の作業】')) {
        currentSection = 'today';
        currentProjObj = null;
        currentTaskObj = null;
        continue;
      }
      if (trimmed.includes('【次日の作業予定】')) {
        currentSection = 'next';
        currentProjObj = null;
        currentTaskObj = null;
        continue;
      }

      if (trimmed.startsWith('◆') || trimmed.startsWith('■') || trimmed.startsWith('★')) {
        const rawProjStr = trimmed.replace(/^[◆■★]\s*/, '').trim();
        const projMatch = rawProjStr.match(/^([^(（]+)(?:[（\(]\s*予定\s*([\d.]+)\s*h(?:\s*\/\s*実績\s*([\d.]+)\s*h)?\s*[）\)])?/i);

        let projName = rawProjStr;
        let projPlanned = null;
        let projActual = 0;

        if (projMatch) {
          projName = projMatch[1].trim();
          if (projMatch[2] !== undefined) {
            const parsedPlanned = parseFloat(projMatch[2]);
            if (Number.isFinite(parsedPlanned)) projPlanned = parsedPlanned;
          }
          if (projMatch[3]) projActual = parseFloat(projMatch[3]) || 0;
        }

        currentProjObj = {
          projName: projName,
          projPlanned: projPlanned,
          projActual: projActual,
          tasks: []
        };

        if (currentSection === 'today') {
          rawTodayProjects.push(currentProjObj);
        } else {
          rawNextProjects.push(currentProjObj);
        }
        currentTaskObj = null;
        continue;
      }

      if (trimmed.startsWith('・') || trimmed.startsWith('•')) {
        const taskTitle = trimmed.replace(/^[・•]\s*/, '').trim();
        currentTaskObj = {
          name: taskTitle,
          plannedHours: 0,
          actualHours: 0,
          subtasks: []
        };
        if (currentProjObj) {
          currentProjObj.tasks.push(currentTaskObj);
        }
        continue;
      }

      if (currentTaskObj) {
        currentTaskObj.subtasks.push(line);
      }
    }

    const todayTasks = [];
    rawTodayProjects.forEach(proj => {
      const taskCount = proj.tasks.length || 1;
      const defaultPlannedPerTask = proj.projPlanned === null ? 1.0 : proj.projPlanned / taskCount;

      if (proj.tasks.length === 0) {
        todayTasks.push({
          id: generateId(),
          project: proj.projName,
          name: '作業・打合せ',
          plannedHours: proj.projPlanned ?? 1.0,
          actualSeconds: Math.round((proj.projActual || 0) * 3600),
          subtasks: '',
          isRunning: false
        });
      } else {
        proj.tasks.forEach((t, idx) => {
          let planned = defaultPlannedPerTask;
          let actualSec = 0;

          if (idx === 0 && proj.projActual > 0) {
            actualSec = Math.round(proj.projActual * 3600);
          }

          todayTasks.push({
            id: generateId(),
            project: proj.projName,
            name: t.name,
            plannedHours: parseFloat(planned.toFixed(1)),
            actualSeconds: actualSec,
            subtasks: t.subtasks.join('\n'),
            isRunning: false
          });
        });
      }
    });

    const nextTasks = [];
    rawNextProjects.forEach(proj => {
      const taskCount = proj.tasks.length || 1;
      const defaultPlannedPerTask = proj.projPlanned === null ? 1.0 : proj.projPlanned / taskCount;

      if (proj.tasks.length === 0) {
        nextTasks.push({
          id: generateId(),
          project: proj.projName,
          name: '作業予定',
          plannedHours: proj.projPlanned ?? 1.0,
          subtasks: ''
        });
      } else {
        proj.tasks.forEach(t => {
          nextTasks.push({
            id: generateId(),
            project: proj.projName,
            name: t.name,
            plannedHours: parseFloat(defaultPlannedPerTask.toFixed(1)),
            subtasks: t.subtasks.join('\n')
          });
        });
      }
    });

    return { todayTasks, nextTasks };
  }

  function fetchAndApplyTemplateFile(showToastMessage = true) {
    fetch('DailyReportFormat.txt')
      .then(res => {
        if (!res.ok) throw new Error('File not found');
        return res.text();
      })
      .then(text => {
        // 起動時の自動読込が遅れて完了した場合、その間にユーザーが追加・計測開始した
        // タスクを上書き（＝計測停止）しないようにする
        if (!showToastMessage && (state.tasks.length > 0 || state.nextDayTasks.length > 0)) return;
        state.templateRawText = text;
        const parsed = parseTemplateText(text);
        if (parsed) {
          state.tasks = parsed.todayTasks;
          state.nextDayTasks = parsed.nextTasks;
          saveToLocalStorage();
          renderAll();
          if (showToastMessage) showToast('テンプレートを読み込みました');
        }
      })
      .catch(() => {
        if (state.tasks.length === 0) {
          state.tasks = [
            { id: generateId(), project: 'アイテムA', name: '打合せ', plannedHours: 2.0, actualSeconds: 7200, subtasks: '課題について打合せ実施\n（Aさん、Bさん、Cさん）\n定例会議', isRunning: false },
            { id: generateId(), project: 'アイテムB', name: '設計', plannedHours: 2.0, actualSeconds: 3600, subtasks: '基本設計資料作成', isRunning: false }
          ];
          state.nextDayTasks = [
            { id: generateId(), project: 'アイテムA', name: '打合せ、資料確認', plannedHours: 2.5, subtasks: '' },
            { id: generateId(), project: 'アイテムB', name: 'テスト確認', plannedHours: 2.0, subtasks: '' }
          ];
          saveToLocalStorage();
          renderAll();
        }
      });
  }

  // --- Local Server & File System Synchronization ---
  function updateSyncBadge(connected, message = '') {
    const badge = document.getElementById('titlebar-sync-badge');
    const text = document.getElementById('sync-status-text');
    const pathCode = document.getElementById('storage-file-path');
    const dot = document.querySelector('.status-indicator-dot');

    if (badge && text) {
      if (connected) {
        badge.className = 'titlebar-badge synced';
        text.textContent = message || 'ローカルファイル連携中 (data/tracker_data.json)';
        badge.title = 'PC上のローカルファイルと同期しています';
      } else {
        badge.className = 'titlebar-badge offline';
        text.textContent = message || 'ブラウザ保存 (LocalStorage)';
        badge.title = message || 'ファイル未接続のためブラウザ内に保存しています';
      }
    }
    if (dot) dot.classList.toggle('offline', !connected);
    if (pathCode) {
      pathCode.textContent = connected ? 'TimeTrackerReport/data/tracker_data.json' : 'ブラウザ内 LocalStorage (time_tracker_data_v6)';
    }
  }

  function showStorageWarning(message = '') {
    const warning = document.getElementById('storage-warning');
    document.getElementById('storage-warning-text').textContent = message;
    warning.classList.toggle('hidden', !message);
  }

  function reportFileFailure() {
    if (fileConflict) {
      updateSyncBadge(false, '保存競合：ファイルへの保存を停止中');
      showStorageWarning('別の画面でファイルが更新されました。この画面の変更はファイルに反映されていません。まずバックアップを出力し、ファイルの最新版を読み込んで変更を確認してください。'
        + (localSaveFailed ? ' ブラウザーにも保存できないため、画面を閉じる前にバックアップしてください。' : ' この画面の変更はブラウザー内に保存しています。'));
    } else {
      updateSyncBadge(false, localSaveFailed ? '未保存：バックアップしてください' : 'ブラウザ保存のみ（ファイル保存に失敗）');
      showStorageWarning(localSaveFailed
        ? 'ファイルとブラウザーの両方に保存できません。画面を閉じる前にバックアップを出力してください。'
        : 'ファイルに保存できません。変更はブラウザー内に保存しています。サーバーや保存先を確認してください。');
    }
  }

  function readFileAck() {
    try { return JSON.parse(localStorage.getItem(FILE_ACK_KEY)); } catch (_) { return null; }
  }

  async function syncWithLocalFileServer() {
    if (!window.location.protocol.startsWith('http')) {
      updateSyncBadge(false);
      return;
    }
    try {
      const res = await fetch('/api/data', { cache: 'no-store' });
      // Read again in case another tab saved while this request was pending.
      loadFromLocalStorage();
      if (res.status === 404) {
        serverRevision = null;
        updateSyncBadge(false);
        return;
      }
      if (!res.ok) throw new Error('File read failed');
      const serverData = await res.json();
      loadFromLocalStorage();
      serverRevision = res.headers.get('ETag') || undefined;
      const local = loadedSnapshot;
      const ack = readFileAck();
      const tracked = local && local.saveId && Object.hasOwn(local, 'fileBaseRevision');
      const alreadySaved = tracked && (serverData.saveId === local.saveId || (ack && ack.saveId === local.saveId));
      // Unacknowledged local edits must survive a restart even if the other
      // browser has a later clock. Never adopt a new revision for stale edits.
      if (tracked && !alreadySaved && local.fileBaseRevision !== serverRevision) {
        fileConflict = true;
        serverRevision = local.fileBaseRevision;
        reportFileFailure();
        return;
      }
      const useServer = tracked ? alreadySaved : (Number(serverData.savedAt) || 0) >= lastPersistedAt;
      if (useServer) {
        if (serverData.selectedDate) state.selectedDate = serverData.selectedDate;
        if (Array.isArray(serverData.tasks)) state.tasks = serverData.tasks;
        if (Array.isArray(serverData.nextDayTasks)) state.nextDayTasks = serverData.nextDayTasks;
        state.activeTaskId = serverData.activeTaskId || null;
        if (serverData.history && typeof serverData.history === 'object') state.history = serverData.history;
        if (serverData.settings) state.settings = Object.assign(state.settings, serverData.settings);
        lastPersistedAt = Number(serverData.savedAt) || 0;
        normalizeRunningTasks();
      }
      if (useServer) {
        updateSyncBadge(true);
      } else {
        updateSyncBadge(false, 'ブラウザ保存のみ（ファイル未反映）');
        showStorageWarning('ブラウザー内にファイルへ未反映の変更があります。画面を閉じる前にバックアップを出力し、保存先と保存状態を確認してください。');
      }
    } catch (_) {
      serverRevision = undefined;
      loadFromLocalStorage();
      updateSyncBadge(false);
    }
  }

  async function saveToLocalFileServer(serialized) {
    pendingFileSave = serialized;
    if (fileConflict || serverRevision === undefined) {
      reportFileFailure();
      return;
    }
    if (fileSaveInFlight) return;
    fileSaveInFlight = true;
    try {
      // Serialize requests and coalesce pending snapshots. Every request uses
      // the revision returned by the preceding successful write.
      while (pendingFileSave && !fileConflict) {
        const body = pendingFileSave;
        pendingFileSave = null;
        const headers = { 'Content-Type': 'application/json' };
        if (serverRevision === null) headers['If-None-Match'] = '*';
        else headers['If-Match'] = serverRevision;
        const res = await fetch('/api/data', { method: 'POST', headers, body });
        if (res.status === 412 || res.status === 428) fileConflict = true;
        if (!res.ok) throw new Error('File save failed');
        const revision = res.headers.get('ETag');
        if (!revision) throw new Error('Missing file revision');
        serverRevision = revision;
        const saved = JSON.parse(body);
        try {
          const current = JSON.parse(localStorage.getItem(STORAGE_KEY));
          if (current && current.saveId === saved.saveId) {
            localStorage.setItem(FILE_ACK_KEY, JSON.stringify({ saveId: saved.saveId, revision }));
          }
        } catch (_) { /* The file is safe even when browser storage is full. */ }
        if (!pendingFileSave) {
          updateSyncBadge(true, localSaveFailed ? 'ファイル保存済み（ブラウザ容量不足）' : '');
          showStorageWarning(localSaveFailed ? 'ブラウザー内には保存できませんが、変更はファイルに保存しました。' : '');
        }
      }
    } catch (_) {
      // A lost response may mean the file was saved. Keeping the old revision
      // makes the next attempt fail safely rather than overwrite another writer.
      reportFileFailure();
    } finally {
      fileSaveInFlight = false;
    }
  }

  // --- LocalStorage Logic ---
  function saveToLocalStorage() {
    if (discardOnReload) return;
    syncRunningTasks();
    archiveCurrentDay();
    const dataToSave = {
      selectedDate: state.selectedDate,
      tasks: state.tasks,
      nextDayTasks: state.nextDayTasks,
      activeTaskId: state.activeTaskId,
      history: state.history,
      settings: state.settings,
      savedAt: Date.now(),
      saveId: generateId(),
      fileBaseRevision: serverRevision === undefined ? null : serverRevision
    };
    const serialized = JSON.stringify(dataToSave);
    localSaveFailed = false;
    try {
      localStorage.setItem(STORAGE_KEY, serialized);
    } catch (_) {
      localSaveFailed = true;
    }
    lastAutoSaveAt = Date.now();
    lastPersistedAt = dataToSave.savedAt;
    updateSyncBadge(false, 'ファイル保存中…');
    // A full browser store must never prevent the independent file write.
    saveToLocalFileServer(serialized);
  }

  function loadFromLocalStorage() {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return;
      const data = JSON.parse(raw);
      loadedSnapshot = data;
      if (data.selectedDate) state.selectedDate = data.selectedDate;
      if (Array.isArray(data.tasks)) state.tasks = data.tasks;
      if (Array.isArray(data.nextDayTasks)) state.nextDayTasks = data.nextDayTasks;
      state.activeTaskId = data.activeTaskId || null;
      if (data.history && typeof data.history === 'object') state.history = data.history;
      if (data.settings) state.settings = Object.assign(state.settings, data.settings);
      lastPersistedAt = Number(data.savedAt) || 0;
      normalizeRunningTasks();
    } catch (e) {
      console.error('Load failed:', e);
    }
  }

  // 旧バージョンのデータ（startedAt を持たない計測中タスク）や複数同時計測を補正
  function normalizeRunningTasks() {
    const now = Date.now();
    let runningFound = false;
    state.tasks.forEach(t => {
      t.actualSeconds = Number(t.actualSeconds) || 0;
      if (!t.isRunning) {
        delete t.startedAt;
        delete t.runBaseSeconds;
        delete t.runStartedAt;
        return;
      }
      if (runningFound) {
        stopTaskRun(t, now);
        return;
      }
      runningFound = true;
      if (!t.startedAt) {
        t.startedAt = now;
        t.runBaseSeconds = t.actualSeconds;
      }
      if (!t.runStartedAt) t.runStartedAt = t.startedAt;
      state.activeTaskId = t.id;
    });
  }

  // --- Date History (for Analytics) ---
  function snapshotTask(t) {
    return {
      id: t.id,
      project: t.project || 'その他',
      name: t.name,
      plannedHours: Number(t.plannedHours) || 0,
      actualSeconds: Number(t.actualSeconds) || 0,
      subtasks: t.subtasks || ''
    };
  }

  // 現在の作業タスクを selectedDate の履歴として保存
  function archiveCurrentDay() {
    if (!state.selectedDate) return;
    if (state.tasks.length > 0) {
      state.history[state.selectedDate] = {
        tasks: state.tasks.map(snapshotTask),
        updatedAt: Date.now()
      };
    } else {
      delete state.history[state.selectedDate];
    }
  }

  // 作業対象日を切り替える（現在日の内容は履歴へ保存し、切替先の履歴を読み込む）
  function switchToDate(newDate) {
    if (!newDate || newDate === state.selectedDate) return false;

    const now = Date.now();
    state.tasks.forEach(t => {
      if (t.isRunning) stopTaskRun(t, now);
    });
    state.activeTaskId = null;
    longAlertTriggeredForTaskId = null;
    idleSinceMs = now;
    archiveCurrentDay();

    state.selectedDate = newDate;
    const record = state.history[newDate];
    state.tasks = record && Array.isArray(record.tasks)
      ? record.tasks.map(t => Object.assign(snapshotTask(t), { isRunning: false }))
      : [];
    analyticsState.targetDate = newDate;

    const dateInput = document.getElementById('work-date');
    if (dateInput) dateInput.value = newDate;

    saveToLocalStorage();
    renderAll();
    if (document.getElementById('view-report').classList.contains('active')) {
      renderDailyReport();
    }
    if (document.getElementById('view-analytics').classList.contains('active')) {
      renderAnalytics();
    }
    return true;
  }

  // 日付が変わったら新しい日として開始（前日分は履歴へ保存済み）
  function checkDayRollover(isStartup = false) {
    const today = getTodayString();

    if (isStartup) {
      lastKnownToday = today;
      if (state.selectedDate && state.selectedDate < today) {
        // 前日から計測中のままアプリが閉じられていた場合は、夜間分を加算しないよう
        // 最後にアプリが保存した時刻で計測を打ち切る
        const stopAt = lastPersistedAt && lastPersistedAt < Date.now() ? lastPersistedAt : Date.now();
        state.tasks.forEach(t => {
          if (t.isRunning) stopTaskRun(t, Math.max(stopAt, t.startedAt || stopAt));
        });
        if (switchToDate(today)) showToast('日付が変わったため、新しい日の作業を開始しました');
      }
      return;
    }

    if (today === lastKnownToday) return;
    // 計測中は切り替えを保留（停止後に切り替える）
    if (state.tasks.some(t => t.isRunning)) return;

    const previousToday = lastKnownToday;
    lastKnownToday = today;
    if (state.selectedDate === previousToday) {
      if (switchToDate(today)) showToast('日付が変わったため、新しい日の作業を開始しました');
    }
  }

  // --- Timer Engine ---
  // 計測中タスクの実績秒数を「計測開始時刻からの実経過時間」で更新する。
  // ウィンドウ最小化・バックグラウンド時のタイマー間引きやスリープ、
  // ページの再読み込みが発生しても計測時間が失われない。
  function syncRunningTasks(now = Date.now()) {
    state.tasks.forEach(t => {
      if (!t.isRunning) return;
      if (!t.startedAt) {
        t.startedAt = now;
        t.runBaseSeconds = Number(t.actualSeconds) || 0;
      }
      if (!t.runStartedAt) t.runStartedAt = t.startedAt;
      const elapsed = Math.max(0, Math.floor((now - t.startedAt) / 1000));
      t.actualSeconds = (Number(t.runBaseSeconds) || 0) + elapsed;
    });
  }

  function startTaskRun(task, now = Date.now()) {
    task.actualSeconds = Number(task.actualSeconds) || 0;
    task.isRunning = true;
    task.startedAt = now;
    task.runStartedAt = now;
    task.runBaseSeconds = task.actualSeconds;
  }

  function stopTaskRun(task, now = Date.now()) {
    if (task.isRunning && task.startedAt) {
      const elapsed = Math.max(0, Math.floor((now - task.startedAt) / 1000));
      task.actualSeconds = (Number(task.runBaseSeconds) || 0) + elapsed;
    }
    task.isRunning = false;
    delete task.startedAt;
    delete task.runBaseSeconds;
    delete task.runStartedAt;
  }

  // 実績秒数を直接変更する（計測中の場合は計測基準点もリセット）
  function setTaskActualSeconds(task, seconds) {
    task.actualSeconds = Math.max(0, Math.round(seconds));
    if (task.isRunning) {
      task.startedAt = Date.now();
      task.runBaseSeconds = task.actualSeconds;
    }
  }

  function timerTick() {
    const now = Date.now();
    checkDayRollover(false);
    syncRunningTasks(now);

    const runningTask = state.tasks.find(t => t.isRunning);
    if (runningTask) {
      idleSinceMs = now;
      hideForgetAlertBanner();
      checkLongRunningAlert(runningTask, now);
      // 計測中は定期的に保存（ブラウザ終了・クラッシュ・再読み込み時の消失防止）
      if (now - lastAutoSaveAt >= AUTOSAVE_INTERVAL_MS) saveToLocalStorage();
    } else {
      checkIdleAlert(now);
    }

    updateActiveTimerDisplay();
    updateRunningTaskCard();
    updateTodayStats();
  }

  function startTimerEngine() {
    if (timerInterval) clearInterval(timerInterval);
    timerInterval = setInterval(timerTick, 1000);
  }

  function bindLifecycleEvents() {
    // 非表示になる直前に保存し、再表示時は即座に表示を最新化
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'hidden') {
        saveToLocalStorage();
      } else {
        timerTick();
        renderAll();
      }
    });
    window.addEventListener('pagehide', saveToLocalStorage);
    window.addEventListener('beforeunload', saveToLocalStorage);

    // 複数ウィンドウで開いている場合、古い状態での上書き（＝計測停止）を防ぐため同期する
    window.addEventListener('storage', (e) => {
      if (e.key === FILE_ACK_KEY && !fileConflict && !fileSaveInFlight && !pendingFileSave) {
        const ack = readFileAck();
        if (ack && loadedSnapshot && ack.saveId === loadedSnapshot.saveId) serverRevision = ack.revision;
        return;
      }
      if (e.key !== STORAGE_KEY || !e.newValue || fileConflict || fileSaveInFlight || pendingFileSave) return;
      loadFromLocalStorage();
      const dateInput = document.getElementById('work-date');
      if (dateInput) dateInput.value = state.selectedDate;
      renderAll();
      if (document.getElementById('view-analytics').classList.contains('active')) {
        renderAnalytics();
      }
    });
  }

  function checkLongRunningAlert(task, now = Date.now()) {
    if (!state.settings.enableLongAlert) return;
    const thresholdSec = (state.settings.longAlertMinutes || 90) * 60;
    const continuousSec = Math.max(0, Math.floor((now - (task.runStartedAt || now)) / 1000));

    if (continuousSec >= thresholdSec && longAlertTriggeredForTaskId !== task.id) {
      longAlertTriggeredForTaskId = task.id;
      showLongAlertModal(task, Math.floor(continuousSec / 60));
    }
  }

  function checkIdleAlert(now = Date.now()) {
    if (!state.settings.enableIdleAlert) return;
    const thresholdSec = (state.settings.idleAlertMinutes || 15) * 60;
    const idleSec = Math.floor((now - idleSinceMs) / 1000);

    if (idleSec >= thresholdSec && state.tasks.length > 0) {
      showForgetAlertBanner();
    }
  }

  function showForgetAlertBanner() {
    const banner = document.getElementById('forget-prevention-banner');
    if (banner) banner.classList.remove('hidden');
  }

  function hideForgetAlertBanner() {
    const banner = document.getElementById('forget-prevention-banner');
    if (banner) banner.classList.add('hidden');
  }

  function showLongAlertModal(task, runningMins) {
    const modal = document.getElementById('modal-forget-stop');
    if (!modal) return;

    document.getElementById('modal-task-title').textContent = `${task.project ? '[' + task.project + '] ' : ''}${task.name}`;
    document.getElementById('modal-running-minutes').textContent = `${runningMins}分`;
    modal.classList.remove('hidden');
  }

  function hideLongAlertModal() {
    const modal = document.getElementById('modal-forget-stop');
    if (modal) modal.classList.add('hidden');
  }

  // --- Task Editing & Reordering ---
  function moveTaskUp(taskId, isNextDay = false) {
    const list = isNextDay ? state.nextDayTasks : state.tasks;
    const idx = list.findIndex(t => t.id === taskId);
    if (idx > 0) {
      const temp = list[idx];
      list[idx] = list[idx - 1];
      list[idx - 1] = temp;
      saveToLocalStorage();
      renderAll();
      if (document.getElementById('view-report').classList.contains('active')) {
        renderDailyReport();
      }
      showToast('タスクの順序を繰り上げました');
    }
  }

  function moveTaskDown(taskId, isNextDay = false) {
    const list = isNextDay ? state.nextDayTasks : state.tasks;
    const idx = list.findIndex(t => t.id === taskId);
    if (idx >= 0 && idx < list.length - 1) {
      const temp = list[idx];
      list[idx] = list[idx + 1];
      list[idx + 1] = temp;
      saveToLocalStorage();
      renderAll();
      if (document.getElementById('view-report').classList.contains('active')) {
        renderDailyReport();
      }
      showToast('タスクの順序を繰り下げました');
    }
  }

  function parsePlannedHours(value) {
    const hours = parseFloat(value);
    return Number.isFinite(hours) ? hours : 1.0;
  }

  function openEditTaskModal(taskId, isNextDay = false) {
    const list = isNextDay ? state.nextDayTasks : state.tasks;
    const task = list.find(t => t.id === taskId);
    if (!task) return;

    document.getElementById('edit-task-id').value = task.id;
    document.getElementById('edit-task-is-next').value = isNextDay ? 'true' : 'false';
    document.getElementById('edit-project').value = task.project || '';
    document.getElementById('edit-task-name').value = task.name || '';
    document.getElementById('edit-planned-hours').value = parsePlannedHours(task.plannedHours).toFixed(1);

    const actualGroup = document.getElementById('edit-actual-group');
    if (isNextDay) {
      if (actualGroup) actualGroup.style.display = 'none';
    } else {
      if (actualGroup) actualGroup.style.display = 'block';
      syncRunningTasks();
      const actualHours = ((task.actualSeconds || 0) / 3600).toFixed(1);
      const actualInputElem = document.getElementById('edit-actual-hours');
      actualInputElem.value = actualHours;
      // 実績欄が変更されたかどうかを判定するため初期表示値を保持
      actualInputElem.dataset.initialValue = actualHours;
    }

    document.getElementById('edit-subtasks').value = task.subtasks || '';

    const modal = document.getElementById('modal-edit-task');
    if (modal) modal.classList.remove('hidden');
  }

  function hideEditTaskModal() {
    const modal = document.getElementById('modal-edit-task');
    if (modal) modal.classList.add('hidden');
  }

  function saveEditedTask() {
    const id = document.getElementById('edit-task-id').value;
    const isNextDay = document.getElementById('edit-task-is-next').value === 'true';

    const proj = document.getElementById('edit-project').value.trim();
    const name = document.getElementById('edit-task-name').value.trim();
    const planned = parsePlannedHours(document.getElementById('edit-planned-hours').value);
    const subtasks = document.getElementById('edit-subtasks').value.trim();
    if (planned < 0) { showToast('予定時間は0以上で入力してください'); return; }

    if (!name) {
      showToast('タスク名を入力してください');
      return;
    }

    const list = isNextDay ? state.nextDayTasks : state.tasks;
    const task = list.find(t => t.id === id);
    if (task) {
      task.project = proj || 'その他';
      task.name = name;
      task.plannedHours = planned;
      task.subtasks = subtasks;

      if (!isNextDay) {
        // 実績欄が変更された場合のみ反映する。
        // （未変更のまま保存すると 0.1h 単位に丸めた値で上書きされ、計測中の時間が巻き戻っていた）
        const actualInputElem = document.getElementById('edit-actual-hours');
        const rawValue = actualInputElem.value.trim();
        const actualInput = parseFloat(rawValue);
        const changed = rawValue !== (actualInputElem.dataset.initialValue || '');
        if (changed && !isNaN(actualInput) && actualInput >= 0) {
          syncRunningTasks();
          setTaskActualSeconds(task, actualInput * 3600);
        }
      }

      saveToLocalStorage();
      renderAll();
      if (document.getElementById('view-report').classList.contains('active')) {
        renderDailyReport();
      }
      showToast('タスク情報を更新しました');
    }

    hideEditTaskModal();
  }

  // --- Task Operations ---
  function toggleTaskTimer(taskId) {
    const task = state.tasks.find(t => t.id === taskId);
    if (!task) return;

    const now = Date.now();
    if (task.isRunning) {
      stopTaskRun(task, now);
      state.activeTaskId = null;
      longAlertTriggeredForTaskId = null;
      idleSinceMs = now;
      showToast('タイマーを一時停止しました');
    } else {
      // 他に計測中のタスクがあれば、経過時間を確定させてから停止
      state.tasks.forEach(t => {
        if (t.isRunning) stopTaskRun(t, now);
      });
      startTaskRun(task, now);
      state.activeTaskId = task.id;
      longAlertTriggeredForTaskId = null;
      idleSinceMs = now;
      hideForgetAlertBanner();
      showToast(`「${task.name}」の計測を開始しました`);
    }

    saveToLocalStorage();
    renderAll();
  }

  function addTodayTask() {
    const projInput = document.getElementById('input-project');
    const nameInput = document.getElementById('input-task-name');
    const hoursInput = document.getElementById('input-planned');
    const subtasksInput = document.getElementById('input-subtasks');

    const proj = projInput ? projInput.value.trim() : '';
    const name = nameInput ? nameInput.value.trim() : '';
    const planned = parsePlannedHours(hoursInput ? hoursInput.value : undefined);
    const subtasks = subtasksInput ? subtasksInput.value.trim() : '';
    if (planned < 0) { showToast('予定時間は0以上で入力してください'); return; }

    if (!name) {
      showToast('タスク名を入力してください');
      if (nameInput) nameInput.focus();
      return;
    }

    const newTask = {
      id: generateId(),
      project: proj || 'その他',
      name: name,
      plannedHours: planned,
      actualSeconds: 0,
      subtasks: subtasks,
      isRunning: false
    };

    state.tasks.push(newTask);

    if (nameInput) nameInput.value = '';
    if (subtasksInput) subtasksInput.value = '';

    saveToLocalStorage();
    renderAll();
    showToast('タスクを追加しました');
  }

  function addNextDayTask() {
    const projInput = document.getElementById('input-next-project');
    const nameInput = document.getElementById('input-next-task-name');
    const hoursInput = document.getElementById('input-next-planned');
    const subtasksInput = document.getElementById('input-next-subtasks');

    const proj = projInput ? projInput.value.trim() : '';
    const name = nameInput ? nameInput.value.trim() : '';
    const planned = parsePlannedHours(hoursInput ? hoursInput.value : undefined);
    const subtasks = subtasksInput ? subtasksInput.value.trim() : '';
    if (planned < 0) { showToast('予定時間は0以上で入力してください'); return; }

    if (!name) {
      showToast('次日タスク名を入力してください');
      if (nameInput) nameInput.focus();
      return;
    }

    const newTask = {
      id: generateId(),
      project: proj || 'その他',
      name: name,
      plannedHours: planned,
      subtasks: subtasks
    };

    state.nextDayTasks.push(newTask);

    if (nameInput) nameInput.value = '';
    if (subtasksInput) subtasksInput.value = '';

    saveToLocalStorage();
    renderAll();
    showToast('次日の作業予定を追加しました');
  }

  function deleteTask(taskId, isNextDay = false) {
    if (isNextDay) {
      state.nextDayTasks = state.nextDayTasks.filter(t => t.id !== taskId);
    } else {
      if (state.activeTaskId === taskId) {
        state.activeTaskId = null;
      }
      state.tasks = state.tasks.filter(t => t.id !== taskId);
    }
    saveToLocalStorage();
    renderAll();
    showToast('タスクを削除しました');
  }

  function importYesterdayPlan() {
    if (state.nextDayTasks.length === 0) {
      showToast('次日の作業予定が登録されていません');
      return;
    }

    const importedTasks = state.nextDayTasks.map(nt => ({
      id: generateId(),
      project: nt.project,
      name: nt.name,
      plannedHours: nt.plannedHours,
      actualSeconds: 0,
      subtasks: nt.subtasks,
      isRunning: false
    }));

    state.tasks = state.tasks.concat(importedTasks);
    saveToLocalStorage();
    renderAll();
    showToast('前日の作業予定を引き継ぎました');
  }

  function copyTodayToNextDay() {
    if (state.tasks.length === 0) {
      showToast('本日のタスクがありません');
      return;
    }

    const copiedNextTasks = state.tasks.map(t => ({
      id: generateId(),
      project: t.project,
      name: t.name,
      plannedHours: t.plannedHours,
      subtasks: t.subtasks
    }));

    state.nextDayTasks = copiedNextTasks;
    saveToLocalStorage();
    renderAll();
    showToast('本日のタスクを次日予定へコピーしました');
  }

  // --- Rendering Functions ---
  function renderAll() {
    renderSettings();
    syncRunningTasks();
    updateDayBadge();
    renderTodayTaskList();
    renderNextDayTaskList();
    updateActiveTimerDisplay();
    updateTodayStats();
  }

  // 計測中タスクのカード（実績・進捗）を毎秒更新（カード全体は再描画しない）
  function updateRunningTaskCard() {
    const container = document.getElementById('task-list-container');
    if (!container) return;
    state.tasks.forEach(task => {
      if (!task.isRunning) return;
      const card = container.querySelector(`.task-card[data-id="${task.id}"]`);
      if (!card) return;
      const actualSec = Number(task.actualSeconds) || 0;
      const planned = Number(task.plannedHours) || 0;
      const progressPercent = Math.min(Math.round((actualSec / (planned * 3600)) * 100) || 0, 100);
      const isOvertime = (actualSec / 3600) > planned;

      const actualStrong = card.querySelector('.task-card-header .task-time-text strong');
      if (actualStrong) actualStrong.textContent = `${(actualSec / 3600).toFixed(1)}h`;
      const fill = card.querySelector('.progress-bar-fill');
      if (fill) {
        fill.style.width = `${progressPercent}%`;
        fill.classList.toggle('overtime', isOvertime);
      }
      const percentText = card.querySelector('.task-progress-row .task-time-text');
      if (percentText) percentText.textContent = `${progressPercent}%`;
    });
  }

  function updateActiveTimerDisplay() {
    const card = document.getElementById('active-timer-card');
    const projLabel = document.getElementById('active-project-name');
    const taskLabel = document.getElementById('active-task-name');
    const clockLabel = document.getElementById('active-timer-clock');
    const decimalLabel = document.getElementById('active-timer-decimal');
    const toggleBtn = document.getElementById('btn-global-toggle');
    const toggleText = document.getElementById('timer-toggle-text');
    const statusLabel = document.getElementById('active-status-label');

    const activeTask = state.tasks.find(t => t.id === state.activeTaskId || t.isRunning);

    if (activeTask) {
      if (card) {
        card.classList.remove('idle');
        if (activeTask.isRunning) card.classList.add('running');
        else card.classList.remove('running');
      }

      if (projLabel) projLabel.textContent = activeTask.project || 'その他';
      if (taskLabel) taskLabel.textContent = activeTask.name;

      const formattedClock = formatSecondsToClock(activeTask.actualSeconds || 0);
      const decimalHours = ((activeTask.actualSeconds || 0) / 3600).toFixed(1);

      if (clockLabel) clockLabel.textContent = formattedClock;
      if (decimalLabel) decimalLabel.textContent = `${decimalHours}h`;

      if (toggleBtn) toggleBtn.disabled = false;
      if (toggleText) toggleText.textContent = activeTask.isRunning ? '一時停止' : '再開';
      if (statusLabel) statusLabel.textContent = activeTask.isRunning ? '計測中' : '一時停止中';
    } else {
      if (card) {
        card.classList.add('idle');
        card.classList.remove('running');
      }

      if (projLabel) projLabel.textContent = '未選択';
      if (taskLabel) taskLabel.textContent = '計測するタスクを選択してください';
      if (clockLabel) clockLabel.textContent = '00:00:00';
      if (decimalLabel) decimalLabel.textContent = '0.0h';

      if (toggleBtn) toggleBtn.disabled = true;
      if (toggleText) toggleText.textContent = '開始';
      if (statusLabel) statusLabel.textContent = 'タイマー停止中';
    }
  }

  function renderTodayTaskList() {
    const container = document.getElementById('task-list-container');
    if (!container) return;

    if (state.tasks.length === 0) {
      container.innerHTML = `
        <div class="empty-task-placeholder">
          <span>📋 本日の登録済みタスクはありません</span>
          <span style="font-size:0.75rem; margin-top:4px;">上部の「タスククイック追加」から登録してください</span>
        </div>
      `;
      return;
    }

    container.innerHTML = state.tasks.map((task, index) => {
      const actualHours = (task.actualSeconds / 3600).toFixed(1);
      const plannedHoursStr = (Number(task.plannedHours) || 0).toFixed(1);
      const progressPercent = Math.min(Math.round((task.actualSeconds / (task.plannedHours * 3600)) * 100) || 0, 100);
      const isOvertime = (task.actualSeconds / 3600) > task.plannedHours;
      const tagColor = getProjectTagColor(task.project);

      return `
        <div class="task-card ${task.isRunning ? 'active-running' : ''}" data-id="${task.id}" draggable="true">
          <div class="task-card-header">
            <div style="display:flex; align-items:center; gap:6px;">
              <span class="drag-handle" title="ドラッグして並び替え">⋮⋮</span>
              <span class="task-project-tag" data-tag-color="${tagColor}">${escapeHtml(task.project || 'その他')}</span>
            </div>
            <span class="task-time-text">予定: ${plannedHoursStr}h / 実績: <strong>${actualHours}h</strong></span>
          </div>

          <div class="task-card-title">${escapeHtml(task.name)}</div>

          ${task.subtasks ? `<div class="subtasks-list">${escapeHtml(task.subtasks)}</div>` : ''}

          <div class="task-progress-row">
            <div class="progress-bar-bg">
              <div class="progress-bar-fill ${isOvertime ? 'overtime' : ''}" style="width: ${progressPercent}%"></div>
            </div>
            <span class="task-time-text" style="font-size:0.75rem;">${progressPercent}%</span>
          </div>

          <div class="task-card-actions">
            <button class="btn btn-xs btn-outline btn-edit-task" data-id="${task.id}" title="タスク編集・時間変更" draggable="false">✏️ 編集</button>
            <button class="btn btn-xs ${task.isRunning ? 'btn-primary' : 'btn-outline'} btn-toggle-timer" data-id="${task.id}" draggable="false">
              ${task.isRunning ? '⏸ 一時停止' : '▶ 計測開始'}
            </button>
            <button class="btn-icon-danger btn-delete-task" data-id="${task.id}" title="削除" draggable="false">
              🗑️
            </button>
          </div>
        </div>
      `;
    }).join('');

    container.querySelectorAll('.btn-toggle-timer').forEach(btn => {
      btn.addEventListener('click', (e) => {
        const id = e.currentTarget.getAttribute('data-id');
        toggleTaskTimer(id);
      });
    });

    container.querySelectorAll('.btn-edit-task').forEach(btn => {
      btn.addEventListener('click', (e) => {
        const id = e.currentTarget.getAttribute('data-id');
        openEditTaskModal(id, false);
      });
    });

    container.querySelectorAll('.btn-delete-task').forEach(btn => {
      btn.addEventListener('click', (e) => {
        const id = e.currentTarget.getAttribute('data-id');
        deleteTask(id, false);
      });
    });

    bindTaskCardDragAndDrop(container, false);
  }

  function renderNextDayTaskList() {
    const container = document.getElementById('next-task-list-container');
    if (!container) return;

    if (state.nextDayTasks.length === 0) {
      container.innerHTML = `
        <div class="empty-task-placeholder">
          <span>📋 次日の作業予定はありません</span>
        </div>
      `;
      return;
    }

    container.innerHTML = state.nextDayTasks.map((task, index) => {
      const plannedHoursStr = (Number(task.plannedHours) || 0).toFixed(1);
      const tagColor = getProjectTagColor(task.project);

      return `
        <div class="task-card" data-id="${task.id}" draggable="true">
          <div class="task-card-header">
            <div style="display:flex; align-items:center; gap:6px;">
              <span class="drag-handle" title="ドラッグして並び替え">⋮⋮</span>
              <span class="task-project-tag" data-tag-color="${tagColor}">${escapeHtml(task.project || 'その他')}</span>
            </div>
            <span class="task-time-text">予定: ${plannedHoursStr}h</span>
          </div>

          <div class="task-card-title">${escapeHtml(task.name)}</div>

          ${task.subtasks ? `<div class="subtasks-list">${escapeHtml(task.subtasks)}</div>` : ''}

          <div class="task-card-actions">
            <button class="btn btn-xs btn-outline btn-edit-next-task" data-id="${task.id}" title="タスク編集・時間変更" draggable="false">✏️ 編集</button>
            <button class="btn-icon-danger btn-delete-next-task" data-id="${task.id}" title="削除" draggable="false">
              🗑️
            </button>
          </div>
        </div>
      `;
    }).join('');

    container.querySelectorAll('.btn-edit-next-task').forEach(btn => {
      btn.addEventListener('click', (e) => {
        const id = e.currentTarget.getAttribute('data-id');
        openEditTaskModal(id, true);
      });
    });

    container.querySelectorAll('.btn-delete-next-task').forEach(btn => {
      btn.addEventListener('click', (e) => {
        const id = e.currentTarget.getAttribute('data-id');
        deleteTask(id, true);
      });
    });

    bindTaskCardDragAndDrop(container, true);
  }

  function bindTaskCardDragAndDrop(container, isNextDay) {
    if (!container) return;
    const cards = container.querySelectorAll('.task-card');

    cards.forEach(card => {
      const buttons = card.querySelectorAll('button');
      buttons.forEach(btn => btn.setAttribute('draggable', 'false'));

      card.addEventListener('dragstart', (e) => {
        const id = card.getAttribute('data-id');
        draggedTaskInfo = { id: id, isNextDay: isNextDay };
        card.classList.add('dragging');
        if (e.dataTransfer) {
          e.dataTransfer.effectAllowed = 'move';
          e.dataTransfer.setData('text/plain', id);
        }
      });

      card.addEventListener('dragover', (e) => {
        e.preventDefault();
        if (!draggedTaskInfo || draggedTaskInfo.isNextDay !== isNextDay) return;
        if (draggedTaskInfo.id === card.getAttribute('data-id')) return;

        if (e.dataTransfer) {
          e.dataTransfer.dropEffect = 'move';
        }

        const rect = card.getBoundingClientRect();
        const midY = rect.top + rect.height / 2;
        if (e.clientY < midY) {
          card.classList.add('drag-over-top');
          card.classList.remove('drag-over-bottom');
        } else {
          card.classList.add('drag-over-bottom');
          card.classList.remove('drag-over-top');
        }
      });

      card.addEventListener('dragleave', () => {
        card.classList.remove('drag-over-top', 'drag-over-bottom');
      });

      card.addEventListener('drop', (e) => {
        e.preventDefault();
        card.classList.remove('drag-over-top', 'drag-over-bottom');

        if (!draggedTaskInfo || draggedTaskInfo.isNextDay !== isNextDay) return;
        const draggedId = draggedTaskInfo.id;
        const targetId = card.getAttribute('data-id');
        if (draggedId === targetId) return;

        const list = isNextDay ? state.nextDayTasks : state.tasks;
        const fromIndex = list.findIndex(t => t.id === draggedId);
        let toIndex = list.findIndex(t => t.id === targetId);

        if (fromIndex < 0 || toIndex < 0) return;

        const rect = card.getBoundingClientRect();
        const midY = rect.top + rect.height / 2;
        if (e.clientY >= midY) {
          toIndex++;
        }

        const [movedTask] = list.splice(fromIndex, 1);
        if (fromIndex < toIndex) {
          toIndex--;
        }
        list.splice(toIndex, 0, movedTask);

        saveToLocalStorage();
        renderAll();
        if (document.getElementById('view-report').classList.contains('active')) {
          renderDailyReport();
        }
        showToast('タスクの順序を変更しました');
      });

      card.addEventListener('dragend', () => {
        cards.forEach(c => c.classList.remove('drag-over-top', 'drag-over-bottom', 'dragging'));
        draggedTaskInfo = null;
      });
    });
  }

  function updateTodayStats() {
    const plannedElem = document.getElementById('total-planned-hours');
    const actualElem = document.getElementById('total-actual-hours');
    const nextPlannedElem = document.getElementById('total-next-planned-hours');

    let totalPlanned = 0;
    let totalActualSec = 0;

    state.tasks.forEach(t => {
      totalPlanned += (Number(t.plannedHours) || 0);
      totalActualSec += (Number(t.actualSeconds) || 0);
    });

    if (plannedElem) plannedElem.textContent = totalPlanned.toFixed(1);
    if (actualElem) actualElem.textContent = (totalActualSec / 3600).toFixed(1);

    if (nextPlannedElem) {
      let totalNextPlanned = 0;
      state.nextDayTasks.forEach(t => {
        totalNextPlanned += (Number(t.plannedHours) || 0);
      });
      nextPlannedElem.textContent = totalNextPlanned.toFixed(1);
    }
  }

  // --- Daily Report Generator ---
  function renderDailyReport() {
    const textarea = document.getElementById('report-text-area');
    if (!textarea) return;
    syncRunningTasks();

    // Group Today Tasks preserving list order
    const todayProjectOrder = [];
    const todayGrouped = Object.create(null);
    state.tasks.forEach(task => {
      const proj = task.project || 'その他';
      if (!todayGrouped[proj]) {
        todayGrouped[proj] = { plannedTotal: 0, actualTotalSec: 0, items: [] };
        todayProjectOrder.push(proj);
      }
      todayGrouped[proj].plannedTotal += task.plannedHours || 0;
      todayGrouped[proj].actualTotalSec += task.actualSeconds || 0;
      todayGrouped[proj].items.push(task);
    });

    // Group Next Tasks preserving list order
    const nextProjectOrder = [];
    const nextGrouped = Object.create(null);
    state.nextDayTasks.forEach(task => {
      const proj = task.project || 'その他';
      if (!nextGrouped[proj]) {
        nextGrouped[proj] = { plannedTotal: 0, items: [] };
        nextProjectOrder.push(proj);
      }
      nextGrouped[proj].plannedTotal += task.plannedHours || 0;
      nextGrouped[proj].items.push(task);
    });

    // Calculate overall totals
    let todayTotalPlanned = 0;
    let todayTotalActualSec = 0;
    state.tasks.forEach(t => {
      todayTotalPlanned += t.plannedHours || 0;
      todayTotalActualSec += t.actualSeconds || 0;
    });
    const todayTotalActualH = (todayTotalActualSec / 3600).toFixed(1);

    let nextTotalPlanned = 0;
    state.nextDayTasks.forEach(t => {
      nextTotalPlanned += t.plannedHours || 0;
    });

    let reportLines = [];

    // Header Today with Total Planned and Total Actual
    reportLines.push(`【本日の作業】(予定合計: ${todayTotalPlanned.toFixed(1)}h / 実績合計: ${todayTotalActualH}h)-----------------------------------`);

    // Section 1: Today
    todayProjectOrder.forEach(proj => {
      const g = todayGrouped[proj];
      const pTotal = g.plannedTotal.toFixed(1);
      const aTotal = (g.actualTotalSec / 3600).toFixed(1);

      reportLines.push(`　◆${proj}（予定${pTotal}h/実績 ${aTotal}h）`);

      g.items.forEach(t => {
        reportLines.push(`　・${t.name}`);
        if (t.subtasks) {
          const subLines = t.subtasks.split('\n');
          subLines.forEach(sl => {
            if (sl.trim()) {
              reportLines.push(`　　　${sl.trim()}`);
            }
          });
        }
      });
      reportLines.push('');
    });

    // Header Next Day with Total Planned
    reportLines.push(`【次日の作業予定】(予定合計: ${nextTotalPlanned.toFixed(1)}h)---------------------------------------------------`);

    // Section 2: Next Day
    nextProjectOrder.forEach(proj => {
      const g = nextGrouped[proj];
      const pTotal = g.plannedTotal.toFixed(1);

      reportLines.push(`　◆${proj}（予定 ${pTotal}h）`);

      g.items.forEach(t => {
        reportLines.push(`　　・${t.name}`);
        if (t.subtasks) {
          const subLines = t.subtasks.split('\n');
          subLines.forEach(sl => {
            if (sl.trim()) {
              reportLines.push(`　　　${sl.trim()}`);
            }
          });
        }
      });
      reportLines.push('');
    });

    textarea.value = reportLines.join('\n');
  }

  // --- Export Actions ---
  function copyReportToClipboard() {
    const textarea = document.getElementById('report-text-area');
    if (!textarea || !textarea.value) return;

    navigator.clipboard.writeText(textarea.value)
      .then(() => showToast('日報文面をクリップボードにコピーしました'))
      .catch(err => {
        textarea.select();
        document.execCommand('copy');
        showToast('日報文面をコピーしました');
      });
  }

  function downloadReportTxt() {
    const textarea = document.getElementById('report-text-area');
    if (!textarea || !textarea.value) return;

    const dateFormatted = state.selectedDate.replace(/-/g, '');
    const filename = `DailyReport_${dateFormatted}.txt`;

    const blob = new Blob([textarea.value], { type: 'text/plain;charset=utf-8;' });
    const link = document.createElement('a');
    link.href = URL.createObjectURL(blob);
    link.download = filename;
    link.click();
    showToast(`${filename} をダウンロードしました`);
  }

  function downloadReportCsv() {
    let csvRows = ['"日付","区分","プロジェクト","タスク名","予定時間(h)","実績時間(h)","詳細"'];

    state.tasks.forEach(t => {
      const actualHours = (t.actualSeconds / 3600).toFixed(1);
      const plannedHours = (Number(t.plannedHours) || 0).toFixed(1);
      const sub = (t.subtasks || '').replace(/"/g, '""');
      csvRows.push(`"${state.selectedDate}","本日","${csvEscape(t.project)}","${csvEscape(t.name)}","${plannedHours}","${actualHours}","${sub}"`);
    });

    state.nextDayTasks.forEach(t => {
      const plannedHours = (Number(t.plannedHours) || 0).toFixed(1);
      const sub = (t.subtasks || '').replace(/"/g, '""');
      csvRows.push(`"${state.selectedDate}","次日予定","${csvEscape(t.project)}","${csvEscape(t.name)}","${plannedHours}","0.0","${sub}"`);
    });

    const bom = '\uFEFF';
    const blob = new Blob([bom + csvRows.join('\n')], { type: 'text/csv;charset=utf-8;' });
    const link = document.createElement('a');
    const filename = `TimeTrackerReport_${state.selectedDate.replace(/-/g, '')}.csv`;
    link.href = URL.createObjectURL(blob);
    link.download = filename;
    link.click();
    showToast(`${filename} を出力しました`);
  }

  function downloadAnalyticsCsv() {
    const range = getAnalyticsDateRange(analyticsState.periodMode, analyticsState.targetDate || state.selectedDate);

    let csvRows = ['"対象期間","区分","プロジェクト","タスク名","予定時間(h)","実績時間(h)","乖離(h)","詳細メモ"'];

    const projectMap = buildAnalyticsProjectMap(range);

    const projects = Object.keys(projectMap);
    if (projects.length === 0) {
      showToast('出力可能な予実分析データがありません');
      return;
    }

    projects.forEach(proj => {
      const data = projectMap[proj];
      const actualH = (data.actualSec / 3600).toFixed(1);
      const plannedH = data.planned.toFixed(1);
      const diff = (data.actualSec / 3600) - data.planned;
      const diffSign = diff >= 0 ? '+' : '';
      const diffStr = `${diffSign}${diff.toFixed(1)}`;

      // Project Total Row
      csvRows.push(`"${range.displayStr}","プロジェクト合計","${csvEscape(proj)}","-","${plannedH}","${actualH}","${diffStr}",""`);

      // Individual Tasks
      data.tasks.forEach(t => {
        const tActual = (t.actualSeconds / 3600).toFixed(1);
        const tPlanned = (Number(t.plannedHours) || 0).toFixed(1);
        const tDiff = (t.actualSeconds / 3600) - t.plannedHours;
        const tDiffSign = tDiff >= 0 ? '+' : '';
        const tDiffStr = `${tDiffSign}${tDiff.toFixed(1)}`;
        const sub = (t.subtasks || '').replace(/"/g, '""').replace(/\r?\n/g, ' ');

        csvRows.push(`"${range.displayStr}","タスク詳細","${csvEscape(proj)}","${csvEscape(t.name)}","${tPlanned}","${tActual}","${tDiffStr}","${sub}"`);
      });
    });

    const bom = '\uFEFF';
    const blob = new Blob([bom + csvRows.join('\n')], { type: 'text/csv;charset=utf-8;' });
    const link = document.createElement('a');
    const filename = `TimeAnalytics_${range.startStr.replace(/-/g, '')}.csv`;
    link.href = URL.createObjectURL(blob);
    link.download = filename;
    link.click();
    showToast(`予実分析CSV (${filename}) を出力しました`);
  }

  // --- Analytics Logic ---
  // 期間内の全日付のタスクを収集（selectedDate は編集中の最新データ、それ以外は履歴から）
  function collectTasksInRange(range) {
    syncRunningTasks();
    const dates = new Set(Object.keys(state.history));
    if (state.selectedDate) dates.add(state.selectedDate);

    const entries = [];
    Array.from(dates).sort().forEach(date => {
      if (date < range.startStr || date > range.endStr) return;
      const tasks = date === state.selectedDate
        ? state.tasks
        : ((state.history[date] && state.history[date].tasks) || []);
      tasks.forEach(task => entries.push({ date, task }));
    });
    return entries;
  }

  // プロジェクト単位に集計（同一プロジェクト内の同名タスクは複数日分を合算）
  function buildAnalyticsProjectMap(range) {
    const projectMap = Object.create(null);
    collectTasksInRange(range).forEach(({ date, task }) => {
      const proj = task.project || 'その他';
      if (!projectMap[proj]) {
        projectMap[proj] = { planned: 0, actualSec: 0, tasks: [], taskIndex: Object.create(null) };
      }
      const p = projectMap[proj];
      const planned = Number(task.plannedHours) || 0;
      const actualSec = Number(task.actualSeconds) || 0;
      p.planned += planned;
      p.actualSec += actualSec;

      const key = task.name || '';
      let agg = p.taskIndex[key];
      if (!agg) {
        agg = { name: task.name, plannedHours: 0, actualSeconds: 0, subtasks: '', dates: [] };
        p.taskIndex[key] = agg;
        p.tasks.push(agg);
      }
      agg.plannedHours += planned;
      agg.actualSeconds += actualSec;
      if (!agg.dates.includes(date)) agg.dates.push(date);
      if (task.subtasks) agg.subtasks = task.subtasks;
    });
    return projectMap;
  }

  function getAnalyticsDateRange(periodMode, targetDateStr) {
    const target = new Date(targetDateStr + 'T00:00:00');
    if (isNaN(target.getTime())) {
      const todayStr = getTodayString();
      return { startStr: todayStr, endStr: todayStr, displayStr: todayStr };
    }

    if (periodMode === 'week') {
      const dayOfWeek = target.getDay();
      const distanceToMon = dayOfWeek === 0 ? -6 : 1 - dayOfWeek;
      const mon = new Date(target);
      mon.setDate(target.getDate() + distanceToMon);
      const sun = new Date(mon);
      sun.setDate(mon.getDate() + 6);

      const startStr = formatDateStr(mon);
      const endStr = formatDateStr(sun);
      const displayStr = `${startStr.replace(/-/g, '/')} ~ ${endStr.slice(5).replace(/-/g, '/')}`;
      return { startStr, endStr, displayStr };
    } else if (periodMode === 'month') {
      const yyyy = target.getFullYear();
      const mm = target.getMonth();
      const firstDay = new Date(yyyy, mm, 1);
      const lastDay = new Date(yyyy, mm + 1, 0);

      const startStr = formatDateStr(firstDay);
      const endStr = formatDateStr(lastDay);
      const displayStr = `${yyyy}/${String(mm + 1).padStart(2, '0')}月 全体`;
      return { startStr, endStr, displayStr };
    } else {
      const displayStr = `${targetDateStr.replace(/-/g, '/')} (${getDayNameJP(targetDateStr)})`;
      return { startStr: targetDateStr, endStr: targetDateStr, displayStr };
    }
  }

  function shiftAnalyticsDate(delta) {
    const target = new Date((analyticsState.targetDate || getTodayString()) + 'T00:00:00');
    if (isNaN(target.getTime())) return;

    if (analyticsState.periodMode === 'week') {
      target.setDate(target.getDate() + delta * 7);
    } else if (analyticsState.periodMode === 'month') {
      // 月末の日を保持すると、存在しない日付が翌月へ繰り越される。
      target.setDate(1);
      target.setMonth(target.getMonth() + delta);
    } else {
      target.setDate(target.getDate() + delta);
    }

    analyticsState.targetDate = formatDateStr(target);
    renderAnalytics();
  }

  function formatDateStr(d) {
    const yyyy = d.getFullYear();
    const mm = String(d.getMonth() + 1).padStart(2, '0');
    const dd = String(d.getDate()).padStart(2, '0');
    return `${yyyy}-${mm}-${dd}`;
  }

  function renderAnalytics() {
    const kpiPlanned = document.getElementById('kpi-total-planned');
    const kpiActual = document.getElementById('kpi-total-actual');
    const kpiVariance = document.getElementById('kpi-total-variance');
    const kpiVarianceWrap = document.getElementById('kpi-total-variance-wrap');
    const projectListContainer = document.getElementById('analytics-project-list');
    const periodDisplay = document.getElementById('analytics-period-range');

    const range = getAnalyticsDateRange(analyticsState.periodMode, analyticsState.targetDate || state.selectedDate);
    if (periodDisplay) {
      periodDisplay.textContent = range.displayStr;
    }

    const btnDay = document.getElementById('btn-period-day');
    const btnWeek = document.getElementById('btn-period-week');
    const btnMonth = document.getElementById('btn-period-month');
    if (btnDay) btnDay.classList.toggle('active', analyticsState.periodMode === 'day');
    if (btnWeek) btnWeek.classList.toggle('active', analyticsState.periodMode === 'week');
    if (btnMonth) btnMonth.classList.toggle('active', analyticsState.periodMode === 'month');

    let totalPlanned = 0;
    let totalActualSec = 0;

    const projectMap = buildAnalyticsProjectMap(range);
    Object.keys(projectMap).forEach(proj => {
      totalPlanned += projectMap[proj].planned;
      totalActualSec += projectMap[proj].actualSec;
    });

    const totalActual = totalActualSec / 3600;
    const totalVariance = totalActual - totalPlanned;

    if (kpiPlanned) kpiPlanned.textContent = totalPlanned.toFixed(1);
    if (kpiActual) kpiActual.textContent = totalActual.toFixed(1);

    if (kpiVariance) {
      const sign = totalVariance >= 0 ? '+' : '';
      kpiVariance.textContent = `${sign}${totalVariance.toFixed(1)}`;
    }

    if (kpiVarianceWrap) {
      if (totalVariance > 0.1) {
        kpiVarianceWrap.style.color = 'var(--accent-rose)';
      } else {
        kpiVarianceWrap.style.color = 'var(--accent-emerald)';
      }
    }

    drawAnalyticsChart(projectMap);

    if (projectListContainer) {
      const projects = Object.keys(projectMap);
      if (projects.length === 0) {
        projectListContainer.innerHTML = '<div class="empty-task-placeholder">集計データがありません</div>';
        return;
      }

      projectListContainer.innerHTML = projects.map(proj => {
        const data = projectMap[proj];
        const actualH = data.actualSec / 3600;
        const diff = actualH - data.planned;
        const diffSign = diff >= 0 ? '+' : '';
        const isOver = diff > 0.1;
        const isOpen = !!analyticsState.openDrawers[proj];

        return `
          <div class="variance-card" data-proj="${escapeHtml(proj)}">
            <div class="task-card-header">
              <span class="task-project-tag">${escapeHtml(proj)}</span>
              <span class="task-time-text" style="color: ${isOver ? 'var(--accent-rose)' : 'var(--accent-emerald)'}">
                乖離: ${diffSign}${diff.toFixed(1)}h <span style="font-size:0.75rem; margin-left:4px; opacity:0.8;">${isOpen ? '▲ 折りたたむ' : '▼ タスク内訳'}</span>
              </span>
            </div>
            <div style="font-size:0.85rem; color:var(--text-secondary);">
              予定: ${data.planned.toFixed(1)}h / 実績: ${actualH.toFixed(1)}h (${data.tasks.length}件のタスク)
            </div>
            ${isOpen ? `
              <div class="project-task-details">
                ${data.tasks.map(t => {
                  const tActual = (t.actualSeconds / 3600).toFixed(1);
                  const tPlanned = (Number(t.plannedHours) || 0).toFixed(1);
                  const tDiff = (t.actualSeconds / 3600) - t.plannedHours;
                  const tDiffSign = tDiff >= 0 ? '+' : '';
                  return `
                    <div class="detail-task-item">
                      <div class="detail-task-row">
                        <span class="detail-task-name">${escapeHtml(t.name)}${t.dates && t.dates.length > 1 ? ` <span style="font-size:0.75rem; opacity:0.7;">(${t.dates.length}日分)</span>` : ''}</span>
                        <span class="detail-task-time">予定: ${tPlanned}h / 実績: ${tActual}h (乖離 ${tDiffSign}${tDiff.toFixed(1)}h)</span>
                      </div>
                      ${t.subtasks ? `<div class="detail-task-sub">${escapeHtml(t.subtasks)}</div>` : ''}
                    </div>
                  `;
                }).join('')}
              </div>
            ` : ''}
          </div>
        `;
      }).join('');

      projectListContainer.querySelectorAll('.variance-card').forEach(card => {
        card.addEventListener('click', (e) => {
          const proj = card.getAttribute('data-proj');
          if (proj) {
            analyticsState.openDrawers[proj] = !analyticsState.openDrawers[proj];
            renderAnalytics();
          }
        });
      });
    }
  }

  function drawAnalyticsChart(projectMap) {
    const canvas = document.getElementById('analytics-chart-canvas');
    if (!canvas) return;

    const ctx = canvas.getContext('2d');
    const width = canvas.parentElement.clientWidth || 600;
    canvas.width = width;
    const height = 220;
    canvas.height = height;

    ctx.clearRect(0, 0, width, height);

    const projects = Object.keys(projectMap);
    if (projects.length === 0) {
      ctx.fillStyle = '#64748b';
      ctx.font = '14px sans-serif';
      ctx.textAlign = 'center';
      ctx.fillText('集計可能なタスクデータがありません', width / 2, height / 2);
      return;
    }

    const padding = { top: 20, right: 30, bottom: 40, left: 50 };
    const chartWidth = width - padding.left - padding.right;
    const chartHeight = height - padding.top - padding.bottom;

    let maxVal = 1;
    projects.forEach(p => {
      const planned = projectMap[p].planned;
      const actual = projectMap[p].actualSec / 3600;
      if (planned > maxVal) maxVal = planned;
      if (actual > maxVal) maxVal = actual;
    });
    maxVal = Math.ceil(maxVal * 1.2);

    const barGroupWidth = chartWidth / projects.length;
    const barWidth = Math.min(24, barGroupWidth * 0.35);

    ctx.strokeStyle = 'rgba(255, 255, 255, 0.08)';
    ctx.lineWidth = 1;
    const gridCount = 4;
    for (let i = 0; i <= gridCount; i++) {
      const y = padding.top + (chartHeight / gridCount) * i;
      const val = (maxVal - (maxVal / gridCount) * i).toFixed(1);

      ctx.beginPath();
      ctx.moveTo(padding.left, y);
      ctx.lineTo(width - padding.right, y);
      ctx.stroke();

      ctx.fillStyle = '#64748b';
      ctx.font = '11px sans-serif';
      ctx.textAlign = 'right';
      ctx.fillText(`${val}h`, padding.left - 8, y + 4);
    }

    projects.forEach((proj, idx) => {
      const groupX = padding.left + barGroupWidth * idx + barGroupWidth / 2;
      const planned = projectMap[proj].planned;
      const actual = projectMap[proj].actualSec / 3600;

      const plannedH = (planned / maxVal) * chartHeight;
      const actualH = (actual / maxVal) * chartHeight;

      ctx.fillStyle = '#475569';
      ctx.fillRect(groupX - barWidth - 3, padding.top + chartHeight - plannedH, barWidth, plannedH);

      ctx.fillStyle = actual > planned ? '#f43f5e' : '#06b6d4';
      ctx.fillRect(groupX + 3, padding.top + chartHeight - actualH, barWidth, actualH);

      ctx.fillStyle = '#94a3b8';
      ctx.font = '12px sans-serif';
      ctx.textAlign = 'center';
      ctx.fillText(proj, groupX, height - 12);
    });
  }

  // --- Event Binding ---
  function renderSettings() {
    Object.entries(SETTINGS_INPUTS).forEach(([key, id]) => {
      const input = document.getElementById(id);
      if (!input) return;
      if (input.type === 'checkbox') input.checked = state.settings[key];
      else input.value = state.settings[key];
    });
  }

  function bindSettingsEvents() {
    Object.entries(SETTINGS_INPUTS).forEach(([key, id]) => {
      const input = document.getElementById(id);
      if (!input) return;
      input.addEventListener('change', () => {
        if (input.type === 'checkbox') {
          state.settings[key] = input.checked;
        } else {
          if (input.value.trim() === '' || !input.checkValidity()) {
            renderSettings();
            showToast('設定範囲内の分数を入力してください');
            return;
          }
          state.settings[key] = Number(input.value);
        }
        if (key === 'enableLongAlert' || key === 'longAlertMinutes') {
          longAlertTriggeredForTaskId = null;
          hideLongAlertModal();
        } else {
          hideForgetAlertBanner();
        }
        saveToLocalStorage();
        timerTick();
        renderSettings();
      });
    });
  }

  function bindEvents() {
    bindSettingsEvents();
    const workDateInput = document.getElementById('work-date');
    if (workDateInput) {
      workDateInput.addEventListener('change', (e) => {
        const newDate = e.target.value;
        if (!newDate) {
          e.target.value = state.selectedDate;
          return;
        }
        if (switchToDate(newDate)) {
          showToast(`${newDate.replace(/-/g, '/')} の作業データに切り替えました`);
        }
      });
    }

    const btnGlobalToggle = document.getElementById('btn-global-toggle');
    if (btnGlobalToggle) {
      btnGlobalToggle.addEventListener('click', () => {
        if (state.activeTaskId) {
          toggleTaskTimer(state.activeTaskId);
        }
      });
    }

    const btnAddTask = document.getElementById('btn-add-task');
    if (btnAddTask) {
      btnAddTask.addEventListener('click', addTodayTask);
    }

    const btnAddNextTask = document.getElementById('btn-add-next-task');
    if (btnAddNextTask) {
      btnAddNextTask.addEventListener('click', addNextDayTask);
    }

    const btnImportYesterday = document.getElementById('btn-import-yesterday');
    if (btnImportYesterday) {
      btnImportYesterday.addEventListener('click', importYesterdayPlan);
    }

    const btnCopyTodayNext = document.getElementById('btn-copy-today-to-next');
    if (btnCopyTodayNext) {
      btnCopyTodayNext.addEventListener('click', copyTodayToNextDay);
    }

    const btnClearToday = document.getElementById('btn-clear-today');
    if (btnClearToday) {
      btnClearToday.addEventListener('click', () => {
        if (confirm('本日のタスクをすべて消去しますか？')) {
          state.tasks = [];
          state.activeTaskId = null;
          longAlertTriggeredForTaskId = null;
          idleSinceMs = Date.now();
          saveToLocalStorage();
          renderAll();
          showToast('本日のタスクをクリアしました');
        }
      });
    }

    const btnClearNext = document.getElementById('btn-clear-next');
    if (btnClearNext) {
      btnClearNext.addEventListener('click', () => {
        if (confirm('次日の作業予定をすべて消去しますか？')) {
          state.nextDayTasks = [];
          saveToLocalStorage();
          renderAll();
          showToast('次日の作業予定をクリアしました');
        }
      });
    }

    const templateInput = document.getElementById('input-template-file');
    if (templateInput) {
      templateInput.addEventListener('change', (e) => {
        const file = e.target.files[0];
        if (!file) return;

        const reader = new FileReader();
        reader.onload = (evt) => {
          const text = evt.target.result;
          state.templateRawText = text;
          const parsed = parseTemplateText(text);
          if (parsed) {
            state.tasks = parsed.todayTasks;
            state.nextDayTasks = parsed.nextTasks;
            state.activeTaskId = null;
            longAlertTriggeredForTaskId = null;
            saveToLocalStorage();
            renderAll();
            showToast('テンプレートファイルを読み込みました');
          }
        };
        reader.readAsText(file);
      });
    }

    // Report Actions
    const btnCopyReport = document.getElementById('btn-copy-report');
    if (btnCopyReport) btnCopyReport.addEventListener('click', copyReportToClipboard);

    const btnDownloadTxt = document.getElementById('btn-download-txt');
    if (btnDownloadTxt) btnDownloadTxt.addEventListener('click', downloadReportTxt);

    const btnDownloadCsv = document.getElementById('btn-download-csv');
    if (btnDownloadCsv) btnDownloadCsv.addEventListener('click', downloadReportCsv);

    // Modal Actions
    const btnModalKeep = document.getElementById('btn-modal-keep-running');
    if (btnModalKeep) {
      btnModalKeep.addEventListener('click', hideLongAlertModal);
    }

    const btnModalStop = document.getElementById('btn-modal-stop-now');
    if (btnModalStop) {
      btnModalStop.addEventListener('click', () => {
        if (state.activeTaskId) toggleTaskTimer(state.activeTaskId);
        hideLongAlertModal();
      });
    }

    const btnModalMinus30 = document.getElementById('btn-modal-minus-30m');
    if (btnModalMinus30) {
      btnModalMinus30.addEventListener('click', () => {
        const task = state.tasks.find(t => t.id === state.activeTaskId);
        if (task && task.isRunning) {
          syncRunningTasks();
          setTaskActualSeconds(task, (task.actualSeconds || 0) - 1800);
          toggleTaskTimer(task.id);
        }
        hideLongAlertModal();
      });
    }

    const btnModalMinus60 = document.getElementById('btn-modal-minus-60m');
    if (btnModalMinus60) {
      btnModalMinus60.addEventListener('click', () => {
        const task = state.tasks.find(t => t.id === state.activeTaskId);
        if (task && task.isRunning) {
          syncRunningTasks();
          setTaskActualSeconds(task, (task.actualSeconds || 0) - 3600);
          toggleTaskTimer(task.id);
        }
        hideLongAlertModal();
      });
    }

    // Edit Task Modal Events
    const btnSaveEdit = document.getElementById('btn-save-edit-task');
    if (btnSaveEdit) {
      btnSaveEdit.addEventListener('click', saveEditedTask);
    }

    const btnCancelEdit = document.getElementById('btn-cancel-edit-task');
    if (btnCancelEdit) {
      btnCancelEdit.addEventListener('click', hideEditTaskModal);
    }

    const btnDismissAlert = document.getElementById('btn-quick-dismiss-alert');
    if (btnDismissAlert) {
      btnDismissAlert.addEventListener('click', hideForgetAlertBanner);
    }

    // Analytics Period & Navigation Events
    const btnPeriodDay = document.getElementById('btn-period-day');
    if (btnPeriodDay) {
      btnPeriodDay.addEventListener('click', () => {
        analyticsState.periodMode = 'day';
        renderAnalytics();
      });
    }

    const btnPeriodWeek = document.getElementById('btn-period-week');
    if (btnPeriodWeek) {
      btnPeriodWeek.addEventListener('click', () => {
        analyticsState.periodMode = 'week';
        renderAnalytics();
      });
    }

    const btnPeriodMonth = document.getElementById('btn-period-month');
    if (btnPeriodMonth) {
      btnPeriodMonth.addEventListener('click', () => {
        analyticsState.periodMode = 'month';
        renderAnalytics();
      });
    }

    const btnPeriodPrev = document.getElementById('btn-period-prev');
    if (btnPeriodPrev) {
      btnPeriodPrev.addEventListener('click', () => {
        shiftAnalyticsDate(-1);
      });
    }

    const btnPeriodNext = document.getElementById('btn-period-next');
    if (btnPeriodNext) {
      btnPeriodNext.addEventListener('click', () => {
        shiftAnalyticsDate(1);
      });
    }

    const btnExportAnalyticsCsv = document.getElementById('btn-export-analytics-csv');
    if (btnExportAnalyticsCsv) {
      btnExportAnalyticsCsv.addEventListener('click', downloadAnalyticsCsv);
    }

    // Windows Desktop Global Keyboard Shortcuts
    document.addEventListener('keydown', (e) => {
      // Space キーでタイマー切り替え (テキスト入力中以外)
      if (e.code === 'Space' && !['INPUT', 'TEXTAREA'].includes(document.activeElement.tagName)) {
        e.preventDefault();
        if (state.activeTaskId) {
          toggleTaskTimer(state.activeTaskId);
        } else if (state.tasks.length > 0) {
          toggleTaskTimer(state.tasks[0].id);
        }
      }
    });
  }

  // Windows 11 Native Titlebar Controls
  function initTitlebarControls() {
    const btnMin = document.getElementById('btn-window-minimize');
    const btnMax = document.getElementById('btn-window-maximize');
    const btnClose = document.getElementById('btn-window-close');

    if (btnMin) {
      btnMin.addEventListener('click', () => {
        showToast('ウィンドウ最小化');
      });
    }

    if (btnMax) {
      btnMax.addEventListener('click', () => {
        if (!document.fullscreenElement) {
          document.documentElement.requestFullscreen().catch(() => {});
        } else {
          document.exitFullscreen().catch(() => {});
        }
      });
    }

    if (btnClose) {
      btnClose.addEventListener('click', () => {
        saveToLocalStorage();
        if (confirm('TimeTrackerReport を終了しますか？')) {
          window.close();
        }
      });
    }
  }

  // Storage & Backup Event Handlers
  // Build a validated copy before touching live state or either storage target.
  function validateBackup(data) {
    const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
    const fail = () => { throw new Error('Invalid backup'); };
    const date = value => typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value)
      && Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 10) === value;
    const number = value => typeof value === 'number' && Number.isFinite(value) && value >= 0;
    function taskList(list) {
      if (!Array.isArray(list)) fail();
      const ids = new Set();
      return list.map(task => {
        if (!object(task) || typeof task.id !== 'string' || !task.id || ids.has(task.id)
          || typeof task.name !== 'string' || !task.name.trim()) fail();
        ids.add(task.id);
        for (const key of ['project', 'subtasks']) {
          if (task[key] !== undefined && typeof task[key] !== 'string') fail();
        }
        for (const key of ['plannedHours', 'actualSeconds', 'startedAt', 'runBaseSeconds', 'runStartedAt']) {
          if (task[key] !== undefined && !number(task[key])) fail();
        }
        if (task.isRunning !== undefined && typeof task.isRunning !== 'boolean') fail();
        return { ...task, project: task.project || 'その他', subtasks: task.subtasks || '',
          plannedHours: task.plannedHours ?? 1, actualSeconds: task.actualSeconds ?? 0 };
      });
    }
    if (!object(data) || !date(data.selectedDate) || !object(data.history)
      || (data.version !== undefined && data.version !== 'v6')) fail();
    if (data.savedAt !== undefined && !number(data.savedAt)) fail();
    if (data.activeTaskId != null && typeof data.activeTaskId !== 'string') fail();
    const result = { selectedDate: data.selectedDate, tasks: taskList(data.tasks),
      nextDayTasks: taskList(data.nextDayTasks), history: Object.create(null), settings: {} };
    for (const [day, record] of Object.entries(data.history)) {
      if (!date(day) || !object(record) || (record.updatedAt !== undefined && !number(record.updatedAt))) fail();
      result.history[day] = { tasks: taskList(record.tasks), updatedAt: record.updatedAt ?? 0 };
    }
    if (data.settings !== undefined) {
      if (!object(data.settings)) fail();
      for (const key of Object.keys(SETTINGS_INPUTS)) {
        const value = data.settings[key];
        if (value === undefined) continue;
        if (key.startsWith('enable')) {
          if (typeof value !== 'boolean') fail();
        } else {
          const input = document.getElementById(SETTINGS_INPUTS[key]);
          if (!number(value) || !Number.isInteger(value) || value < Number(input.min) || value > Number(input.max)) fail();
        }
        result.settings[key] = value;
      }
    }
    return result;
  }

  function bindStorageAndBackupEvents() {
    document.getElementById('btn-backup-unsaved').addEventListener('click', () => {
      document.getElementById('btn-export-backup-json').click();
    });
    document.getElementById('btn-reload-saved').addEventListener('click', () => {
      if (!confirm('この画面の未反映の変更を破棄し、ファイルの最新版を読み込みます。必要な変更をバックアップしましたか？')) return;
      // Do not let unload handlers save the discarded snapshot again.
      discardOnReload = true;
      localStorage.removeItem(STORAGE_KEY);
      localStorage.removeItem(FILE_ACK_KEY);
      window.location.reload();
    });

    // 保存先フォルダをエクスプローラーで開く
    const btnOpenDir = document.getElementById('btn-open-data-dir');
    if (btnOpenDir) {
      btnOpenDir.addEventListener('click', () => {
        fetch('/api/open-data-folder', { method: 'POST' })
          .then(res => {
            if (!res.ok) throw new Error();
            showToast('データフォルダを開きました');
          })
          .catch(() => {
            showToast('Pythonサーバー未起動のためエクスプローラーを開けませんでした');
          });
      });
    }

    // 全データJSONエクスポート
    const btnExportJson = document.getElementById('btn-export-backup-json');
    if (btnExportJson) {
      btnExportJson.addEventListener('click', () => {
        syncRunningTasks();
        archiveCurrentDay();
        const exportData = {
          version: 'v6',
          exportedAt: new Date().toISOString(),
          selectedDate: state.selectedDate,
          tasks: state.tasks,
          nextDayTasks: state.nextDayTasks,
          history: state.history,
          settings: state.settings,
          savedAt: Date.now()
        };
        const blob = new Blob([JSON.stringify(exportData, null, 2)], { type: 'application/json' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `TimeTrackerReport_Backup_${state.selectedDate.replace(/-/g, '')}.json`;
        a.click();
        URL.revokeObjectURL(url);
        showToast('バックアップJSONを出力しました');
      });
    }

    // JSONインポート
    const inputImportJson = document.getElementById('input-import-backup-json');
    if (inputImportJson) {
      inputImportJson.addEventListener('change', (e) => {
        const file = e.target.files[0];
        if (!file) return;
        const reader = new FileReader();
        reader.onload = (evt) => {
          try {
            const imported = validateBackup(JSON.parse(evt.target.result));
            if (confirm('現在のデータを上書きして復元しますか？')) {
              if (imported.selectedDate) state.selectedDate = imported.selectedDate;
              if (Array.isArray(imported.tasks)) state.tasks = imported.tasks;
              if (Array.isArray(imported.nextDayTasks)) state.nextDayTasks = imported.nextDayTasks;
              if (imported.history && typeof imported.history === 'object') state.history = imported.history;
              if (imported.settings) state.settings = Object.assign(state.settings, imported.settings);
              state.activeTaskId = null;
              normalizeRunningTasks();
              longAlertTriggeredForTaskId = null;
              idleSinceMs = Date.now();
              hideLongAlertModal();
              hideForgetAlertBanner();
              saveToLocalStorage();
              initDateInput();
              renderAll();
              showToast('バックアップデータを正常に復元しました');
            }
          } catch (err) {
            alert('バックアップの形式または内容が不正です。現在のデータは変更していません。');
          }
        };
        reader.readAsText(file);
      });
    }
  }

  // Project Tag Color Auto-Classifier
  function getProjectTagColor(proj) {
    if (!proj) return 'cyan';
    const p = String(proj).toLowerCase();
    if (p.includes('アイテムa') || p.includes('企画') || p.includes('設計')) return 'cyan';
    if (p.includes('アイテムb') || p.includes('開発') || p.includes('実装') || p.includes('コーディング')) return 'violet';
    if (p.includes('アイテムc') || p.includes('テスト') || p.includes('検証') || p.includes('レビュー')) return 'emerald';
    if (p.includes('打合せ') || p.includes('会議') || p.includes('mtg') || p.includes('定例')) return 'amber';
    return 'cyan';
  }

  // --- Helper Functions ---
  function generateId() {
    return 't_' + Math.random().toString(36).substr(2, 9);
  }

  function formatSecondsToClock(totalSeconds) {
    const h = Math.floor(totalSeconds / 3600);
    const m = Math.floor((totalSeconds % 3600) / 60);
    const s = totalSeconds % 60;
    return [h, m, s].map(v => String(v).padStart(2, '0')).join(':');
  }

  function csvEscape(str) {
    return String(str == null ? '' : str).replace(/"/g, '""').replace(/\r?\n/g, ' ');
  }

  function escapeHtml(str) {
    if (!str) return '';
    return str
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#039;');
  }

  function showToast(message) {
    const container = document.getElementById('toast-container');
    if (!container) return;

    const toast = document.createElement('div');
    toast.className = 'toast';
    toast.textContent = message;

    container.appendChild(toast);

    setTimeout(() => {
      toast.style.opacity = '0';
      toast.style.transform = 'translateY(10px)';
      toast.style.transition = 'all 0.3s ease';
      setTimeout(() => toast.remove(), 300);
    }, 2500);
  }

})();
