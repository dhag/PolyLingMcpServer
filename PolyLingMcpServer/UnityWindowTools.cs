using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PolyLingMcpServer;

/// <summary>
/// Unity Editor のウィンドウを前面へ出す。
///
/// 【なぜ Editor 側ではなくサーバ側で呼ぶか】
///   SetForegroundWindow が通る条件は Win32 の仕様で決まっており、
///   「呼び出し元がフォアグラウンドのプロセス」「フォアグラウンドのプロセスから
///   起動されたプロセス」「最後の入力イベントを受け取ったプロセス」などの
///   いずれかを満たす必要がある。満たさない場合はタスクバーが点滅するだけになる。
///   背面の Unity Editor 自身が呼ぶ形はどの条件にも当たらないので、サーバ側で呼ぶ。
///
///   副次的な利点として、Editor と一切通信しないので、
///   unity_state などが応答を返せないコンパイル中・インポート中でも動く。
///
/// 【子プロセスであることを当てにはできない】
///   本サーバは Claude Desktop の子プロセスだが、「フォアグラウンドのプロセスから
///   起動されたプロセス」の条件は親が“呼び出し時点で”前面にあることを要求し、
///   さらに継承した前面化権は一度使うと消費され、ユーザーが他ウィンドウを
///   操作した時点でも失効する。よって子プロセスというだけでは常に通るとは限らない。
///   実測でも、親である claude.exe が前面にいる状態で拒否された。
///
///   SPI_SETFOREGROUNDLOCKTIMEOUT でロック自体を外す手は使えない。
///   この設定変更もまた前面化権を要求し、ERROR_INVALID_PARAMETER で失敗する。
///   そこで AttachThreadInput で前面スレッドと入力状態を共有してから呼ぶ。
///   詳細は TryFocusViaAttachedInput を参照。
///
/// 【最前面固定はしない】
///   SetWindowPos の HWND_TOPMOST は解除するまで居座り、
///   他のウィンドウを手前に出せなくなるため使わない。
/// </summary>
[McpServerToolType]
public static class UnityWindowTools
{
    /// <summary>ShowWindow の SW_RESTORE。最小化・最大化を解いて元の大きさに戻す。</summary>
    private const int SW_RESTORE = 9;

    /// <summary>
    /// SystemParametersInfo のフォアグラウンドロック時間（ミリ秒）。取得のみ行う。
    /// 設定側（SPI_SETFOREGROUNDLOCKTIMEOUT）は「呼び出しスレッドが前面ウィンドウを
    /// 変更できること」を要求するため、前面化権を持たない本サーバからは
    /// 必ず ERROR_INVALID_PARAMETER で失敗する。実測で確認済みなので試さない。
    /// </summary>
    private const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;

    /// <summary>応答確認に使う無害なメッセージ。固まったウィンドウ相手なら時間切れになる。</summary>
    private const uint WM_NULL = 0x0000;

    /// <summary>応答しないウィンドウが相手なら待たずに打ち切る。</summary>
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>応答確認の待ち時間。ここで待たされるのは前面が固まっている異常時だけ。</summary>
    private const uint HungCheckTimeoutMs = 1000;

