// cpn-drag.js – SVG node drag + arc waypoint editing for CSharPN Visualizer
//
// Node drag:   mousedown on [data-node-id] → moves node live, commits on mouseup.
//              Arc paths connected to the node are recomputed in JS using the same
//              border-point geometry as the C# LayoutEngine (ellipse / rect).
//
// Arc bending: mousedown on .arc-hit (transparent overlay) near an existing
//              waypoint → drag that waypoint; elsewhere → insert a new waypoint.
//              dblclick on .arc-hit near a waypoint → remove it.
//              All changes are committed on mouseup via FinalizeArcWaypoints.
//
// Waypoints are stored in the arc path 'd' attribute as polyline (M L L …) and
// parsed from there – no separate DOM elements needed.

const _handlers = new Map();

// ── Geometry helpers (mirror C# LayoutEngine) ─────────────────────────────────

function ellipseBorder(cx, cy, rx, ry, tx, ty) {
    const dx = tx - cx, dy = ty - cy;
    if (Math.abs(dx) < 0.001 && Math.abs(dy) < 0.001) return [cx, cy];
    const denom = Math.sqrt(dx * dx / (rx * rx) + dy * dy / (ry * ry));
    return [cx + dx / denom, cy + dy / denom];
}

function rectBorder(cx, cy, w, h, tx, ty) {
    const dx = tx - cx, dy = ty - cy;
    if (Math.abs(dx) < 0.001 && Math.abs(dy) < 0.001) return [cx, cy];
    const hw = w / 2, hh = h / 2;
    const sx = Math.abs(dx) < 0.001 ? Infinity : hw / Math.abs(dx);
    const sy = Math.abs(dy) < 0.001 ? Infinity : hh / Math.abs(dy);
    return [cx + Math.min(sx, sy) * dx, cy + Math.min(sx, sy) * dy];
}

// Compute the border point of a node <g> element toward (tx, ty).
// Requires data-node-shape, data-node-rx/ry (place) or data-node-w/h (transition).
function nodeBorderEl(nodeEl, cx, cy, tx, ty) {
    if (nodeEl.dataset.nodeShape === 'place') {
        return ellipseBorder(cx, cy,
            parseFloat(nodeEl.dataset.nodeRx),
            parseFloat(nodeEl.dataset.nodeRy), tx, ty);
    }
    return rectBorder(cx, cy,
        parseFloat(nodeEl.dataset.nodeW),
        parseFloat(nodeEl.dataset.nodeH), tx, ty);
}

// ── Arc path helpers ───────────────────────────────────────────────────────────

// Parse intermediate waypoints from a polyline path "M x y L x y L x y …".
// Returns [] for bezier paths (Q/C) which have no user waypoints.
function getArcWaypoints(pathEl) {
    const d = pathEl?.getAttribute('d') ?? '';
    if (!d || /[CQcq]/.test(d)) return [];
    const pts = [];
    const tokens = d.trim().split(/\s+/);
    for (let i = 0; i < tokens.length; i++) {
        if (tokens[i] === 'M' || tokens[i] === 'L') {
            pts.push({ x: parseFloat(tokens[i + 1]), y: parseFloat(tokens[i + 2]) });
            i += 2;
        }
    }
    // Strip first (arc start) and last (adjusted arrowhead endpoint)
    return pts.slice(1, -1);
}

// Build the SVG 'd' string for a polyline arc.
// ex/ey is the RAW target border; we pull it back 9 px along the last segment
// so the arrowhead tip sits exactly on the node border.
function buildPolyPath(sx, sy, ex, ey, waypoints) {
    const lastX = waypoints.length > 0 ? waypoints[waypoints.length - 1].x : sx;
    const lastY = waypoints.length > 0 ? waypoints[waypoints.length - 1].y : sy;
    const dx = lastX - ex, dy = lastY - ey;
    const dist = Math.sqrt(dx * dx + dy * dy);
    const adjEx = dist > 1 ? ex + 9 * dx / dist : ex;
    const adjEy = dist > 1 ? ey + 9 * dy / dist : ey;

    let d = `M ${sx} ${sy}`;
    for (const wp of waypoints) d += ` L ${wp.x} ${wp.y}`;
    d += ` L ${adjEx} ${adjEy}`;
    return d;
}

