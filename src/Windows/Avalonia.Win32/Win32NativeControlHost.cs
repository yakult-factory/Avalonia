using System;
using System.Runtime.InteropServices;
using Avalonia.Controls.Platform;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Avalonia.Win32.Interop;

namespace Avalonia.Win32
{
    class Win32NativeControlHost : INativeControlHostImpl
    {
        public WindowImpl Window { get; }

        public Win32NativeControlHost(WindowImpl window)
        {
            Window = window;
        }

        void AssertCompatible(IPlatformHandle handle)
        {
            if (!IsCompatibleWith(handle))
                throw new ArgumentException($"Don't know what to do with {handle.HandleDescriptor}");
        }

        public INativeControlHostDestroyableControlHandle CreateDefaultChild(IPlatformHandle parent)
        {
            AssertCompatible(parent);
            return new DumbWindow(parent.Handle);
        }

        public INativeControlHostControlTopLevelAttachment CreateNewAttachment(Func<IPlatformHandle, IPlatformHandle> create)
        {
            var holder = new DumbWindow(Window.Handle.Handle);
            Win32NativeControlAttachment attachment = null;
            try
            {
                var child = create(holder);
                // ReSharper disable once UseObjectOrCollectionInitializer
                // It has to be assigned to the variable before property setter is called so we dispose it on exception
                attachment = new Win32NativeControlAttachment(holder, child);
                attachment.AttachedTo = this;
                return attachment;
            }
            catch
            {
                attachment?.Dispose();
                holder?.Destroy();
                throw;
            }
        }

        public INativeControlHostControlTopLevelAttachment CreateNewAttachment(IPlatformHandle handle)
        {
            AssertCompatible(handle);
            return new Win32NativeControlAttachment(new DumbWindow(Window.Handle.Handle),
                handle) { AttachedTo = this };
        }

        public bool IsCompatibleWith(IPlatformHandle handle) => handle.HandleDescriptor == "HWND";

        class DumbWindow : IDisposable, INativeControlHostDestroyableControlHandle
        {
            public IntPtr Handle { get;}
            public string HandleDescriptor => "HWND";
            public void Destroy() => Dispose();

            UnmanagedMethods.WndProc _wndProcDelegate;
            private readonly string _className;

            public DumbWindow(IntPtr? parent = null)
            {
                _wndProcDelegate = WndProc;
                var wndClassEx = new UnmanagedMethods.WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<UnmanagedMethods.WNDCLASSEX>(),
                    hInstance = UnmanagedMethods.GetModuleHandle(null),
                    lpfnWndProc = _wndProcDelegate,
                    lpszClassName = _className = "AvaloniaDumbWindow-" + Guid.NewGuid(),
                };

                var atom = UnmanagedMethods.RegisterClassEx(ref wndClassEx);
                Handle = UnmanagedMethods.CreateWindowEx(
                    0,
                    atom,
                    null,
                    (int)UnmanagedMethods.WindowStyles.WS_CHILD,
                    0,
                    0,
                    640,
                    480,
                    parent ?? OffscreenParentWindow.Handle,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);
            }



            protected virtual unsafe IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
            {
                return UnmanagedMethods.DefWindowProc(hWnd, msg, wParam, lParam);
            }

            private void ReleaseUnmanagedResources()
            {
                UnmanagedMethods.DestroyWindow(Handle);
                UnmanagedMethods.UnregisterClass(_className, UnmanagedMethods.GetModuleHandle(null));
            }

            public void Dispose()
            {
                ReleaseUnmanagedResources();
                GC.SuppressFinalize(this);
            }

            ~DumbWindow()
            {
                ReleaseUnmanagedResources();
            }
        }

        class Win32NativeControlAttachment : INativeControlHostControlTopLevelAttachment
        {
            private DumbWindow _holder;
            private IPlatformHandle _child;
            private Win32NativeControlHost _attachedTo;

            public Win32NativeControlAttachment(DumbWindow holder, IPlatformHandle child)
            {
                _holder = holder;
                _child = child;
                UnmanagedMethods.SetParent(child.Handle, _holder.Handle);
                UnmanagedMethods.ShowWindow(child.Handle, UnmanagedMethods.ShowWindowCommand.Show);
            }

            void CheckDisposed()
            {
                if (_holder == null)
                    throw new ObjectDisposedException(nameof(Win32NativeControlAttachment));
            }

            public void Dispose()
            {
                if (_child != null)
                    UnmanagedMethods.SetParent(_child.Handle, OffscreenParentWindow.Handle);
                _holder?.Dispose();
                _holder = null;
                _child = null;
                _attachedTo = null;
            }

            public INativeControlHostImpl AttachedTo
            {
                get => _attachedTo;
                set
                {
                    CheckDisposed();
                    _attachedTo = (Win32NativeControlHost) value;
                    if (_attachedTo == null)
                    {
                        UnmanagedMethods.ShowWindow(_holder.Handle, UnmanagedMethods.ShowWindowCommand.Hide);
                        UnmanagedMethods.SetParent(_holder.Handle, OffscreenParentWindow.Handle);
                    }
                    else
                        UnmanagedMethods.SetParent(_holder.Handle, _attachedTo.Window.Handle.Handle);
                }
            }

            public bool IsCompatibleWith(INativeControlHostImpl host) => host is Win32NativeControlHost;

            public void Hide()
            {
                UnmanagedMethods.SetWindowPos(_holder.Handle, IntPtr.Zero,
                    -100, -100, 1, 1,
                    UnmanagedMethods.SetWindowPosFlags.SWP_HIDEWINDOW |
                    UnmanagedMethods.SetWindowPosFlags.SWP_NOACTIVATE);
            }
            
            public unsafe void ShowInBounds(TransformedBounds transformedBounds)
            {
                CheckDisposed();
                if (_attachedTo == null)
                    throw new InvalidOperationException("The control isn't currently attached to a toplevel");
                var bounds = transformedBounds.Bounds.TransformToAABB(transformedBounds.Transform) *
                             new Vector(_attachedTo.Window.Scaling, _attachedTo.Window.Scaling);
                var pixelRect = new PixelRect((int)bounds.X, (int)bounds.Y, Math.Max(1, (int)bounds.Width),
                    Math.Max(1, (int)bounds.Height));
                
                UnmanagedMethods.MoveWindow(_child.Handle, 0, 0, pixelRect.Width, pixelRect.Height, true);
                UnmanagedMethods.SetWindowPos(_holder.Handle, IntPtr.Zero, pixelRect.X, pixelRect.Y, pixelRect.Width,
                    pixelRect.Height,
                    UnmanagedMethods.SetWindowPosFlags.SWP_SHOWWINDOW
                    | UnmanagedMethods.SetWindowPosFlags.SWP_NOZORDER
                    | UnmanagedMethods.SetWindowPosFlags.SWP_NOACTIVATE);
                
                UnmanagedMethods.InvalidateRect(_attachedTo.Window.Handle.Handle, null, false);
            }
        }

    }
}
