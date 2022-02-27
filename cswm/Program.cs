using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using X11;

namespace SimpleWM;

public class SimpleLogger
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error,
    }

    public LogLevel Level;

    public SimpleLogger(LogLevel level)
    {
        Level = level;
    }

    private void Write(string message, LogLevel messageLevel)
    {
        if (Level <= messageLevel)
            Console.WriteLine($"{DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss tt")} {messageLevel} {message}");
    }

    public void Debug(string message)
    {
        Write(message, LogLevel.Debug);
    }

    public void Info(string message)
    {
        Write(message, LogLevel.Info);
    }

    public void Warn(string message)
    {
        Write(message, LogLevel.Warn);
    }

    public void Error(string message)
    {
        Write(message, LogLevel.Error);
    }
}

public record WindowGroup(Window Title, Window Child, Window Frame);

public struct WmCursors
{
    public Cursor DefaultCursor;
    public Cursor FrameCursor;
    public Cursor TitleCursor;
}

public struct WmColours
{
    public ulong ActiveFrameColor;
    public ulong ActiveTitleColor;
    public ulong ActiveTitleBorder;
    public ulong InactiveFrameColor;
    public ulong InactiveTitleColor;
    public ulong InactiveTitleBorder;
    public ulong DesktopBackground;
    public ulong WindowBackground;
}

public enum MouseMoveType
{
    TitleDrag,
    TopLeftFrameDrag,
    TopRightFrameDrag,
    BottomLeftFrameDrag,
    BottomRightFrameDrag,
    RightFrameDrag,
    TopFrameDrag,
    LeftFrameDrag,
    BottomFrameDrag,
}

public class MouseMovement
{
    public MouseMoveType Type { get; }
    public int MotionStartX { get; set; }
    public int MotionStartY { get; set; }
    public int WindowOriginPointX { get; }
    public int WindowOriginPointY { get; }

    public MouseMovement(MouseMoveType type, int motionX, int motionY, int windowX, int windowY)
    {
        Type = type;
        MotionStartX = motionX;
        MotionStartY = motionY;
        WindowOriginPointX = windowX;
        WindowOriginPointY = windowY;
    }
}

public class WindowManager
{
    private SimpleLogger log;
    private IntPtr display;
    private Window root;
    private WmCursors cursors = new();
    private WmColours colours = new();
    private readonly Dictionary<Window, WindowGroup> windowIndexByClient = new();
    private readonly Dictionary<Window, WindowGroup> windowIndexByFrame = new();
    private readonly Dictionary<Window, WindowGroup> windowIndexByTitle = new();
    private MouseMovement mouseMovement;

    public XErrorHandlerDelegate OnError;

    public int ErrorHandler(IntPtr display, ref XErrorEvent ev)
    {
        if (ev.error_code == 10) // BadAccess, i.e. another window manager has already claimed those privileges.
        {
            log.Error("X11 denied access to window manager resources - another window manager is already running");
            Environment.Exit(1);
        }

        // Other runtime errors and warnings.
        var description = Marshal.AllocHGlobal(1024);
        Xlib.XGetErrorText(this.display, ev.error_code, description, 1024);
        var desc = Marshal.PtrToStringAnsi(description);
        log.Warn($"X11 Error: {desc}");
        Marshal.FreeHGlobal(description);
        return 0;
    }

    public WindowManager(SimpleLogger.LogLevel level)
    {
        log = new SimpleLogger(level);
        var pDisplayText = Xlib.XDisplayName(null);
        var displayText = Marshal.PtrToStringAnsi(pDisplayText);
        if (displayText == String.Empty)
        {
            log.Error("No display configured for X11; check the value of the DISPLAY variable is set correctly");
            Environment.Exit(1);
        }

        log.Info($"Connecting to X11 Display {displayText}");
        display = Xlib.XOpenDisplay(null);

        if (display == IntPtr.Zero)
        {
            log.Error("Unable to open the default X display");
            Environment.Exit(1);
        }

        root = Xlib.XDefaultRootWindow(display);
        OnError = ErrorHandler;

        Xlib.XSetErrorHandler(OnError);
        // This will trigger a bad access error if another window manager is already running
        Xlib.XSelectInput(display, root,
            EventMask.SubstructureRedirectMask | EventMask.SubstructureNotifyMask |
            EventMask.ButtonPressMask | EventMask.KeyPressMask);

        Xlib.XSync(display, false);

        // Setup cursors
        cursors.DefaultCursor = Xlib.XCreateFontCursor(display, FontCursor.XC_left_ptr);
        cursors.TitleCursor = Xlib.XCreateFontCursor(display, FontCursor.XC_fleur);
        cursors.FrameCursor = Xlib.XCreateFontCursor(display, FontCursor.XC_sizing);
        Xlib.XDefineCursor(display, root, cursors.DefaultCursor);

        // Setup colours
        colours.DesktopBackground = GetPixelByName("black");
        colours.WindowBackground = GetPixelByName("white");
        colours.InactiveTitleBorder = GetPixelByName("light slate grey");
        colours.InactiveTitleColor = GetPixelByName("slate grey");
        colours.InactiveFrameColor = GetPixelByName("dark slate grey");
        colours.ActiveFrameColor = GetPixelByName("dark goldenrod");
        colours.ActiveTitleColor = GetPixelByName("gold");
        colours.ActiveTitleBorder = GetPixelByName("saddle brown");

        Xlib.XSetWindowBackground(display, root, colours.DesktopBackground);
        Xlib.XClearWindow(display, root); // force a redraw with the new background color
    }

