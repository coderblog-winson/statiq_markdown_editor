// =====================================================
//  spellcheck.js — nspell-based English spell checker
//  Plain script (not ESM). Uses dynamic import() to load
//  nspell from esm.sh on first use. Exposes window.SpellCheck.
// =====================================================
(function () {
    'use strict';

    const DICT_AFF = 'https://cdn.jsdelivr.net/gh/wooorm/dictionaries@main/dictionaries/en/index.aff';
    const DICT_DIC = 'https://cdn.jsdelivr.net/gh/wooorm/dictionaries@main/dictionaries/en/index.dic';
    const CACHE_KEY_AFF = 'sme.dict.en.aff';
    const CACHE_KEY_DIC = 'sme.dict.en.dic';
    const CACHE_VERSION = 'v1';
    const CACHE_KEY_VERSION = 'sme.dict.version';

    let _spell = null;
    let _loading = null;

    // Words that are not in the en_US dictionary but should be accepted
    // (Statiq-specific terms, common tech words, contractions, etc.)
    const CUSTOM_WORDS = new Set([
        'statiq', 'razor', 'markdown', 'csharp', 'dotnet', 'aspnet', 'blazor',
        'frontend', 'backend', 'webapi', 'middleware', 'configs',
        'regex', 'json', 'yaml', 'toml', 'http', 'https', 'websocket',
        'localhost', 'cli', 'api', 'sdk', 'ide', 'url', 'uri',
        'boolean', 'integer', 'string', 'array', 'byte', 'bytes',
        'memoization', 'memoize', 'memoized', 'deserialise', 'serialise',
        'tailwind', 'webpack', 'vite', 'rollup', 'esbuild',
        'selfhosting', 'self-hosted', 'selfhosted',
        'rollback', 'rollout', 'rollforward', 'rollbacks', 'rollouts',
        'readonly', 'writeable', 'writable',
        'multi-line', 'multiline', 'single-line', 'inline',
        'prebuilt', 'precompile', 'precompiled', 'precompile',
        'realtime', 'geolocation',
        'px', 'em', 'rem', 'rgb', 'rgba', 'hsl', 'vw', 'vh',
        'en-US', 'en-GB', 'zh-CN', 'zh-TW',
    ]);

    async function fetchText(url) {
        const r = await fetch(url, { cache: 'force-cache' });
        if (!r.ok) throw new Error(`Failed to fetch ${url}: ${r.status}`);
        return r.text();
    }

    function loadDictFromCache() {
        try {
            if (localStorage.getItem(CACHE_KEY_VERSION) !== CACHE_VERSION) return null;
            const aff = localStorage.getItem(CACHE_KEY_AFF);
            const dic = localStorage.getItem(CACHE_KEY_DIC);
            if (!aff || !dic) return null;
            return { aff, dic };
        } catch { return null; }
    }

    function saveDictToCache(aff, dic) {
        try {
            localStorage.setItem(CACHE_KEY_AFF, aff);
            localStorage.setItem(CACHE_KEY_DIC, dic);
            localStorage.setItem(CACHE_KEY_VERSION, CACHE_VERSION);
        } catch (e) {
            // Quota exceeded or storage disabled — silently degrade
            console.warn('Could not cache dictionary to localStorage:', e.message);
        }
    }

    /**
     * Loads the dictionary and returns an nspell instance. Cached after first call.
     * First call downloads the dictionary from the CDN (~600 KB); subsequent calls
     * read from localStorage and are instant.
     */
    function loadSpellcheck(onProgress) {
        if (_spell) return Promise.resolve(_spell);
        if (_loading) return _loading;

        _loading = (async () => {
            if (onProgress) onProgress('Loading spell checker…');
            const nspellMod = await import('https://esm.sh/nspell@2.1.5');
            const nspell = nspellMod.default;

            let dict = loadDictFromCache();
            if (!dict) {
                if (onProgress) onProgress('Downloading English dictionary (~600 KB, cached after first run)…');
                const [aff, dic] = await Promise.all([fetchText(DICT_AFF), fetchText(DICT_DIC)]);
                dict = { aff, dic };
                saveDictToCache(aff, dic);
            }
            _spell = nspell(dict.aff, dict.dic);
            return _spell;
        })();

        return _loading;
    }

    /**
     * Check `text` and return an array of issues, one per misspelled word.
     * Each issue: { kind: 'spell', word, start, end, suggestions: [..], severity }
     *
     * Ignores:
     *  - words inside fenced code blocks (``` or `) and inline code
     *  - URLs (http://, https://, /path/...)
     *  - markdown links/images
     *  - HTML tags
     *  - all-uppercase words (likely acronyms)
     *  - words containing digits
     *  - words in CUSTOM_WORDS
     */
    function checkText(spell, text) {
        if (!spell) return [];

        const issues = [];
        const lines = text.split('\n');
        let offset = 0;
        let inFence = false;

        for (let i = 0; i < lines.length; i++) {
            const line = lines[i];
            // Track fenced code blocks
            if (/^```/.test(line)) {
                inFence = !inFence;
                offset += line.length + 1;
                continue;
            }
            if (inFence) {
                offset += line.length + 1;
                continue;
            }

            // Strip out: inline code spans `code`, URLs, link/image syntax, HTML tags
            // We replace with same-length spaces so positions map 1:1.
            let stripped = '';
            let cursor = 0;
            const skipRanges = [];
            // inline code
            const inlineCodeRe = /`[^`\n]*`/g;
            let m;
            while ((m = inlineCodeRe.exec(line)) !== null) {
                skipRanges.push([m.index, m.index + m[0].length]);
            }
            // URLs
            const urlRe = /https?:\/\/\S+/g;
            while ((m = urlRe.exec(line)) !== null) {
                skipRanges.push([m.index, m.index + m[0].length]);
            }
            // markdown links [text](url) and images ![alt](url)
            const linkRe = /!?\[[^\]]*\]\([^)]*\)/g;
            while ((m = linkRe.exec(line)) !== null) {
                skipRanges.push([m.index, m.index + m[0].length]);
            }
            // HTML tags
            const htmlRe = /<\/?[a-zA-Z][^>]*>/g;
            while ((m = htmlRe.exec(line)) !== null) {
                skipRanges.push([m.index, m.index + m[0].length]);
            }
            // Sort and merge
            skipRanges.sort((a, b) => a[0] - b[0]);

            for (let j = 0; j < line.length; j++) {
                const inSkip = skipRanges.some(([s, e]) => j >= s && j < e);
                stripped += inSkip ? ' ' : line[j];
            }

            // Word regex
            const wordRe = /[a-zA-Z][a-zA-Z'\-]*/g;
            let wm;
            while ((wm = wordRe.exec(stripped)) !== null) {
                const word = wm[0];
                const lower = word.toLowerCase();
                if (CUSTOM_WORDS.has(lower)) continue;
                if (word.length < 2) continue;
                // Skip likely acronyms (2+ consecutive uppercase)
                if (word.length >= 2 && word === word.toUpperCase() && /[A-Z]/.test(word[1])) continue;
                if (word.includes("'")) continue; // contractions
                if (word.includes('-') && word.split('-').some(p => p.length < 2)) continue;

                if (spell.correct(word)) continue;

                const startInStripped = wm.index;
                const endInStripped = startInStripped + word.length;
                const start = offset + startInStripped;
                const end = offset + endInStripped;

                issues.push({
                    kind: 'spell',
                    word,
                    start,
                    end,
                    severity: 'warning',
                    suggestions: (spell.suggest(word) || []).slice(0, 5),
                });
            }
            offset += line.length + 1;
        }
        return issues;
    }

    window.SpellCheck = { loadSpellcheck, checkText };
})();
