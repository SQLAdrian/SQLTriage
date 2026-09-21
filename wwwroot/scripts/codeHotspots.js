/* In the name of God, the Merciful, the Compassionate */

/**
 * Code Hotspots — squarified treemap of database -> object -> statement.
 * Pure DOM (no canvas, no external deps) so tile click + tooltip behave like
 * normal HTML. Squarify algorithm = Bruls/Huijsen/van Wijk 2000.
 */
window.codeHotspots = (function () {
    'use strict';

    var _instances = {}; // containerId -> { dotnetRef, tipEl, lastNodes, lastMetric }
    var _tip = null;

    function ensureTip() {
        if (_tip) return _tip;
        _tip = document.createElement('div');
        _tip.className = 'hot-tip';
        _tip.style.cssText =
            'position:fixed;pointer-events:none;z-index:9999;display:none;' +
            'background:rgba(15,23,42,0.96);border:1px solid rgba(99,102,241,0.5);' +
            'border-radius:6px;padding:8px 10px;font-size:11px;color:#e2e8f0;' +
            'line-height:1.55;max-width:320px;box-shadow:0 6px 22px rgba(0,0,0,0.65);';
        document.body.appendChild(_tip);
        return _tip;
    }
    function showTip(x, y, html) {
        var t = ensureTip();
        t.innerHTML = html;
        t.style.display = 'block';
        var tw = t.offsetWidth, th = t.offsetHeight;
        t.style.left = Math.min(x + 14, window.innerWidth  - tw - 8) + 'px';
        t.style.top  = Math.min(y + 14, window.innerHeight - th - 8) + 'px';
    }
    function hideTip() { if (_tip) _tip.style.display = 'none'; }

    function init(containerId, dotnetRef) {
        var el = document.getElementById(containerId);
        if (!el) return;
        _instances[containerId] = { dotnetRef: dotnetRef, lastNodes: null, lastMetric: 'cpu' };
        // Re-layout on resize (debounced)
        var t;
        var onResize = function () {
            clearTimeout(t);
            t = setTimeout(function () {
                var inst = _instances[containerId];
                if (inst && inst.lastNodes) {
                    render(containerId, inst.lastNodes, inst.lastMetric);
                }
            }, 80);
        };
        window.addEventListener('resize', onResize);
        _instances[containerId].onResize = onResize;
    }

    function dispose(containerId) {
        var inst = _instances[containerId];
        if (inst && inst.onResize) window.removeEventListener('resize', inst.onResize);
        var el = document.getElementById(containerId);
        if (el) el.innerHTML = '';
        delete _instances[containerId];
    }

    function render(containerId, nodes, metric) {
        var el = document.getElementById(containerId);
        var inst = _instances[containerId];
        if (!el || !inst) return;
        inst.lastNodes = nodes;
        inst.lastMetric = metric;

        var W = el.clientWidth, H = el.clientHeight;
        if (W < 10 || H < 10) {
            // container not laid out yet — try again
            setTimeout(function () { render(containerId, nodes, metric); }, 60);
            return;
        }

        // sort desc by value, normalise zero/negative to a tiny floor
        var data = nodes.slice().filter(function (n) { return n && n.value > 0; });
        data.sort(function (a, b) { return b.value - a.value; });
        if (data.length === 0) {
            el.innerHTML = '<div class="hot-empty">No data.</div>';
            return;
        }

        var rects = squarify(data, { x: 0, y: 0, w: W, h: H });

        // build DOM
        el.innerHTML = '';
        var frag = document.createDocumentFragment();
        rects.forEach(function (r) {
            var d = r.node;
            var t = document.createElement('div');
            t.className = 'hot-tile hot-tile-' + (d.kind || 'x');
            t.style.left = r.x + 'px';
            t.style.top = r.y + 'px';
            t.style.width = Math.max(1, r.w - 2) + 'px';
            t.style.height = Math.max(1, r.h - 2) + 'px';
            t.style.background = d.color || '#334155';
            t.dataset.id = d.id;
            t.dataset.name = d.name;
            t.dataset.kind = d.kind;

            // label only fits if rect big enough
            if (r.w > 70 && r.h > 28) {
                var lbl = document.createElement('div');
                lbl.className = 'hot-tile-label';
                lbl.textContent = d.name;
                t.appendChild(lbl);
                if (r.h > 50 && d.sublabel) {
                    var sub = document.createElement('div');
                    sub.className = 'hot-tile-sublabel';
                    sub.textContent = d.sublabel;
                    t.appendChild(sub);
                }
            }
            t.addEventListener('mouseenter', function (ev) {
                if (d.tip) showTip(ev.clientX, ev.clientY, d.tip);
            });
            t.addEventListener('mousemove', function (ev) {
                if (d.tip) showTip(ev.clientX, ev.clientY, d.tip);
            });
            t.addEventListener('mouseleave', hideTip);
            t.addEventListener('click', function () {
                hideTip();
                if (inst.dotnetRef) {
                    inst.dotnetRef.invokeMethodAsync('OnTileClicked', d.id, d.name, d.kind);
                }
            });
            frag.appendChild(t);
        });
        el.appendChild(frag);
    }

    // ── Squarified treemap (Bruls/Huijsen/van Wijk) ───────────────────────
    function squarify(items, rect) {
        var total = 0;
        items.forEach(function (i) { total += i.value; });
        var area = rect.w * rect.h;
        if (total === 0 || area === 0) return [];
        var scale = area / total;
        var queue = items.map(function (i) { return { node: i, scaled: i.value * scale }; });

        var out = [];
        squarifyStep(queue, [], rect, out);
        return out;
    }

    function shortestSide(rr) { return Math.min(rr.w, rr.h); }

    // Worst aspect ratio of the row if its tiles were laid along `w` (the short side).
    function worstRatio(row, w) {
        if (row.length === 0) return Infinity;
        var rmax = -Infinity, rmin = Infinity, s = 0;
        for (var k = 0; k < row.length; k++) {
            if (row[k].scaled > rmax) rmax = row[k].scaled;
            if (row[k].scaled < rmin) rmin = row[k].scaled;
            s += row[k].scaled;
        }
        if (s === 0) return Infinity;
        var w2 = w * w;
        var s2 = s * s;
        return Math.max((w2 * rmax) / s2, s2 / (w2 * rmin));
    }

    function squarifyStep(queue, row, rect, out) {
        if (queue.length === 0) {
            if (row.length > 0) layoutRow(row, rect, out);
            return;
        }
        var w = shortestSide(rect);
        if (w <= 0) {
            // Nothing more to draw
            return;
        }
        var head = queue[0];
        var rowWith = row.concat([head]);
        // Add to current row only if it does NOT make the worst aspect ratio worse.
        if (row.length === 0 || worstRatio(rowWith, w) <= worstRatio(row, w)) {
            squarifyStep(queue.slice(1), rowWith, rect, out);
        } else {
            // Flush current row, reduce rect, recurse with same queue (head not yet placed)
            var newRect = layoutRow(row, rect, out);
            squarifyStep(queue, [], newRect, out);
        }
    }

    // Place tiles in `row` along the short side of `rect`. Returns the remaining rect.
    function layoutRow(row, rect, out) {
        var sum = 0;
        for (var k = 0; k < row.length; k++) sum += row[k].scaled;
        if (sum <= 0) return rect;
        var w = shortestSide(rect);
        var thick = sum / w; // perpendicular thickness consumed off the long side
        var x = rect.x, y = rect.y;

        if (rect.w <= rect.h) {
            // short side = w; lay tiles left→right; thickness consumes from top
            for (var k = 0; k < row.length; k++) {
                var width = row[k].scaled / thick;
                out.push({ node: row[k].node, x: x, y: y, w: width, h: thick });
                x += width;
            }
            return { x: rect.x, y: rect.y + thick, w: rect.w, h: rect.h - thick };
        } else {
            // short side = h; lay tiles top→bottom; thickness consumes from left
            for (var k = 0; k < row.length; k++) {
                var height = row[k].scaled / thick;
                out.push({ node: row[k].node, x: x, y: y, w: thick, h: height });
                y += height;
            }
            return { x: rect.x + thick, y: rect.y, w: rect.w - thick, h: rect.h };
        }
    }

    return { init: init, dispose: dispose, render: render };
})();
