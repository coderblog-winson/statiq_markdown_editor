// =====================================================
//  site-builder.js — top-nav Build / Preview / Deploy / Git Sync
//
// 4 button actions, all targeting the currently-active site:
//
//   Build     → POST /api/sites/{name}/build      (in-process Bootstrapper)
//   Preview   → POST /api/sites/{name}/preview    (scripts/preview.sh: dotnet run + http.server)
//   Deploy    → POST /api/sites/{name}/deploy     (scripts/build_deploy.sh: build + rsync + git-sync)
//   Git Sync  → POST /api/sites/{name}/git-sync   (scripts/git_sync.sh: commit + push)
//
// Deploy and Build are "fire-and-forget" one-shots — modal shows the
// captured log + exit code.
// Preview is long-lived — same modal, plus a Stop button (kills the
// detached bash + any leftover python http.server on the port).
// Git Sync is one-shot — modal shows the captured log.
// =====================================================
(() => {
    'use strict';

    const els = {
        buildBtn:   document.getElementById('build-btn'),
        previewBtn: document.getElementById('preview-btn'),
        deployBtn:  document.getElementById('deploy-btn'),
        gitsyncBtn: document.getElementById('gitsync-btn'),
        siteMenuBtn: document.getElementById('site-menu-btn'),
        siteMenu:    document.getElementById('site-menu'),
        siteLabel:   document.getElementById('site-label'),
        modal:       document.getElementById('script-modal'),
        modalTitle:  document.getElementById('script-modal-title'),
        modalStatus: document.getElementById('script-modal-status'),
        modalLog:    document.getElementById('script-log'),
        modalStop:   document.getElementById('script-stop-btn'),
        modalOpen:   document.getElementById('script-open-link'),
    };

    // active kind = which script the modal is currently showing.
    // null = modal closed. Each call to run* sets this.
    const state = {
        activeName: '',
        activeKind: null,       // 'build' | 'preview' | 'deploy' | 'gitsync' | null
        pollHandle: null,
        sites: [],
        sitesByName: {},       // for quick script-enabled checks
    };

    // ---------- toast ----------
    function toast(message, kind = 'info', ms = 2400) {
        const host = document.getElementById('toast-host');
        if (!host) return;
        const el = document.createElement('div');
        el.className = 'toast ' + kind;
        el.textContent = message;
        host.appendChild(el);
        setTimeout(() => {
            el.style.transition = 'opacity .2s, transform .2s';
            el.style.opacity = '0';
            el.style.transform = 'translateY(8px)';
            setTimeout(() => el.remove(), 200);
        }, ms);
    }

    // ---------- fetch ----------
    async function fetchJson(url, opts) {
        const r = await fetch(url, opts);
        if (!r.ok) {
            let msg = 'HTTP ' + r.status;
            try { const j = await r.json(); if (j?.error) msg = j.error; } catch {}
            throw new Error(msg);
        }
        return r.json();
    }

    // ---------- modal ----------
    function openModal(title) {
        if (!els.modal) return;
        els.modal.hidden = false;
        if (els.modalTitle) els.modalTitle.textContent = title;
        if (els.modalLog) els.modalLog.textContent = '';
        setStatus('running', 'running…');
        // Stop only makes sense for preview (long-lived) + maybe build.
        if (els.modalStop) {
            const canStop = state.activeKind === 'preview';
            els.modalStop.hidden = !canStop;
        }
        if (els.modalOpen) els.modalOpen.hidden = true;
    }
    function closeModal() {
        if (els.modal) els.modal.hidden = true;
        stopPolling();
        state.activeKind = null;
    }
    function setStatus(kind, text) {
        if (!els.modalStatus) return;
        els.modalStatus.className = 'script-status status-' + kind;
        els.modalStatus.textContent = text;
    }
    function appendLog(line) {
        if (!els.modalLog) return;
        els.modalLog.textContent += line + '\n';
        els.modalLog.scrollTop = els.modalLog.scrollHeight;
    }

    // ---------- site menu ----------
    function renderSiteMenu() {
        if (!els.siteMenu) return;
        if (!state.sites.length) {
            els.siteMenu.innerHTML = '<div class="empty">No sites found</div>';
            return;
        }
        els.siteMenu.innerHTML = state.sites
            .map(s => {
                const active = s.name === state.activeName;
                return `<button type="button" data-name="${escapeHtml(s.name)}"${active ? ' class="is-active"' : ''}>${escapeHtml(s.name)}${active ? ' ✓' : ''}</button>`;
            })
            .join('');
        els.siteMenu.querySelectorAll('button[data-name]').forEach(btn => {
            btn.addEventListener('click', () => {
                closeSiteMenu();
                activate(btn.dataset.name);
            });
        });
    }
    function openSiteMenu() {
        if (!els.siteMenu) return;
        els.siteMenu.hidden = false;
        setTimeout(() => {
            const close = (e) => {
                if (!els.siteMenu.contains(e.target) && e.target !== els.siteMenuBtn) {
                    closeSiteMenu();
                    document.removeEventListener('click', close);
                }
            };
            document.addEventListener('click', close);
        }, 0);
    }
    function closeSiteMenu() {
        if (els.siteMenu) els.siteMenu.hidden = true;
    }
    function updateSiteLabel() {
        if (els.siteLabel) els.siteLabel.textContent = state.activeName || '(no site)';
    }
    function escapeHtml(s) {
        return String(s ?? '')
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    async function activate(name) {
        try {
            await fetchJson('/api/projects/activate', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name }),
            });
            await refreshActive();
            window.dispatchEvent(new CustomEvent('active-site-changed', { detail: { name } }));
            toast(`Active site → ${name}`, 'success');
        } catch (ex) {
            toast(`Activate failed: ${ex.message}`, 'error', 4000);
        }
    }

    // ---------- load state ----------
    async function refreshActive() {
        try {
            const payload = await fetchJson('/api/projects');
            state.sites = payload.sites || [];
            state.sitesByName = Object.fromEntries(state.sites.map(s => [s.name, s]));
            state.activeName = payload.activeProjectName || '';
            updateSiteLabel();
            renderSiteMenu();
        } catch {
            // Silent — dropdown just stays empty.
        }
    }

    // ---------- polling (for preview status) ----------
    function startPolling(kind, siteName) {
        stopPolling();
        state.pollHandle = setInterval(async () => {
            try {
                const status = await fetchJson(`/api/scripts/${kind}/status`);
                if (!status.running) {
                    setStatus(status.exitCode === 0 ? 'ready' : 'error',
                        status.exitCode === 0 ? 'done ✓' : `failed (exit ${status.exitCode})`);
                    if (els.modalStop) els.modalStop.hidden = true;
                    if (kind === 'preview' && els.modalOpen) {
                        // Keep the open-link as long as we know the port.
                        // (The URL was set on Start.)
                    }
                    stopPolling();
                    return;
                }
                // Still running — refresh tail of log every tick.
                const log = await fetchJson(`/api/scripts/${kind}/log?tail=200`);
                if (els.modalLog && log.text) {
                    els.modalLog.textContent = log.text;
                    els.modalLog.scrollTop = els.modalLog.scrollHeight;
                }
                setStatus('running', `PID ${status.pid} running…`);
            } catch {
                // Network blip — keep polling.
            }
        }, 1200);
    }
    function stopPolling() {
        if (state.pollHandle) {
            clearInterval(state.pollHandle);
            state.pollHandle = null;
        }
    }

    // ---------- build (in-process, no sh) ----------
    async function runBuild() {
        if (!state.activeName) {
            toast('No active site — pick one in the site dropdown', 'error', 4000);
            return;
        }
        state.activeKind = 'build';
        openModal('Build');
        appendLog(`[site-builder] build → ${state.activeName}`);

        try {
            const result = await fetchJson(
                `/api/sites/${encodeURIComponent(state.activeName)}/build`,
                { method: 'POST' });
            appendLog(`[site-builder] exitCode=${result.exitCode} outputDir=${result.outputDir}`);
            appendLog(`[site-builder] captured ${result.lines ?? 0} log lines`);
            setStatus('ready', 'done ✓');
            toast('Build complete', 'success');
        } catch (ex) {
            appendLog(`[site-builder] ERROR: ${ex.message}`);
            setStatus('error', 'failed');
            toast('Build failed', 'error', 4000);
        }
        state.activeKind = null;
    }

    // ---------- sh-script actions (preview / deploy / git-sync) ----------
    async function runScript(kind, title) {
        if (!state.activeName) {
            toast('No active site — pick one in the site dropdown', 'error', 4000);
            return;
        }
        state.activeKind = kind;
        openModal(title);
        appendLog(`[site-builder] ${kind} → ${state.activeName}`);

        try {
            const result = await fetchJson(
                `/api/sites/${encodeURIComponent(state.activeName)}/${kindEndpoint(kind)}`,
                { method: 'POST' });
            appendLog(`[site-builder] started PID=${result.pid} → ${result.logFile}`);
            if (result.previewUrl) {
                appendLog(`[site-builder] preview: ${result.previewUrl}`);
                if (els.modalOpen) {
                    els.modalOpen.href = result.previewUrl;
                    els.modalOpen.hidden = false;
                }
            }
            // Long-lived (preview) → poll. One-shot (deploy, gitsync) → server blocks until exit.
            if (kind === 'preview') {
                startPolling('preview', state.activeName);
                setStatus('running', `PID ${result.pid} running…`);
            } else {
                // One-shot: server holds the request open until script exits.
                // Stream partial log via polling too.
                startPolling(kind, state.activeName);
                setStatus('running', `PID ${result.pid} running…`);
            }
        } catch (ex) {
            appendLog(`[site-builder] ERROR: ${ex.message}`);
            setStatus('error', 'failed');
            toast(`${title} failed`, 'error', 4000);
            state.activeKind = null;
        }
    }
    function kindEndpoint(kind) {
        return {
            preview: 'preview',
            deploy: 'deploy',
            gitsync: 'git-sync',
        }[kind];
    }

    async function stopPreview() {
        if (!state.activeName) return;
        try {
            const r = await fetchJson('/api/scripts/preview/stop', { method: 'POST' });
            appendLog(`[site-builder] stopped (killed=${r.killed})`);
            setStatus('ready', 'stopped');
            toast('Preview stopped', 'success');
        } catch (ex) {
            appendLog(`[site-builder] stop failed: ${ex.message}`);
            setStatus('error', 'stop failed');
        }
        stopPolling();
        if (els.modalStop) els.modalStop.hidden = true;
        state.activeKind = null;
    }

    // ---------- close handlers ----------
    function wireModalCloses() {
        if (!els.modal) return;
        els.modal.querySelectorAll('[data-script-close]').forEach(el => {
            el.addEventListener('click', closeModal);
        });
        if (els.modalStop) els.modalStop.addEventListener('click', stopPreview);
    }

    // ---------- init ----------
    function init() {
        if (els.buildBtn)   els.buildBtn.addEventListener('click',   runBuild);
        if (els.previewBtn) els.previewBtn.addEventListener('click', () => runScript('preview', 'Preview'));
        if (els.deployBtn)  els.deployBtn.addEventListener('click',  () => runScript('deploy',  'Deploy'));
        if (els.gitsyncBtn) els.gitsyncBtn.addEventListener('click', () => runScript('gitsync', 'Git Sync'));
        if (els.siteMenuBtn) els.siteMenuBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            if (els.siteMenu && els.siteMenu.hidden) openSiteMenu();
            else closeSiteMenu();
        });
        wireModalCloses();
        refreshActive();
        window.addEventListener('active-site-changed', refreshActive);
    }

    init();
})();