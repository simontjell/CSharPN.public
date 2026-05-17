#!/usr/bin/env python3
"""
pack-standalone.py
==================
Bundle a published Blazor WebAssembly output directory into a single,
self-contained HTML file that runs in any modern browser without a server.

Strategy
--------
1. Read index.html from the publish output.
2. Inline every <link rel="stylesheet"> as a <style> block.
3. Inline every <script src="..."> as a <script> block (catches blazor.webassembly.js).
4. Base64-encode every remaining file in wwwroot/ (framework DLLs, WASM binary,
   JSON manifests, JS modules …) into a virtual file-system (VFS) object.
5. Inject a <script type="importmap"> (via document.write) that maps
   _framework/*.js module specifiers to blob: URLs.  This is the key fix for
   running from file:// — dynamic import() bypasses window.fetch, but the
   browser's module loader DOES honour importmaps, so resolving to blob: URLs
   avoids the CORS restriction entirely.
6. Inject a fetch + XMLHttpRequest interceptor so all remaining resource
   requests (WASM binary, DLLs, JSON manifests) are served from the VFS.

Usage
-----
    python3 tools/pack-standalone.py <publish_wwwroot_dir> <output.html>

Example
-------
    python3 tools/pack-standalone.py publish/wasm/wwwroot dist/cpn-visualizer.html
"""

import sys
import re
import json
import base64
import mimetypes
from pathlib import Path


# ---------------------------------------------------------------------------
# MIME type helper
# ---------------------------------------------------------------------------

_MIME: dict[str, str] = {
    ".wasm":  "application/wasm",
    ".js":    "application/javascript",
    ".mjs":   "application/javascript",
    ".json":  "application/json",
    ".css":   "text/css",
    ".html":  "text/html",
    ".gz":    "application/octet-stream",
    ".br":    "application/octet-stream",
    ".dat":   "application/octet-stream",
    ".dll":   "application/octet-stream",
    ".pdb":   "application/octet-stream",
    ".png":   "image/png",
    ".svg":   "image/svg+xml",
    ".ico":   "image/x-icon",
    ".woff":  "font/woff",
    ".woff2": "font/woff2",
}

def mime_for(path: str) -> str:
    ext = Path(path).suffix.lower()
    return _MIME.get(ext, mimetypes.guess_type(path)[0] or "application/octet-stream")


# ---------------------------------------------------------------------------
# Build VFS
# ---------------------------------------------------------------------------

def build_vfs(wwwroot: Path) -> dict[str, str]:
    """
    Walk wwwroot/ and base64-encode every file except index.html.
    Keys are POSIX-style paths relative to wwwroot (e.g. "_framework/dotnet.js").
    """
    vfs: dict[str, str] = {}
    for p in sorted(wwwroot.rglob("*")):
        if p.is_file() and p.name != "index.html":
            key = p.relative_to(wwwroot).as_posix()
            raw = p.read_bytes()
            vfs[key] = base64.b64encode(raw).decode()
            kb = len(raw) / 1024
            print(f"  + {key}  ({kb:,.0f} KB)")
    return vfs


# ---------------------------------------------------------------------------
# HTML transformations
# ---------------------------------------------------------------------------

def inline_css(html: str, wwwroot: Path) -> str:
    """Replace <link rel="stylesheet" href="..."> with inline <style>."""
    def replace(m: re.Match) -> str:
        href = m.group(1) or m.group(2)
        css_path = wwwroot / href.lstrip("/")
        if css_path.exists():
            css = css_path.read_text(encoding="utf-8")
            return f'<style>/* inlined: {href} */\n{css}</style>'
        return m.group(0)

    return re.sub(
        r'<link[^>]+rel=["\']stylesheet["\'][^>]+href=["\']([^"\']*)["\'][^>]*/?>|'
        r'<link[^>]+href=["\']([^"\']*)["\'][^>]+rel=["\']stylesheet["\'][^>]*/?>',
        replace, html
    )


def inline_scripts(html: str, wwwroot: Path) -> str:
    """
    Replace <script src="..."></script> with an inline <script> block.
    Handles: blazor.webassembly.js and any other plain script tags.
    Skips tags with type="module" (handled via importmap + VFS).
    """
    def replace(m: re.Match) -> str:
        full_tag = m.group(0)
        src      = m.group(1)
        # Skip module scripts – they're loaded dynamically via import()
        if 'type=' in full_tag.lower() and 'module' in full_tag.lower():
            return full_tag
        script_path = wwwroot / src.lstrip("/")
        if script_path.exists():
            content = script_path.read_text(encoding="utf-8")
            return f'<script>/* inlined: {src} */\n{content}\n</script>'
        return full_tag

    return re.sub(
        r'<script[^>]+src=["\']([^"\']+)["\'][^>]*>\s*</script>',
        replace, html
    )


