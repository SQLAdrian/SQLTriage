/* In the name of God, the Merciful, the Compassionate */

/**
 * Query Plan V2 renderer.
 *
 * Consumes the JSON graph produced by ExecutionPlanParser.cs and renders
 * a horizontal tree (root left, leaves right) using absolutely-positioned
 * div nodes and an SVG edge layer — exactly the SSMS reading direction.
 *
 * Rollback: set _useV2 = false in QueryPlanModal.razor to fall back to
 * the legacy queryPlanInterop renderer without removing any code.
 */
window.queryPlanInteropV2 = (function () {
    'use strict';

    // ── Layout constants ──────────────────────────────────────────────────────
    const NODE_W = 144;   // node box width
    const NODE_H = 70;    // node box height
    const H_GAP  = 72;    // horizontal gap between tree depth levels
    const V_GAP  = 14;    // vertical gap between sibling subtrees
    const PAD    = 24;    // canvas padding

    // ── Icon map (LogicalOp/PhysicalOp → qp-icon-* CSS class suffix) ─────────
    // Keys must match the exact PhysicalOp/LogicalOp strings from showplan XML.
    // Values must match the suffix used in qp.css (e.g. qp-icon-ClusteredIndexScan).
    const ICON_MAP = {
        'Clustered Index Scan':      'ClusteredIndexScan',
        'Clustered Index Seek':      'ClusteredIndexSeek',
        'Clustered Index Delete':    'ClusteredIndexDelete',
        'Clustered Index Insert':    'ClusteredIndexInsert',
        'Clustered Index Update':    'ClusteredIndexUpdate',
        'Clustered Index Merge':     'ClusteredIndexMerge',
        'Index Scan':                'IndexScan',
        'Index Seek':                'IndexSeek',
        'Index Delete':              'IndexDelete',
        'Index Insert':              'IndexInsert',
        'Index Update':              'IndexUpdate',
        'Table Scan':                'TableScan',
        'Table Delete':              'TableDelete',
        'Table Insert':              'TableInsert',
        'Table Update':              'TableUpdate',
        'Key Lookup':                'KeyLookup',
        'RID Lookup':                'RIDLookup',
        'Hash Match':                'HashMatch',
        'Hash Aggregate':            'HashMatch',
        'Nested Loops':              'NestedLoops',
        'Merge Join':                'MergeJoin',
        'Sort':                      'Sort',
        'Stream Aggregate':          'StreamAggregate',
        'Compute Scalar':            'ComputeScalar',
        'Filter':                    'Filter',
        'Top':                       'Top',
        'Result':                    'Result',
        'Select':                    'Result',
        'Concatenation':             'Concatenation',
        'Parallelism':               'Parallelism',
        'Bitmap':                    'Bitmap',
        'Table Spool':               'Spool',
        'Index Spool':               'Spool',
        'Lazy Spool':                'Spool',
        'Eager Spool':               'Spool',
        'Assert':                    'Assert',
        'Constant Scan':             'ConstantScan',
        'Remote Query':              'RemoteQuery',
        'Remote Scan':               'RemoteScan',
        'Remote Index Seek':         'RemoteIndexSeek',
        'Aggregate':                 'StreamAggregate',
        'Table Valued Function':     'TableScan',       // sprite's TableValuedFunction shows Σ — TableScan matches SSMS appearance
        'Table-valued function':     'TableScan',
        'Collapse':                  'Collapse',
        'Deleted Scan':              'DeletedScan',
        'Inserted Scan':             'InsertedScan',
        'Log Row Scan':              'LogRowScan',
        'Print':                     'Print',
        'Sequence':                  'Sequence',
        'Sequence Project':          'SequenceProject',
        'Segment':                   'Segment',
        'Row Count Spool':           'RowCountSpool',
        'Merge':                     'MergeInterval',
        'Update':                    'Update',
        'Delete':                    'Delete',
        'Insert':                    'Insert',
        'Statement':                 'Statement',
        'Parameter Table Scan':      'ConstantScan',
        'UDX':                       'UDX',
        'Split':                     'Split',
    };

    function iconClass(type) {
        return 'qp-icon-' + (ICON_MAP[type] || 'Catchall');
    }

    // ── Cost → colour ─────────────────────────────────────────────────────────
    function costColor(pct) {
        if (pct < 5)  return '#4caf7a';   // green
        if (pct < 20) return '#f0a500';   // amber
        if (pct < 40) return '#e06c00';   // orange
        return '#c0392b';                  // red
    }

    // ── Number formatting ─────────────────────────────────────────────────────
    function fmt(n) {
        if (n == null || n === '') return '–';
        n = +n;
        if (n >= 1e9) return (n / 1e9).toFixed(2) + 'B';
        if (n >= 1e6) return (n / 1e6).toFixed(2) + 'M';
        if (n >= 1e3) return (n / 1e3).toFixed(1) + 'K';
        return (n % 1 === 0 ? n.toFixed(0) : n.toFixed(4));
    }

    // ── Tree layout ───────────────────────────────────────────────────────────

    /**
     * Recursive Reingold–Tilford–style layout.
     * Returns the subtree height (px) and fills positions[nodeId] = {x, y}.
     * x = depth * (NODE_W + H_GAP)  → grows right (root at x=0, leaves rightmost)
     * y is computed so each node is centred on its children.
     */
    function computeLayout(nodeId, childMap, positions, depth) {
        const kids = childMap[nodeId] || [];

        if (kids.length === 0) {
            positions[nodeId] = { x: depth * (NODE_W + H_GAP), y: 0 };
            return NODE_H;
        }

        // Layout each child subtree independently (each starts at y=0)
        const childHeights = kids.map(cid => computeLayout(cid, childMap, positions, depth + 1));

        // Stack children vertically
        let yOff = 0;
        kids.forEach((cid, i) => {
            shiftSubtree(cid, childMap, positions, 0, yOff);
            yOff += childHeights[i];
            if (i < kids.length - 1) yOff += V_GAP;
        });

        // Centre this node on the midpoint between first and last child centres
        const fy = positions[kids[0]].y + NODE_H / 2;
        const ly = positions[kids[kids.length - 1]].y + NODE_H / 2;
        const ny = (fy + ly) / 2 - NODE_H / 2;

        positions[nodeId] = { x: depth * (NODE_W + H_GAP), y: ny };
        return Math.max(NODE_H, yOff);
    }

    function shiftSubtree(nodeId, childMap, positions, dx, dy) {
        const p = positions[nodeId];
        if (p) { p.x += dx; p.y += dy; }
        (childMap[nodeId] || []).forEach(cid => shiftSubtree(cid, childMap, positions, dx, dy));
    }

    // ── Detail Pane ───────────────────────────────────────────────────────────

    function createPane(container) {
        const pane = document.createElement('div');
        pane.className = 'qp-v2-pane';
        container.appendChild(pane);

        pane.addEventListener('mouseenter', () => cancelFade(pane));
        pane.addEventListener('mouseleave', () => startFade(pane));

        return pane;
    }

    function showPane(pane, node, nodeEl) {
        cancelFade(pane);
        populatePane(pane, node);

        // Position pane near the hovered operator instead of fixed top-right
        if (nodeEl) {
            const containerRect = pane.parentElement.getBoundingClientRect();
            const nodeRect      = nodeEl.getBoundingClientRect();
            const scrollLeft    = pane.parentElement.scrollLeft || 0;
            const scrollTop     = pane.parentElement.scrollTop  || 0;

            // Place pane to the right of the node, vertically aligned to its top
            let left = (nodeRect.right - containerRect.left + scrollLeft) + 12;
            let top  = (nodeRect.top   - containerRect.top  + scrollTop);

            // If pane would overflow right edge, place it to the left of the node
            if (left + 430 > pane.parentElement.scrollWidth)
                left = (nodeRect.left - containerRect.left + scrollLeft) - 430 - 12;

            // Clamp top so pane stays visible
            if (top < 4) top = 4;

            pane.style.top   = top  + 'px';
            pane.style.left  = left + 'px';
            pane.style.right = 'auto';

            // After paint, clamp bottom edge so pane doesn't overflow the container
            requestAnimationFrame(() => {
                const paneH    = pane.offsetHeight;
                const maxTop   = (pane.parentElement.clientHeight || pane.parentElement.scrollHeight) - paneH - 4;
                const clamped  = Math.max(4, Math.min(top, maxTop));
                if (clamped !== top) pane.style.top = clamped + 'px';
            });
        }

        pane.style.transition = 'opacity 0.12s ease';
        pane.style.opacity    = '1';
        pane.style.pointerEvents = 'all';
    }

    function startFade(pane) {
        // Remove any previous transitionend listener then begin 1.5 s fade
        pane._onFadeEnd && pane.removeEventListener('transitionend', pane._onFadeEnd);
        pane._onFadeEnd = function () {
            if (parseFloat(pane.style.opacity) < 0.01)
                pane.style.pointerEvents = 'none';
            pane.removeEventListener('transitionend', pane._onFadeEnd);
            pane._onFadeEnd = null;
        };
        pane.addEventListener('transitionend', pane._onFadeEnd);
        pane.style.transition = 'opacity 2.5s ease';
        pane.style.opacity    = '0';
    }

    function cancelFade(pane) {
        // Freeze at current computed opacity, then snap to fully visible
        const live = parseFloat(window.getComputedStyle(pane).opacity);
        pane._onFadeEnd && pane.removeEventListener('transitionend', pane._onFadeEnd);
        pane._onFadeEnd = null;
        pane.style.transition = 'none';
        pane.style.opacity    = String(isNaN(live) ? 1 : live);
        // Force reflow so the transition:none takes effect before we change opacity
        pane.getBoundingClientRect();
        pane.style.transition = 'opacity 0.12s ease';
        pane.style.opacity    = '1';
        pane.style.pointerEvents = 'all';
    }

    function populatePane(pane, node) {
        const displayPct = node._individualPct ?? node.relativeCost;
        const cc         = costColor(displayPct);

        pane.innerHTML = '';

        // ── Header ────────────────────────────────────────────────────────────
        const hdr = document.createElement('div');
        hdr.className = 'qp-v2-pane-hdr';
        hdr.style.borderLeft = '4px solid ' + cc;

        const titleEl = document.createElement('span');
        titleEl.className   = 'qp-v2-pane-title';
        titleEl.textContent = node.physicalType || node.type;

        const copyBtn = document.createElement('button');
        copyBtn.className = 'qp-v2-copy-btn';
        copyBtn.title     = 'Copy to clipboard';
        copyBtn.innerHTML = '<i class="fa-solid fa-copy"></i>';

        hdr.append(titleEl, copyBtn);
        pane.appendChild(hdr);

        // ── Table ─────────────────────────────────────────────────────────────
        const rows   = buildRows(node, displayPct);
        const tbl    = document.createElement('table');
        tbl.className = 'qp-v2-pane-table';

        rows.forEach(([label, value, cls]) => {
            if (value == null || value === '') return;
            const tr = document.createElement('tr');
            if (cls) tr.className = cls;
            const th = document.createElement('th');
            th.textContent = label;
            const td = document.createElement('td');
            // Suggested Index renders as a <pre> code block
            if (cls === 'pane-row-index') {
                const pre = document.createElement('pre');
                pre.textContent = value;
                td.appendChild(pre);
            } else {
                td.textContent = value;
            }
            tr.append(th, td);
            tbl.appendChild(tr);
        });
        pane.appendChild(tbl);

        // ── Copy action ───────────────────────────────────────────────────────
        copyBtn.addEventListener('click', () => {
            const text = (node.physicalType || node.type) + '\n'
                + rows.filter(([, v]) => v != null && v !== '')
                      .map(([k, v]) => k.padEnd(22) + v)
                      .join('\n');
            navigator.clipboard.writeText(text).catch(() => {});
            copyBtn.innerHTML = '<i class="fa-solid fa-check"></i>';
            setTimeout(() => { copyBtn.innerHTML = '<i class="fa-solid fa-copy"></i>'; }, 1800);
        });
    }

    /**
     * Builds a CREATE NONCLUSTERED INDEX suggestion from a node's Predicate,
     * Seek Predicate, and Output fields.
     *
     * Algorithm:
     *  1. Extract all bracketed column chains ([a].[b].[c].[col]) from Predicate
     *     and Seek Predicate; take the last bracket segment as the column name.
     *  2. If the word "Scalar" appears before a column in the text, mark it scalar
     *     (scalars sort first — they are equality predicates).
     *  3. Deduplicate across Predicate + Seek Predicate.
     *  4. Extract Output columns (handles bare "table.col" and "[table].[col]").
     *  5. Remove from Output any column already in the key list.
     *  6. If no INCLUDE columns remain, omit INCLUDE().
     */
    function buildSuggestedIndex(node) {
        const table    = node.properties && node.properties['Table'];
        if (!table) return null;

        const pred     = node.predicate || '';
        const seekPred = (node.properties && node.properties['Seek Predicate']) || '';
        const output   = (node.properties && node.properties['Output'])         || '';

        // ── Extract bracketed column references from a predicate string ───────
        // Matches chains like [msdb].[dbo].[backupset].[colName]
        // Takes the LAST [segment] as the column name.
        // Marks a column "scalar" if "Scalar" appears in the text immediately before it
        // (not after AND/OR which begins a new predicate clause).
        function extractKeyCols(text) {
            const results  = [];
            const seen     = new Set();
            const chainRe  = /(\[[\w\s#$@]+\](?:\.\[[\w\s#$@]+\])+)/g;
            let   lastPos  = 0;
            let   m;
            while ((m = chainRe.exec(text)) !== null) {
                const chain  = m[1];
                const before = text.slice(lastPos, m.index);
                // "scalar" between the previous match end and this match start,
                // but not as part of "AND"/"OR" that resets context
                const clauseBefore = before.split(/\bAND\b|\bOR\b/i).pop() || '';
                const isScalar = /scalar/i.test(clauseBefore);
                lastPos = chainRe.lastIndex;

                const parts = chain.match(/\[[\w\s#$@]+\]/g);
                if (!parts || parts.length < 1) continue;
                const colBracketed = parts[parts.length - 1];          // e.g. [is_copy_only]
                const colKey       = colBracketed.toLowerCase();

                if (!seen.has(colKey)) {
                    seen.add(colKey);
                    results.push({ col: colBracketed, scalar: isScalar });
                } else if (isScalar) {
                    // Upgrade existing entry to scalar
                    const existing = results.find(r => r.col.toLowerCase() === colKey);
                    if (existing) existing.scalar = true;
                }
            }
            return results;
        }

        // ── Extract column names from Output (bare or bracketed) ─────────────
        // Output looks like: "backupset.col1, backupset.col2" or "[tbl].[col]"
        function extractOutputCols(text) {
            const cols = [];
            const seen = new Set();
            // First try bracketed chains
            const chainRe = /(\[[\w\s#$@]+\](?:\.\[[\w\s#$@]+\])+)/g;
            let m;
            while ((m = chainRe.exec(text)) !== null) {
                const parts = m[1].match(/\[[\w\s#$@]+\]/g);
                if (!parts) continue;
                const col = parts[parts.length - 1].replace(/^\[|\]$/g, ''); // strip brackets
                const key = col.toLowerCase();
                if (!seen.has(key)) { seen.add(key); cols.push(col); }
            }
            // Also handle bare "table.col" tokens not covered by bracketed regex
            text.split(/[\s,]+/).forEach(token => {
                token = token.trim();
                if (!token || token.startsWith('[')) return;
                const parts = token.split('.');
                const col   = parts[parts.length - 1].trim();
                const key   = col.toLowerCase();
                if (col && !seen.has(key)) { seen.add(key); cols.push(col); }
            });
            return cols;
        }

        // ── Merge predicate + seek predicate columns ──────────────────────────
        const predCols     = extractKeyCols(pred);
        const seekPredCols = extractKeyCols(seekPred);
        const keyMap       = new Map();   // normalised-key → { col, scalar }

        [...predCols, ...seekPredCols].forEach(({ col, scalar }) => {
            const k = col.toLowerCase();
            if (!keyMap.has(k)) keyMap.set(k, { col, scalar });
            else if (scalar)    keyMap.get(k).scalar = true;
        });

        if (keyMap.size === 0) return null;

        // Scalars first, then normal
        const allKeyEntries = [...keyMap.values()];
        const keyColsFinal  = [
            ...allKeyEntries.filter(x =>  x.scalar).map(x => x.col),
            ...allKeyEntries.filter(x => !x.scalar).map(x => x.col),
        ];

        // ── Build INCLUDE (output columns not already in keys) ────────────────
        const keyKeys     = new Set([...keyMap.keys()]);
        const outputCols  = extractOutputCols(output);
        const includeCols = outputCols.filter(c => !keyKeys.has(c.toLowerCase()) &&
                                                   !keyKeys.has('[' + c.toLowerCase() + ']'));

        // ── Build DDL ─────────────────────────────────────────────────────────
        const keyLines = keyColsFinal.map((c, i) => (i === 0 ? '  ' + c : '  ,' + c)).join('\n');
        let ddl = `CREATE NONCLUSTERED INDEX [IX_SQLDBA_]\nON ${table}\n(\n${keyLines}\n)`;
        if (includeCols.length > 0)
            ddl += `\nINCLUDE(${includeCols.join(', ')})`;
        ddl += `\nWITH(DATA_COMPRESSION=PAGE)`;

        return ddl;
    }

    /**
     * Returns [ [label, value, optionalRowClass], ... ]
     * Rows with a null/empty value are filtered out by populatePane.
     */
    function buildRows(node, displayPct) {
        const rows = [];

        rows.push(['Node ID',         String(node.id)]);

        // Show logical op only when it differs from physical
        if (node.type && node.physicalType && node.type !== node.physicalType)
            rows.push(['Logical Op', node.type]);

        // Full object path: [db].[schema].[table].[index]
        if (node.subtext && node.subtext.length > 0) {
            const parts = [];
            if (node.objectDb)     parts.push('[' + node.objectDb     + ']');
            if (node.objectSchema) parts.push('[' + node.objectSchema + ']');
            node.subtext.forEach(s => parts.push('[' + s + ']'));
            rows.push(['Object', parts.join('.')]);
        }

        rows.push(['Est. Rows',       fmt(node.estimateRows)]);
        if (node.actualRows != null)
            rows.push(['Actual Rows', fmt(node.actualRows)]);
        rows.push(['Est. Executions', fmt(node.estimateExecutions ?? 1)]);
        rows.push(['Avg Row Size',    node.avgRowSize + ' B']);

        rows.push(['CPU Cost',        (+node.estimateCPU).toFixed(6)]);
        rows.push(['I/O Cost',        (+node.estimateIO ).toFixed(6)]);
        rows.push(['Operator Cost',   (+node.cost       ).toFixed(6)]);
        rows.push(['Subtree Cost',    (+node.subTreeCost).toFixed(6)]);
        rows.push(['Relative Cost',   displayPct.toFixed(1) + '%']);

        if (node.isParallel)
            rows.push(['Exec. Mode', 'Parallel']);

        // ── Dynamic properties from the parser ──────────────────────────────────
        const propOrder = [
            'Actual Executions', 'Actual Elapsed (ms)', 'Actual CPU (ms)',
            'Actual Rows Read', 'Actual Scans',
            'Actual Logical Reads', 'Actual Physical Reads', 'Actual Read-Ahead',
            'Thread Count',
            'Est. Rows Read', 'Table Cardinality', 'Estimated Rebinds', 'Estimated Rewinds',
            'Ordered', 'Scan Ordered', 'Scan Direction', 'Storage',
            'Table', 'Index', 'Lookup', 'Forced Index', 'Force Scan', 'NOEXPAND Hint',
            'Seek Predicate', 'Output', 'Defined Values',
            'Hash Keys (Probe)', 'Hash Keys (Build)', 'Probe Column', 'Build Column',
            'Order By', 'Group By', 'Partition Columns', 'Outer References',
            'Required Memory (KB)', 'Desired Memory (KB)', 'Granted Memory (KB)', 'Max Used Memory (KB)',
            'Warning Details'
        ];
        if (node.properties) {
            const shown = new Set();
            propOrder.forEach(key => {
                if (node.properties[key] != null) {
                    const cls = (key === 'Seek Predicate' || key === 'Output' ||
                                 key === 'Defined Values' || key === 'Warning Details' ||
                                 key === 'Outer References')
                        ? 'pane-row-predicate' : undefined;
                    rows.push([key, node.properties[key], cls]);
                    shown.add(key);
                }
            });
            Object.keys(node.properties).forEach(key => {
                if (!shown.has(key))
                    rows.push([key, node.properties[key]]);
            });
        }

        if (node.predicate)
            rows.push(['Predicate', node.predicate, 'pane-row-predicate']);

        const suggestedIdx = buildSuggestedIndex(node);
        if (suggestedIdx)
            rows.push(['Suggested Index', suggestedIdx, 'pane-row-index']);

        if (node.badges && node.badges.length > 0)
            rows.push(['Warnings', node.badges.join(', ')]);

        return rows;
    }

    // ── Main render ───────────────────────────────────────────────────────────

    function render(container, graph) {
        if (!graph.nodes || graph.nodes.length === 0) {
            container.innerHTML =
                '<p style="color:#888;padding:24px;font-size:12px;font-family:Consolas,monospace;">' +
                'No execution plan operators found. The plan XML may be empty or in an unsupported format.</p>';
            return;
        }

        // Container must be position:relative so the pane can be absolute
        container.style.position = 'relative';

        // ── Statement header ──────────────────────────────────────────────────
        if (graph.query) {
            const hdr = document.createElement('div');
            hdr.className = 'qp-v2-stmt-hdr';

            const lbl = document.createElement('span');
            lbl.className   = 'qp-v2-stmt-label';
            lbl.textContent = 'SQL';

            const sql = document.createElement('span');
            sql.className   = 'qp-v2-stmt-sql';
            sql.textContent = graph.query.replace(/\s+/g, ' ').trim();

            hdr.append(lbl, sql);
            container.appendChild(hdr);
        }

        // Build lookup structures
        const nodeMap  = {};
        const childMap = {};

        graph.nodes.forEach(n => {
            nodeMap[n.id]  = n;
            childMap[n.id] = [];
        });
        graph.edges.forEach(e => {
            if (childMap[e.source] !== undefined) childMap[e.source].push(e.target);
        });

        // Compute individual node cost = own subtree% minus sum of direct children subtree%
        // (matches SSMS "Cost: N %" — each operator's own contribution, not cumulative)
        graph.nodes.forEach(n => {
            const childSum = (childMap[n.id] || [])
                .reduce((sum, cid) => sum + (nodeMap[cid] ? (nodeMap[cid].relativeCost || 0) : 0), 0);
            n._individualPct = Math.max(0, (n.relativeCost || 0) - childSum);
        });

        // Find root (no incoming edges)
        const hasParent = new Set(graph.edges.map(e => e.target));
        const roots     = graph.nodes.filter(n => !hasParent.has(n.id));
        if (roots.length === 0) return;
        const rootId = roots[0].id;

        // Compute layout
        const positions = {};
        computeLayout(rootId, childMap, positions, 0);

        // Normalise to (0,0)
        const allPos = Object.values(positions);
        const minX   = Math.min(...allPos.map(p => p.x));
        const minY   = Math.min(...allPos.map(p => p.y));
        allPos.forEach(p => { p.x -= minX; p.y -= minY; });

        const canvasW = Math.max(...allPos.map(p => p.x)) + NODE_W + PAD * 2;
        const canvasH = Math.max(...allPos.map(p => p.y)) + NODE_H + PAD * 2;

        // ── Recommendations banner ────────────────────────────────────────────
        if (graph.recommendations && graph.recommendations.length > 0) {
            const banner = document.createElement('div');
            banner.className = 'qp-v2-banner';
            graph.recommendations.forEach((rec, i) => {
                if (i > 0) banner.appendChild(document.createElement('hr'));
                const pre = document.createElement('pre');
                pre.className = 'qp-v2-banner-rec';
                pre.textContent = rec;
                banner.appendChild(pre);
            });
            container.appendChild(banner);
        }

        // ── Zoom/pan viewport ─────────────────────────────────────────────────
        const viewport = document.createElement('div');
        viewport.className = 'qp-v2-viewport';
        container.appendChild(viewport);

        // Toolbar overlay (zoom controls)
        const toolbar = document.createElement('div');
        toolbar.className = 'qp-v2-toolbar';
        toolbar.innerHTML =
            '<button class="qp-v2-tb-btn" data-action="zoomin"  title="Zoom in">+</button>' +
            '<button class="qp-v2-tb-btn" data-action="zoomout" title="Zoom out">−</button>' +
            '<button class="qp-v2-tb-btn" data-action="fit"     title="Fit / reset">⤢</button>';
        viewport.appendChild(toolbar);

        // ── Canvas wrapper ────────────────────────────────────────────────────
        const wrap = document.createElement('div');
        wrap.style.cssText = `position:relative;width:${canvasW}px;height:${canvasH}px;transform-origin:0 0;`;
        viewport.appendChild(wrap);

        // ── SVG edge layer (behind nodes) ─────────────────────────────────────
        const SVG_NS = 'http://www.w3.org/2000/svg';
        const svg    = document.createElementNS(SVG_NS, 'svg');
        svg.setAttribute('width',  canvasW);
        svg.setAttribute('height', canvasH);
        svg.style.cssText = 'position:absolute;top:0;left:0;pointer-events:none;overflow:visible;';
        wrap.appendChild(svg);

        const maxRows = Math.max(1, ...graph.edges.map(e => e.rowCount || 1));

        graph.edges.forEach(edge => {
            const sp = positions[edge.source];
            const tp = positions[edge.target];
            if (!sp || !tp) return;

            const x1 = tp.x + PAD + NODE_W;
            const y1 = tp.y + PAD + NODE_H / 2;
            const x2 = sp.x + PAD;
            const y2 = sp.y + PAD + NODE_H / 2;
            const mx = (x1 + x2) / 2;

            const rows  = Math.max(1, edge.rowCount || 1);
            const width = Math.max(1.5, Math.min(12,
                1.5 + (Math.log10(rows + 1) / Math.log10(maxRows + 1)) * 10.5));

            const path = document.createElementNS(SVG_NS, 'path');
            path.setAttribute('d',             `M ${x1} ${y1} C ${mx} ${y1} ${mx} ${y2} ${x2} ${y2}`);
            path.setAttribute('fill',          'none');
            path.setAttribute('stroke',        '#5a5a5a');
            path.setAttribute('stroke-width',  width);
            path.setAttribute('stroke-linecap', 'round');
            svg.appendChild(path);

            if (rows > 1) {
                const txt = document.createElementNS(SVG_NS, 'text');
                txt.setAttribute('x',           mx);
                txt.setAttribute('y',           (y1 + y2) / 2 - width / 2 - 3);
                txt.setAttribute('text-anchor', 'middle');
                txt.setAttribute('font-size',   '9');
                txt.setAttribute('fill',        '#888');
                txt.textContent = fmt(rows);
                svg.appendChild(txt);
            }
        });

        // ── Shared detail pane (one per render) ───────────────────────────────
        const pane = createPane(container);

        // ── Node divs ─────────────────────────────────────────────────────────
        graph.nodes.forEach(node => {
            const pos    = positions[node.id];
            if (!pos) return;

            const displayPct = node._individualPct ?? node.relativeCost;
            const cc         = costColor(displayPct);

            const nx = pos.x + PAD;
            const ny = pos.y + PAD;

            const group = document.createElement('div');
            group.className = 'qp-v2-group';
            group.style.left = nx + 'px';
            group.style.top  = ny + 'px';

            const nodeDiv = document.createElement('div');
            nodeDiv.className = 'qp-node-v2';

            // Cost bar (top stripe)
            const bar = document.createElement('div');
            bar.className        = 'qp-v2-costbar';
            bar.style.background = cc;
            nodeDiv.appendChild(bar);

            // Body: icon + labels
            const body = document.createElement('div');
            body.className = 'qp-v2-body';

            const icon = document.createElement('div');
            icon.className  = iconClass(node.physicalType || node.type);
            icon.style.cssText = 'flex-shrink:0;width:32px;height:32px;position:relative;margin:0;';

            if (node.badges.includes('Warning') || node.badges.includes('CriticalWarning')) {
                const w = document.createElement('div');
                w.className = 'qp-iconwarn';
                icon.appendChild(w);
            }
            if (node.badges.includes('Parallelism')) {
                const p = document.createElement('div');
                p.className = 'qp-iconpar';
                icon.appendChild(p);
            }
            body.appendChild(icon);

            const labels = document.createElement('div');
            labels.className = 'qp-v2-labels';

            const nameEl = document.createElement('div');
            nameEl.className   = 'qp-v2-name';
            nameEl.textContent = node.physicalType || node.type;
            labels.appendChild(nameEl);

            if (node.subtext && node.subtext.length > 0) {
                const sub = document.createElement('div');
                sub.className   = 'qp-v2-sub';
                sub.textContent = node.subtext[0];
                labels.appendChild(sub);
            }

            const costEl = document.createElement('div');
            costEl.className   = 'qp-v2-cost';
            costEl.style.color = cc;
            costEl.textContent = 'Cost: ' + displayPct.toFixed(1) + '%';
            labels.appendChild(costEl);

            body.appendChild(labels);
            nodeDiv.appendChild(body);
            group.appendChild(nodeDiv);
            wrap.appendChild(group);

            // ── Hover events → shared pane ────────────────────────────────────
            nodeDiv.addEventListener('mouseenter', () => { if (!_dragging) showPane(pane, node, nodeDiv); });
            nodeDiv.addEventListener('mouseleave', () => startFade(pane));
        });

        // ── Pan / zoom state ──────────────────────────────────────────────────
        let _tx = 0, _ty = 0, _scale = 1;
        let _dragging = false, _lastX = 0, _lastY = 0;

        function applyXform() {
            wrap.style.transform = `translate(${_tx}px,${_ty}px) scale(${_scale})`;
        }

        function clampScale(s) { return Math.max(0.12, Math.min(5, s)); }

        // Zoom centred on a viewport-relative point (vx, vy)
        function zoomAt(vx, vy, factor) {
            const ns = clampScale(_scale * factor);
            _tx = vx - (vx - _tx) * (ns / _scale);
            _ty = vy - (vy - _ty) * (ns / _scale);
            _scale = ns;
            applyXform();
        }

        function resetFit() {
            _tx = 0; _ty = 0; _scale = 1;
            applyXform();
        }

        // Wheel zoom
        viewport.addEventListener('wheel', e => {
            e.preventDefault();
            const r   = viewport.getBoundingClientRect();
            const f   = e.deltaY < 0 ? 1.12 : 1 / 1.12;
            zoomAt(e.clientX - r.left, e.clientY - r.top, f);
        }, { passive: false });

        // Drag pan
        viewport.addEventListener('mousedown', e => {
            if (e.button !== 0) return;
            _dragging = true;
            _lastX = e.clientX; _lastY = e.clientY;
            viewport.style.cursor = 'grabbing';
            e.preventDefault();
        });

        // Listen on window so drag continues even if cursor leaves viewport
        const onMove = e => {
            if (!_dragging) return;
            _tx += e.clientX - _lastX;
            _ty += e.clientY - _lastY;
            _lastX = e.clientX; _lastY = e.clientY;
            applyXform();
        };
        const onUp = () => {
            if (_dragging) { _dragging = false; viewport.style.cursor = ''; }
        };
        window.addEventListener('mousemove', onMove);
        window.addEventListener('mouseup',   onUp);

        // Double-click to reset
        viewport.addEventListener('dblclick', resetFit);

        // Toolbar buttons
        toolbar.addEventListener('click', e => {
            const btn = e.target.closest('[data-action]');
            if (!btn) return;
            const r = viewport.getBoundingClientRect();
            const cx = r.width / 2, cy = r.height / 2;
            if      (btn.dataset.action === 'zoomin')  zoomAt(cx, cy, 1.25);
            else if (btn.dataset.action === 'zoomout') zoomAt(cx, cy, 1 / 1.25);
            else if (btn.dataset.action === 'fit')     resetFit();
        });

        // Cleanup listeners when container is re-used (next plan render clears innerHTML)
        const obs = new MutationObserver(() => {
            if (!viewport.isConnected) {
                window.removeEventListener('mousemove', onMove);
                window.removeEventListener('mouseup',   onUp);
                obs.disconnect();
            }
        });
        obs.observe(container, { childList: true });
    }

    // ── Public API ────────────────────────────────────────────────────────────
    return {
        showPlan: function (containerId, jsonData) {
            const container = document.getElementById(containerId);
            if (!container) return;
            container.innerHTML = '';
            try {
                const graph = typeof jsonData === 'string' ? JSON.parse(jsonData) : jsonData;
                render(container, graph);
            } catch (e) {
                container.innerHTML =
                    '<p style="color:#f44336;padding:16px;">Failed to render query plan: ' + e.message + '</p>';
            }
        },

        showParseError: function (containerId, message) {
            const container = document.getElementById(containerId);
            if (!container) return;
            container.innerHTML =
                '<p style="color:#f44336;padding:24px;font-size:12px;font-family:Consolas,monospace;">' +
                'Plan parser error: ' + (message || 'unknown error') + '</p>';
        },

        clearPlan: function (containerId) {
            const el = document.getElementById(containerId);
            if (el) el.innerHTML = '';
        }
    };
})();
