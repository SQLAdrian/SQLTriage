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
        bg:          'transparent',
        grid:        'rgba(45,212,191,0.05)',
        serverFill:  'rgba(45,212,191,0.15)',
        serverBorder:'#2dd4bf',
        serverGlow:  'rgba(45,212,191,0.5)',
        serverText:  '#5eead4',
        hostFill:    'rgba(18,24,27,0.85)',
        hostBorder:  'rgba(255,255,255,0.1)',
        hostHover:   '#2dd4bf',
        hostActive:  '#2dd4bf',
        hostText:    '#f1f5f9',
        hostMeta:    '#94a3b8',
        edgeIdle:    'rgba(255,255,255,0.15)',
        edgeActive:  'rgba(45,212,191,0.6)',
        flowDot:     '#2dd4bf',
        xlinkAmber:  'rgba(245,158,11,0.8)',
        xlinkBlue:   'rgba(56,189,248,0.8)',
        xDotAmber:   '#f59e0b',
        xDotBlue:    '#38bdf8',
        errBorder:   'rgba(239,68,68,0.8)',
        errText:     '#f87171'
    };

    var RAD_SERVER = 42;     // server node radius
    var HOST_W     = 136;
    var HOST_H     = 50;
    var FONT_SMALL = '10px "Segoe UI",system-ui,sans-serif';
    var FONT_NAME  = '600 12px "Segoe UI",system-ui,sans-serif';

    // Dig Deeper edge kinds — colour + display label per server-to-server relationship
    var EDGE_KINDS = {
        LinkedServer:          { color: 'rgba(56,189,248,0.85)',  label: 'Linked server' },
        ReplicationPublisher:  { color: 'rgba(192,132,252,0.9)',  label: 'Replication' },
        ReplicationSubscriber: { color: 'rgba(192,132,252,0.9)',  label: 'Replication' },
        AgReplica:             { color: 'rgba(52,211,153,0.9)',   label: 'AG replica' },
        MirrorPartner:         { color: 'rgba(45,212,191,0.9)',   label: 'Mirroring' },
        LogShipPrimary:        { color: 'rgba(251,146,60,0.9)',   label: 'Log shipping' },
        LogShipSecondary:      { color: 'rgba(251,146,60,0.9)',   label: 'Log shipping' }
    };

    // Auto-group clients above this many total hosts — per-host nodes clump into an unusable blob
    var GROUP_THRESHOLD = 30;

    // Node positions + viewport per container. Survives re-renders so live discovery
    // (which streams deltas and re-renders) grows the graph instead of resetting it.
    var _posCache = {};

    // Edge currently under the cursor (module-level so drawEdge can highlight it)
    var _hoverEdgeRef = null;
    // Focused server (module-level): hovering a server brightens its links and dims the rest —
    // the key to reading a dense linked-server mesh. Set per frame by the active instance.
    var _focusNode = null;

    // #3 Scale-aware render vars — set per frame by the active instance, read by the
    // module-level draw + hit-test functions. r = effective server radius (shrinks when dense),
    // dense = drop decorative animation, labels = draw text (culled when zoomed out), find = match node.
    var _rv = { r: RAD_SERVER, dense: false, labels: true, find: null };

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

        // Target rest lengths. Server-to-server links in a big mesh get a longer rest length so
        // the hairball spreads instead of bunching at the minimum spacing.
        function restLen(a, b) {
            if (a.type === 'server' && b.type === 'server') {
                var sz = Math.max(a.compSize || 0, b.compSize || 0);
                return sz > 6 ? 440 : 320;
            }
            if (a.type === 'server' || b.type === 'server') return 200;
            return 160;
        }

        function tick() {
            var n = nodes.length;
            // Reset forces
            for (var i = 0; i < n; i++) {
                nodes[i].fx = 0; nodes[i].fy = 0;
            }

            // Repulsion and collision between all pairs
            var PAD = 16;
            for (var i = 0; i < n; i++) {
                for (var j = i + 1; j < n; j++) {
                    var ni = nodes[i], nj = nodes[j];
                    var dx = ni.x - nj.x, dy = ni.y - nj.y;
                    var d2 = dx*dx + dy*dy + 1;
                    var d = Math.sqrt(d2);
                    
                    // 1. Soft inverse-square repulsion
                    var f  = REPEL / d2;
                    var ux = dx / d, uy = dy / d;
                    if (!ni.pinned) { ni.fx += f * ux; ni.fy += f * uy; }
                    if (!nj.pinned) { nj.fx -= f * ux; nj.fy -= f * uy; }

                    // 2. Hard bounding-box collision
                    var wi = ni.type === 'server' ? RAD_SERVER : HOST_W / 2;
                    var hi = ni.type === 'server' ? RAD_SERVER : HOST_H / 2;
                    var wj = nj.type === 'server' ? RAD_SERVER : HOST_W / 2;
                    var hj = nj.type === 'server' ? RAD_SERVER : HOST_H / 2;

                    var minDx = wi + wj + PAD;
                    var minDy = hi + hj + PAD;
                    var absDx = Math.abs(dx);
                    var absDy = Math.abs(dy);

                    if (absDx < minDx && absDy < minDy) {
                        var overlapX = minDx - absDx;
                        var overlapY = minDy - absDy;
                        // Push apart along the axis of least penetration
                        if (overlapX < overlapY) {
                            var pushX = (overlapX * 0.5 + 0.5) * (dx >= 0 ? 1 : -1);
                            if (!ni.pinned) { ni.x += pushX; ni.vx *= 0.5; }
                            if (!nj.pinned) { nj.x -= pushX; nj.vx *= 0.5; }
                        } else {
                            var pushY = (overlapY * 0.5 + 0.5) * (dy >= 0 ? 1 : -1);
                            if (!ni.pinned) { ni.y += pushY; ni.vy *= 0.5; }
                            if (!nj.pinned) { nj.y -= pushY; nj.vy *= 0.5; }
                        }
                    }
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

            // Gravity toward each node's anchor (#5 per-component) or the canvas centre.
            // Pull strength is per-node (nd.gpull): tight for small islands so they stay distinct,
            // weak for a big mesh so mutual repulsion can spread it across the canvas instead of
            // collapsing it into one pile.
            var cx = W/2, cy = H/2;
            for (var i = 0; i < n; i++) {
                var nd = nodes[i];
                if (nd.pinned) continue;
                var ax = (nd.ax != null) ? nd.ax : cx;
                var ay = (nd.ay != null) ? nd.ay : cy;
                var g = (nd.gpull != null) ? GRAV * nd.gpull : GRAV;
                nd.fx += g * (ax - nd.x);
                nd.fy += g * (ay - nd.y);
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

    function drawServerNode(ctx, nd, hover, timeMs) {
        var x = nd.x, y = nd.y, r = _rv.r;
        var dim = nd.dim && !hover;

        ctx.save();
        if (dim) ctx.globalAlpha = 0.28;

        // #4 find-match highlight ring
        if (_rv.find === nd) {
            ctx.beginPath(); ctx.arc(x, y, r + 8, 0, Math.PI*2);
            ctx.strokeStyle = '#fbbf24'; ctx.lineWidth = 3; ctx.stroke();
        }

        // Gravity wave animation — skipped when dense (#3): pure noise at 50 nodes, and costly.
        if (!_rv.dense && !dim) {
            ctx.save();
            var wave1 = (timeMs % 2000) / 2000;
            var wave2 = ((timeMs + 1000) % 2000) / 2000;
            var glowColor = nd.error ? 'rgba(239,68,68,' : 'rgba(45,212,191,';
            ctx.beginPath(); ctx.arc(x, y, r + wave1 * 30, 0, Math.PI*2);
            ctx.strokeStyle = glowColor + (1 - wave1) * 0.5 + ')';
            ctx.stroke();
            ctx.beginPath(); ctx.arc(x, y, r + wave2 * 30, 0, Math.PI*2);
            ctx.strokeStyle = glowColor + (1 - wave2) * 0.5 + ')';
            ctx.stroke();
            ctx.restore();
        }

        // Glow
        if (hover) {
            ctx.save();
            ctx.shadowColor = nd.error ? C.errBorder : C.serverGlow;
            ctx.shadowBlur  = 30;
        }
        // Circle fill
        ctx.beginPath();
        ctx.arc(x, y, r, 0, Math.PI*2);
        var grad = ctx.createRadialGradient(x, y-r*0.3, r*0.1, x, y, r);
        grad.addColorStop(0, nd.error ? 'rgba(239,68,68,0.2)' : 'rgba(45,212,191,0.35)');
        grad.addColorStop(1, nd.error ? 'rgba(153,27,27,0.45)' : 'rgba(15,118,110,0.45)');
        ctx.fillStyle = grad;
        ctx.fill();
        // Border — dashed for servers discovered by Dig Deeper but not yet in the catalogue
        ctx.strokeStyle = nd.error ? C.errBorder : C.serverBorder;
        ctx.lineWidth   = hover ? 3 : 2;
        if (nd.newServer) ctx.setLineDash([6, 4]);
        ctx.stroke();
        ctx.setLineDash([]);
        if (hover) ctx.restore();

        // Rack-unit segments — decorative; skip when dense or shrunk to keep it clean.
        if (!_rv.dense && r >= 36) {
            ctx.save();
            ctx.globalAlpha = dim ? 0.06 : 0.18;
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
        }

        // Name — culled when zoomed out (#3) unless hovered or the find-match.
        if (_rv.labels || hover || _rv.find === nd) {
            ctx.fillStyle = nd.error ? C.errText : C.serverText;
            ctx.font      = 'bold 10px "Segoe UI",sans-serif';
            ctx.textAlign = 'center';
            ctx.textBaseline = 'middle';
            var label = shortName(nd.label, 16);
            ctx.fillText(label, x, y + (r >= 36 ? 4 : 0));

            // Counts / error row only at full size to avoid overlap when shrunk.
            if (r >= 36) {
                if (!nd.error && nd.counts) {
                    ctx.font      = '9px "Segoe UI",sans-serif';
                    ctx.fillStyle = 'rgba(110,231,183,0.7)';
                    ctx.fillText((nd.counts.hosts||0)+'h · '+(nd.counts.apps||0)+'a · '+(nd.counts.dbs||0)+'db', x, y + 18);
                } else if (nd.error) {
                    ctx.font      = '9px "Segoe UI",sans-serif';
                    ctx.fillStyle = C.errText;
                    ctx.fillText('unreachable', x, y + 18);
                }
            }
        }
        ctx.restore();
    }

    // #1 corral supernode — collapsed bag of standalone servers.
    function drawStandaloneGroup(ctx, nd, hover) {
        var w = 184, h = 54, x = nd.x - w/2, y = nd.y - h/2;
        ctx.save();
        if (hover) { ctx.shadowColor = 'rgba(148,163,184,0.5)'; ctx.shadowBlur = 14; }
        // stacked cards
        drawRoundRect(ctx, x+5, y-5, w, h, 7); ctx.fillStyle = 'rgba(30,41,59,0.5)'; ctx.fill();
        drawRoundRect(ctx, x+2.5, y-2.5, w, h, 7); ctx.fillStyle = 'rgba(30,41,59,0.7)'; ctx.fill();
        drawRoundRect(ctx, x, y, w, h, 7); ctx.fillStyle = 'rgba(30,41,59,0.95)'; ctx.fill();
        ctx.strokeStyle = hover ? '#94a3b8' : 'rgba(148,163,184,0.5)'; ctx.lineWidth = hover ? 2 : 1.2;
        ctx.stroke();
        ctx.restore();
        ctx.fillStyle = '#cbd5e1'; ctx.font = '600 12px "Segoe UI",sans-serif';
        ctx.textAlign = 'left'; ctx.textBaseline = 'top';
        ctx.fillText('▸ ' + nd.count + ' standalone servers', x+12, y+9);
        ctx.font = '10px "Segoe UI",sans-serif'; ctx.fillStyle = C.hostMeta;
        ctx.fillText('no topology · ' + nd.unreachCount + ' unreachable · click to expand', x+12, y+30);
    }

    function drawPinDot(ctx, nd) {
        // Small dot in top-right corner to indicate node is manually pinned
        var px = nd.type === 'server' ? nd.x + _rv.r * 0.65 : nd.x + HOST_W/2 - 5;
        var py = nd.type === 'server' ? nd.y - _rv.r * 0.65 : nd.y - HOST_H/2 + 5;
        ctx.beginPath();
        ctx.arc(px, py, 4, 0, Math.PI*2);
        ctx.fillStyle = 'rgba(245,158,11,0.85)';
        ctx.fill();
    }

    function drawHostNode(ctx, nd, hover, active) {
        var x = nd.x - HOST_W/2, y = nd.y - HOST_H/2;
        var bcolor = active ? C.hostActive : hover ? C.hostHover : C.hostBorder;
        var alpha  = active ? 0.3 : hover ? 0.2 : 0;

        ctx.save();
        if (nd.dim && !hover && !active) ctx.globalAlpha = 0.28;

        // Shadow/glow
        if (hover || active) {
            ctx.save();
            ctx.shadowColor = active ? 'rgba(45,212,191,0.6)' : 'rgba(45,212,191,0.3)';
            ctx.shadowBlur  = 15;
        }

        drawRoundRect(ctx, x, y, HOST_W, HOST_H, 6);
        ctx.fillStyle = 'rgba(18,24,27,' + (0.85 + alpha) + ')';
        ctx.fill();
        ctx.strokeStyle = bcolor;
        ctx.lineWidth   = active || hover ? 2 : 1;
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
        ctx.restore();
    }

    // Aggregated client group (by app or subnet) — replaces N individual host boxes.
    function drawClusterNode(ctx, nd, hover) {
        var x = nd.x - HOST_W/2, y = nd.y - HOST_H/2;
        if (hover) {
            ctx.save();
            ctx.shadowColor = 'rgba(99,102,241,0.5)';
            ctx.shadowBlur  = 15;
        }
        // Stacked-card effect to signal "this is a group"
        drawRoundRect(ctx, x+4, y-4, HOST_W, HOST_H, 6);
        ctx.fillStyle = 'rgba(30,27,75,0.5)';
        ctx.fill();
        drawRoundRect(ctx, x, y, HOST_W, HOST_H, 6);
        ctx.fillStyle = 'rgba(30,27,75,0.92)';
        ctx.fill();
        ctx.strokeStyle = hover ? '#818cf8' : 'rgba(129,140,248,0.55)';
        ctx.lineWidth   = hover ? 2 : 1.2;
        if (nd.expanded) ctx.setLineDash([4,3]);
        ctx.stroke();
        ctx.setLineDash([]);
        if (hover) ctx.restore();

        ctx.font         = FONT_NAME;
        ctx.fillStyle    = '#c7d2fe';
        ctx.textAlign    = 'left';
        ctx.textBaseline = 'top';
        ctx.fillText((nd.expanded ? '▾ ' : '▸ ') + shortName(nd.label, 15), x+10, y+8);

        ctx.font      = FONT_SMALL;
        ctx.fillStyle = C.hostMeta;
        ctx.fillText(nd.hostCount + ' hosts · ' + nd.connCount + ' conn', x+10, y+26);
    }

    // Control point of an xlink's quadratic curve — shared by draw + hit-testing.
    function xlinkCtrl(a, b) {
        var dx = b.x - a.x, dy = b.y - a.y;
        var dist = Math.sqrt(dx*dx + dy*dy) || 1;
        var ux = dx/dist, uy = dy/dist;
        var sx = a.x + ux * (a.type==='server'?_rv.r:0), sy = a.y + uy * (a.type==='server'?_rv.r:0);
        var ex = b.x - ux * (b.type==='server'?_rv.r:0), ey = b.y - uy * (b.type==='server'?_rv.r:0);
        var mx = (sx+ex)/2, my = (sy+ey)/2;
        return { sx:sx, sy:sy, ex:ex, ey:ey, px: mx - uy*60, py: my + ux*60 };
    }

    function drawEdge(ctx, e, t) {
        var a = e.source, b = e.target;
        var dx = b.x - a.x, dy = b.y - a.y;
        var dist = Math.sqrt(dx*dx + dy*dy) || 1;
        var ux = dx/dist, uy = dy/dist;

        // Offset edge start/end to node boundaries
        var rA = a.type === 'server' ? _rv.r : 0;
        var rB = b.type === 'server' ? _rv.r : 0;
        var sx = a.x + ux * rA, sy = a.y + uy * rA;
        var ex = b.x - ux * rB, ey = b.y - uy * rB;

        // Edge focus: when a server is hovered, its incident links stay bright and the rest fade.
        var incident = !_focusNode || a === _focusNode || b === _focusNode;
        var faded = _focusNode && !incident;

        if (e.xlink) {
            // Curved dashed arc for cross-server links; colour by relationship kind when known.
            var ek = e.kind && EDGE_KINDS[e.kind];
            var color = ek ? ek.color
                           : (a.serverIndex < b.serverIndex ? C.xlinkAmber : C.xlinkBlue);
            var q = xlinkCtrl(a, b);
            ctx.save();
            if (faded) ctx.globalAlpha = 0.07;
            else if (e.dim) ctx.globalAlpha = 0.3;
            ctx.setLineDash([7, 4]);
            ctx.strokeStyle = color;
            ctx.lineWidth   = (e === _hoverEdgeRef || (_focusNode && incident)) ? 3 : 1.8;
            ctx.beginPath();
            ctx.moveTo(q.sx, q.sy);
            ctx.quadraticCurveTo(q.px, q.py, q.ex, q.ey);
            ctx.stroke();
            ctx.setLineDash([]);
            // Direction arrowhead at the target end (replication/log-shipping etc. are directed)
            if (ek && !faded) {
                var tx = q.ex - q.px, ty = q.ey - q.py;
                var tl = Math.sqrt(tx*tx + ty*ty) || 1;
                tx /= tl; ty /= tl;
                ctx.beginPath();
                ctx.moveTo(q.ex, q.ey);
                ctx.lineTo(q.ex - tx*10 - ty*5, q.ey - ty*10 + tx*5);
                ctx.lineTo(q.ex - tx*10 + ty*5, q.ey - ty*10 - tx*5);
                ctx.closePath();
                ctx.fillStyle = color;
                ctx.fill();
            }
            ctx.restore();
            return;
        }

        // Straight edge (server → host/cluster)
        ctx.save();
        if (faded) ctx.globalAlpha = 0.07;
        ctx.beginPath();
        ctx.moveTo(sx, sy);
        ctx.lineTo(ex, ey);
        ctx.strokeStyle = C.edgeIdle;
        ctx.lineWidth   = Math.max(1, Math.min(3.5, 1 + (b.data ? Math.log2((b.data.connectionCount||1)+1)*0.4 : 1)));
        ctx.stroke();

        // Animated flow dot — skipped when dense/faded (noise + cost at scale).
        if (!faded && !_rv.dense) {
            var tp = ((t * 0.001) + (a.x * 0.001)) % 1;
            var dpx = sx + (ex-sx)*tp, dpy = sy + (ey-sy)*tp;
            ctx.shadowColor = C.flowDot;
            ctx.shadowBlur = 10;
            ctx.beginPath();
            ctx.arc(dpx, dpy, 3, 0, Math.PI*2);
            ctx.fillStyle = C.flowDot;
            ctx.fill();
        }
        ctx.restore();
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
                if (dx*dx + dy*dy <= _rv.r*_rv.r) return nd;
            } else if (nd.type === 'standalone-group') {
                var sw = 184, sh = 54;
                if (wx >= nd.x-sw/2 && wx <= nd.x+sw/2 && wy >= nd.y-sh/2 && wy <= nd.y+sh/2) return nd;
            } else {
                // host + cluster share the same box dims
                var hx = nd.x - HOST_W/2, hy = nd.y - HOST_H/2;
                if (wx >= hx && wx <= hx+HOST_W && wy >= hy && wy <= hy+HOST_H) return nd;
            }
        }
        return null;
    }

    // Cross-server (xlink) edge hit test — distance to sampled points on the quadratic curve.
    function hitEdge(edges, wx, wy, scale) {
        var thresh = 8 / Math.max(0.2, scale);
        var t2 = thresh * thresh;
        for (var i = 0; i < edges.length; i++) {
            var e = edges[i];
            if (!e.xlink) continue;
            var q = xlinkCtrl(e.source, e.target);
            for (var s = 1; s <= 4; s++) {
                var t = s / 5;
                var omt = 1 - t;
                var cx = omt*omt*q.sx + 2*omt*t*q.px + t*t*q.ex;
                var cy = omt*omt*q.sy + 2*omt*t*q.py + t*t*q.ey;
                var dx = wx - cx, dy = wy - cy;
                if (dx*dx + dy*dy <= t2) return e;
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
        function toolbarHtml() {
            // Legend shows only the relationship kinds actually present in the data
            var kindLegend = '';
            var seen = {};
            (data.crossLinks || []).forEach(function(l) {
                var ek = l.edgeKind && EDGE_KINDS[l.edgeKind];
                if (ek && !seen[ek.label]) {
                    seen[ek.label] = true;
                    kindLegend += '<span class="env-fd-leg-dash" style="border-color:' + ek.color + ';margin-left:8px"></span>' + ek.label + ' ';
                }
            });
            if (!kindLegend) {
                kindLegend = '<span class="env-fd-leg-dash" style="border-color:#f59e0b;margin-left:8px"></span>Cross-server link ';
            }
            function btn(action, lbl, on, title) {
                return '<button data-action="' + action + '" title="' + (title||'') + '" class="' + (on ? 'env-fd-btn-active' : '') +
                    '" style="white-space:nowrap;width:auto;' + (on ? 'color:#2dd4bf;border-color:#2dd4bf;' : '') + '">' + lbl + '</button>';
            }
            // Client grouping toggle only matters when there are hosts to group
            var modeBtns = '';
            if (totalHosts > 0) {
                modeBtns = '<span style="color:#64748b;font-size:10px;margin-left:10px;">Clients:</span>' +
                    btn('cm-all','All',clientMode==='all') + btn('cm-app','By app',clientMode==='app') + btn('cm-subnet','By subnet',clientMode==='subnet');
            }
            // #2 reachability filter — only when there are unreachable servers
            var reachBtns = '';
            if (unreachCount > 0) {
                reachBtns = '<span style="color:#64748b;font-size:10px;margin-left:10px;">Unreachable (' + unreachCount + '):</span>' +
                    btn('rm-show','Show',reachMode==='show') + btn('rm-dim','Dim',reachMode==='dim') + btn('rm-hide','Hide',reachMode==='hide');
            }
            // #1 corral collapse — only when there are standalone servers
            var corralBtn = standaloneNamesCount > 0
                ? btn('corral', (standaloneCollapsed ? '▸ ' : '▾ ') + 'Standalone (' + standaloneNamesCount + ')', !standaloneCollapsed,
                      'Collapse/expand servers with no topology links')
                : '';
            // #4 find box
            var findBox = '<input class="env-fd-find" type="text" placeholder="🔎 find server…" ' +
                'style="width:120px;height:22px;margin-left:10px;background:rgba(15,23,42,0.8);border:1px solid rgba(148,163,184,0.3);' +
                'border-radius:4px;color:#e2e8f0;font-size:11px;padding:0 6px;" />';

            return '<button data-action="zoomin"   title="Zoom in">+</button>' +
                '<button data-action="zoomout"  title="Zoom out">−</button>' +
                '<button data-action="fit"      title="Fit to screen">⤢</button>' +
                '<button data-action="relayout" title="Re-run layout (unpins all nodes)" class="env-fd-btn-wide" style="white-space:nowrap;width:auto;">↺ Re-layout</button>' +
                modeBtns + reachBtns + corralBtn + findBox +
                '<span class="env-fd-legend">' +
                    '<span class="env-fd-leg-dot" style="background:#10b981"></span>SQL Server ' +
                    '<span class="env-fd-leg-dot" style="background:#6366f1;margin-left:8px"></span>Host ' +
                    kindLegend +
                    '<span style="color:#64748b;font-size:10px;margin-left:10px;">Drag to pin · Click a group/corral to expand</span>' +
                '</span>';
        }
        vp.appendChild(tb);   // populated after the graph is built — the legend needs the data

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
        var pc = _posCache[containerId] = _posCache[containerId] || { pos: {}, view: null, clientMode: null, expanded: {} };

        var totalHosts = 0;
        servers.forEach(function(s) { totalHosts += (s.hosts||[]).length; });

        // Client grouping: 'all' (per-host nodes) | 'app' | 'subnet'. Dense estates default to app
        // grouping — 100 host boxes on one server is unusable (the clumping problem).
        var clientMode = pc.clientMode || (totalHosts > GROUP_THRESHOLD ? 'app' : 'all');
        var expanded   = pc.expanded;     // groupId -> true (user drilled into this cluster)

        // #2 reachability filter: 'show' | 'dim' | 'hide'. Count unreachable to decide defaults.
        var unreachCount = servers.filter(function(s){ return s.discovered && s.error; }).length;
        var reachMode = pc.reachMode || 'show';
        // #1 corral starts collapsed when there are many lone servers (the AD graveyard).
        var standaloneCollapsed = (pc.standaloneCollapsed != null)
            ? pc.standaloneCollapsed
            : (servers.length > 24);

        var nodes = [], edges = [];
        var standaloneNamesCount = 0, corralHeight = 0;   // set by buildGraph; drive the corral backdrop

        // Restore a node's previous position if we've seen it before; otherwise place at (x,y).
        function placeNode(nd, x, y) {
            var c = pc.pos[nd.key];
            if (c) { nd.x = c.x; nd.y = c.y; nd.pinned = !!c.pinned; nd.restored = true; }
            else   { nd.x = x;   nd.y = y;   nd.pinned = false; }
            nd.vx = 0; nd.vy = 0; nd.fx = 0; nd.fy = 0;
            return nd;
        }

        function groupKeyOf(h) {
            if (clientMode === 'app')    return h.topApp  || '(unknown app)';
            if (clientMode === 'subnet') return h.subnet  || '(unknown subnet)';
            return null;
        }

        // #5 connected components, #1 standalone corral, #2 reachability filter.
        function isUnreachable(s) { return !!(s.discovered && s.error); }

        function buildGraph() {
            nodes = []; edges = [];

            // #2 Reachability filter: 'show' (all) | 'dim' (draw faint) | 'hide' (omit).
            var visibleServers = servers.filter(function(s) {
                return !(reachMode === 'hide' && isUnreachable(s));
            });

            var maxConn = 1;
            visibleServers.forEach(function(s) {
                (s.hosts||[]).forEach(function(h) { maxConn = Math.max(maxConn, h.connectionCount||0); });
            });

            // ── #5 Connected components over the server-to-server (xlink) graph ──
            var present = {}; visibleServers.forEach(function(s) { present[s.name] = true; });
            var adj = {}; visibleServers.forEach(function(s) { adj[s.name] = []; });
            crossLinks.forEach(function(l) {
                if (present[l.fromServer] && present[l.toServer] && l.fromServer !== l.toServer) {
                    adj[l.fromServer].push(l.toServer);
                    adj[l.toServer].push(l.fromServer);
                }
            });
            var compOf = {}, components = [];
            visibleServers.forEach(function(s) {
                if (compOf[s.name] != null) return;
                var id = components.length, stack = [s.name], members = [];
                compOf[s.name] = id;
                while (stack.length) {
                    var c = stack.pop(); members.push(c);
                    adj[c].forEach(function(nb) { if (compOf[nb] == null) { compOf[nb] = id; stack.push(nb); } });
                }
                components.push(members);
            });

            var srvByName = {}; servers.forEach(function(s) { srvByName[s.name] = s; });
            function hasClients(name) { var s = srvByName[name]; return s && (s.hosts||[]).length > 0; }

            // #1 Classify into main regions vs corral. A server earns a main region if it has
            // topology links (≥2 component) OR has client hosts to show. Only the true noise —
            // no links AND no clients (the unreachable AD graveyard, idle boxes) — gets corralled.
            // This keeps the plain Scan view intact (every scanned server has clients).
            var islands = [], standaloneNames = [];
            components.forEach(function(m) {
                if (m.length >= 2 || hasClients(m[0])) islands.push(m);
                else standaloneNames.push(m[0]);
            });

            // Reserve a bottom strip for the corral; islands lay out above it.
            var corralH = standaloneNames.length ? 168 : 0;
            standaloneNamesCount = standaloneNames.length;     // expose for the corral backdrop
            corralHeight = corralH;
            var mainH   = Math.max(120, H - corralH);
            var nIsl    = islands.length;
            var cols    = Math.max(1, Math.ceil(Math.sqrt(nIsl)));
            var rows    = Math.max(1, Math.ceil(nIsl / cols));
            function islandAnchor(i) {
                if (nIsl <= 1) return { x: W/2, y: mainH/2 };
                var cI = i % cols, rI = Math.floor(i / cols);
                return { x: W*(cI+0.5)/cols, y: mainH*(rI+0.5)/rows };
            }
            var islandIdxOf = {};
            islands.forEach(function(m, i) { m.forEach(function(n) { islandIdxOf[n] = i; }); });

            var byName = {};

            function emitHostsFor(s, parent, si, ax, ay) {
                var hosts = s.hosts || [];
                var spread = Math.min(W, H) * 0.16;
                function emitHost(h, hi, total, par) {
                    var ha = (2*Math.PI*hi / Math.max(1, total));
                    var hn = placeNode({
                        id: 'host_' + si + '_' + hi, key: 'host:' + s.name + ':' + h.hostname,
                        type: 'host', label: h.hostname, data: h,
                        connFrac: (h.connectionCount||0) / maxConn, serverNodeId: parent.id, dim: parent.dim
                    }, par.x + spread*Math.cos(ha) + (Math.random()-0.5)*30,
                       par.y + spread*Math.sin(ha) + (Math.random()-0.5)*30);
                    hn.ax = ax; hn.ay = ay;
                    nodes.push(hn);
                    edges.push({ source: par, target: hn, xlink: false });
                }
                if (clientMode === 'all') {
                    hosts.forEach(function(h, hi) { emitHost(h, hi, hosts.length, parent); });
                } else {
                    var groups = {};
                    hosts.forEach(function(h) { var k = groupKeyOf(h); (groups[k] = groups[k] || []).push(h); });
                    var keys = Object.keys(groups).sort(), slot = 0, slots = keys.length;
                    keys.forEach(function(k) {
                        var members = groups[k];
                        if (members.length === 1) { emitHost(members[0], slot++, slots, parent); return; }
                        var gid = 'grp:' + s.name + ':' + clientMode + ':' + k;
                        var ga = (2*Math.PI*(slot++) / Math.max(1, slots));
                        var gn = placeNode({
                            id: gid, key: gid, type: 'cluster', label: k, groupId: gid,
                            expanded: !!expanded[gid], hostCount: members.length,
                            connCount: members.reduce(function(a,h){ return a+(h.connectionCount||0); }, 0),
                            members: members, serverNodeId: parent.id, dim: parent.dim
                        }, parent.x + spread*Math.cos(ga) + (Math.random()-0.5)*30,
                           parent.y + spread*Math.sin(ga) + (Math.random()-0.5)*30);
                        gn.ax = ax; gn.ay = ay;
                        nodes.push(gn);
                        edges.push({ source: parent, target: gn, xlink: false });
                        if (expanded[gid]) members.forEach(function(h, mi) { emitHost(h, mi, members.length, gn); });
                    });
                }
            }

            function emitServer(s, si, px, py, ax, ay, pin) {
                var nd = placeNode({
                    id: 'srv_' + si, key: 'srv:' + s.name, type: 'server', label: s.name,
                    counts: s.counts, error: s.error || null,
                    discovered: !!s.discovered, fromAd: !!s.fromAd,
                    newServer: !!s.discovered && s.inCatalogue === false,
                    unreachable: isUnreachable(s),
                    dim: (reachMode === 'dim' && isUnreachable(s)),
                    serverIndex: si
                }, px, py);
                nd.ax = ax; nd.ay = ay;
                if (pin) nd.pinned = true;
                nodes.push(nd); byName[s.name] = nd;
                return nd;
            }

            // ── Islands: members placed around their region anchor, in the force sim ──
            var si = 0;
            islands.forEach(function(members, i) {
                var a = islandAnchor(i);
                var big = members.length > 6;
                // Big mesh: weak anchor pull so repulsion can spread it; small island: tight pull.
                var gpull = members.length === 1 ? 1 : (big ? 0.3 : 2.2);
                // Spawn radius grows with member count so a 40-node mesh starts spread, not stacked.
                var base = nIsl > 1 ? 0.10 : 0.22;
                var ring = members.length === 1 ? 0
                    : Math.min(W, H) * Math.min(0.46, base + members.length * 0.012);
                members.forEach(function(name, mi) {
                    var ang = (2*Math.PI*mi / members.length) - Math.PI/2;
                    var nd = emitServer(srvByName[name], si++,
                        a.x + ring*Math.cos(ang) + (Math.random()-0.5)*20,
                        a.y + ring*Math.sin(ang) + (Math.random()-0.5)*20,
                        a.x, a.y, false);
                    nd.gpull = gpull; nd.compSize = members.length;
                    emitHostsFor(srvByName[name], nd, nd.serverIndex, a.x, a.y);
                });
            });

            // ── #1 Standalone corral: pinned grid in the bottom strip, OR one supernode ──
            if (standaloneNames.length) {
                var stripTop = H - corralH + 30;
                if (!standaloneCollapsed) {
                    var gcols = Math.max(1, Math.floor((W - 80) / 150));
                    standaloneNames.forEach(function(name, k) {
                        var cx = 70 + (k % gcols) * 150;
                        var cy = stripTop + Math.floor(k / gcols) * 64;
                        var nd = emitServer(srvByName[name], si++, cx, cy, cx, cy, true);
                        // corralled servers don't spawn host/cluster nodes — they're the noise we're hiding
                        void nd;
                    });
                } else {
                    var unreach = standaloneNames.filter(function(n){ return isUnreachable(srvByName[n]); }).length;
                    var gx = 70, gy = stripTop + 6;
                    var gn = placeNode({
                        id: 'standalone-group', key: 'standalone-group', type: 'standalone-group',
                        label: standaloneNames.length + ' standalone servers',
                        count: standaloneNames.length, unreachCount: unreach,
                        members: standaloneNames.map(function(n){ return srvByName[n]; })
                    }, gx, gy);
                    gn.pinned = true; gn.ax = gx; gn.ay = gy;
                    nodes.push(gn);
                }
            }

            // ── Cross-server edges — coloured by relationship kind (Dig Deeper) ──
            crossLinks.forEach(function(lnk) {
                var a = byName[lnk.fromServer], b = byName[lnk.toServer];
                if (a && b) edges.push({
                    source: a, target: b, xlink: true,
                    count: lnk.connectionCount, kind: lnk.edgeKind || '', detail: lnk.detail || '',
                    dim: a.dim || b.dim
                });
            });
        }

        buildGraph();
        tb.innerHTML = toolbarHtml();

        // ── Animation loop ───────────────────────────────────────────────
        var ctx    = cvs.getContext('2d');
        var sim    = createSimulation(nodes, edges, W, H);
        var _raf   = null;
        var _t     = 0;
        var MAX_SIM_STEPS = nodes.length > 40 ? 520 : 300;   // big meshes need longer to settle

        function settledSteps() {
            // When most nodes kept their previous position (live-discovery re-render), run only a
            // short settle for the new arrivals instead of re-exploding the whole layout.
            var restored = 0;
            nodes.forEach(function(n) { if (n.restored) restored++; });
            return (nodes.length > 0 && restored / nodes.length > 0.5) ? MAX_SIM_STEPS - 80 : 0;
        }
        var _steps = settledSteps();

        // Interaction state
        var _hover  = null;
        var _active = null;
        var _hoverEdge = null;
        var _findNode = null;                                   // #4 find-match node
        var _corralTop = standaloneNamesCount ? (H - corralHeight + 8) : null;  // #1 corral zone top
        var _tx = 0, _ty = 0, _scale = 1;
        if (pc.view) { _tx = pc.view.tx; _ty = pc.view.ty; _scale = pc.view.scale; }
        var _drag = false, _lx = 0, _ly = 0;
        var _nodeDrag = null, _ndOffX = 0, _ndOffY = 0;
        var _mouseDownPos = null;   // {x,y} at mousedown — used to distinguish click from drag
        var DRAG_THRESHOLD = 5;     // px movement before treating as a drag

        function savePositions() {
            nodes.forEach(function(n) { pc.pos[n.key] = { x: n.x, y: n.y, pinned: n.pinned }; });
            pc.view = { tx: _tx, ty: _ty, scale: _scale };
            pc.clientMode = clientMode;
            pc.reachMode = reachMode;
            pc.standaloneCollapsed = standaloneCollapsed;
        }

        // Rebuild nodes/edges in place (client-mode switch, cluster expand/collapse) without
        // losing positions or the viewport.
        function rebuild() {
            savePositions();
            buildGraph();
            _corralTop = standaloneNamesCount ? (H - corralHeight + 8) : null;
            sim = createSimulation(nodes, edges, W, H);
            _steps = settledSteps();
            _hover = null; _active = null; _hoverEdge = null; _hoverEdgeRef = null;
        }

        function toWorld(cx, cy) {
            return { x: (cx - _tx) / _scale, y: (cy - _ty) / _scale };
        }

        function draw() {
            ctx.clearRect(0, 0, W, H);

            // Background
            ctx.fillStyle = C.bg;
            ctx.fillRect(0, 0, W, H);
            drawGrid(ctx, W, H);

            // #3 Scale-aware render vars for this frame.
            var serverCount = 0;
            nodes.forEach(function(n){ if (n.type==='server') serverCount++; });
            _rv.dense  = serverCount > 22;
            _rv.r      = _rv.dense ? 30 : RAD_SERVER;
            _rv.labels = _scale >= 0.55;          // cull labels when zoomed out
            _rv.find   = _findNode;
            // Focus a hovered/selected server so the mesh fades to just its connections.
            _focusNode = (_hover && _hover.type === 'server') ? _hover
                       : (_active && _active.type === 'server') ? _active : null;

            // Corral strip backdrop, so pinned standalones read as a separate zone.
            if (_corralTop != null) {
                ctx.save();
                ctx.translate(_tx, _ty); ctx.scale(_scale, _scale);
                ctx.fillStyle = 'rgba(148,163,184,0.04)';
                ctx.fillRect(0, _corralTop, W, H - _corralTop);
                ctx.strokeStyle = 'rgba(148,163,184,0.12)'; ctx.lineWidth = 1;
                ctx.beginPath(); ctx.moveTo(0, _corralTop); ctx.lineTo(W, _corralTop); ctx.stroke();
                ctx.fillStyle = 'rgba(148,163,184,0.5)'; ctx.font = '10px "Segoe UI",sans-serif';
                ctx.textAlign = 'left'; ctx.textBaseline = 'top';
                ctx.fillText('STANDALONE — no topology links', 8, _corralTop + 5);
                ctx.restore();
            }

            ctx.save();
            ctx.translate(_tx, _ty);
            ctx.scale(_scale, _scale);

            // Edges first
            var timeMs = Date.now();
            edges.forEach(function(e) { drawEdge(ctx, e, timeMs); });

            // Nodes
            nodes.forEach(function(nd) {
                if (nd.type === 'server') {
                    drawServerNode(ctx, nd, nd === _hover, timeMs);
                } else if (nd.type === 'cluster') {
                    drawClusterNode(ctx, nd, nd === _hover);
                } else if (nd.type === 'standalone-group') {
                    drawStandaloneGroup(ctx, nd, nd === _hover);
                } else {
                    drawHostNode(ctx, nd, nd === _hover, nd === _active);
                }
                if (nd.pinned && nd.type !== 'standalone-group') drawPinDot(ctx, nd);
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

        // Initial fit after short settle — but keep the user's viewport on live re-renders
        if (!pc.view) setTimeout(fit, 400);

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

            // Edge hover (only when no node is hit) — shows relationship kind + detail
            var edgeHit = hit ? null : hitEdge(edges, w.x, w.y, _scale);
            if (edgeHit !== _hoverEdge) { _hoverEdge = edgeHit; _hoverEdgeRef = edgeHit; }

            if (hit) {
                var html = '<strong style="color:#e2e8f0">' + esc(hit.label) + '</strong>';
                if (hit.type === 'server') {
                    var c = hit.counts||{};
                    html += '<br><span style="color:#64748b">SQL Server</span>';
                    if (hit.discovered) {
                        html += '<br>Clients: ' + (c.hosts||0);
                        html += '<br><span style="color:#64748b">' + (hit.fromAd ? 'Found via Active Directory' : 'Found by topology crawl') + '</span>';
                        if (hit.newServer) html += '<br><span style="color:#f59e0b">Not in catalogue yet</span>';
                    } else {
                        html += '<br>Hosts: '+c.hosts+' · Apps: '+c.apps+' · DBs: '+c.dbs;
                    }
                    if (hit.error) html += '<br><span style="color:#f87171">'+esc(hit.error)+'</span>';
                } else if (hit.type === 'cluster') {
                    var names = (hit.members||[]).slice(0, 10).map(function(m){ return esc(m.hostname); }).join('<br>');
                    html += '<br><span style="color:#64748b">Client group · ' + hit.hostCount + ' hosts · ' + hit.connCount + ' conn</span>' +
                        '<br>' + names +
                        ((hit.members||[]).length > 10 ? '<br><span style="color:#64748b">+' + (hit.members.length-10) + ' more</span>' : '') +
                        '<br><span style="color:#6366f1">Click to ' + (hit.expanded ? 'collapse' : 'expand') + '</span>';
                } else if (hit.type === 'standalone-group') {
                    var snames = (hit.members||[]).slice(0, 14).map(function(m){ return esc(m.name) + (m.error ? ' <span style="color:#f87171">·unreachable</span>' : ''); }).join('<br>');
                    html += '<br><span style="color:#64748b">' + hit.count + ' servers with no replication / AG / mirror / linked-server links</span>' +
                        '<br>' + snames +
                        ((hit.members||[]).length > 14 ? '<br><span style="color:#64748b">+' + (hit.members.length-14) + ' more</span>' : '') +
                        '<br><span style="color:#6366f1">Click to expand into the corral</span>';
                } else {
                    var d = hit.data||{};
                    html += '<br><span style="color:#64748b">Host</span>' +
                        '<br>'+d.connectionCount+' connections' +
                        '<br>'+d.uniqueApps+' apps · '+d.uniqueUsers+' users · '+d.uniqueDbs+' DBs' +
                        '<br><span style="color:#6366f1">Click to inspect</span>';
                }
                showTip(e.clientX, e.clientY, html);
            } else if (edgeHit) {
                var ek = edgeHit.kind && EDGE_KINDS[edgeHit.kind];
                var ehtml = '<strong style="color:#e2e8f0">' + (ek ? ek.label : 'Cross-server link') + '</strong>' +
                    '<br>' + esc(edgeHit.source.label) + ' → ' + esc(edgeHit.target.label);
                if (edgeHit.detail) ehtml += '<br><span style="color:#64748b">' + esc(edgeHit.detail) + '</span>';
                if (!ek && edgeHit.count) ehtml += '<br>' + edgeHit.count + ' connections';
                showTip(e.clientX, e.clientY, ehtml);
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
            if (hit && hit.type === 'cluster') {
                // Drill into / collapse the client group
                expanded[hit.groupId] = !expanded[hit.groupId];
                rebuild();
                return;
            }
            if (hit && hit.type === 'standalone-group') {
                // #1 expand the corral into individual standalone server nodes
                standaloneCollapsed = false;
                rebuild();
                tb.innerHTML = toolbarHtml();
                return;
            }
            if (hit && hit.type === 'server') {
                // Sticky focus: click a server to isolate its links (study a hub); click again to clear.
                _active = (_active === hit) ? null : hit;
                return;
            }
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
            else if (f.indexOf('cm-') === 0) {
                // Switch client grouping mode (all / by app / by subnet)
                var m = f.substring(3);
                if (m !== clientMode) {
                    clientMode = m;
                    rebuild();
                    tb.innerHTML = toolbarHtml();
                }
            }
            else if (f.indexOf('rm-') === 0) {
                // #2 reachability filter (show / dim / hide)
                var rm = f.substring(3);
                if (rm !== reachMode) {
                    reachMode = rm;
                    rebuild();
                    tb.innerHTML = toolbarHtml();
                }
            }
            else if (f === 'corral') {
                // #1 collapse / expand the standalone corral
                standaloneCollapsed = !standaloneCollapsed;
                rebuild();
                tb.innerHTML = toolbarHtml();
            }
        });

        // #4 Find box — highlight + centre the first server whose name contains the query.
        tb.addEventListener('input', function(e) {
            if (!e.target.classList || !e.target.classList.contains('env-fd-find')) return;
            var q = (e.target.value || '').trim().toLowerCase();
            if (!q) { _findNode = null; return; }
            var match = nodes.find(function(n) {
                return n.type === 'server' && n.label.toLowerCase().indexOf(q) >= 0;
            });
            _findNode = match || null;
            if (match) {
                // Centre the match and zoom in a touch if we're zoomed way out.
                var s = Math.max(_scale, 0.8);
                _scale = s;
                _tx = W/2 - match.x * s;
                _ty = H/2 - match.y * s;
            }
        });
        // Keep canvas drag/keys from stealing focus while typing in the find box.
        tb.addEventListener('mousedown', function(e) {
            if (e.target.classList && e.target.classList.contains('env-fd-find')) e.stopPropagation();
        });

        // ── ResizeObserver ───────────────────────────────────────────────
        // Note: observe() fires the callback once immediately — ignore that initial report,
        // otherwise every render re-simulates + re-fits and kills layout/viewport persistence.
        var _lastVpW = vp.clientWidth, _lastVpH = vp.clientHeight;
        var ro = new ResizeObserver(function() {
            if (vp.clientWidth === _lastVpW && vp.clientHeight === _lastVpH) return;
            _lastVpW = vp.clientWidth; _lastVpH = vp.clientHeight;
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
                savePositions();   // next render of this container keeps the layout (live discovery)
                if (_raf) cancelAnimationFrame(_raf);
                ro.disconnect();
                hideTip();
                _hoverEdgeRef = null;
                _focusNode = null;
                window.removeEventListener('mouseup', onMouseUp);
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

    // Export the topology canvas as a PNG data-URL.
    // Returns null if the canvas is not yet rendered.
    function exportPng(containerId) {
        var container = document.getElementById(containerId);
        if (!container) return null;
        var cvs = container.querySelector('canvas');
        if (!cvs) return null;
        return cvs.toDataURL('image/png');
    }

    // Forget cached node positions/viewport for a container — call when starting a NEW
    // scan or discovery run so it lays out fresh (within a run the cache keeps it stable).
    function resetLayout(containerId) {
        delete _posCache[containerId];
    }

    return {
        renderTopology:  renderTopology,
        renderMini:      renderMini,
        setHostCallback: setHostCallback,
        exportPng:       exportPng,
        resetLayout:     resetLayout
    };
})();