// Redraw an arc path + its hit-area overlay in the DOM.
function repaintArc(svg, fromId, toId, sx, sy, ex, ey, waypoints) {
    const pathEl = svg.querySelector(`[data-arc-path="${fromId}|${toId}"]`);
    const hitEl  = svg.querySelector(`.arc-hit[data-arc-from="${fromId}"][data-arc-to="${toId}"]`);
    const d = buildPolyPath(sx, sy, ex, ey, waypoints);
    if (pathEl) pathEl.setAttribute('d', d);
    if (hitEl)  hitEl.setAttribute('d', d);
}

// Recompute and redraw every arc whose source or target is movedNodeId.
// Called on every mousemove during a node drag for live arc feedback.
function updateConnectedArcs(svg, movedNodeId, parseTranslate) {
    for (const pathEl of svg.querySelectorAll('[data-arc-path]')) {
        const parts = (pathEl.dataset.arcPath ?? '').split('|');
        if (parts.length !== 2) continue;
        const [fnId, tnId] = parts;
        if (fnId !== movedNodeId && tnId !== movedNodeId) continue;

        const fnEl = svg.querySelector(`[data-node-id="${fnId}"]`);
        const tnEl = svg.querySelector(`[data-node-id="${tnId}"]`);
        if (!fnEl || !tnEl) continue;

        const [fx, fy] = parseTranslate(fnEl);
        const [tx, ty] = parseTranslate(tnEl);

        const waypoints = getArcWaypoints(pathEl);

        // Border toward first waypoint (or direct if none)
        const ft = waypoints.length > 0 ? waypoints[0] : { x: tx, y: ty };
        const [sx, sy] = nodeBorderEl(fnEl, fx, fy, ft.x, ft.y);

        // Border toward last waypoint (or direct if none)
        const lt = waypoints.length > 0 ? waypoints[waypoints.length - 1] : { x: fx, y: fy };
        const [ex, ey] = nodeBorderEl(tnEl, tx, ty, lt.x, lt.y);

        // Update stored values so subsequent arc-drag reads current positions
        pathEl.dataset.arcSx = sx; pathEl.dataset.arcSy = sy;
        pathEl.dataset.arcEx = ex; pathEl.dataset.arcEy = ey;

        repaintArc(svg, fnId, tnId, sx, sy, ex, ey, waypoints);
    }
}

// ── Distance helpers ───────────────────────────────────────────────────────────

function ptSegDistSq(px, py, ax, ay, bx, by) {
    const dx = bx - ax, dy = by - ay;
    const lenSq = dx * dx + dy * dy;
    if (lenSq < 0.01) return (px - ax) ** 2 + (py - ay) ** 2;
    const t = Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / lenSq));
    return (px - (ax + t * dx)) ** 2 + (py - (ay + t * dy)) ** 2;
}

function findInsertIdx(sx, sy, ex, ey, waypoints, px, py) {
    const pts = [{ x: sx, y: sy }, ...waypoints, { x: ex, y: ey }];
    let best = 0, bestD = Infinity;
    for (let i = 0; i < pts.length - 1; i++) {
        const d = ptSegDistSq(px, py, pts[i].x, pts[i].y, pts[i + 1].x, pts[i + 1].y);
        if (d < bestD) { bestD = d; best = i; }
    }
    return best;
}

// Index of the waypoint closest to (px,py), or -1 if none within threshold.
function nearestWaypoint(waypoints, px, py, threshold = 15) {
    let best = -1, bestDsq = threshold * threshold;
    for (let i = 0; i < waypoints.length; i++) {
        const dsq = (px - waypoints[i].x) ** 2 + (py - waypoints[i].y) ** 2;
        if (dsq < bestDsq) { bestDsq = dsq; best = i; }
    }
    return best;
}

