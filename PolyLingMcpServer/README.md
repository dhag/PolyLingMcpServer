# PolyLingMcpServer

Console MCP server for working on a Unity project from an MCP client: reading and
editing source files, driving the Unity Editor, and issuing PolyLing commands.

The executable is headless. By default Claude Desktop launches it as a child process and
talks to it over stdin/stdout, so there is no window and no listening port.
stdout is the JSON-RPC channel; all diagnostics and the activity log go to stderr.

With `--http <port>` it instead listens on `127.0.0.1:<port>` for Streamable HTTP and
serves a browser client on the same port (see [HTTP mode](#http-mode)).

## Requirements

- Windows
- .NET 8 SDK
- Visual Studio 2022 or newer recommended
- NuGet: `ModelContextProtocol.AspNetCore` 2.2.0

## Usage

```text
PolyLingMcpServer.exe --project <unity-project-folder> [--editable <folder>] ... [--root <name>=<folder>] ... [--unity-log <Editor.log path>] [--http <port>]
```

- `--project` is required. It is the unnamed root, and project-relative paths are measured from it.
- `--root` adds a readable folder **outside** the project, addressed as `@<name>/path`. May be
  repeated. Names are case-insensitive and may not contain `@`, `/` or `\`. A malformed
  value, a duplicate name, a relative path or a missing folder stops startup.
- `--editable` may be repeated, or given once as a `;`-separated list. Defaults to `Assets`.
  Each item is either a project-relative folder or `@<name>[/sub]`. An item naming an
  unknown root stops startup.
- `--unity-log` defaults to `%LOCALAPPDATA%\Unity\Editor\Editor.log`. Set it only when
  Unity is started with `-logFile`.
- `--http` switches from stdio to Streamable HTTP on `127.0.0.1:<port>`. A value that is not
  a port number 1-65535 stops startup.
- `--guide <folder>` is the AI guide data (`primer.md`, `hints.txt`). Default
  `<project>/Packages/com.haglib.polyling/McpGuide`. `primer.md` is appended to the first tool
  result of each session (and to the initialize instructions); each rule in `hints.txt`
  (tab-separated: id, tool regex, argument regex, doc that suppresses it once read, message) is
  appended once per session when a call matches. Tool results are used because some clients
  (Claude via remote-devices, 2026-09-30) do not pass instructions to the model. The files are
  re-read when they change, so editing them needs no rebuild or restart. See `GuideRules.cs`.
- `--target editor|player` chooses what the server talks to. `editor` (default) is the Unity
  Editor on port 8770. `player` is a built PolyLing player on port 8772
  (`PolyLingPlayerControlServer`). With `player`, `--project` may be omitted, and the tools a
  build cannot serve are hidden: source read/write/search, `read_binary` / `write_binary` /
  `delete_path`, `compile` / `get_compile_errors`, `unity_play` / `unity_stop` /
  `unity_refresh` / `unity_recompile`, `unity_validate_changes` and `read_unity_log`.
  `unity_prefab_export` / `unity_prefab_instantiate` / `unity_prefab_import` /
  `unity_prefab_add_component` stay visible and
  always go to the Editor on 8770, so files made in a build can still become prefabs while the
  Editor is open. `sendHierarchyBundle` (through `polyling_call`) also works from a build.
- `--capture-folder` is where `unity_capture` saves, and the folder filled in when `uiCapture`
  is called through `polyling_call` / `pl_uiCapture` without one. Project-relative,
  `@<name>[/sub]` or absolute; default `SandBox/Captures`. It is created at startup and must lie
  inside a readable root. With `--target player` and no `--project` it must be absolute, and it
  becomes the only folder `read_image` can read.
- `--player-exe` is the process name (without `.exe`) that `unity_focus` brings to the front
  with `--target player`.

At startup the target, capture folder, resolved roots, editable folders and log path are written to stderr.

### claude_desktop_config.json

```json
{
  "mcpServers": {
    "unity": {
      "command": "C:\\path\\to\\PolyLingMcpServer.exe",
      "args": [
        "--project", "C:\\path\\to\\UnityProject",
        "--editable", "Packages/com.example.foo;Assets",
        "--root", "settings=C:\\Users\\me\\AppData\\LocalLow\\Vendor"
      ]
    }
  }
}
```

**The client reads the tool list once, when it connects.** After adding or renaming a
tool, rebuild *and* restart the MCP client. A rebuilt executable alone changes nothing
for a session that is already running, and the copy step of the build fails anyway while
the old process holds the file.

## HTTP mode

```text
PolyLingMcpServer.exe --project <unity-project-folder> --editable <folder> --http 8771
```

Then open `http://127.0.0.1:8771/` in a browser on the same PC. The server and the browser
must be on one machine: it binds `127.0.0.1` only.

| Path | What it is |
|---|---|
| `/` | The browser client, `WebClient/index.html`, embedded in the executable |
| `/mcp` | The MCP endpoint (Streamable HTTP, protocol `2026-07-28`, stateless) |
| `/openai/v1/responses` (POST), `/openai/v1/models` (GET) | Relay to `https://api.openai.com/v1/…` for the browser client |

- **Same origin, no CORS.** The page and `/mcp` share one origin, so nothing is configured
  for cross-origin access.
- **Origin check.** A request whose `Origin` header is anything other than
  `http://127.0.0.1:<port>` or `http://localhost:<port>` gets `403`. The MCP specification
  requires this against DNS rebinding, and `ModelContextProtocol.AspNetCore` 2.2.0 does not
  do it, so `Program.RunHttp` does. A request with no `Origin` (curl) is let through.
- **Stateless.** The SDK default. No tool keeps per-session state, and
  `EditorControlClient` opens one connection per call.
- **The Claude API is called from the browser**, not from this server. The page sends the
  API key typed into it straight to `api.anthropic.com` with
  `anthropic-dangerous-direct-browser-access: true`; without that header the API returns no
  CORS headers and the browser cannot read the reply. The key is held only in the page and
  is gone on reload.
- **The OpenAI API goes through this server.** Choosing `OpenAI` in the page's API selector
  switches to the Responses API. Whether `api.openai.com` can be called straight from a
  browser (CORS) could not be confirmed, so the page posts to `/openai/v1/responses` on this
  server, which forwards the request with the browser's `Authorization` header and streams
  the answer back. Only those two paths are relayed; the key is not stored. Requests are sent
  with `store: false`, and the returned output items (reasoning with its encrypted content,
  messages, function calls) are sent back as the next input. Switching the API clears the
  conversation, since the two history formats do not convert without loss. The cache
  checkbox applies to Anthropic only; the CSV gains a `provider` column. For OpenAI,
  `cacheReadTokens` is `input_tokens_details.cached_tokens` and is subtracted from
  `inputTokens`; `cacheWriteTokens` is `input_tokens_details.cache_write_tokens` and is **not**
  subtracted, because whether `input_tokens` includes it was not confirmed.
- **Tool calls run without confirmation**, including `write_file`, `apply_patch`,
  `write_binary` and `delete_path`. The editable folders are the limit, exactly as in stdio
  mode. One send stops after 30 model/tool round trips (`MAX_TOOL_ROUNDS` in the page).
- `WebClient/index.html` is an `EmbeddedResource`; after editing it, rebuild. Its resource
  name is `Program.WebClientResourceName`, and its `MCP_ENDPOINT` must match
  `Program.McpEndpointPath`.

## Tools

25 tools. All of them are registered by `WithToolsFromAssembly`, so a new
`[McpServerToolType]` class is picked up without touching `Program.cs`.

### Paths

Every tool takes the same kind of path. No prefix means project-relative, exactly as
before. `@<name>/…` means relative to a `--root` folder; `@<name>` on its own is that
folder. Absolute paths are still refused, so adding a root never turns into "reads
anywhere". Tool output uses the same spelling, so a path from `list_files` or
`search_text` can be handed straight back to `read_file`.

### Files

| Tool | What it does |
|---|---|
| `list_files` | Lists files under the project. `search_pattern` defaults to `*.cs`. |
| `search_text` | grep-style search. Literal or .NET regex, with context lines. |
| `read_file` | Reads a text file. `encoding` accepts .NET names such as `shift_jis`. |
| `read_lines` | Reads one slice of a text file by line number, and reports the total line count and EOL style. |
| `read_binary` | Reads bytes and returns them as base64 **text**. Not for images; see below. |
| `read_image` | Reads an image file and returns it as image content. |
| `write_file` | Creates or replaces a text file. |
| `apply_patch` | Exact-text replacement inside an existing file. |
| `write_binary` | Writes base64 bytes. Large files go in pieces with `append=true`. |
| `delete_path` | Moves a file or folder to `<root>/_McpTrash/<timestamp>/`. Nothing is unlinked. |

`Library`, `Temp`, `obj`, `bin`, `.vs` and `Logs` are never searched by `search_text`.

Code-page encodings (`shift_jis` and the rest) work because `Program.Main` registers
`CodePagesEncodingProvider`. .NET 8 does not carry them by default.

### Unity Editor

| Tool | What it does |
|---|---|
| `unity_ping` | Is the Editor running and answering? |
| `unity_state` | Play mode, paused, compiling, importing. |
| `unity_play` / `unity_stop` | Enter or leave Play mode. **The reply comes back before the transition**, so confirm with `unity_state`. |
| `unity_refresh` | Refreshes the Asset Database and compiles. Fails in Play mode. |
| `unity_recompile` | Clears the build cache and recompiles everything. Slower. |
| `unity_focus` | Brings the Editor window to the front. |
| `unity_capture` | Photographs the PolyLing screen and returns the picture. Focus, shot, wait and read in one call. |
| `read_unity_log` | Reads `Editor.log`, which is where Unity's own compile errors and exceptions are. |

`unity_focus` runs entirely inside this process and never talks to the Editor, so it
also works while the Editor is compiling or importing. It has to be here rather than in
the Editor: Win32 grants the right to change the foreground window only to the process
that already holds it, so a background Editor calling `SetForegroundWindow` on itself
only makes the taskbar button flash. `UnityWindowTools` attaches to the foreground
thread's input queue for the duration of one call instead. The comments in that file
record which alternatives were measured and rejected.

### Compile

| Tool | What it does |
|---|---|
| `compile` | `dotnet build <csproj> --nologo --verbosity minimal`. |
| `get_compile_errors` | Errors from the most recent `compile`. |

`compile` is an external check. **It does not make a running Unity Editor compile.**
`compile` passing does not mean Unity will accept the code, and the reverse is also
true — look at both. With no `project_file`, `Assembly-CSharp.csproj` is preferred.

`dotnet` output is decoded as UTF-8 explicitly; the console code page (cp932 on Japanese
Windows) would turn every non-ASCII path in the build log into mojibake.

### PolyLing

| Tool | What it does |
|---|---|
| `polyling_tools` | Command list as a JSON Schema document. Requires the PolyLing panel to be open. |
| `polyling_call` | Runs one command. |
| `ping` | Is this server alive? Touches nothing else. |

`polyling_call` arguments are a flat object of **strings only**: arrays go in as one
comma-separated string (`"0,1,2"`), nesting uses dotted keys (`"params.widthTop"`).

## Images

### `read_image`, not `read_binary`

`read_binary` returns base64 as a **string**. A string cannot be viewed as an image, and
base64 costs roughly one token per three characters — a 500KB image burns close to
200,000 tokens while showing nothing. Use `read_binary` when the bytes themselves are
the point; use `read_image` when the picture is.

### Limits

| Limit | Value | Where |
|---|---|---|
| Bytes per tool result | 600,000 (before base64) | `McpContent.MaxImageBytes` |
| Long edge worth sending | 1,568 px | `McpContent.RecommendedLongEdge` |

The byte limit is a refusal: the file is not read at all, and the error says so. Claude
Desktop rejects responses over roughly 1MB, and base64 turns 3 bytes into 4 characters,
so 600,000 bytes leaves room for the JSON-RPC envelope and the explanatory text. With
several images it applies to the total (`McpContent.FitsBudget`).

The pixel limit is only a note appended to the text. Anything longer is scaled down on
the receiving side, which spends bytes without adding detail.

### Formats

PNG, JPEG, GIF and WebP — what Claude can decode. The MIME type is decided from the
leading bytes, never from the extension, because a wrong `mimeType` makes decoding fail
on the client and because captured bytes have no filename to inspect. BMP, TIFF and ICO
are rejected **by name**, so that GDI+'s default output does not become a mysteriously
unreadable image. Width and height are read from the header only; nothing is decoded.

### `image_selftest`

Returns a 150-byte built-in PNG: 64x64, red top-left, green top-right, blue bottom-left,
white bottom-right. It touches no file, no Unity, no Win32. If the model can name the
four quadrants, pixels really are arriving; a string passed through verbatim cannot
produce that answer. Run it first whenever an image path misbehaves — it separates
"the picture was never taken" from "the picture could not be sent".

### `unity_capture`

One call that does the whole thing: bring the window to the front, run `uiCapture`,
poll `uiCaptureStatus` inside the server until the shot lands, read the PNG, return it
as image content. Done by hand it is four round trips, and two of them are easy to get
wrong.

- `uiCaptureStatus` reports an **absolute** path while `read_image` takes a
  project-relative one, so every call used to need a manual conversion.
- The shot is taken at the end of a frame. With the Unity window in the background and
  Run In Background off, no frame finishes and the job stays `pending` for good.
  Focusing is therefore part of the procedure, not something to remember.

`folder` is a sub-folder of the capture folder (`--capture-folder`, default
`SandBox/Captures`); empty means the capture folder itself. Absolute paths and `..` are
refused. The server hands Unity an absolute path, which `PLSandbox` accepts as long as it is
inside the PolyLing work folder. The PNG is kept and its project-relative path is reported,
so `read_image` can show it again later.

Measured: `target=MainView` takes 717x507 / 42KB, `target=Window` 1960x830 / 150KB, both
completing on the first status check. Over-size is a refusal with the file left on disk;
there is no downscaling here (see above).

### Images returned by a PolyLing command

`polyling_call` hands the Editor's answer to `EditorImage.Lift`, which moves any image
out of the JSON and into image content, leaving only a count behind
(`images_moved_to_image_content`). Keeping the base64 in the JSON as well would charge
the tokens twice.

The Editor side puts one of these directly on the response object:

```json
"image":  { "mimeType": "image/png", "base64": "…" }
"images": [ { "mimeType": "image/png", "base64": "…" } ]
```

`mimeType` / `mime_type` / `mime` and `base64` / `data` are all accepted, and `mimeType`
may be omitted — the bytes decide, and a declared type that disagrees with them is
reported as a warning. A response with no image key is passed through byte for byte, so
commands that return no image are unaffected.

**No PolyLing command returns an image yet**, so this path is written but not exercised.
Screen captures take the other route, which `unity_capture` wraps up: `uiCapture` writes
a PNG under the capture folder, `uiCaptureStatus` reports the path, and the file is read
from disk. When adding a command that does return an image, check separately that the
transport survives it — `EditorControlClient` exchanges one JSON per line, and an
embedded image makes that line hundreds of thousands of characters long.

## Safety model

- `list_files`, `read_file`, `read_lines`, `search_text`, `read_binary` and `read_image`
  reach anywhere **inside any root** — the project folder, plus every `--root` folder.
- `unity_capture` reads the file the Editor names, and that path is checked against the
  roots the same way. It is the one read whose path comes from the Editor rather than
  from the caller, which is why `PathGuard` has a separate entry point for absolute paths.
- `write_file`, `apply_patch`, `write_binary` and `delete_path` reach only inside the
  configured **editable folders**. A `--root` folder is readable as soon as it is
  declared; it becomes writable only by also naming it in `--editable` as `@<name>`.
  There is one mechanism for "may be changed", not two.
- Absolute paths and `..` escapes are rejected. Symbolic links, junctions and mount
  points are resolved component by component before the containment check, because
  `Path.GetFullPath` is purely lexical and cannot see that `Assets/shared` points
  outside the project.
- An unknown root name is an error, not a fallback to project-relative: a typo that
  quietly became "file not found" would be impossible to read.
- Writable extensions:

| Tool | Allowed |
|---|---|
| `write_file`, `apply_patch` | `.cs` `.asmdef` `.asmref` `.json` `.txt` `.md` `.compute` |
| `write_binary` | `.cs` `.asmdef` `.asmref` `.json` `.txt` `.md` plus `.csv` `.pmx` `.mqo` `.obj` `.vrm` `.vrma` `.plrf` `.mfproj` `.png` `.jpg` `.jpeg` |
| `delete_path` | any — see below |

Executable formats (`.dll`, `.exe`) are on neither write list. The `write_binary` set
mirrors `WritableExtensions` in
`Packages/com.haglib.polyling/Runtime/Poly_Ling_Main/Core/Config/PLSandbox.cs`; that
class cannot be referenced from here because it depends on `UnityEngine`, so **change
both together**.

## Deleting

`delete_path` does not unlink anything. It moves the item to
`<root>/_McpTrash/<yyyyMMdd_HHmmss>/`, keeping the path it had inside that root, so
putting it back is a manual move. The trash sits directly under the root that held the
item — not under the project — so the move never crosses a volume, and so a root on
another drive keeps its own trash. The trash is outside the editable folders, which
means this tool cannot touch what is already in it; empty it by hand.

Unity imports `Assets`, `Packages` and `ProjectSettings` and nothing else, so a trash
folder at the project root is never compiled. Moving a `.cs` out of a package therefore
cannot produce a duplicate type.

The extension table does not apply. Folders and `.meta` files have no place on a
writable-extension list, and the real gate is containment in an editable folder, not the
extension. A matching `<name>.meta` is moved with the item: leaving it behind only makes
Unity delete the orphan, and then restoring the item gives it a new GUID and breaks every
reference to it.

Refused: a root folder itself, an editable folder itself, anything already inside
`_McpTrash`, and a non-empty folder unless `recursive=true`.

## apply_patch

Exact-text replacement, not unified diff.

```text
path: Assets/MyPackage/Runtime/Foo.cs
old_text: exact old code block
new_text: replacement code block
replace_all: false
```

With `replace_all=false` the old text must occur exactly once. `old_text` matches
literally, line endings included: a CRLF file will not match text written with LF, and
is rewritten with `write_file overwrite=true` instead.

## Source layout

| File | Holds |
|---|---|
| `Program.cs` | Startup, argument parsing, stdio and HTTP transports, Origin check, encoding registration |
| `WebClient/index.html` | Browser client served on `/` in HTTP mode (embedded resource) |
| `ServerState.cs` | Project root, extra roots, editable folders, Unity log path, target, capture folder |
| `ServerTarget.cs` | `--target editor` / `player` and the tools hidden for a build |
| `PathGuard.cs` | Root resolution, path containment and extension rules |
| `UnityProjectTools.cs` | File, compile, `unity_*` and `polyling_*` tools |
| `SourceSearchTools.cs` | `search_text`, `read_lines` |
| `BinaryFileTools.cs` | `read_binary`, `write_binary` |
| `DeleteTools.cs` | `delete_path` and the trash layout |
| `UnityLogTools.cs` | `read_unity_log` |
| `UnityWindowTools.cs` | `unity_focus` |
| `UnityCaptureTools.cs` | `unity_capture` |
| `ImageFileTools.cs` | `image_selftest`, `read_image` |
| `ImageBytes.cs` | Format identification, header-only size reading, per-image checks |
| `McpContent.cs` | The one place that builds `ContentBlock` sequences, and the size limits |
| `EditorImage.cs` | Lifting images out of the Editor's response JSON |
| `EditorControlClient.cs` | The connection to the Editor |
| `UiDispatcher.cs` | The activity log on stderr |

A tool that returns only text returns `string` and the SDK wraps it. A tool that returns
an image must return `IEnumerable<ContentBlock>` and build it through `McpContent`, so
the size limits and the failure wording stay in one place. Failures keep the existing
convention of a string starting with `ERROR: `.
