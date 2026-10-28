using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System.Runtime.InteropServices;
using System.Text;
#endif

// Windows Player only. The Editor keeps its normal window and input behaviour.
public sealed class DiandianDesktopWindow : MonoBehaviour
{
    public bool IsDesktop { get; private set; }
    public Vector2Int windowSize = new Vector2Int(340, 400);
    public Camera renderCamera;
    [Tooltip("Invert alpha-mask rows if a graphics driver returns a vertically flipped readback.")]
    public bool flipMaskY;
    public event Action Clicked, DoubleClicked, ContextRequested;
    public event Action<bool> DragChanged;
    public event Action<int> MenuSelected;

    bool topmost = true;
    bool InspectMode => Array.IndexOf(Environment.GetCommandLineArgs(), "-diandian-inspect") >= 0;
    const string Prefs = "DiandianV2.Window.";

    public void SetTopmost(bool value)
    {
        topmost = value;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        PlayerPrefs.SetInt(Prefs + "Topmost", value ? 1 : 0);
        if (IsDesktop)
            SetWindowPos(window, value ? new IntPtr(-1) : new IntPtr(-2), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
#endif
    }

    public void SetSize(int width, int height)
    {
        windowSize = new Vector2Int(Mathf.Clamp(width, 120, 1600), Mathf.Clamp(height, 160, 1600));
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (!IsDesktop) return;
        GetWindowRect(window, out RECT current);
        resizeRect = ClampToWorkArea(new RECT(current.right - windowSize.x, current.bottom - windowSize.y, windowSize.x, windowSize.y));
        windowSize = new Vector2Int(resizeRect.Width, resizeRect.Height);
        Screen.SetResolution(windowSize.x, windowSize.y, FullScreenMode.Windowed);
        resizeFrame = Time.frameCount + 2; // Unity applies resolution changes at frame end.
#endif
    }

    public void ShowMenu(string[] labels, int[] ids)
    {
        if (labels == null || ids == null || labels.Length != ids.Length)
            throw new ArgumentException("Menu labels and IDs must have the same length.");
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (!IsDesktop || menuOpen || labels.Length == 0) return;
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        int choice = 0;
        menuOpen = true;
        pendingClick = false;
        SetClickThrough(false);
        try
        {
            for (int i = 0; i < labels.Length; i++)
                AppendMenu(menu, string.IsNullOrEmpty(labels[i]) ? 0x800u : 0u, new UIntPtr((uint)i + 1), labels[i]);
            GetCursorPos(out POINT point);
            SetForegroundWindow(window);
            choice = TrackPopupMenuEx(menu, 0x0100 | 0x0080 | 0x0002, point.x, point.y, window, IntPtr.Zero);
            PostMessage(window, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
            menuOpen = false;
            previousLeft = Down(1);
            previousRight = Down(2);
            rightPressedHere = false;
            lock (pointerLock) pointerMessages.Clear();
        }
        if (choice > 0 && choice <= ids.Length) MenuSelected?.Invoke(ids[choice - 1]);
#endif
    }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    const long WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_TRANSPARENT = 0x20;
    const int MaskSize = 64;
    IntPtr window, originalStyle, originalExStyle, originalWndProc;
    static IntPtr foundWindow;
    static DiandianDesktopWindow hookOwner;
    static IntPtr fallbackWndProc;
    static readonly WindowProc windowProc = ReceiveWindowMessage;
    readonly object pointerLock = new object();
    readonly List<PointerMessage> pointerMessages = new List<PointerMessage>();
    readonly List<PointerMessage> frameMessages = new List<PointerMessage>();
    bool stylesSaved, clickThrough, captured, dragging, menuOpen, previousLeft, previousRight, rightPressedHere;
    bool pendingClick, useAsync, readbackPending, maskReady;
    POINT pressPoint, lastClickPoint;
    RECT pressRect, resizeRect;
    int resizeFrame = -1;
    float clickDeadline, nextWorkAreaCheck;
    RenderTexture alphaTarget;
    Texture2D alphaCpu;
    Color32[] alphaPixels = new Color32[MaskSize * MaskSize];
    DiandianAlphaCapture alphaCapture;
    AsyncGPUReadbackRequest readback;

    IEnumerator Start()
    {
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
        {
            Debug.LogError("DIANDIAN_DESKTOP: DWM transparency requires Direct3D11 and the BitBlt swap chain.");
            yield break;
        }
        Application.runInBackground = true;
        topmost = PlayerPrefs.GetInt(Prefs + "Topmost", 1) != 0;
        windowSize = new Vector2Int(PlayerPrefs.GetInt(Prefs + "Width", windowSize.x), PlayerPrefs.GetInt(Prefs + "Height", windowSize.y));
        windowSize.x = Mathf.Clamp(windowSize.x, 120, 1600);
        windowSize.y = Mathf.Clamp(windowSize.y, 160, 1600);
        Screen.SetResolution(windowSize.x, windowSize.y, FullScreenMode.Windowed);
        yield return null;
        yield return null;
        float deadline = Time.realtimeSinceStartup + 5;
        while (window == IntPtr.Zero && Time.realtimeSinceStartup < deadline)
        {
            foundWindow = IntPtr.Zero;
            EnumWindows(FindWindow, new IntPtr(GetCurrentProcessId()));
            window = foundWindow;
            if (window == IntPtr.Zero) yield return null;
        }
        if (window == IntPtr.Zero)
        {
            Debug.LogError("DIANDIAN_DESKTOP: Unity Player window was not found.");
            yield break;
        }
        renderCamera = renderCamera ? renderCamera : Camera.main;
        if (!renderCamera || GraphicsSettings.currentRenderPipeline != null)
        {
            Debug.LogError("DIANDIAN_DESKTOP: Assign the v2 Built-in rendering camera.");
            yield break;
        }

        originalStyle = GetStyle(window, -16);
        originalExStyle = GetStyle(window, -20);
        stylesSaved = true;
        MakeBorderless();
        var margins = new MARGINS { left = -1, right = -1, top = -1, bottom = -1 };
        if (!SetLayeredWindowAttributes(window, 0, 255, 2) || DwmExtendFrameIntoClientArea(window, ref margins) < 0)
        {
            Debug.LogError("DIANDIAN_DESKTOP: Windows rejected transparent window initialization.");
            enabled = false;
            yield break;
        }
        IsDesktop = true;
        originalWndProc = GetStyle(window, -4);
        fallbackWndProc = originalWndProc;
        hookOwner = this;
        IntPtr previousProc = IntPtr.Size == 8
            ? SetWindowLongPtr(window, -4, Marshal.GetFunctionPointerForDelegate(windowProc))
            : new IntPtr(SetWindowLong(window, -4, Marshal.GetFunctionPointerForDelegate(windowProc).ToInt32()));
        if (previousProc == IntPtr.Zero)
        {
            Debug.LogError("DIANDIAN_DESKTOP: Could not install the native mouse input hook.");
            hookOwner = null;
            enabled = false;
            yield break;
        }
        originalWndProc = previousProc;
        fallbackWndProc = previousProc;
        SetTopmost(topmost);
        GetWindowRect(window, out RECT current);
        RECT work = WorkArea(current);
        int x = PlayerPrefs.GetInt(Prefs + "X", work.right - windowSize.x - 24);
        int y = PlayerPrefs.GetInt(Prefs + "Y", work.bottom - windowSize.y - 24);
        MoveClamped(new RECT(x, y, windowSize.x, windowSize.y));
        previousLeft = Down(1);
        previousRight = Down(2);
        SetClickThrough(true);

        // Windows swap-chain buffers do not provide a readable alpha channel.
        renderCamera.forceIntoRenderTexture = true;
        alphaTarget = new RenderTexture(MaskSize, MaskSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
        { name = "DiandianV2 Alpha Hit Mask", filterMode = FilterMode.Bilinear, useMipMap = false };
        alphaTarget.Create();
        alphaCapture = renderCamera.gameObject.AddComponent<DiandianAlphaCapture>();
        alphaCapture.mask = alphaTarget;
        useAsync = SystemInfo.supportsAsyncGPUReadback;
        var endOfFrame = new WaitForEndOfFrame();
        var interval = new WaitForSecondsRealtime(.1f);
        while (IsDesktop)
        {
            yield return endOfFrame;
            if (!readbackPending)
            {
                if (useAsync)
                {
                    readbackPending = true;
                    readback = AsyncGPUReadback.Request(alphaTarget, 0, TextureFormat.RGBA32, ReceiveMask);
                }
                else ReadMaskSynchronously();
            }
            yield return interval;
        }
    }

    void ReceiveMask(AsyncGPUReadbackRequest request)
    {
        readbackPending = false;
        if (!this || !IsDesktop) return;
        if (request.hasError)
        {
            useAsync = false;
            Debug.LogWarning("DIANDIAN_DESKTOP: Async alpha readback failed; using a 64x64 readback at 10 Hz.");
            return;
        }
        request.GetData<Color32>().CopyTo(alphaPixels);
        maskReady = true;
    }

    void ReadMaskSynchronously()
    {
        // ponytail: a 10 Hz fallback for older GPUs; the normal path never waits for a readback.
        if (!alphaCpu) alphaCpu = new Texture2D(MaskSize, MaskSize, TextureFormat.RGBA32, false, true);
        var previous = RenderTexture.active;
        try
        {
            RenderTexture.active = alphaTarget;
            alphaCpu.ReadPixels(new Rect(0, 0, MaskSize, MaskSize), 0, 0, false);
            alphaPixels = alphaCpu.GetPixels32();
            maskReady = true;
        }
        finally { RenderTexture.active = previous; }
    }

    bool OpaqueAt(POINT screenPoint)
    {
        return AlphaAt(screenPoint, flipMaskY) > 12;
    }

    int AlphaAt(POINT screenPoint, bool invertY)
    {
        if (!maskReady || !GetClientRect(window, out RECT client)) return 0;
        ScreenToClient(window, ref screenPoint);
        if (client.Width <= 0 || client.Height <= 0 || screenPoint.x < 0 || screenPoint.y < 0 ||
            screenPoint.x >= client.Width || screenPoint.y >= client.Height) return 0;
        int x = Mathf.Clamp(screenPoint.x * MaskSize / client.Width, 0, MaskSize - 1);
        int y = Mathf.Clamp(MaskSize - 1 - screenPoint.y * MaskSize / client.Height, 0, MaskSize - 1);
        if (invertY) y = MaskSize - 1 - y;
        return alphaPixels[y * MaskSize + x].a;
    }

    void Update()
    {
        if (!IsDesktop || menuOpen || !GetCursorPos(out POINT cursor)) return;
        if (resizeFrame >= 0 && Time.frameCount >= resizeFrame)
        {
            resizeFrame = -1;
            MakeBorderless();
            MoveClamped(resizeRect);
            SavePlacement();
        }
        if (!captured) SetClickThrough(!OpaqueAt(cursor));
        bool left = Down(1), right = Down(2);
        // Only messages delivered to this HWND can start a gesture. Polling is used
        // only to continue/release an established drag, never to steal another app's click.
        lock (pointerLock)
        {
            frameMessages.AddRange(pointerMessages);
            pointerMessages.Clear();
        }
        foreach (PointerMessage message in frameMessages)
        {
            bool down = message.id == 0x201 || message.id == 0x203 || message.id == 0x204 || message.id == 0x206;
            bool hit = OpaqueAt(message.point);
            if (down) TracePointer(message, hit);
            switch (message.id)
            {
                case 0x201: case 0x203: // WM_LBUTTONDOWN / WM_LBUTTONDBLCLK
                    if (hit && !captured)
                    {
                        captured = true; dragging = false; pressPoint = message.point;
                        GetWindowRect(window, out pressRect);
                        SetClickThrough(false); SetCapture(window);
                    }
                    break;
                case 0x202: // WM_LBUTTONUP, including down+up within one rendered frame
                    if (captured) { MoveDrag(message.point); EndLeft(message.point); }
                    break;
                case 0x204: case 0x206:
                    rightPressedHere = hit && !captured;
                    break;
                case 0x205:
                    if (rightPressedHere && hit) ContextRequested?.Invoke();
                    rightPressedHere = false;
                    break;
            }
        }
        frameMessages.Clear();
        if (captured)
        {
            MoveDrag(cursor);
            // A physical release can finish a drag when capture was lost. Do not
            // cancel a just-queued synthetic down solely because async state is up.
            if (!left && previousLeft) EndLeft(cursor);
        }
        if (pendingClick && !captured && Time.unscaledTime > clickDeadline)
        {
            pendingClick = false;
            Clicked?.Invoke();
        }
        if (!right && previousRight) rightPressedHere = false;
        previousLeft = left;
        previousRight = right;
        if (!captured && Time.unscaledTime >= nextWorkAreaCheck)
        {
            nextWorkAreaCheck = Time.unscaledTime + 1;
            GetWindowRect(window, out RECT rect);
            RECT clamped = ClampToWorkArea(rect);
            if (rect.left != clamped.left || rect.top != clamped.top || rect.Width != clamped.Width || rect.Height != clamped.Height)
            {
                MoveClamped(clamped);
                SavePlacement();
            }
        }
    }

    void MoveDrag(POINT cursor)
    {
        int dx = cursor.x - pressPoint.x, dy = cursor.y - pressPoint.y;
        if (!dragging && (Math.Abs(dx) >= GetSystemMetrics(68) || Math.Abs(dy) >= GetSystemMetrics(69)))
        {
            dragging = true; pendingClick = false; DragChanged?.Invoke(true);
        }
        if (dragging) SetWindowPos(window, IntPtr.Zero, pressRect.left + dx, pressRect.top + dy, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    void EndLeft(POINT cursor)
    {
        captured = false; ReleaseCapture();
        if (dragging)
        {
            dragging = false; GetWindowRect(window, out RECT rect);
            MoveClamped(rect); SavePlacement(); DragChanged?.Invoke(false);
        }
        else if (pendingClick && Time.unscaledTime <= clickDeadline &&
            Math.Abs(cursor.x - lastClickPoint.x) <= GetSystemMetrics(36) && Math.Abs(cursor.y - lastClickPoint.y) <= GetSystemMetrics(37))
        { pendingClick = false; DoubleClicked?.Invoke(); }
        else
        {
            if (pendingClick) Clicked?.Invoke();
            pendingClick = true; lastClickPoint = cursor;
            clickDeadline = Time.unscaledTime + GetDoubleClickTime() / 1000f;
        }
    }

    void TracePointer(PointerMessage message, bool hit)
    {
        if (!InspectMode) return;
        POINT clientPoint = message.point; ScreenToClient(window, ref clientPoint);
        GetClientRect(window, out RECT client);
        int count = 0, max = 0;
        foreach (Color32 pixel in alphaPixels) { if (pixel.a > 12) count++; max = Math.Max(max, pixel.a); }
        Debug.Log($"DIANDIAN_INPUT: WM={message.id:X} screen={message.point.x},{message.point.y} client={clientPoint.x},{clientPoint.y}/{client.Width}x{client.Height} unity={Screen.width}x{Screen.height} maskReady={maskReady} alpha={AlphaAt(message.point,flipMaskY)} flippedAlpha={AlphaAt(message.point,!flipMaskY)} maskMax={max} opaquePixels={count}/{alphaPixels.Length} hit={hit} foreground={GetForegroundWindow()==window} underCursor={WindowFromPoint(message.point)==window}");
    }

    [AOT.MonoPInvokeCallback(typeof(WindowProc))]
    static IntPtr ReceiveWindowMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam)
    {
        // This callback can run on the native window thread. Do not call Unity APIs here.
        DiandianDesktopWindow owner = hookOwner;
        if (!ReferenceEquals(owner, null) && handle == owner.window && message >= 0x201 && message <= 0x206)
        {
            long position = lParam.ToInt64();
            var point = new POINT { x = unchecked((short)(position & 0xffff)), y = unchecked((short)((position >> 16) & 0xffff)) };
            ClientToScreen(handle, ref point);
            lock (owner.pointerLock)
                if (owner.pointerMessages.Count < 128) owner.pointerMessages.Add(new PointerMessage { id = message, point = point });
        }
        IntPtr previous = ReferenceEquals(owner, null) ? fallbackWndProc : owner.originalWndProc;
        return previous != IntPtr.Zero ? CallWindowProc(previous, handle, message, wParam, lParam) : DefWindowProc(handle, message, wParam, lParam);
    }

    void MakeBorderless()
    {
        SetStyle(window, -16, new IntPtr((GetStyle(window, -16).ToInt64() & ~0x00CF0000L) | 0x80000000L));
        // Optional QA flag exposes the window to accessibility tools; normal pets stay out of the taskbar.
        bool inspect = InspectMode;
        long extended = (GetStyle(window, -20).ToInt64() & ~(WS_EX_APPWINDOW | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT)) | WS_EX_LAYERED | (inspect ? WS_EX_APPWINDOW : WS_EX_TOOLWINDOW);
        SetStyle(window, -20, new IntPtr(extended | (clickThrough && !inspect ? WS_EX_TRANSPARENT : 0)));
    }

    void SetClickThrough(bool value)
    {
        if (clickThrough == value) return;
        clickThrough = value;
        long style = GetStyle(window, -20).ToInt64();
        SetStyle(window, -20, new IntPtr(value && !InspectMode ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT));
    }

    void MoveClamped(RECT rect)
    {
        rect = ClampToWorkArea(rect);
        windowSize = new Vector2Int(rect.Width, rect.Height);
        SetWindowPos(window, IntPtr.Zero, rect.left, rect.top, rect.Width, rect.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    static RECT ClampToWorkArea(RECT rect)
    {
        RECT work = WorkArea(rect);
        int width = Mathf.Min(rect.Width, work.Width), height = Mathf.Min(rect.Height, work.Height);
        return new RECT(Mathf.Clamp(rect.left, work.left, work.right - width), Mathf.Clamp(rect.top, work.top, work.bottom - height), width, height);
    }

    static RECT WorkArea(RECT rect)
    {
        var info = new MONITORINFO { size = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(MonitorFromRect(ref rect, 2), ref info)) return info.work;
        return new RECT(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));
    }

    void SavePlacement()
    {
        if (!IsDesktop || !GetWindowRect(window, out RECT rect)) return;
        PlayerPrefs.SetInt(Prefs + "X", rect.left);
        PlayerPrefs.SetInt(Prefs + "Y", rect.top);
        PlayerPrefs.SetInt(Prefs + "Width", windowSize.x);
        PlayerPrefs.SetInt(Prefs + "Height", windowSize.y);
        PlayerPrefs.Save();
    }

    void OnApplicationFocus(bool focused)
    {
        if (!focused || !IsDesktop) return;
        var margins = new MARGINS { left = -1, right = -1, top = -1, bottom = -1 };
        DwmExtendFrameIntoClientArea(window, ref margins);
    }

    void OnApplicationQuit() { SavePlacement(); }

    void OnDisable()
    {
        SavePlacement();
        if (originalWndProc != IntPtr.Zero && IsWindow(window)) SetStyle(window, -4, originalWndProc);
        if (ReferenceEquals(hookOwner, this)) hookOwner = null;
        lock (pointerLock) pointerMessages.Clear();
        IsDesktop = false;
        StopAllCoroutines();
        if (captured) ReleaseCapture();
        if (dragging) DragChanged?.Invoke(false);
        if (alphaCapture) { alphaCapture.mask = null; Destroy(alphaCapture); }
        if (readbackPending) readback.WaitForCompletion();
        if (alphaTarget) { alphaTarget.Release(); Destroy(alphaTarget); }
        if (alphaCpu) Destroy(alphaCpu);
        if (stylesSaved && IsWindow(window))
        {
            SetStyle(window, -16, originalStyle);
            SetStyle(window, -20, originalExStyle);
            var margins = new MARGINS();
            DwmExtendFrameIntoClientArea(window, ref margins);
            SetWindowPos(window, new IntPtr(-2), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }

    static bool Down(int button) { return (GetAsyncKeyState(button) & 0x8000) != 0; }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [AOT.MonoPInvokeCallback(typeof(EnumWindowsProc))]
    static bool FindWindow(IntPtr handle, IntPtr process)
    {
        GetWindowThreadProcessId(handle, out uint id);
        if (id != (uint)process.ToInt64() || !IsWindowVisible(handle) || GetWindow(handle, 4) != IntPtr.Zero) return true;
        var name = new StringBuilder(64);
        GetClassName(handle, name, name.Capacity);
        if (name.ToString() != "UnityWndClass") return true;
        foundWindow = handle;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    struct PointerMessage { public uint id; public POINT point; }
    [StructLayout(LayoutKind.Sequential)] struct RECT
    {
        public int left, top, right, bottom;
        public int Width => right - left;
        public int Height => bottom - top;
        public RECT(int x, int y, int width, int height) { left = x; top = y; right = x + width; bottom = y + height; }
    }
    [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int left, right, top, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int size; public RECT monitor, work; public uint flags; }
    delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr WindowProc(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    static IntPtr GetStyle(IntPtr handle, int index) => IntPtr.Size == 8 ? GetWindowLongPtr(handle, index) : new IntPtr(GetWindowLong(handle, index));
    static void SetStyle(IntPtr handle, int index, IntPtr value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr(handle, index, value);
        else SetWindowLong(handle, index, unchecked((int)value.ToInt64()));
    }
    [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr handle, out uint process);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr handle, StringBuilder name, int length);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong(IntPtr handle, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr handle, uint key, byte alpha, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr handle, ref MARGINS margins);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr handle, out RECT rect);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr handle, out RECT rect);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr handle, ref POINT point);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr handle, ref POINT point);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")] static extern IntPtr CallWindowProc(IntPtr previous, IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] static extern IntPtr DefWindowProc(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr handle);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string label);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
#endif
}
