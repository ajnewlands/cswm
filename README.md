# CSWM
A simple X11 window manager implemented in C#

![Csharp Window Manager](https://github.com/ajnewlands/cswm/blob/master/cswm.PNG)

CSWM was created alongside the X11 library for Csharp (https://www.nuget.org/packages/X11) primarily to validate that the library functions worked as intended, and to demonstrate the ability to build X11/Xlib applications in .NET core.

It certainly won't revolutionize the world of window management but is usable as is. The aesthetic was largely inspired by the fvwm and motif window managers which were popular in my undergraduate years rather than the big desktop environments seen in many modern Linux distributions. It is very lightweight and quite usable on a Pentium N netbook.

It supports basic mouse driven window management (snap to edge, resize by grabbing the frame, reposition by grabbing the title). It does not (yet) include a native menu so combining it with something like 9menu would be best.
