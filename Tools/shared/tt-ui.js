/* =====================================================================
   tt-ui.js — small DOM helpers so the pages stay readable.

   Nothing framework-shaped: just element construction, a labelled-slider
   widget that keeps a number input and a range input in sync, and the
   top-down / side SVG schematic of a vehicle design.
   ===================================================================== */
(function (global) {
    'use strict';

    function el(tag, attrs, children) {
        const node = document.createElement(tag);
        if (attrs) {
            Object.keys(attrs).forEach(function (k) {
                if (k === 'class') node.className = attrs[k];
                else if (k === 'html') node.innerHTML = attrs[k];
                else if (k === 'text') node.textContent = attrs[k];
                else if (k.slice(0, 2) === 'on') node.addEventListener(k.slice(2), attrs[k]);
                else if (attrs[k] !== null && attrs[k] !== undefined) node.setAttribute(k, attrs[k]);
            });
        }
        (children || []).forEach(function (c) {
            if (c === null || c === undefined) return;
            node.appendChild(typeof c === 'string' ? document.createTextNode(c) : c);
        });
        return node;
    }
    function $(sel, root) { return (root || document).querySelector(sel); }
    function $$(sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); }
    function clear(node) { while (node.firstChild) node.removeChild(node.firstChild); return node; }

    function fmt(v, dp) {
        if (v === undefined || v === null || !isFinite(v)) return '—';
        return v.toFixed(dp === undefined ? 3 : dp);
    }

    /* A labelled slider bound to an object property.
       spec: {label, unit, min, max, step, dp, hint, get, set, onChange} */
    function slider(spec) {
        const val = el('input', { type: 'number', step: spec.step || 'any', class: 'val' });
        const range = el('input', { type: 'range', min: spec.min, max: spec.max, step: spec.step || 'any' });
        const dp = spec.dp === undefined ? 3 : spec.dp;

        function push(v) {
            const clamped = Math.min(spec.max, Math.max(spec.min, v));
            spec.set(clamped);
            sync();
            if (spec.onChange) spec.onChange(clamped);
        }
        function sync() {
            const v = spec.get();
            range.value = String(v);
            if (document.activeElement !== val) val.value = Number(v.toFixed(dp + 2));
        }
        range.addEventListener('input', function () { push(Number(range.value)); });
        val.addEventListener('change', function () { push(Number(val.value)); });

        const wrap = el('div', { class: 'field' }, [
            el('label', { text: spec.label + (spec.unit ? '  (' + spec.unit + ')' : '') }),
            el('div', { class: 'slider-row' }, [range, val]),
            spec.hint ? el('div', { class: 'hint', html: spec.hint }) : null
        ]);
        wrap.sync = sync;
        sync();
        return wrap;
    }

    /* A plain number field (for values with no sensible slider range). */
    function numberField(spec) {
        const input = el('input', { type: 'number', step: spec.step || 'any', value: spec.get() });
        input.addEventListener('change', function () {
            const v = Number(input.value);
            spec.set(isFinite(v) ? v : 0);
            if (spec.onChange) spec.onChange(v);
        });
        const wrap = el('div', { class: 'field' }, [
            el('label', { text: spec.label + (spec.unit ? '  (' + spec.unit + ')' : '') }),
            input,
            spec.hint ? el('div', { class: 'hint', html: spec.hint }) : null
        ]);
        wrap.sync = function () { input.value = spec.get(); };
        return wrap;
    }

    function textField(spec) {
        const input = el('input', { type: 'text', value: spec.get() || '' });
        input.addEventListener('input', function () {
            spec.set(input.value);
            if (spec.onChange) spec.onChange(input.value);
        });
        const wrap = el('div', { class: 'field' }, [
            el('label', { text: spec.label }), input,
            spec.hint ? el('div', { class: 'hint', html: spec.hint }) : null
        ]);
        wrap.sync = function () { input.value = spec.get() || ''; };
        return wrap;
    }

    function checkField(spec) {
        const input = el('input', { type: 'checkbox' });
        input.checked = !!spec.get();
        input.addEventListener('change', function () {
            spec.set(input.checked);
            if (spec.onChange) spec.onChange(input.checked);
        });
        const wrap = el('div', { class: 'field' }, [
            el('label', { class: 'lbl' }, [input, document.createTextNode(spec.label)]),
            spec.hint ? el('div', { class: 'hint', html: spec.hint }) : null
        ]);
        wrap.sync = function () { input.checked = !!spec.get(); };
        return wrap;
    }

    function selectField(spec) {
        const sel = el('select', {}, spec.options.map(function (o) {
            return el('option', { value: o.value, text: o.label });
        }));
        sel.value = String(spec.get());
        sel.addEventListener('change', function () {
            const v = spec.numeric ? Number(sel.value) : sel.value;
            spec.set(v);
            if (spec.onChange) spec.onChange(v);
        });
        const wrap = el('div', { class: 'field' }, [
            el('label', { text: spec.label }), sel,
            spec.hint ? el('div', { class: 'hint', html: spec.hint }) : null
        ]);
        wrap.sync = function () { sel.value = String(spec.get()); };
        return wrap;
    }

    /* A row of mutually exclusive chips. */
    function chipRow(spec) {
        const chips = spec.options.map(function (o) {
            const c = el('span', { class: 'chip', text: o.label, title: o.title || '' });
            c.addEventListener('click', function () {
                spec.set(o.value);
                paint();
                if (spec.onChange) spec.onChange(o.value);
            });
            c._value = o.value;
            return c;
        });
        function paint() {
            const cur = String(spec.get());
            chips.forEach(function (c) { c.classList.toggle('on', String(c._value) === cur); });
        }
        paint();
        const wrap = el('div', { class: 'field' }, [
            spec.label ? el('label', { text: spec.label }) : null,
            el('div', { class: 'btn-row' }, chips),
            spec.hint ? el('div', { class: 'hint', html: spec.hint }) : null
        ]);
        wrap.sync = paint;
        return wrap;
    }

    // ---- Vehicle schematic ------------------------------------------------

    const SVG_NS = 'http://www.w3.org/2000/svg';
    function svgEl(tag, attrs) {
        const n = document.createElementNS(SVG_NS, tag);
        Object.keys(attrs || {}).forEach(function (k) {
            if (k === 'text') n.textContent = attrs[k];
            else n.setAttribute(k, attrs[k]);
        });
        return n;
    }

    /* Draw a design as a top-down + side schematic. Body-local axes: +x is
       right, +y is up, +z is forward, so in the top view forward points up
       the screen and in the side view forward points right. */
    function drawSchematic(container, design, opts) {
        const o = opts || {};
        const S = global.TT.Schema;
        const d = S.normalize(design);
        const target = typeof container === 'string' ? document.getElementById(container) : container;
        if (!target) return;
        clear(target);

        const W = target.clientWidth || 420;
        const halfW = W / 2 - 6;

        // World extents (metres) with a margin, so long cars still fit.
        const ext = { x: d.bodySize.x / 2, z: d.bodySize.z / 2, y: d.bodySize.y };
        d.wheels.forEach(function (w) {
            const off = S.hubOffset(w.localPos.x, w.suspAngleDeg, w.suspLength);
            ext.x = Math.max(ext.x, Math.abs(w.localPos.x + off.x) + w.radius * 0.6);
            ext.z = Math.max(ext.z, Math.abs(w.localPos.z) + w.radius);
            ext.y = Math.max(ext.y, Math.abs(w.localPos.y + off.y) + w.radius);
        });
        d.sensors.concat(d.aero, d.batteries, d.antennas).forEach(function (p) {
            ext.x = Math.max(ext.x, Math.abs(p.localPos.x) + 0.01);
            ext.z = Math.max(ext.z, Math.abs(p.localPos.z) + 0.01);
            ext.y = Math.max(ext.y, Math.abs(p.localPos.y) + 0.01);
        });
        const spanX = ext.x * 2 * 1.12, spanZ = ext.z * 2 * 1.08;

        const accent = '#ff9e33', dim = '#8d97a6', line = '#3b424e';
        const bodyFill = colorToCss(d.bodyColor, 0.30), bodyStroke = colorToCss(d.bodyColor, 0.95);

        // ---- Top view ----
        const topH = Math.round(halfW * (spanZ / spanX));
        const top = svgEl('svg', { width: halfW, height: Math.min(topH, 340), viewBox: '0 0 ' + halfW + ' ' + Math.min(topH, 340), class: 'svg-preview' });
        const tScale = Math.min(halfW / spanX, Math.min(topH, 340) / spanZ);
        const tcx = halfW / 2, tcy = Math.min(topH, 340) / 2;
        const TX = function (x) { return tcx + x * tScale; };
        const TY = function (z) { return tcy - z * tScale; };   // +z (forward) is up

        top.appendChild(svgEl('rect', {
            x: TX(-d.bodySize.x / 2), y: TY(d.bodySize.z / 2),
            width: d.bodySize.x * tScale, height: d.bodySize.z * tScale,
            rx: Math.min(10, d.bodySize.x * tScale * 0.18),
            fill: bodyFill, stroke: bodyStroke, 'stroke-width': 1.4
        }));
        // Nose marker so "which way is forward" is never in doubt.
        top.appendChild(svgEl('path', {
            d: 'M ' + TX(0) + ' ' + (TY(d.bodySize.z / 2) - 9) +
               ' L ' + (TX(0) - 6) + ' ' + (TY(d.bodySize.z / 2) - 1) +
               ' L ' + (TX(0) + 6) + ' ' + (TY(d.bodySize.z / 2) - 1) + ' Z',
            fill: accent
        }));

        d.wheels.forEach(function (w) {
            const off = S.hubOffset(w.localPos.x, w.suspAngleDeg, w.suspLength);
            const hx = w.localPos.x + off.x;
            const tw = w.radius * 0.8;      // tread width, drawn to scale-ish
            const g = svgEl('g', {
                transform: 'rotate(' + w.yaw + ' ' + TX(hx) + ' ' + TY(w.localPos.z) + ')'
            });
            g.appendChild(svgEl('rect', {
                x: TX(hx) - tw * tScale / 2, y: TY(w.localPos.z) - w.radius * tScale,
                width: tw * tScale, height: w.radius * 2 * tScale, rx: 2,
                fill: w.powered ? '#4a4f5a' : '#2c313a',
                stroke: w.allowsSteering ? accent : '#565d6a', 'stroke-width': 1.2
            }));
            top.appendChild(g);
        });

        d.batteries.forEach(function (b) {
            top.appendChild(svgEl('rect', {
                x: TX(b.localPos.x) - 0.047 / 2 * tScale, y: TY(b.localPos.z) - 0.138 / 2 * tScale,
                width: 0.047 * tScale, height: 0.138 * tScale, rx: 2,
                fill: 'none', stroke: '#5aa9f0', 'stroke-width': 1.2, 'stroke-dasharray': '3 2'
            }));
        });

        d.aero.forEach(function (a) {
            const wSpan = 0.10 * (a.sizeScale || 1);
            top.appendChild(svgEl('rect', {
                x: TX(a.localPos.x) - wSpan / 2 * tScale, y: TY(a.localPos.z) - 2,
                width: wSpan * tScale, height: 4, fill: '#c58af0', opacity: 0.85
            }));
        });

        d.sensors.forEach(function (s) {
            const col = sensorColor(s.kind);
            // Aim wedge for the sensors that actually point somewhere.
            if (s.kind === S.SensorType.Tof || s.kind === S.SensorType.Camera) {
                const yaw = (s.aimEuler.y || 0) * Math.PI / 180;
                const half = ((s.kind === S.SensorType.Camera ? (s.camFov || 60) : Math.max(4, s.coneAngle || 8)) / 2) * Math.PI / 180;
                const len = Math.min(s.kind === S.SensorType.Camera ? 0.22 : (s.range || 4) * 0.10, ext.z * 0.9) * tScale;
                const px = TX(s.localPos.x), py = TY(s.localPos.z);
                // Sensor yaw is measured about +y with 0 = forward (+z).
                const a1 = yaw - half - Math.PI / 2, a2 = yaw + half - Math.PI / 2;
                top.appendChild(svgEl('path', {
                    d: 'M ' + px + ' ' + py +
                       ' L ' + (px + Math.cos(a1) * len) + ' ' + (py + Math.sin(a1) * len) +
                       ' L ' + (px + Math.cos(a2) * len) + ' ' + (py + Math.sin(a2) * len) + ' Z',
                    fill: col, opacity: 0.16, stroke: col, 'stroke-width': 0.6, 'stroke-opacity': 0.5
                }));
            }
            top.appendChild(svgEl('circle', { cx: TX(s.localPos.x), cy: TY(s.localPos.z), r: 3, fill: col }));
        });

        // Centre of mass.
        const mp = S.massProperties(d);
        top.appendChild(comMarker(TX(mp.com.x), TY(mp.com.z)));

        // ---- Side view ----
        const sideH = Math.min(topH, 340);
        const side = svgEl('svg', { width: halfW, height: sideH, viewBox: '0 0 ' + halfW + ' ' + sideH, class: 'svg-preview' });
        const sScale = Math.min(halfW / spanZ, sideH / (ext.y * 3.4));
        const scx = halfW / 2, scy = sideH * 0.60;
        const SX = function (z) { return scx + z * sScale; };   // +z (forward) is right
        const SY = function (y) { return scy - y * sScale; };

        // Ground line: the wheels' contact patch.
        let groundY = 0;
        d.wheels.forEach(function (w) {
            const off = S.hubOffset(w.localPos.x, w.suspAngleDeg, w.suspLength);
            groundY = Math.min(groundY, w.localPos.y + off.y - w.radius);
        });
        side.appendChild(svgEl('line', {
            x1: 0, y1: SY(groundY), x2: halfW, y2: SY(groundY),
            stroke: line, 'stroke-width': 1, 'stroke-dasharray': '4 3'
        }));

        side.appendChild(svgEl('rect', {
            x: SX(-d.bodySize.z / 2), y: SY(d.bodySize.y / 2),
            width: d.bodySize.z * sScale, height: d.bodySize.y * sScale,
            rx: Math.min(8, d.bodySize.y * sScale * 0.3),
            fill: bodyFill, stroke: bodyStroke, 'stroke-width': 1.4
        }));

        d.wheels.forEach(function (w) {
            const off = S.hubOffset(w.localPos.x, w.suspAngleDeg, w.suspLength);
            const hy = w.localPos.y + off.y;
            // Strut, drawn from the body mount down to the hub.
            if (w.suspLength > 0) {
                side.appendChild(svgEl('line', {
                    x1: SX(w.localPos.z), y1: SY(w.localPos.y),
                    x2: SX(w.localPos.z), y2: SY(hy),
                    stroke: '#7a8496', 'stroke-width': 2.5, 'stroke-linecap': 'round'
                }));
            }
            side.appendChild(svgEl('circle', {
                cx: SX(w.localPos.z), cy: SY(hy), r: w.radius * sScale,
                fill: 'none', stroke: w.powered ? accent : '#7a8496', 'stroke-width': 2
            }));
            side.appendChild(svgEl('circle', { cx: SX(w.localPos.z), cy: SY(hy), r: 1.6, fill: '#7a8496' }));
        });

        d.batteries.forEach(function (b) {
            side.appendChild(svgEl('rect', {
                x: SX(b.localPos.z) - 0.138 / 2 * sScale, y: SY(b.localPos.y) - 0.025 / 2 * sScale,
                width: 0.138 * sScale, height: 0.025 * sScale, rx: 1,
                fill: 'none', stroke: '#5aa9f0', 'stroke-width': 1.2, 'stroke-dasharray': '3 2'
            }));
        });
        d.sensors.forEach(function (s) {
            side.appendChild(svgEl('circle', {
                cx: SX(s.localPos.z), cy: SY(s.localPos.y), r: 2.6, fill: sensorColor(s.kind)
            }));
        });
        d.aero.forEach(function (a) {
            side.appendChild(svgEl('line', {
                x1: SX(a.localPos.z) - 0.02 * sScale, y1: SY(a.localPos.y),
                x2: SX(a.localPos.z) + 0.02 * sScale, y2: SY(a.localPos.y) + (a.angleDeg || 0) * 0.35,
                stroke: '#c58af0', 'stroke-width': 2.5
            }));
        });
        d.antennas.forEach(function (a) {
            const h = 0.095 * (a.sizeScale || 1);
            const tilt = (a.tiltDeg || 0) * Math.PI / 180;
            side.appendChild(svgEl('line', {
                x1: SX(a.localPos.z), y1: SY(a.localPos.y),
                x2: SX(a.localPos.z - Math.sin(tilt) * h), y2: SY(a.localPos.y + Math.cos(tilt) * h),
                stroke: '#4fc98a', 'stroke-width': 1.6
            }));
        });
        side.appendChild(comMarker(SX(mp.com.z), SY(mp.com.y)));

        const wrap = el('div', { class: 'row', style: 'gap:8px' }, [
            el('div', { style: 'flex:1 1 0;min-width:0' }, [
                el('div', { class: 'tiny muted center', text: 'top — nose up' })
            ]),
            el('div', { style: 'flex:1 1 0;min-width:0' }, [
                el('div', { class: 'tiny muted center', text: 'side — nose right' })
            ])
        ]);
        wrap.children[0].insertBefore(top, wrap.children[0].firstChild);
        wrap.children[1].insertBefore(side, wrap.children[1].firstChild);
        target.appendChild(wrap);
        target.appendChild(el('div', { class: 'tt-plot-legend', html:
            '<span><i style="background:' + accent + '"></i>powered / steered</span>' +
            '<span><i style="background:#5aa9f0"></i>battery</span>' +
            '<span><i style="background:#f0685a"></i>ToF</span>' +
            '<span><i style="background:#4fc98a"></i>encoder</span>' +
            '<span><i style="background:#c58af0"></i>camera / aero</span>' +
            '<span><i style="background:#f0c04a"></i>centre of mass</span>'
        }));

        function comMarker(cx, cy) {
            const g = svgEl('g', {});
            g.appendChild(svgEl('circle', { cx: cx, cy: cy, r: 5, fill: 'none', stroke: '#f0c04a', 'stroke-width': 1.4 }));
            g.appendChild(svgEl('path', {
                d: 'M ' + (cx - 5) + ' ' + cy + ' L ' + (cx + 5) + ' ' + cy +
                   ' M ' + cx + ' ' + (cy - 5) + ' L ' + cx + ' ' + (cy + 5),
                stroke: '#f0c04a', 'stroke-width': 1.1
            }));
            return g;
        }
    }

    function sensorColor(kind) {
        const S = global.TT.Schema;
        switch (kind) {
            case S.SensorType.Tof: return '#f0685a';
            case S.SensorType.Encoder: return '#4fc98a';
            case S.SensorType.Camera: return '#c58af0';
            case S.SensorType.Suspension: return '#f0c04a';
            case S.SensorType.Battery: return '#5aa9f0';
            default: return '#8d97a6';
        }
    }

    function colorToCss(c, alpha) {
        const to255 = function (v) { return Math.round(Math.min(1, Math.max(0, v)) * 255); };
        return 'rgba(' + to255(c.r) + ',' + to255(c.g) + ',' + to255(c.b) + ',' + (alpha === undefined ? 1 : alpha) + ')';
    }
    function cssToColor(hex) {
        const m = /^#?([0-9a-f]{6})$/i.exec(hex);
        if (!m) return { r: 0.2, g: 0.55, b: 0.95, a: 1 };
        const n = parseInt(m[1], 16);
        return { r: ((n >> 16) & 255) / 255, g: ((n >> 8) & 255) / 255, b: (n & 255) / 255, a: 1 };
    }
    function colorToHex(c) {
        const to255 = function (v) { return Math.round(Math.min(1, Math.max(0, v)) * 255); };
        return '#' + [to255(c.r), to255(c.g), to255(c.b)].map(function (v) {
            return ('0' + v.toString(16)).slice(-2);
        }).join('');
    }

    /* Render a validation report ([{level,msg}]) into an element. */
    function renderIssues(target, issues) {
        const node = typeof target === 'string' ? document.getElementById(target) : target;
        clear(node);
        if (!issues.length) {
            node.appendChild(el('div', { class: 'callout good', html: '<b>No problems found.</b> This design should load and drive.' }));
            return;
        }
        const order = { error: 0, warn: 1, info: 2 };
        issues.slice().sort(function (a, b) { return order[a.level] - order[b.level]; }).forEach(function (i) {
            node.appendChild(el('div', {
                class: 'callout ' + (i.level === 'error' ? 'bad' : i.level === 'warn' ? 'warn' : ''),
                html: '<b>' + (i.level === 'error' ? 'Error' : i.level === 'warn' ? 'Check' : 'Note') + ':</b> ' + i.msg
            }));
        });
    }

    global.TT = global.TT || {};
    global.TT.UI = {
        el: el, $: $, $$: $$, clear: clear, fmt: fmt, svgEl: svgEl,
        slider: slider, numberField: numberField, textField: textField,
        checkField: checkField, selectField: selectField, chipRow: chipRow,
        drawSchematic: drawSchematic, renderIssues: renderIssues,
        colorToCss: colorToCss, cssToColor: cssToColor, colorToHex: colorToHex,
        sensorColor: sensorColor
    };
})(typeof window !== 'undefined' ? window : globalThis);