    private ulong GetPixelByName(string name)
    {
        var screen = Xlib.XDefaultScreen(display);
        XColor color = new XColor();
        if (0 == Xlib.XParseColor(display, Xlib.XDefaultColormap(display, screen), name, ref color))
        {
            log.Error($"Invalid Color {name}");
        }

        if (0 == Xlib.XAllocColor(display, Xlib.XDefaultColormap(display, screen), ref color))
        {
            log.Error($"Failed to allocate color {name}");
        }

        return color.pixel;
    }

    private void AddFrame(Window child)
    {
        const int frameWidth = 3;
        const int titleHeight = 20;
        const int innerBorder = 1;

        if (windowIndexByClient.ContainsKey(child))
            return; // Window has already been framed.

        var name = String.Empty;
        Xlib.XFetchName(display, child, ref name);
        log.Debug($"Framing {name}");

        Xlib.XGetWindowAttributes(display, child, out var attr);
        var title = Xlib.XCreateSimpleWindow(display, root, attr.x, attr.y, attr.width - (2 * innerBorder),
            (titleHeight - 2 * innerBorder), innerBorder, colours.InactiveTitleColor, colours.InactiveTitleBorder);

        // Try to keep the child window in the same place, unless this would push the window decorations off screen.
        var adjustedXLoc = (attr.x - frameWidth < 0) ? 0 : attr.x - frameWidth;
        var adjustedYLoc = (attr.y - (titleHeight + frameWidth) < 0) ? 0 : (attr.y - (titleHeight + frameWidth));

        var frame = Xlib.XCreateSimpleWindow(display, root, adjustedXLoc,
            adjustedYLoc, attr.width, attr.height + titleHeight,
            3, colours.InactiveFrameColor, colours.WindowBackground);

        Xlib.XSelectInput(display, title, EventMask.ButtonPressMask | EventMask.ButtonReleaseMask
            | EventMask.Button1MotionMask | EventMask.ExposureMask);
        Xlib.XSelectInput(display, frame, EventMask.ButtonPressMask | EventMask.ButtonReleaseMask
            | EventMask.Button1MotionMask | EventMask.FocusChangeMask | EventMask.SubstructureRedirectMask | EventMask.SubstructureNotifyMask);

        Xlib.XDefineCursor(display, title, cursors.TitleCursor);
        Xlib.XDefineCursor(display, frame, cursors.FrameCursor);

        Xlib.XReparentWindow(display, title, frame, 0, 0);
        Xlib.XReparentWindow(display, child, frame, 0, titleHeight);
        Xlib.XMapWindow(display, title);
        Xlib.XMapWindow(display, frame);
        // Ensure the child window survives the untimely death of the window manager.
        Xlib.XAddToSaveSet(display, child);

        // Grab left click events from the client, so we can focus & raise on click
        SetFocusTrap(child);

        var wg = new WindowGroup(title, child, frame);
        windowIndexByClient[child] = wg;
        windowIndexByTitle[title] = wg;
        windowIndexByFrame[frame] = wg;
    }

    private void RemoveFrame(Window child)
    {

        if (!windowIndexByClient.ContainsKey(child))
        {
            return; // Do not attempt to unframe a window we have not framed.
        }
        var frame = windowIndexByClient[child].Frame;

        Xlib.XUnmapWindow(display, frame);
        Xlib.XDestroyWindow(display, frame);

        windowIndexByClient.Remove(child); // Cease tracking the window/frame pair.
    }

