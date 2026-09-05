// =====================================================
//  editor.js — Monaco editor + paste handling (image + HTML→md)
// =====================================================
(() => {
    'use strict';

    // ----------------------------------------------------------
    //  Statiq <?# Figure ?> shortcode → marked block extension
    // ----------------------------------------------------------
    //
    // The editor's preview is rendered with marked.js, which doesn't
    // know about Statiq shortcodes. Without this extension, a block
    // like
    //
    //   <?# Figure src="/images/x.png" alt="..." ?>
    //   Fig 1. caption
    //   <?#/ Figure ?>
    //
    // shows up in the preview as raw text. The extension below
    // tokenizes the paired shortcode as a block-level element and
    // emits:
    //
    //   <figure class="statiq-figure">
    //     <img src="..." alt="...">
    //     <figcaption>Fig 1. caption</figcaption>
    //   </figure>
    //
    // The body between the open/close tags is run through
    // parser.parseInline so bold/italic/links inside the caption work.
    //
    // Only the *paired* form is supported (self-closing is rejected
    // by Statiq anyway — see the in-repo convention). Unknown
    // attributes (class, title, …) are passed through as <img>
    // attributes.
    //
    if (window.marked && typeof window.marked.use === 'function' && !window.__statiqFigureExtRegistered) {
        window.__statiqFigureExtRegistered = true;
        const _figEscAttr = (s) => String(s).replace(/[&<>"']/g, c => (
            { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
        ));
        const _figEscText = (s) => String(s).replace(/[&<>]/g, c => (
            { '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]
        ));
        // Parse the attribute string "src='x' alt=\"y\" class=z" into a dict.
        const _figParseAttrs = (s) => {
            const out = {};
            const re = /([\w-]+)\s*=\s*(?:"([^"]*)"|'([^']*)'|(\S+))/g;
            let m;
            while ((m = re.exec(s)) !== null) {
                out[m[1]] = m[2] ?? m[3] ?? m[4] ?? '';
            }
            return out;
        };
        // Open: <?# Figure ... ?>\n  Close: <?#/ Figure ?>
        // Body: any text (incl. inline markdown) on the lines between.
        const _figRule = /^<\?#\s*Figure\s+([\s\S]+?)\s*\?>\s*\n?([\s\S]*?)\n?\s*<\?#\/\s*Figure\s*\?>/;
        window.marked.use({
            extensions: [{
                name: 'statiqFigure',
                level: 'block',
                // No `start()` hint: a `<?# Figure` substring can also appear
                // *inside* an inline code span (e.g. "use `<?# Figure ?>` for
                // ..."), and a naive start-position hint would point the
                // lexer into the middle of an inline run and corrupt the
                // surrounding paragraph. Marked will call our tokenizer at
                // each position — slightly slower, but correct.
                tokenizer(src) {
                    const m = _figRule.exec(src);
                    if (!m) return false;
                    return {
                        type: 'statiqFigure',
                        raw: m[0],
                        attrs: _figParseAttrs(m[1].trim()),
                        body: m[2].trim(),
                    };
                },
                renderer(token) {
                    const a = token.attrs || {};
                    const imgAttrs = [];
                    if (a.src)  imgAttrs.push(`src="${_figEscAttr(a.src)}"`);
                    if (a.alt != null) imgAttrs.push(`alt="${_figEscAttr(a.alt)}"`);
                    if (a.title) imgAttrs.push(`title="${_figEscAttr(a.title)}"`);
                    if (a.class) imgAttrs.push(`class="${_figEscAttr(a.class)}"`);
                    let caption = '';
                    if (token.body) {
                        // Inside the renderer, `this.parser.parseInline` is the
                        // *instance* method — it expects an array of inline
                        // tokens, not a raw string. The static-style entry
                        // point we want is `marked.parseInline(text)`, which
                        // runs the full lex→parse pipeline.
                        try {
                            caption = window.marked.parseInline(token.body);
                        } catch (e) {
                            console.warn('[statiqFigure] parseInline failed:', e);
                            caption = _figEscText(token.body);
                        }
                    }
                    const img = a.src ? `<img ${imgAttrs.join(' ')}>` : '';
                    const cap = caption ? `<figcaption>${caption}</figcaption>` : '';
                    return `<figure class="statiq-figure">${img}${cap}</figure>\n`;
                },
            }],
        });
    }

    const params = new URLSearchParams(location.search);
    const filePath = params.get('path') || '';

    // Encode each path segment separately so "/" stays literal — ASP.NET
    // Core's catch-all route parameter does not decode %2F, so encoding the
    // slash would break the route. Also escapes "#", "?", "&", etc. inside
    // each segment.
    const pathForUrl = (p) => p.split('/').map(encodeURIComponent).join('/');

    const els = {
        pathLabel: document.getElementById('file-path'),
        stats: document.getElementById('file-stats'),
        saveBtn: document.getElementById('save-btn'),
        discardBtn: document.getElementById('discard-btn'),
        dirty: document.getElementById('dirty-indicator'),
        loading: document.getElementById('editor-loading'),
        statusLeft: document.getElementById('statusbar-left'),
        statusRight: document.getElementById('statusbar-right'),
        spellToggle: document.getElementById('spell-toggle'),
        spellToggleLabel: document.getElementById('spell-toggle-label'),
        ltToggle: document.getElementById('lt-toggle'),
        ltToggleLabel: document.getElementById('lt-toggle-label'),
        autoSaveToggle: document.getElementById('auto-save-toggle'),
        autoSaveToggleLabel: document.getElementById('auto-save-toggle-label'),
        formatToolbar: document.getElementById('format-toolbar'),
        fileInput: document.getElementById('image-file-input'),
        previewBtn: document.getElementById('preview-btn'),
        deployBtn: document.getElementById('deploy-btn'),
        // Script-output modal
        scriptModal: document.getElementById('script-modal'),
        scriptTitle: document.getElementById('script-modal-title'),
        scriptStatus: document.getElementById('script-modal-status'),
        scriptLog: document.getElementById('script-log'),
        scriptOpenLink: document.getElementById('script-open-link'),
        scriptStopBtn: document.getElementById('script-stop-btn'),
    };

    let editor = null;
    let initialValue = '';
    let saving = false;
    let lastSavedValue = '';

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

    // ---------- helpers ----------
    async function fetchJson(url, opts) {
        const r = await fetch(url, opts);
        if (!r.ok) {
            let msg = 'HTTP ' + r.status;
            try { const j = await r.json(); if (j?.error) msg = j.error; } catch {}
            throw new Error(msg);
        }
        return r.json();
    }

    function setStatus(left, right) {
        if (left !== undefined) els.statusLeft.textContent = left;
        if (right !== undefined) els.statusRight.textContent = right;
    }

    function updateStats() {
        if (!editor) return;
        const text = editor.getValue();
        const words = countWords(text);
        const chars = text.length;
        const lines = editor.getModel()?.getLineCount() || 0;
        const col = editor.getPosition()?.column || 1;
        const ln = editor.getPosition()?.lineNumber || 1;
        els.stats.textContent = `${words.toLocaleString()} words · ${chars.toLocaleString()} chars`;
        setStatus(undefined, `Ln ${ln}, Col ${col} · ${lines} lines · UTF-8`);
    }

    function countWords(s) {
        if (!s) return 0;
        let n = 0, inWord = false;
        for (let i = 0; i < s.length; i++) {
            const c = s.charCodeAt(i);
            const isWs = c === 32 || c === 9 || c === 10 || c === 13 || (c >= 0x2000 && c <= 0x200a);
            if (isWs) inWord = false;
            else if (!inWord) { inWord = true; n++; }
        }
        return n;
    }

    function isDirty() {
        if (!editor) return false;
        return editor.getValue() !== lastSavedValue;
    }

    function refreshDirty() {
        const dirty = isDirty();
        els.dirty.hidden = !dirty;
        els.saveBtn.disabled = !dirty || saving;
        els.discardBtn.hidden = !dirty;
    }

    // ---------- save / discard ----------
    async function save() {
        if (!editor || !filePath) return;
        if (!isDirty()) {
            toast('Nothing to save', 'info', 1200);
            return;
        }
        saving = true;
        els.saveBtn.disabled = true;
        setStatus('Saving…', undefined);
        try {
            const raw = editor.getValue();
            await fetchJson('/api/posts/' + pathForUrl(filePath), {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ relativePath: filePath, rawText: raw }),
            });
            lastSavedValue = raw;
            refreshDirty();
            setStatus('Saved ' + new Date().toLocaleTimeString(), undefined);
            toast('Saved', 'success', 1200);
        } catch (err) {
            setStatus('Save failed: ' + err.message, undefined);
            toast('Save failed: ' + err.message, 'error', 4000);
        } finally {
            saving = false;
            refreshDirty();
        }
    }

    function discard() {
        if (!editor) return;
        if (!confirm('Discard unsaved changes?')) return;
        editor.setValue(initialValue);
        lastSavedValue = initialValue;
        refreshDirty();
        setStatus('Discarded', undefined);
    }

    // ---------- load file ----------
    async function loadFile() {
        if (!filePath) {
            els.loading.textContent = 'No file path specified. Add ?path=your-post.md to the URL.';
            return;
        }
        els.pathLabel.textContent = filePath;
        document.title = filePath + ' — Statiq MD Editor';
        setStatus('Loading ' + filePath + '…', undefined);
        try {
            const data = await fetchJson('/api/posts/' + pathForUrl(filePath));
            const raw = data.rawText ?? '';
            await mountMonaco(raw);
            initialValue = raw;
            lastSavedValue = raw;
            updateStats();
            setStatus('Loaded — paste an image or HTML to import', undefined);
            refreshDirty();
            renderPreview();
            setViewMode('split');
        } catch (err) {
            els.loading.textContent = 'Failed to load: ' + err.message;
            toast('Load failed: ' + err.message, 'error', 4000);
        }
    }

    // ---------- paste handling ----------
    let turndownInstance = null;
    function getTurndown() {
        if (turndownInstance) return turndownInstance;
        if (typeof window.TurndownService !== 'function') return null;
        turndownInstance = new window.TurndownService({
            headingStyle: 'atx',         // # Heading (Statiq convention)
            bulletListMarker: '-',       // matches existing posts
            codeBlockStyle: 'fenced',    // matches existing posts
            emDelimiter: '*',
            strongDelimiter: '**',
            linkStyle: 'inlined',
            hr: '---',
        });
        // Strip noise that doesn't belong in a markdown post
        turndownInstance.remove([
            'script', 'style', 'noscript', 'iframe', 'button', 'form',
            'input', 'select', 'textarea', 'svg',
        ]);
        // Drop empty paragraphs and stray spans
        turndownInstance.addRule('dropEmpty', {
            filter: node => {
                if (node.nodeType !== 1) return false;
                const tag = node.nodeName.toLowerCase();
                if (tag === 'p' || tag === 'div' || tag === 'span') {
                    return node.textContent.trim() === '';
                }
                return false;
            },
            replacement: () => '',
        });
        // Convert <pre><code> with class="language-xxx" into a fenced block with
        // the language tag. Turndown's default already does this for some classes
        // but we make it explicit.
        turndownInstance.addRule('fencedCodeWithLang', {
            filter: node =>
                node.nodeName === 'PRE' &&
                node.firstChild?.nodeName === 'CODE',
            replacement: (content, node) => {
                const code = node.firstChild;
                const langClass = Array.from(code.classList || []).find(c => c.startsWith('language-'));
                const lang = langClass ? langClass.replace('language-', '') : '';
                return '\n\n```' + lang + '\n' + code.textContent.replace(/\n$/, '') + '\n```\n\n';
            },
        });
        return turndownInstance;
    }

    function insertAtCursor(text) {
        if (!editor) return;
        const sel = editor.getSelection();
        editor.executeEdits('paste-handler', [{
            range: sel,
            text,
            forceMoveMarkers: true,
        }]);
        // Move cursor to the end of the inserted text
        const lines = text.split('\n');
        const lastLineLen = lines[lines.length - 1].length;
        const newLine = sel.startLineNumber + lines.length - 1;
        const newCol = lines.length === 1
            ? sel.startColumn + text.length
            : lastLineLen + 1;
        editor.setSelection(new monaco.Range(newLine, newCol, newLine, newCol));
        editor.focus();
    }

    function replaceRange(range, newText) {
        editor.executeEdits('paste-handler', [{
            range,
            text: newText,
            forceMoveMarkers: true,
        }]);
    }

    // ============================================================
    //  Formatting actions (toolbar buttons)
    // ============================================================

    const REGEX_ESCAPE_RE = /[.*+?^${}()|[\]\\]/g;
    function escapeRegex(s) {
        return s.replace(REGEX_ESCAPE_RE, '\\$&');
    }

    /**
     * Wrap every selection (or insert a pair at every cursor) with `before`/`after`.
     * Multi-cursor aware: Cmd+D-style selections all get wrapped in a single edit
     * transaction. After wrapping, the inner text of each non-empty selection
     * becomes selected so the user can immediately type to replace it. Empty
     * cursors get a pair inserted around them and the cursor lands in the middle.
     *
     * Implementation note: edits are applied in a single `executeEdits` call,
     * sorted in DESCENDING order by start position so the range coordinates
     * remain valid as each edit lands. For multi-cursor selections on the same
     * line, the new column of each selection's start is computed as
     * `originalStartCol + cumulativeShift + before.length`, where the cumulative
     * shift is the sum of `(before.length + after.length)` for every other edit
     * on the same line at a column strictly before the current selection's start.
     */
    function wrapSelectionMulti(before, after, placeholder) {
        if (!editor) return;
        const model = editor.getModel();
        const selections = editor.getSelections();
        if (!selections || selections.length === 0) return;

        // Capture each selection's data. Use `getValueInRange` ONCE up front
        // because after the model is mutated by `executeEdits` the original
        // ranges no longer refer to the same text.
        const indexed = selections.map((sel, i) => ({
            sel, i,
            text: sel.isEmpty() ? '' : model.getValueInRange(sel),
        }));

        // Sort DESCENDING by start position. Monaco applies edits in array
        // order, each against the model state as updated by previous edits
        // in the array — so highest-start first keeps later-edit coordinates
        // valid.
        indexed.sort((a, b) => {
            if (a.sel.startLineNumber !== b.sel.startLineNumber) {
                return b.sel.startLineNumber - a.sel.startLineNumber;
            }
            if (a.sel.startColumn !== b.sel.startColumn) {
                return b.sel.startColumn - a.sel.startColumn;
            }
            return 0;
        });

        // Build the edit operations. For a non-empty selection, replace the
        // selected text with `before + selected + after`. For an empty cursor,
        // insert `before + placeholder + after` (defaulting `placeholder` to
        // empty string) and place the cursor in the middle so the user can
        // immediately type to fill in the gap.
        const ph = placeholder || '';
        const edits = indexed.map(({ sel, text }) => {
            if (text.length > 0) {
                return { range: sel, text: before + text + after, forceMoveMarkers: true };
            }
            const pos = sel.getStartPosition();
            return {
                range: new monaco.Range(pos.lineNumber, pos.column, pos.lineNumber, pos.column),
                text: before + ph + after,
                forceMoveMarkers: true,
            };
        });

        editor.executeEdits('fmt', edits);

        // After executeEdits, the model reflects all edits. To position the new
        // selections we need to know where each original selection's "inner
        // text" landed. We can't just read the model around the original
        // positions because multiple edits on the same line can shift columns.
        //
        // Strategy: build a per-line column→shift table. For each line that
        // had at least one selection, list every selection on that line with
        // its start column and the net length change that selection's edit
        // contributed (`before.length + after.length`, same for both empty
        // and non-empty cases — the only thing being added is `before` + `after`).
        // For a given original selection at column C, the cumulative shift of
        // all OTHER selections on the same line with column < C is added to C,
        // then `before.length` is added to land on the inner text start.
        const netChange = before.length + after.length;
        const lineEdits = new Map(); // line -> [{col, idx}]
        for (let idx = 0; idx < indexed.length; idx++) {
            const { sel } = indexed[idx];
            const line = sel.startLineNumber;
            if (!lineEdits.has(line)) lineEdits.set(line, []);
            lineEdits.get(line).push({ col: sel.startColumn, idx });
        }

        const newSelections = indexed.map(({ sel, text }) => {
            const startLine = sel.startLineNumber;
            const startCol = sel.startColumn;
            const sameLine = lineEdits.get(startLine) || [];
            let cumulativeShift = 0;
            for (const e of sameLine) {
                if (e.col < startCol) {
                    cumulativeShift += netChange;
                }
            }
            const newCol = startCol + cumulativeShift + before.length;
            // For non-empty selections the inner text to re-select is the
            // original selected text. For empty cursors it's the placeholder
            // (or zero-length if no placeholder was passed). Either way the
            // selection length is `text.length || ph.length` — but since
            // empty cursors have `text === ''`, just use `text.length` and
            // adjust: an empty cursor with a placeholder should still select
            // the placeholder text.
            const selectLen = text.length > 0 ? text.length : ph.length;
            // Use `monaco.Selection`, not `monaco.Range` — in Monaco 0.45.0,
            // `setSelections` (plural) refuses plain Range objects and throws
            // "Invalid arguments". Selection is the canonical type.
            return new monaco.Selection(
                startLine, newCol,
                startLine, newCol + selectLen
            );
        });

        // Defer the selection update until the next animation frame.
        // Monaco's view layer re-renders the model right after `executeEdits`
        // returns, and in 0.45.0 the renderer occasionally asks for a line
        // number that's mid-update when we set selections synchronously,
        // throwing "Illegal value for lineNumber" from `getLineContent`.
        // Waiting one frame lets the model change settle before we move the
        // caret. (The wrap itself is unaffected — only the caret move is
        // deferred.)
        const applySelections = () => {
            try {
                editor.setSelections(newSelections);
            } catch (e) {
                // If even the deferred set fails, fall back to the primary
                // selection only so the caret lands somewhere sensible.
                if (newSelections.length > 0) {
                    try { editor.setSelection(newSelections[0]); } catch {}
                }
            }
        };
        if (typeof requestAnimationFrame === 'function') {
            requestAnimationFrame(applySelections);
        } else {
            setTimeout(applySelections, 0);
        }
        editor.focus();
    }

    // Backward-compat alias for any code that still calls the old name.
    const wrapSelection = wrapSelectionMulti;

    /**
     * Add or remove a per-line prefix across the current selection. Used for
     * headings, list markers, and block quotes. If every line in the selection
     * already has the prefix, it is removed (toggle off); otherwise it is
     * added to every line that lacks it.
     */
    function toggleLinePrefix(prefix) {
        if (!editor) return;
        const sel = editor.getSelection();
        const model = editor.getModel();
        const startLine = sel.startLineNumber;
        const endLine = sel.endLineNumber;
        const escaped = escapeRegex(prefix);
        const prefixRe = new RegExp('^' + escaped);

        // Heading prefixes (#–#######) behave differently from list/quote
        // prefixes. With a heading click, the user's mental model is
        // "set this line to H<N>" — so clicking H2 on a line that's
        // already H3 should *replace* the H3, not stack to "### ## foo".
        // List/quote prefixes still do plain add. We allow up to 7 hashes
        // so H7 participates in the same replace logic (HTML only renders
        // h1–h6, but markdown processors usually accept deeper levels).
        const isHeading = /^#{1,7} $/.test(prefix);
        const anyHeadingRe = /^#{1,7} ?/;

        let allHave = true;
        for (let ln = startLine; ln <= endLine; ln++) {
            const text = model.getLineContent(ln);
            if (text.trim().length > 0 && !prefixRe.test(text)) {
                allHave = false;
                break;
            }
        }

        const edits = [];
        for (let ln = startLine; ln <= endLine; ln++) {
            const text = model.getLineContent(ln);
            if (text.trim().length === 0) continue;
            if (allHave) {
                // Toggle off: every line already has this exact prefix,
                // strip it. (For headings, this also covers "drop back
                // from H<N> to plain text".)
                const stripped = text.replace(new RegExp('^' + escaped + ' ?'), '');
                edits.push({
                    range: new monaco.Range(ln, 1, ln, text.length + 1),
                    text: stripped,
                    forceMoveMarkers: true,
                });
            } else if (isHeading) {
                // Set the heading level: strip any existing heading prefix
                // (any level), then add the new one. e.g. H3 → H4 changes
                // "### foo" into "#### foo", not "#### ### foo".
                const stripped = text.replace(anyHeadingRe, '');
                edits.push({
                    range: new monaco.Range(ln, 1, ln, text.length + 1),
                    text: prefix + stripped,
                    forceMoveMarkers: true,
                });
            } else {
                // Plain add for list / quote / codeblock prefixes.
                edits.push({
                    range: new monaco.Range(ln, 1, ln, 1),
                    text: prefix,
                    forceMoveMarkers: true,
                });
            }
        }
        if (edits.length === 0) return;
        editor.executeEdits('fmt', edits);
        editor.focus();
    }

    function actionBold()      { wrapSelection('**', '**', 'bold text'); }
    function actionItalic()    { wrapSelection('*', '*', 'italic text'); }
    function actionCode()      { wrapSelection('`', '`', 'code'); }
    function actionCodeBlock() {
        // If the selection spans multiple lines, fence the whole block. Otherwise
        // insert a fenced block with a blank line and place the cursor inside.
        const sel = editor.getSelection();
        const model = editor.getModel();
        const selected = model.getValueInRange(sel);
        if (selected && selected.includes('\n')) {
            wrapSelection('\n```\n', '\n```\n');
        } else if (selected) {
            wrapSelection('```\n', '\n```');
        } else {
            const pos = editor.getPosition();
            const before = pos.column === 1 ? '' : '\n';
            const text = before + '```\n\n```\n';
            editor.executeEdits('fmt', [{
                range: new monaco.Range(pos.lineNumber, pos.column, pos.lineNumber, pos.column),
                text,
                forceMoveMarkers: true,
            }]);
            const newLine = pos.lineNumber + (before ? 1 : 0) + 1;
            editor.setSelection(new monaco.Range(newLine, 1, newLine, 1));
            editor.focus();
        }
    }
    function actionH1() { toggleLinePrefix('# '); }
    function actionH2() { toggleLinePrefix('## '); }
    function actionH3() { toggleLinePrefix('### '); }
    function actionH4() { toggleLinePrefix('#### '); }
    function actionH5() { toggleLinePrefix('##### '); }
    function actionH6() { toggleLinePrefix('###### '); }
    function actionH7() { toggleLinePrefix('####### '); }
    function actionQuoteInline() {
        // Wrap selection in typographic curly double quotes (U+201C, U+201D).
        // Delegates to the multi-cursor aware wrap so Cmd+D selections all
        // get quoted in one transaction.
        wrapSelectionMulti('\u201C', '\u201D');
    }
    function actionUl() { toggleLinePrefix('- '); }
    function actionOl() { toggleLinePrefix('1. '); }
    function actionQuote() { toggleLinePrefix('> '); }

    /**
     * Insert a CommonMark hard line break (`\` followed by a newline) at
     * every cursor / selection. Multi-cursor aware: each cursor gets its own
     * break inserted in a single transaction, sorted descending by start
     * position so all range coordinates stay valid. After the insert,
     * each cursor naturally lands at the start of the line below.
     */
    function actionLineBreak() {
        if (!editor) return;
        const selections = editor.getSelections();
        if (!selections || selections.length === 0) return;

        // Sort descending by start position. Edits applied in this order
        // never invalidate the ranges of subsequent (earlier-position)
        // edits in the array.
        const sorted = [...selections].sort((a, b) => {
            if (a.startLineNumber !== b.startLineNumber) {
                return b.startLineNumber - a.startLineNumber;
            }
            return b.startColumn - a.startColumn;
        });

        const edits = sorted.map(sel => {
            const pos = sel.getStartPosition();
            return {
                range: new monaco.Range(pos.lineNumber, pos.column, pos.lineNumber, pos.column),
                text: '\\\n',
                forceMoveMarkers: true,
            };
        });

        editor.executeEdits('fmt', edits);
        editor.focus();
    }

    function actionLink() {
        if (!editor) return;
        const sel = editor.getSelection();
        const model = editor.getModel();
        const selected = model.getValueInRange(sel);
        const url = window.prompt('Link URL', 'https://');
        if (url === null) return; // cancelled
        const safeUrl = url.trim() || 'https://';
        if (selected && selected.length > 0) {
            const text = '[' + selected + '](' + safeUrl + ')';
            editor.executeEdits('fmt', [{ range: sel, text, forceMoveMarkers: true }]);
            // Select the URL so the user can type over it
            const startCol = sel.startColumn + selected.length + 3; // [text](
            const endCol = startCol + safeUrl.length;
            editor.setSelection(new monaco.Range(sel.startLineNumber, startCol, sel.endLineNumber, endCol));
        } else {
            const pos = editor.getPosition();
            const text = '[' + (window.prompt('Link text', '') || '') + '](' + safeUrl + ')';
            editor.executeEdits('fmt', [{
                range: new monaco.Range(pos.lineNumber, pos.column, pos.lineNumber, pos.column),
                text,
                forceMoveMarkers: true,
            }]);
            const startCol = pos.column + 1;
            const endCol = startCol + (text.length - safeUrl.length - 3); // highlight the link text
            editor.setSelection(new monaco.Range(pos.lineNumber, startCol, pos.lineNumber, endCol));
        }
        editor.focus();
    }

    // Image upload actions (toolbar button) — wrap the existing paste handler.
    async function uploadImageFromClipboard() {
        if (!editor) return;
        setStatus('Reading clipboard…');
        try {
            // navigator.clipboard.read() is the modern API; returns array of
            // ClipboardItems, each with a `types` array. We look for an image.
            if (!navigator.clipboard || !navigator.clipboard.read) {
                throw new Error('Clipboard read not supported in this browser');
            }
            const items = await navigator.clipboard.read();
            let blob = null;
            for (const item of items) {
                const imageType = item.types.find(t => t.startsWith('image/'));
                if (imageType) {
                    blob = await item.getType(imageType);
                    break;
                }
            }
            if (!blob) {
                toast('No image on the clipboard. Copy a screenshot first, or click to pick a file.', 'warning', 3000);
                setStatus('No image on clipboard');
                // Fallback to file picker so the user is never stuck.
                uploadImageFromFile();
                return;
            }
            const ext = (blob.type.split('/')[1] || 'png').replace('jpeg', 'jpg');
            const file = new File([blob], 'clipboard-image.' + ext, { type: blob.type });
            await handleImagePaste(file);
        } catch (err) {
            // Browser may require user gesture or secure context
            setStatus('Clipboard read failed: ' + err.message);
            toast('Clipboard read failed — opening file picker. (' + err.message + ')', 'warning', 3500);
            // Fallback to file picker
            uploadImageFromFile();
        }
    }

    function uploadImageFromFile() {
        if (!editor) return;
        if (els.fileInput) els.fileInput.click();
    }

    async function uploadImageFromChosenFile(file) {
        if (!file || !file.type.startsWith('image/')) {
            toast('Not an image file', 'error', 2400);
            return;
        }
        await handleImagePaste(file);
    }

    function makeUuid() {
        if (window.crypto?.randomUUID) return window.crypto.randomUUID();
        return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
            const r = (Math.random() * 16) | 0;
            const v = c === 'x' ? r : (r & 0x3) | 0x8;
            return v.toString(16);
        });
    }

    // Track in-flight image placeholders so the completion step can find
    // the exact range to replace, even if other uploads / edits have
    // shifted the document in the meantime. Key = uploadId, value =
    // monaco.Range covering the full placeholder (start line/col +
    // end line/col). Line/col coords are robust to text shifts around
    // the placeholder as long as the placeholder itself isn't edited.
    const _pendingImageReplaces = new Map();

    async function handleImagePaste(file) {
        // Insert a placeholder first so the user gets immediate feedback.
        // The placeholder uses the REAL shortcode shape so the live preview
        // shows an obviously-loading figure (broken-image alt text) instead
        // of raw `<?# Figure ... ?>` text. We embed a unique upload-id
        // marker inside `alt` so the completion step can find the
        // placeholder's *exact* start in the document even if other
        // uploads / edits have shifted lines around in the meantime —
        // line/col coords alone get invalidated by interleaved inserts.
        const uploadId = makeUuid();
        const marker = `__up_${uploadId.slice(0, 8)}__`;
        // Length of the literal `<?# Figure src="" alt="` prefix that
        // precedes the marker inside the placeholder. Used to walk
        // backwards from the marker position to the placeholder's start.
        const PLACEHOLDER_PREFIX = `<?# Figure src="" alt="`;
        const placeholderText =
            `<?# Figure src="" alt="${marker} ⏳ uploading…" ?>\n\n` +
            `Fig. ?? — uploading…\n\n` +
            `<?#/ Figure ?>\n`;
        const sel = editor.getSelection();
        // Place the placeholder on its own line if not at line start
        const needsLeadingNewline = sel.startColumn > 1;
        const insertText = (needsLeadingNewline ? '\n' : '') + placeholderText;
        editor.executeEdits('image-paste', [{
            range: new monaco.Range(
                sel.startLineNumber, sel.startColumn,
                sel.startLineNumber, sel.startColumn
            ),
            text: insertText,
            forceMoveMarkers: true,
        }]);
        setStatus('Uploading image…');

        try {
            const fd = new FormData();
            fd.append('file', file, file.name || 'pasted-image.png');
            if (filePath) fd.append('postPath', filePath);
            const r = await fetch('/api/images', { method: 'PUT', body: fd });
            const json = await r.json();
            if (!r.ok) throw new Error(json?.error || 'HTTP ' + r.status);

            // Find the placeholder by its unique marker. The marker is
            // *inside* the placeholder, so we walk back PLACEHOLDER_PREFIX
            // chars to reach the start of the placeholder. This is robust
            // to any number of interleaved edits / inserts elsewhere in
            // the document (the line/col coords from the original insert
            // would be invalidated by those).
            const model = editor.getModel();
            const fullText = model.getValue();
            const markerIdx = fullText.indexOf(marker);
            if (markerIdx === -1) {
                // The user (or our own code) removed the placeholder
                // while the upload was in flight. Bail out.
                setStatus(`Image saved but placeholder was removed: ${json.filename}`);
                toast('Image saved (placeholder gone): ' + json.filename, 'info', 2400);
                return;
            }
            const phStartOffset = markerIdx - PLACEHOLDER_PREFIX.length;
            const startPos = model.getPositionAt(phStartOffset);
            const endPos = model.getPositionAt(phStartOffset + placeholderText.length);
            const placeholderRange = new monaco.Range(
                startPos.lineNumber, startPos.column,
                endPos.lineNumber, endPos.column
            );

            // Count already-completed figures (empty alt) — this
            // excludes in-flight placeholders, which carry a marker
            // and a "⏳ uploading…" alt instead. The new figure is the
            // next one in sequence.
            const realCount = (fullText.match(/<\?#\s*Figure\b[^>]*alt=""/g) || []).length;
            const figCount = realCount + 1;
            const figStr = String(figCount).padStart(2, '0');

            // Final shortcode. `alt=""` is the edit hotspot (cursor lands
            // inside the quotes); caption template is "Fig. NN — " with a
            // trailing space so the user can start typing the description
            // immediately if they prefer editing the caption instead.
            const finalText =
                `<?# Figure src="${json.url}" alt="" ?>\n\n` +
                `Fig. ${figStr} — \n\n` +
                `<?#/ Figure ?>\n`;

            // Position of the cursor inside the empty alt attribute.
            // `altAttrOffset` is the index within `finalText` of the empty
            // string between the two `"` quotes.
            const altAttrOffset = finalText.indexOf('alt=""') + 'alt="'.length;

            replaceRange(placeholderRange, finalText);

            // Place the cursor in the alt attribute. The replacement
            // started at `phStartOffset`, so the absolute offset is
            // `phStartOffset + altAttrOffset`. We re-derive the line/col
            // from the new model state.
            const cursorAbs = phStartOffset + altAttrOffset;
            const cursorPos = model.getPositionAt(cursorAbs);
            editor.setSelection(new monaco.Selection(
                cursorPos.lineNumber, cursorPos.column,
                cursorPos.lineNumber, cursorPos.column
            ));
            editor.revealPositionInCenter(cursorPos);
            editor.focus();

            setStatus(`Image saved: ${json.filename} (${json.width}×${json.height}, ${formatBytes(json.sizeBytes)})`);
            toast('Image uploaded: ' + json.filename, 'success', 2200);
        } catch (err) {
            // On failure, find the placeholder by its marker and replace
            // it with a visible-error shortcode so the user sees what
            // went wrong. If the placeholder is already gone, just toast.
            const model = editor.getModel();
            const fullText = model.getValue();
            const markerIdx = fullText.indexOf(marker);
            if (markerIdx !== -1) {
                const phStartOffset = markerIdx - PLACEHOLDER_PREFIX.length;
                const startPos = model.getPositionAt(phStartOffset);
                const endPos = model.getPositionAt(phStartOffset + placeholderText.length);
                const errorText =
                    `<?# Figure src="" alt="❌ upload failed: ${err.message.replace(/"/g, "'")}" ?>\n\n` +
                    `Fig. ?? — upload failed\n\n` +
                    `<?#/ Figure ?>\n`;
                replaceRange(new monaco.Range(
                    startPos.lineNumber, startPos.column,
                    endPos.lineNumber, endPos.column
                ), errorText);
            }
            setStatus('Image upload failed: ' + err.message);
            toast('Upload failed: ' + err.message, 'error', 4000);
        }
    }

    function formatBytes(n) {
        if (n < 1024) return n + ' B';
        if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
        return (n / 1024 / 1024).toFixed(2) + ' MB';
    }

    function handleHtmlPaste(html) {
        const turndown = getTurndown();
        if (!turndown) {
            // No Turndown — fall back to inserting the HTML as-is.
            insertAtCursor(html);
            return;
        }
        setStatus('Converting HTML → markdown…');
        try {
            let md = turndown.turndown(html);
            // Tidy: collapse 3+ blank lines down to 2; trim trailing whitespace per line.
            md = md.replace(/\n{3,}/g, '\n\n').replace(/[ \t]+\n/g, '\n').trim();
            insertAtCursor(md + '\n');
            setStatus('Inserted ' + md.length + ' chars of markdown from HTML');
            toast('HTML converted to markdown', 'success', 1500);
        } catch (err) {
            setStatus('HTML conversion failed: ' + err.message);
            toast('Conversion failed: ' + err.message, 'error', 3000);
        }
    }

    function onPaste(e) {
        if (!editor) return;
        const cd = e.clipboardData;
        if (!cd) return;

        // 1) image on the clipboard
        const imageItem = Array.from(cd.items || []).find(i => i.type.startsWith('image/'));
        if (imageItem) {
            const file = imageItem.getAsFile();
            if (file && file.size > 0) {
                e.preventDefault();
                e.stopPropagation();
                handleImagePaste(file);
                return;
            }
        }

        // 2) HTML — convert to markdown
        const html = cd.getData('text/html');
        const text = cd.getData('text/plain');
        // Only convert when there's a meaningful amount of HTML structure; the
        // plain-text path is left to Monaco for tiny things like a link from
        // a notes app.
        if (html && html.length > 100 && /<[a-z][\s\S]*>/i.test(html)) {
            e.preventDefault();
            e.stopPropagation();
            handleHtmlPaste(html);
            return;
        }

        // 3) plain text — Monaco handles it itself
        void text;
    }

    // ---------- monaco ----------
    function mountMonaco(initialText) {
        return new Promise((resolve, reject) => {
            const onReady = () => {
                try {
                    editor = monaco.editor.create(document.getElementById('monaco-container'), {
                        value: initialText,
                        language: 'markdown',
                        theme: 'vs',
                        automaticLayout: true,
                        fontSize: 14,
                        fontFamily: '"JetBrains Mono", "SF Mono", Menlo, monospace',
                        lineHeight: 22,
                        wordWrap: 'on',
                        // 'deepIndent' visually indents wrapped lines (looks like
                        // a paragraph break to the eye). 'none' makes wrap
                        // lines flush-left with the line start, so a wrapped
                        // sentence looks like one continuous paragraph instead
                        // of a separate block.
                        wrappingIndent: 'none',
                        minimap: { enabled: false },
                        scrollBeyondLastLine: false,
                        smoothScrolling: true,
                        cursorBlinking: 'smooth',
                        renderLineHighlight: 'all',
                        lineNumbersMinChars: 3,
                        rulers: [100],
                        tabSize: 2,
                        insertSpaces: true,
                        'aria-label': filePath || 'markdown post',
                        padding: { top: 16, bottom: 16 },
                    });
                    editor.onDidChangeModelContent(refreshDirty);
                    editor.onDidChangeCursorPosition(updateStats);
                    // Monaco-level key handler. Runs *before* Monaco inserts the
                    // typed character, so it's the only place that can intercept
                    // a plain ` (backtick) when the user has a selection and
                    // wants to wrap the selection in inline-code backticks.
                    editor.onKeyDown(onEditorKeyDown);
                    window.addEventListener('beforeunload', beforeUnload);
                    window.addEventListener('keydown', onKeyDown, true);
                    window.addEventListener('keydown', onPasteKey, true);
                    // Listen for paste on the container with capture so we beat Monaco.
                    const host = document.getElementById('monaco-container');
                    host.addEventListener('paste', onPaste, true);
                    // Debounced spell + grammar check on every model change
                    let previewTimer = null;
                    editor.onDidChangeModelContent(() => {
                        if (_spellCheckEnabled) {
                            if (_spellTimer) clearTimeout(_spellTimer);
                            _spellTimer = setTimeout(runSpellAndGrammarCheck, SPELL_CHECK_DEBOUNCE_MS);
                        }
                        if (_ltEnabled) {
                            if (_ltTimer) clearTimeout(_ltTimer);
                            _ltTimer = setTimeout(runLanguageToolCheck, LT_CHECK_DEBOUNCE_MS);
                        }
                        if (previewTimer) clearTimeout(previewTimer);
                        previewTimer = setTimeout(renderPreview, 200);
                    });
                    registerSpellProviders();
                    registerLtCodeActionProvider();
                    // Only kick off the dictionary load when the user wants
                    // spell check on. Skipping the ~600 KB download matters
                    // on big posts where the user has turned the check off.
                    if (_spellCheckEnabled) initSpellChecker();
                    // LanguageTool — no dictionary download needed, just a
                    // single HTTP call. Kick it off if the user wants it.
                    if (_ltEnabled) runLanguageToolCheck();
                    els.loading.hidden = true;
                    editor.focus();
                    resolve();
                } catch (e) {
                    reject(e);
                }
            };

            if (window.require && window.monaco) {
                onReady();
            } else {
                const loaderScript = document.querySelector('script[src*="monaco-editor"][src$="loader.js"]');
                if (!loaderScript) {
                    reject(new Error('Monaco loader script missing'));
                    return;
                }
                window.require.config({ paths: { vs: 'https://cdn.jsdelivr.net/npm/monaco-editor@0.45.0/min/vs' } });
                window.require(['vs/editor/editor.main'], onReady, reject);
            }
        });
    }

    // ============================================================
    //  Spell + grammar checking
    // ============================================================
    const SPELL_CHECK_DEBOUNCE_MS = 600;
    const SPELL_CHECK_PREF_KEY = 'sme.spellCheckEnabled';
    let _spellchecker = null;
    let _spellcheckerLoading = false;
    let _lastIssueCount = 0;
    let _spellTimer = null;
    // Spell + grammar check is on by default. Toggleable from the status
    // bar — long posts can make the nspell pass sluggish, so power users
    // turn it off. Persisted to localStorage.
    let _spellCheckEnabled = readSpellCheckPref();

    function readSpellCheckPref() {
        try {
            const v = localStorage.getItem(SPELL_CHECK_PREF_KEY);
            // Default ON when nothing is stored (first visit).
            return v === null ? true : v === '1';
        } catch { return true; }
    }
    function writeSpellCheckPref(on) {
        try { localStorage.setItem(SPELL_CHECK_PREF_KEY, on ? '1' : '0'); } catch {}
    }

    function updateSpellToggleUi() {
        const btn = els.spellToggle;
        const label = els.spellToggleLabel;
        if (!btn || !label) return;
        if (_spellCheckEnabled) {
            btn.classList.remove('spell-off');
            btn.classList.add('spell-on');
            btn.setAttribute('aria-pressed', 'true');
            label.textContent = 'Spell: on';
        } else {
            btn.classList.remove('spell-on');
            btn.classList.add('spell-off');
            btn.setAttribute('aria-pressed', 'false');
            label.textContent = 'Spell: off';
        }
    }

    // ============================================================
    //  Marker meta store
    //
    //  Monaco's `setModelMarkers` strips any non-standard fields from
    //  the marker (e.g. our `_meta`), so we can't get them back via
    //  `getModelMarkers`. To let the code-action / hover providers
    //  know the suggestions / message behind each squiggle, we keep
    //  a parallel Map keyed by (model, owner, position).
    //
    //  `setMarkersWithMeta` and `clearMarkersWithMeta` are the only
    //  sanctioned ways to mutate Monaco markers in this file.
    // ============================================================
    const _markersMeta = new Map();
    function metaKey(model, owner, startLine, startCol, endLine, endCol) {
        return `${model.id}|${owner}|${startLine}:${startCol}-${endLine}:${endCol}`;
    }
    function setMarkersWithMeta(model, owner, issues, buildMarker) {
        // `buildMarker(issue)` returns the Monaco marker shape; the
        // helper stashes the issue itself in the meta map.
        const markers = [];
        for (const issue of issues) {
            const mk = buildMarker(issue);
            markers.push(mk);
            if (model && mk && issue) {
                _markersMeta.set(metaKey(model, owner, mk.startLineNumber, mk.startColumn, mk.endLineNumber, mk.endColumn), issue);
            }
        }
        monaco.editor.setModelMarkers(model, owner, markers);
    }
    function clearMarkersWithMeta(model, owner) {
        if (!model) return;
        // Drop every entry whose key starts with `<modelId>|<owner>|`
        const prefix = `${model.id}|${owner}|`;
        for (const k of Array.from(_markersMeta.keys())) {
            if (k.startsWith(prefix)) _markersMeta.delete(k);
        }
        monaco.editor.setModelMarkers(model, owner, []);
    }
    function getMarkerMeta(model, owner, marker) {
        if (!model || !marker) return null;
        return _markersMeta.get(metaKey(model, owner, marker.startLineNumber, marker.startColumn, marker.endLineNumber, marker.endColumn)) || null;
    }

    function clearSpellMarkers() {
        if (!editor) return;
        clearMarkersWithMeta(editor.getModel(), 'spellcheck');
    }

    function setSpellCheckEnabled(enabled) {
        if (_spellCheckEnabled === enabled) return;
        _spellCheckEnabled = enabled;
        writeSpellCheckPref(enabled);
        updateSpellToggleUi();
        if (!enabled) {
            // Stop any pending check and clear existing squiggles
            if (_spellTimer) { clearTimeout(_spellTimer); _spellTimer = null; }
            clearSpellMarkers();
            // Hide the "Issues: N" / "Spell check: 0 issues" status — it's
            // misleading when the check is off.
            if (els.statusRight) {
                els.statusRight.textContent = 'Spell off';
            }
        } else {
            // Re-enable: run a fresh check (and load the dictionary if not yet)
            if (!_spellchecker && !_spellcheckerLoading) {
                initSpellChecker();
            } else {
                runSpellAndGrammarCheck();
            }
        }
    }

    // ============================================================
    //  LanguageTool online grammar / spelling / style check
    //
    //  Separate from the offline nspell check above. The user can
    //  toggle them independently. We keep their markers in separate
    //  owners ('spellcheck' vs 'languagetool') so clearing one
    //  doesn't wipe the other.
    // ============================================================
    const LT_CHECK_DEBOUNCE_MS = 1500;     // longer than nspell (LT is online)
    const LT_PREF_KEY = 'sme.grammarCheckEnabled';
    let _ltEnabled = readLtPref();
    let _ltTimer = null;
    let _ltInFlight = false;     // tracks a request currently awaiting the API
    let _ltSeq = 0;              // monotonically increasing; used to discard stale responses

    function readLtPref() {
        try {
            const v = localStorage.getItem(LT_PREF_KEY);
            return v === null ? true : v === '1';
        } catch { return true; }
    }
    function writeLtPref(on) {
        try { localStorage.setItem(LT_PREF_KEY, on ? '1' : '0'); } catch {}
    }

    function updateLtToggleUi() {
        const btn = els.ltToggle;
        const label = els.ltToggleLabel;
        if (!btn || !label) return;
        if (_ltEnabled) {
            btn.classList.remove('lt-off', 'lt-checking');
            btn.classList.add('lt-on');
            btn.setAttribute('aria-pressed', 'true');
            label.textContent = 'Grammar: on';
        } else {
            btn.classList.remove('lt-on', 'lt-checking');
            btn.classList.add('lt-off');
            btn.setAttribute('aria-pressed', 'false');
            label.textContent = 'Grammar: off';
        }
    }

    function clearLtMarkers() {
        if (!editor) return;
        clearMarkersWithMeta(editor.getModel(), 'languagetool');
    }

    function setLtCheckingUi(checking) {
        if (!els.ltToggle) return;
        if (checking && _ltEnabled) els.ltToggle.classList.add('lt-checking');
        else els.ltToggle.classList.remove('lt-checking');
    }

    async function runLanguageToolCheck() {
        if (!editor) return;
        if (!_ltEnabled) {
            clearLtMarkers();
            return;
        }
        const model = editor.getModel();
        if (!model) return;
        const text = model.getValue();
        if (!text || text.trim().length < 2) {
            clearLtMarkers();
            return;
        }

        // Bump the sequence so any in-flight response from a previous
        // call is dropped — we only want the latest.
        const seq = ++_ltSeq;
        _ltInFlight = true;
        setLtCheckingUi(true);

        try {
            const issues = await window.LanguageTool.checkText(text);
            if (seq !== _ltSeq) return; // stale, ignore
            setMarkersWithMeta(model, 'languagetool', issues || [], issue => {
                const startPos = model.getPositionAt(Math.min(issue.start, model.getValueLength()));
                const endPos = model.getPositionAt(Math.min(Math.max(issue.end, issue.start + 1), model.getValueLength()));
                return {
                    startLineNumber: startPos.lineNumber,
                    startColumn: startPos.column,
                    endLineNumber: endPos.lineNumber,
                    endColumn: endPos.column,
                    message: issue.message || (issue.category || 'Suggestion'),
                    severity: issueSeverityToMonaco(issue.severity),
                    source: 'LanguageTool',
                    code: 'lt',
                };
            });
            // Update status right
            const ltCount = (issues || []).length;
            if (els.statusRight) {
                if (ltCount > 0) {
                    els.statusRight.textContent = `LT: ${ltCount} issue${ltCount === 1 ? '' : 's'}`;
                } else {
                    els.statusRight.textContent = 'LanguageTool: 0 issues';
                }
            }
        } catch (err) {
            if (seq !== _ltSeq) return;
            console.warn('[lt] check failed:', err);
        } finally {
            if (seq === _ltSeq) {
                _ltInFlight = false;
                setLtCheckingUi(false);
            }
        }
    }

    function setLtEnabled(enabled) {
        if (_ltEnabled === enabled) return;
        _ltEnabled = enabled;
        writeLtPref(enabled);
        updateLtToggleUi();
        if (!enabled) {
            if (_ltTimer) { clearTimeout(_ltTimer); _ltTimer = null; }
            _ltSeq++; // invalidate any in-flight response
            clearLtMarkers();
            if (els.statusRight && els.statusRight.textContent.startsWith('LT:')) {
                els.statusRight.textContent = 'Grammar off';
            }
        } else {
            // Re-enable: run a fresh check (no extra setup — LT is just an HTTP call)
            runLanguageToolCheck();
        }
    }

    function issueSeverityToMonaco(sev) {
        // 'info' | 'warning' | 'error' — Monaco MarkerSeverity
        // 1 Hint, 2 Info, 4 Warning, 8 Error
        if (sev === 'error') return monaco.MarkerSeverity.Error;
        if (sev === 'warning') return monaco.MarkerSeverity.Warning;
        return monaco.MarkerSeverity.Info;
    }

    function runSpellAndGrammarCheck() {
        if (!editor) return;
        if (!_spellCheckEnabled) {
            // Defensive: if the toggle is off, never run. The caller is
            // supposed to gate on `_spellCheckEnabled` already, but if a
            // check was scheduled and the user toggled off before the
            // debounce fired, this short-circuits.
            clearSpellMarkers();
            return;
        }
        const model = editor.getModel();
        if (!model) return;
        const text = model.getValue();

        // Grammar check is synchronous and always available
        const grammarIssues = (window.GrammarCheck?.checkGrammar(text)) || [];

        // Spell check needs the dictionary to be loaded
        const spellIssues = _spellchecker
            ? (window.SpellCheck?.checkText(_spellchecker, text) || [])
            : [];

        const all = [...spellIssues, ...grammarIssues];
        _lastIssueCount = all.length;
        // Use the helper that also stashes each issue in the meta map, so
        // hover + code-action providers can read suggestions back.
        setMarkersWithMeta(model, 'spellcheck', all, buildMonacoMarkerFromIssue);

        // Debug — print to console so you can verify in DevTools
        console.debug(`[spellcheck] text=${text.length}c, spell=${spellIssues.length}, grammar=${grammarIssues.length}, total=${all.length}`);

        // Update status bar
        const statusEl = els.statusRight;
        if (statusEl) {
            if (all.length === 0) {
                statusEl.textContent = _spellchecker
                    ? 'Spell check: 0 issues'
                    : 'Spell check: loading…';
            } else {
                statusEl.textContent = `Issues: ${all.length}`;
            }
        }
    }

    function buildMonacoMarkerFromIssue(issue) {
        const model = editor && editor.getModel();
        if (!model) return null;
        const startPos = model.getPositionAt(Math.min(issue.start, model.getValueLength()));
        const endPos = model.getPositionAt(Math.min(Math.max(issue.end, issue.start + 1), model.getValueLength()));
        return {
            startLineNumber: startPos.lineNumber,
            startColumn: startPos.column,
            endLineNumber: endPos.lineNumber,
            endColumn: endPos.column,
            message: issue.message || `Misspelled: "${issue.word}"`,
            severity: issueSeverityToMonaco(issue.severity),
            source: issue.kind === 'spell' ? 'spell' : (issue.rule || 'grammar'),
            code: issue.kind,
        };
    }

    function registerSpellProviders() {
        if (!window.monaco) return;
        const langs = ['markdown', 'plaintext'];

        // Hover: show suggestions
        for (const lang of langs) {
            monaco.languages.registerHoverProvider(lang, {
                provideHover: (model, position) => {
                    const markers = monaco.editor.getModelMarkers({ resource: model.uri, owner: 'spellcheck' });
                    const hit = markers.find(mk =>
                        mk.startLineNumber === position.lineNumber
                        && position.column >= mk.startColumn
                        && position.column <= mk.endColumn);
                    if (!hit) return null;
                    const meta = getMarkerMeta(model, 'spellcheck', hit);
                    if (!meta) return null;
                    let body = meta.message || '';
                    if (meta.word) body = `**${meta.word}**\n\n${body}`;
                    if (meta.suggestions && meta.suggestions.length > 0) {
                        body += '\n\n**Suggestions:**\n';
                        meta.suggestions.forEach(s => { body += `- ${s}\n`; });
                    }
                    return {
                        range: new monaco.Range(
                            hit.startLineNumber, hit.startColumn,
                            hit.endLineNumber, hit.endColumn,
                        ),
                        contents: [{ value: body }],
                    };
                },
            });

            // Code action: quick fix (replace with first suggestion)
            monaco.languages.registerCodeActionProvider(lang, {
                provideCodeActions: (model, range, ctx) => {
                    const actions = [];
                    const markers = monaco.editor.getModelMarkers({ resource: model.uri, owner: 'spellcheck' });
                    for (const mk of markers) {
                        const meta = getMarkerMeta(model, 'spellcheck', mk);
                        if (!meta || !meta.suggestions || meta.suggestions.length === 0) continue;
                        // Only act on markers intersecting the requested range or all markers if range is empty
                        const intersects = range.startLineNumber > mk.endLineNumber
                            || range.endLineNumber < mk.startLineNumber
                            || (range.startLineNumber === mk.endLineNumber && range.startColumn > mk.endColumn);
                        if (range.startLineNumber > 0 && intersects) continue;

                        meta.suggestions.forEach((sug, i) => {
                            actions.push({
                                title: i === 0
                                    ? `Replace with "${sug}"`
                                    : `Replace with "${sug}"`,
                                kind: 'quickfix',
                                edit: {
                                    edits: [{
                                        resource: model.uri,
                                        edit: {
                                            range: new monaco.Range(
                                                mk.startLineNumber, mk.startColumn,
                                                mk.endLineNumber, mk.endColumn,
                                            ),
                                            text: sug,
                                        },
                                    }],
                                },
                                isPreferred: i === 0,
                            });
                        });

                        // Also offer "Ignore" for the spelling issues (per-instance)
                        if (meta.kind === 'spell' && window.SpellCheck) {
                            actions.push({
                                title: 'Ignore this word',
                                kind: 'quickfix',
                                edit: { edits: [] },
                                command: { id: 'sme.ignore-word', title: 'Ignore', arguments: [meta.word] },
                            });
                        }
                    }
                    return { actions, dispose: () => {} };
                },
            });
        }
    }

    function registerLtCodeActionProvider() {
        if (!window.monaco) return;
        const langs = ['markdown', 'plaintext'];
        for (const lang of langs) {
            // Hover: show the full LanguageTool message + suggestions + rule link
            monaco.languages.registerHoverProvider(lang, {
                provideHover: (model, position) => {
                    const markers = monaco.editor.getModelMarkers({ resource: model.uri, owner: 'languagetool' });
                    const hit = markers.find(mk =>
                        mk.startLineNumber === position.lineNumber
                        && position.column >= mk.startColumn
                        && position.column <= mk.endColumn);
                    if (!hit) return null;
                    const meta = getMarkerMeta(model, 'languagetool', hit);
                    if (!meta) return null;
                    let body = meta.message || meta.shortMessage || 'LanguageTool suggestion';
                    if (meta.category) body += `\n\n*${meta.category}*`;
                    if (meta.suggestions && meta.suggestions.length > 0) {
                        body += '\n\n**Suggestions:**\n';
                        meta.suggestions.forEach(s => { body += `- ${s}\n`; });
                    }
                    if (meta.url) body += `\n[More info](${meta.url})`;
                    return {
                        range: new monaco.Range(
                            hit.startLineNumber, hit.startColumn,
                            hit.endLineNumber, hit.endColumn,
                        ),
                        contents: [{ value: body }],
                    };
                },
            });

            // Code action: quick-fix "Replace with …" for each LT suggestion
            monaco.languages.registerCodeActionProvider(lang, {
                provideCodeActions: (model, range, ctx) => {
                    const actions = [];
                    const markers = monaco.editor.getModelMarkers({ resource: model.uri, owner: 'languagetool' });
                    for (const mk of markers) {
                        const meta = getMarkerMeta(model, 'languagetool', mk);
                        if (!meta || !meta.suggestions || meta.suggestions.length === 0) continue;
                        const intersects = range.startLineNumber > mk.endLineNumber
                            || range.endLineNumber < mk.startLineNumber
                            || (range.startLineNumber === mk.endLineNumber && range.startColumn > mk.endColumn);
                        if (range.startLineNumber > 0 && intersects) continue;

                        meta.suggestions.forEach((sug, i) => {
                            actions.push({
                                title: i === 0
                                    ? `LT: Replace with "${sug}"`
                                    : `LT: Replace with "${sug}"`,
                                kind: 'quickfix',
                                edit: {
                                    edits: [{
                                        resource: model.uri,
                                        edit: {
                                            range: new monaco.Range(
                                                mk.startLineNumber, mk.startColumn,
                                                mk.endLineNumber, mk.endColumn,
                                            ),
                                            text: sug,
                                        },
                                    }],
                                },
                                isPreferred: i === 0,
                            });
                        });
                    }
                    return { actions, dispose: () => {} };
                },
            });
        }
    }

    async function initSpellChecker() {
        if (_spellchecker || _spellcheckerLoading) return;
        _spellcheckerLoading = true;
        try {
            _spellchecker = await window.SpellCheck.loadSpellcheck((msg) => {
                setStatus(msg);
            });
            runSpellAndGrammarCheck();
        } catch (err) {
            console.error('Spell checker init failed:', err);
            setStatus('Spell checker unavailable: ' + err.message);
        } finally {
            _spellcheckerLoading = false;
        }
    }

    // ============================================================
    //  Auto-save
    // ============================================================
    // Saves the post every AUTO_SAVE_INTERVAL_MS if there are unsaved
    // changes. Stays out of the way: no toast, just a one-line status
    // flash; the dot pulses briefly so the user can see it fired. State
    // persists in localStorage so the toggle survives reloads. Default
    // ON — the whole point of this feature is "don't lose work".
    const AUTO_SAVE_INTERVAL_MS = 60 * 1000;     // 1 minute
    const AUTO_SAVE_PREF_KEY = 'sme.autoSaveEnabled';
    let _autoSaveEnabled = readAutoSavePref();
    let _autoSaveTimer = null;

    function readAutoSavePref() {
        try {
            const v = localStorage.getItem(AUTO_SAVE_PREF_KEY);
            // Default ON for first visit (the whole point is to never lose work).
            return v === null ? true : v === '1';
        } catch { return true; }
    }
    function writeAutoSavePref(on) {
        try { localStorage.setItem(AUTO_SAVE_PREF_KEY, on ? '1' : '0'); } catch {}
    }

    function updateAutoSaveToggleUi() {
        const btn = els.autoSaveToggle;
        const label = els.autoSaveToggleLabel;
        if (!btn || !label) return;
        if (_autoSaveEnabled) {
            btn.classList.remove('auto-save-off');
            btn.classList.add('auto-save-on');
            label.textContent = 'Auto-save: on';
            btn.setAttribute('aria-pressed', 'true');
        } else {
            btn.classList.remove('auto-save-on');
            btn.classList.add('auto-save-off');
            label.textContent = 'Auto-save: off';
            btn.setAttribute('aria-pressed', 'false');
        }
    }

    function setAutoSaveEnabled(enabled) {
        if (_autoSaveEnabled === enabled) return;
        _autoSaveEnabled = enabled;
        writeAutoSavePref(enabled);
        updateAutoSaveToggleUi();
        if (enabled) {
            startAutoSaveTimer();
        } else {
            stopAutoSaveTimer();
        }
    }

    function startAutoSaveTimer() {
        if (_autoSaveTimer) clearInterval(_autoSaveTimer);
        _autoSaveTimer = setInterval(autoSaveTick, AUTO_SAVE_INTERVAL_MS);
    }
    function stopAutoSaveTimer() {
        if (_autoSaveTimer) { clearInterval(_autoSaveTimer); _autoSaveTimer = null; }
    }

    async function autoSaveTick() {
        // Bail out if not ready, no dirty content, or a save is already in flight.
        if (!editor || !filePath) return;
        if (saving) return;                       // a manual save is running
        if (!isDirty()) return;                    // nothing changed
        // Visual ping so the user sees the dot flicker.
        if (els.autoSaveToggle) {
            els.autoSaveToggle.classList.add('auto-save-saving');
            setTimeout(() => els.autoSaveToggle?.classList.remove('auto-save-saving'), 500);
        }
        // Briefly flash the right-hand status with a "auto-saved" marker.
        const prev = els.statusRight.textContent;
        try {
            await save();  // reuse the existing save() — it shows a toast + sets 'Saved …' status on success
            setStatus(undefined, (els.statusRight.textContent || '') + ' · auto');
        } catch (err) {
            // Save() already sets a status on failure. Keep silent on auto-save errors
            // to avoid spamming the user — the next tick will retry.
            console.warn('[auto-save] tick failed:', err);
        }
    }


    const VIEW_MODES = ['edit', 'split', 'preview'];
    let currentViewMode = 'split';

    const els2 = {
        editorHost: document.getElementById('editor-host'),
        previewContent: document.getElementById('preview-content'),
        previewStatus: document.getElementById('preview-status'),
        viewBtns: {
            edit: document.getElementById('view-edit-btn'),
            split: document.getElementById('view-split-btn'),
            preview: document.getElementById('view-preview-btn'),
        },
    };

    function setViewMode(mode) {
        if (!VIEW_MODES.includes(mode)) return;
        currentViewMode = mode;
        // Update host class
        const host = els2.editorHost;
        host.classList.remove('view-mode-edit', 'view-mode-split', 'view-mode-preview');
        host.classList.add('view-mode-' + mode);
        // Update button pressed state
        for (const m of VIEW_MODES) {
            const btn = els2.viewBtns[m];
            if (btn) btn.setAttribute('aria-pressed', m === mode ? 'true' : 'false');
        }
        // Update status-bar badge so the current mode is always visible
        const badge = document.getElementById('mode-badge');
        if (badge) badge.textContent = 'View: ' + mode;
        // Tell Monaco to relayout (its size changed)
        if (editor) {
            requestAnimationFrame(() => editor.layout());
        }
    }

    function cycleViewMode() {
        const idx = VIEW_MODES.indexOf(currentViewMode);
        const next = VIEW_MODES[(idx + 1) % VIEW_MODES.length];
        setViewMode(next);
    }

    function stripFrontmatter(text) {
        // YAML frontmatter sits at the very top of the file between two `---`
        // fences. It's metadata, not body content — the editor still shows it
        // (so the user can edit it), but the preview shouldn't render it.
        if (!text || !text.startsWith('---')) return text;
        // Match the opening fence, the YAML body, the closing fence (with
        // optional trailing spaces / tabs), and the blank line(s) that
        // typically follow. Lazy `*?` so a `---` inside the body is not
        // mistaken for the closing fence.
        const m = text.match(/^---\r?\n[\s\S]*?\r?\n---[ \t]*(?:\r?\n+|$)/);
        return m ? text.slice(m[0].length) : text;
    }

    function renderPreview() {
        if (!editor) return;
        const rawText = editor.getValue();
        const target = els2.previewContent;
        if (!target) {
            console.warn('[preview] #preview-content not found in DOM');
            return;
        }
        // Strip YAML frontmatter — it is metadata, not article content.
        const text = stripFrontmatter(rawText);
        if (!text || text.trim().length === 0) {
            target.innerHTML = '<p class="empty">Preview will appear here…</p>';
            return;
        }
        try {
            // Configure marked (safe to call on every render)
            if (window.marked && typeof window.marked.setOptions === 'function') {
                window.marked.setOptions({
                    gfm: true,
                    breaks: false,
                    headerIds: false,
                    mangle: false,
                });
            }
            // Render markdown → HTML
            let rawHtml;
            if (window.marked && typeof window.marked.parse === 'function') {
                rawHtml = window.marked.parse(text);
                if (typeof rawHtml !== 'string') {
                    throw new Error(`marked.parse returned ${typeof rawHtml} (expected string)`);
                }
            } else {
                // marked.js never loaded — most likely a script-order issue
                // (AMD collision with Monaco's loader). Surface a real error
                // so the status bar reflects reality instead of pretending
                // success.
                const hint = (window.define && window.define.amd)
                    ? ' (AMD `define` is set — monaco loader probably loaded first; check script order)'
                    : '';
                throw new Error(`marked.js not loaded${hint}`);
            }
            // Sanitize if DOMPurify is available, else trust the source.
            let safeHtml = rawHtml;
            if (window.DOMPurify && typeof window.DOMPurify.sanitize === 'function') {
                try {
                    safeHtml = window.DOMPurify.sanitize(rawHtml, { ADD_ATTR: ['target'] });
                } catch (e) {
                    console.warn('[preview] DOMPurify failed, using raw html', e);
                    safeHtml = rawHtml;
                }
            }
            target.innerHTML = safeHtml;
            console.debug(`[preview] ${text.length}c (raw ${rawText.length}c) → ${rawHtml.length}h bytes (marked=${!!window.marked}, dompurify=${!!window.DOMPurify})`);
            if (els2.previewStatus) {
                els2.previewStatus.className = 'preview-status ok';
                els2.previewStatus.textContent = `rendered ${text.length} chars · marked v12`;
            }
            // External links open in a new tab
            target.querySelectorAll('a[href^="http"]').forEach(a => {
                a.setAttribute('target', '_blank');
                a.setAttribute('rel', 'noopener noreferrer');
            });
        } catch (err) {
            console.error('[preview] failed:', err);
            target.innerHTML = `<p class="empty">Preview error: ${escapeHtmlPreview(err.message)}</p>`;
            if (els2.previewStatus) {
                els2.previewStatus.className = 'preview-status error';
                els2.previewStatus.textContent = `error: ${err.message}`;
            }
        }
    }

    function escapeHtmlPreview(s) {
        return String(s).replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c]));
    }

    function beforeUnload(e) {
        if (isDirty()) {
            e.preventDefault();
            e.returnValue = '';
        }
    }

    // ============================================================
    //  Paste interception at the window level
    //
    //  Monaco does not respond to the browser's native `paste` event. It
    //  intercepts Cmd+V at keydown time and calls its own
    //  `BrowserClipboardService.doPaste()`, which reads the clipboard and
    //  inserts text/html into the model. This means any `addEventListener(
    //  'paste', ...)` on a DOM node never fires for paste operations inside
    //  Monaco.
    //
    //  We handle the entire paste ourselves in the document capture phase:
    //    - Read navigator.clipboard once.
    //    - If an image is present, upload it and insert markdown.
    //    - Otherwise insert the text via Monaco's executeEdits API.
    //  This avoids relying on `editor.trigger('keyboard', 'paste', null)`
    //  (which can silently no-op) and avoids `e.stopPropagation()` (which
    //  was eating the event before Monaco's own keydown handler could run).
    // ============================================================
    async function onPasteKey(e) {
        if (e.key.toLowerCase() !== 'v') return;
        if (!(e.metaKey || e.ctrlKey)) return;
        if (!editor) return;
        if (editor.hasTextFocus && !editor.hasTextFocus()) return;

        e.preventDefault();

        try {
            // Try reading the clipboard. This requires user-gesture
            // (Cmd+V qualifies) and either a secure context or granted
            // permission. The .read() form gives us access to images;
            // .readText() gives us text only.
            if (navigator.clipboard && navigator.clipboard.read) {
                const items = await navigator.clipboard.read();
                for (const item of items) {
                    const imgType = item.types.find(t => t.startsWith('image/'));
                    if (imgType) {
                        const blob = await item.getType(imgType);
                        const ext = (blob.type.split('/')[1] || 'png').replace('jpeg', 'jpg');
                        const file = new File([blob], 'pasted.' + ext, { type: blob.type });
                        await handleImagePaste(file);
                        return;
                    }
                }
            }
            // No image found — read text and insert via Monaco API.
            const text = await navigator.clipboard.readText();
            if (text) {
                const sel = editor.getSelection();
                editor.executeEdits('paste', [{ range: sel, text, forceMoveMarkers: true }]);
            } else {
                toast('Clipboard is empty', 'info', 1500);
            }
        } catch (err) {
            // Either clipboard read failed (no permission) or the browser
            // blocked the call. We fall back to Monaco's built-in paste
            // action so the user doesn't lose their copy.
            console.warn('[paste] clipboard read failed, falling back', err);
            try {
                const action = editor.getAction('editor.action.clipboardPasteAction');
                if (action) {
                    await action.run();
                    return;
                }
            } catch {}
            toast('Paste failed: ' + (err.message || 'unknown'), 'error', 3000);
        }
    }

    function onKeyDown(e) {
        const mod = e.metaKey || e.ctrlKey;
        if (!mod) return;
        const key = e.key.toLowerCase();
        if (key === 's' && !e.shiftKey) {
            e.preventDefault();
            save();
        } else if (key === 'b' && !e.shiftKey) {
            e.preventDefault();
            actionBold();
        } else if (key === 'i' && !e.shiftKey) {
            e.preventDefault();
            actionItalic();
        } else if (key === 'k' && !e.shiftKey) {
            e.preventDefault();
            actionLink();
        } else if (key === 'e' && !e.shiftKey) {
            // Cmd+E — inline code (alt to typing `` around a selection)
            e.preventDefault();
            actionCode();
        } else if (key === 'e' && e.shiftKey) {
            // Cmd+Shift+E — fenced code block
            e.preventDefault();
            actionCodeBlock();
        } else if (key === 'v' && e.shiftKey) {
            // Cmd+Shift+V — upload image from clipboard
            e.preventDefault();
            uploadImageFromClipboard();
        } else if (key === 'o' && e.shiftKey) {
            // Cmd+Shift+O — upload image from local file
            e.preventDefault();
            uploadImageFromFile();
        } else if (key === '\\') {
            // Cmd+\ — cycle through view modes
            e.preventDefault();
            cycleViewMode();
        } else if (e.code === 'Digit1' && !e.shiftKey) {
            e.preventDefault();
            actionH1();
        } else if (e.code === 'Digit2' && !e.shiftKey) {
            e.preventDefault();
            actionH2();
        } else if (e.code === 'Digit3' && !e.shiftKey) {
            e.preventDefault();
            actionH3();
        } else if (e.code === 'Digit4' && !e.shiftKey) {
            e.preventDefault();
            actionH4();
        } else if (e.code === 'Digit5' && !e.shiftKey) {
            e.preventDefault();
            actionH5();
        } else if (e.code === 'Digit6' && !e.shiftKey) {
            e.preventDefault();
            actionH6();
        } else if (e.code === 'Digit7' && !e.shiftKey) {
            e.preventDefault();
            actionH7();
        } else if (e.code === 'Digit7' && e.shiftKey) {
            // Cmd+Shift+7 — ordered list (US layout: "&" key, but e.code is layout-independent)
            e.preventDefault();
            actionOl();
        } else if (e.code === 'Digit8' && e.shiftKey) {
            // Cmd+Shift+8 — unordered list ("*" key on US layout)
            e.preventDefault();
            actionUl();
        } else if (e.code === 'Period' && e.shiftKey) {
            // Cmd+Shift+. — blockquote (">" on US layout)
            e.preventDefault();
            actionQuote();
        } else if (e.code === 'Quote' && e.shiftKey) {
            // Cmd+Shift+" — inline quote (typographic "smart" quotes)
            e.preventDefault();
            actionQuoteInline();
        }
    }

    /**
     * Monaco-pipeline key handler. Runs before Monaco inserts the typed
     * character, so it can intercept plain keys that the window-level
     * `onKeyDown` is too late to catch. Currently only handles:
     *
     *   `  (backtick, no modifier) when at least one selection is non-empty
     *     → wrap every selection with backticks (inline code)
     *
     * Without any non-empty selection we let Monaco insert a literal backtick
     * at the (single) cursor. Multi-cursor is supported: if any cursor has
     * a non-empty selection, all selections are wrapped in one transaction.
     */
    function onEditorKeyDown(e) {
        if (!e.keyCode || e.keyCode !== monaco.KeyCode.Backquote) return;
        if (e.metaKey || e.ctrlKey || e.altKey || e.shiftKey) return;
        if (!editor) return;
        const selections = editor.getSelections();
        if (!selections || selections.length === 0) return;
        const model = editor.getModel();
        if (!model) return;
        // Only intercept if at least one selection has text — otherwise
        // let Monaco type a literal backtick at the primary cursor.
        const hasNonEmpty = selections.some(s => {
            if (s.isEmpty()) return false;
            const t = model.getValueInRange(s);
            return t && t.length > 0;
        });
        if (!hasNonEmpty) return;
        e.preventDefault();
        e.stopPropagation();
        actionCode();
    }

    // ============================================================
    //  Script runner (Preview / Deploy toolbar buttons)
    // ============================================================
    //
    // Each action (preview | deploy) maps to a script path configured
    // in Settings. The server returns:
    //   - POST   /api/scripts/{action}        → start
    //   - GET    /api/scripts/{action}/status → { running, ... }
    //   - GET    /api/scripts/{action}/log?tail=N → { text }
    //   - POST   /api/scripts/preview/stop    → kill detached preview
    //
    // The modal polls /status and /log every 1s while open. We keep
    // polling even after the user closes the modal (in a no-op
    // background interval) so the buttons reflect up-to-date state on
    // re-open — but we don't blow up the user's network when the
    // editor sits idle.

    let _scriptPollTimer = null;     // active poll interval
    let _scriptIdleTimer = null;     // background poll (every 10s)
    let _scriptCurrent = null;       // 'preview' | 'deploy' | null

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
        // tooltips/status stay fresh if the user reopens later.
        stopScriptPolling();
        startIdlePolling();
    }
    function setScriptStatus(state, text) {
        if (!els.scriptStatus) return;
        els.scriptStatus.className = 'script-status status-' + state;
        els.scriptStatus.textContent = text;
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
    function escapeHtml(s) {
        return String(s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    }

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
            // Sync toolbar button state from the latest status
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

    function syncToolbarButton(action, status) {
        const btn = action === 'preview' ? els.previewBtn : els.deployBtn;
        if (!btn) return;
        // Mark the button with a state class so the user can tell at
        // a glance whether a server is up / a deploy is running.
        btn.classList.remove('btn-running', 'btn-ready');
        if (action === 'preview') {
            if (status.serverReady) btn.classList.add('btn-ready');
            else if (status.running) btn.classList.add('btn-running');
        } else {
            if (status.running) btn.classList.add('btn-running');
        }
        // Also reflect status in the title (tooltip)
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

    async function runScript(action) {
        // Open the modal first so the user immediately sees "starting…"
        openScriptModal(action);
        try {
            const r = await fetchJson(`/api/scripts/${action}`, { method: 'POST' });
            // Triggers an immediate re-poll so the log shows the
            // first lines without waiting a full second.
            pollScriptOnce();
            if (r.logFile) {
                setScriptStatus('running', 'building…');
            }
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

    // ---------- init ----------
    function init() {
        if (els.saveBtn) els.saveBtn.addEventListener('click', save);
        if (els.discardBtn) els.discardBtn.addEventListener('click', discard);

        // Spell + grammar check toggle (status bar). The on/off state is
        // loaded from localStorage at module init time; this click handler
        // flips it and persists.
        updateSpellToggleUi();
        if (els.spellToggle) {
            els.spellToggle.addEventListener('click', () => {
                setSpellCheckEnabled(!_spellCheckEnabled);
            });
        }
        // LanguageTool online grammar check — independent toggle
        updateLtToggleUi();
        if (els.ltToggle) {
            els.ltToggle.addEventListener('click', () => {
                setLtEnabled(!_ltEnabled);
            });
        }
        // Auto-save toggle. Starts ON by default; the timer kicks in once
        // the editor is ready (startAutoSaveTimer called from the
        // editor.onDidChangeModelContent setup below).
        updateAutoSaveToggleUi();
        if (els.autoSaveToggle) {
            els.autoSaveToggle.addEventListener('click', () => {
                setAutoSaveEnabled(!_autoSaveEnabled);
            });
        }
        if (_autoSaveEnabled) startAutoSaveTimer();

        // View-mode toggle buttons
        for (const m of VIEW_MODES) {
            const btn = els2.viewBtns[m];
            if (btn) btn.addEventListener('click', () => setViewMode(m));
        }

        // Format toolbar — wire each button to its action.
        const actions = {
            bold: actionBold,
            italic: actionItalic,
            h1: actionH1,
            h2: actionH2,
            h3: actionH3,
            h4: actionH4,
            h5: actionH5,
            h6: actionH6,
            h7: actionH7,
            ul: actionUl,
            ol: actionOl,
            quote: actionQuote,
            'quote-inline': actionQuoteInline,
            code: actionCode,
            codeblock: actionCodeBlock,
            linebreak: actionLineBreak,
            link: actionLink,
            'upload-image': uploadImageFromClipboard,
        };
        if (els.formatToolbar) {
            els.formatToolbar.addEventListener('click', e => {
                const btn = e.target.closest('[data-action]');
                if (!btn) return;
                const action = actions[btn.dataset.action];
                if (action) action();
            });
        }
        if (els.fileInput) {
            els.fileInput.addEventListener('change', () => {
                const file = els.fileInput.files && els.fileInput.files[0];
                if (file) uploadImageFromChosenFile(file);
                // Reset so the same file can be picked again
                els.fileInput.value = '';
            });
        }

        // Script-runner toolbar buttons (Preview / Deploy)
        if (els.previewBtn) els.previewBtn.addEventListener('click', () => runScript('preview'));
        if (els.deployBtn)  els.deployBtn.addEventListener('click',  () => runScript('deploy'));
        if (els.scriptStopBtn) els.scriptStopBtn.addEventListener('click', stopPreview);
        // Close modal via ×, footer Close, or backdrop click
        if (els.scriptModal) {
            els.scriptModal.addEventListener('click', e => {
                if (e.target.matches('[data-script-close]')) closeScriptModal();
            });
            document.addEventListener('keydown', e => {
                if (e.key === 'Escape' && !els.scriptModal.hidden) closeScriptModal();
            });
        }

        // Begin low-frequency background polling so the toolbar
        // buttons reflect up-to-date status without the user opening
        // the modal first.
        startIdlePolling();
    }
    init();
    document.addEventListener('DOMContentLoaded', loadFile);
})();
