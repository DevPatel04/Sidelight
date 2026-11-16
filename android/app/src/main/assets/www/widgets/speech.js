import { API, LOCALE, PANEL_HEADERS, fmtTime, h, haptic, loadConfig, online, t, toast } from '../main.js';
import { W } from '../registry.js';

async function speechApi(path, method = 'GET', timeout = 8000) {
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), timeout);
  try {
    const res = await fetch(API + path, {
      method,
      signal: ctl.signal,
      cache: 'no-store',
      headers: PANEL_HEADERS,
    });
    const ct = res.headers.get('content-type') || '';
    const data = ct.includes('json') ? await res.json() : { error: await res.text() };
    if (!res.ok) {
      const err = new Error(data?.error || ('HTTP ' + res.status));
      err.state = data?.state || null;
      throw err;
    }
    return data;
  } finally {
    clearTimeout(timer);
  }
}

function noteSummaryLabel(noteTarget, autoAppend = false) {
  if (!noteTarget) return '';
  if (noteTarget === 'first') return t(autoAppend ? 'w.speech.autoNote' : 'w.speech.noteAuto');
  const parts = String(noteTarget).split('/');
  return t(autoAppend ? 'w.speech.autoNotePrefix' : 'w.speech.notePrefix', { name: parts[parts.length - 1] });
}

function shortenDisplayPath(rawPath) {
  const s = String(rawPath || '').trim();
  if (!s) return '';
  return s
    .replace(/^\/Users\/[^/]+(?=\/|$)/, '~')
    .replace(/^\/home\/[^/]+(?=\/|$)/, '~')
    .replace(/^[A-Za-z]:\\Users\\[^\\]+(?=\\|$)/, '~');
}

