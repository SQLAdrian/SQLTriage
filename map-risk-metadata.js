// Risk Metadata Mapper
// Priority order for each risk-metadata entry:
//   1. Match CSV "Recommendation Title" → entry displayName  (exact, then substring)
//   2. Match CSV "Recommendation Title" → ruleset displayName / description / message
//   3. Derive algorithmically from ruleset via helpLink
//   4. Derive algorithmically from ruleset via displayName fuzzy search
//   5. Defaults based on level

const fs   = require('fs');
const path = require('path');

const riskPath    = path.join(__dirname, 'Config', 'risk-metadata.json');
const rulesetPath = path.join(__dirname, 'Config', 'ruleset.json');
const csvGlob     = path.join(__dirname, 'output');

// ── Load files ───────────────────────────────────────────────────────────────

const riskMeta = JSON.parse(fs.readFileSync(riskPath,    'utf8'));
const ruleset  = JSON.parse(fs.readFileSync(rulesetPath, 'utf8'));
const rules    = ruleset.rules || ruleset.checks || ruleset;

// Find the 2022 CSV
const csvFile = fs.readdirSync(csvGlob)
    .filter(f => f.endsWith('.csv') && f.toLowerCase().includes('assessmentprogram'))
    .map(f => path.join(csvGlob, f))
    .sort().pop();

if (!csvFile) { console.error('No AssessmentProgram CSV found in ./output'); process.exit(1); }
console.log('CSV source:', path.basename(csvFile));

// ── CSV parser (handles multi-line quoted cells) ─────────────────────────────

function parseCsv(text) {
    const rows = [];
    let i = 0;
    // strip BOM
    if (text.charCodeAt(0) === 0xFEFF) i = 1;
    while (i < text.length) {
        const row = [];
        while (i < text.length) {
            let cell = '';
            if (text[i] === '"') {
                i++;
                while (i < text.length) {
                    if (text[i] === '"' && text[i+1] === '"') { cell += '"'; i += 2; }
                    else if (text[i] === '"') { i++; break; }
                    else cell += text[i++];
                }
            } else {
                while (i < text.length && text[i] !== ',' && text[i] !== '\n' && text[i] !== '\r')
                    cell += text[i++];
                cell = cell.trim();
            }
            row.push(cell);
            if (i < text.length && text[i] === ',') i++;
            else break;
        }
        while (i < text.length && (text[i] === '\r' || text[i] === '\n')) i++;
        if (row.length > 1 || row[0]) rows.push(row);
    }
    return rows;
}

const csvRows   = parseCsv(fs.readFileSync(csvFile, 'utf8'));
const csvHeader = csvRows[0].map(h => h.trim());
const col = h => csvHeader.indexOf(h);

const C = {
    title:  col('Recommendation Title'),
    score:  col('Score'),
    prob:   col('Probability'),
    impact: col('Impact'),
    effort: col('Effort'),
    tech:   col('Technology'),
};

// Deduplicate CSV rows by title, keeping the first occurrence
const csvByTitle = new Map();   // normalised title → row
for (const row of csvRows.slice(1)) {
    const title = (row[C.title] || '').trim();
    if (!title) continue;
    const key = title.toLowerCase();
    if (!csvByTitle.has(key)) csvByTitle.set(key, row);
}
console.log(`CSV rows (unique titles): ${csvByTitle.size}`);

// ── Ruleset indexes ──────────────────────────────────────────────────────────

const ruleByHelpLink     = new Map();
const ruleByDisplayName  = new Map();

for (const rule of rules) {
    if (rule.helpLink)    ruleByHelpLink.set(rule.helpLink.toLowerCase(), rule);
    if (rule.displayName) {
        const key = rule.displayName.toLowerCase().trim();
        if (!ruleByDisplayName.has(key)) ruleByDisplayName.set(key, rule);
    }
}

// ── Helpers ──────────────────────────────────────────────────────────────────

function toProbability(level) {
    switch ((level||'').toLowerCase()) {
        case 'critical': return 'High';
        case 'high':     return 'High';
        case 'medium':   return 'Medium';
        case 'low':      return 'Low';
        case 'information': return 'Low';
        default:         return 'Medium';
    }
}
function toScore(level) {
    switch ((level||'').toLowerCase()) {
        case 'critical':    return 10;
        case 'high':        return 7;
        case 'medium':      return 5;
        case 'low':         return 3;
        case 'information': return 1;
        default:            return 5;
    }
}
function toEffort(tags, desc) {
    const t = (tags||[]).join(' ').toLowerCase();
    const d = (desc||'').toLowerCase();
    if (/index|security|backup|encrypt/.test(t+d)) return 'High';
    if (/config|performance|memory|cpu/.test(t+d))  return 'Medium';
    return 'Low';
}
function toImpact(desc) {
    if (!desc) return '';
    const dot = desc.indexOf('.');
    if (dot > 0 && dot < 300) return desc.slice(0, dot + 1);
    return desc.slice(0, 300) + (desc.length > 300 ? '...' : '');
}
function toTech(tags) {
    return (tags||[]).filter(t => t !== 'DefaultRuleset' && t !== 'DefaultRuleSet').join(', ');
}
function toThisIsBad(tags) {
    const t = (tags||[]).join(' ').toLowerCase();
    return !/information|deprecated/.test(t);
}

