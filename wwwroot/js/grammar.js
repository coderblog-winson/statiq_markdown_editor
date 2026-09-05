// =====================================================
//  grammar.js — basic grammar / style rules (no ML).
//  Plain script. Exposes window.GrammarCheck.checkGrammar.
// =====================================================
(function () {
    'use strict';

    // Common typos that are easy to catch and a dictionary can't fix.
    const COMMON_TYPOS = {
        'teh': 'the',
        'recieve': 'receive',
        'occured': 'occurred',
        'occuring': 'occurring',
        'occurence': 'occurrence',
        'seperate': 'separate',
        'seperator': 'separator',
        'seperately': 'separately',
        'definately': 'definitely',
        'accomodate': 'accommodate',
        'accomodation': 'accommodation',
        'occassion': 'occasion',
        'occassionally': 'occasionally',
        'untill': 'until',
        'wich': 'which',
        'recomend': 'recommend',
        'recomended': 'recommended',
        'usefull': 'useful',
        'usefully': 'usefully',
        'begining': 'beginning',
        'writting': 'writing',
        'writte': 'wrote',
        'writen': 'written',
        'thier': 'their',
        'alot': 'a lot',
        'aswell': 'as well',
        'infront': 'in front',
    };

    function isWordBoundary(ch) {
        return !/[a-zA-Z0-9'\-_]/.test(ch);
    }

    function findWordAt(text, idx) {
        let s = idx, e = idx;
        while (s > 0 && !isWordBoundary(text[s - 1])) s--;
        while (e < text.length && !isWordBoundary(text[e + 1])) e++;
        return { start: s, end: e + 1, word: text.slice(s, e + 1) };
    }

    const SENTENCE_END = /[.!?]/;

    /**
     * Walk the text line by line, skipping fenced code blocks and inline code,
     * and emit issues for the rules below.
     */
    function checkGrammar(text) {
        const issues = [];
        const lines = text.split('\n');
        let offset = 0;
        let inFence = false;

        for (let i = 0; i < lines.length; i++) {
            const line = lines[i];
            if (/^\s*```/.test(line)) {
                inFence = !inFence;
                offset += line.length + 1;
                continue;
            }
            if (inFence) {
                offset += line.length + 1;
                continue;
            }

            // Strip inline code to avoid false positives in code samples
            const visible = line.replace(/`[^`\n]*`/g, m => ' '.repeat(m.length));

            // 1) Common typo map
            for (const [wrong, right] of Object.entries(COMMON_TYPOS)) {
                const re = new RegExp(`\\b${wrong}\\b`, 'gi');
                let m;
                while ((m = re.exec(visible)) !== null) {
                    issues.push({
                        kind: 'typo',
                        rule: 'common-typo',
                        severity: 'warning',
                        start: offset + m.index,
                        end: offset + m.index + m[0].length,
                        word: m[0],
                        message: `Did you mean "${right}"?`,
                        suggestions: [right],
                    });
                }
            }

            // 2) Double space (not at end of line, not indentation)
            for (let j = 0; j < visible.length - 1; j++) {
                if (visible[j] === ' ' && visible[j + 1] === ' ') {
                    if (j === visible.length - 2) continue;  // trailing wrap
                    if (j === 0) continue;                    // indentation
                    issues.push({
                        kind: 'grammar',
                        rule: 'double-space',
                        severity: 'info',
                        start: offset + j,
                        end: offset + j + 2,
                        message: 'Double space — remove the extra space.',
                        suggestions: [' '],
                    });
                    while (j < visible.length - 1 && visible[j + 1] === ' ') j++;
                }
            }

            // 3) Sentence-end followed by lowercase: missing capital
            for (let j = 1; j < visible.length; j++) {
                const prev = visible[j - 1];
                const cur = visible[j];
                if (SENTENCE_END.test(prev) && /[a-z]/.test(cur) && /[a-zA-Z]/.test(visible[j - 2] || '')) {
                    if (j >= 2) {
                        const ctx2 = visible.slice(Math.max(0, j - 4), j + 1);
                        if (/\b(?:e\.g|i\.e|Mr|Mrs|Ms|Dr|vs|etc)\.\s*$/i.test(ctx2 + cur)) continue;
                    }
                    if (cur !== ' ' && /[a-zA-Z]/.test(cur)) {
                        const w = findWordAt(visible, j);
                        if (w.word && w.word.length > 1 && /^[a-z]/.test(w.word)) {
                            issues.push({
                                kind: 'grammar',
                                rule: 'sentence-capital',
                                severity: 'info',
                                start: offset + w.start,
                                end: offset + w.end,
                                message: `Capitalize the first letter after "${prev}".`,
                                suggestions: [w.word[0].toUpperCase() + w.word.slice(1)],
                            });
                        }
                    }
                }
            }

            // 4) Lowercase "i" as a standalone word (should be "I")
            // Skip markdown list markers, headings, and frontmatter
            const lowerIRe = /(^|[^a-zA-Z0-9])(i)($|[^a-zA-Z0-9])/g;
            let m;
            while ((m = lowerIRe.exec(visible)) !== null) {
                const wordStart = offset + m.index + m[1].length;
                const prev = visible.slice(0, wordStart);
                if (/^\s*[-*+] /.test(prev)) continue;
                if (/^\s*\d+\. /.test(prev)) continue;
                if (/^\s{0,3}#{1,6} /.test(prev)) continue;
                if (/^---+$/.test(prev.trim())) continue;
                if (/^\s*\w+\s*:/.test(prev)) continue;
                issues.push({
                    kind: 'grammar',
                    rule: 'lowercase-i',
                    severity: 'warning',
                    start: wordStart,
                    end: wordStart + 1,
                    message: 'Capitalize the pronoun "I".',
                    suggestions: ['I'],
                });
            }

            // 5) Sentence end punctuation check (long lines without terminal)
            if (visible.trim().length > 0
                && !/^\s*[-*+\d>]/.test(visible)
                && !/^\s*#/.test(visible)
                && !/^\s*-{3,}\s*$/.test(visible)
                && !/^\s*```/.test(visible)
                && !/^\s*\[.*\]\(.*\)\s*$/.test(visible)
                && i < lines.length - 1
                && lines[i + 1].trim().length > 0) {
                const lastChar = visible.trimEnd().slice(-1);
                const length = visible.trim().length;
                if (length > 80 && !/[.!?\)"'\*_`>]/.test(lastChar)) {
                    issues.push({
                        kind: 'grammar',
                        rule: 'sentence-end',
                        severity: 'info',
                        start: offset + visible.length - 1,
                        end: offset + visible.length,
                        message: 'Long sentence without terminal punctuation.',
                        suggestions: ['.'],
                    });
                }
            }

            offset += line.length + 1;
        }
        return issues;
    }

    window.GrammarCheck = { checkGrammar };
})();