    private void SetFocusTrap(Window child)
    {
        Xlib.XGrabButton(display, Button.LEFT, KeyButtonMask.AnyModifier, child, false,
                        EventMask.ButtonPressMask, GrabMode.Async, GrabMode.Async, 0, 0);
    }

    private void UnsetFocusTrap(Window w)
    {
        Xlib.XUngrabButton(display, Button.LEFT, KeyButtonMask.AnyModifier, w);
    }


    private void OnMapRequest(XMapRequestEvent ev)
    {
        AddFrame(ev.window);
        Xlib.XMapWindow(display, ev.window);
    }

    private void OnButtonPressEvent(XButtonEvent ev)
    {
        var client = ev.window;
        if (windowIndexByClient.ContainsKey(ev.window) && ev.button == (uint)Button.LEFT)
        {
            LeftClickClientWindow(ev);
        }

        else if (windowIndexByTitle.ContainsKey(ev.window) && ev.button == (uint)Button.LEFT)
        {
            LeftClickTitleBar(ev);
            client = windowIndexByTitle[ev.window].Child;
        }

        else if (windowIndexByFrame.ContainsKey(ev.window) && ev.button == (uint)Button.LEFT)
        {
            LeftClickFrame(ev);
            client = windowIndexByFrame[ev.window].Child;
        }
        FocusAndRaiseWindow(client);
    }

    private void LeftClickTitleBar(XButtonEvent ev)
    {
        Window frame;
        var wg = windowIndexByTitle[ev.window];

        frame = wg.Frame;
        var child = wg.Child;
        FocusAndRaiseWindow(child);
        Xlib.XGetWindowAttributes(display, frame, out var attr);
        mouseMovement = new MouseMovement(MouseMoveType.TitleDrag, ev.x_root, ev.y_root, attr.x, attr.y);
    }

    private void LeftClickFrame(XButtonEvent ev)
    {
        Xlib.XGetWindowAttributes(display, ev.window, out var attr);

        var controlWidth = (attr.width / 2) <= 40 ? attr.width / 2 : 40;
        var controlHeight = (attr.height / 2) <= 40 ? attr.width / 2 : 40;

        if (ev.x >= attr.width - controlWidth) // right side
        {
            if (ev.y >= attr.height - controlHeight)
            {
                mouseMovement = new MouseMovement(MouseMoveType.BottomRightFrameDrag, ev.x_root, ev.y_root, attr.x, attr.y);
            }
            else if (ev.y <= controlHeight)
            {
                mouseMovement = new MouseMovement(MouseMoveType.TopRightFrameDrag, ev.x_root, ev.y_root, attr.x, attr.y);
            }
            else
            {
                mouseMovement = new MouseMovement(MouseMoveType.RightFrameDrag, ev.x_root, ev.y_root, attr.x, attr.y);
            }
        }
        else if (ev.x <= controlWidth)
        {
            if (ev.y >= attr.height - controlHeight)
            {
                mouseMovement = new MouseMovement(MouseMoveType.BottomLeftFrameDrag, ev.x_root, ev.y_root, attr.x, attr.y);
            }
            else if (ev.y <= controlHeight)
            {
                mouseMovement = new MouseMovement(MouseMoveType.TopLeftFrameDrag, ev.x_root, ev.y_root, attr.x, attr.y);
            }
            else
            {
                mouseMovement = new MouseMovement(MouseMoveType.LeftFrameDrag, ev.x_root, ev.y_root, attr.x, attr.y);
            }
        }
        else if (ev.y >= attr.height / 2)
        {
            mouseMovement = new MouseMovement(MouseMoveType.BottomFrameDrag, ev.x_root, ev.y_root, attr.x, attr.y);
        }
        else
        {
            mouseMovement = new MouseMovement(MouseMoveType.TopFrameDrag, ev.x_root, ev.y_root, attr.x, attr.y);
        }
    }

    private void OnExposeEvent(XExposeEvent ev)
    {
        if (windowIndexByTitle.ContainsKey(ev.window))
        {
            UpdateWindowTitle(ev.window);
        }
    }

    private void UpdateWindowTitle(Window titlebar)
    {
        var client = windowIndexByTitle[titlebar].Child;
        var name = String.Empty;
        if (Xlib.XFetchName(display, client, ref name) != Status.Failure)
            Xlib.XDrawString(display, titlebar, Xlib.XDefaultGC(display, Xlib.XDefaultScreen(display)), 2, 13,
                name, name.Length);
    }