# ---------------------------------------------------------------------------
# Importmap injection  (fixes dynamic import() on file:// origin)
# ---------------------------------------------------------------------------

_IMPORTMAP_SCRIPT_TEMPLATE = """\
<script>
/* ── CSharPN standalone: blob-URL importmap for _framework JS modules ──────
 *
 * Problem 1 – dynamic import() blocked on file://
 * ------------------------------------------------
 * blazor.webassembly.js calls  import('./_framework/dotnet.js')
 * → resolves to  file:///…/_framework/dotnet.js
 * → blocked: "Cross origin requests only supported for http/https"
 * window.fetch overrides do NOT intercept import().
 * An importmap DOES — we map each specifier to a blob: URL.
 *
 * Problem 2 – import.meta.url is "blob:null/uuid" inside a blob module
 * ----------------------------------------------------------------------
 * When dotnet.js (Emscripten) runs as a blob module on a file:// page,
 * import.meta.url is "blob:null/<uuid>".  The runtime then calls:
 *   new URL('./blazor.boot.json', import.meta.url)
 *   → throws "Invalid URL" because blob:null is not a valid URL base.
 * Fix: replace import.meta.url in source text with a synthetic document-
 * relative URL (e.g. "file:///C:/Downloads/_framework/dotnet.js").
 *
 * Problem 3 – Chrome does NOT apply importmaps to imports from blob:null
 * -----------------------------------------------------------------------
 * dotnet.js calls  import('./dotnet.runtime.js')  and
 *                  import('./dotnet.native.js')
 * Even though those paths are in the importmap, Chrome silently bypasses
 * the document's importmap for ALL imports originating from a blob:null
 * module — whether the specifier is relative or absolute.
 *
 * The imports can appear in several syntactic forms that a single regex
 * cannot safely cover:
 *   import { … } from './dotnet.runtime.js'          ← static import
 *   import('./dotnet.runtime.js')                     ← dynamic, bare
 *   import(/* webpackIgnore: true *\/ './dotnet.runtime.js')  ← commented
 *   import(new URL('./dotnet.runtime.js', import.meta.url).href)
 *
 * Fix: two-phase blob approach with STRING-LITERAL replacement.
 *   Phase 1 – create a blob URL for every JS file so we know the URL map.
 *   Phase 2 – for every file, replace any quoted string literal whose
 *             value is a relative path to another known JS file  with  the
 *             corresponding blob URL.  e.g. "./dotnet.runtime.js" →
 *             "blob:null/<uuid>".  This works for all four forms above:
 *             • static / dynamic imports — the specifier IS replaced
 *             • new URL("./foo", …) — the first arg is replaced with an
 *               absolute blob: URL, so new URL() returns that blob: URL
 *               (absolute URLs ignore the base), and import(blob:…) works.
 *
 * The importmap is still useful for the one top-level import() that
 * blazor.webassembly.js makes from the file:// document context (Chrome
 * DOES honour importmaps there) and is injected via document.write() so
 * the browser sees it as static HTML before any import() runs.
 * ──────────────────────────────────────────────────────────────────────── */
(function () {
  'use strict';

  var JS_FILES = %s;   // { "path/key": "base64…", … }

  // Base directory of this HTML file, e.g. "file:///C:/Downloads/"
  var docDir = document.location.href.substring(
    0, document.location.href.lastIndexOf('/') + 1
  );

  function b64ToText(b64) {
    var bin = atob(b64);
    var arr = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
    return new TextDecoder().decode(arr);
  }

  // ── Phase 1: initial (unpatched) blob URLs ────────────────────────────
  var blobUrls = {};
  for (var key in JS_FILES) {
    blobUrls[key] = URL.createObjectURL(
      new Blob([b64ToText(JS_FILES[key])], { type: 'application/javascript' })
    );
  }

  // ── Phase 2: patch source text + recreate blob ────────────────────────
  //
  // We repeat the patch loop 4 times WITHOUT revoking old blobs.
  //
  // Why multiple passes?
  //   dotnet.js → dotnet.runtime.js → dotnet.native.js is a 3-level chain.
  //   JavaScript object key iteration has no guaranteed order.  If dotnet.js
  //   is processed BEFORE dotnet.runtime.js in a single pass, it embeds the
  //   phase-1 (unpatched) runtime blob URL.  The next pass re-reads the
  //   original source and embeds the now-updated runtime blob URL instead.
  //   After D passes (D = chain depth), all references have stabilised.
  //
  // Why NOT revoke?
  //   Each pass creates new blobs and updates blobUrls[].  A blob from pass N
  //   may reference a blob from pass N-1 (which is still being used by the
  //   blob from pass N-2, etc.).  Revoking any of these would break the
  //   in-flight reference chain.  We keep all blobs alive; the browser GCs
  //   them when the page unloads.
  for (var pass = 0; pass < 4; pass++) {
    for (var key in JS_FILES) {
      var text = b64ToText(JS_FILES[key]);

      // (a) Replace every quoted string literal whose value is a relative
      //     path to another known JS module → blob URL.  Covers:
      //       static  import { … } from './dotnet.runtime.js'
      //       dynamic import('./dotnet.runtime.js')
      //       dynamic import(/* webpackIgnore: true */ './dotnet.runtime.js')
      //       new URL('./dotnet.runtime.js', import.meta.url)
      //     Double, single, AND backtick quote styles are handled.
      for (var depKey in blobUrls) {
        var depFile = depKey.split('/').pop();
        var blobStr = JSON.stringify(blobUrls[depKey]);   // "blob:null/uuid"
        var rels    = ['./' + depFile, './' + depKey];
        for (var ri = 0; ri < rels.length; ri++) {
          var r = rels[ri];
          text = text.split('"'  + r + '"' ).join(blobStr);
          text = text.split("'"  + r + "'" ).join(blobStr);
          text = text.split('\`' + r + '\`').join(blobStr);
        }
      }

      // (b) Patch  import.meta.url  → synthetic document-relative file:// URL
      //     so Emscripten's  new URL('./blazor.boot.json', import.meta.url)
      //     produces a valid URL that the window.fetch interceptor can serve.
      text = text.replace(/\\bimport\\.meta\\.url\\b/g,
        JSON.stringify(docDir + key));

      // Create new blob (do NOT revoke old blobUrls[key]).
      blobUrls[key] = URL.createObjectURL(
        new Blob([text], { type: 'application/javascript' })
      );
    }
  }

  // ── Importmap ─────────────────────────────────────────────────────────
  // Covers the first  import('./_framework/dotnet.js')  from
  // blazor.webassembly.js (file:// context → importmap IS honoured).
  var imports = {};
  for (var k in blobUrls) {
    var u = blobUrls[k];
    imports['./' + k]              = u;
    imports['/'  + k]              = u;
    imports['./' + k.split('/').pop()] = u;
  }

  document.write(
    '<sc' + 'ript type="importmap">' +
    JSON.stringify({ imports: imports }) +
    '<\\/sc' + 'ript>'
  );
})();
</script>
"""


