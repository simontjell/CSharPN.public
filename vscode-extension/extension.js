'use strict';

const vscode = require('vscode');
const { spawn } = require('child_process');
const path    = require('path');
const net     = require('net');
const fs      = require('fs');

// ── Utilities ─────────────────────────────────────────────────────────────────

function findFreePort() {
    return new Promise((resolve, reject) => {
        const srv = net.createServer();
        srv.listen(0, '127.0.0.1', () => {
            const { port } = srv.address();
            srv.close(() => resolve(port));
        });
        srv.on('error', reject);
    });
}

/** Walk up from `startDir` until we find a directory containing scripts/serve.sh. */
function findProjectRoot(startDir) {
    let dir = startDir;
    while (true) {
        if (fs.existsSync(path.join(dir, 'scripts', 'serve.sh')))
            return dir;
        const parent = path.dirname(dir);
        if (parent === dir) return null;   // reached filesystem root
        dir = parent;
    }
}

/** Poll TCP until the server accepts connections or timeout. */
function waitForServer(port, timeoutMs = 60_000) {
    return new Promise((resolve, reject) => {
        const deadline = Date.now() + timeoutMs;
        function attempt() {
            const sock = net.createConnection(port, '127.0.0.1');
            sock.on('connect', () => { sock.destroy(); resolve(); });
            sock.on('error', () => {
                if (Date.now() >= deadline)
                    reject(new Error(`Server on port ${port} did not respond within ${timeoutMs / 1000}s`));
                else
                    setTimeout(attempt, 400);
            });
        }
        attempt();
    });
}

// ── State (one server at a time) ──────────────────────────────────────────────

/** @type {import('child_process').ChildProcess | null} */
let _proc = null;
/** @type {NodeJS.Timeout | null} */
let _pollTimer = null;

function killServer() {
    if (_pollTimer) { clearInterval(_pollTimer); _pollTimer = null; }
    if (_proc) { _proc.kill('SIGTERM'); _proc = null; }
}

/** Poll /api/navigate and open the file in VS Code when a request arrives. */
function startNavigatePoll(port) {
    const http = require('http');
    _pollTimer = setInterval(() => {
        http.get(`http://127.0.0.1:${port}/api/navigate`, res => {
            if (res.statusCode !== 200) { res.resume(); return; }
            let body = '';
            res.on('data', c => body += c);
            res.on('end', () => {
                // body = "/path/to/file.cs:42"
                const match = body.match(/^(.+):(\d+)$/);
                if (!match) return;
                const uri = vscode.Uri.file(match[1]);
                const line = parseInt(match[2], 10) - 1; // 0-based
                vscode.window.showTextDocument(uri, {
                    viewColumn: vscode.ViewColumn.One,
                    selection: new vscode.Range(line, 0, line, 0),
                    preserveFocus: false,
                });
            });
        }).on('error', () => {}); // ignore connection errors
    }, 500);
}

// ── Activation ────────────────────────────────────────────────────────────────

function activate(context) {
    const channel = vscode.window.createOutputChannel('CPN Preview');

    const cmd = vscode.commands.registerCommand('cpn.openPreview', async () => {

        // ── Resolve current file ──────────────────────────────────────────────
        const editor = vscode.window.activeTextEditor;
        if (!editor || !editor.document.fileName.endsWith('.cs')) {
            vscode.window.showWarningMessage(
                'CPN Preview: open a .cs CPN model file first.');
            return;
        }
        const filePath = editor.document.fileName;
        // The server loads the model class whose declaration contains the cursor,
        // so a file with several models opens the one being edited.
        const cursorLine = editor.selection.active.line + 1;

        // Walk up from the file to find the project root (contains scripts/serve.sh)
        const projectRoot = findProjectRoot(path.dirname(filePath));
        if (!projectRoot) {
            vscode.window.showErrorMessage(
                'CPN Preview: could not find scripts/serve.sh. ' +
                'Make sure the file is inside the CSharPN project.');
            return;
        }

        const serveScript = path.join(projectRoot, 'scripts', 'serve.sh');

        // ── Kill previous server ──────────────────────────────────────────────
        killServer();

        // ── Start server ──────────────────────────────────────────────────────
        const port = await findFreePort();

        await vscode.window.withProgress({
            location: vscode.ProgressLocation.Notification,
            title: `CPN: building and starting server for ${path.basename(filePath)} …`,
            cancellable: false,
        }, async (progress) => {

            _proc = spawn('bash', [serveScript, '--port', String(port), '--line', String(cursorLine), filePath], {
                cwd: projectRoot,
                stdio: ['ignore', 'pipe', 'pipe'],
            });

            _proc.stdout?.on('data', d => channel.append(d.toString()));
            _proc.stderr?.on('data', d => channel.append(d.toString()));
            _proc.on('error', err => {
                channel.appendLine(`[error] ${err.message}`);
                vscode.window.showErrorMessage(
                    `CPN Preview: could not start server: ${err.message}`);
            });

            progress.report({ message: 'waiting for server …' });
            await waitForServer(port);
        });

        const url = `http://localhost:${port}`;
        channel.appendLine(`[CPN] ready at ${url}`);

        // Start polling for source-navigation requests from the visualiser
        startNavigatePoll(port);

        // ── Open in Simple Browser to the right of the current editor ────────
        try {
            // Focus a group to the right (creates one if needed)
            await vscode.commands.executeCommand('workbench.action.focusRightGroup');
            // If no right group existed, the above is a no-op — split explicitly
            const groups = vscode.window.tabGroups.all;
            if (groups.length < 2) {
                await vscode.commands.executeCommand('workbench.action.splitEditorRight');
            }
            // Now open Simple Browser in the (now-active) right group
            await vscode.commands.executeCommand('simpleBrowser.show', url);
            // Return focus to the left editor group
            await vscode.commands.executeCommand('workbench.action.focusFirstEditorGroup');
        } catch {
            channel.appendLine('[CPN] Simple Browser not available — opening in external browser');
            vscode.env.openExternal(vscode.Uri.parse(url));
        }
    });

    context.subscriptions.push(
        cmd,
        channel,
        { dispose: killServer },
    );
}

function deactivate() {
    killServer();
}

module.exports = { activate, deactivate };
