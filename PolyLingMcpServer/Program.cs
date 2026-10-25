using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;

namespace PolyLingMcpServer;

internal static class Program
{
    // 起動方法は 2 つ。
    //   既定      : Claude Desktop が子プロセスとして起動し、stdin/stdout で話す（stdio）。
    //   --http <port> : 127.0.0.1:<port> で Streamable HTTP を待ち受け、
    //                   同じポートの / でブラウザ用クライアント（WebClient/MCP_Client.html）を返す。
    // Unknown arguments, including a legacy --stdio, are ignored by the option parsers below.
    static int Main(string[] args) => Run(args).GetAwaiter().GetResult();

    /// <summary>ブラウザ用クライアントの埋め込みリソース名。csproj の LogicalName と一致させる。</summary>
    private const string WebClientResourceName = "PolyLingMcpServer.WebClient.MCP_Client.html";

    /// <summary>HTTP モードの MCP エンドポイント。WebClient/MCP_Client.html の MCP_ENDPOINT と一致させる。</summary>
    private const string McpEndpointPath = "/mcp";

    private static async Task<int> Run(string[] args)
    {    // Windows では、リダイレクトされた stderr の既定エンコーディングがシステムの
         // コードページになる。stdout は MCP トランスポートが生ストリームを直接扱うので
         // 触らず、stderr だけを UTF-8 に差し替える。
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false))
        {
            AutoFlush = true
        });

        // Shift_JIS などのコードページ系エンコーディングを使えるようにする。
        // .NET 8 は既定ではこれらを持たず、登録しないと
        // Encoding.GetEncoding("shift_jis") が ArgumentException になる。
        // read_file / read_lines / search_text の encoding 引数が使う
        // （MQO は Shift_JIS で書かれている）。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // 計測ログの集計だけを行う（MCP サーバとしては起動しない）。--summarize-metrics の後ろの引数を全部 JSONL として読む。
        int summarizeAt = Array.FindIndex(args, a => string.Equals(a, "--summarize-metrics", StringComparison.OrdinalIgnoreCase));
        if (summarizeAt >= 0)
        {
            var files = args.Skip(summarizeAt + 1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (files.Count == 0)
            {
                await Console.Error.WriteLineAsync("ERROR: --summarize-metrics needs one or more JSONL files.");
                return 2;
            }
            Console.OutputEncoding = new UTF8Encoding(false);
            return MetricsSummary.Run(files, Console.Out);
        }

        var projectRoot = GetOption(args, "--project");
        var editableFolders = GetEditableFolders(args);

        // 接続先。player（ビルド版）のときは --project を省ける（ソースを扱う道具は隠れる）。
        var target = ServerTarget.Editor;
        if (HasOption(args, "--target"))
        {
            var targetText = GetOption(args, "--target");
            if (!TargetRules.TryParse(targetText, out target))
            {
                await Console.Error.WriteLineAsync($"ERROR: --target must be editor or player: {targetText ?? "(missing)"}");
                return 2;
            }
        }

        bool hasProject = !string.IsNullOrWhiteSpace(projectRoot);

        // Editor.log の場所。Unity を -logFile 付きで起動しているときだけ指定する。
        var unityLogPath = GetOption(args, "--unity-log");
        if (string.IsNullOrWhiteSpace(unityLogPath))
            unityLogPath = DefaultUnityLogPath();

        bool projectUsable = hasProject && Directory.Exists(projectRoot);
        bool projectMissingButAllowed = !hasProject && target == ServerTarget.Player;

        if (!projectUsable && !projectMissingButAllowed)
        {
            // stdout is the JSON-RPC channel. Diagnostics always go to stderr.
            await Console.Error.WriteLineAsync(
                "usage: PolyLingMcpServer.exe --project <unity-project-folder> "
                + "[--editable <folder>] ... [--root <name>=<absolute folder>] ... [--unity-log <Editor.log path>] [--http <port>]\n"
                + "                            [--profile generic|current|optimized] [--metrics <jsonl path>] [--scene <name>]\n"
                + "                            [--target editor|player] [--capture-folder <folder>] [--player-exe <process name>]\n"
                + "  --target   editor (default) talks to the Unity Editor on port " + EditorControlClient.Port + ".\n"
                + "             player talks to a built PolyLing player on port " + EditorControlClient.PlayerPort + ";\n"
                + "             source, compile, Play mode and Editor.log tools are hidden, --project may be omitted,\n"
                + "             and unity_prefab_* still go to the Unity Editor.\n"
                + "  --capture-folder  where unity_capture saves, and the folder filled in when uiCapture is called without one.\n"
                + "             Project-relative, @<name>[/sub], or absolute. Default " + DefaultCaptureFolder + ".\n"
                + "             With --target player and no --project it must be absolute, and it is the only folder read_image can read.\n"
                + "  --player-exe  process name (without .exe) that unity_focus brings to the front when --target player.\n"
                + "  --guide    folder of the AI guide data (primer.md, hints.tsv) added to tool results.\n"
                + "             Default <project>/Packages/com.haglib.polyling/McpGuide. Re-read when the files change.\n"
                + "  --editable may be repeated, or given once as a ';'-separated list.\n"
                + "             Each item is a project-relative folder, or @<name>[/sub] for a --root folder.\n"
                + "  --root     adds a readable folder outside the project, addressed as @<name>/path.\n"
                + "             May be repeated. Example: --root settings=C:\\Users\\me\\AppData\\LocalLow\\Vendor\n"
                + "  --unity-log defaults to %LOCALAPPDATA%\\Unity\\Editor\\Editor.log.\n"
                + "  --unity-editor  Unity.exe that unity_editor_launch tries first. Without it, the version in\n"
                + "             ProjectSettings/ProjectVersion.txt is looked up in the Unity Hub install folders.\n"
                + "  --http     serves Streamable HTTP on 127.0.0.1:<port>" + McpEndpointPath + " and the browser client on /.\n"
                + "             It also relays /openai/v1/responses and /openai/v1/models to api.openai.com for the browser client.\n"
                + "             Without it the server talks over stdin/stdout.\n"
                + "  --profile  which tool set to expose, for comparing costs (default optimized):\n"
                + "             generic   = one MCP tool per PolyLing command (pl_<command>)\n"
                + "             current   = polyling_tools + polyling_call, as before\n"
                + "             optimized = polyling_search + polyling_describe + polyling_call\n"
                + "  --metrics  appends one JSON line per tools/list and tools/call (sizes, time, success) to this file.\n"
                + "  --metrics-search  also records polyling_search queries and their top results (off by default;\n"
                + "             the query text is the user's words, unlike the other records).\n"
                + "  --summarize-metrics <jsonl...>  prints a Markdown summary of metrics files and exits.\n"
                + "  --scene    optimized only: the work scene (PolyLing setScene) used when a request names none.\n"
                + "             Clients that can send _meta choose the scene per request with \"polyling/scene\".");
            return 2;
        }

        if (!TryParseRoots(args, out var extraRoots, out var rootError))
        {
            await Console.Error.WriteLineAsync("ERROR: " + rootError);
            return 2;
        }

        // --http は指定されたときだけ検証する。数値でない・範囲外を黙って stdio に
        // 落とすと、「ブラウザから繋がらない」の原因が起動引数だと分からなくなる。
        int? httpPort = null;
        if (HasOption(args, "--http"))
        {
            var portText = GetOption(args, "--http");
            if (!int.TryParse(portText, out var port) || port < 1 || port > 65535)
            {
                await Console.Error.WriteLineAsync($"ERROR: --http must be a port number 1-65535: {portText ?? "(missing)"}");
                return 2;
            }
            httpPort = port;
        }

        var profile = ServerProfile.Optimized;
        if (HasOption(args, "--profile"))
        {
            var profileText = GetOption(args, "--profile");
            if (!ProfileRules.TryParse(profileText, out profile))
            {
                await Console.Error.WriteLineAsync($"ERROR: --profile must be generic, current or optimized: {profileText ?? "(missing)"}");
                return 2;
            }
        }

        MetricsLog? metrics = null;
        bool metricsSearch = HasOption(args, "--metrics-search");
        if (HasOption(args, "--metrics"))
        {
            var metricsPath = GetOption(args, "--metrics");
            if (string.IsNullOrWhiteSpace(metricsPath))
            {
                await Console.Error.WriteLineAsync("ERROR: --metrics needs a file path.");
                return 2;
            }
            try
            {
                metrics = new MetricsLog(metricsPath);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"ERROR: --metrics {metricsPath}: {ex.Message}");
                return 2;
            }
        }

        var state = new ServerState
        {
            Target = target,
            Profile = profile,
            Metrics = metrics,
            MetricsSearch = metricsSearch,
            DefaultScene = (GetOption(args, "--scene") ?? string.Empty).Trim(),
            ProjectRoot = projectUsable ? Path.GetFullPath(projectRoot!) : string.Empty,
            ExtraRoots = extraRoots,
            EditableFolders = editableFolders,
            UnityLogPath = Path.GetFullPath(unityLogPath),
            UnityEditorPath = (GetOption(args, "--unity-editor") ?? string.Empty).Trim()
        };

        // player で --project が無いときは書き込み先を持たない（書く道具は TargetRules で隠れる）。
        // 既定の "Assets" はプロジェクト相対なので、残すと下の検査で止まる。
        if (!projectUsable) state.EditableFolders = [];

        // 打ち間違いをここで止める。起動してから「書けない」と言われるより、
        // 起動時に名前が引けないと分かる方が早い。
        foreach (var folder in state.EditableFolders)
        {
            if (state.TryResolveEditableFolder(folder, out _)) continue;

            await Console.Error.WriteLineAsync(
                $"ERROR: --editable {folder} names no known root. Known roots: {state.RootsDisplay}");
            return 2;
        }

        // キャプチャの保存先。どのルートの中でもなければ、撮れても読めないので起動時に止める。
        if (!TryResolveCaptureFolder(state, GetOption(args, "--capture-folder"), out var captureFolder, out var captureError))
        {
            await Console.Error.WriteLineAsync("ERROR: --capture-folder " + captureError);
            return 2;
        }
        state.CaptureFolder = captureFolder;

        // player で --project が無いときは、キャプチャフォルダを読み取りの根にする。
        // read_image が読めるのはここだけになる。
        if (state.ProjectRoot.Length == 0)
            state.ProjectRoot = captureFolder;

        if (!PathGuard.TryGetContainingRoot(state, captureFolder, out _))
        {
            await Console.Error.WriteLineAsync(
                $"ERROR: --capture-folder {captureFolder} is outside every readable root, so captures could not be read back. "
                + $"Known roots: {state.RootsDisplay}");
            return 2;
        }

        if (target == ServerTarget.Player)
        {
            var playerExe = (GetOption(args, "--player-exe") ?? string.Empty).Trim();
            if (playerExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) playerExe = playerExe[..^4];
            state.FocusProcessName = playerExe;
            EditorControlClient.TargetPort = EditorControlClient.PlayerPort;
        }

        // 起動時に一度だけ出す。どこまで届くのかを stderr のログで確かめられるようにする。
        await Console.Error.WriteLineAsync($"target: {TargetRules.Name(state.Target)} ({EditorControlClient.Endpoint})");
        await Console.Error.WriteLineAsync($"capture folder: {state.CaptureFolder}");

        // AI への案内データ（GuideRules）。--guide で変えられる。無ければプロジェクトの既定の置き場。
        // フォルダが無くても止めない（案内が出ないだけ）。
        var guideOption = GetOption(args, "--guide");
        GuideRules.GuideFolder = !string.IsNullOrWhiteSpace(guideOption)
            ? Path.GetFullPath(guideOption)
            : GuideRules.DefaultFolder(state.ProjectRoot);
        await Console.Error.WriteLineAsync(
            $"guide folder: {(GuideRules.GuideFolder.Length == 0 ? "(none)" : GuideRules.GuideFolder)}"
            + (GuideRules.GuideFolder.Length > 0 && !Directory.Exists(GuideRules.GuideFolder) ? " (missing)" : ""));
        await Console.Error.WriteLineAsync($"roots: {state.RootsDisplay}");
        await Console.Error.WriteLineAsync($"editable: {state.EditableFoldersDisplay}");
        await Console.Error.WriteLineAsync($"unity log: {state.UnityLogPath}");
        await Console.Error.WriteLineAsync($"unity editor: {(state.UnityEditorPath.Length == 0 ? "(Unity Hub lookup)" : state.UnityEditorPath)}");
        await Console.Error.WriteLineAsync($"profile: {ProfileRules.Name(state.Profile)}");
        await Console.Error.WriteLineAsync($"metrics: {state.Metrics?.FullPath ?? "(off)"}"
            + (state.Metrics != null && state.MetricsSearch ? " (+search queries)" : ""));
        if (state.DefaultScene.Length > 0)
            await Console.Error.WriteLineAsync(state.Profile == ServerProfile.Optimized
                ? $"scene: {state.DefaultScene}"
                : $"scene: {state.DefaultScene} (ignored: --scene works only with --profile optimized)");


        // 接続診断。stdout は JSON-RPC の経路なので stderr へ出す。
        // TcpDuplexServer は Editor 側でしか使わないため、ここでは購読しない
        // （csproj にリンクしていないので参照できない）。
        HagLib.NET.Duplex.PipeDuplexChannel.DiagnosticLog = line => Console.Error.WriteLine(line);
        HagLib.NET.Duplex.PipeDuplexServer.DiagnosticLog  = line => Console.Error.WriteLine(line);

        if (httpPort is int httpPortValue)
            await RunHttp(state, httpPortValue);
        else
            await RunStdio(state);

        return 0;
    }

    private static async Task RunStdio(ServerState state)
    {
        var builder = Host.CreateApplicationBuilder();

        // The default console logger writes to stdout, which would corrupt the
        // JSON-RPC stream. Send every log record to stderr instead.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(UiDispatcher.ForStandardError());
        builder.Services
            .AddPolyLingMcp(state)
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(UnityProjectTools).Assembly);

        await builder.Build().RunAsync();
    }

    /// <summary>
    /// Streamable HTTP で待ち受ける。ブラウザ用クライアントも同じオリジンから返すので CORS は設定しない。
    ///
    /// 【待ち受け先】 127.0.0.1 のみ。MCP 仕様（2026-07-28, Streamable HTTP「Security &amp; Endpoint」）が
    ///   ローカル実行時は localhost だけに bind することを求めている（SHOULD）。
    /// 【Origin 検証】 同節で MUST。Origin ヘッダがあり不正なら 403。ModelContextProtocol.AspNetCore 2.2.0 は
    ///   この検証を持たないので、ここで行う。許すのはこのサーバ自身のオリジンだけ。
    ///   Origin ヘッダの無い要求（curl など、ブラウザ以外）は仕様どおり通す。
    /// 【セッション】 2.2.0 の既定は Stateless（2026-07-28 でプロトコル上のセッションが廃止された）。
    ///   道具は ServerState / UiDispatcher 以外に状態を持たず、EditorControlClient も呼び出しごとに
    ///   接続するので、セッションを必要としない。
    /// </summary>
    private static async Task RunHttp(ServerState state, int port)
    {
        var builder = WebApplication.CreateBuilder();

        // stdio と揃えて、ログは stderr に出す。
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));

        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(UiDispatcher.ForStandardError());
        builder.Services
            .AddPolyLingMcp(state)
            .WithHttpTransport()
            .WithToolsFromAssembly(typeof(UnityProjectTools).Assembly);

        var app = builder.Build();

        // localhost と 127.0.0.1 はブラウザにとって別オリジンなので両方を許す。
        var allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"http://127.0.0.1:{port}",
            $"http://localhost:{port}"
        };

        app.Use(async (context, next) =>
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && !allowedOrigins.Contains(origin))
            {
                Console.Error.WriteLine($"403 origin rejected: {origin} {context.Request.Method} {context.Request.Path}");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
        });

        // ディスク上の WebClient/MCP_Client.html を探す（exe の場所から上へ。カレントフォルダには依らない）。
        // 見つかれば要求のたびにそれを読むので、HTML を直したらブラウザの再読み込みだけで反映される。
        // 見つからなければ（exe だけを配った場合など）埋め込みの写しを返す。
        string? webClientFile = FindWebClientFile();
        Console.Error.WriteLine(webClientFile is null
            ? "WebClient: embedded copy"
            : $"WebClient: {webClientFile}");

        app.MapGet("/", async context =>
        {
            if (webClientFile is not null && File.Exists(webClientFile))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(webClientFile);
                return;
            }
            await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(WebClientResourceName);
            if (stream is null)
            {
                // csproj の EmbeddedResource が外れていると起きる。黙って 404 にしない。
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync($"ERROR: embedded resource {WebClientResourceName} is missing.");
                return;
            }
            context.Response.ContentType = "text/html; charset=utf-8";
            await stream.CopyToAsync(context.Response.Body);
        });

        app.MapMcp(McpEndpointPath);
        MapOpenAiRelay(app);

        await Console.Error.WriteLineAsync($"http: http://127.0.0.1:{port}/  (MCP endpoint {McpEndpointPath})");

        await app.RunAsync();
    }

    /// <summary>--capture-folder を省いたときの保存先（プロジェクト相対）。</summary>
    private const string DefaultCaptureFolder = "SandBox/Captures";

    /// <summary>
    /// --capture-folder を絶対パスにし、無ければ作る。
    ///   絶対パス           … そのまま
    ///   @名前[/以下]       … その --root の中
    ///   それ以外           … プロジェクト相対（プロジェクトが無ければ失敗）
    /// 省いたときはプロジェクト相対の DefaultCaptureFolder。
    /// </summary>
    private static bool TryResolveCaptureFolder(ServerState state, string? spec, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error    = string.Empty;

        bool hasProject = state.ProjectRoot.Length > 0;
        var text = string.IsNullOrWhiteSpace(spec) ? DefaultCaptureFolder : spec.Trim();

        try
        {
            if (Path.IsPathRooted(text))
            {
                fullPath = Path.GetFullPath(text);
            }
            else if (text[0] == '@')
            {
                if (!state.TryResolveEditableFolder(text, out fullPath))
                {
                    error = $"{text} names no known root. Known roots: {state.RootsDisplay}";
                    return false;
                }
            }
            else
            {
                if (!hasProject)
                {
                    error = string.IsNullOrWhiteSpace(spec)
                        ? "is required as an absolute path when --target player is used without --project."
                        : $"{text} is relative, but there is no --project to resolve it against. Give an absolute path.";
                    return false;
                }
                fullPath = Path.GetFullPath(Path.Combine(state.ProjectRoot, text));
            }

            Directory.CreateDirectory(fullPath);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{text}: {ex.Message}";
            return false;
        }
    }

    // ================================================================
    // OpenAI API の中継（HTTP モードのみ）
    // ================================================================
    //
    // ブラウザ用クライアント（WebClient/index.html）が OpenAI の Responses API を使うための経路。
    // ブラウザから api.openai.com を直接呼べるか（CORS）は資料で確認できなかったので、
    // 同じオリジンのこのサーバを通す。
    //
    //   POST /openai/v1/responses → https://api.openai.com/v1/responses
    //   GET  /openai/v1/models    → https://api.openai.com/v1/models
    //
    // 中継するのはこの 2 つだけ（MapPost / MapGet に無いパスは 404）。
    // API キーは保存しない。ブラウザが付けた Authorization ヘッダをそのまま渡すだけ。
    // Origin の検査は RunHttp の前段がすでに行っている。

    private const string OpenAiUpstream = "https://api.openai.com/v1/";

    /// <summary>推論の長い応答を待つため、既定の 100 秒より長くする。</summary>
    private static readonly HttpClient OpenAiHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    private static void MapOpenAiRelay(WebApplication app)
    {
        app.MapPost("/openai/v1/responses", context => RelayToOpenAi(context, HttpMethod.Post, "responses"));
        app.MapGet("/openai/v1/models", context => RelayToOpenAi(context, HttpMethod.Get, "models"));
    }

    private static async Task RelayToOpenAi(HttpContext context, HttpMethod method, string path)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.Length == 0)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"error\":{\"message\":\"Authorization header is missing.\"}}");
            return;
        }

        using var request = new HttpRequestMessage(method, OpenAiUpstream + path);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        if (method == HttpMethod.Post)
        {
            var content = new StreamContent(context.Request.Body);
            content.Headers.TryAddWithoutValidation("Content-Type",
                string.IsNullOrEmpty(context.Request.ContentType) ? "application/json" : context.Request.ContentType);
            request.Content = content;
        }

        HttpResponseMessage response;
        try
        {
            response = await OpenAiHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (context.RequestAborted.IsCancellationRequested) return; // ブラウザ側が中止した
            await Console.Error.WriteLineAsync($"openai relay {path}: {ex.Message}");
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(
                System.Text.Json.JsonSerializer.Serialize(new { error = new { message = "relay to api.openai.com failed: " + ex.Message } }));
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } type)
                context.Response.ContentType = type.ToString();

            await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            await body.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }

    /// <summary>
    /// exe のあるフォルダから親へたどり、WebClient/MCP_Client.html を探す。
    /// bin/Debug/net8.0 で動かすと 3 つ上のプロジェクトフォルダで見つかる。無ければ null。
    /// </summary>
    private static string? FindWebClientFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "WebClient", "MCP_Client.html");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static bool HasOption(string[] args, string name) =>
        Array.Exists(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Unity Editor のログの既定の場所。Windows では
    /// &lt;LOCALAPPDATA&gt;\Unity\Editor\Editor.log（Unity マニュアル「Log files」）。
    /// LocalApplicationData は CSIDL_LOCAL_APPDATA に当たる。
    /// </summary>
    private static string DefaultUnityLogPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Unity", "Editor", "Editor.log");

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// Collects every --editable value. The flag may be repeated, and a single
    /// value may itself be a ';' or ',' separated list, because some MCP client
    /// configurations are easier to write with one argument than with several.
    /// </summary>
    private static string[] GetEditableFolders(string[] args)
    {
        var folders = new List<string>();

        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (!string.Equals(args[i], "--editable", StringComparison.OrdinalIgnoreCase))
                continue;

            folders.AddRange(SplitFolderList(args[i + 1]));
        }

        if (folders.Count == 0)
            folders.Add("Assets");

        return folders.ToArray();
    }

    // Typed as char[] rather than written inline as a collection expression:
    // an inline [';', ','] leaves string.Split ambiguous between its char[] and
    // string overloads (CS0121).
    private static readonly char[] FolderSeparators = [';', ','];

    internal static IEnumerable<string> SplitFolderList(string value) =>
        value.Split(FolderSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>ルート名に使えない文字。@ は接頭辞、区切りは相対パスとの境目。</summary>
    private static readonly char[] RootNameRejected = ['@', '/', '\\'];

    /// <summary>
    /// --root &lt;名前&gt;=&lt;絶対パス&gt; を集める。繰り返し可。
    ///
    /// 形の違い・名前の重なり・実在しないフォルダは起動を止める。黙って捨てると、
    /// 「@settings が引けない」が実行時の「ファイルが無い」に化けて原因が分からなくなる。
    /// </summary>
    internal static bool TryParseRoots(string[] args, out List<NamedRoot> roots, out string error)
    {
        roots = [];
        error = string.Empty;

        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (!string.Equals(args[i], "--root", StringComparison.OrdinalIgnoreCase))
                continue;

            var spec = args[i + 1];
            int eq = spec.IndexOf('=');

            if (eq <= 0 || eq == spec.Length - 1)
            {
                error = $"--root must be written name=path: {spec}";
                return false;
            }

            var name = spec[..eq].Trim();
            var path = spec[(eq + 1)..].Trim().Trim('"');

            if (name.IndexOfAny(RootNameRejected) >= 0)
            {
                error = $"--root name must not contain '@', '/' or '\\': {name}";
                return false;
            }

            if (!Path.IsPathRooted(path))
            {
                error = $"--root path must be absolute: {path}";
                return false;
            }

            var full = Path.GetFullPath(path);

            if (!Directory.Exists(full))
            {
                error = $"--root folder not found: {full}";
                return false;
            }

            if (roots.Any(root => string.Equals(root.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"--root name is used twice: {name}";
                return false;
            }

            roots.Add(new NamedRoot(name, full));
        }

        return true;
    }
}
