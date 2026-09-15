// =====================================================
//  settings.js — site selector (v3, post-refactor)
//
// Auto-discovers sites from sites/<name>/config.json via /api/settings.
// User picks one to activate. The only mutable state is which site is
// active; per-site config lives in each site's config.json.
// =====================================================
(() => {
    'use strict';

    const els = {
        list: document.getElementById('sites-list'),
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

    // ---------- render ----------
    function renderSites(payload) {
        const sites = payload.sites || [];
        const activeName = payload.activeProjectName || '';

        if (!sites.length) {
            els.list.innerHTML = `
                <div class="site-card empty">
                    <div class="site-card-body">
                        <p class="muted">No sites found under <code>sites/</code>.</p>
                        <p class="muted small">
                            To add one: create <code>sites/&lt;name&gt;/config.json</code>
                            with a theme + host, then drop your markdown under
                            <code>sites/&lt;name&gt;/input/posts/</code>.
                        </p>
                    </div>
                </div>`;
            return;
        }

        const cards = sites.map(s => renderCard(s, s.name === activeName)).join('');
        els.list.innerHTML = cards;

        els.list.querySelectorAll('button[data-activate]').forEach(btn => {
            btn.addEventListener('click', () => activate(btn.dataset.activate));
        });
    }

    function renderCard(s, isActive) {
        const errorLine = s.error
            ? `<p class="muted small error">⚠ ${escapeHtml(s.error)}</p>`
            : '';
        const themeLine = s.theme
            ? `<code class="muted small">${escapeHtml(s.theme)}</code>`
            : '';
        const hostLine = s.host
            ? `<span class="muted small">host: ${escapeHtml(s.host)}</span>`
            : '';
        const counts = `<span class="muted small">${s.postCount ?? 0} posts</span>`;
        const badges = [];
        if (s.inputExists) badges.push('✓ input');
        if (s.themeExists) badges.push('✓ theme');
        if (s.outputExists) badges.push('✓ output');
        const badgeLine = badges.length
            ? `<span class="muted small">${badges.join(' · ')}</span>`
            : `<span class="muted small warn">⚠ missing input or theme</span>`;

        return `
            <div class="site-card${isActive ? ' is-active' : ''}" data-name="${escapeHtml(s.name)}">
                <div class="site-card-body">
                    <div class="site-card-head">
                        <h3 class="site-name">${escapeHtml(s.name)}</h3>
                        ${isActive ? '<span class="site-badge">ACTIVE</span>' : ''}
                    </div>
                    <div class="site-meta">
                        ${themeLine}
                        ${hostLine}
                        ${counts}
                        ${badgeLine}
                    </div>
                    ${errorLine}
                </div>
                <div class="site-card-actions">
                    <button type="button" class="btn btn-primary"
                            data-activate="${escapeHtml(s.name)}"
                            ${isActive ? 'disabled' : ''}>
                        ${isActive ? 'Active' : 'Activate'}
                    </button>
                </div>
            </div>`;
    }

    function escapeHtml(s) {
        return String(s ?? '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    // ---------- activate ----------
    async function activate(name) {
        try {
            await fetchJson('/api/projects/activate', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name }),
            });
            toast(`Active site → ${name}`, 'success');
            await load();
            // Notify other parts of the UI (top-nav dropdown, list page)
            // so they can re-fetch their own state.
            window.dispatchEvent(new CustomEvent('active-site-changed', { detail: { name } }));
        } catch (ex) {
            toast(`Activate failed: ${ex.message}`, 'error', 4000);
        }
    }

    // ---------- load ----------
    async function load() {
        try {
            const payload = await fetchJson('/api/settings');
            renderSites(payload);
        } catch (ex) {
            els.list.innerHTML = `
                <div class="site-card error">
                    <div class="site-card-body">
                        <p class="error">Failed to load sites: ${escapeHtml(ex.message)}</p>
                    </div>
                </div>`;
        }
    }

    load();
})();