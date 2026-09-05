// =====================================================
//  languagetool.js — LanguageTool public API wrapper.
//
//  Public API (no auth needed):
//    POST https://api.languagetool.org/v2/check
//      text=<text>&language=en-US&enabledOnly=false
//
//  Returns { matches: [{ offset, length, message, shortMessage,
//    replacements: [{value}], rule: { id, description, issueType,
//    category: { id, name }, urls: [{value}] } }] }
//
//  CORS is enabled server-side (`Access-Control-Allow-Origin: *`),
//  so we can call it directly from the browser without a proxy.
//
//  Rate limit on the public endpoint: ~20 requests / IP / minute.
//  We debounce calls on the editor side, so manual typing never
//  blows through that.
//
//  Self-host option: run a LanguageTool Docker container and
//  override LT_ENDPOINT below (e.g. "https://lt.your-domain.com").
// =====================================================
(function () {
    'use strict';

    const LT_ENDPOINT = 'https://api.languagetool.org/v2/check';
    const DEFAULT_LANGUAGE = 'en-US';
    const REQUEST_TIMEOUT_MS = 8000;

    // Map LanguageTool issueType → Monaco MarkerSeverity.
    // LanguageTool values: 'misspelling' | 'grammar' | 'style' | 'typographical' | 'uncategorized' | 'locale-violation'
    function ltSeverityToMonaco(issueType) {
        // Spell: error (red squiggle). Grammar: warning (yellow). Style: hint (blue).
        if (issueType === 'misspelling') return 'error';
        if (issueType === 'grammar' || issueType === 'typographical') return 'warning';
        if (issueType === 'style' || issueType === 'uncategorized') return 'info';
        return 'info';
    }

    /**
     * Strip the markdown frontmatter from the text so LanguageTool
     * doesn't try to "fix" our YAML metadata. Also strip the text
     * inside fenced code blocks and inline code, since we don't want
     * LT to flag identifiers, config syntax, etc. as misspellings.
     * We replace the stripped spans with same-length whitespace so
     * the returned `offset`s still map 1:1 to positions in the
     * original text.
     */
    function maskNonProseRegions(text) {
        // 1. Drop YAML frontmatter entirely
        const fmMatch = text.match(/^---\r?\n[\s\S]*?\r?\n---[ \t]*(?:\r?\n+|$)/);
        let masked = text;
        let fmOffset = 0;
        let fmLength = 0;
        if (fmMatch) {
            fmOffset = 0;
            fmLength = fmMatch[0].length;
            masked = ' '.repeat(fmLength) + text.slice(fmLength);
        }

        // 2. Mask fenced code blocks (```...```) and inline code (`...`)
        //    in the (possibly masked-frontmatter) string.
        const ranges = [];
        const fenceRe = /```[\s\S]*?(?:```|$)/g;
        let m;
        while ((m = fenceRe.exec(masked)) !== null) ranges.push([m.index, m.index + m[0].length]);
        const inlineRe = /`[^`\n]+`/g;
        while ((m = inlineRe.exec(masked)) !== null) ranges.push([m.index, m.index + m[0].length]);
        // 3. Mask URLs (http(s)://...)
        const urlRe = /https?:\/\/\S+/g;
        while ((m = urlRe.exec(masked)) !== null) ranges.push([m.index, m.index + m[0].length]);

        // Sort and merge overlapping ranges
        ranges.sort((a, b) => a[0] - b[0]);
        const merged = [];
        for (const r of ranges) {
            if (merged.length && r[0] <= merged[merged.length - 1][1]) {
                merged[merged.length - 1][1] = Math.max(merged[merged.length - 1][1], r[1]);
            } else {
                merged.push([r[0], r[1]]);
            }
        }

        // Build masked string
        let out = '';
        let cur = 0;
        for (const [s, e] of merged) {
            out += masked.slice(cur, s) + ' '.repeat(e - s);
            cur = e;
        }
        out += masked.slice(cur);

        return { masked: out, ranges: merged, fmLength };
    }

    /**
     * Convert LanguageTool's `offset` (in the masked text) back to
     * an offset in the original text. The masking preserves length,
     * so a position p in masked is also at position p in original
     * EXCEPT for ranges that were masked — those positions are in
     * "dead" space where we never want to surface a marker. We
     * simply drop any match whose offset falls inside a masked
     * range (LanguageTool shouldn't return such matches, but
     * be defensive).
     */
    function filterMaskedMatches(matches, ranges) {
        if (!ranges.length) return matches;
        return matches.filter(m => {
            const start = m.offset;
            const end = m.offset + m.length;
            return !ranges.some(([s, e]) => start < e && end > s);
        });
    }

    /**
     * Check `text` against LanguageTool. Resolves to an array of
     * "issues" shaped for the existing Monaco marker pipeline:
     *
     *   { kind, word, start, end, severity, suggestions[], rule, _meta }
     *
     * On any network / parse / 4xx error, resolves to [] (don't
     * disrupt the editor on a flaky API).
     */
    async function checkText(text, options) {
        const opts = options || {};
        const endpoint = opts.endpoint || LT_ENDPOINT;
        const language = opts.language || DEFAULT_LANGUAGE;
        if (!text || text.trim().length < 2) return [];

        const { masked, ranges } = maskNonProseRegions(text);
        // Don't send huge payloads — LanguageTool has size limits on
        // the public endpoint. ~20k chars is a safe ceiling.
        const payload = masked.length > 20000 ? masked.slice(0, 20000) : masked;

        const ac = new AbortController();
        const timer = setTimeout(() => ac.abort(), REQUEST_TIMEOUT_MS);

        try {
            const body = new URLSearchParams({
                text: payload,
                language,
                enabledOnly: 'false',
            });
            const r = await fetch(endpoint, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/x-www-form-urlencoded',
                    'Accept': 'application/json',
                },
                body,
                signal: ac.signal,
            });
            clearTimeout(timer);
            if (!r.ok) {
                console.warn('[languagetool] HTTP ' + r.status + ' — skipping');
                return [];
            }
            const data = await r.json();
            const matches = Array.isArray(data.matches) ? data.matches : [];
            const filtered = filterMaskedMatches(matches, ranges);

            return filtered.map(m => {
                const replacements = (m.replacements || []).map(r => r.value).filter(Boolean);
                const firstReplacement = replacements[0] || '';
                const word = payload.slice(m.offset, m.offset + m.length) || '';
                const issueType = (m.rule && m.rule.issueType) || 'uncategorized';
                return {
                    kind: 'lt',
                    word,
                    start: m.offset,
                    end: m.offset + m.length,
                    severity: ltSeverityToMonaco(issueType),
                    suggestions: replacements.slice(0, 5),
                    // Extra metadata used by the code-action provider
                    // to build a "Replace with …" quickfix menu.
                    rule: m.rule && m.rule.id,
                    category: m.rule && m.rule.category && m.rule.category.name,
                    message: m.message,
                    shortMessage: m.shortMessage,
                    url: m.rule && Array.isArray(m.rule.urls) && m.rule.urls[0] && m.rule.urls[0].value,
                    // The actual replacement text the quickfix should
                    // apply, pre-computed for the click handler.
                    _replacement: firstReplacement,
                };
            });
        } catch (err) {
            clearTimeout(timer);
            if (err.name === 'AbortError') {
                console.warn('[languagetool] request timed out after ' + REQUEST_TIMEOUT_MS + 'ms');
            } else {
                console.warn('[languagetool] check failed:', err.message);
            }
            return [];
        }
    }

    window.LanguageTool = { checkText, LT_ENDPOINT };
})();