    private void LeftClickClientWindow(XButtonEvent ev)
    {
        Window frame = windowIndexByClient[ev.window].Frame;
        // Release control of the left button to this application
        UnsetFocusTrap(ev.window);
        // Raise and focus it
        FocusAndRaiseWindow(ev.window);
    }

    private void FocusAndRaiseWindow(Window focus)
    {
        if (windowIndexByClient.ContainsKey(focus))
        {
            var frame = windowIndexByClient[focus].Frame;
            Xlib.XSetInputFocus(display, focus, RevertFocus.RevertToNone, 0);
            Xlib.XRaiseWindow(display, frame);
        }
    }


    void OnMotionEvent(XMotionEvent ev)
    {
        if (windowIndexByTitle.ContainsKey(ev.window))
        {
            LeftDragTitle(ev);
            return;
        }
        if (windowIndexByFrame.ContainsKey(ev.window))
        {
            LeftDragFrame(ev);
        }
    }

    private void LeftDragTitle(XMotionEvent ev)
    {
        if (mouseMovement == null)
            return;

        // If we hit the screen edges, snap to edge
        Xlib.XGetWindowAttributes(display, root, out var attr);
        if (ev.y_root == attr.height - 1 // Snap to bottom
            || ev.y_root == 0 // snap to top
            || ev.x_root == attr.width - 1 // snap to right
            || ev.x_root == 0)  // snap left
        {
            var frame = windowIndexByTitle[ev.window].Frame;
            SnapFrameToEdge(frame, ev.x_root, ev.y_root, attr.width, attr.height);
            return;
        }

        // Move the window, after converting co-ordinates into offsets relative to the origin point of motion
        var newY = ev.y_root - mouseMovement.MotionStartY;
        var newX = ev.x_root - mouseMovement.MotionStartX;
        Xlib.XMoveWindow(display, windowIndexByTitle[ev.window].Frame,
            mouseMovement.WindowOriginPointX + newX, mouseMovement.WindowOriginPointY + newY);
    }

    private void SnapFrameToEdge(Window frame, int x, int y, uint w, uint h)
    {
        var title = windowIndexByFrame[frame].Title;
        var client = windowIndexByFrame[frame].Child;

        Xlib.XGetWindowAttributes(display, title, out var tAttr);
        var tH = tAttr.height;
        Xlib.XGetWindowAttributes(display, frame, out var fAttr);
        var borderW = (uint)fAttr.border_width;
        int fY = 0, fX = 0;

        if (x == 0 || x == w - 1)
        { // Vertical half screen sized window
            if (x == w - 1)
                fX = (int)w / 2;

            Xlib.XMoveResizeWindow(display, frame, fX, 0, w / 2, h - (2 * borderW));
            Xlib.XMoveResizeWindow(display, title, 0, 0, w / 2, tH);
            Xlib.XMoveResizeWindow(display, client, 0, (int)tH, w / 2, (h - tH) - 2 * borderW);
        }
        else
        { // Horizontal half screen sized window
            if (y == h - 1)
                fY = (int)h / 2;

            Xlib.XMoveResizeWindow(display, frame, 0, fY, w, h / 2 - (2 * borderW));
            Xlib.XMoveResizeWindow(display, title, 0, 0, w, tH);
            Xlib.XMoveResizeWindow(display, client, 0, (int)tH, w, (h / 2) - tH - 2 * borderW);
        }
    }

