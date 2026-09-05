// =====================================================
//  list.js — post list page (open / rename / delete)
// =====================================================
(() => {
    'use strict';

    const state = {
        // Server-side pagination. We track the active page + a
        // request id so out-of-order responses (e.g. user types fast)
        // can't overwrite a fresher view of the list.
        page: 1,
        pageSize: 12,
        total: 0,
        totalPages: 0,
        posts: [],
        categories: [],
        query: '',
        category: '',
        _requestId: 0,
    };

    const els = {
        list: document.getElementById('post-list'),
        cards: document.getElementById('post-cards'),
        empty: document.getElementById('post-list-empty'),
        loading: document.getElementById('post-list-loading'),
        count: document.getElementById('post-count'),
        search: document.getElementById('search-input'),
        categoryFilter: document.getElementById('category-filter'),
        pagination: document.getElementById('pagination'),
        // rename modal
        renameModal: document.getElementById('rename-modal'),
        renameForm: document.getElementById('rename-form'),
        renameSubmit: document.getElementById('rename-submit'),
        renameCurrent: document.getElementById('rename-current'),
        renameNew: document.getElementById('rename-new'),
        // delete modal
        deleteModal: document.getElementById('delete-modal'),
        deleteName: document.getElementById('delete-name'),
        deleteConfirm: document.getElementById('delete-confirm'),
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

    // ---------- modal helpers ----------
    function openModal(m) { m.hidden = false; }
    function closeModal(m) { m.hidden = true; }
    function wireModals() {
        document.querySelectorAll('.modal').forEach(m => {
            m.addEventListener('click', e => {
                if (e.target.matches('[data-close]')) closeModal(m);
            });
        });
        document.addEventListener('keydown', e => {
            if (e.key === 'Escape') {
                document.querySelectorAll('.modal:not([hidden])').forEach(closeModal);
            }
        });
    }

    // ---------- data ----------
    async function fetchJson(url, opts) {
        const r = await fetch(url, opts);
        if (!r.ok) {
            let msg = 'HTTP ' + r.status;
            try { const j = await r.json(); if (j?.error) msg = j.error; } catch {}
            throw new Error(msg);
        }
        return r.json();
    }

    // Fetch the current page of posts from the server. The server
    // already applies the category + search filter; we just render
    // the returned page.
    async function loadPage() {
        const reqId = ++state._requestId;
        els.loading.hidden = false;
        els.loading.textContent = 'Loading…';
        try {
            const params = new URLSearchParams();
            params.set('page', String(state.page));
            params.set('pageSize', String(state.pageSize));
            if (state.category) params.set('category', state.category);
            if (state.query.trim()) params.set('q', state.query.trim());
            const [page, cats] = await Promise.all([
                fetchJson('/api/posts?' + params.toString()),
                fetchJson('/api/categories'),
            ]);
            // Drop stale responses (e.g. user typed in the search box
            // between the request firing and resolving).
            if (reqId !== state._requestId) return;
            state.posts = page.items;
            state.total = page.total;
            state.totalPages = page.totalPages;
            state.page = page.page;
            state.categories = cats;
            renderCategoryOptions();
            render();
        } catch (e) {
            if (reqId !== state._requestId) return;
            els.loading.textContent = 'Failed to load: ' + e.message;
            toast('Load failed: ' + e.message, 'error');
        }
    }

    // For "rename" / "delete" we want to refresh in-place without
    // bumping the request id (the user is mid-action). Just refetch
    // the current page and trust the server to clamp page if the
    // result set shrank.
    async function loadCurrentPage() {
        const reqId = ++state._requestId;
        try {
            const params = new URLSearchParams();
            params.set('page', String(state.page));
            params.set('pageSize', String(state.pageSize));
            if (state.category) params.set('category', state.category);
            if (state.query.trim()) params.set('q', state.query.trim());
            const r = await fetchJson('/api/posts?' + params.toString());
            if (reqId !== state._requestId) return;
            state.posts = r.items;
            state.total = r.total;
            state.totalPages = r.totalPages;
            state.page = r.page;
            render();
        } catch (e) {
            toast('Refresh failed: ' + e.message, 'error');
        }
    }

    function renderCategoryOptions() {
        const cur = els.categoryFilter.value;
        els.categoryFilter.innerHTML = '<option value="">All categories</option>' +
            state.categories.map(c => `<option value="${escapeAttr(c)}">${escapeHtml(c)}</option>`).join('');
        // Only reset to current state.category if it still exists in
        // the new list. Otherwise the filter would orphan a category
        // that just got removed by another tab.
        if (state.category && !state.categories.some(c => c.toLowerCase() === state.category.toLowerCase())) {
            state.category = '';
        }
        els.categoryFilter.value = state.category;
        if (cur && !els.categoryFilter.value) {
            // user-selected value was dropped — fall through with ''
        }
    }

    // ---------- render ----------
    function render() {
        els.loading.hidden = true;

        if (state.total === 0) {
            els.cards.innerHTML = '';
            els.empty.hidden = false;
            els.pagination.hidden = true;
            els.count.textContent = '0 posts';
            return;
        }
        els.empty.hidden = true;

        // Server already paginated, so the visible count is just the
        // current page slice.
        const visible = state.posts.length;
        const noun = state.total === 1 ? 'post' : 'posts';
        if (state.totalPages <= 1) {
            els.count.textContent = `${state.total} ${noun}`;
        } else {
            const start = (state.page - 1) * state.pageSize + 1;
            const end = start + visible - 1;
            els.count.textContent = `${start}–${end} of ${state.total} ${noun}`;
        }

        els.cards.innerHTML = state.posts.map(p => renderCard(p)).join('');

        els.cards.querySelectorAll('[data-action]').forEach(btn => {
            btn.addEventListener('click', e => {
                e.preventDefault();
                const action = btn.dataset.action;
                const path = btn.dataset.path;
                if (action === 'open') openEditor(path);
                else if (action === 'rename') openRename(path);
                else if (action === 'delete') openDelete(path);
            });
        });
        els.cards.querySelectorAll('.meta-cat.clickable').forEach(el => {
            el.addEventListener('click', () => {
                els.categoryFilter.value = el.dataset.cat;
                state.category = el.dataset.cat;
                state.page = 1;
                loadPage();
            });
        });

        renderPagination();
    }

    function renderPagination() {
        const nav = els.pagination;
        if (!nav) return;
        if (state.totalPages <= 1) {
            nav.hidden = true;
            nav.innerHTML = '';
            return;
        }
        nav.hidden = false;

        // Build a windowed list of page numbers: always show 1 and the
        // last page, plus a few around the current page. Use "…" for
        // gaps. Cap the window so a 200-page list doesn't render 200
        // buttons.
        const cur = state.page;
        const total = state.totalPages;
        const pages = new Set([1, total, cur, cur - 1, cur + 1]);
        if (cur <= 3) { pages.add(2); pages.add(3); }
        if (cur >= total - 2) { pages.add(total - 1); pages.add(total - 2); }
        const sorted = [...pages].filter(p => p >= 1 && p <= total).sort((a, b) => a - b);

        const out = [];
        out.push(`<button type="button" class="pg-btn" data-page="${cur - 1}"${cur <= 1 ? ' disabled' : ''} aria-label="Previous page">‹</button>`);
        let prev = 0;
        for (const p of sorted) {
            if (p - prev > 1) out.push(`<span class="pg-gap" aria-hidden="true">…</span>`);
            const cls = p === cur ? 'pg-btn pg-current' : 'pg-btn';
            out.push(`<button type="button" class="${cls}" data-page="${p}"${p === cur ? ' aria-current="page"' : ''}>${p}</button>`);
            prev = p;
        }
        out.push(`<button type="button" class="pg-btn" data-page="${cur + 1}"${cur >= total ? ' disabled' : ''} aria-label="Next page">›</button>`);
        nav.innerHTML = out.join('');

        nav.querySelectorAll('button.pg-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                const p = parseInt(btn.dataset.page, 10);
                if (!p || p < 1 || p > state.totalPages || p === state.page) return;
                state.page = p;
                loadPage();
                // Scroll the page so the user sees the new list (the
                // toolbar stays pinned; the list moves under it).
                window.scrollTo({ top: 0, behavior: 'smooth' });
            });
        });
    }

    function renderCard(p) {
        const date = p.date ? new Date(p.date).toISOString().slice(0, 10) : '—';
        const cat = p.category
            ? `<span class="meta-cat clickable" data-cat="${escapeAttr(p.category)}" title="Filter by this category">${escapeHtml(p.category)}</span>`
            : '';
        const tags = (p.tags || [])
            .slice(0, 6)
            .map(t => `<span class="post-card-tag">${escapeHtml(t)}</span>`)
            .join('');
        const desc = p.description ? `<p class="post-card-desc">${escapeHtml(p.description)}</p>` : '';
        const path = p.relativePath || '';
        // Encode each path segment separately so "/" stays literal (catch-all
        // route does not decode %2F) and special characters like "#" get encoded.
        const pathForUrl = path.split('/').map(encodeURIComponent).join('/');
        return `
            <li class="post-card">
                <div class="post-card-main">
                    <h3 class="post-card-title">
                        <a href="/editor?path=${pathForUrl}" data-action="open" data-path="${escapeAttr(path)}">${escapeHtml(p.title || path)}</a>
                    </h3>
                    <div class="post-card-meta">
                        <span class="meta-date">${escapeHtml(date)}</span>
                        ${cat}
                        <span class="meta-words">${(p.wordCount || 0).toLocaleString()} words · ${(p.charCount || 0).toLocaleString()} chars</span>
                    </div>
                    ${desc}
                    ${tags ? `<div class="post-card-tags">${tags}</div>` : ''}
                </div>
                <div class="post-card-actions">
                    <div class="post-card-path" title="${escapeAttr(path)}">${escapeHtml(path)}</div>
                    <div class="post-card-buttons">
                        <a class="btn btn-ghost" href="/editor?path=${pathForUrl}" data-action="open" data-path="${escapeAttr(path)}">Open</a>
                        <button class="btn btn-ghost btn-rename" data-action="rename" data-path="${escapeAttr(path)}">Rename</button>
                        <button class="btn btn-ghost btn-delete" data-action="delete" data-path="${escapeAttr(path)}">Delete</button>
                    </div>
                </div>
            </li>
        `;
    }

    function openEditor(path) {
        // Encode each path segment separately so the "/" separator stays
        // literal — ASP.NET Core's catch-all route parameter does not decode
        // %2F, so encoding the slash would break the route.
        const encoded = path.split('/').map(encodeURIComponent).join('/');
        window.location.href = '/editor?path=' + encoded;
    }

    // ---------- rename ----------
    let renameTargetPath = null;
    function openRename(path) {
        renameTargetPath = path;
        els.renameCurrent.value = path;
        const baseName = path.split('/').pop().replace(/\.md$/, '');
        els.renameNew.value = baseName;
        openModal(els.renameModal);
        setTimeout(() => {
            els.renameNew.focus();
            els.renameNew.select();
        }, 0);
    }
    async function submitRename(e) {
        e.preventDefault();
        if (!renameTargetPath) return;
        const newSlug = els.renameNew.value.trim();
        if (!newSlug) return;
        els.renameSubmit.disabled = true;
        try {
            const r = await fetchJson('/api/posts/rename?path=' + encodeURIComponent(renameTargetPath), {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ newSlug }),
            });
            closeModal(els.renameModal);
            toast('Renamed to ' + r.path, 'success');
            await loadCurrentPage();
        } catch (err) {
            toast('Rename failed: ' + err.message, 'error');
        } finally {
            els.renameSubmit.disabled = false;
        }
    }

    // ---------- delete ----------
    let deleteTargetPath = null;
    function openDelete(path) {
        deleteTargetPath = path;
        els.deleteName.textContent = path;
        openModal(els.deleteModal);
    }
    async function confirmDelete() {
        if (!deleteTargetPath) return;
        els.deleteConfirm.disabled = true;
        try {
            await fetchJson('/api/posts/' + encodeURI(deleteTargetPath), { method: 'DELETE' });
            closeModal(els.deleteModal);
            toast('Deleted ' + deleteTargetPath, 'success');
            // The current page may now be empty if this was the last
            // item on it. loadCurrentPage → server clamps `page` back
            // into range, so we land on a valid page automatically.
            await loadCurrentPage();
        } catch (err) {
            toast('Delete failed: ' + err.message, 'error');
        } finally {
            els.deleteConfirm.disabled = false;
        }
    }

    // ---------- escape ----------
    function escapeHtml(s) {
        return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    }
    function escapeAttr(s) { return escapeHtml(s); }

    // ---------- init ----------
    function init() {
        wireModals();
        // Typing in the search box resets to page 1 — otherwise a user
        // could type a query that returns 3 results while sitting on
        // page 12, and see an empty list.
        els.search.addEventListener('input', () => {
            state.query = els.search.value;
            state.page = 1;
            loadPage();
        });
        els.categoryFilter.addEventListener('change', () => {
            state.category = els.categoryFilter.value;
            state.page = 1;
            loadPage();
        });
        els.renameForm.addEventListener('submit', submitRename);
        els.deleteConfirm.addEventListener('click', confirmDelete);
        loadPage();
    }

    document.addEventListener('DOMContentLoaded', init);
})();
