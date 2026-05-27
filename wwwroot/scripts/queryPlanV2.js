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

    // ── Helpers ──────────────────────────────────────────────────────────────
    function escHtml(s) {
        var d = document.createElement('div');
        d.textContent = s;
        return d.innerHTML;
    }

    // Clipboard helper — falls back to textarea trick when Clipboard API is unavailable
    // (non-secure context: LAN server mode accessed over plain HTTP)
    function copyToClipboard(text) {
        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(text).catch(() => clipboardFallback(text));
        } else {
            clipboardFallback(text);
        }
    }
    function clipboardFallback(text) {
        const ta = document.createElement('textarea');
        ta.value = text;
        ta.style.cssText = 'position:fixed;top:0;left:0;opacity:0;';
        document.body.appendChild(ta);
        ta.focus();
        ta.select();
        try { document.execCommand('copy'); } catch (_) {}
        document.body.removeChild(ta);
    }

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

    // ── V2 Icon set (individual PNGs from vscode-mssql) ──────────────────────
    // Set to true to use higher-quality individual PNG icons instead of sprite sheet.
    // Set to false to revert to legacy sprite-based icons (coloured).
    // Controlled at runtime via queryPlanInteropV2.setUseV2Icons(bool).
    let USE_V2_ICONS = false;

    // V2 map — same keys as ICON_MAP but values are qp-icon2-* suffixes
    // matching the CSS classes in qp_icons_v2.css.
    const ICON_MAP_V2 = {
        'Adaptive Join':             'AdaptiveJoin',
        'Clustered Index Scan':      'ClusteredIndexScan',
        'Clustered Index Seek':      'ClusteredIndexSeek',
        'Clustered Index Delete':    'ClusteredIndexDelete',
        'Clustered Index Insert':    'ClusteredIndexInsert',
        'Clustered Index Update':    'ClusteredIndexUpdate',
        'Clustered Index Merge':     'ClusteredIndexMerge',
        'Clustered Update':          'ClusteredUpdate',
        'Columnstore Index Scan':    'ColumnstoreIndexScan',
        'Columnstore Index Delete':  'ColumnstoreIndexDelete',
        'Columnstore Index Insert':  'ColumnstoreIndexInsert',
        'Columnstore Index Merge':   'ColumnstoreIndexMerge',
        'Columnstore Index Update':  'ColumnstoreIndexUpdate',
        'Index Scan':                'IndexScan',
        'Index Seek':                'IndexSeek',
        'Index Delete':              'IndexDelete',
        'Index Insert':              'IndexInsert',
        'Index Update':              'IndexUpdate',
        'Index Spool':               'IndexSpool',
        'Table Scan':                'TableScan',
        'Table Delete':              'TableDelete',
        'Table Insert':              'TableInsert',
        'Table Update':              'TableUpdate',
        'Table Merge':               'TableMerge',
        'Table Spool':               'TableSpool',
        'Key Lookup':                'KeyLookup',
        'RID Lookup':                'RIDLookup',
        'Bookmark Lookup':           'BookmarkLookup',
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
        'Lazy Spool':                'Spool',
        'Eager Spool':               'Spool',
        'Assert':                    'Assert',
        'Constant Scan':             'ConstantScan',
        'Remote Query':              'RemoteQuery',
        'Remote Scan':               'RemoteScan',
        'Remote Index Scan':         'RemoteIndexScan',
        'Remote Index Seek':         'RemoteIndexSeek',
        'Remote Insert':             'RemoteInsert',
        'Remote Delete':             'RemoteDelete',
        'Remote Update':             'RemoteUpdate',
        'Aggregate':                 'Aggregate',
        'Table Valued Function':     'TableValuedFunction',
        'Table-valued function':     'TableValuedFunction',
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
        'Statement':                 'SQL',
        'Parameter Table Scan':      'ParameterTableScan',
        'UDX':                       'UDX',
        'Split':                     'Split',
        'Window Aggregate':          'WindowAggregate',
        'Assign':                    'Assign',
        'Convert':                   'Convert',
        'Declare':                   'Declare',
        'If':                        'If',
        'Intrinsic':                 'Intrinsic',
        'Predict':                   'Predict',
        'Rank':                      'Rank',
        'Switch':                    'Switch',
        'Trim':                      'Trim',
        'Union':                     'Union',
        'Union All':                 'UnionAll',
        'Apply':                     'Apply',
        'Foreign Key References Check': 'ForeignKeyReferencesCheck',
    };

    function iconClass(type) {
        if (USE_V2_ICONS) {
            return 'qp-icon2-' + (ICON_MAP_V2[type] || 'IteratorCatchAll');
        }
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
        
        // Drag handle
        const dragHandle = document.createElement('div');
        dragHandle.className = 'qp-v2-pane-drag-handle';
        dragHandle.title = 'Drag to move';
        pane.appendChild(dragHandle);
        
        // Dragging state
        let isDragging = false;
        let dragOffsetX = 0;
        let dragOffsetY = 0;
        
        dragHandle.addEventListener('mousedown', (e) => {
            isDragging = true;
            dragOffsetX = e.clientX - pane.getBoundingClientRect().left;
            dragOffsetY = e.clientY - pane.getBoundingClientRect().top;
            pane.style.opacity = '0.9';
            pane.style.zIndex = '500';
            e.preventDefault();
            e.stopPropagation();
        });
        
        document.addEventListener('mousemove', (e) => {
            if (!isDragging) return;
            const containerRect = pane.parentElement.getBoundingClientRect();
            const scrollLeft = pane.parentElement.scrollLeft || 0;
            const scrollTop = pane.parentElement.scrollTop || 0;
            
            let x = e.clientX - containerRect.left + scrollLeft - dragOffsetX;
            let y = e.clientY - containerRect.top + scrollTop - dragOffsetY;
            
            // Keep pane inside viewport
            x = Math.max(0, Math.min(x, containerRect.width - pane.offsetWidth));
            y = Math.max(0, Math.min(y, containerRect.height - pane.offsetHeight));
            
            pane.style.left = x + 'px';
            pane.style.top = y + 'px';
            pane.style.right = 'auto';
        });
        
        document.addEventListener('mouseup', () => {
            if (isDragging) {
                isDragging = false;
                pane.style.opacity = '';
                pane.style.zIndex = '';
            }
        });

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
            if (left + 670 > pane.parentElement.scrollWidth)
                left = (nodeRect.left - containerRect.left + scrollLeft) - 670 - 12;

            // Clamp top so pane stays visible
            if (top < 4) top = 4;

            pane.style.top   = top  + 'px';
            pane.style.left  = left + 'px';
            pane.style.right = 'auto';

            // After paint, clamp bottom edge so pane doesn't overflow the viewport
            requestAnimationFrame(() => {
                const paneH    = pane.offsetHeight;
                const viewportH = pane.parentElement.clientHeight;
                // Add extra 16px margin at bottom to avoid modal window border overlap
                const maxTop   = viewportH - paneH - 20;
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

        // ── Table (4-column grid: short props paired 2-per-row; wide rows span all) ──
        const rows = buildRows(node, displayPct);
        const tbl  = document.createElement('table');
        tbl.className = 'qp-v2-pane-table';

        // Wide rows get their own full-width row; short rows are paired left/right
        const WIDE_CLASSES = new Set(['pane-row-predicate', 'pane-row-index']);
        const WIDE_LABELS  = new Set(['Object', 'Warnings', 'Warning Details', 'Suggested Index',
                                      'Predicate', 'Seek Predicate', 'Output', 'Defined Values',
                                      'Outer References', 'Order By', 'Group By',
                                      'Partition Columns', 'Hash Keys (Probe)', 'Hash Keys (Build)']);

        const filteredRows = rows.filter(([, v]) => v != null && v !== '');
        let i = 0;
        while (i < filteredRows.length) {
            const [label, value, cls] = filteredRows[i];
            const isWide = cls && WIDE_CLASSES.has(cls) || WIDE_LABELS.has(label);

            if (isWide) {
                // Full-width row spanning all 4 columns
                const tr = document.createElement('tr');
                if (cls) tr.className = cls + ' pane-row-wide';
                else tr.className = 'pane-row-wide';
                const th = document.createElement('th');
                th.textContent = label;
                const td = document.createElement('td');
                td.colSpan = 3;
                if (cls === 'pane-row-index') {
                    const pre = document.createElement('pre');
                    pre.textContent = value;
                    td.appendChild(pre);
                } else {
                    td.textContent = value;
                }
                tr.append(th, td);
                tbl.appendChild(tr);
                i++;
            } else {
                // Pair two short rows into one 4-column table row
                const tr = document.createElement('tr');
                const th1 = document.createElement('th'); th1.textContent = label;
                const td1 = document.createElement('td'); td1.textContent = value;
                tr.append(th1, td1);

                const next = filteredRows[i + 1];
                const nextIsWide = next && (
                    (next[2] && WIDE_CLASSES.has(next[2])) || WIDE_LABELS.has(next[0])
                );
                if (next && !nextIsWide) {
                    const th2 = document.createElement('th'); th2.textContent = next[0];
                    const td2 = document.createElement('td'); td2.textContent = next[1];
                    tr.append(th2, td2);
                    i += 2;
                } else {
                    // Last odd row — span remaining columns
                    td1.colSpan = 3;
                    i++;
                }
                tbl.appendChild(tr);
            }
        }
        pane.appendChild(tbl);

        // ── Copy action ───────────────────────────────────────────────────────
        copyBtn.addEventListener('click', () => {
            const text = (node.physicalType || node.type) + '\n'
                + rows.filter(([, v]) => v != null && v !== '')
                      .map(([k, v]) => k.padEnd(22) + v)
                      .join('\n');
            copyToClipboard(text);
            copyBtn.innerHTML = '<i class="fa-solid fa-check"></i>';
            setTimeout(() => { copyBtn.innerHTML = '<i class="fa-solid fa-copy"></i>'; }, 1800);
        });

        // ── Index action bar: shown whenever a suggested index exists ────────
        const suggestedRow = rows.find(([label]) => label === 'Suggested Index');
        if (suggestedRow) {
            const execBar = document.createElement('div');
            execBar.style.cssText = 'padding:8px 10px 6px;border-top:1px solid rgba(239,68,68,0.3);display:flex;align-items:center;gap:8px;flex-wrap:wrap;';

            // Copy DDL button — always available
            const copyDdlBtn = document.createElement('button');
            copyDdlBtn.className   = 'qp-exec-btn';
            copyDdlBtn.style.cssText = 'background:rgba(99,102,241,0.15);border-color:rgba(99,102,241,0.4);';
            copyDdlBtn.textContent = 'Copy DDL';
            copyDdlBtn.title       = 'Copy CREATE INDEX statement to clipboard';
            copyDdlBtn.addEventListener('click', () => {
                copyToClipboard(suggestedRow[1]);
                copyDdlBtn.textContent = 'Copied ✓';
                setTimeout(() => { copyDdlBtn.textContent = 'Copy DDL'; }, 1800);
            });
            execBar.appendChild(copyDdlBtn);

            // Execute on server — only when No-Pants .NET ref is registered
            if (_dotNetRef) {
                const execBtn = document.createElement('button');
                execBtn.className   = 'qp-exec-btn';
                execBtn.textContent = 'Execute on server';
                execBtn.title       = 'No-Pants: run CREATE INDEX against the connected server';

                const statusEl = document.createElement('span');
                statusEl.style.cssText = 'font-size:11px;opacity:0.75;display:none;';

                execBar.append(execBtn, statusEl);

                execBtn.addEventListener('click', async () => {
                    execBtn.disabled    = true;
                    execBtn.textContent = 'Running…';
                    statusEl.style.display = 'inline';
                    statusEl.textContent   = '';
                    try {
                        await _dotNetRef.invokeMethodAsync('ExecuteIndexFromOperator', suggestedRow[1]);
                        execBtn.textContent    = 'Done ✓';
                        statusEl.textContent   = 'Index created';
                    } catch (e) {
                        execBtn.textContent    = 'Failed';
                        execBtn.disabled       = false;
                        statusEl.textContent   = e.message || 'error';
                    }
                });
            }

            pane.appendChild(execBar);
        }
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
        rows.push(['Operator Cost',   (+node.cost).toFixed(6) + ' (' + displayPct.toFixed(1) + '%)']);
        rows.push(['Subtree Cost',    (+node.subTreeCost).toFixed(6)]);

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

        // ── Plan Summary panel ───────────────────────────────────────────────
        if (graph.summary) {
            const s = graph.summary;
            const panel = document.createElement('div');
            panel.className = 'qp-v2-summary';

            // Header (collapsible)
            const header = document.createElement('div');
            header.className = 'qp-v2-summary-header';
            header.innerHTML = '<i class="fa-solid fa-chart-bar"></i> Plan Summary';
            const toggle = document.createElement('span');
            toggle.className = 'qp-v2-summary-toggle';
            toggle.textContent = '▼';
            header.appendChild(toggle);
            panel.appendChild(header);

            const body = document.createElement('div');
            body.className = 'qp-v2-summary-body';

            header.addEventListener('click', function () {
                body.classList.toggle('collapsed');
                toggle.textContent = body.classList.contains('collapsed') ? '▶' : '▼';
            });

            // ── Summary warnings (top, highlighted) ─────────────────────────────
            if (s.warnings && s.warnings.length > 0) {
                const warnBox = document.createElement('div');
                warnBox.className = 'qp-v2-summary-warnings';
                s.warnings.forEach(function (w) {
                    const item = document.createElement('div');
                    item.className = 'qp-v2-summary-warn-item';
                    item.innerHTML = '<i class="fa-solid fa-triangle-exclamation"></i> ' + escHtml(w);
                    warnBox.appendChild(item);
                });
                body.appendChild(warnBox);
            }

            // ── Metrics grid ────────────────────────────────────────────────────
            const grid = document.createElement('div');
            grid.className = 'qp-v2-summary-grid';

            function addMetric(label, value, cls) {
                if (value == null || value === '') return;
                const cell = document.createElement('div');
                cell.className = 'qp-v2-summary-metric' + (cls ? ' ' + cls : '');
                cell.innerHTML = '<span class="qp-v2-sm-label">' + escHtml(label) + '</span>' +
                                 '<span class="qp-v2-sm-value">' + escHtml(String(value)) + '</span>';
                grid.appendChild(cell);
            }

            addMetric('CE Version', s.ceVersion);
            addMetric('Optimization', s.optimizationLevel);
            if (s.earlyAbortReason) addMetric('Abort Reason', s.earlyAbortReason, 'warn');
            addMetric('DOP', s.dop);
            if (s.nonParallelReason) addMetric('Serial Reason', s.nonParallelReason);
            if (s.compileTimeMs != null) addMetric('Compile Time', s.compileTimeMs + ' ms');
            if (s.compileCpuMs != null) addMetric('Compile CPU', s.compileCpuMs + ' ms');
            if (s.compileMemoryKB != null) addMetric('Compile Memory', Math.round(s.compileMemoryKB) + ' KB');
            if (s.memoryGrantKB != null) addMetric('Memory Grant', Math.round(s.memoryGrantKB).toLocaleString() + ' KB');
            if (s.cachedPlanSizeKB != null) addMetric('Cached Plan', Math.round(s.cachedPlanSizeKB) + ' KB');
            addMetric('Parameterization', s.parameterization);
            if (s.queryHash) addMetric('Query Hash', s.queryHash);
            if (s.planHash) addMetric('Plan Hash', s.planHash);
            // #13 Plan age
            if (s.planCreationTime) {
                try {
                    const d   = new Date(s.planCreationTime);
                    const age = isNaN(d) ? s.planCreationTime : d.toLocaleString();
                    addMetric('Plan Created', age, 'info');
                } catch (_) {
                    addMetric('Plan Created', s.planCreationTime);
                }
            }

            if (grid.children.length > 0) body.appendChild(grid);

            // Helper: create a collapsible summary section with chevron toggle
            function makeSec(iconClass, label) {
                const sec = document.createElement('div');
                sec.className = 'qp-v2-summary-section';

                const titleEl = document.createElement('div');
                titleEl.className = 'qp-v2-summary-sec-title';
                titleEl.innerHTML = '<span><i class="' + iconClass + '"></i> ' + escHtml(label) + '</span>';

                const chevron = document.createElement('span');
                chevron.className   = 'qp-v2-sec-chevron';
                chevron.textContent = '▼';
                titleEl.appendChild(chevron);

                const secBody = document.createElement('div');
                secBody.className = 'qp-v2-sec-body';

                titleEl.addEventListener('click', function () {
                    sec.classList.toggle('collapsed');
                    chevron.textContent = sec.classList.contains('collapsed') ? '▶' : '▼';
                });

                sec.append(titleEl, secBody);
                return { sec, body: secBody };
            }

            // ── Parameters table ────────────────────────────────────────────────
            if (s.parameters && s.parameters.length > 0) {
                const { sec, body: sb } = makeSec('fa-solid fa-at', 'Parameters');
                const tbl = document.createElement('table');
                tbl.className = 'qp-v2-summary-table';
                tbl.innerHTML = '<tr><th>Name</th><th>Type</th><th>Compiled</th><th>Runtime</th></tr>';
                s.parameters.forEach(function (p) {
                    const mismatch = p.compiledValue && p.runtimeValue && p.compiledValue !== p.runtimeValue;
                    const tr = document.createElement('tr');
                    if (mismatch) tr.className = 'qp-v2-param-mismatch';
                    tr.innerHTML = '<td>' + escHtml(p.name) + '</td>' +
                                   '<td>' + escHtml(p.dataType || '') + '</td>' +
                                   '<td>' + escHtml(p.compiledValue || '-') + '</td>' +
                                   '<td>' + escHtml(p.runtimeValue || '-') + '</td>';
                    tbl.appendChild(tr);
                });
                sb.appendChild(tbl);
                body.appendChild(sec);
            }

            // ── Wait stats ──────────────────────────────────────────────────────
            if (s.waitStats && s.waitStats.length > 0) {
                const { sec, body: sb } = makeSec('fa-solid fa-hourglass-half', 'Wait Stats');
                const tbl = document.createElement('table');
                tbl.className = 'qp-v2-summary-table';
                tbl.innerHTML = '<tr><th>Wait Type</th><th>Time (ms)</th><th>Count</th></tr>';
                s.waitStats.forEach(function (w) {
                    const tr = document.createElement('tr');
                    tr.innerHTML = '<td>' + escHtml(w.waitType) + '</td>' +
                                   '<td>' + w.waitMs.toFixed(1) + '</td>' +
                                   '<td>' + w.waitCount + '</td>';
                    tbl.appendChild(tr);
                });
                sb.appendChild(tbl);
                body.appendChild(sec);
            }

            // ── Statistics used ─────────────────────────────────────────────────
            if (s.statsUsed && s.statsUsed.length > 0) {
                const { sec, body: sb } = makeSec('fa-solid fa-chart-simple', 'Statistics Used (' + s.statsUsed.length + ')');
                const tbl = document.createElement('table');
                tbl.className = 'qp-v2-summary-table';
                tbl.innerHTML = '<tr><th>Statistic</th><th>Table</th><th>Modifications</th><th>Sampling %</th><th>Last Update</th></tr>';
                s.statsUsed.forEach(function (st) {
                    const stale = st.modCount && st.modCount > 1000;
                    const tr = document.createElement('tr');
                    if (stale) tr.className = 'qp-v2-stat-stale';
                    tr.innerHTML = '<td>' + escHtml(st.name) + '</td>' +
                                   '<td>' + escHtml(st.table || '') + '</td>' +
                                   '<td>' + (st.modCount != null ? st.modCount.toLocaleString() : '-') + '</td>' +
                                   '<td>' + (st.samplingPct != null ? st.samplingPct.toFixed(1) + '%' : '-') + '</td>' +
                                   '<td>' + escHtml(st.lastUpdate || '-') + '</td>';
                    tbl.appendChild(tr);
                });
                sb.appendChild(tbl);
                body.appendChild(sec);
            }

            // ── SET options ─────────────────────────────────────────────────────
            if (s.setOptions) {
                const keys = Object.keys(s.setOptions);
                if (keys.length > 0) {
                    const { sec, body: sb } = makeSec('fa-solid fa-sliders', 'SET Options');
                    const chips = document.createElement('div');
                    chips.className = 'qp-v2-set-options';
                    keys.forEach(function (k) {
                        const chip = document.createElement('span');
                        chip.className = 'qp-v2-set-chip ' + (s.setOptions[k] ? 'on' : 'off');
                        chip.textContent = k + ': ' + (s.setOptions[k] ? 'ON' : 'OFF');
                        chips.appendChild(chip);
                    });
                    sb.appendChild(chips);
                    body.appendChild(sec);
                }
            }

            // ── Trace flags ─────────────────────────────────────────────────────
            if (s.traceFlags && s.traceFlags.length > 0) {
                const { sec, body: sb } = makeSec('fa-solid fa-flag', 'Trace Flags');
                const chips = document.createElement('div');
                chips.className = 'qp-v2-set-options';
                s.traceFlags.forEach(function (tf) {
                    const chip = document.createElement('span');
                    chip.className = 'qp-v2-set-chip on';
                    chip.textContent = tf;
                    chips.appendChild(chip);
                });
                sb.appendChild(chips);
                body.appendChild(sec);
            }

            panel.appendChild(body);
            container.appendChild(panel);
        }

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
        svg.style.cssText = 'position:absolute;top:0;left:0;overflow:visible;';
        wrap.appendChild(svg);

        // ── Edge hover tooltip (shared, one per render) ───────────────────────
        const edgeTip = document.createElement('div');
        edgeTip.className = 'qp-edge-tooltip';
        edgeTip.style.display = 'none';
        container.appendChild(edgeTip);

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

            // ── #3 Row estimate mismatch — colour edge red/amber when actual ≠ estimate >10× ──
            const targetNode   = nodeMap[edge.target];
            const estRows      = targetNode ? (targetNode.estimateRows || 0) : 0;
            const actRows      = targetNode ? (targetNode.actualRows   ?? null) : null;
            const avgRowSize   = targetNode ? (targetNode.avgRowSize   || 0)  : 0;
            let   edgeColor    = '#5a5a5a';
            let   mismatchTip  = null;
            if (actRows !== null && estRows > 0) {
                const ratio = actRows / estRows;
                if (ratio > 10) {
                    edgeColor   = '#e53935';
                    mismatchTip = `Estimate: ${fmt(estRows)} → Actual: ${fmt(actRows)} (×${ratio.toFixed(0)} over)`;
                } else if (ratio < 0.1) {
                    edgeColor   = '#f59e0b';
                    mismatchTip = `Estimate: ${fmt(estRows)} → Actual: ${fmt(actRows)} (×${(1/ratio).toFixed(0)} under)`;
                }
            }

            const path = document.createElementNS(SVG_NS, 'path');
            path.setAttribute('d',             `M ${x1} ${y1} C ${mx} ${y1} ${mx} ${y2} ${x2} ${y2}`);
            path.setAttribute('fill',          'none');
            path.setAttribute('stroke',        edgeColor);
            path.setAttribute('stroke-width',  mismatchTip ? Math.max(width, 3) : width);
            path.setAttribute('stroke-linecap', 'round');
            path.style.pointerEvents = 'stroke';
            path.style.cursor = 'pointer';
            // Data attrs used by collapse/expand toggle
            path.dataset.edgeSource = String(edge.source);
            path.dataset.edgeTarget = String(edge.target);

            // ── Edge hover tooltip content ────────────────────────────────────
            const srcNode = nodeMap[edge.source];
            const dstNode = nodeMap[edge.target];
            const dataBytes = rows * avgRowSize;
            const dataSizeStr = dataBytes >= 1048576
                ? (dataBytes / 1048576).toFixed(1) + ' MB'
                : dataBytes >= 1024
                    ? (dataBytes / 1024).toFixed(1) + ' KB'
                    : dataBytes.toFixed(0) + ' B';

            const outputCols = targetNode?.outputList?.length
                ? targetNode.outputList.slice(0, 8).join(', ') + (targetNode.outputList.length > 8 ? ` (+${targetNode.outputList.length - 8} more)` : '')
                : null;

            path.addEventListener('mouseenter', ev => {
                let html = `<div class="qp-edge-tip-title">Data Flow</div>`;
                html += `<table class="qp-edge-tip-table">`;
                if (srcNode) html += `<tr><td>From</td><td>${srcNode.type || srcNode.physicalType || ''}</td></tr>`;
                if (dstNode) html += `<tr><td>To</td><td>${dstNode.type || dstNode.physicalType || ''}</td></tr>`;
                html += `<tr><td>Est. Rows</td><td>${fmt(estRows || rows)}</td></tr>`;
                if (actRows !== null) html += `<tr><td>Actual Rows</td><td>${fmt(actRows)}</td></tr>`;
                if (avgRowSize > 0) html += `<tr><td>Row Size</td><td>${avgRowSize} B</td></tr>`;
                if (avgRowSize > 0) html += `<tr><td>Est. Data Size</td><td>${dataSizeStr}</td></tr>`;
                if (mismatchTip) html += `<tr><td colspan="2" style="color:#e53935;font-size:11px;">${mismatchTip}</td></tr>`;
                if (outputCols) html += `<tr><td>Output</td><td style="font-size:10px;max-width:220px;word-break:break-all;">${outputCols}</td></tr>`;
                html += `</table>`;
                edgeTip.innerHTML = html;
                edgeTip.style.display = 'block';
                const vp = container.getBoundingClientRect();
                const tipX = ev.clientX - vp.left + 14;
                const tipY = ev.clientY - vp.top  + 14;
                edgeTip.style.left = tipX + 'px';
                edgeTip.style.top  = tipY + 'px';
            });
            path.addEventListener('mousemove', ev => {
                const vp = container.getBoundingClientRect();
                edgeTip.style.left = (ev.clientX - vp.left + 14) + 'px';
                edgeTip.style.top  = (ev.clientY - vp.top  + 14) + 'px';
            });
            path.addEventListener('mouseleave', () => {
                edgeTip.style.display = 'none';
            });

            svg.appendChild(path);

            // Mismatch warning icon on the edge midpoint
            if (mismatchTip) {
                const warnG = document.createElementNS(SVG_NS, 'g');
                warnG.setAttribute('transform', `translate(${mx - 6},${(y1 + y2) / 2 - 7})`);
                const warnCirc = document.createElementNS(SVG_NS, 'circle');
                warnCirc.setAttribute('cx', '6'); warnCirc.setAttribute('cy', '7');
                warnCirc.setAttribute('r', '7');
                warnCirc.setAttribute('fill', edgeColor); warnCirc.setAttribute('opacity', '0.9');
                const warnTxt = document.createElementNS(SVG_NS, 'text');
                warnTxt.setAttribute('x', '6'); warnTxt.setAttribute('y', '11');
                warnTxt.setAttribute('text-anchor', 'middle');
                warnTxt.setAttribute('font-size', '9');
                warnTxt.setAttribute('font-weight', 'bold');
                warnTxt.setAttribute('fill', '#fff');
                warnTxt.textContent = '!';
                const warnTitle = document.createElementNS(SVG_NS, 'title');
                warnTitle.textContent = mismatchTip;
                warnG.append(warnCirc, warnTxt, warnTitle);
                svg.appendChild(warnG);
            }

            if (rows > 1) {
                const txt = document.createElementNS(SVG_NS, 'text');
                txt.setAttribute('x',           mx);
                txt.setAttribute('y',           (y1 + y2) / 2 - width / 2 - (mismatchTip ? 12 : 3));
                txt.setAttribute('text-anchor', 'middle');
                txt.setAttribute('font-size',   '9');
                txt.setAttribute('fill',        mismatchTip ? edgeColor : '#888');
                txt.textContent = fmt(rows);
                svg.appendChild(txt);
            }
        });

        // ── Shared detail pane (one per render) ───────────────────────────────
        const pane = createPane(container);

        // ── Node divs ─────────────────────────────────────────────────────────
        // nodeElements map: nodeId → div element (for search highlight)
        const nodeElements = {};
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

            // ── #7 Seek/scan predicate inline on node face ────────────────────
            const seekPred = node.properties && node.properties['Seek Predicate'];
            if (seekPred) {
                const predEl = document.createElement('div');
                predEl.className   = 'qp-v2-pred';
                predEl.title       = seekPred;
                // Truncate to fit: show first 38 chars
                predEl.textContent = seekPred.length > 38 ? seekPred.slice(0, 36) + '…' : seekPred;
                labels.appendChild(predEl);
            }

            const costEl = document.createElement('div');
            costEl.className   = 'qp-v2-cost';
            costEl.style.color = cc;
            costEl.textContent = 'Cost: ' + displayPct.toFixed(1) + '%';
            labels.appendChild(costEl);

            // ── #8 Memory grant feedback badge ────────────────────────────────
            if (node.badges.includes('MemGrantFeedback')) {
                const mgEl = document.createElement('div');
                mgEl.className   = 'qp-v2-mgf-badge';
                mgEl.title       = 'Memory Grant Feedback applied — grant was adjusted by SQL Server';
                mgEl.textContent = 'MGF';
                labels.appendChild(mgEl);
            }

            body.appendChild(labels);
            nodeDiv.appendChild(body);
            group.appendChild(nodeDiv);

            // ── Collapse/expand toggle (shown when node has children) ─────────
            const nodeChildren = childMap[node.id] || [];
            if (nodeChildren.length > 0) {
                const collapseBtn = document.createElement('div');
                collapseBtn.className = 'qp-v2-collapse-btn';
                collapseBtn.title     = 'Collapse subtree';
                collapseBtn.innerHTML = '−';
                collapseBtn.dataset.collapsed = 'false';
                // Positioned left of the node — the button sits on the connector edge
                collapseBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const isCollapsed = collapseBtn.dataset.collapsed === 'true';
                    // Collect all descendant node IDs recursively
                    function getDescendants(nid) {
                        const kids = childMap[nid] || [];
                        let result = [...kids];
                        kids.forEach(k => { result = result.concat(getDescendants(k)); });
                        return result;
                    }
                    const descendants = getDescendants(node.id);
                    descendants.forEach(did => {
                        const el = wrap.querySelector(`[data-nodeid="${did}"]`);
                        if (el) el.style.visibility = isCollapsed ? 'visible' : 'hidden';
                    });
                    // Hide edges by toggling SVG paths that connect to hidden nodes
                    const hiddenSet = new Set(isCollapsed ? [] : descendants);
                    svg.querySelectorAll('[data-edge-source]').forEach(path => {
                        const src = path.dataset.edgeSource;
                        const tgt = path.dataset.edgeTarget;
                        if (hiddenSet.has(src) || hiddenSet.has(tgt)) {
                            path.style.visibility = isCollapsed ? 'visible' : 'hidden';
                        }
                    });
                    collapseBtn.dataset.collapsed = isCollapsed ? 'false' : 'true';
                    collapseBtn.innerHTML         = isCollapsed ? '−' : '+';
                    collapseBtn.title             = isCollapsed ? 'Collapse subtree' : 'Expand subtree';
                });
                group.appendChild(collapseBtn);
            }

            group.dataset.nodeid = String(node.id);
            wrap.appendChild(group);

            // ── Hover events → shared pane ────────────────────────────────────
            nodeDiv.addEventListener('mouseenter', () => { if (!_dragging) showPane(pane, node, nodeDiv); });
            nodeDiv.addEventListener('mouseleave', () => startFade(pane));

            // ── #12 Right-click copy node properties ──────────────────────────
            nodeDiv.addEventListener('contextmenu', ev => {
                ev.preventDefault();
                const rows = buildRows(node, node._individualPct ?? node.relativeCost);
                const text = (node.physicalType || node.type) + '\n'
                    + rows.filter(([, v]) => v != null && v !== '')
                          .map(([k, v]) => k.padEnd(22) + v)
                          .join('\n');
                copyToClipboard(text);
                // Brief visual feedback on the node
                nodeDiv.style.outline = '2px solid var(--accent, #2196f3)';
                setTimeout(() => { nodeDiv.style.outline = ''; }, 900);
            });

            // Register for search highlight
            nodeElements[node.id] = nodeDiv;
        });

        // Expose node elements so the public API setSearchTerm() can reach them
        _lastNodeElements = nodeElements;

        // ── Pan / zoom state ──────────────────────────────────────────────────
        let _tx = 0, _ty = 0, _scale = 1;
        let _dragging = false, _lastX = 0, _lastY = 0;

        function applyXform() {
            wrap.style.transform = `translate(${_tx}px,${_ty}px) scale(${_scale})`;
            updateMinimap();
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

        // ── #11 Keyboard shortcuts for zoom/pan ───────────────────────────────
        const onKey = e => {
            // Only handle when the viewport (or its children) are focused/visible
            // and no input element is focused
            if (document.activeElement && ['INPUT','TEXTAREA','SELECT'].includes(document.activeElement.tagName)) return;
            if (!viewport.isConnected) return;
            const r  = viewport.getBoundingClientRect();
            const cx = r.width / 2, cy = r.height / 2;
            const PAN_STEP = 60;
            switch (e.key) {
                case '+': case '=':
                    if (e.ctrlKey || e.metaKey) { e.preventDefault(); zoomAt(cx, cy, 1.25); }
                    break;
                case '-':
                    if (e.ctrlKey || e.metaKey) { e.preventDefault(); zoomAt(cx, cy, 1 / 1.25); }
                    break;
                case '0':
                    if (e.ctrlKey || e.metaKey) { e.preventDefault(); resetFit(); }
                    break;
                case 'ArrowLeft':  e.preventDefault(); _tx += PAN_STEP; applyXform(); break;
                case 'ArrowRight': e.preventDefault(); _tx -= PAN_STEP; applyXform(); break;
                case 'ArrowUp':    e.preventDefault(); _ty += PAN_STEP; applyXform(); break;
                case 'ArrowDown':  e.preventDefault(); _ty -= PAN_STEP; applyXform(); break;
            }
        };
        window.addEventListener('keydown', onKey);

        // ── #6 Search highlight ───────────────────────────────────────────────
        // nodeElements map: nodeId → div element (for highlight)
        // (declared above before node loop; populated during node div build)

        // ── #10 Minimap ───────────────────────────────────────────────────────
        // Only show minimap when there are enough nodes to warrant it (>= 8)
        let minimapEl = null, mmViewport = null;
        const MINIMAP_W = 160, MINIMAP_H = 100;
        if (graph.nodes.length >= 8) {
            minimapEl = document.createElement('div');
            minimapEl.className = 'qp-v2-minimap';
            minimapEl.title = 'Minimap — drag the viewport rectangle to pan';

            const mmSvg = document.createElementNS(SVG_NS, 'svg');
            mmSvg.setAttribute('width', MINIMAP_W);
            mmSvg.setAttribute('height', MINIMAP_H);

            const scaleX = MINIMAP_W / canvasW;
            const scaleY = MINIMAP_H / canvasH;
            const mmScale = Math.min(scaleX, scaleY);

            // Draw node rects on minimap
            graph.nodes.forEach(n => {
                const p = positions[n.id];
                if (!p) return;
                const rect = document.createElementNS(SVG_NS, 'rect');
                rect.setAttribute('x',      (p.x + PAD) * mmScale);
                rect.setAttribute('y',      (p.y + PAD) * mmScale);
                rect.setAttribute('width',  NODE_W * mmScale);
                rect.setAttribute('height', NODE_H * mmScale);
                rect.setAttribute('fill',   costColor(n._individualPct ?? n.relativeCost));
                rect.setAttribute('opacity', '0.7');
                mmSvg.appendChild(rect);
            });

            // Viewport rectangle (draggable)
            mmViewport = document.createElementNS(SVG_NS, 'rect');
            mmViewport.setAttribute('fill',   'none');
            mmViewport.setAttribute('stroke', '#fff');
            mmViewport.setAttribute('stroke-width', '1.5');
            mmViewport.setAttribute('rx', '2');
            mmSvg.appendChild(mmViewport);

            minimapEl.appendChild(mmSvg);
            viewport.appendChild(minimapEl);

            // Minimap drag to pan
            let mmDragging = false;
            mmSvg.addEventListener('mousedown', ev => {
                mmDragging = true;
                ev.preventDefault();
                ev.stopPropagation();
            });
            window.addEventListener('mousemove', ev => {
                if (!mmDragging) return;
                const mmRect = mmSvg.getBoundingClientRect();
                const relX   = (ev.clientX - mmRect.left) / mmScale;
                const relY   = (ev.clientY - mmRect.top)  / mmScale;
                const vw     = viewport.clientWidth;
                const vh     = viewport.clientHeight;
                // Centre the viewport rectangle on the clicked minimap point
                _tx = -(relX * _scale - vw / 2);
                _ty = -(relY * _scale - vh / 2);
                applyXform();
            });
            window.addEventListener('mouseup', () => { mmDragging = false; });
        }

        function updateMinimap() {
            if (!minimapEl || !mmViewport) return;
            const scaleX  = MINIMAP_W / canvasW;
            const scaleY  = MINIMAP_H / canvasH;
            const mmScale = Math.min(scaleX, scaleY);
            const vw = viewport.clientWidth;
            const vh = viewport.clientHeight;
            // Viewport rect in canvas coords
            const vLeft = -_tx / _scale;
            const vTop  = -_ty / _scale;
            const vW    = vw / _scale;
            const vH    = vh / _scale;
            mmViewport.setAttribute('x',      Math.max(0, vLeft * mmScale));
            mmViewport.setAttribute('y',      Math.max(0, vTop  * mmScale));
            mmViewport.setAttribute('width',  vW * mmScale);
            mmViewport.setAttribute('height', vH * mmScale);
        }
        updateMinimap();

        // Cleanup listeners when container is re-used (next plan render clears innerHTML)
        const obs = new MutationObserver(() => {
            if (!viewport.isConnected) {
                window.removeEventListener('mousemove', onMove);
                window.removeEventListener('mouseup',   onUp);
                window.removeEventListener('keydown',   onKey);
                obs.disconnect();
            }
        });
        obs.observe(container, { childList: true });
    }

    // ── Per-render search highlight state (module-level so setSearchTerm can reach it) ──
    let _currentNodeElements = {};   // nodeId → div
    let _currentSearchTerm   = '';
    let _lastNodeElements    = {};   // written by render(), read by showPlan()

    // ── No-Pants .NET reference for index execution ───────────────────────────
    let _dotNetRef = null;

    function applySearchHighlight(term) {
        _currentSearchTerm = term;
        const lc = (term || '').toLowerCase().trim();
        Object.values(_currentNodeElements).forEach(div => {
            if (!lc) {
                div.style.outline     = '';
                div.style.opacity     = '';
                div.style.zIndex      = '';
            } else {
                // Match against operator name text inside the node
                const nameEl = div.querySelector('.qp-v2-name');
                const text   = (nameEl ? nameEl.textContent : '').toLowerCase();
                if (text.includes(lc)) {
                    div.style.outline = '2px solid #f59e0b';
                    div.style.opacity = '1';
                    div.style.zIndex  = '10';
                } else {
                    div.style.outline = '';
                    div.style.opacity = '0.35';
                    div.style.zIndex  = '';
                }
            }
        });
    }

    // ── Public API ────────────────────────────────────────────────────────────

    // Per-render plan XML store (for .sqlplan download)
    let _currentPlanXml = null;

    return {
        showPlan: function (containerId, jsonData, planXml) {
            const container = document.getElementById(containerId);
            if (!container) return;
            container.innerHTML = '';
            _currentNodeElements = {};
            _currentSearchTerm   = '';
            _currentPlanXml      = planXml || null;
            try {
                const parsed = typeof jsonData === 'string' ? JSON.parse(jsonData) : jsonData;
                // ParseToJson now returns an array; handle both array (new) and object (legacy)
                const graphs = Array.isArray(parsed) ? parsed : [parsed];

                // Store all graphs for switching
                window._qpCurrentGraphs = graphs;
                window._qpRenderStatement = function(index) {
                    document.querySelectorAll('.qp-stmt-tab').forEach((t, i) => {
                        t.classList.toggle('active', i === index);
                    });
                    const renderContainer = document.getElementById('qp-render-container');
                    if (!renderContainer) return;
                    renderContainer.innerHTML = '';
                    _currentNodeElements = {};
                    render(renderContainer, graphs[index]);
                    window._qpActiveStatementIndex = index;
                };

                // Auto select highest cost statement by default
                let highestCostIndex = 0;
                let highestCostValue = 0;
                graphs.forEach((graph, idx) => {
                    const pct = typeof graph.batchPct === 'number' ? graph.batchPct : 0;
                    if (pct > highestCostValue) {
                        highestCostValue = pct;
                        highestCostIndex = idx;
                    }
                });
                window._qpActiveStatementIndex = highestCostIndex;

                // Statement tab bar — always shown (single or multi), placed OUTSIDE
                // #qp-container (which uses overflow:hidden for pan/zoom) so tabs aren't clipped.
                // We insert it as a sibling before the container in .query-plan-modal-body.
                {
                    // Remove any previously injected tab bar (re-render guard)
                    const existingTabBar = container.parentElement
                        ? container.parentElement.querySelector('.qp-v2-statement-tabs')
                        : null;
                    if (existingTabBar) existingTabBar.remove();

                    const tabBar = document.createElement('div');
                    tabBar.className = 'qp-v2-statement-tabs';

                    // ── Prev / Next nav buttons ──────────────────────────────
                    const btnPrev = document.createElement('button');
                    btnPrev.className = 'qp-stmt-nav';
                    btnPrev.innerHTML = '&#8249;';
                    btnPrev.title = 'Previous statement';
                    tabBar.appendChild(btnPrev);

                    graphs.forEach((graph, idx) => {
                        const tab = document.createElement('div');
                        const pct = typeof graph.batchPct === 'number' ? graph.batchPct : 0;
                        const pctFixed = pct.toFixed(0);
                        const opType = graph.query ? graph.query.split(' ')[0].toUpperCase().substring(0, 12) : 'STATEMENT';

                        // Count warnings for this statement
                        let warningCount = 0;
                        if (graph.summary?.warnings) warningCount = graph.summary.warnings.length;

                        // Cost color coding matches operator highlighting thresholds
                        let costClass = '';
                        if (pct >= 50) costClass = 'cost-high';
                        else if (pct >= 20) costClass = 'cost-medium';
                        else if (pct >= 5) costClass = 'cost-low';

                        const classes = ['qp-stmt-tab'];
                        if (idx === highestCostIndex) classes.push('active');
                        if (costClass) classes.push(costClass);
                        tab.className = classes.join(' ');

                        const warningBadge = warningCount > 0
                            ? `<span class="stmt-tab-badge">${warningCount}</span>` : '';
                        tab.innerHTML = `
                            <span class="stmt-tab-num">${idx + 1}</span>
                            <span class="stmt-tab-type">${opType}</span>
                            <span class="stmt-tab-cost">${pctFixed}%</span>
                            ${warningBadge}
                        `;
                        tab.title = graph.query || `Statement ${idx + 1}`;
                        tab.onclick = () => window._qpRenderStatement(idx);
                        tabBar.appendChild(tab);
                    });

                    const btnNext = document.createElement('button');
                    btnNext.className = 'qp-stmt-nav';
                    btnNext.innerHTML = '&#8250;';
                    btnNext.title = 'Next statement';
                    tabBar.appendChild(btnNext);

                    // Wire up prev/next
                    btnPrev.onclick = () => {
                        const cur = window._qpActiveStatementIndex ?? 0;
                        if (cur > 0) window._qpRenderStatement(cur - 1);
                    };
                    btnNext.onclick = () => {
                        const cur = window._qpActiveStatementIndex ?? 0;
                        if (cur < graphs.length - 1) window._qpRenderStatement(cur + 1);
                    };
                    const updateNavBtns = () => {
                        const cur = window._qpActiveStatementIndex ?? 0;
                        btnPrev.disabled = cur === 0;
                        btnNext.disabled = cur === graphs.length - 1;
                    };
                    updateNavBtns();
                    // Patch _qpRenderStatement to also update nav button state
                    const _origRender = window._qpRenderStatement;
                    window._qpRenderStatement = function(index) {
                        _origRender(index);
                        updateNavBtns();
                    };

                    // Insert tab bar before #qp-container (its parent) so it sits outside overflow:hidden
                    if (container.parentElement) {
                        container.parentElement.insertBefore(tabBar, container);
                    } else {
                        container.appendChild(tabBar);
                    }
                }
                
                // Main render container
                const renderContainer = document.createElement('div');
                renderContainer.id = 'qp-render-container';
                renderContainer.className = 'qp-render-container';
                container.appendChild(renderContainer);
                
                // Render highest cost statement initially
                render(renderContainer, graphs[highestCostIndex]);
                
            } catch (e) {
                container.innerHTML =
                    '<p style="color:#f44336;padding:16px;">Failed to render query plan: ' + e.message + '</p>';
            }
        },

        showParseError: function (containerId, message) {
            const container = document.getElementById(containerId);
            if (!container) return;
            container.innerHTML = `
                <div style="color:#f44336;padding:24px;font-size:12px;font-family:Consolas,monospace;">
                    <p><strong>Plan parser error:</strong> ${message || 'unknown error'}</p>
                    <p style="opacity:0.7;margin-top:12px;">
                        This usually occurs with:<br>
                        • UTF-16 encoded plans<br>
                        • SQL Server 2012 and older showplan schema<br>
                        • Corrupted / truncated plan XML<br>
                        • Plans with no execution operators
                    </p>
                    <p style="margin-top:16px;">
                        You can still download the raw .sqlplan file and open it in SSMS.
                    </p>
                </div>
            `;
        },

        clearPlan: function (containerId) {
            const el = document.getElementById(containerId);
            if (el) {
                el.innerHTML = '';
                // Remove tab bar that was inserted as sibling outside the container
                if (el.parentElement) {
                    const tabBar = el.parentElement.querySelector('.qp-v2-statement-tabs');
                    if (tabBar) tabBar.remove();
                }
            }
            _currentNodeElements = {};
            _currentPlanXml      = null;
            // Clean up multi-statement state
            delete window._qpCurrentGraphs;
            delete window._qpActiveStatementIndex;
            delete window._qpRenderStatement;
        },

        setUseV2Icons: function (enabled) {
            USE_V2_ICONS = !!enabled;
        },

        // ── #6 Search highlight ───────────────────────────────────────────────
        setSearchTerm: function (term) {
            applySearchHighlight(term);
        },

        // ── No-Pants: register .NET reference for index execution ────────────
        setDotNetRef: function (ref) {
            _dotNetRef = ref || null;
        },

        // ── #9 Download .sqlplan ──────────────────────────────────────────────
        downloadSqlplan: function (xml) {
            const blob = new Blob([xml || _currentPlanXml || ''], { type: 'application/xml' });
            const url  = URL.createObjectURL(blob);
            const a    = document.createElement('a');
            a.href     = url;
            a.download = 'query-plan.sqlplan';
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            URL.revokeObjectURL(url);
        }
    };
})();