    private void LeftDragFrame(XMotionEvent ev)
    {
        var frame = ev.window;
        var title = windowIndexByFrame[frame].Title;
        var client = windowIndexByFrame[frame].Child;

        var yDelta = 0;
        var xDelta = 0;

        var wDelta = 0;
        var hDelta = 0;

        var t = mouseMovement.Type;

        // Stretch to the right, or compress left, no lateral relocation of window origin.
        if (t == MouseMoveType.RightFrameDrag
            || t == MouseMoveType.TopRightFrameDrag
            || t == MouseMoveType.BottomRightFrameDrag)
        {
            wDelta = ev.x_root - mouseMovement.MotionStartX; // width change
        }
        // Stretch down, or compress upwards, no vertical movement of the window origin.
        if (t == MouseMoveType.BottomFrameDrag
            || t == MouseMoveType.BottomRightFrameDrag
            || t == MouseMoveType.BottomLeftFrameDrag)
        {
            hDelta = ev.y_root - mouseMovement.MotionStartY;
        }
        // Combine vertical stretch with movement of the window origin.
        if (t == MouseMoveType.TopFrameDrag
            || t == MouseMoveType.TopRightFrameDrag
            || t == MouseMoveType.TopLeftFrameDrag)
        {
            hDelta = mouseMovement.MotionStartY - ev.y_root;
            yDelta = -hDelta;
        }
        // Combined left stretch with movement of the window origin
        if (t == MouseMoveType.LeftFrameDrag
            || t == MouseMoveType.TopLeftFrameDrag
            || t == MouseMoveType.BottomLeftFrameDrag)
        {
            wDelta = mouseMovement.MotionStartX - ev.x_root;
            xDelta = -wDelta;
        }

        //// Resize and move the frame
        Xlib.XGetWindowAttributes(display, frame, out var attr);
        var newWidth = (uint)(attr.width + wDelta);
        var newHeight = (uint)(attr.height + hDelta);
        Xlib.XMoveResizeWindow(display, frame, attr.x + xDelta, attr.y + yDelta, newWidth, newHeight);

        //// Resize and move the title bar
        Xlib.XGetWindowAttributes(display, title, out attr);
        newWidth = (uint)(attr.width + wDelta);
        newHeight = attr.height;
        Xlib.XResizeWindow(display, title, newWidth, newHeight);

        //// Resize and move the client window bar
        Xlib.XGetWindowAttributes(display, client, out attr);
        newWidth = (uint)(attr.width + wDelta);
        newHeight = (uint)(attr.height + hDelta);
        Xlib.XResizeWindow(display, client, newWidth, newHeight);

        mouseMovement.MotionStartX = ev.x_root;
        mouseMovement.MotionStartY = ev.y_root;
    }

    void OnMapNotify(XMapEvent ev)
    {
        log.Debug($"(MapNotifyEvent) Window {ev.window} has been mapped.");
    }

    void OnConfigureRequest(XConfigureRequestEvent ev)
    {
        var changes = new XWindowChanges
        {
            x = ev.x,
            y = ev.y,
            width = ev.width,
            height = ev.height,
            border_width = ev.border_width,
            sibling = ev.above,
            stack_mode = ev.detail
        };

        if (windowIndexByClient.ContainsKey(ev.window))
        {
            // Resize the frame
            Xlib.XConfigureWindow(display, windowIndexByClient[ev.window].Frame, ev.value_mask, ref changes);
        }
        // Resize the window
        Xlib.XConfigureWindow(display, ev.window, ev.value_mask, ref changes);
    }

    void OnUnmapNotify(XUnmapEvent ev)
    {
        if (ev.@event == root)
        {
            log.Debug($"(OnUnmapNotify) Window {ev.window} has been reparented to root");
            return;
        }
        if (!windowIndexByClient.ContainsKey(ev.window))
            return; // Don't unmap a window we don't own.

        RemoveFrame(ev.window);
    }

    // Annoyingly, this event fires when an application quits itself, resuling in some bad window errors.
    void OnFocusOutEvent(XFocusChangeEvent ev)
    {
        var title = windowIndexByFrame[ev.window].Title;
        var frame = ev.window;
        if (Status.Failure == Xlib.XSetWindowBorder(display, frame, colours.InactiveTitleBorder))
            return; // If the windows have been destroyed asynchronously, cut this short.
        Xlib.XSetWindowBackground(display, title, colours.InactiveTitleColor);
        Xlib.XSetWindowBorder(display, title, colours.InactiveFrameColor);
        Xlib.XClearWindow(display, title);
        UpdateWindowTitle(title);

        SetFocusTrap(windowIndexByFrame[ev.window].Child);
    }

    void OnFocusInEvent(XFocusChangeEvent ev)
    {
        var title = windowIndexByFrame[ev.window].Title;
        var frame = ev.window;
        Xlib.XSetWindowBorder(display, frame, colours.ActiveFrameColor);
        Xlib.XSetWindowBackground(display, title, colours.ActiveTitleColor);
        Xlib.XSetWindowBorder(display, title, colours.ActiveTitleBorder);
        Xlib.XClearWindow(display, title); // Force colour update

        UpdateWindowTitle(title); //Redraw the title, purged by clearing.
    }