W.speech = {
  needs: ['speech', 'tick', 'state'],
  create(box, { page, w, o }) {
    const rawId = w.id || 'speech';
    const widgetId = page?.id && !rawId.includes('/') ? `${page.id}/${rawId}` : rawId;
    const isMyWidgetId = (id) => !!id && id === widgetId;
    const titleText = (o.title || t('w.speech.title')).trim() || t('w.speech.title');
    const mode = o.mode === 'hold' ? 'hold' : 'toggle';
    const control = o.control === true;
    const providerPref = o.provider && o.provider !== 'default' ? o.provider : '';

    let latestState = null;
    let actionError = '';
    let localRecordStart = 0;
    let pollTimer = null;
    let reconcileTimer = null;
    let reconcileAttempts = 0;
    let busy = false;
    let disposed = false;
    let holdPointerId = null;
    let holdActive = false;
    let holdKey = null;
    let suppressNextHoldClick = false;
    let pendingHoldAction = null; // null | 'stop' | 'cancel'
    let startPromise = null;
    let nextSeq = 0;
    let appliedSeq = 0;
    let inFlightActions = 0;
    let pendingEventRefresh = false;
    const seenAutoAppended = new Set();

    const headTitle = h('span', 'sp-title', titleText);
    const provBadge = h('span', 'sp-prov', providerPref || 'auto');
    const statusBadge = h('span', 'sp-status', control ? t('w.speech.ready') : t('w.speech.readOnly'));

    const cancelBtn = h('button', 'sp-btn sp-cancel', t('w.speech.cancel'));
    cancelBtn.type = 'button';
    cancelBtn.hidden = true;
    cancelBtn.dataset.noPageSwipe = '1';

    const clearBtn = h('button', 'sp-btn sp-clear', t('w.speech.clear'));
    clearBtn.type = 'button';
    clearBtn.hidden = true;
    clearBtn.dataset.noPageSwipe = '1';

    const head = h('div', 'sp-head', headTitle, provBadge, statusBadge, cancelBtn, clearBtn);

    const micIcon = h('span', 'sp-mic-dot');
    const micLabel = h('span', 'sp-mic-lbl', mode === 'hold' ? t('w.speech.holdToTalk') : t('w.speech.dictate'));
    const micMain = h('span', 'sp-mic-main', micIcon, micLabel);
    const micSub = h('span', 'sp-mic-sub', '');
    const savePill = h('span', 'sp-target-pill sp-save-pill', '');
    savePill.hidden = true;
    const noteLabelText = noteSummaryLabel(o.noteTarget, !!o.autoAppendNote);
    const notePill = h('span', 'sp-target-pill sp-note-pill', noteLabelText);
    notePill.hidden = !noteLabelText;
    const targetsRow = h('span', 'sp-targets', savePill, notePill);
    targetsRow.hidden = !noteLabelText;

    const micBtn = h('button', 'sp-mic', micMain, micSub, targetsRow);
    micBtn.type = 'button';
    micBtn.dataset.noPageSwipe = '1';

    const controls = h('div', 'sp-ctrl', micBtn);
    controls.dataset.noPageSwipe = '1';

    const errBanner = h('div', 'sp-err');
    errBanner.hidden = true;

    const body = h('div', 'sp-body s-list');
    box.append(head, controls, errBanner, body);

    const stopPolling = () => {
      if (pollTimer) {
        clearInterval(pollTimer);
        pollTimer = null;
      }
    };

    const clearReconcileTimer = () => {
      if (reconcileTimer) {
        clearTimeout(reconcileTimer);
        reconcileTimer = null;
      }
    };

    const ensurePolling = () => {
      const st = latestState?.status;
      if (st === 'recording' || st === 'transcribing') {
        if (!pollTimer) pollTimer = setInterval(() => { refreshState(); }, 1100);
      } else {
        stopPolling();
      }
    };

    const currentElapsedMs = () => {
      if (!latestState || latestState.status !== 'recording') return 0;
      if (localRecordStart > 0) return Math.max(latestState.elapsedMs || 0, Date.now() - localRecordStart);
      return latestState.elapsedMs || 0;
    };

    const formatClockSeconds = (ms) => {
      const total = Math.max(0, Math.floor(ms / 1000));
      const m = Math.floor(total / 60);
      const s = total % 60;
      return `${m}:${String(s).padStart(2, '0')}`;
    };

    const sendTeardownCancel = async () => {
      try {
        await speechApi(`/speech/cancel?widgetId=${encodeURIComponent(widgetId)}`, 'POST', 5000);
      } catch (_) {}
    };

    const flushPendingHoldAction = () => {
      const act = pendingHoldAction;
      if (!act) return;
      if (latestState?.status === 'recording' && isMyWidgetId(latestState?.activeWidgetId)) {
        pendingHoldAction = null;
        if (act === 'cancel') {
          sendTeardownCancel();
        } else if (!disposed) {
          stopRecording();
        } else {
          sendTeardownCancel();
        }
      } else if (latestState && latestState.status === 'idle') {
        pendingHoldAction = null;
      }
    };

    const applyStateSnapshot = (st) => {
      if (!st) return;
      if (disposed) {
        latestState = st;
        flushPendingHoldAction();
        return;
      }
      const hadAutoNote = (st.entries || []).some((e) => e.appendedToNote && !seenAutoAppended.has(e.id));
      for (const e of st.entries || []) {
        if (e.appendedToNote) seenAutoAppended.add(e.id);
      }
      if (st.status === 'recording') {
        if (!localRecordStart) localRecordStart = Date.now() - (st.elapsedMs || 0);
      } else {
        localRecordStart = 0;
      }
      latestState = st;
      ensurePolling();
      render();
      if (hadAutoNote) loadConfig();
      flushPendingHoldAction();
    };

    const refreshState = async (allowDuringPending = false) => {
      if (disposed && !pendingHoldAction) return false;
      if (!allowDuringPending && inFlightActions > 0) {
        pendingEventRefresh = true;
        return false;
      }
      const reqSeq = ++nextSeq;
      try {
        const st = await speechApi('/speech/state', 'GET', 5000);
        if (reqSeq < appliedSeq || (!allowDuringPending && inFlightActions > 0)) return false;
        if (disposed && !pendingHoldAction) return false;
        appliedSeq = reqSeq;
        if (!actionError) actionError = '';
        applyStateSnapshot(st);
        return true;
      } catch (_) {
        return false;
      }
    };

    const scheduleReconcile = () => {
      if (!pendingHoldAction || reconcileTimer || reconcileAttempts >= 6) return;
      const delay = Math.min(4000, 400 * (reconcileAttempts + 1));
      reconcileTimer = setTimeout(async () => {
        reconcileTimer = null;
        reconcileAttempts++;
        const ok = await refreshState(true);
        if (!ok && pendingHoldAction) scheduleReconcile();
      }, delay);
    };

    const handleAction = async (fn, options = {}) => {
      inFlightActions++;
      appliedSeq = ++nextSeq;
      actionError = '';
      busy = true;
      if (!disposed) render();
      let gotState = false;
      try {
        const res = await fn();
        appliedSeq = ++nextSeq;
        if (res?.state) {
          gotState = true;
          applyStateSnapshot(res.state);
        }
        return { ok: true, res, gotState };
      } catch (e) {
        appliedSeq = ++nextSeq;
        actionError = e?.message || t('w.speech.requestFailed');
        if (e?.state) {
          gotState = true;
          applyStateSnapshot(e.state);
        } else if (!disposed) {
          render();
        }
        if (!disposed) toast(actionError, 'err');
        return { ok: false, res: null, gotState };
      } finally {
        inFlightActions = Math.max(0, inFlightActions - 1);
        busy = inFlightActions > 0;
        if (!disposed) render();
        if (options.reconcileOnAmbiguous && !gotState) {
          reconcileAttempts = 0;
          const ok = await refreshState(true);
          if (!ok && pendingHoldAction) scheduleReconcile();
        } else if (pendingEventRefresh && inFlightActions === 0 && !disposed) {
          pendingEventRefresh = false;
          refreshState();
        }
      }
    };

    const startRecording = async () => {
      if (!control || busy || disposed) return;
      if (latestState?.status && latestState.status !== 'idle') return;
      pendingHoldAction = null;
      clearReconcileTimer();
      haptic();
      startPromise = handleAction(
        () => speechApi(`/speech/start?widgetId=${encodeURIComponent(widgetId)}`, 'POST', 68000),
        { reconcileOnAmbiguous: true }
      );
      const outcome = await startPromise;
      startPromise = null;
      return outcome?.res || null;
    };

    const stopRecording = async () => {
      if (disposed) return;
      if (startPromise) {
        if (!pendingHoldAction) pendingHoldAction = 'stop';
        try { await startPromise; } catch (_) {}
        if (disposed) return;
      }
      if (latestState?.status !== 'recording' || !isMyWidgetId(latestState?.activeWidgetId)) {
        if (pendingHoldAction === 'stop') {
          await refreshState(true);
          if (latestState?.status !== 'recording' || !isMyWidgetId(latestState?.activeWidgetId)) {
            scheduleReconcile();
            return;
          }
        } else {
          return;
        }
      }
      pendingHoldAction = null;
      clearReconcileTimer();
      haptic();
      await handleAction(() =>
        speechApi(`/speech/stop?widgetId=${encodeURIComponent(widgetId)}`, 'POST', 10000));
    };

    const cancelRecording = async () => {
      if (disposed || !control) return;
      pendingHoldAction = null;
      clearReconcileTimer();
      haptic();
      await handleAction(() =>
        speechApi(`/speech/cancel?widgetId=${encodeURIComponent(widgetId)}`, 'POST', 5000));
    };

    const appendEntry = async (entryId) => {
      if (disposed || !control) return;
      haptic();
      const qs = new URLSearchParams({ entryId, widgetId });
      const outcome = await handleAction(() =>
        speechApi(`/speech/append-note?${qs.toString()}`, 'POST', 8000));
      if (outcome?.res?.ok) {
        toast(t('w.speech.addedToNoteToast'), 'ok');
        loadConfig();
      }
    };

    const clearHistory = async () => {
      if (disposed || !control) return;
      haptic();
      await handleAction(() =>
        speechApi(`/speech/clear?widgetId=${encodeURIComponent(widgetId)}`, 'POST', 5000));
    };

    const markTouchLocal = (e) => {
      e._rotHold = true;
      e.stopPropagation();
    };

    micBtn.addEventListener('touchstart', markTouchLocal, { passive: true });
    micBtn.addEventListener('pointerdown', (e) => {
      e._rotHold = true;
      e.stopPropagation();
      if (mode !== 'hold' || !control || busy) return;
      if (e.button != null && e.button !== 0 && e.pointerType === 'mouse') return;
      if (latestState?.status && latestState.status !== 'idle') return;
      e.preventDefault();
      holdActive = true;
      holdPointerId = e.pointerId;
      suppressNextHoldClick = true;
      try { micBtn.setPointerCapture(e.pointerId); } catch (_) {}
      startRecording();
    });

    const releaseHold = (e) => {
      if (mode !== 'hold' || !holdActive) return;
      if (holdPointerId != null && e.pointerId != null && e.pointerId !== holdPointerId) return;
      holdActive = false;
      holdPointerId = null;
      setTimeout(() => { suppressNextHoldClick = false; }, 0);
      try { if (e.pointerId != null) micBtn.releasePointerCapture(e.pointerId); } catch (_) {}
      stopRecording();
    };
    micBtn.addEventListener('pointerup', releaseHold);
    micBtn.addEventListener('pointercancel', releaseHold);

    micBtn.addEventListener('keydown', (e) => {
      if (mode !== 'hold') return;
      if (e.key !== ' ' && e.key !== 'Spacebar' && e.key !== 'Enter') return;
      e.preventDefault();
      e.stopPropagation();
      if (e.repeat || holdKey || !control || busy) return;
      if (latestState?.status && latestState.status !== 'idle') return;
      holdKey = e.key;
      suppressNextHoldClick = true;
      startRecording();
    });

    const releaseHoldKey = (e) => {
      if (mode !== 'hold' || !holdKey) return;
      if (e.type === 'keyup') {
        if (e.key !== holdKey) return;
        e.preventDefault();
        e.stopPropagation();
      }
      holdKey = null;
      setTimeout(() => { suppressNextHoldClick = false; }, 0);
      stopRecording();
    };
    micBtn.addEventListener('keyup', releaseHoldKey);
    micBtn.addEventListener('blur', releaseHoldKey);

    const onWindowBlur = () => {
      if (mode !== 'hold') return;
      const pid = holdPointerId;
      const hadHold = !!(holdKey || holdActive);
      holdKey = null;
      holdActive = false;
      holdPointerId = null;
      if (pid != null) {
        try { micBtn.releasePointerCapture(pid); } catch (_) {}
      }
      if (hadHold) {
        suppressNextHoldClick = false;
        stopRecording();
      }
    };
    window.addEventListener('blur', onWindowBlur);

    micBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      if (mode === 'hold') {
        if (suppressNextHoldClick) {
          suppressNextHoldClick = false;
          return;
        }
        if (!control) { toast(t('w.speech.controlDisabled'), 'warn'); return; }
        const st = latestState?.status || 'idle';
        if (st === 'recording' && isMyWidgetId(latestState?.activeWidgetId) && !holdActive && !holdKey && !busy) {
          stopRecording();
        }
        return;
      }
      if (!control) { toast(t('w.speech.controlDisabled'), 'warn'); return; }
      const st = latestState?.status || 'idle';
      if (st === 'idle') startRecording();
      else if (st === 'recording' && isMyWidgetId(latestState?.activeWidgetId)) stopRecording();
    });

    cancelBtn.addEventListener('click', (e) => { e.stopPropagation(); cancelRecording(); });
    clearBtn.addEventListener('click', (e) => { e.stopPropagation(); clearHistory(); });

    function render() {
      const st = latestState?.status || 'idle';
      const isMine = isMyWidgetId(latestState?.activeWidgetId);
      const isRecordingHere = st === 'recording' && isMine;
      const isTranscribingHere = st === 'transcribing' && isMine;
      const busyElsewhere = (st === 'recording' || st === 'transcribing') && !isMine;
      const shownEntries = (latestState?.entries || []).filter((e) => isMyWidgetId(e.widgetId));
      const hasEntries = shownEntries.length > 0;

      provBadge.textContent = (isRecordingHere || isTranscribingHere) && latestState?.activeProvider
        ? latestState.activeProvider
        : (providerPref || latestState?.defaultProvider || 'auto');

      box.classList.toggle('recording', isRecordingHere);
      box.classList.toggle('transcribing', isTranscribingHere);
      box.classList.toggle('locked', !control);
      box.classList.toggle('is-empty', !hasEntries);

      if (!control) {
        statusBadge.textContent = t('w.speech.readOnly');
      } else if (isRecordingHere) {
        statusBadge.textContent = t('w.speech.recElapsed', { time: formatClockSeconds(currentElapsedMs()) });
      } else if (isTranscribingHere) {
        statusBadge.textContent = t('w.speech.transcribing');
      } else if (busyElsewhere) {
        statusBadge.textContent = t('w.speech.busy');
      } else {
        statusBadge.textContent = t('w.speech.ready');
      }

      micBtn.classList.toggle('rec', isRecordingHere);
      micBtn.classList.toggle('work', isTranscribingHere);
      micBtn.setAttribute('aria-pressed', isRecordingHere ? 'true' : 'false');

      if (!control) {
        micBtn.disabled = true;
        micLabel.textContent = t('w.speech.locked');
        micSub.textContent = t('w.speech.enableControlSub');
      } else if (isRecordingHere) {
        micBtn.disabled = false;
        const localHold = holdActive || !!holdKey;
        micLabel.textContent = mode === 'hold' && localHold ? t('w.speech.releaseToSend') : (mode === 'hold' ? t('w.speech.tapToSend') : t('w.speech.stop'));
        micSub.textContent = mode === 'hold' && localHold
          ? t('w.speech.releaseAnywhere')
          : t('w.speech.tapAnywhereFinish');
      } else if (isTranscribingHere) {
        micBtn.disabled = true;
        micLabel.textContent = t('w.speech.transcribing');
        micSub.textContent = t('w.speech.converting');
      } else if (busyElsewhere) {
        micBtn.disabled = true;
        micLabel.textContent = t('w.speech.micInUse');
        micSub.textContent = t('w.speech.anotherRecording');
      } else {
        micBtn.disabled = busy && !holdActive && !holdKey;
        micLabel.textContent = mode === 'hold' ? t('w.speech.holdToTalk') : t('w.speech.dictate');
        micSub.textContent = mode === 'hold'
          ? t('w.speech.holdSub')
          : t('w.speech.tapSub');
      }

      const wTarget = latestState?.widgetTargets?.[widgetId] || null;
      const saveEnabled = o.saveTranscript === 'never'
        ? false
        : wTarget
          ? !!wTarget.saveEnabled
          : (o.saveTranscript === 'always' || !!latestState?.defaultSaveTranscripts);
      const saveTargetText = wTarget?.saveTargetDisplay || latestState?.defaultTranscriptDir || '';
      if (saveEnabled) {
        savePill.hidden = false;
        savePill.textContent = saveTargetText ? t('w.speech.pcTarget', { path: saveTargetText }) : t('w.speech.pcSavingEnabled');
        savePill.title = saveTargetText ? t('w.speech.pcSaveTitlePath', { path: saveTargetText }) : t('w.speech.pcSaveTitle');
      } else {
        savePill.hidden = true;
        savePill.textContent = '';
        savePill.title = '';
      }
      targetsRow.hidden = savePill.hidden && notePill.hidden;

      cancelBtn.hidden = !control || !(isRecordingHere || isTranscribingHere);
      clearBtn.hidden = !control || !hasEntries;

      const err = actionError || latestState?.lastError || '';
      errBanner.textContent = err;
      errBanner.hidden = !err;

      body.hidden = !hasEntries;
      if (!hasEntries) {
        body.replaceChildren();
        return;
      }

      body.replaceChildren(...shownEntries.map((entry) => {
        const dt = entry.createdAt ? new Date(entry.createdAt) : new Date();
        const ts = dt.toLocaleTimeString(LOCALE, { hour: '2-digit', minute: '2-digit' });
        const metaParts = [ts, entry.provider || ''];
        if (entry.durationMs > 0) metaParts.push(fmtTime(entry.durationMs));

        const savedRaw = entry.savedFileDisplay || '';
        const savedDisp = savedRaw ? shortenDisplayPath(savedRaw) : '';
        const savedRow = savedDisp
          ? h('div', 'sp-saved', t('w.speech.savedRow', { path: savedDisp }))
          : null;
        if (savedRow) savedRow.title = t('w.speech.savedTitle', { path: savedDisp });

        const hasNoteTarget = !!(o.noteTarget || entry.noteTarget);
        const addNoteBtn = h('button', 'sp-note-btn' + (entry.appendedToNote ? ' on' : ''),
          entry.appendedToNote ? t('w.speech.addedBtn') : t('w.speech.addNoteBtn'));
        addNoteBtn.type = 'button';
        addNoteBtn.disabled = !control || busy || !hasNoteTarget;
        addNoteBtn.dataset.noPageSwipe = '1';
        addNoteBtn.title = !hasNoteTarget
          ? t('w.speech.selectNoteTitle')
          : (entry.appendedToNote ? t('w.speech.addedNoteTitle') : t('w.speech.addNoteTitle'));
        addNoteBtn.onclick = (ev) => {
          ev.stopPropagation();
          appendEntry(entry.id);
        };

        return h('div', 'sp-item',
          h('div', 'sp-text', entry.text || ''),
          savedRow,
          h('div', 'sp-foot',
            h('span', 'sp-meta', metaParts.filter(Boolean).join(' · ')),
            addNoteBtn));
      }));
    }

    render();
    refreshState();

    return {
      update(kind) {
        if (kind === 'speech') {
          refreshState();
        } else if (kind === 'tick') {
          if (latestState?.status === 'recording' && isMyWidgetId(latestState?.activeWidgetId)) {
            statusBadge.textContent = t('w.speech.recElapsed', { time: formatClockSeconds(currentElapsedMs()) });
          }
        } else if (kind === 'state' && online) {
          if (!latestState) refreshState();
        }
      },
      destroy() {
        const hadHold = mode === 'hold' && (holdActive || !!holdKey || !!startPromise);
        const pendingStart = startPromise;
        const pid = holdPointerId;
        holdActive = false;
        holdKey = null;
        holdPointerId = null;
        suppressNextHoldClick = false;
        if (pid != null) {
          try { micBtn.releasePointerCapture(pid); } catch (_) {}
        }
        if (hadHold) {
          pendingHoldAction = 'cancel';
        }
        disposed = true;
        stopPolling();
        window.removeEventListener('blur', onWindowBlur);
        if (hadHold) {
          (async () => {
            if (pendingStart) {
              try { await pendingStart; } catch (_) {}
            }
            if (latestState?.status === 'recording' && isMyWidgetId(latestState?.activeWidgetId)) {
              pendingHoldAction = null;
              await sendTeardownCancel();
            } else {
              const ok = await refreshState(true);
              if (!ok && pendingHoldAction) scheduleReconcile();
            }
          })();
        } else {
          clearReconcileTimer();
        }
      },
    };
  },
};