    /// <summary>
    /// 前面切り替えが反映されるのを待つ時間。呼び出し直後の GetForegroundWindow は
    /// まだ切り替え前を返すことがあるため、「受理されたが前面でない」を
    /// 競合と本当の失敗とで取り違えないようにする。
    /// </summary>
    private const int ForegroundSettleMs = 250;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out uint pvParam, uint fWinIni);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo,
                                                 [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
                                                    uint flags, uint timeoutMs, out UIntPtr result);

    /// <summary>HKEY_CURRENT_USER。</summary>
    private static readonly IntPtr HKEY_CURRENT_USER = unchecked((IntPtr)(int)0x80000001);

    /// <summary>RegGetValue で型を限定せずに引く。</summary>
    private const uint RRF_RT_ANY = 0x0000FFFF;

    /// <summary>レジストリ値の型 REG_DWORD。</summary>
    private const uint REG_DWORD = 4;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegGetValueW")]
    private static extern int RegGetValue(IntPtr hkey, string lpSubKey, string lpValue,
                                          uint dwFlags, out uint pdwType, out uint pvData, ref uint pcbData);

    [McpServerTool(Name = "unity_focus", Destructive = false),
     Description("Brings the Unity Editor main window to the front (with --target player, the built PolyLing player's window). "
               + "This runs inside the MCP server process and does not talk to Unity, "
               + "so unlike unity_state it also works while the Editor is compiling or importing assets. Windows only.")]
    public static string UnityFocus(ServerState state, UiDispatcher ui)
    {
        ui.Log("unity_focus");

        // 探すプロセス名。editor は "Unity"、player は --player-exe（ServerState.FocusProcessName）。
        string processName = state.FocusProcessName;
        if (string.IsNullOrWhiteSpace(processName))
            return "ERROR: no process name to bring to the front. Start the server with --player-exe <process name> when --target player is used.";

        if (!OperatingSystem.IsWindows())
            return "ERROR: unity_focus works on Windows only.";

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Exception ex)
        {
            return "ERROR: " + ex.Message;
        }

        try
        {
            var candidates = new List<(int Pid, IntPtr Handle, string Title)>();

            foreach (var process in processes)
            {
                IntPtr handle;
                string title;

                try
                {
                    // 落ちかけのプロセスでは両方とも例外になりうる。
                    handle = process.MainWindowHandle;
                    title  = process.MainWindowTitle ?? string.Empty;
                }
                catch (Exception)
                {
                    continue;
                }

                if (handle == IntPtr.Zero) continue;

                candidates.Add((process.Id, handle, title));
            }

            if (candidates.Count == 0)
                return $"ERROR: no '{processName}' process with a main window was found. Is it running?";

            if (candidates.Count > 1)
            {
                var list = new StringBuilder();
                list.Append($"ERROR: {candidates.Count} '{processName}' windows are open, so the target is ambiguous.");

                foreach (var candidate in candidates)
                    list.Append($"\n  pid={candidate.Pid}; title=\"{candidate.Title}\"");

                return list.ToString();
            }

            var target = candidates[0];

            bool minimized = IsIconic(target.Handle);
            if (minimized) ShowWindow(target.Handle, SW_RESTORE);

            // ロック時間は診断のためだけに読む。設定側は前面化権を要求するので使わない。
            bool timeoutRead = SystemParametersInfo(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, out uint lockTimeout, 0);

            bool accepted = TryFocusViaAttachedInput(target.Handle, out string attachDetail);

            bool   inFront    = GetForegroundWindow() == target.Handle;
            string ownerFirst = DescribeForegroundOwner();

            // 切り替えが遅れて反映されることがあるので、前面でなければ待って読み直す。
            if (!inFront)
            {
                Thread.Sleep(ForegroundSettleMs);
                inFront = GetForegroundWindow() == target.Handle;
            }

            var detail = $"pid={target.Pid}; title=\"{target.Title}\"; was_minimized={minimized}"
                       + $"; {attachDetail}"
                       + $"; fg_immediately={ownerFirst}"
                       + $"; fg_after_{ForegroundSettleMs}ms={DescribeForegroundOwner()}"
                       + $"; lock_timeout_effective={(timeoutRead ? lockTimeout.ToString() : "unknown")}"
                       + $"; lock_timeout_registry={ReadStoredForegroundLockTimeout()}";

            if (inFront)
                return $"OK: the '{processName}' window is in the foreground. {detail}";

            if (accepted)
                return $"OK: SetForegroundWindow was accepted, but another window is in front right now. {detail}";

            return "ERROR: Windows refused to change the foreground window, so the taskbar button is flashing instead. "
                 + $"The window in front belongs to {DescribeForegroundOwner()}. "
                 + detail;
        }
        catch (Exception ex)
        {
            return "ERROR: " + ex.Message;
        }
        finally
        {
            foreach (var process in processes)
            {
                try { process.Dispose(); }
                catch (Exception) { }
            }
        }
    }

    /// <summary>
    /// 前面ウィンドウのスレッドへ入力キューをアタッチしてから SetForegroundWindow を呼ぶ。
    ///
    /// アタッチしている間、こちらのスレッドは相手と入力状態を共有するため
    /// 「呼び出し元がフォアグラウンドのプロセス」と同じ扱いになり、前面化が通る。
    /// 公式に保証された手段ではない。ただし SPI_SETFOREGROUNDLOCKTIMEOUT による
    /// ロック解除は、その解除自体が前面化権を要求する（実測 ERROR_INVALID_PARAMETER）ため、
    /// 権利を持たない側から取れる手はこれしかない。
    ///
    /// 【ハング対策】
    ///   入力キューを共有する以上、相手スレッドが固まればこちらも巻き込まれる。
    ///   そこで WM_NULL を SMTO_ABORTIFHUNG 付きで投げ、応答したウィンドウにだけアタッチする。
    ///   アタッチ時間は SetForegroundWindow 1 回分に限定し、finally で必ず解除する。
    /// </summary>
    private static bool TryFocusViaAttachedInput(IntPtr target, out string detail)
    {
        var foreground = GetForegroundWindow();

        if (foreground == target)
        {
            detail = "attach=unnecessary (already in front)";
            return true;
        }

        // 前面が無いならロックも働かない。素直に呼ぶ。
        if (foreground == IntPtr.Zero)
        {
            detail = "attach=skipped (no foreground window)";
            return SetForegroundWindow(target);
        }

        if (SendMessageTimeout(foreground, WM_NULL, IntPtr.Zero, IntPtr.Zero,
                               SMTO_ABORTIFHUNG, HungCheckTimeoutMs, out _) == IntPtr.Zero)
        {
            // 応答しないスレッドへのアタッチは自分ごと巻き込まれるので、やらない。
            detail = "attach=refused (the foreground thread is not responding)";
            return SetForegroundWindow(target);
        }

        uint thisThread       = GetCurrentThreadId();
        uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
        uint targetThread     = GetWindowThreadProcessId(target, out _);

        if (foregroundThread == 0 || foregroundThread == thisThread)
        {
            detail = "attach=unnecessary (same input queue)";
            return SetForegroundWindow(target);
        }

        bool attachedSelf  = AttachThreadInput(thisThread, foregroundThread, true);
        int  attachSelfErr = attachedSelf ? 0 : Marshal.GetLastWin32Error();

        // 対象スレッドも同じ入力状態へ載せる。前面スレッドと呼び出し側だけを繋いだ場合、
        // 呼び出しは受理されても実際の切り替えが起きないことがある。
        bool attachedTarget = targetThread != 0
                           && targetThread != foregroundThread
                           && targetThread != thisThread
                           && AttachThreadInput(targetThread, foregroundThread, true);

        bool accepted;
        int  acceptError;

        try
        {
            BringWindowToTop(target);
            accepted    = SetForegroundWindow(target);
            acceptError = accepted ? 0 : Marshal.GetLastWin32Error();
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(targetThread, foregroundThread, false);
            if (attachedSelf)   AttachThreadInput(thisThread, foregroundThread, false);
        }

        detail = $"attach_self={(attachedSelf ? "ok" : $"failed({attachSelfErr})")}"
               + $"; attach_target={(attachedTarget ? "ok" : "no")}"
               + $"; set_foreground={(accepted ? "accepted" : $"refused({acceptError})")}";

        return accepted;
    }

    /// <summary>
    /// レジストリに保存されている ForegroundLockTimeout を返す。
    /// SystemParametersInfo が返す実効値とは別物で、両者が食い違っていれば
    /// 「実行中に誰かが変えた」のか「書き換え済みで再ログオン待ち」なのかを切り分けられる。
    /// </summary>
    private static string ReadStoredForegroundLockTimeout()
    {
        try
        {
            uint size = sizeof(uint);
            int  code = RegGetValue(HKEY_CURRENT_USER, @"Control Panel\Desktop", "ForegroundLockTimeout",
                                    RRF_RT_ANY, out uint type, out uint value, ref size);

            // 2 = ERROR_FILE_NOT_FOUND。値そのものが無い＝Windows の既定に従っている。
            if (code == 2) return "absent (falls back to the Windows default)";
            if (code != 0) return $"unreadable (win32_error={code})";
            if (type != REG_DWORD) return $"not a DWORD (reg_type={type})";

            return value.ToString();
        }
        catch (Exception)
        {
            return "unreadable";
        }
    }

    /// <summary>
    /// 失敗時にどのプロセスが前面を握っていたかを返す。
    /// 「前面化権が無かった」で片付けず、どの条件で落ちたかを切り分けるための診断。
    /// </summary>
    private static string DescribeForegroundOwner()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "no process (there is no foreground window)";

            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "an unknown process";

            using var owner = Process.GetProcessById((int)pid);
            return $"\"{owner.ProcessName}\" (pid={pid})";
        }
        catch (Exception)
        {
            return "an unknown process";
        }
    }
}
