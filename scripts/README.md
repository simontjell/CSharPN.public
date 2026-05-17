# CSharPN Build & Deployment Scripts

Denne folder indeholder scripts til at bygge og deploye CSharPN projektet.

## `build-and-serve-wasm.sh` (Linux/macOS)

Bygger og hoster Blazor WebAssembly-appen lokalt.

### Brug

```bash
./scripts/build-and-serve-wasm.sh [options]
```

### Optioner

- `--host <host>` – Server vært (default: `localhost`)
- `--port <port>` – Server port (default: `8080`)
- `--no-build` – Spring build over, kun host eksisterende build
- `--no-open` – Åbn ikke browser automatisk
- `--help` – Vis hjælp

### Eksempler

```bash
# Build og start server på localhost:8080
./scripts/build-and-serve-wasm.sh

# Start allerede bygget version på port 3000
./scripts/build-and-serve-wasm.sh --port 3000 --no-build

# Build men åbn ikke browser
./scripts/build-and-serve-wasm.sh --no-open

# Host på alle interfaces (f.eks. fra dev container)
./scripts/build-and-serve-wasm.sh --host 0.0.0.0
```

## `build-and-serve-wasm.bat` (Windows)

Samme funktionalitet som bash-versionen, men til Windows.

### Brug

```cmd
build-and-serve-wasm.bat [options]
```

### Eksempler

```cmd
REM Build og start server
build-and-serve-wasm.bat

REM Start uden build på port 3000
build-and-serve-wasm.bat --port 3000 --no-build
```

## Typisk workflow

```bash
# 1. Build og start development server
./scripts/build-and-serve-wasm.sh

# 2. (Eller hvis du bare vil hoste eksisterende build)
./scripts/build-and-serve-wasm.sh --no-build
```

## Hvorfor ikke en enkelt HTML-fil?

Blazor WebAssembly kan ikke pakkes i én `file://`-kompatibel HTML.
Årsagen er at Blazor's `dotnet.js` beregner URLs til sine peer-moduler
(`dotnet.runtime.js`, `dotnet.native.js`) dynamisk via `import.meta.url`
→ `locateFile()` → `new URL(filename, scriptDirectory)`.
Det resulterer altid i `file://`-URLs, som Chrome blokerer med CORS-fejl
når de importeres fra en `blob:null`-kontekst.
HTTP-serveren i scriptene omgår dette fuldstændigt.
