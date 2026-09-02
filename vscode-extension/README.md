# CPN Preview — VS Code Extension

Live CPN model preview inside VS Code. Opens the CSharPN Visualizer in a side panel next to your model source code, with hot-reload on save.

## Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/) (10.0+)
- [Node.js](https://nodejs.org/) (for building the VSIX)

## Install

Every [release of CSharPN](https://github.com/simontjell/CSharPN.public/releases) carries
`cpn-preview-<version>.vsix` as an asset, built with the same version number as the
`CSharPN.Core` package. Download it and install:

```bash
code --install-extension cpn-preview-<version>.vsix --force
```

Or in VS Code: **Extensions** > `...` > **Install from VSIX...** > select the file.

Reload VS Code after installation.

## Build locally

```bash
cd vscode-extension
bash build.sh
```

This produces a `.vsix` from the version in `package.json` (a placeholder; the release
workflow stamps the real version).

## Usage

1. Open a `.cs` file containing a CPN model (must be inside the CSharPN project tree) and
   put the cursor inside the model class you want to see. A file may contain several
   models — a test file, say; the one whose class contains the cursor is shown, or the
   first model in the file when the cursor is outside any.
2. Press **Ctrl+Shift+V** (or **Cmd+Shift+V** on Mac), or click the preview icon in the editor title bar.
3. The extension:
   - Finds a free port automatically
   - Starts `scripts/serve.sh` with the model file and the cursor line
   - Opens the visualiser in a panel to the right of the editor
4. Edit the model and save — the visualiser hot-reloads automatically.

When the file belongs to a project (a `.csproj` above it), the server builds that project
with `dotnet build` and loads the model from the output, so NuGet packages, project
references and `InternalsVisibleTo` resolve exactly as in the IDE. Every `.cs` file of
the project is watched. A loose file without a project is compiled on its own.

## How it works

- The extension walks up from the open file to find `scripts/serve.sh` (the CSharPN project root).
- It spawns `serve.sh --port <free-port> --line <cursor-line> <model.cs>` which starts the Blazor Server with hot-reload.
- The visualiser opens in VS Code's built-in Simple Browser, positioned to the right of the active editor.
- The server process is killed when the extension deactivates or a new preview is opened.
