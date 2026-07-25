/* =====================================================================
   tt-plot.js — dependency-free canvas line plots.

   Enough charting for the control lessons, calibration fits and telemetry
   traces: multiple series, autoscale or fixed axes, hover readout, drag to
   pan, wheel to zoom the time axis, marker/step annotations, scatter for
   measured points against a fitted line.
   ===================================================================== */
(function (global) {
    'use strict';

    const COLORS = ['#ff9e33', '#5aa9f0', '#4fc98a', '#f0685a', '#c58af0', '#f0c04a', '#5ad8d0', '#a0a8b4'];

    const css = function (name, fallback) {
        try {
            const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
            return v || fallback;
        } catch (e) { return fallback; }
    };

    function Plot(canvas, opts) {
        this.canvas = typeof canvas === 'string' ? document.getElementById(canvas) : canvas;
        this.opts = Object.assign({
            xLabel: '', yLabel: '', y2Label: '',
            padL: 62, padR: 16, padT: 12, padB: 34,
            height: 260, legend: true, interactive: true,
            xMin: null, xMax: null, yMin: null, yMax: null, y2Min: null, y2Max: null,
            title: ''
        }, opts || {});
        this.series = [];
        this.markers = [];
        this.bands = [];
        this.view = null;          // {xMin,xMax} when zoomed/panned
        this.hover = null;
        this._bind();
        this.resize();
    }

    Plot.prototype._bind = function () {
        const self = this;
        const c = this.canvas;
        if (!this.opts.interactive) return;

        c.addEventListener('mousemove', function (e) {
            const r = c.getBoundingClientRect();
            self.hover = { x: e.clientX - r.left, y: e.clientY - r.top };
            if (self._drag) {
                const dx = self.hover.x - self._drag.x0;
                const span = self._drag.xMax - self._drag.xMin;
                const perPx = span / Math.max(1, self._plotW());
                self.view = { xMin: self._drag.xMin - dx * perPx, xMax: self._drag.xMax - dx * perPx };
            }
            self.draw();
        });
        c.addEventListener('mouseleave', function () { self.hover = null; self._drag = null; self.draw(); });
        c.addEventListener('mousedown', function (e) {
            const r = c.getBoundingClientRect();
            const b = self._bounds();
            self._drag = { x0: e.clientX - r.left, xMin: b.xMin, xMax: b.xMax };
        });
        window.addEventListener('mouseup', function () { self._drag = null; });
        c.addEventListener('wheel', function (e) {
            e.preventDefault();
            const b = self._bounds();
            const r = c.getBoundingClientRect();
            const px = (e.clientX - r.left - self.opts.padL) / Math.max(1, self._plotW());
            const at = b.xMin + (b.xMax - b.xMin) * Math.min(1, Math.max(0, px));
            const f = e.deltaY > 0 ? 1.18 : 1 / 1.18;
            self.view = { xMin: at - (at - b.xMin) * f, xMax: at + (b.xMax - at) * f };
            self.draw();
        }, { passive: false });
        c.addEventListener('dblclick', function () { self.view = null; self.draw(); });
    };

    Plot.prototype.resize = function () {
        const c = this.canvas;
        const dpr = global.devicePixelRatio || 1;
        const w = c.clientWidth || 600;
        const h = this.opts.height;
        c.width = Math.round(w * dpr);
        c.height = Math.round(h * dpr);
        c.style.height = h + 'px';
        this._dpr = dpr;
        this._w = w; this._h = h;
    };

    Plot.prototype._plotW = function () { return Math.max(10, this._w - this.opts.padL - this.opts.padR); };
    Plot.prototype._plotH = function () { return Math.max(10, this._h - this.opts.padT - this.opts.padB); };

    /* series: {name, x:[], y:[], color?, axis:'left'|'right', style:'line'|'dots'|'dashed', width?} */
    Plot.prototype.setSeries = function (series) {
        this.series = (series || []).map(function (s, i) {
            return Object.assign({ color: COLORS[i % COLORS.length], axis: 'left', style: 'line', width: 1.8 }, s);
        });
        return this;
    };
    Plot.prototype.setMarkers = function (m) { this.markers = m || []; return this; };
    Plot.prototype.setBands = function (b) { this.bands = b || []; return this; };

    Plot.prototype._bounds = function () {
        const o = this.opts;
        let xMin = Infinity, xMax = -Infinity, yMin = Infinity, yMax = -Infinity, y2Min = Infinity, y2Max = -Infinity;
        this.series.forEach(function (s) {
            for (let i = 0; i < s.x.length; i++) {
                const xv = s.x[i], yv = s.y[i];
                if (!isFinite(xv) || !isFinite(yv)) continue;
                if (xv < xMin) xMin = xv;
                if (xv > xMax) xMax = xv;
                if (s.axis === 'right') {
                    if (yv < y2Min) y2Min = yv;
                    if (yv > y2Max) y2Max = yv;
                } else {
                    if (yv < yMin) yMin = yv;
                    if (yv > yMax) yMax = yv;
                }
            }
        });
        if (!isFinite(xMin)) { xMin = 0; xMax = 1; }
        if (!isFinite(yMin)) { yMin = 0; yMax = 1; }
        if (xMax - xMin < 1e-9) xMax = xMin + 1;

        // Pad the value axis a little so lines don't graze the frame.
        const padY = (yMax - yMin) * 0.08 || 0.5;
        yMin -= padY; yMax += padY;
        if (isFinite(y2Min)) {
            const p2 = (y2Max - y2Min) * 0.08 || 0.5;
            y2Min -= p2; y2Max += p2;
        }

        if (o.xMin !== null) xMin = o.xMin;
        if (o.xMax !== null) xMax = o.xMax;
        if (o.yMin !== null) yMin = o.yMin;
        if (o.yMax !== null) yMax = o.yMax;
        if (o.y2Min !== null) y2Min = o.y2Min;
        if (o.y2Max !== null) y2Max = o.y2Max;
        if (this.view) { xMin = this.view.xMin; xMax = this.view.xMax; }
        return { xMin: xMin, xMax: xMax, yMin: yMin, yMax: yMax, y2Min: y2Min, y2Max: y2Max };
    };

    function niceTicks(lo, hi, count) {
        const span = hi - lo;
        if (!(span > 0)) return [lo];
        const raw = span / Math.max(1, count);
        const mag = Math.pow(10, Math.floor(Math.log10(raw)));
        const norm = raw / mag;
        const step = (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * mag;
        const out = [];
        for (let v = Math.ceil(lo / step) * step; v <= hi + step * 0.001; v += step) out.push(v);
        return out;
    }
    function fmt(v) {
        const a = Math.abs(v);
        if (a === 0) return '0';
        if (a >= 1e5 || a < 1e-3) return v.toExponential(1);
        if (a >= 100) return v.toFixed(0);
        if (a >= 10) return v.toFixed(1);
        if (a >= 1) return v.toFixed(2);
        return v.toFixed(3);
    }

    Plot.prototype.draw = function () {
        const c = this.canvas, ctx = c.getContext('2d');
        const o = this.opts, dpr = this._dpr;
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.clearRect(0, 0, this._w, this._h);

        const b = this._bounds();
        const pw = this._plotW(), ph = this._plotH();
        const x0 = o.padL, y0 = o.padT;

        const fg = css('--fg', '#d8dde5'), dim = css('--fg-dim', '#8d97a6'), line = css('--line-soft', '#2a2f38');
        const sx = function (v) { return x0 + (v - b.xMin) / (b.xMax - b.xMin) * pw; };
        const sy = function (v) { return y0 + ph - (v - b.yMin) / (b.yMax - b.yMin) * ph; };
        const hasRight = isFinite(b.y2Min) && this.series.some(function (s) { return s.axis === 'right'; });
        const sy2 = function (v) { return y0 + ph - (v - b.y2Min) / (b.y2Max - b.y2Min) * ph; };

        // Shaded bands (e.g. a settling tolerance).
        const self = this;
        this.bands.forEach(function (band) {
            ctx.fillStyle = band.color || 'rgba(255,158,51,0.08)';
            const ya = sy(band.yLo), yb = sy(band.yHi);
            ctx.fillRect(x0, Math.min(ya, yb), pw, Math.abs(yb - ya));
        });

        // Grid + ticks.
        ctx.strokeStyle = line; ctx.lineWidth = 1;
        ctx.fillStyle = dim; ctx.font = '11px ui-monospace, Consolas, monospace';
        ctx.textAlign = 'right'; ctx.textBaseline = 'middle';
        niceTicks(b.yMin, b.yMax, 5).forEach(function (v) {
            const y = sy(v);
            if (y < y0 - 1 || y > y0 + ph + 1) return;
            ctx.beginPath(); ctx.moveTo(x0, y); ctx.lineTo(x0 + pw, y); ctx.stroke();
            ctx.fillText(fmt(v), x0 - 6, y);
        });
        ctx.textAlign = 'center'; ctx.textBaseline = 'top';
        niceTicks(b.xMin, b.xMax, 6).forEach(function (v) {
            const x = sx(v);
            if (x < x0 - 1 || x > x0 + pw + 1) return;
            ctx.beginPath(); ctx.moveTo(x, y0); ctx.lineTo(x, y0 + ph); ctx.stroke();
            ctx.fillText(fmt(v), x, y0 + ph + 6);
        });
        if (hasRight) {
            ctx.textAlign = 'left'; ctx.textBaseline = 'middle';
            niceTicks(b.y2Min, b.y2Max, 5).forEach(function (v) {
                const y = sy2(v);
                if (y < y0 - 1 || y > y0 + ph + 1) return;
                ctx.fillText(fmt(v), x0 + pw + 5, y);
            });
        }

        // Frame.
        ctx.strokeStyle = css('--line', '#333944');
        ctx.strokeRect(x0 + 0.5, y0 + 0.5, pw, ph);

        // Vertical markers.
        this.markers.forEach(function (m) {
            const x = sx(m.x);
            if (x < x0 || x > x0 + pw) return;
            ctx.save();
            ctx.strokeStyle = m.color || '#f0c04a';
            ctx.setLineDash([4, 3]);
            ctx.beginPath(); ctx.moveTo(x, y0); ctx.lineTo(x, y0 + ph); ctx.stroke();
            ctx.restore();
            if (m.label) {
                ctx.fillStyle = m.color || '#f0c04a';
                ctx.textAlign = 'left'; ctx.textBaseline = 'top';
                ctx.font = '10px ui-monospace, Consolas, monospace';
                ctx.fillText(m.label, x + 3, y0 + 3);
            }
        });

        // Series.
        ctx.save();
        ctx.beginPath(); ctx.rect(x0, y0, pw, ph); ctx.clip();
        this.series.forEach(function (s) {
            const map = s.axis === 'right' && hasRight ? sy2 : sy;
            ctx.strokeStyle = s.color; ctx.fillStyle = s.color; ctx.lineWidth = s.width;
            if (s.style === 'dots') {
                for (let i = 0; i < s.x.length; i++) {
                    const px = sx(s.x[i]), py = map(s.y[i]);
                    if (!isFinite(px) || !isFinite(py)) continue;
                    ctx.beginPath(); ctx.arc(px, py, s.width + 1.4, 0, Math.PI * 2); ctx.fill();
                }
            } else {
                ctx.setLineDash(s.style === 'dashed' ? [6, 4] : []);
                ctx.beginPath();
                let started = false;
                for (let i = 0; i < s.x.length; i++) {
                    const px = sx(s.x[i]), py = map(s.y[i]);
                    if (!isFinite(px) || !isFinite(py)) { started = false; continue; }
                    if (!started) { ctx.moveTo(px, py); started = true; } else ctx.lineTo(px, py);
                }
                ctx.stroke();
                ctx.setLineDash([]);
            }
        });
        ctx.restore();

        // Hover crosshair + readout.
        if (this.hover && this.hover.x >= x0 && this.hover.x <= x0 + pw && this.series.length) {
            const xv = b.xMin + (this.hover.x - x0) / pw * (b.xMax - b.xMin);
            ctx.save();
            ctx.strokeStyle = 'rgba(255,255,255,0.22)';
            ctx.beginPath(); ctx.moveTo(this.hover.x, y0); ctx.lineTo(this.hover.x, y0 + ph); ctx.stroke();
            ctx.restore();

            const rows = [];
            this.series.forEach(function (s) {
                if (!s.x.length) return;
                let best = 0, bd = Infinity;
                for (let i = 0; i < s.x.length; i++) {
                    const d = Math.abs(s.x[i] - xv);
                    if (d < bd) { bd = d; best = i; }
                }
                rows.push({ name: s.name, v: s.y[best], color: s.color });
            });
            const bw = 128, bh = 16 + rows.length * 14;
            let bx = this.hover.x + 10, by = y0 + 8;
            if (bx + bw > x0 + pw) bx = this.hover.x - bw - 10;
            ctx.fillStyle = 'rgba(18,20,26,0.92)';
            ctx.strokeStyle = css('--line', '#333944');
            ctx.fillRect(bx, by, bw, bh); ctx.strokeRect(bx + 0.5, by + 0.5, bw, bh);
            ctx.font = '11px ui-monospace, Consolas, monospace';
            ctx.textAlign = 'left'; ctx.textBaseline = 'top';
            ctx.fillStyle = dim;
            ctx.fillText((o.xLabel || 'x') + ' ' + fmt(xv), bx + 6, by + 4);
            rows.forEach(function (r, i) {
                ctx.fillStyle = r.color;
                ctx.fillText(r.name + ' ' + fmt(r.v), bx + 6, by + 18 + i * 14);
            });
        }

        // Axis labels.
        ctx.fillStyle = dim; ctx.font = '11px system-ui, sans-serif';
        if (o.xLabel) { ctx.textAlign = 'center'; ctx.textBaseline = 'bottom'; ctx.fillText(o.xLabel, x0 + pw / 2, this._h - 2); }
        if (o.yLabel) {
            ctx.save(); ctx.translate(11, y0 + ph / 2); ctx.rotate(-Math.PI / 2);
            ctx.textAlign = 'center'; ctx.textBaseline = 'top'; ctx.fillText(o.yLabel, 0, 0); ctx.restore();
        }
        if (o.title) {
            ctx.fillStyle = fg; ctx.textAlign = 'left'; ctx.textBaseline = 'top';
            ctx.font = '12px system-ui, sans-serif';
            ctx.fillText(o.title, x0 + 4, y0 + 2);
        }
    };

    /* Render a <div> legend next to the canvas (kept out of the canvas so
       it stays selectable text). */
    Plot.prototype.renderLegend = function (el) {
        const target = typeof el === 'string' ? document.getElementById(el) : el;
        if (!target) return;
        target.className = 'tt-plot-legend';
        target.innerHTML = this.series.map(function (s) {
            return '<span><i style="background:' + s.color + '"></i>' + s.name + '</span>';
        }).join('');
    };

    Plot.prototype.resetView = function () { this.view = null; return this; };

    global.TT = global.TT || {};
    global.TT.Plot = Plot;
    global.TT.PlotColors = COLORS;
})(typeof window !== 'undefined' ? window : globalThis);