    void OnDestroyNotify(XDestroyWindowEvent ev)
    {
        if (windowIndexByClient.ContainsKey(ev.window))
            windowIndexByClient.Remove(ev.window);
        else if (windowIndexByFrame.ContainsKey(ev.window))
            windowIndexByFrame.Remove(ev.window);
        else if (windowIndexByTitle.ContainsKey(ev.window))
            windowIndexByTitle.Remove(ev.window);
        log.Debug($"(OnDestroyNotify) Destroyed {ev.window}");
    }

    void OnReparentNotify(XReparentEvent ev)
    {
    }

    void OnCreateNotify(XCreateWindowEvent ev)
    {
        log.Debug($"(OnCreateNotify) Created event {ev.window}, parent {ev.parent}");
    }

    public int Run()
    {
        IntPtr ev = Marshal.AllocHGlobal(24 * sizeof(long));

        Window returnedParent = 0, returnedRoot = 0;


        Xlib.XGrabServer(display); // Lock the server during initialization
        var r = Xlib.XQueryTree(display, root, ref returnedRoot, ref returnedParent,
            out var childWindows);

        log.Debug($"Reparenting and framing pre-existing child windows: {childWindows.Count}");
        for (var i = 0; i < childWindows.Count; i++)
        {
            log.Debug($"Framing child {i}, {childWindows[i]}");
            AddFrame(childWindows[i]);
        }
        Xlib.XUngrabServer(display); // Release the lock on the server.


        while (true)
        {
            Xlib.XNextEvent(display, ev);
            var xevent = Marshal.PtrToStructure<XAnyEvent>(ev);

            switch (xevent.type)
            {
                case (int)Event.DestroyNotify:
                    var destroyEvent = Marshal.PtrToStructure<XDestroyWindowEvent>(ev);
                    OnDestroyNotify(destroyEvent);
                    break;
                case (int)Event.CreateNotify:
                    var createEvent = Marshal.PtrToStructure<XCreateWindowEvent>(ev);
                    OnCreateNotify(createEvent);
                    break;
                case (int)Event.MapNotify:
                    var mapNotify = Marshal.PtrToStructure<XMapEvent>(ev);
                    OnMapNotify(mapNotify);
                    break;
                case (int)Event.MapRequest:
                    var mapEvent = Marshal.PtrToStructure<XMapRequestEvent>(ev);
                    OnMapRequest(mapEvent);
                    break;
                case (int)Event.ConfigureRequest:
                    var cfgEvent = Marshal.PtrToStructure<XConfigureRequestEvent>(ev);
                    OnConfigureRequest(cfgEvent);
                    break;
                case (int)Event.UnmapNotify:
                    var unmapEvent = Marshal.PtrToStructure<XUnmapEvent>(ev);
                    OnUnmapNotify(unmapEvent);
                    break;
                case (int)Event.ReparentNotify:
                    var reparentEvent = Marshal.PtrToStructure<XReparentEvent>(ev);
                    OnReparentNotify(reparentEvent);
                    break;
                case (int)Event.ButtonPress:
                    var buttonPressEvent = Marshal.PtrToStructure<XButtonEvent>(ev);
                    OnButtonPressEvent(buttonPressEvent);
                    break;
                case (int)Event.ButtonRelease:
                    mouseMovement = null;
                    break;
                case (int)Event.MotionNotify:
                    // We only want the newest motion event in order to reduce perceived lag
                    while (Xlib.XCheckMaskEvent(display, EventMask.Button1MotionMask, ev)) { /* skip over */ }
                    var motionEvent = Marshal.PtrToStructure<XMotionEvent>(ev);
                    OnMotionEvent(motionEvent);
                    break;
                case (int)Event.FocusOut:
                    var focusOutEvent = Marshal.PtrToStructure<XFocusChangeEvent>(ev);
                    OnFocusOutEvent(focusOutEvent);
                    break;
                case (int)Event.FocusIn:
                    var focusInEvent = Marshal.PtrToStructure<XFocusChangeEvent>(ev);
                    OnFocusInEvent(focusInEvent);
                    break;
                case (int)Event.ConfigureNotify:
                    break;
                case (int)Event.Expose:
                    var exposeEvent = Marshal.PtrToStructure<XExposeEvent>(ev);
                    OnExposeEvent(exposeEvent);
                    break;
                default:
                    log.Debug($"Event type: { Enum.GetName(typeof(Event), xevent.type)}");
                    break;
            }
        }
        Marshal.FreeHGlobal(ev);
    }
}

class Program
{
    static void Main(string[] args)
    {
        var wm = new WindowManager(SimpleLogger.LogLevel.Info);
        wm.Run();
    }
}