// Parse all points (including start/end) from a polyline path "M x y L x y …".
function parsePolyPoints(d) {
    if (!d || /[CQcq]/.test(d)) return [];
    const pts = [];
    const tokens = d.trim().split(/\s+/);
    for (let i = 0; i < tokens.length; i++) {
        if (tokens[i] === 'M' || tokens[i] === 'L') {
            pts.push({ x: parseFloat(tokens[i + 1]), y: parseFloat(tokens[i + 2]) });
            i += 2;
        }
    }
    return pts;
}

// Return the [data-arc-path] element whose polyline is closest to (px,py),
// or null if nothing is within thresholdSvg SVG units.
function nearestArcPath(svg, px, py, thresholdSvg) {
    let best = null, bestDsq = thresholdSvg * thresholdSvg;
    for (const pathEl of svg.querySelectorAll('[data-arc-path]')) {
        const pts = parsePolyPoints(pathEl.getAttribute('d') ?? '');
        for (let i = 0; i < pts.length - 1; i++) {
            const dsq = ptSegDistSq(px, py, pts[i].x, pts[i].y, pts[i + 1].x, pts[i + 1].y);
            if (dsq < bestDsq) { bestDsq = dsq; best = pathEl; }
        }
    }
    return best;
}


export function initDrag(dotNetRef, svgId) {
    disposeDrag(svgId);

    const svg = document.getElementById(svgId);
    if (!svg) return;

    function svgPt(e) {
        const p = svg.createSVGPoint();
        p.x = e.clientX; p.y = e.clientY;
        return p.matrixTransform(svg.getScreenCTM().inverse());
    }

    function parseTranslate(g) {
        const m = (g.getAttribute('transform') ?? '').match(/translate\(\s*([^,\s]+)[,\s]+([^)\s]+)/);
        return m ? [parseFloat(m[1]), parseFloat(m[2])] : [0, 0];
    }

    let nodeDrag     = null;   // { g, nodeId, offX, offY, stickyArcs }
    let arcDrag      = null;   // { fromId, toId, wpIndex, sx, sy, ex, ey, waypoints }
    let canvasDrag   = null;   // { startClientX, startClientY, startVBX, startVBY }
    let labelDrag    = null;   // { el, id, kind, gx, gy, bx, by, offX, offY }
    let nodeMoved    = false;  // true once the node actually moved — suppresses the ensuing click
    let wheelTimer   = null;
    // stickyArc entries: { fromId, toId, isFrom, wps, sx, sy, ex, ey, modified }

    svg.style.cursor = 'default';

    // ── mousedown ──────────────────────────────────────────────────────────

    // Convert 10 screen pixels to SVG user units for proximity tests.
    function arcThreshold() {
        return 10 / (svg.clientWidth / svg.viewBox.baseVal.width);
    }

    const onDown = e => {
        if (e.button !== 0) return;
        const pt = svgPt(e);

        // Movable labels take top priority — they sit above nodes/arcs.
        const labelEl = e.target.closest('[data-label-id]');
        if (labelEl) {
            e.preventDefault(); e.stopPropagation();
            // Labels inside a node <g> use that group's translate as their origin;
            // arc inscriptions live in the root coordinate system (no group).
            const groupEl = labelEl.closest('[data-node-id]');
            const [gx, gy] = groupEl ? parseTranslate(groupEl) : [0, 0];
            const lx = parseFloat(labelEl.getAttribute('x'));
            const ly = parseFloat(labelEl.getAttribute('y'));
            labelDrag = {
                el:   labelEl,
                id:   labelEl.dataset.labelId,
                kind: labelEl.dataset.labelKind,
                gx, gy,
                bx:   parseFloat(labelEl.dataset.labelBx),
                by:   parseFloat(labelEl.dataset.labelBy),
                offX: pt.x - (gx + lx),
                offY: pt.y - (gy + ly),
            };
            return;
        }

        // Node drag takes priority (nodes sit on top visually).
        const g = e.target.closest('[data-node-id]');
        if (!g) {
            // Find the arc whose polyline is closest to the click.
            const arcPathEl = nearestArcPath(svg, pt.x, pt.y, arcThreshold());
            if (arcPathEl) {
                e.preventDefault(); e.stopPropagation();
                const [fromId, toId] = (arcPathEl.dataset.arcPath ?? '').split('|');
                const sx = parseFloat(arcPathEl.dataset.arcSx), sy = parseFloat(arcPathEl.dataset.arcSy);
                const ex = parseFloat(arcPathEl.dataset.arcEx), ey = parseFloat(arcPathEl.dataset.arcEy);
                const waypoints = getArcWaypoints(arcPathEl);

                const nearIdx = nearestWaypoint(waypoints, pt.x, pt.y);
                if (nearIdx >= 0) {
                    arcDrag = { fromId, toId, wpIndex: nearIdx, sx, sy, ex, ey, waypoints };
                } else {
                    const insertIdx = findInsertIdx(sx, sy, ex, ey, waypoints, pt.x, pt.y);
                    waypoints.splice(insertIdx, 0, { x: pt.x, y: pt.y });
                    repaintArc(svg, fromId, toId, sx, sy, ex, ey, waypoints);
                    arcDrag = { fromId, toId, wpIndex: insertIdx, sx, sy, ex, ey, waypoints };
                }
                return;
            }

            // Canvas pan — drag on empty background
            e.preventDefault();
            const vb = svg.viewBox.baseVal;
            canvasDrag = { startClientX: e.clientX, startClientY: e.clientY,
                           startVBX: vb.x, startVBY: vb.y };
            svg.style.cursor = 'grabbing';
            return;
        }
        e.preventDefault();
        const [cx, cy] = parseTranslate(g);
        nodeDrag  = { g, nodeId: g.dataset.nodeId, offX: pt.x - cx, offY: pt.y - cy };
        nodeMoved = false;
        g.style.cursor = 'grabbing';

        // Collect connected arcs whose endpoint waypoint is orthogonally aligned with this node.
        // These can be "sticky" when the user holds Shift during the drag — the right-angle
        // bend is preserved by moving the endpoint waypoint's Y together with the node.
        const stickyArcs = [];
        for (const pathEl of svg.querySelectorAll('[data-arc-path]')) {
            const parts = (pathEl.dataset.arcPath ?? '').split('|');
            if (parts.length !== 2) continue;
            const [fnId, tnId] = parts;
            const isFrom = fnId === g.dataset.nodeId;
            const isTo   = tnId === g.dataset.nodeId;
            if (!isFrom && !isTo) continue;
            const wps = getArcWaypoints(pathEl);
            if (wps.length < 2) continue;
            const checkWp = isFrom ? wps[0] : wps[wps.length - 1];
            if (Math.abs(checkWp.y - cy) > 12) continue;   // not orthogonally aligned
            const sx = parseFloat(pathEl.dataset.arcSx), sy = parseFloat(pathEl.dataset.arcSy);
            const ex = parseFloat(pathEl.dataset.arcEx), ey = parseFloat(pathEl.dataset.arcEy);
            stickyArcs.push({ fromId: fnId, toId: tnId, isFrom, wps, sx, sy, ex, ey, modified: false });
        }
        nodeDrag.stickyArcs = stickyArcs;
    };

    // ── mousemove ──────────────────────────────────────────────────────────

    const onMove = e => {
        if (labelDrag) {
            e.preventDefault();
            const pt = svgPt(e);
            labelDrag.el.setAttribute('x', pt.x - labelDrag.offX - labelDrag.gx);
            labelDrag.el.setAttribute('y', pt.y - labelDrag.offY - labelDrag.gy);
            return;
        }
        if (canvasDrag) {
            e.preventDefault();
            const vb = svg.viewBox.baseVal;
            const scaleX = vb.width  / svg.clientWidth;
            const scaleY = vb.height / svg.clientHeight;
            const newX = canvasDrag.startVBX - (e.clientX - canvasDrag.startClientX) * scaleX;
            const newY = canvasDrag.startVBY - (e.clientY - canvasDrag.startClientY) * scaleY;
            svg.setAttribute('viewBox', `${newX} ${newY} ${vb.width} ${vb.height}`);
            return;
        }
        if (arcDrag) {
            e.preventDefault();
            const pt = svgPt(e);
            arcDrag.waypoints[arcDrag.wpIndex] = { x: pt.x, y: pt.y };
            repaintArc(svg, arcDrag.fromId, arcDrag.toId,
                       arcDrag.sx, arcDrag.sy, arcDrag.ex, arcDrag.ey, arcDrag.waypoints);
            return;
        }
        if (nodeDrag) {
            e.preventDefault();
            nodeMoved = true;
            const pt = svgPt(e);
            const nx = pt.x - nodeDrag.offX, ny = pt.y - nodeDrag.offY;
            nodeDrag.g.setAttribute('transform', `translate(${nx},${ny})`);

            // Shift held: preserve right-angle bends by sliding the endpoint waypoint's
            // Y to match the node's new Y.  We update the path `d` attribute directly so
            // that the subsequent updateConnectedArcs() picks up the new waypoints and
            // recomputes border-points correctly.
            if (e.shiftKey) {
                for (const sa of nodeDrag.stickyArcs) {
                    const pathEl = svg.querySelector(`[data-arc-path="${sa.fromId}|${sa.toId}"]`);
                    if (!pathEl) continue;
                    if (sa.isFrom) sa.wps[0] = { x: sa.wps[0].x, y: ny };
                    else           sa.wps[sa.wps.length - 1] = { x: sa.wps[sa.wps.length - 1].x, y: ny };
                    sa.modified = true;
                    // Write updated waypoints into the DOM so updateConnectedArcs reads them
                    const arcSx = parseFloat(pathEl.dataset.arcSx), arcSy = parseFloat(pathEl.dataset.arcSy);
                    const arcEx = parseFloat(pathEl.dataset.arcEx), arcEy = parseFloat(pathEl.dataset.arcEy);
                    const d = buildPolyPath(arcSx, arcSy, arcEx, arcEy, sa.wps);
                    pathEl.setAttribute('d', d);
                    const hitEl = svg.querySelector(`.arc-hit[data-arc-from="${sa.fromId}"][data-arc-to="${sa.toId}"]`);
                    if (hitEl) hitEl.setAttribute('d', d);
                }
            }

            updateConnectedArcs(svg, nodeDrag.nodeId, parseTranslate);
        }
    };

    // ── mouseup ────────────────────────────────────────────────────────────

    const onUp = e => {
        if (labelDrag) {
            const lx = parseFloat(labelDrag.el.getAttribute('x'));
            const ly = parseFloat(labelDrag.el.getAttribute('y'));
            dotNetRef.invokeMethodAsync('UpdateLabelOffset',
                labelDrag.id, labelDrag.kind,
                lx - labelDrag.bx, ly - labelDrag.by).catch(() => {});
            labelDrag = null;
            return;
        }
        if (canvasDrag) {
            const vb = svg.viewBox.baseVal;
            dotNetRef.invokeMethodAsync('UpdatePan', vb.x, vb.y).catch(() => {});
            canvasDrag = null;
            svg.style.cursor = 'default';
            return;
        }
        if (arcDrag) {
            const { fromId, toId, waypoints } = arcDrag;
            dotNetRef.invokeMethodAsync('FinalizeArcWaypoints',
                fromId, toId,
                waypoints.map(w => w.x),
                waypoints.map(w => w.y)).catch(() => {});
            arcDrag = null;
            return;
        }
        if (nodeDrag) {
            const pt = svgPt(e);
            nodeDrag.g.style.cursor = '';
            dotNetRef.invokeMethodAsync('UpdateNodePosition',
                nodeDrag.nodeId,
                pt.x - nodeDrag.offX,
                pt.y - nodeDrag.offY).catch(() => {});
            // Finalize any sticky arc waypoints updated during a Shift-drag so that
            // the Blazor component stores them as user bends (preserving the bends
            // after re-render).
            for (const sa of nodeDrag.stickyArcs) {
                if (!sa.modified) continue;
                dotNetRef.invokeMethodAsync('FinalizeArcWaypoints',
                    sa.fromId, sa.toId,
                    sa.wps.map(w => w.x),
                    sa.wps.map(w => w.y)).catch(() => {});
            }
            nodeDrag = null;
        }
    };

    // ── click: suppress if the mousedown turned into a drag ───────────────
    // Runs in capture phase so it beats Blazor's bubble-phase @onclick handler.

    const onClick = e => {
        if (nodeMoved) {
            nodeMoved = false;
            e.stopImmediatePropagation();
            e.preventDefault();
        }
    };

    // ── dblclick: remove nearest waypoint ─────────────────────────────────

    const onDblClick = e => {
        const pt = svgPt(e);
        const arcPathEl = nearestArcPath(svg, pt.x, pt.y, arcThreshold());
        if (!arcPathEl) return;
        const [fromId, toId] = (arcPathEl.dataset.arcPath ?? '').split('|');
        const waypoints = getArcWaypoints(arcPathEl);
        const nearIdx = nearestWaypoint(waypoints, pt.x, pt.y, 20);
        if (nearIdx >= 0) {
            e.preventDefault(); e.stopPropagation();
            dotNetRef.invokeMethodAsync('RemoveArcWaypoint', fromId, toId, nearIdx).catch(() => {});
        }
    };

    // ── wheel: two-finger scroll = pan, ctrl+scroll = zoom (handled by Blazor) ──

    const onWheel = e => {
        if (e.ctrlKey) return;   // pinch-zoom → Blazor OnWheel handles it
        e.preventDefault();
        const vb = svg.viewBox.baseVal;
        const scaleX = vb.width  / svg.clientWidth;
        const scaleY = vb.height / svg.clientHeight;
        const newX = vb.x + e.deltaX * scaleX;
        const newY = vb.y + e.deltaY * scaleY;
        svg.setAttribute('viewBox', `${newX} ${newY} ${vb.width} ${vb.height}`);
        clearTimeout(wheelTimer);
        wheelTimer = setTimeout(() => {
            dotNetRef.invokeMethodAsync('UpdatePan', newX, newY).catch(() => {});
        }, 150);
    };

    svg.addEventListener('mousedown', onDown);
    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup',   onUp);
    svg.addEventListener('dblclick',   onDblClick);
    svg.addEventListener('click',      onClick, true);  // capture phase — beats Blazor @onclick
    svg.addEventListener('wheel',      onWheel, { passive: false });

    _handlers.set(svgId, { svg, onDown, onMove, onUp, onDblClick, onClick, onWheel });
}