// Find a CSV row whose title matches a search string
function findCsvRow(searchStr) {
    if (!searchStr) return null;
    const s = searchStr.toLowerCase().trim();
    // Exact
    if (csvByTitle.has(s)) return csvByTitle.get(s);
    // Substring both ways
    for (const [key, row] of csvByTitle) {
        if (key.includes(s) || s.includes(key)) return row;
    }
    return null;
}

// Find ruleset rule for an entry (helpLink → displayName fuzzy → description → message)
function findRule(entry) {
    if (entry.helpLink) {
        const r = ruleByHelpLink.get(entry.helpLink.toLowerCase());
        if (r) return r;
    }
    const dn = (entry.displayName||'').toLowerCase().trim();
    if (dn && ruleByDisplayName.has(dn)) return ruleByDisplayName.get(dn);
    // substring search
    for (const rule of rules) {
        if (rule.displayName && rule.displayName.toLowerCase().includes(dn)) return rule;
    }
    for (const rule of rules) {
        if (rule.description && rule.description.toLowerCase().includes(dn)) return rule;
    }
    for (const rule of rules) {
        if (rule.message && rule.message.toLowerCase().includes(dn)) return rule;
    }
    return null;
}

// ── Main mapping loop ────────────────────────────────────────────────────────

const entries = riskMeta.VulnerabilityAssessment.entries;
let skipped = 0, fromCsv = 0, fromRuleset = 0, fromDefaults = 0;

for (const checkId of Object.keys(entries)) {
    const entry = entries[checkId];

    const needsUpdate = !entry.probability || !entry.impact ||
                        !entry.effort || !entry.technology || entry.score === 0;
    if (!needsUpdate) { skipped++; continue; }

    // --- Pass 1: match CSV by entry displayName ---
    let csvRow = findCsvRow(entry.displayName);

    // --- Pass 2: match CSV via ruleset (message / description fields) ---
    if (!csvRow) {
        const rule = findRule(entry);
        if (rule) {
            csvRow = findCsvRow(rule.message) || findCsvRow(rule.description) || findCsvRow(rule.displayName);
        }
    }

    if (csvRow) {
        // Use real curated values from CSV
        const score  = parseFloat(csvRow[C.score])  || 0;
        const prob   = (csvRow[C.prob]   || '').trim();
        const impact = (csvRow[C.impact] || '').trim();
        const effort = (csvRow[C.effort] || '').trim();
        const tech   = (csvRow[C.tech]   || '').trim();

        if (!entry.probability && prob)   entry.probability = prob;
        if (!entry.impact      && impact) entry.impact      = impact;
        if (!entry.effort      && effort) entry.effort      = effort;
        if (!entry.technology  && tech)   entry.technology  = tech;
        if (entry.score === 0  && score)  entry.score       = score;
        fromCsv++;
    } else {
        // No CSV match — derive from ruleset or defaults
        const rule  = findRule(entry);
        const level = rule?.level || entry.level || '';
        const tags  = rule?.tags  || [];
        const desc  = rule?.description || '';

        if (!entry.probability) entry.probability = toProbability(level);
        if (!entry.impact)      entry.impact      = toImpact(desc) || 'Review and address based on check requirements.';
        if (!entry.effort)      entry.effort      = toEffort(tags, desc);
        if (!entry.technology)  entry.technology  = toTech(tags) || entry.category || '';
        if (entry.score === 0)  entry.score       = toScore(level);
        if (!entry.thisIsBad)   entry.thisIsBad   = toThisIsBad(tags);

        rule ? fromRuleset++ : fromDefaults++;
    }
}

console.log('');
console.log('Results:');
console.log(`  Already filled (skipped) : ${skipped}`);
console.log(`  Matched from 2022 CSV    : ${fromCsv}`);
console.log(`  Derived from ruleset     : ${fromRuleset}`);
console.log(`  Defaults only            : ${fromDefaults}`);

fs.writeFileSync(riskPath, JSON.stringify(riskMeta, null, 2), 'utf8');
console.log('');
console.log('Saved →', riskPath);