def inject_importmap(html: str, vfs: dict[str, str]) -> str:
    """
    Inject a script that creates blob URLs for all JS modules in the VFS and
    writes a <script type="importmap"> into the parse stream via document.write.
    This must be called BEFORE inject_interceptor (both inject before </head>,
    so calling importmap first means it ends up first in the document).
    """
    js_files = {
        k: v for k, v in vfs.items()
        if k.endswith('.js') or k.endswith('.mjs')
    }
    js_json = json.dumps(js_files, separators=(',', ':'))
    script  = _IMPORTMAP_SCRIPT_TEMPLATE % js_json
    return html.replace("</head>", script + "\n</head>", 1)


# ---------------------------------------------------------------------------
# Fetch / XHR interceptor  (handles WASM, DLLs, JSON, CSS …)
# ---------------------------------------------------------------------------

_INTERCEPTOR_TEMPLATE = """\
<script>
/* ── CSharPN standalone bundle: virtual-filesystem + fetch interceptor ── */
(function () {
  'use strict';

  // VFS: path → base64-encoded bytes
  var VFS   = %s;
  var MIMES = %s;

  // --- helpers -----------------------------------------------------------

  function b64ToBytes(b64) {
    var bin = atob(b64);
    var arr = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
    return arr;
  }

  // Strip origin + query string, return the bare path key (no leading slash).
  function urlToKey(url) {
    if (!url) return null;
    try { url = new URL(url, location.href).pathname; } catch (_) {}
    url = url.replace(/^\/+/, '').split('?')[0].split('#')[0];
    if (VFS[url]) return url;
    var m = url.match(/(_framework\\/.+)/);
    return (m && VFS[m[1]]) ? m[1] : null;
  }

  // --- override fetch (used by Blazor runtime for non-module resources) --

  var _origFetch = window.fetch.bind(window);
  window.fetch = function (input, init) {
    var url = (typeof input === 'string') ? input
            : (input && typeof input.url === 'string') ? input.url : '';
    var key = urlToKey(url);
    if (key) {
      var bytes = b64ToBytes(VFS[key]);
      return Promise.resolve(new Response(bytes, {
        status: 200,
        headers: { 'Content-Type': MIMES[key] || 'application/octet-stream' }
      }));
    }
    return _origFetch(input, init);
  };

  // --- override XMLHttpRequest (some Blazor versions use XHR for boot) --

  var _XOpen = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function (method, url) {
    this._vfsKey = urlToKey(url);
    _XOpen.apply(this, arguments);
  };

  var _XSend = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.send = function (body) {
    if (!this._vfsKey) { _XSend.apply(this, arguments); return; }
    var bytes = b64ToBytes(VFS[this._vfsKey]);
    var self  = this;
    setTimeout(function () {
      Object.defineProperty(self, 'readyState',   { get: function() { return 4; } });
      Object.defineProperty(self, 'status',       { get: function() { return 200; } });
      Object.defineProperty(self, 'response',     { get: function() { return bytes.buffer; } });
      Object.defineProperty(self, 'responseText', {
        get: function() { return new TextDecoder().decode(bytes); }
      });
      self.onreadystatechange && self.onreadystatechange();
      self.onload && self.onload();
    }, 0);
  };

})();
</script>
"""


