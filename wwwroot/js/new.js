// =====================================================
//  new.js — New post form (live frontmatter preview + submit)
// =====================================================
(() => {
    'use strict';

    const els = {
        form: document.getElementById('new-post-form'),
        title: document.querySelector('[name=title]'),
        slug: document.getElementById('slug-field'),
        slugLabel: document.getElementById('slug-label'),
        slugHint: document.getElementById('slug-hint'),
        slugError: document.getElementById('slug-error'),
        date: document.getElementById('date-field'),
        layout: document.getElementById('layout-field'),
        category: document.querySelector('[name=category]'),
        tags: document.querySelector('[name=tags]'),
        image: document.querySelector('[name=image]'),
        description: document.querySelector('[name=description]'),
        draft: document.getElementById('draft-field'),
        knownCategories: document.getElementById('known-categories'),
        submitBtn: document.getElementById('submit-btn'),
        preview: document.getElementById('fm-preview'),
    };

    // Default date = today (local)
    if (!els.date.value) {
        const d = new Date();
        const yyyy = d.getFullYear();
        const mm = String(d.getMonth() + 1).padStart(2, '0');
        const dd = String(d.getDate()).padStart(2, '0');
        els.date.value = `${yyyy}-${mm}-${dd}`;
    }

    // CJK detection — covers CJK Unified, Extension A, Compatibility, and
    // fullwidth forms. (Not exhaustive for all CJK ranges, but covers 99.9% of
    // the characters a real title would use.)
    const CJK_REGEX = /[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\uff00-\uffef]/;
    function hasCjk(s) {
        return CJK_REGEX.test(s || '');
    }

    // Slug format: kebab-case ASCII (lowercase letters, digits, dashes).
    // Mirrors MarkdownFileService.Slugify on the server.
    const SLUG_REGEX = /^[a-z0-9]+(-[a-z0-9]+)*$/;

    // ----- helpers -----
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
    async function fetchJson(url, opts) {
        const r = await fetch(url, opts);
        if (!r.ok) {
            let msg = 'HTTP ' + r.status;
            try { const j = await r.json(); if (j?.error) msg = j.error; } catch {}
            throw new Error(msg);
        }
        return r.json();
    }

    function slugify(s) {
        return (s || '').toLowerCase()
            .replace(/[^a-z0-9]+/g, '-')
            .replace(/-+/g, '-')
            .replace(/^-|-$/g, '');
    }

    function yamlQuote(v) {
        // Always wrap strings in quotes. Empty values are still emitted
        // as `""` so the key is always present in the file.
        return '"' + String(v ?? '').replace(/"/g, '\\"') + '"';
    }

    function buildPreview() {
        const title = els.title.value.trim();
        const slug = els.slug.value.trim() || (title ? slugify(title) : '');
        const year = (els.date.value || new Date().toISOString().slice(0, 10)).slice(0, 4);
        const fileName = slug ? `${slug}${els.slug.value.trim() ? '' : '-' + year}.md` : '(derived-from-title).md';
        const layout = els.layout.value;
        const date = els.date.value || 'YYYY-MM-DD';
        const category = els.category.value.trim();
        const image = els.image.value.trim();
        const description = els.description.value.trim();
        const tags = (els.tags.value || '')
            .split(',').map(s => s.trim()).filter(Boolean);
        const draft = !!(els.draft && els.draft.checked);

        // All 8 fields are always emitted, in canonical order, even when
        // blank. This mirrors the server-side FrontmatterService and
        // means the file is always self-explanatory: every knob a user
        // might want to tweak is already on the page.
        const lines = ['---'];
        lines.push(`Title: ${yamlQuote(title)}`);
        lines.push(`Description: ${yamlQuote(description)}`);
        lines.push(`Date: ${date}`);
        lines.push(`Layout: ${yamlQuote(layout)}`);
        lines.push(`Image: ${yamlQuote(image)}`);
        lines.push(`Category: ${yamlQuote(category)}`);
        lines.push(`Draft: ${draft ? 'true' : 'false'}`);
        lines.push(`Tags: [${tags.join(', ')}]`);
        lines.push('---');
        lines.push('');
        lines.push('Write your post here. The intro paragraph is what shows up in the homepage card, so make it count.');
        lines.push('');
        lines.push(`# File: ${fileName}`);

        els.preview.textContent = lines.join('\n');
    }

    function updateSlugHint() {
        const title = els.title.value.trim();
        const slug = els.slug.value.trim();
        const cjk = hasCjk(title);

        if (cjk) {
            // Title contains CJK — the auto-derived slug is not safe for URLs.
            // Slug becomes required; the field is highlighted.
            els.slug.setAttribute('required', '');
            els.slug.setAttribute('aria-required', 'true');
            els.slugLabel.innerHTML = 'Slug <em class="req">(required — title contains CJK)</em>';
            if (!slug) {
                els.slugHint.className = 'field-hint warn';
                els.slugHint.textContent = 'Title contains CJK characters. Please enter an ASCII slug (lowercase, dashes) so the file name and URL stay clean.';
            } else if (!SLUG_REGEX.test(slug)) {
                els.slugHint.className = 'field-hint warn';
                els.slugHint.textContent = 'Slug must be lowercase ASCII letters, digits, and dashes.';
            } else {
                els.slugHint.className = 'field-hint ok';
                els.slugHint.textContent = 'Will become: ' + slug + '.md';
            }
        } else {
            // Title is plain ASCII (or empty) — slug is optional.
            els.slug.removeAttribute('required');
            els.slug.removeAttribute('aria-required');
            els.slugLabel.innerHTML = 'Slug <em>(optional, derived from title)</em>';
            if (slug) {
                if (!SLUG_REGEX.test(slug)) {
                    els.slugHint.className = 'field-hint warn';
                    els.slugHint.textContent = 'Custom slug must be lowercase ASCII letters, digits, and dashes.';
                } else {
                    els.slugHint.className = 'field-hint ok';
                    els.slugHint.textContent = 'Custom slug — the year suffix will NOT be added.';
                }
            } else if (title) {
                const yyyy = (els.date.value || new Date().toISOString().slice(0, 10)).slice(0, 4);
                els.slugHint.className = 'field-hint';
                els.slugHint.textContent = `Will become: ${slugify(title)}-${yyyy}.md`;
            } else {
                els.slugHint.className = 'field-hint';
                els.slugHint.textContent = '';
            }
        }

        // Toggle invalid styling
        if (slug && !SLUG_REGEX.test(slug)) {
            els.slug.setAttribute('aria-invalid', 'true');
            els.slug.classList.add('invalid');
        } else {
            els.slug.removeAttribute('aria-invalid');
            els.slug.classList.remove('invalid');
        }

        // Error row: only used for hard validation failures (shown on submit)
        els.slugError.hidden = true;
        els.slugError.textContent = '';
    }

    async function loadCategories() {
        try {
            const cats = await fetchJson('/api/categories');
            els.knownCategories.innerHTML = cats.map(c => `<option value="${escapeAttr(c)}">`).join('');
        } catch {}
    }

    async function submit(e) {
        e.preventDefault();

        // Final validation pass — surface the same checks the live hint does,
        // but as inline errors that block submit.
        const title = els.title.value.trim();
        const slug = els.slug.value.trim();
        let problem = null;
        if (!title) problem = 'Title is required.';
        else if (hasCjk(title) && !slug) {
            problem = 'Title contains CJK characters — please enter an English slug so the file name and URL are valid.';
        }
        else if (slug && !SLUG_REGEX.test(slug)) {
            problem = 'Slug must be lowercase ASCII letters, digits, and dashes (kebab-case).';
        }

        if (problem) {
            els.slugError.textContent = problem;
            els.slugError.hidden = false;
            els.slug.classList.add('invalid');
            if (!slug || !SLUG_REGEX.test(slug)) {
                els.slug.focus();
            } else {
                els.title.focus();
            }
            return;
        }

        const body = {
            title,
            slug: slug || undefined,
            date: els.date.value || undefined,
            layout: els.layout.value || undefined,
            category: els.category.value.trim() || undefined,
            description: els.description.value.trim() || undefined,
            image: els.image.value.trim() || undefined,
            tags: (els.tags.value || '').split(',').map(s => s.trim()).filter(Boolean),
            draft: !!(els.draft && els.draft.checked),
        };
        els.submitBtn.disabled = true;
        try {
            const r = await fetchJson('/api/posts', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body),
            });
            toast('Created: ' + r.path, 'success', 1200);
            // Hand off to the editor so the user can start writing (and Grammarly kicks in)
            // Encode each path segment so "/" stays literal and "#" gets encoded.
            setTimeout(() => {
                const pathForUrl = r.path.split('/').map(encodeURIComponent).join('/');
                window.location.href = '/editor?path=' + pathForUrl;
            }, 600);
        } catch (err) {
            toast('Create failed: ' + err.message, 'error', 4000);
            els.submitBtn.disabled = false;
        }
    }

    function escapeAttr(s) {
        return String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    }

    // ----- init -----
    ['input', 'change'].forEach(ev => {
        els.form.addEventListener(ev, () => {
            updateSlugHint();
            buildPreview();
        });
    });
    els.form.addEventListener('submit', submit);
    document.addEventListener('DOMContentLoaded', () => {
        loadCategories();
        updateSlugHint();
        buildPreview();
    });
})();
