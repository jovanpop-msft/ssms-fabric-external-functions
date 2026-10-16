using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FabricExternalFunctions.Ssms
{
    internal static class ObjectExplorerImages
    {
        private static readonly Dictionary<string, Icon> icons = new Dictionary<string, Icon>();

        public static Size SizeFor(TreeNode node) => node.TreeView?.ImageList?.ImageSize ?? new Size(16, 16);
        public static Icon Folder(Size size) => Load("folder.ico", size, true);
        public static Icon OpenFolder(Size size) => Load("openfolder.ico", size, true);
        public static Icon Function(Size size) => Load("UserDataFunctions.ico", size, false);
        public static Icon Parameter(Size size) => Load("input_parameter.ico", size, true);
        public static Icon Delete(Size size)
        {
            string key = "Delete:" + size.Width + ":" + size.Height;
            lock (icons)
            {
                if (icons.TryGetValue(key, out var cached)) return cached;
                using (var bitmap = new Bitmap(size.Width, size.Height))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    using (var pen = new Pen(Color.FromArgb(190, 50, 20), Math.Max(2f, size.Width / 8f)))
                    {
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        float x = size.Width / 5f, y = size.Height / 5f;
                        graphics.DrawLine(pen, x, y, size.Width - x, size.Height - y);
                        graphics.DrawLine(pen, size.Width - x, y, x, size.Height - y);
                    }
                    var icon = FromBitmap(bitmap);
                    icons.Add(key, icon);
                    return icon;
                }
            }
        }

        public static Icon Loading(Size size)
        {
            string key = "Loading:" + size.Width + ":" + size.Height;
            lock (icons)
            {
                if (icons.TryGetValue(key, out var cached)) return cached;
                using (var bitmap = new Bitmap(size.Width, size.Height))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    using (var pen = new Pen(Color.FromArgb(64, SystemColors.GrayText), Math.Max(2f, size.Width / 8f)))
                    {
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        float inset = pen.Width;
                        var bounds = new RectangleF(inset, inset, size.Width - inset * 2, size.Height - inset * 2);
                        graphics.DrawEllipse(pen, bounds);
                        pen.Color = SystemColors.HotTrack;
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        graphics.DrawArc(pen, bounds, -90, 250);
                    }
                    var icon = FromBitmap(bitmap);
                    icons.Add(key, icon);
                    return icon;
                }
            }
        }

        private static Icon FromBitmap(Bitmap bitmap)
        {
            IntPtr handle = bitmap.GetHicon();
            try
            {
                using (var borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone();
            }
            finally
            {
                if (!DestroyIcon(handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);

        private static Icon Load(string resource, Size size, bool native)
        {
            string key = resource + ":" + size.Width + ":" + size.Height;
            lock (icons)
            {
                if (icons.TryGetValue(key, out var icon)) return icon;
                var assembly = native
                    ? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "ObjectExplorer") ??
                        throw new NotSupportedException("The native Object Explorer icon assembly is not loaded.")
                    : typeof(ObjectExplorerImages).Assembly;
                string name = (native ? "Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer." :
                    "FabricExternalFunctions.Ssms.Resources.") + resource;
                using (var stream = assembly.GetManifestResourceStream(name) ??
                    throw new NotSupportedException("The Object Explorer icon resource is missing: " + name))
                    icon = new Icon(stream, size);
                icons.Add(key, icon);
                return icon;
            }
        }

        public static void Register(TreeView tree, TreeNode node)
        {
            var method = tree.GetType().GetMethod("AddIconToImageMap", BindingFlags.Instance | BindingFlags.Public) ??
                throw new NotSupportedException("This SSMS build does not expose native Object Explorer image registration.");
            method.Invoke(tree, new object[] { node });
        }
    }
}