def inject_interceptor(html: str, vfs: dict[str, str]) -> str:
    """Inject the VFS + fetch/XHR interceptor just before </head>."""
    mime_map  = {k: mime_for(k) for k in vfs}
    vfs_json  = json.dumps(vfs,      separators=(',', ':'))
    mime_json = json.dumps(mime_map, separators=(',', ':'))
    script    = _INTERCEPTOR_TEMPLATE % (vfs_json, mime_json)
    return html.replace("</head>", script + "\n</head>", 1)


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main() -> None:
    if len(sys.argv) != 3:
        print(f"Usage: {sys.argv[0]} <publish_wwwroot_dir> <output.html>",
              file=sys.stderr)
        sys.exit(1)

    wwwroot  = Path(sys.argv[1]).resolve()
    out_path = Path(sys.argv[2])

    if not wwwroot.is_dir():
        print(f"ERROR: {wwwroot} is not a directory", file=sys.stderr)
        sys.exit(1)

    framework = wwwroot / "_framework"
    if not framework.exists():
        print(f"ERROR: No _framework/ folder found in {wwwroot}.\n"
              f"Make sure you ran 'dotnet publish' first.", file=sys.stderr)
        sys.exit(1)

    out_path.parent.mkdir(parents=True, exist_ok=True)

    print(f"\n=== CSharPN standalone packer ===")
    print(f"Source : {wwwroot}")
    print(f"Output : {out_path}\n")

    # 1. Build VFS from everything except index.html
    vfs = build_vfs(wwwroot)

    # 2. Read and transform index.html
    html = (wwwroot / "index.html").read_text(encoding="utf-8")
    html = inline_css(html, wwwroot)
    html = inline_scripts(html, wwwroot)

    # 3. Inject importmap FIRST (must precede blazor.webassembly.js inline block)
    #    This creates blob: URLs for _framework/*.js and injects an importmap via
    #    document.write, making dynamic import() work from file:// origin.
    html = inject_importmap(html, vfs)

    # 4. Inject fetch/XHR interceptor (handles WASM, DLLs, JSON, CSS …)
    html = inject_interceptor(html, vfs)

    # 5. Write output
    out_path.write_text(html, encoding="utf-8")

    size_mb = out_path.stat().st_size / 1024 / 1024
    print(f"\n✓  Done!  {out_path.name}  ({size_mb:.1f} MB)")


if __name__ == "__main__":
    main()
