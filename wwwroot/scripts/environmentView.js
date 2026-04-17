/* In the name of God, the Merciful, the Compassionate */

/**
 * Environment View — Force-directed network topology graph.
 * Renders SQL Server nodes, host nodes, and cross-server links using canvas.
 * No external dependencies — custom spring/repulsion physics.
 */
window.environmentView = (function () {
    'use strict';

    // ── Colour palette ───────────────────────────────────────────────────
    var C = {
        bg:          '#0d1117',
        grid:        'rgba(255,255,255,0.025)',
        serverFill:  'rgba(16,185,129,0.18)',
        serverBorder:'#10b981',
        serverGlow:  'rgba(16,185,129,0.35)',
        serverText:  '#6ee7b7',
        hostFill:    'rgba(30,41,59,0.92)',
        hostBorder:  'rgba(100,116,139,0.55)',
        hostHover:   '#6366f1',
        hostActive:  '#6366f1',
        hostText:    '#e2e8f0',
        hostMeta:    '#94a3b8',
        edgeIdle:    'rgba(100,116,139,0.35)',
        edgeActive:  'rgba(99,102,241,0.6)',
        flowDot:     'rgba(99,102,241,0.85)',
        xlinkAmber:  'rgba(245,158,11,0.7)',
        xlinkBlue:   'rgba(56,189,248,0.7)',
        xDotAmber:   '#f59e0b',
        xDotBlue:    '#38bdf8',
        errBorder:   'rgba(239,68,68,0.6)',
        errText:     '#f87171'
    };

    var RAD_SERVER = 42;     // server node radius
    var HOST_W     = 136;
    var HOST_H     = 50;
    var FONT_SMALL = '10px "Segoe UI",system-ui,sans-serif';
    var FONT_NAME  = '600 12px "Segoe UI",system-ui,sans-serif';

    // Per-canvas state keyed by containerId
    var _instances = {};

    // ── Tooltip ──────────────────────────────────────────────────────────
    var _tip = null;
    function getTooltip() {
        if (!_tip) {
            _tip = document.createElement('div');
            _tip.id = 'env-topo-tip';
            _tip.style.cssText =
                'position:fixed;pointer-events:none;z-index:9999;display:none;' +
                'background:rgba(15,23,42,0.96);border:1px solid rgba(99,102,241,0.5);' +
                'border-radius:6px;padding:7px 10px;font-size:11px;color:#e2e8f0;' +
                'line-height:1.5;max-width:220px;box-shadow:0 4px 20px rgba(0,0,0,0.6);';
            document.body.appendChild(_tip);
        }
        return _tip;
    }
    function showTip(x, y, html) {
        var t = getTooltip();
        t.innerHTML = html;
        t.style.display = 'block';
        var tw = t.offsetWidth, th = t.offsetHeight;
        t.style.left = Math.min(x + 12, window.innerWidth  - tw - 8) + 'px';
        t.style.top  = Math.min(y + 12, window.innerHeight - th - 8) + 'px';
    }
    function hideTip() { getTooltip().style.display = 'none'; }

    // ── Force simulation ─────────────────────────────────────────────────
    function createSimulation(nodes, edges, W, H) {
        var REPEL  = 9000;
        var SPRING = 0.035;
        var DAMP   = 0.72;
        var GRAV   = 0.018;

        // Target rest lengths
        function restLen(a, b) {
            if (a.type === 'server' && b.type === 'server') return 320;
            if (a.type === 'server' || b.type === 'server') return 200;
            return 160;
        }

        function tick() {
            var n = nodes.length;
            // Reset forces
            for (var i = 0; i < n; i++) {
                nodes[i].fx = 0; nodes[i].fy = 0;
            }

            // Repulsion between all pairs (only push non-pinned nodes)
            for (var i = 0; i < n; i++) {
                for (var j = i + 1; j < n; j++) {
                    var ni = nodes[i], nj = nodes[j];
                    var dx = ni.x - nj.x, dy = ni.y - nj.y;
                    var d2 = dx*dx + dy*dy + 1;
                    var f  = REPEL / d2;
                    var ux = dx / Math.sqrt(d2), uy = dy / Math.sqrt(d2);
                    if (!ni.pinned) { ni.fx += f * ux; ni.fy += f * uy; }
                    if (!nj.pinned) { nj.fx -= f * ux; nj.fy -= f * uy; }
                }
            }

            // Spring forces along edges (only pull non-pinned nodes)
            for (var k = 0; k < edges.length; k++) {
                var e = edges[k];
                var a = e.source, b = e.target;
                var dx = b.x - a.x, dy = b.y - a.y;
                var d = Math.sqrt(dx*dx + dy*dy) || 1;
                var rl = restLen(a, b);
                var f = SPRING * (d - rl);
                var ux = dx/d, uy = dy/d;
                if (!a.pinned) { a.fx += f * ux; a.fy += f * uy; }
                if (!b.pinned) { b.fx -= f * ux; b.fy -= f * uy; }
            }

            // Gravity toward center
            var cx = W/2, cy = H/2;
            for (var i = 0; i < n; i++) {
                var nd = nodes[i];
                if (nd.pinned) continue;
                nd.fx += GRAV * (cx - nd.x);
                nd.fy += GRAV * (cy - nd.y);
            }

            // Integrate
            for (var i = 0; i < n; i++) {
                var nd = nodes[i];
                if (nd.pinned) continue;
                nd.vx = (nd.vx + nd.fx) * DAMP;
                nd.vy = (nd.vy + nd.fy) * DAMP;
                nd.x += nd.vx;
                nd.y += nd.vy;
                // Boundary
                nd.x = Math.max(80, Math.min(W - 80, nd.x));
                nd.y = Math.max(80, Math.min(H - 80, nd.y));
            }
        }

        return { tick: tick };
    }

    // ── Drawing helpers ──────────────────────────────────────────────────
    function drawRoundRect(ctx, x, y, w, h, r) {
        ctx.beginPath();
        ctx.moveTo(x+r, y);
        ctx.lineTo(x+w-r, y);
        ctx.arcTo(x+w, y,   x+w, y+r,   r);
        ctx.lineTo(x+w, y+h-r);
        ctx.arcTo(x+w, y+h, x+w-r, y+h, r);
        ctx.lineTo(x+r,  y+h);
        ctx.arcTo(x,   y+h, x,   y+h-r, r);
        ctx.lineTo(x,   y+r);
        ctx.arcTo(x,   y,   x+r, y,     r);
        ctx.closePath();
    }

    function drawServerNode(ctx, nd, hover) {
        var x = nd.x, y = nd.y, r = RAD_SERVER;
        // Glow
        if (hover) {
            ctx.save();
            ctx.shadowColor = C.serverGlow;
            ctx.shadowBlur  = 28;
        }
        // Circle fill
        ctx.beginPath();
        ctx.arc(x, y, r, 0, Math.PI*2);
        var grad = ctx.createRadialGradient(x, y-r*0.3, r*0.1, x, y, r);
        grad.addColorStop(0, 'rgba(16,185,129,0.35)');
        grad.addColorStop(1, 'rgba(6,78,59,0.45)');
        ctx.fillStyle = grad;
        ctx.fill();
        // Border
        ctx.strokeStyle = nd.error ? C.errBorder : C.serverBorder;
        ctx.lineWidth   = hover ? 2.5 : 1.8;
        ctx.stroke();
        if (hover) ctx.restore();

        // Rack-unit segments (decorative tech detail)
        ctx.save();
        ctx.globalAlpha = 0.18;
        ctx.strokeStyle = C.serverBorder;
        ctx.lineWidth   = 1;
        for (var i = -1; i <= 1; i++) {
            ctx.beginPath();
            var ly = y + i * 11;
            var hw = Math.sqrt(Math.max(0, r*r - (ly-y)*(ly-y))) * 0.75;
            ctx.moveTo(x - hw, ly);
            ctx.lineTo(x + hw, ly);
            ctx.stroke();
        }
        ctx.restore();

        // Server icon (Unicode approximation via text)
        ctx.fillStyle = nd.error ? C.errText : C.serverText;
        ctx.font      = '700 14px "Segoe UI",sans-serif';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText('⬛', x, y - 10); // placeholder — FA icons not on canvas

        // Name
        ctx.font      = 'bold 10px "Segoe UI",sans-serif';
        ctx.fillStyle = nd.error ? C.errText : C.serverText;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        var label = shortName(nd.label, 16);
        ctx.fillText(label, x, y + 8);

        // Counts row
        if (!nd.error && nd.counts) {
            ctx.font      = '9px "Segoe UI",sans-serif';
            ctx.fillStyle = 'rgba(110,231,183,0.7)';
            var meta = (nd.counts.hosts||0)+'h · '+(nd.counts.apps||0)+'a · '+(nd.counts.dbs||0)+'db';
            ctx.fillText(meta, x, y + 22);
        }
        if (nd.error) {
            ctx.font      = '9px "Segoe UI",sans-serif';
            ctx.fillStyle = C.errText;
            ctx.fillText('error', x, y + 22);
        }
    }

    function drawPinDot(ctx, nd) {
        // Small dot in top-right corner to indicate node is manually pinned
        var px = nd.type === 'server' ? nd.x + RAD_SERVER * 0.65 : nd.x + HOST_W/2 - 5;
        var py = nd.type === 'server' ? nd.y - RAD_SERVER * 0.65 : nd.y - HOST_H/2 + 5;
        ctx.beginPath();
        ctx.arc(px, py, 4, 0, Math.PI*2);
        ctx.fillStyle = 'rgba(245,158,11,0.85)';
        ctx.fill();
    }

    function drawHostNode(ctx, nd, hover, active) {
        var x = nd.x - HOST_W/2, y = nd.y - HOST_H/2;
        var bcolor = active ? C.hostActive : hover ? C.hostHover : C.hostBorder;
        var alpha  = active ? 0.18 : hover ? 0.1 : 0;

        // Shadow/glow
        if (hover || active) {
            ctx.save();
            ctx.shadowColor = active ? 'rgba(99,102,241,0.4)' : 'rgba(99,102,241,0.2)';
            ctx.shadowBlur  = 12;
        }

        drawRoundRect(ctx, x, y, HOST_W, HOST_H, 7);
        ctx.fillStyle = 'rgba(30,41,59,' + (0.88 + alpha) + ')';
        ctx.fill();
        ctx.strokeStyle = bcolor;
        ctx.lineWidth   = active || hover ? 1.8 : 1;
        ctx.stroke();

        if (hover || active) ctx.restore();

        // Connection bar (width proportional to conn count vs max)
        var barW = Math.round((nd.connFrac || 0) * (HOST_W - 16));
        if (barW > 0) {
            ctx.fillStyle = active ? 'rgba(99,102,241,0.35)' : 'rgba(99,102,241,0.18)';
            drawRoundRect(ctx, x+8, y+HOST_H-8, barW, 4, 2);
            ctx.fill();
        }

        // Hostname
        ctx.font         = FONT_NAME;
        ctx.fillStyle    = C.hostText;
        ctx.textAlign    = 'left';
        ctx.textBaseline = 'top';
        var label = shortName(nd.label, 17);
        ctx.fillText(label, x+10, y+8);

        // Meta line
        ctx.font      = FONT_SMALL;
        ctx.fillStyle = C.hostMeta;
        var meta = (nd.data.connectionCount||0) + ' conn';
        if (nd.data.uniqueApps)  meta += ' · ' + nd.data.uniqueApps + ' apps';
        if (nd.data.uniqueDbs)   meta += ' · ' + nd.data.uniqueDbs + ' dbs';
        ctx.fillText(meta, x+10, y+26);
    }

    function drawEdge(ctx, a, b, t, isXlink, xlinkColor) {
        var dx = b.x - a.x, dy = b.y - a.y;
        var dist = Math.sqrt(dx*dx + dy*dy) || 1;
        var ux = dx/dist, uy = dy/dist;

        // Offset edge start/end to node boundaries
        var rA = a.type === 'server' ? RAD_SERVER : 0;
        var rB = b.type === 'server' ? RAD_SERVER : 0;
        var sx = a.x + ux * rA, sy = a.y + uy * rA;
        var ex = b.x - ux * rB, ey = b.y - uy * rB;

        if (isXlink) {
            // Curved dashed arc for cross-server links
            ctx.save();
            ctx.setLineDash([7, 4]);
            ctx.strokeStyle = xlinkColor || C.xlinkAmber;
            ctx.lineWidth   = 1.8;
            var mx = (sx+ex)/2, my = (sy+ey)/2;
            var perp = 60;
            var px = mx - uy*perp, py = my + ux*perp;
            ctx.beginPath();
            ctx.moveTo(sx, sy);
            ctx.quadraticCurveTo(px, py, ex, ey);
            ctx.stroke();
            ctx.setLineDash([]);
            ctx.restore();
            return;
        }

        // Straight edge
        ctx.beginPath();
        ctx.moveTo(sx, sy);
        ctx.lineTo(ex, ey);
        ctx.strokeStyle = C.edgeIdle;
        ctx.lineWidth   = Math.max(1, Math.min(3.5, 1 + (b.data ? Math.log2((b.data.connectionCount||1)+1)*0.4 : 1)));
        ctx.stroke();

        // Animated flow dot
        var tp = ((t * 0.0004) + (a.x * 0.001)) % 1;
        var dpx = sx + (ex-sx)*tp, dpy = sy + (ey-sy)*tp;
        ctx.beginPath();
        ctx.arc(dpx, dpy, 2.5, 0, Math.PI*2);
        ctx.fillStyle = C.flowDot;
        ctx.fill();
    }

    function drawGrid(ctx, W, H) {
        ctx.save();
        ctx.strokeStyle = C.grid;
        ctx.lineWidth   = 1;
        var step = 28;
        for (var x = 0; x < W; x += step) {
            ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, H); ctx.stroke();
        }
        for (var y = 0; y < H; y += step) {
            ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(W, y); ctx.stroke();
        }
        ctx.restore();
    }

    // ── Hit testing ──────────────────────────────────────────────────────
    function hitNode(nodes, wx, wy) {
        for (var i = nodes.length-1; i >= 0; i--) {
            var nd = nodes[i];
            if (nd.type === 'server') {
                var dx = wx - nd.x, dy = wy - nd.y;
                if (dx*dx + dy*dy <= RAD_SERVER*RAD_SERVER) return nd;
            } else {
                var hx = nd.x - HOST_W/2, hy = nd.y - HOST_H/2;
                if (wx >= hx && wx <= hx+HOST_W && wy >= hy && wy <= hy+HOST_H) return nd;
            }
        }
        return null;
    }

    // ── Main render entry ─────────────────────────────────────────────────
    function renderTopology(containerId, jsonData) {
        var container = document.getElementById(containerId);
        if (!container) return;

        // Destroy previous instance
        var prev = _instances[containerId];
        if (prev) { prev.destroy(); delete _instances[containerId]; }

        container.innerHTML = '';

        var data;
        try { data = typeof jsonData === 'string' ? JSON.parse(jsonData) : jsonData; }
        catch (e) { container.innerHTML = '<p style="color:#f44;padding:16px;">Parse error: '+e.message+'</p>'; return; }

        var servers   = data.servers   || [];
        var crossLinks= data.crossLinks|| [];
        if (!servers.length) { container.innerHTML = '<p style="color:#64748b;padding:16px;font-size:13px;">No data to display.</p>'; return; }

        // ── Viewport container ───────────────────────────────────────────
        var vp = document.createElement('div');
        vp.className = 'env-topo-viewport';
        container.appendChild(vp);

        // Toolbar
        var tb = document.createElement('div');
        tb.className = 'env-fd-toolbar';
        tb.innerHTML =
            '<button data-action="zoomin"   title="Zoom in">+</button>' +
            '<button data-action="zoomout"  title="Zoom out">−</button>' +
            '<button data-action="fit"      title="Fit to screen">⤢</button>' +
            '<button data-action="relayout" title="Re-run layout (unpins all nodes)" class="env-fd-btn-wide">↺ Re-layout</button>' +
            '<span class="env-fd-legend">' +
                '<span class="env-fd-leg-dot" style="background:#10b981"></span>SQL Server ' +
                '<span class="env-fd-leg-dot" style="background:#6366f1;margin-left:8px"></span>Host ' +
                '<span class="env-fd-leg-dash" style="border-color:#f59e0b;margin-left:8px"></span>Cross-server link ' +
                '<span style="color:#64748b;font-size:10px;margin-left:10px;">Drag to pin · Dbl-click node to unpin · Dbl-click canvas to fit</span>' +
            '</span>';
        vp.appendChild(tb);

        // Canvas
        var cvs = document.createElement('canvas');
        cvs.style.cssText = 'display:block;width:100%;height:100%;cursor:grab;';
        vp.appendChild(cvs);

        // Size canvas to viewport
        function resize() {
            cvs.width  = vp.clientWidth  || 800;
            cvs.height = (vp.clientHeight || 400) - 36; // subtract toolbar
        }
        resize();

        var W = cvs.width, H = cvs.height;

        // ── Build graph ──────────────────────────────────────────────────
        var nodes = [], edges = [];

        // Find max connection count for sizing
        var maxConn = 1;
        servers.forEach(function(s) {
            (s.hosts||[]).forEach(function(h) { maxConn = Math.max(maxConn, h.connectionCount||0); });
        });

        // Place server nodes in a row/circle depending on count
        var ns = servers.length;
        servers.forEach(function(s, si) {
            var angle = ns > 1 ? (2*Math.PI*si/ns) - Math.PI/2 : 0;
            var r     = ns > 1 ? Math.min(W, H) * 0.3 : 0;
            var nd = {
                id:     'srv_' + si,
                type:   'server',
                label:  s.name,
                counts: s.counts,
                error:  s.error || null,
                x:      W/2 + r * Math.cos(angle) + (Math.random()-0.5)*40,
                y:      H/2 + r * Math.sin(angle) + (Math.random()-0.5)*40,
                vx: 0, vy: 0, fx: 0, fy: 0,
                pinned: false
            };
            nd.serverIndex = si;
            nodes.push(nd);

            // Host nodes for this server
            (s.hosts||[]).forEach(function(h, hi) {
                var spread = Math.min(W, H) * 0.22;
                var ha = (2*Math.PI*hi / Math.max(1, (s.hosts||[]).length)) + angle;
                var hn = {
                    id:       'host_' + si + '_' + hi,
                    type:     'host',
                    label:    h.hostname,
                    data:     h,
                    connFrac: (h.connectionCount||0) / maxConn,
                    x:        nd.x + spread * Math.cos(ha) + (Math.random()-0.5)*30,
                    y:        nd.y + spread * Math.sin(ha) + (Math.random()-0.5)*30,
                    vx: 0, vy: 0, fx: 0, fy: 0,
                    pinned: false,
                    serverNodeId: nd.id
                };
                nodes.push(hn);
                edges.push({ source: nd, target: hn, xlink: false });
            });
        });

        // Cross-server edges
        crossLinks.forEach(function(lnk) {
            var a = nodes.find(function(n) { return n.type==='server' && n.label===lnk.fromServer; });
            var b = nodes.find(function(n) { return n.type==='server' && n.label===lnk.toServer; });
            if (a && b) edges.push({ source: a, target: b, xlink: true, count: lnk.connectionCount });
        });

        // ── Animation loop ───────────────────────────────────────────────
        var ctx    = cvs.getContext('2d');
        var sim    = createSimulation(nodes, edges, W, H);
        var _raf   = null;
        var _t     = 0;
        var _steps = 0;
        var MAX_SIM_STEPS = 300;

        // Interaction state
        var _hover  = null;
        var _active = null;
        var _tx = 0, _ty = 0, _scale = 1;
        var _drag = false, _lx = 0, _ly = 0;
        var _nodeDrag = null, _ndOffX = 0, _ndOffY = 0;
        var _mouseDownPos = null;   // {x,y} at mousedown — used to distinguish click from drag
        var DRAG_THRESHOLD = 5;     // px movement before treating as a drag

        function toWorld(cx, cy) {
            return { x: (cx - _tx) / _scale, y: (cy - _ty) / _scale };
        }

        function draw() {
            ctx.clearRect(0, 0, W, H);

            // Background
            ctx.fillStyle = C.bg;
            ctx.fillRect(0, 0, W, H);
            drawGrid(ctx, W, H);

            ctx.save();
            ctx.translate(_tx, _ty);
            ctx.scale(_scale, _scale);

            // Edges first
            edges.forEach(function(e) {
                drawEdge(ctx, e.source, e.target, _t, e.xlink,
                    e.xlink ? (e.source.serverIndex < e.target.serverIndex ? C.xlinkAmber : C.xlinkBlue) : null);
            });

            // Nodes
            nodes.forEach(function(nd) {
                if (nd.type === 'server') {
                    drawServerNode(ctx, nd, nd === _hover);
                } else {
                    drawHostNode(ctx, nd, nd === _hover, nd === _active);
                }
                if (nd.pinned) drawPinDot(ctx, nd);
            });

            ctx.restore();

            _t++;
        }

        function loop() {
            if (_steps < MAX_SIM_STEPS) { sim.tick(); _steps++; }
            draw();
            _raf = requestAnimationFrame(loop);
        }

        // ── Fit ──────────────────────────────────────────────────────────
        function fit() {
            if (!nodes.length) return;
            var minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
            nodes.forEach(function(n) {
                var pad = n.type==='server' ? RAD_SERVER : Math.max(HOST_W, HOST_H)/2;
                minX = Math.min(minX, n.x-pad); minY = Math.min(minY, n.y-pad);
                maxX = Math.max(maxX, n.x+pad); maxY = Math.max(maxY, n.y+pad);
            });
            var gw = maxX-minX || 1, gh = maxY-minY || 1;
            var s  = Math.min(0.92, Math.min(W/gw, H/gh) * 0.88);
            _scale = s;
            _tx    = (W - gw*s) / 2 - minX*s;
            _ty    = (H - gh*s) / 2 - minY*s;
        }

        // Initial fit after short settle
        setTimeout(fit, 400);

        // ── Mouse interaction ────────────────────────────────────────────
        function canvasXY(e) {
            var r = cvs.getBoundingClientRect();
            var cx = (e.clientX||e.touches[0].clientX) - r.left;
            var cy = (e.clientY||e.touches[0].clientY) - r.top;
            return { cx: cx, cy: cy };
        }

        cvs.addEventListener('mousemove', function(e) {
            var p = canvasXY(e); var w = toWorld(p.cx, p.cy);
            var hit = hitNode(nodes, w.x, w.y);
            if (_nodeDrag) {
                _nodeDrag.x = w.x + _ndOffX;
                _nodeDrag.y = w.y + _ndOffY;
                _nodeDrag.vx = 0; _nodeDrag.vy = 0;
                return;
            }
            if (_drag) {
                _tx += e.clientX - _lx; _ty += e.clientY - _ly;
                _lx = e.clientX; _ly = e.clientY; return;
            }
            if (hit !== _hover) { _hover = hit; cvs.style.cursor = hit ? 'pointer' : 'grab'; }
            if (hit) {
                var html = '<strong style="color:#e2e8f0">' + esc(hit.label) + '</strong>';
                if (hit.type === 'server') {
                    var c = hit.counts||{};
                    html += '<br><span style="color:#64748b">SQL Server</span>' +
                        '<br>Hosts: '+c.hosts+' · Apps: '+c.apps+' · DBs: '+c.dbs;
                    if (hit.error) html += '<br><span style="color:#f87171">'+esc(hit.error)+'</span>';
                } else {
                    var d = hit.data||{};
                    html += '<br><span style="color:#64748b">Host</span>' +
                        '<br>'+d.connectionCount+' connections' +
                        '<br>'+d.uniqueApps+' apps · '+d.uniqueUsers+' users · '+d.uniqueDbs+' DBs' +
                        '<br><span style="color:#6366f1">Click to inspect</span>';
                }
                showTip(e.clientX, e.clientY, html);
            } else hideTip();
        });

        cvs.addEventListener('mousedown', function(e) {
            if (e.button !== 0) return;
            var p = canvasXY(e); var w = toWorld(p.cx, p.cy);
            var hit = hitNode(nodes, w.x, w.y);
            _mouseDownPos = { x: e.clientX, y: e.clientY };
            if (hit) {
                _nodeDrag = hit;
                _ndOffX   = hit.x - w.x;
                _ndOffY   = hit.y - w.y;
                hit.pinned = true;  // pin immediately; stays pinned after drop
                hit.vx = 0; hit.vy = 0;
                // do NOT restart simulation — only dragged node moves
            } else {
                _drag = true; _lx = e.clientX; _ly = e.clientY;
                cvs.style.cursor = 'grabbing';
            }
            e.preventDefault();
        });

        function onMouseUp() {
            var wasDraggingNode = _nodeDrag;
            if (_nodeDrag) {
                // Keep node pinned where user dropped it; zero velocity
                _nodeDrag.vx = 0; _nodeDrag.vy = 0;
                _nodeDrag = null;
            }
            if (_drag) { _drag = false; cvs.style.cursor = _hover ? 'pointer' : 'grab'; }
            return wasDraggingNode;
        }
        window.addEventListener('mouseup', onMouseUp);

        cvs.addEventListener('click', function(e) {
            // Ignore if the mouse moved more than threshold — it was a drag, not a click
            if (_mouseDownPos) {
                var dx = e.clientX - _mouseDownPos.x;
                var dy = e.clientY - _mouseDownPos.y;
                if (dx*dx + dy*dy > DRAG_THRESHOLD*DRAG_THRESHOLD) { _mouseDownPos = null; return; }
                _mouseDownPos = null;
            }
            var p = canvasXY(e); var w = toWorld(p.cx, p.cy);
            var hit = hitNode(nodes, w.x, w.y);
            if (hit && hit.type === 'host') {
                var toggling = (_active === hit);
                _active = toggling ? null : hit;
                if (!toggling && window._envDotNetRef) {
                    window._envDotNetRef.invokeMethodAsync('OnHostNodeClicked', hit.label);
                } else if (toggling) {
                    // Clicking active node again — close the detail panel
                    window._envDotNetRef && window._envDotNetRef.invokeMethodAsync('OnHostNodeClicked', '');
                }
            }
        });

        cvs.addEventListener('dblclick', function(e) {
            var p = canvasXY(e); var w = toWorld(p.cx, p.cy);
            var hit = hitNode(nodes, w.x, w.y);
            if (hit) {
                // Unpin this node — release it back to simulation
                hit.pinned = false;
                hit.vx = 0; hit.vy = 0;
                if (_steps >= MAX_SIM_STEPS) _steps = Math.max(0, MAX_SIM_STEPS - 120);
            } else {
                fit();
            }
        });

        cvs.addEventListener('wheel', function(e) {
            e.preventDefault();
            var p = canvasXY(e);
            var f = e.deltaY < 0 ? 1.12 : 1/1.12;
            var ns = Math.max(0.1, Math.min(5, _scale * f));
            _tx = p.cx - (p.cx - _tx) * (ns / _scale);
            _ty = p.cy - (p.cy - _ty) * (ns / _scale);
            _scale = ns;
        }, { passive: false });

        tb.addEventListener('click', function(e) {
            var btn = e.target.closest('[data-action]');
            if (!btn) return;
            var f = btn.dataset.action;
            if      (f==='zoomin')   { _scale = Math.min(5, _scale*1.25); }
            else if (f==='zoomout')  { _scale = Math.max(0.1, _scale/1.25); }
            else if (f==='fit')      fit();
            else if (f==='relayout') {
                // Unpin all nodes, reset velocities, restart simulation
                nodes.forEach(function(nd) { nd.pinned = false; nd.vx = 0; nd.vy = 0; nd.fx = 0; nd.fy = 0; });
                _steps = 0;
            }
        });

        // ── ResizeObserver ───────────────────────────────────────────────
        var ro = new ResizeObserver(function() {
            resize();
            W = cvs.width; H = cvs.height;
            sim = createSimulation(nodes, edges, W, H);
            _steps = 0;
            setTimeout(fit, 200);
        });
        ro.observe(vp);

        // Start loop
        loop();

        // Cleanup handle
        _instances[containerId] = {
            destroy: function() {
                if (_raf) cancelAnimationFrame(_raf);
                ro.disconnect();
                hideTip();
                window.removeEventListener('mouseup', arguments.callee);
            }
        };
    }

    function esc(s) {
        var d = document.createElement('div');
        d.textContent = String(s||'');
        return d.innerHTML;
    }

    function shortName(name, max) {
        if (!name) return '';
        max = max || 18;
        if (name.length <= max) return name;
        var parts = name.split('\\');
        var m = parts[0].length > max-3 ? parts[0].substring(0, max-4)+'…' : parts[0];
        return parts.length > 1 ? m+'\\'+parts[1] : m;
    }

    function setHostCallback(dotNetRef) {
        window._envDotNetRef = dotNetRef || null;
    }

    // Keep renderMini as alias so existing Blazor calls don't break during transition
    function renderMini(containerId, jsonData) {
        // Wrap single-server legacy format into multi-server format
        var data;
        try { data = typeof jsonData === 'string' ? JSON.parse(jsonData) : jsonData; }
        catch(e) { return; }
        renderTopology(containerId, {
            servers: [data.server ? Object.assign({}, data.server, { hosts: data.hosts }) : {}],
            crossLinks: (data.crossIn||[]).map(function(l){ return { fromServer: l.server, toServer: data.server&&data.server.name||'', connectionCount: l.count }; })
                .concat((data.crossOut||[]).map(function(l){ return { fromServer: data.server&&data.server.name||'', toServer: l.server, connectionCount: l.count }; }))
        });
    }

    return {
        renderTopology:  renderTopology,
        renderMini:      renderMini,
        setHostCallback: setHostCallback
    };
})();
