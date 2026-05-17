# CPN Preview — VS Code Extension

Live CPN model preview inside VS Code. Opens the CSharPN Visualizer in a side panel next to your model source code, with hot-reload on save.

## Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/) (10.0+)
- [Node.js](https://nodejs.org/) (for building the VSIX)

## Build

```bash
cd vscode-extension
bash build.sh
```

This produces `cpn-preview-0.1.0.vsix`.

## Install

```bash
code --install-extension cpn-preview-0.1.0.vsix --force
```

Or in VS Code: **Extensions** > `...` > **Install from VSIX...** > select the file.

Reload VS Code after installation.

## Usage

1. Open a `.cs` file containing a CPN model (must be inside the CSharPN project tree).
2. Press **Ctrl+Shift+V** (or **Cmd+Shift+V** on Mac), or click the preview icon in the editor title bar.
3. The extension:
   - Finds a free port automatically
   - Starts `scripts/serve.sh` with the model file
   - Opens the visualiser in a panel to the right of the editor
4. Edit the model and save — the visualiser hot-reloads automatically.

## How it works

- The extension walks up from the open file to find `scripts/serve.sh` (the CSharPN project root).
- It spawns `serve.sh --port <free-port> <model.cs>` which starts the Blazor Server with hot-reload.
- The visualiser opens in VS Code's built-in Simple Browser, positioned to the right of the active editor.
- The server process is killed when the extension deactivates or a new preview is opened.
