// =====================================================
//  settings.js — multi-project settings (v2)
//
// Reads/writes the list of Statiq projects + the per-project details
// of the currently active one. The top-nav project dropdown (set up
// in _Layout.cshtml) calls /api/projects/activate directly when the
// user picks a different project; this page is the "edit everything"
// surface.
// =====================================================
(() => {
    'use strict';

    // In-memory state. Always reflects what's on disk after load/save.
    // We re-render the active panel from this on every change.
    const state = {
        projects: [],          // [{ name, root, contentSubdir, imagesSubdir,
                                //    previewScriptPath, deployScriptPath, previewPort }, ...]
        activeName: '',         // name of the active project
        rootExists: {},         // { [name]: boolean } — directory presence check
        // For each project, the status text shown next to its script
        // input ("✓ path set" / "✗ script not found on disk").
        scriptStatus: {},       // { [name]: { preview, deploy, previewFileExists, deployFileExists } }
    };

    const els = {
        form: document.getElementById('settings-form'),
        projectsField: document.getElementById('projects-field'),
        projectsHint: document.getElementById('projects-hint'),
        projectCards: document.getElementById('project-cards'),
        activeName: document.getElementById('active-name'),
        activePanel: document.getElementById('active-panel'),
        contentSubdir: document.querySelector('[name=contentSubdir]'),
        imagesSubdir: document.querySelector('[name=imagesSubdir]'),
        previewScriptPath: document.querySelector('[name=previewScriptPath]'),
        deployScriptPath: document.querySelector('[name=deployScriptPath]'),
        previewPort: document.querySelector('[name=previewPort]'),
        watermark: document.querySelector('[name=watermark]'),
        previewStatus: document.getElementById('preview-status'),
        deployStatus: document.getElementById('deploy-status'),
        resolvedPaths: document.getElementById('resolved-paths'),
        fullContent: document.getElementById('full-content'),
        fullImages: document.getElementById('full-images'),
        saveBtn: document.getElementById('save-btn'),
        resetBtn: document.getElementById('reset-btn'),
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

    // ---------- parse / serialise the projects textarea ----------
    //
    // Format: one project per line,
    //   "Name | /abs/path | optional-watermark"
    // Lines starting with '#' or empty are skipped. The pipe is the
    // separator so the user can put spaces in the path without escaping.
    // Name can't contain '|' (validated below). The watermark is a
    // free-form string drawn in the bottom-right of every uploaded image;
    // leave the third field empty to opt out.
    //
    // Known-project defaults: if a project's third field is empty AND
    // its name has a known default in DEFAULT_WATERMARKS, the default is
    // used. This way "add a |" to upgrade from the 2-column format
    // automatically gives you the project's default watermark — no need
    // to retype "Coderblog.In" etc. on every line.

    const DEFAULT_WATERMARKS = {
        'CoderBlog':    'Coderblog.In',
        'WinsonInvest': 'Winsoninvest.com',
        'Tableware':    'Tableware.com',
    };

    function applyDefaultWatermarks(projects) {
        return projects.map(p => ({
            ...p,
            watermark: p.watermark || DEFAULT_WATERMARKS[p.name] || '',
        }));
    }

    function parseProjectsTextarea(text) {
        const out = [];
        const errors = [];
        const seen = new Set();
        text.split('\n').forEach((rawLine, i) => {
            const line = rawLine.trim();
            if (!line || line.startsWith('#')) return;
            // Allow tabs as a fallback separator (for paste from spreadsheet).
            const sep = line.includes('|') ? '|' : (line.includes('\t') ? '\t' : null);
            if (!sep) {
                errors.push(`Line ${i + 1}: missing " | " separator`);
                return;
            }
            const parts = line.split(sep);
            const name = (parts.shift() || '').trim();
            // Last part is watermark (if 3+ parts), the rest is path.
            // This way a path that happens to contain '|' is preserved.
            const watermark = parts.length >= 2 ? (parts.pop() || '').trim() : '';
            const root = parts.join(sep).trim();
            if (!name) {
                errors.push(`Line ${i + 1}: empty name`);
                return;
            }
            if (!root) {
                errors.push(`Line ${i + 1}: empty path`);
                return;
            }
            if (seen.has(name)) {
                errors.push(`Line ${i + 1}: duplicate name "${name}"`);
                return;
            }
            seen.add(name);
            out.push({ name, root, watermark });
        });
        return { projects: out, errors };
    }

    function projectsToTextarea(projects) {
        return projects.map(p => {
            // Always emit the 3-field form so users editing the file by
            // hand see the watermark. Empty watermark still emits the
            // trailing "|" — fine, just looks like an empty third column.
            return `${p.name} | ${p.root} | ${p.watermark || ''}`;
        }).join('\n');
    }

    // ---------- load ----------
    async function load() {
        try {
            const s = await fetchJson('/api/settings');
            // Build a { name → exists } map.
            const exists = {};
            for (const p of s.projects) exists[p.name] = !!p.rootExists;
            // Backfill defaults on the client too — if the server somehow
            // returns an empty watermark for a known project (e.g. a
            // hand-edited appsettings.json), we still want the textarea
            // to show the project default so the user can see what
            // they're "opting out of". The first save will persist the
            // default back to the server.
            const projects = applyDefaultWatermarks(s.projects.map(p => ({
                name: p.name,
                root: p.root,
                rootExists: !!p.rootExists,
                contentSubdir: p.contentSubdir || 'input/posts',
                imagesSubdir: p.imagesSubdir || 'input/images',
                previewScriptPath: p.previewScriptPath ?? '',
                deployScriptPath: p.deployScriptPath ?? '',
                previewPort: p.previewPort || 5080,
                watermark: p.watermark ?? '',
            })));
            state.projects = projects;
            state.activeName = s.activeProjectName || (state.projects[0]?.name ?? '');
            state.rootExists = exists;

            els.projectsField.value = projectsToTextarea(state.projects);
            renderProjectsHint();
            renderProjectCards();
            renderActivePanel();

            if (s.migrated) {
                toast('Migrated config — defaults filled in (e.g. watermark text). Review and Save.', 'info', 4000);
            }
        } catch (e) {
            toast('Failed to load settings: ' + e.message, 'error');
        }
    }

    function renderProjectsHint() {
        const valid = state.projects.length;
        const total = state.projects.length;
        const validPathCount = state.projects.filter(p => p.rootExists).length;
        const parts = [`${valid} project${valid === 1 ? '' : 's'}`];
        if (validPathCount < total) {
            parts.push(`<span class="warn">${total - validPathCount} path${total - validPathCount === 1 ? '' : 's'} not found on disk</span>`);
        }
        els.projectsHint.innerHTML = parts.join(' · ');
    }

    function renderProjectCards() {
        if (!els.projectCards) return;
        if (state.projects.length === 0) {
            els.projectCards.innerHTML = '<p class="muted project-empty">No projects yet. Add one above.</p>';
            return;
        }
        els.projectCards.innerHTML = state.projects.map(p => {
            const active = p.name === state.activeName;
            const exists = p.rootExists;
            return `
                <div class="project-card${active ? ' active' : ''}" data-name="${escapeAttr(p.name)}">
                    <div class="project-card-main">
                        <div class="project-card-name">${escapeHtml(p.name)}${active ? ' <span class="active-badge">active</span>' : ''}</div>
                        <div class="project-card-root${exists ? '' : ' missing'}">${escapeHtml(p.root || '(no path)')}</div>
                    </div>
                    <div class="project-card-actions">
                        <button type="button" class="btn btn-ghost btn-activate" data-action="activate" data-name="${escapeAttr(p.name)}"${active ? ' disabled' : ''}>${active ? '✓ Active' : 'Activate'}</button>
                        <button type="button" class="btn btn-ghost btn-remove" data-action="remove" data-name="${escapeAttr(p.name)}" title="Remove from list">Remove</button>
                    </div>
                </div>
            `;
        }).join('');

        els.projectCards.querySelectorAll('button').forEach(btn => {
            btn.addEventListener('click', () => {
                const action = btn.dataset.action;
                const name = btn.dataset.name;
                if (action === 'activate') activateProject(name);
                else if (action === 'remove') removeProjectLocal(name);
            });
        });
    }

    function renderActivePanel() {
        const p = state.projects.find(x => x.name === state.activeName);
        if (!p) {
            els.activeName.textContent = 'No active project. Click "Activate" on one above, or add a new project.';
            els.activePanel.hidden = true;
            return;
        }
        els.activeName.textContent = `Editing: ${p.name} (${p.root})`;
        els.activePanel.hidden = false;

        els.contentSubdir.value = p.contentSubdir;
        els.imagesSubdir.value = p.imagesSubdir;
        els.previewScriptPath.value = p.previewScriptPath;
        els.deployScriptPath.value = p.deployScriptPath;
        els.previewPort.value = p.previewPort;
        if (els.watermark) els.watermark.value = p.watermark;

        // Status text under each script input
        setScriptStatus(els.previewStatus, p.previewScriptPath, p.root, 'preview');
        setScriptStatus(els.deployStatus,  p.deployScriptPath,  p.root, 'deploy');

        // Resolved paths
        if (p.root) {
            const contentFull = p.root + '/' + p.contentSubdir;
            const imagesFull = p.root + '/' + p.imagesSubdir;
            els.fullContent.textContent = contentFull;
            els.fullImages.textContent = imagesFull;
            els.resolvedPaths.hidden = false;
        } else {
            els.resolvedPaths.hidden = true;
        }
    }

    function setScriptStatus(el, rel, root, kind) {
        if (!el) return;
        if (!rel) {
            el.className = 'field-hint muted';
            el.textContent = `${kind} disabled (blank path)`;
            return;
        }
        // Browser security blocks fs access; show that the value
        // looks like a path and the server will validate on use.
        el.className = 'field-hint ok';
        el.textContent = `✓ path set → ${root}/${rel} (server validates on use)`;
    }

    // ---------- actions ----------
    async function activateProject(name) {
        try {
            await fetchJson('/api/projects/activate', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name }),
            });
            state.activeName = name;
            renderProjectCards();
            renderActivePanel();
            toast('Switched to ' + name + ' (refresh other pages to see the change)', 'info', 2400);
        } catch (err) {
            toast('Failed to switch project: ' + err.message, 'error');
        }
    }

    function removeProjectLocal(name) {
        if (state.projects.length <= 1) {
            toast('Cannot remove the last project — at least one is required.', 'warning');
            return;
        }
        if (!confirm(`Remove "${name}" from the list? Its files on disk are not touched.`)) return;
        state.projects = state.projects.filter(p => p.name !== name);
        if (state.activeName === name) {
            // Fall back to the first remaining project. The actual switch
            // happens on Save (so we don't trigger an out-of-band POST).
            state.activeName = state.projects[0].name;
        }
        // Re-render the textarea and cards. Save will re-persist everything.
        els.projectsField.value = projectsToTextarea(state.projects);
        renderProjectsHint();
        renderProjectCards();
        renderActivePanel();
    }

    // Live preview as the user types in the textarea: parse and show
    // the cards. Don't persist anything until they hit Save.
    function onProjectsTextareaInput() {
        const { projects, errors } = parseProjectsTextarea(els.projectsField.value);
        // Merge parsed (name + root + watermark) with existing per-project
        // details. For names that already exist in state, keep their
        // settings. For new names, use defaults.
        const oldByName = new Map(state.projects.map(p => [p.name, p]));
        state.projects = projects.map(p => {
            const existing = oldByName.get(p.name);
            return existing
                ? { ...existing, root: p.root, watermark: p.watermark || existing.watermark }
                : {
                    name: p.name,
                    root: p.root,
                    rootExists: false,    // we don't know — server will tell us on save
                    contentSubdir: 'input/posts',
                    imagesSubdir: 'input/images',
                    previewScriptPath: 'preview.sh',
                    deployScriptPath: 'build_deploy.sh',
                    previewPort: 5080,
                    watermark: p.watermark || '',
                };
        });
        if (!state.projects.some(p => p.name === state.activeName)) {
            state.activeName = state.projects[0]?.name ?? '';
        }
        renderProjectsHint(errors);
        renderProjectCards();
        renderActivePanel();
    }

    function renderProjectsHint(errors) {
        if (errors && errors.length) {
            els.projectsHint.innerHTML =
                `<span class="err">${errors.length} parse error${errors.length === 1 ? '' : 's'}:</span> ` +
                errors.slice(0, 3).map(e => `<span class="err">${escapeHtml(e)}</span>`).join(' · ') +
                (errors.length > 3 ? ` <span class="muted">(+${errors.length - 3} more)</span>` : '');
            return;
        }
        const total = state.projects.length;
        if (total === 0) {
            els.projectsHint.innerHTML = '<span class="muted">No projects yet. Add one line per project.</span>';
            return;
        }
        els.projectsHint.innerHTML = `<span class="ok">${total} project${total === 1 ? '' : 's'} parsed</span>`;
    }

    // ---------- save ----------
    async function save(e) {
        e.preventDefault();
        const { projects: parsed, errors } = parseProjectsTextarea(els.projectsField.value);
        if (errors.length) {
            toast('Fix the parse errors in the projects list first.', 'error', 3500);
            return;
        }
        if (parsed.length === 0) {
            toast('Add at least one project before saving.', 'error');
            return;
        }
        // Empty 3rd field on a known project → use the project default
        // (matches what the v2→v3 server migration does on first read).
        // This makes "add |" a one-step upgrade instead of forcing the
        // user to retype the watermark on every line.
        const withDefaults = applyDefaultWatermarks(parsed);

        // Merge current per-project details (subdirs, scripts, port,
        // watermark) into the parsed list. For new projects use
        // defaults. The textarea's per-line `watermark` (3rd field) is
        // the source of truth if non-empty; the active-panel input
        // overrides it for the *active* project.
        const oldByName = new Map(state.projects.map(p => [p.name, p]));
        const activeParsed = withDefaults.find(p => p.name === state.activeName);
        const panelWm = (els.watermark?.value || '').trim();
        const merged = withDefaults.map(p => {
            const old = oldByName.get(p.name);
            // Watermark precedence: 1) the active project's panel input
            // (if the user edited it), 2) the 3rd field on this project's
            // line, 3) the existing saved value, 4) empty.
            let wm;
            if (p === activeParsed && panelWm !== '') {
                wm = panelWm;
            } else if (p.watermark) {
                wm = p.watermark;
            } else {
                wm = old?.watermark ?? '';
            }
            return {
                name: p.name,
                root: p.root,
                contentSubdir: els.contentSubdir.value.trim()
                    || old?.contentSubdir || 'input/posts',
                imagesSubdir: els.imagesSubdir.value.trim()
                    || old?.imagesSubdir || 'input/images',
                previewScriptPath: els.previewScriptPath.value.trim()
                    ?? old?.previewScriptPath ?? '',
                deployScriptPath: els.deployScriptPath.value.trim()
                    ?? old?.deployScriptPath ?? '',
                previewPort: parseInt(els.previewPort.value, 10) || old?.previewPort || 5080,
                watermark: wm,
            };
        });

        const body = {
            projects: merged,
            activeProjectName: state.activeName,
        };

        els.saveBtn.disabled = true;
        try {
            await fetchJson('/api/settings', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body),
            });
            toast('Settings saved. Reloading…', 'success', 1200);
            setTimeout(() => location.reload(), 800);
        } catch (err) {
            toast('Save failed: ' + err.message, 'error', 4000);
            els.saveBtn.disabled = false;
        }
    }

    function reset() {
        load();
    }

    // ---------- escape ----------
    function escapeHtml(s) {
        return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    }
    function escapeAttr(s) { return escapeHtml(s); }

    // ---------- init ----------
    function init() {
        els.form.addEventListener('submit', save);
        els.resetBtn.addEventListener('click', reset);
        els.projectsField.addEventListener('input', onProjectsTextareaInput);
        document.addEventListener('DOMContentLoaded', load);
    }

    init();
})();
