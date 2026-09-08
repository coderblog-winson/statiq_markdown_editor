// =====================================================
//  script-runner.js — Preview/Deploy modal + toolbar
//
//  Used to live inside editor.js, but the buttons now sit in the
//  top nav (Pages/Shared/_Layout.cshtml) so every page can drive a
//  preview / deploy run — not just the Editor. The page just needs
//  the standard #script-modal / #preview-btn / #deploy-btn DOM
//  (which is now in _Layout.cshtml), no other dependencies.
//
//  API contract:
//    POST   /api/scripts/{action}        → start preview | deploy
//    GET    /api/scripts/{action}/status → { running, serverReady?, port?, exitCode? }
//    GET    /api/scripts/{action}/log?tail=N → { text }
//    POST   /api/scripts/preview/stop    → kill detached preview
// =====================================================
(function () {
    'use strict';

    // ---------- DOM refs ----------
    const els = {
        previewBtn: document.getElementById('preview-btn'),
        deployBtn: document.getElementById('deploy-btn'),
        scriptModal: document.getElementById('script-modal'),
        scriptTitle: document.getElementById('script-modal-title'),
        scriptStatus: document.getElementById('script-modal-status'),
        scriptLog: document.getElementById('script-log'),
        scriptOpenLink: document.getElementById('script-open-link'),
        scriptStopBtn: document.getElementById('script-stop-btn'),
    };

    // If neither the buttons nor the modal exist on the page, do nothing.
    if (!els.previewBtn && !els.deployBtn && !els.scriptModal) return;

    // ---------- state ----------
    let _scriptPollTimer = null;     // active 1s poll while modal is open
    let _scriptIdleTimer = null;     // low-frequency (10s) background poll for button state
    let _scriptCurrent = null;       // 'preview' | 'deploy' | null — what the modal is showing

    // ---------- helpers ----------
    async function fetchJson(url, opts) {
        const r = await fetch(url, opts);
        if (!r.ok) {
            let msg = 'HTTP ' + r.status;
            try { const j = await r.json(); if (j && j.error) msg = j.error; } catch {}
            throw new Error(msg);
        }
        return r.json();
    }

    function setScriptStatus(state, text) {
        if (!els.scriptStatus) return;
        els.scriptStatus.className = 'script-status status-' + state;
        els.scriptStatus.textContent = text;
    }

    function escapeHtml(s) {
        return String(s).replace(/[&<>"']/g, c => (
            { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
        ));
    }

    function renderLog(text) {
        if (!els.scriptLog) return;
        // Highlight common signal lines so the log is easier to scan.
        const lines = String(text || '').split('\n');
        const html = lines.map(line => {
            const lower = line.toLowerCase();
            if (/error|❌|fail|exception|traceback/i.test(line) && !/0 errors?/.test(lower)) {
                return `<span class="err-line">${escapeHtml(line)}</span>`;
            }
            if (/✅|succeeded|complete|started server|deployment|部署完成/i.test(line)) {
                return `<span class="ok-line">${escapeHtml(line)}</span>`;
            }
            return escapeHtml(line);
        }).join('\n');
        const atBottom = els.scriptLog.scrollTop + els.scriptLog.clientHeight >= els.scriptLog.scrollHeight - 30;
        els.scriptLog.innerHTML = html;
        if (atBottom) els.scriptLog.scrollTop = els.scriptLog.scrollHeight;
    }

    // ---------- modal ----------
    function openScriptModal(action) {
        _scriptCurrent = action;
        if (els.scriptTitle) {
            els.scriptTitle.textContent = action === 'preview' ? 'Preview' : 'Deploy';
        }
        if (els.scriptLog) els.scriptLog.textContent = '';
        if (els.scriptOpenLink) {
            els.scriptOpenLink.hidden = true;
            els.scriptOpenLink.removeAttribute('href');
        }
        if (els.scriptStopBtn) {
            // Only Preview is stoppable
            els.scriptStopBtn.hidden = action !== 'preview';
        }
        setScriptStatus('idle', 'starting…');
        if (els.scriptModal) {
            els.scriptModal.hidden = false;
            // Focus the close button so Enter / Esc don't accidentally
            // retrigger the toolbar button.
            const closeBtn = els.scriptModal.querySelector('[data-script-close]');
            closeBtn && closeBtn.focus && closeBtn.focus({ preventScroll: true });
        }
        startScriptPolling();
    }

    function closeScriptModal() {
        if (els.scriptModal) els.scriptModal.hidden = true;
        // Keep polling in the background at 10s cadence so button
        // tooltips / status stay fresh if the user reopens later.
        stopScriptPolling();
        startIdlePolling();
    }

    // ---------- polling ----------
    async function pollScriptOnce() {
        if (!_scriptCurrent) return;
        const action = _scriptCurrent;
        try {
            const [statusR, logR] = await Promise.all([
                fetchJson(`/api/scripts/${action}/status`),
                fetchJson(`/api/scripts/${action}/log?tail=300`),
            ]);
            renderLog(logR.text || '');
            if (action === 'preview') {
                if (statusR.running && statusR.serverReady) {
                    setScriptStatus('ready', `running on http://127.0.0.1:${statusR.port}/`);
                    if (els.scriptOpenLink) {
                        els.scriptOpenLink.href = `http://127.0.0.1:${statusR.port}/`;
                        els.scriptOpenLink.hidden = false;
                    }
                } else if (statusR.running) {
                    setScriptStatus('running', 'building…');
                } else {
                    setScriptStatus('idle', 'not started');
                }
            } else {
                // deploy
                if (statusR.running) {
                    setScriptStatus('running', 'deploying…');
                } else if (statusR.exitCode === 0) {
                    setScriptStatus('done', 'done · exit 0');
                } else if (typeof statusR.exitCode === 'number') {
                    setScriptStatus('error', `failed · exit ${statusR.exitCode}`);
                } else {
                    setScriptStatus('idle', 'not started');
                }
            }
            syncToolbarButton(action, statusR);
        } catch (e) {
            // Network blip — keep last log, show transient error
            setScriptStatus('error', 'lost connection');
        }
    }

    function startScriptPolling() {
        stopScriptPolling();
        stopIdlePolling();
        pollScriptOnce();
        _scriptPollTimer = setInterval(pollScriptOnce, 1000);
    }
    function stopScriptPolling() {
        if (_scriptPollTimer) {
            clearInterval(_scriptPollTimer);
            _scriptPollTimer = null;
        }
    }
    function startIdlePolling() {
        stopIdlePolling();
        // While the modal is closed, refresh button state every 10s
        // so the user sees current status if they reopen later.
        _scriptIdleTimer = setInterval(async () => {
            try {
                const [p, d] = await Promise.all([
                    fetchJson('/api/scripts/preview/status'),
                    fetchJson('/api/scripts/deploy/status'),
                ]);
                syncToolbarButton('preview', p);
                syncToolbarButton('deploy', d);
            } catch { /* ignore */ }
        }, 10000);
    }
    function stopIdlePolling() {
        if (_scriptIdleTimer) {
            clearInterval(_scriptIdleTimer);
            _scriptIdleTimer = null;
        }
    }

    // Mark the button with a state class so the user can tell at a
    // glance whether a server is up / a deploy is running, and append
    // a status note to the tooltip.
    function syncToolbarButton(action, status) {
        const btn = action === 'preview' ? els.previewBtn : els.deployBtn;
        if (!btn) return;
        btn.classList.remove('btn-running', 'btn-ready');
        if (action === 'preview') {
            if (status.serverReady) btn.classList.add('btn-ready');
            else if (status.running) btn.classList.add('btn-running');
        } else {
            if (status.running) btn.classList.add('btn-running');
        }
        let tip = btn.getAttribute('data-tip-default') || btn.getAttribute('title') || '';
        if (!btn.getAttribute('data-tip-default')) btn.setAttribute('data-tip-default', tip);
        if (action === 'preview' && status.serverReady) {
            btn.title = tip + ` (running on :${status.port})`;
        } else if (status.running) {
            btn.title = tip + ' (in progress)';
        } else {
            btn.title = tip;
        }
    }

    // ---------- actions ----------
    async function runScript(action) {
        // Open the modal first so the user immediately sees "starting…"
        openScriptModal(action);
        try {
            await fetchJson(`/api/scripts/${action}`, { method: 'POST' });
            // Triggers an immediate re-poll so the log shows the first
            // lines without waiting a full second.
            pollScriptOnce();
            setScriptStatus('running', action === 'preview' ? 'building…' : 'deploying…');
        } catch (err) {
            setScriptStatus('error', 'failed to start');
            if (els.scriptLog) {
                els.scriptLog.innerHTML = `<span class="err-line">${escapeHtml(err.message)}</span>`;
            }
        }
    }

    async function stopPreview() {
        if (!_scriptCurrent || _scriptCurrent !== 'preview') return;
        try {
            const r = await fetchJson('/api/scripts/preview/stop', { method: 'POST' });
            if (els.scriptLog) {
                const extra = r.killed ? ` (killed ${r.killed} pid${r.killed === 1 ? '' : 's'})` : '';
                els.scriptLog.innerHTML += `\n<span class="ok-line">[editor] preview stopped${extra}</span>\n`;
            }
            if (els.scriptOpenLink) {
                els.scriptOpenLink.hidden = true;
                els.scriptOpenLink.removeAttribute('href');
            }
            setScriptStatus('idle', 'stopped');
            pollScriptOnce();
        } catch (err) {
            setScriptStatus('error', err.message);
        }
    }

    // ---------- wire-up ----------
    if (els.previewBtn) els.previewBtn.addEventListener('click', () => runScript('preview'));
    if (els.deployBtn)  els.deployBtn.addEventListener('click',  () => runScript('deploy'));
    if (els.scriptStopBtn) els.scriptStopBtn.addEventListener('click', stopPreview);
    if (els.scriptModal) {
        els.scriptModal.addEventListener('click', e => {
            if (e.target.matches('[data-script-close]')) closeScriptModal();
        });
        document.addEventListener('keydown', e => {
            if (e.key === 'Escape' && !els.scriptModal.hidden) closeScriptModal();
        });
    }

    // Begin low-frequency background polling so the buttons reflect
    // up-to-date status without the user opening the modal first.
    startIdlePolling();
})();
