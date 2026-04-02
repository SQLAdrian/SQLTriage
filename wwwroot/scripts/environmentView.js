/* In the name of God, the Merciful, the Compassionate */

/**
 * Environment View — Mini topology renderer for a single selected SQL Server.
 * Shows the server at center with host nodes radiating out, plus cross-server arrows.
 * Host nodes are clickable — raises a Blazor callback to show the detail panel.
 */
window.environmentView = (function () {
    'use strict';

    // ── Layout constants ─────────────────────────────────────────────────
    const SRV_R    = 44;    // server circle radius
    const HOST_W   = 130;
    const HOST_H   = 46;
    const XSRV_W   = 110;
    const XSRV_H   = 36;
    const MIN_ORBIT = 160;  // min px from center to host
    const MAX_ORBIT = 280;

    function esc(s) {
        var d = document.createElement('div');
        d.textContent = String(s || '');
        return d.innerHTML;
    }

    // ── Main entry point ─────────────────────────────────────────────────
    function renderMini(containerId, jsonData) {
        var container = document.getElementById(containerId);
        if (!container) return;
        container.innerHTML = '';

        var data;
        try { data = typeof jsonData === 'string' ? JSON.parse(jsonData) : jsonData; }
        catch (e) { container.innerHTML = '<p style="color:#f44;">Parse error: ' + e.message + '</p>'; return; }

        var hosts    = data.hosts    || [];
        var crossIn  = data.crossIn  || [];
        var crossOut = data.crossOut || [];
        var server   = data.server   || { name: '?', counts: {} };

        // ── Canvas sizing ────────────────────────────────────────────────
        var n = hosts.length;
        // Orbit radius scales with host count, clamped
        var orbit = Math.min(MAX_ORBIT, Math.max(MIN_ORBIT, 60 + n * 18));
        // Cross-server nodes sit further out
        var xOrbit = orbit + 90;

        var canvasSize = (xOrbit + XSRV_W + 20) * 2;
        var cx = canvasSize / 2;
        var cy = canvasSize / 2;

        // ── Viewport with zoom/pan ───────────────────────────────────────
        var viewport = document.createElement('div');
        viewport.className = 'env-topo-viewport';
        container.appendChild(viewport);

        // Toolbar
        var toolbar = document.createElement('div');
        toolbar.className = 'qp-v2-toolbar';
        toolbar.innerHTML =
            '<button class="qp-v2-tb-btn" data-action="zoomin"  title="Zoom in">+</button>' +
            '<button class="qp-v2-tb-btn" data-action="zoomout" title="Zoom out">\u2212</button>' +
            '<button class="qp-v2-tb-btn" data-action="fit"     title="Fit">\u2922</button>';
        viewport.appendChild(toolbar);

        var wrap = document.createElement('div');
        wrap.className = 'env-topo-wrap';
        wrap.style.width  = canvasSize + 'px';
        wrap.style.height = canvasSize + 'px';
        viewport.appendChild(wrap);

        // ── SVG layer ────────────────────────────────────────────────────
        var SVG_NS = 'http://www.w3.org/2000/svg';
        var svg = document.createElementNS(SVG_NS, 'svg');
        svg.setAttribute('width',  canvasSize);
        svg.setAttribute('height', canvasSize);
        svg.style.cssText = 'position:absolute;top:0;left:0;pointer-events:none;overflow:visible;';
        wrap.appendChild(svg);

        // Arrow marker
        var defs = document.createElementNS(SVG_NS, 'defs');
        function makeMarker(id, color) {
            var m = document.createElementNS(SVG_NS, 'marker');
            m.setAttribute('id', id);
            m.setAttribute('viewBox', '0 0 10 10');
            m.setAttribute('refX', '9'); m.setAttribute('refY', '5');
            m.setAttribute('markerWidth', '6'); m.setAttribute('markerHeight', '6');
            m.setAttribute('orient', 'auto-start-reverse');
            var p = document.createElementNS(SVG_NS, 'path');
            p.setAttribute('d', 'M 0 0 L 10 5 L 0 10 z');
            p.setAttribute('fill', color);
            m.appendChild(p);
            return m;
        }
        defs.appendChild(makeMarker('env-arr-in',  '#f59e0b'));
        defs.appendChild(makeMarker('env-arr-out', '#38bdf8'));
        svg.appendChild(defs);

        // ── Draw host spokes ─────────────────────────────────────────────
        hosts.forEach(function (host, i) {
            var angle = (2 * Math.PI * i / n) - Math.PI / 2;
            var hx = cx + orbit * Math.cos(angle);
            var hy = cy + orbit * Math.sin(angle);

            // Line from server center to host
            var thick = Math.max(1.5, Math.min(5, 1 + Math.log2((host.connectionCount || 1) + 1)));
            var line = document.createElementNS(SVG_NS, 'line');
            line.setAttribute('x1', cx); line.setAttribute('y1', cy);
            line.setAttribute('x2', hx); line.setAttribute('y2', hy);
            line.setAttribute('stroke', 'rgba(100,116,139,0.5)');
            line.setAttribute('stroke-width', thick);
            svg.appendChild(line);
        });

        // ── Draw cross-server IN arrows ──────────────────────────────────
        crossIn.forEach(function (lnk, i) {
            var angle = (2 * Math.PI * i / Math.max(1, crossIn.length)) + Math.PI / 4;
            var nx = cx + xOrbit * Math.cos(angle);
            var ny = cy + xOrbit * Math.sin(angle);

            // Dashed arrow pointing toward server
            var dx = cx - nx, dy = cy - ny, dist = Math.sqrt(dx*dx + dy*dy);
            var ex = cx - (SRV_R / dist) * dx;
            var ey = cy - (SRV_R / dist) * dy;

            var line = document.createElementNS(SVG_NS, 'line');
            line.setAttribute('x1', nx); line.setAttribute('y1', ny);
            line.setAttribute('x2', ex); line.setAttribute('y2', ey);
            line.setAttribute('stroke', '#f59e0b');
            line.setAttribute('stroke-width', '2');
            line.setAttribute('stroke-dasharray', '6,3');
            line.setAttribute('marker-end', 'url(#env-arr-in)');
            svg.appendChild(line);

            // Cross-server node box
            var div = document.createElement('div');
            div.className = 'env-xsrv-node env-xsrv-in';
            div.style.left = (nx - XSRV_W / 2) + 'px';
            div.style.top  = (ny - XSRV_H / 2) + 'px';
            div.innerHTML = '<i class="fa-solid fa-server"></i> ' + esc(shortName(lnk.server)) +
                '<span class="env-xsrv-count">' + (lnk.count || '') + '</span>';
            div.title = lnk.server + ' connects here';
            wrap.appendChild(div);
        });

        // ── Draw cross-server OUT arrows ─────────────────────────────────
        crossOut.forEach(function (lnk, i) {
            var angle = (2 * Math.PI * i / Math.max(1, crossOut.length)) + Math.PI * 5 / 4;
            var nx = cx + xOrbit * Math.cos(angle);
            var ny = cy + xOrbit * Math.sin(angle);

            var dx = nx - cx, dy = ny - cy, dist = Math.sqrt(dx*dx + dy*dy);
            var sx = cx + (SRV_R / dist) * dx;
            var sy = cy + (SRV_R / dist) * dy;

            var line = document.createElementNS(SVG_NS, 'line');
            line.setAttribute('x1', sx); line.setAttribute('y1', sy);
            line.setAttribute('x2', nx); line.setAttribute('y2', ny);
            line.setAttribute('stroke', '#38bdf8');
            line.setAttribute('stroke-width', '2');
            line.setAttribute('stroke-dasharray', '6,3');
            line.setAttribute('marker-end', 'url(#env-arr-out)');
            svg.appendChild(line);

            var div = document.createElement('div');
            div.className = 'env-xsrv-node env-xsrv-out';
            div.style.left = (nx - XSRV_W / 2) + 'px';
            div.style.top  = (ny - XSRV_H / 2) + 'px';
            div.innerHTML = '<i class="fa-solid fa-server"></i> ' + esc(shortName(lnk.server)) +
                '<span class="env-xsrv-count">' + (lnk.count || '') + '</span>';
            div.title = 'This server connects to ' + lnk.server;
            wrap.appendChild(div);
        });

        // ── Server center node ───────────────────────────────────────────
        var srvDiv = document.createElement('div');
        srvDiv.className = 'env-topo-server';
        srvDiv.style.left   = (cx - SRV_R) + 'px';
        srvDiv.style.top    = (cy - SRV_R) + 'px';
        srvDiv.style.width  = (SRV_R * 2) + 'px';
        srvDiv.style.height = (SRV_R * 2) + 'px';
        var c = server.counts || {};
        srvDiv.innerHTML =
            '<i class="fa-solid fa-server" style="font-size:20px;color:var(--accent);"></i>' +
            '<div style="font-size:10px;font-weight:700;margin-top:4px;word-break:break-all;text-align:center;">' + esc(shortName(server.name)) + '</div>' +
            '<div style="font-size:9px;color:var(--text-secondary);margin-top:2px;">' +
                (c.hosts||0) + 'h · ' + (c.apps||0) + 'a · ' + (c.users||0) + 'u' +
            '</div>';
        wrap.appendChild(srvDiv);

        // ── Host nodes ───────────────────────────────────────────────────
        hosts.forEach(function (host, i) {
            var angle = (2 * Math.PI * i / n) - Math.PI / 2;
            var hx = cx + orbit * Math.cos(angle);
            var hy = cy + orbit * Math.sin(angle);

            var hn = host.hostname || '(unknown)';
            var display = hn.length > 18 ? hn.substring(0, 16) + '…' : hn;

            var div = document.createElement('div');
            div.className = 'env-topo-host';
            div.style.left = (hx - HOST_W / 2) + 'px';
            div.style.top  = (hy - HOST_H / 2) + 'px';
            div.title = hn;
            div.innerHTML =
                '<div class="env-topo-host-name">' + esc(display) + '</div>' +
                '<div class="env-topo-host-meta">' +
                    (host.connectionCount || 0) + ' conn · ' + (host.uniqueApps || 0) + ' apps' +
                '</div>';

            div.addEventListener('click', function () {
                // Highlight
                document.querySelectorAll('.env-topo-host').forEach(function (el) {
                    el.classList.remove('env-topo-host-active');
                });
                div.classList.add('env-topo-host-active');

                // Raise to Blazor
                if (window._envDotNetRef) {
                    window._envDotNetRef.invokeMethodAsync('OnHostNodeClicked', hn);
                }
            });

            wrap.appendChild(div);
        });

        // ── Pan / zoom ───────────────────────────────────────────────────
        var _tx = 0, _ty = 0, _scale = 1;
        var _drag = false, _lx = 0, _ly = 0;

        function applyXform() {
            wrap.style.transform = 'translate(' + _tx + 'px,' + _ty + 'px) scale(' + _scale + ')';
        }
        function clamp(s) { return Math.max(0.15, Math.min(4, s)); }
        function zoomAt(vx, vy, f) {
            var ns = clamp(_scale * f);
            _tx = vx - (vx - _tx) * (ns / _scale);
            _ty = vy - (vy - _ty) * (ns / _scale);
            _scale = ns; applyXform();
        }
        function fit() {
            var vw = viewport.clientWidth || 600;
            var vh = viewport.clientHeight || 260;
            var s = clamp(Math.min(vw / canvasSize, vh / canvasSize) * 0.9);
            _scale = s;
            _tx = (vw - canvasSize * s) / 2;
            _ty = (vh - canvasSize * s) / 2;
            applyXform();
        }

        viewport.addEventListener('wheel', function (e) {
            e.preventDefault();
            var r = viewport.getBoundingClientRect();
            zoomAt(e.clientX - r.left, e.clientY - r.top, e.deltaY < 0 ? 1.12 : 1/1.12);
        }, { passive: false });

        viewport.addEventListener('mousedown', function (e) {
            if (e.button !== 0) return;
            _drag = true; _lx = e.clientX; _ly = e.clientY;
            viewport.style.cursor = 'grabbing'; e.preventDefault();
        });
        var onMove = function (e) {
            if (!_drag) return;
            _tx += e.clientX - _lx; _ty += e.clientY - _ly;
            _lx = e.clientX; _ly = e.clientY; applyXform();
        };
        var onUp = function () { if (_drag) { _drag = false; viewport.style.cursor = ''; } };
        window.addEventListener('mousemove', onMove);
        window.addEventListener('mouseup', onUp);
        viewport.addEventListener('dblclick', fit);

        toolbar.addEventListener('click', function (e) {
            var btn = e.target.closest('[data-action]');
            if (!btn) return;
            var r = viewport.getBoundingClientRect();
            var vx = r.width / 2, vy = r.height / 2;
            if      (btn.dataset.action === 'zoomin')  zoomAt(vx, vy, 1.25);
            else if (btn.dataset.action === 'zoomout') zoomAt(vx, vy, 1/1.25);
            else if (btn.dataset.action === 'fit')     fit();
        });

        var obs = new MutationObserver(function () {
            if (!viewport.isConnected) {
                window.removeEventListener('mousemove', onMove);
                window.removeEventListener('mouseup', onUp);
                obs.disconnect();
            }
        });
        obs.observe(container, { childList: true });

        requestAnimationFrame(fit);
    }

    function shortName(name) {
        if (!name) return '';
        if (name.length <= 18) return name;
        var parts = name.split('\\');
        var m = parts[0].length > 13 ? parts[0].substring(0, 11) + '…' : parts[0];
        return parts.length > 1 ? m + '\\' + parts[1] : m;
    }

    // Register DotNet reference for Blazor callbacks
    function setHostCallback(dotNetRef) {
        window._envDotNetRef = dotNetRef;
    }

    return { renderMini: renderMini, setHostCallback: setHostCallback };
})();
