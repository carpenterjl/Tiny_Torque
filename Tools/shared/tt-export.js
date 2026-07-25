/* =====================================================================
   tt-export.js — getting generated files out of the browser and into the
   places the game and the C toolchain actually read.

   Two paths, because browsers differ:
     • Chromium (Chrome/Edge): the File System Access API. The user picks a
       folder once; the handle is kept in IndexedDB so the next visit saves
       straight there with a single permission re-prompt.
     • Everything else (Firefox/Safari): a plain download, with the exact
       destination path shown so the user can drop the file in.

   Also here: localStorage hand-off between pages, and CSV parsing for the
   telemetry/calibration tools.
   ===================================================================== */
(function (global) {
    'use strict';

    const DB_NAME = 'tiny-torque-tools';
    const STORE = 'handles';

    const supported = typeof global.showDirectoryPicker === 'function';

    // ---- IndexedDB handle persistence -----------------------------------

    function idb() {
        return new Promise(function (resolve, reject) {
            const req = indexedDB.open(DB_NAME, 1);
            req.onupgradeneeded = function () { req.result.createObjectStore(STORE); };
            req.onsuccess = function () { resolve(req.result); };
            req.onerror = function () { reject(req.error); };
        });
    }
    function idbGet(key) {
        return idb().then(function (db) {
            return new Promise(function (resolve, reject) {
                const tx = db.transaction(STORE, 'readonly').objectStore(STORE).get(key);
                tx.onsuccess = function () { resolve(tx.result); };
                tx.onerror = function () { reject(tx.error); };
            });
        }).catch(function () { return null; });
    }
    function idbSet(key, value) {
        return idb().then(function (db) {
            return new Promise(function (resolve, reject) {
                const tx = db.transaction(STORE, 'readwrite').objectStore(STORE).put(value, key);
                tx.onsuccess = function () { resolve(true); };
                tx.onerror = function () { reject(tx.error); };
            });
        }).catch(function () { return false; });
    }

    // ---- Destination paths (what to tell the user) -----------------------

    const Paths = {
        vehiclesEditor: '<repo>\\UnitySim\\Vehicles\\',
        vehiclesBuild: '%USERPROFILE%\\AppData\\LocalLow\\<company>\\<product>\\Vehicles\\',
        tracksEditor: '<repo>\\UnitySim\\Tracks\\',
        controllers: '<repo>\\Controllers\\',
        telemetry: '<repo>\\UnitySim\\TelemetryLogs\\',
        plugins: '<repo>\\UnitySim\\Assets\\Plugins\\x86_64\\'
    };

    function pathHelp(kind) {
        if (kind === 'vehicles') {
            return 'Running the game from the Unity editor? Drop it in <code>' + Paths.vehiclesEditor + '</code>. ' +
                'Running an installed build? Use <code>' + Paths.vehiclesBuild + '</code> ' +
                '(paste <code>%USERPROFILE%\\AppData\\LocalLow</code> into Explorer\'s address bar to get there).';
        }
        if (kind === 'controllers') {
            return 'Put the folder under <code>' + Paths.controllers + '</code>, add the <code>add_controller(...)</code> line to ' +
                '<code>Controllers\\CMakeLists.txt</code>, then run <code>build.ps1</code>.';
        }
        return '';
    }

    // ---- Directory handles -----------------------------------------------

    /* Ask for (or recall) a directory the page may write into. `key` scopes
       the memory so the vehicles folder and the controllers folder are
       remembered separately. */
    function pickDirectory(key) {
        if (!supported) return Promise.reject(new Error('This browser has no File System Access API — use the download button instead.'));
        return global.showDirectoryPicker({ id: 'tt-' + key, mode: 'readwrite' }).then(function (handle) {
            return idbSet(key, handle).then(function () { return handle; });
        });
    }

    function savedDirectory(key) {
        if (!supported) return Promise.resolve(null);
        return idbGet(key).then(function (handle) {
            if (!handle) return null;
            // A remembered handle still needs permission each session.
            return handle.queryPermission({ mode: 'readwrite' }).then(function (state) {
                return state === 'granted' ? handle : { needsPermission: true, handle: handle };
            }).catch(function () { return null; });
        });
    }

    function requestPermission(handle) {
        return handle.requestPermission({ mode: 'readwrite' }).then(function (s) { return s === 'granted'; });
    }

    /* Write one file into a directory handle. */
    function writeFile(dirHandle, name, text) {
        return dirHandle.getFileHandle(name, { create: true })
            .then(function (fh) { return fh.createWritable(); })
            .then(function (w) { return w.write(text).then(function () { return w.close(); }); })
            .then(function () { return name; });
    }

    /* Write a set of files, creating a subfolder if `subdir` is given. */
    function writeFiles(dirHandle, files, subdir) {
        const target = subdir
            ? dirHandle.getDirectoryHandle(subdir, { create: true })
            : Promise.resolve(dirHandle);
        return target.then(function (dir) {
            return files.reduce(function (chain, f) {
                return chain.then(function (done) {
                    return writeFile(dir, f.name, f.text).then(function (n) { return done.concat([n]); });
                });
            }, Promise.resolve([]));
        });
    }

    // ---- Download fallback ------------------------------------------------

    function download(name, text, mime) {
        const blob = new Blob([text], { type: mime || 'application/octet-stream' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url; a.download = name;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        setTimeout(function () { URL.revokeObjectURL(url); }, 4000);
    }
    function downloadAll(files) {
        // Sequential clicks — browsers throttle simultaneous downloads.
        files.forEach(function (f, i) {
            setTimeout(function () { download(f.name, f.text, f.mime); }, i * 260);
        });
    }

    // ---- Page-to-page hand-off -------------------------------------------

    const LS_DESIGN = 'tt.workingDesign';
    const LS_DESIGN_NAME = 'tt.workingDesignName';

    function stashDesign(design) {
        try {
            localStorage.setItem(LS_DESIGN, JSON.stringify(design));
            localStorage.setItem(LS_DESIGN_NAME, String(design && design.name || ''));
            return true;
        } catch (e) { return false; }
    }
    function takeDesign() {
        try {
            const s = localStorage.getItem(LS_DESIGN);
            return s ? JSON.parse(s) : null;
        } catch (e) { return null; }
    }
    function stashedDesignName() {
        try { return localStorage.getItem(LS_DESIGN_NAME) || ''; } catch (e) { return ''; }
    }

    function saveLocal(key, value) {
        try { localStorage.setItem('tt.' + key, JSON.stringify(value)); return true; } catch (e) { return false; }
    }
    function loadLocal(key, fallback) {
        try {
            const s = localStorage.getItem('tt.' + key);
            return s ? JSON.parse(s) : fallback;
        } catch (e) { return fallback; }
    }

    // ---- File reading ------------------------------------------------------

    function readTextFile(file) {
        return new Promise(function (resolve, reject) {
            const r = new FileReader();
            r.onload = function () { resolve(String(r.result)); };
            r.onerror = function () { reject(r.error); };
            r.readAsText(file);
        });
    }

    /* Wire an element as a drag-and-drop target plus click-to-browse. */
    function dropZone(el, onFiles, opts) {
        const o = opts || {};
        const target = typeof el === 'string' ? document.getElementById(el) : el;
        if (!target) return;
        ['dragenter', 'dragover'].forEach(function (ev) {
            target.addEventListener(ev, function (e) {
                e.preventDefault(); e.stopPropagation();
                target.classList.add('dragging');
            });
        });
        ['dragleave', 'drop'].forEach(function (ev) {
            target.addEventListener(ev, function (e) {
                e.preventDefault(); e.stopPropagation();
                target.classList.remove('dragging');
            });
        });
        target.addEventListener('drop', function (e) {
            const files = Array.prototype.slice.call(e.dataTransfer.files);
            if (files.length) onFiles(files);
        });
        if (o.clickToBrowse !== false) {
            target.addEventListener('click', function () {
                const input = document.createElement('input');
                input.type = 'file';
                if (o.accept) input.accept = o.accept;
                if (o.multiple) input.multiple = true;
                input.addEventListener('change', function () {
                    if (input.files && input.files.length) onFiles(Array.prototype.slice.call(input.files));
                });
                input.click();
            });
        }
    }

    // ---- CSV --------------------------------------------------------------

    /* Parse a telemetry CSV into {columns:[names], rows:[[numbers]], byName}.
       Handles quoted fields and skips blank lines; non-numeric cells become
       NaN so a partially-textual column still plots what it can. */
    function parseCsv(text) {
        // The game writes a UTF-8 BOM; left in place it becomes part of the
        // first column's name and "time" stops matching.
        let src = String(text);
        if (src.charCodeAt(0) === 0xFEFF) src = src.slice(1);
        const lines = src.split(/\r?\n/).filter(function (l) { return l.trim().length; });
        if (!lines.length) return { columns: [], rows: [], byName: {} };

        function splitLine(line) {
            const out = [];
            let cur = '', inQ = false;
            for (let i = 0; i < line.length; i++) {
                const ch = line[i];
                if (inQ) {
                    if (ch === '"') {
                        if (line[i + 1] === '"') { cur += '"'; i++; } else inQ = false;
                    } else cur += ch;
                } else if (ch === '"') inQ = true;
                else if (ch === ',') { out.push(cur); cur = ''; }
                else cur += ch;
            }
            out.push(cur);
            return out.map(function (s) { return s.trim(); });
        }

        const columns = splitLine(lines[0]);
        const rows = [];
        for (let i = 1; i < lines.length; i++) {
            const cells = splitLine(lines[i]);
            const row = new Array(columns.length);
            for (let j = 0; j < columns.length; j++) {
                const v = cells[j];
                row[j] = (v === undefined || v === '') ? NaN : Number(v);
            }
            rows.push(row);
        }
        const byName = {};
        columns.forEach(function (name, j) {
            byName[name] = rows.map(function (r) { return r[j]; });
        });
        return { columns: columns, rows: rows, byName: byName };
    }

    function toCsv(columns, series) {
        const n = Math.max.apply(null, series.map(function (s) { return s.length; }));
        const lines = [columns.join(',')];
        for (let i = 0; i < n; i++) {
            lines.push(series.map(function (s) {
                const v = s[i];
                return (v === undefined || v === null || !isFinite(v)) ? '' : String(v);
            }).join(','));
        }
        return lines.join('\n') + '\n';
    }

    global.TT = global.TT || {};
    global.TT.Export = {
        supported: supported, Paths: Paths, pathHelp: pathHelp,
        pickDirectory: pickDirectory, savedDirectory: savedDirectory, requestPermission: requestPermission,
        writeFile: writeFile, writeFiles: writeFiles,
        download: download, downloadAll: downloadAll,
        stashDesign: stashDesign, takeDesign: takeDesign, stashedDesignName: stashedDesignName,
        saveLocal: saveLocal, loadLocal: loadLocal,
        readTextFile: readTextFile, dropZone: dropZone,
        parseCsv: parseCsv, toCsv: toCsv
    };
})(typeof window !== 'undefined' ? window : globalThis);
