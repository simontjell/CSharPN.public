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

// ── Main export ────────────────────────────────────────────────────────────────

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
    let nodeMoved    = false;  // true once the node actually moved — suppresses the ensuing click
    // stickyArc entries: { fromId, toId, isFrom, wps, sx, sy, ex, ey, modified }

    // ── mousedown ──────────────────────────────────────────────────────────

    const onDown = e => {
        if (e.button !== 0) return;
        const pt = svgPt(e);

        // Arc hit area: drag existing waypoint or insert a new one
        const hitEl = e.target.closest('.arc-hit');
        if (hitEl) {
            e.preventDefault(); e.stopPropagation();
            const fromId = hitEl.dataset.arcFrom;
            const toId   = hitEl.dataset.arcTo;
            const pathEl = svg.querySelector(`[data-arc-path="${fromId}|${toId}"]`);
            if (!pathEl) return;

            const sx = parseFloat(pathEl.dataset.arcSx), sy = parseFloat(pathEl.dataset.arcSy);
            const ex = parseFloat(pathEl.dataset.arcEx), ey = parseFloat(pathEl.dataset.arcEy);
            const waypoints = getArcWaypoints(pathEl);

            const nearIdx = nearestWaypoint(waypoints, pt.x, pt.y);
            if (nearIdx >= 0) {
                // Drag the nearby existing waypoint
                arcDrag = { fromId, toId, wpIndex: nearIdx, sx, sy, ex, ey, waypoints };
            } else {
                // Insert a new waypoint at click position
                const insertIdx = findInsertIdx(sx, sy, ex, ey, waypoints, pt.x, pt.y);
                waypoints.splice(insertIdx, 0, { x: pt.x, y: pt.y });
                repaintArc(svg, fromId, toId, sx, sy, ex, ey, waypoints);
                arcDrag = { fromId, toId, wpIndex: insertIdx, sx, sy, ex, ey, waypoints };
            }
            return;
        }

        // Node drag
        const g = e.target.closest('[data-node-id]');
        if (!g) return;
        e.preventDefault();
        const [cx, cy] = parseTranslate(g);
        nodeDrag  = { g, nodeId: g.dataset.nodeId, offX: pt.x - cx, offY: pt.y - cy };
        nodeMoved = false;
        g.style.cursor = 'grabbing';

        // Collect connected arcs whose endpoint waypoint is orthogonally aligned with this node.
        // These can be "sticky" when the user holds Shift during the drag — the right-angle
        // bend is preserved by moving the endpoint waypoint's Y together with the node.
        const [, cy] = parseTranslate(g);
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
        const hitEl = e.target.closest('.arc-hit');
        if (!hitEl) return;
        const fromId = hitEl.dataset.arcFrom;
        const toId   = hitEl.dataset.arcTo;
        const pathEl = svg.querySelector(`[data-arc-path="${fromId}|${toId}"]`);
        if (!pathEl) return;

        const pt = svgPt(e);
        const waypoints = getArcWaypoints(pathEl);
        const nearIdx = nearestWaypoint(waypoints, pt.x, pt.y, 20);
        if (nearIdx >= 0) {
            e.preventDefault(); e.stopPropagation();
            dotNetRef.invokeMethodAsync('RemoveArcWaypoint', fromId, toId, nearIdx).catch(() => {});
        }
    };

    svg.addEventListener('mousedown', onDown);
    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup',   onUp);
    svg.addEventListener('dblclick',   onDblClick);
    svg.addEventListener('click',      onClick, true);  // capture phase — beats Blazor @onclick

    _handlers.set(svgId, { svg, onDown, onMove, onUp, onDblClick, onClick });
}

export function disposeDrag(svgId) {
    const h = _handlers.get(svgId);
    if (!h) return;
    h.svg.removeEventListener('mousedown', h.onDown);
    document.removeEventListener('mousemove', h.onMove);
    document.removeEventListener('mouseup',   h.onUp);
    h.svg.removeEventListener('dblclick',  h.onDblClick);
    h.svg.removeEventListener('click',     h.onClick, true);
    _handlers.delete(svgId);
}
