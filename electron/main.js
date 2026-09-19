// =====================================================
//  electron/main.js — Statiq Markdown Editor host
//
//  The ASP.NET Core backend is built as a self-contained,
//  single-file executable (StatiqMarkdownEditor). We spawn it
//  on a free localhost port, wait for the port to respond, then
//  open a BrowserWindow pointing at it. On quit we kill the
//  child process so the user doesn't end up with a stranded
//  server in Activity Monitor.
//
//  Layout (after electron-builder packages the .app):
//    Statiq Markdown Editor.app/
//      Contents/
//        MacOS/Statiq Markdown Editor   (Electron host)
//        Resources/
//          server/StatiqMarkdownEditor  (.NET self-contained exe)
//          app/                        (webroot, if we choose to bundle)
//
//  In dev (npm start) we run the published executable from
//  ./dist/server/StatiqMarkdownEditor (produced by
//  `dotnet publish -c Release -r osx-arm64 --self-contained
//  -p:PublishSingleFile=true`).
// =====================================================
const { app, BrowserWindow, dialog, shell } = require('electron');
const { spawn } = require('child_process');
const path = require('path');
const fs = require('fs');
const net = require('net');

// ---------- Config ----------
// Try a couple of ports in case one is already in use (e.g. the user
// has a `dotnet run` going on 5070 for development).
const PREFERRED_PORTS = [5070, 5173, 53210, 0];  // 0 = OS picks
const READY_TIMEOUT_MS = 20000;                   // how long to wait for the server to respond
const READY_POLL_MS = 150;                        // polling interval
const SHUTDOWN_GRACE_MS = 2500;                   // give the server a moment to flush on quit

// ---------- Locate the .NET executable ----------
function findServerExe() {
    // In a packaged .app, the exe sits next to this script's
    // app.asar, under Resources/server/
    const packaged = path.join(process.resourcesPath, 'server', 'StatiqMarkdownEditor');
    if (fs.existsSync(packaged)) return packaged;

    // In dev / npm start, look for ./dist/server/StatiqMarkdownEditor
    const devA = path.join(__dirname, '..', 'dist', 'server', 'StatiqMarkdownEditor');
    if (fs.existsSync(devA)) return devA;
    const devB = path.join(__dirname, '..', 'dist', 'server', 'StatiqMarkdownEditor.dll');
    if (fs.existsSync(devB)) {
        // Not self-contained — fall back to `dotnet <dll>` via PATH
        return { exe: 'dotnet', args: [devB] };
    }
    return null;
}

// ---------- Pick a free port ----------
function pickFreePort() {
    return new Promise((resolve, reject) => {
        const tryPort = (idx) => {
            if (idx >= PREFERRED_PORTS.length) {
                return reject(new Error('No preferred ports free'));
            }
            const port = PREFERRED_PORTS[idx];
            const tester = net.createServer()
                .once('error', () => tryPort(idx + 1))
                .once('listening', () => {
                    tester.close(() => resolve(port === 0 ? tester.address().port : port));
                });
            tester.listen(port, '127.0.0.1');
        };
        tryPort(0);
    });
}

// ---------- Wait for the server to start responding ----------
function waitForReady(port, timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    return new Promise((resolve, reject) => {
        const tryOnce = () => {
            if (Date.now() > deadline) {
                return reject(new Error(`Server didn't start within ${timeoutMs}ms on :${port}`));
            }
            const socket = new net.Socket();
            let settled = false;
            const finish = (err) => {
                if (settled) return;
                settled = true;
                socket.destroy();
                if (err) setTimeout(tryOnce, READY_POLL_MS);
                else resolve();
            };
            socket.setTimeout(READY_POLL_MS);
            socket.once('connect', () => finish(null));
            socket.once('timeout', () => finish(new Error('timeout')));
            socket.once('error', () => finish(new Error('error')));
            socket.connect(port, '127.0.0.1');
        };
        tryOnce();
    });
}

// ---------- State ----------
let serverProcess = null;
let serverPort = null;

// ---------- Boot the .NET server ----------
async function startServer() {
    const exeSpec = findServerExe();
    if (!exeSpec) {
        throw new Error(
            'Could not find the Statiq Markdown Editor server executable. ' +
            'Run `dotnet publish -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o ./dist/server` first.'
        );
    }
    const port = await pickFreePort();
    serverPort = port;

    let exe, args;
    if (typeof exeSpec === 'string') {
        // Self-contained: invoke directly
        exe = exeSpec;
        args = [`--urls=http://127.0.0.1:${port}`];
    } else {
        // Framework-dependent fallback (dev): `dotnet <dll>`
        exe = exeSpec.exe;
        args = [...exeSpec.args, `--urls=http://127.0.0.1:${port}`];
    }

    console.log(`[host] launching ${path.basename(exe)} on http://127.0.0.1:${port}`);
    serverProcess = spawn(exe, args, {
        cwd: path.dirname(exe),
        stdio: ['ignore', 'pipe', 'pipe'],
        // Detach so the .NET process doesn't die when we kill it via signal
        detached: false,
    });
    serverProcess.stdout?.on('data', d => process.stdout.write(`[server] ${d}`));
    serverProcess.stderr?.on('data', d => process.stderr.write(`[server] ${d}`));
    serverProcess.on('exit', (code, sig) => {
        console.log(`[host] server exited (code=${code}, sig=${sig})`);
        serverProcess = null;
    });

    await waitForReady(port, READY_TIMEOUT_MS);
    return port;
}