export function disposeDrag(svgId) {
    const h = _handlers.get(svgId);
    if (!h) return;
    h.svg.removeEventListener('mousedown', h.onDown);
    document.removeEventListener('mousemove', h.onMove);
    document.removeEventListener('mouseup',   h.onUp);
    h.svg.removeEventListener('dblclick',  h.onDblClick);
    h.svg.removeEventListener('click',     h.onClick, true);
    h.svg.removeEventListener('wheel',     h.onWheel);
    _handlers.delete(svgId);
}

// ── Source navigation ─────────────────────────────────────────────────────────

export function postNavigate(payload) {
    fetch('/api/navigate', { method: 'POST', body: payload });
}

// ── SVG export ───────────────────────────────────────────────────────────────

export function exportSvg(svgId, filename) {
    const svg = document.getElementById(svgId);
    if (!svg) return;

    const clone = svg.cloneNode(true);

    // Remove interactive-only elements
    clone.querySelectorAll('.arc-hit, title').forEach(el => el.remove());
    clone.removeAttribute('id');

    // Resolve CSS custom properties from the live document
    const cs = getComputedStyle(document.documentElement);
    const v = name => cs.getPropertyValue(name).trim();

    // Embed a self-contained <style> with all CSS variables resolved
    const styleEl = document.createElementNS('http://www.w3.org/2000/svg', 'style');
    styleEl.textContent = `
        .arrowhead { fill: ${v('--arc-color')}; }
        .arrowhead-active { fill: ${v('--arc-active')}; }
        .arc { fill: none; stroke: ${v('--arc-color')}; stroke-width: 1.6; }
        .arc-active { stroke: ${v('--arc-active')}; stroke-width: 2.2; }
        .arc-inscription { font: 11px 'Consolas','Menlo',monospace; fill: #333; }
        .place { fill: ${v('--place-fill')}; stroke: ${v('--place-stroke')}; stroke-width: 1.8; }
        .place.has-tokens { fill: #f0f8ff; }
        .place.place-active { stroke: ${v('--place-active')}; stroke-width: 2.4; }
        .place-name-inner { font: bold 12px 'Consolas','Menlo',monospace; fill: #333; text-anchor: middle; dominant-baseline: central; }
        .place-init-mark { font: italic 10px 'Consolas','Menlo',monospace; fill: #888; text-anchor: middle; }
        .place-type { font: 10px 'Consolas','Menlo',monospace; fill: #666; }
        .trans { fill: ${v('--trans-fill')}; stroke: ${v('--trans-stroke')}; stroke-width: 1.8; }
        .trans.trans-enabled { fill: #e0ffe0; stroke: ${v('--trans-enabled')}; stroke-width: 2.4; }
        .trans-name-inner { font: bold 11px 'Consolas','Menlo',monospace; fill: #333; text-anchor: middle; dominant-baseline: central; }
        .guard-label { font: italic 10px 'Consolas','Menlo',monospace; fill: #c44; text-anchor: middle; dominant-baseline: central; }
        .token-badge { fill: ${v('--green')}; stroke: white; stroke-width: 1.5; }
        .token-badge.badge-empty { fill: #ccc; stroke: #aaa; }
        .badge-count { font: bold 11px sans-serif; fill: white; text-anchor: middle; dominant-baseline: central; }
        .marking-box { fill: #fffacd; stroke: #8b6914; stroke-width: 1; opacity: 0.95; }
        .marking-text { font: 11px 'Consolas','Menlo',monospace; fill: #333; }
        .port-tag-box { fill: #d0e4f7; stroke: #2a5a8c; stroke-width: 1.2; }
        .port-tag-text { font: bold 10px 'Consolas','Menlo',monospace; fill: #2a5a8c; text-anchor: middle; dominant-baseline: central; }
        .page-group-rect { fill: none; stroke-width: 1.5; stroke-dasharray: 8 4; opacity: 0.55; }
        .page-group-label { font: italic 11px 'Consolas','Menlo',monospace; opacity: 0.75; }
    `;

    // Insert style + white background before all content
    const bg = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
    bg.setAttribute('width', '100%');
    bg.setAttribute('height', '100%');
    bg.setAttribute('fill', 'white');

    const defs = clone.querySelector('defs') || clone.firstChild;
    clone.insertBefore(styleEl, defs);

    // Crop viewBox to the bounding box of actual content
    // Temporarily insert clone into DOM to measure
    clone.style.position = 'absolute';
    clone.style.left = '-99999px';
    document.body.appendChild(clone);
    const bbox = clone.getBBox();
    document.body.removeChild(clone);
    clone.style.position = '';
    clone.style.left = '';

    const pad = 15;
    const vx = Math.floor(bbox.x - pad);
    const vy = Math.floor(bbox.y - pad);
    const vw = Math.ceil(bbox.width + pad * 2);
    const vh = Math.ceil(bbox.height + pad * 2);
    clone.setAttribute('viewBox', `${vx} ${vy} ${vw} ${vh}`);
    clone.setAttribute('width', vw);
    clone.setAttribute('height', vh);

    // Insert white background AFTER measuring (covers cropped area)
    bg.setAttribute('x', vx);
    bg.setAttribute('y', vy);
    bg.setAttribute('width', vw);
    bg.setAttribute('height', vh);
    const firstContent = styleEl.nextSibling;
    clone.insertBefore(bg, firstContent);

    // Serialize and download
    const xml = new XMLSerializer().serializeToString(clone);
    const blob = new Blob(
        ['<?xml version="1.0" encoding="UTF-8"?>\n', xml],
        { type: 'image/svg+xml' }
    );
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    a.click();
    URL.revokeObjectURL(url);
}