// ---------- Stop the .NET server ----------
function stopServer() {
    if (!serverProcess) return Promise.resolve();
    const proc = serverProcess;
    serverProcess = null;
    return new Promise((resolve) => {
        let done = false;
        const finish = () => { if (!done) { done = true; resolve(); } };
        proc.once('exit', finish);
        // SIGTERM first, then SIGKILL after grace period
        try { proc.kill('SIGTERM'); } catch (_) {}
        setTimeout(() => {
            if (!done) {
                try { proc.kill('SIGKILL'); } catch (_) {}
                setTimeout(finish, 500);
            }
        }, SHUTDOWN_GRACE_MS);
    });
}

// ---------- Open the editor window ----------
async function createWindow(port) {
    const win = new BrowserWindow({
        width: 1400,
        height: 900,
        minWidth: 960,
        minHeight: 600,
        title: 'Statiq Markdown Editor',
        backgroundColor: '#ffffff',
        // Don't show a menu bar (cleaner look) — the app is a tool, not a
        // document editor. User can re-enable with `Menu.setApplicationMenu`
        // if they want native menus later.
        autoHideMenuBar: true,
        webPreferences: {
            // We don't need a node integration in the renderer — every
            // API call goes through our own backend. Keep the renderer
            // sandboxed.
            contextIsolation: true,
            nodeIntegration: false,
            sandbox: true,
        },
    });
    win.loadURL(`http://127.0.0.1:${port}/`);
    // Forward renderer console messages to the host process stdout + a
    // /tmp log file. Makes packaged-mode debugging possible without
    // opening DevTools manually (the host process is detached from any
    // terminal, so a file is the reliable sink).
    const diagLog = (line) => {
        try {
            require('fs').appendFileSync('/tmp/statiq-editor-diag.log', line + '\n');
        } catch (_) { /* /tmp not writable, swallow */ }
        console.log(line);
    };
    win.webContents.on('console-message', (event) => {
        const lvl = event.level || 'log';
        const loc = `${event.sourceId || '?'}:${event.lineNumber || '?'}`;
        diagLog(`[renderer:${lvl}] ${event.message}  (${loc})`);
    });
    win.webContents.on('render-process-gone', (event, details) => {
        diagLog(`[renderer:gone] reason=${details.reason} exitCode=${details.exitCode}`);
    });
    win.webContents.on('did-fail-load', (event, errorCode, errorDescription, validatedURL) => {
        diagLog(`[renderer:load-failed] ${validatedURL}: ${errorDescription} (${errorCode})`);
    });
    // DevTools is closed by default for a clean production UX. Press F12
    // (or Cmd+Opt+I) to toggle it open when debugging. Console output is
    // always forwarded to /tmp/statiq-editor-diag.log via the
    // console-message handler above so failures leave a paper trail
    // whether or not DevTools is open.
    // Allow F12 / Cmd+Opt+I to toggle DevTools.
    win.webContents.on('before-input-event', (event, input) => {
        if (input.key === 'F12' ||
            (input.key === 'I' && input.meta && input.alt)) {
            win.webContents.toggleDevTools();
        }
    });
    win.on('closed', () => { /* nothing else to do; main process stays alive */ });
    return win;
}

// ---------- App lifecycle ----------
app.whenReady().then(async () => {
    try {
        const port = await startServer();
        await createWindow(port);
    } catch (err) {
        console.error('[host] failed to start:', err);
        dialog.showErrorBox('Statiq Markdown Editor', `Failed to start:\n\n${err.message}`);
        app.quit();
    }
});

// On macOS the convention is for the app to stay alive after the
// window is closed (cmd+Q quits). We quit when ALL windows are
// closed because we always have exactly one window — there's no
// point keeping the process around without it.
app.on('window-all-closed', () => {
    app.quit();
});

app.on('before-quit', async (event) => {
    if (!serverProcess) return;
    event.preventDefault();
    await stopServer();
    app.exit(0);
});

// Don't allow a second instance (single-window editor) — focus the
// existing window instead.
const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
    app.quit();
} else {
    app.on('second-instance', () => {
        const wins = BrowserWindow.getAllWindows();
        if (wins.length > 0) {
            const w = wins[0];
            if (w.isMinimized()) w.restore();
            w.focus();
        }
    });
}
