using System.Runtime.InteropServices;

namespace TrafficVista.Configuration;

internal static class DpiScalingTest
{
    internal static void Verify(MainForm form)
    {
        var output = Path.Combine(AppContext.BaseDirectory, "demo-output", "dpi-scaling");
        Directory.CreateDirectory(output);
        var originalDpi = form.DeviceDpi;
        var controls = new[] { "btnStartStop", "textBox_TaskApiUrl", "numericUpDown_MaxConcurrency", "groupBox_Cache", "checkBox_TaskIntakeBackpressure", "numericUpDown_AutoRestartMinutes" }
            .Select(name => form.Controls.Find(name, true).Single()).ToArray();
        var baseline = controls.ToDictionary(control => control, control => control.Bounds);
        var parents = controls.ToDictionary(control => control, control => control.Parent);
        foreach (var dpi in new[] { 96, 120, 144, 192, originalDpi })
        {
            ChangeDpi(form, dpi);
            Application.DoEvents();
            if (form.DeviceDpi != dpi) throw new InvalidOperationException("DPI切换未生效：" + dpi);
            foreach (var control in controls)
            {
                var expected = baseline[control];
                var scale = dpi / (double)originalDpi;
                if (control.Parent != parents[control]
                    || Math.Abs(control.Left - expected.Left * scale) > 4
                    || Math.Abs(control.Top - expected.Top * scale) > 4)
                    throw new InvalidOperationException($"DPI={dpi}时原布局位置关系改变：{control.Name}，Bounds={control.Bounds}");
            }
            if (form.Controls.OfType<TabControl>().Any() || form.Controls.Find("compactTabs", true).Length > 0)
                throw new InvalidOperationException("DPI缩放不得引入页签重排。");
            var api = form.Controls.Find("textBox_TaskApiUrl", true).Single();
            var proxy = form.Controls.Find("textBox_ProxyIpUrl", true).Single();
            if (api.Bottom > proxy.Top) throw new InvalidOperationException("DPI缩放后接口输入框重叠。");
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            image.Save(Path.Combine(output, $"dpi-{dpi}.png"));
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }
    private static void ChangeDpi(Form form, int dpi)
    {
        var bounds = form.Bounds;
        var ratio = dpi / (double)form.DeviceDpi;
        var rectangle = new WindowRect { Left = bounds.Left, Top = bounds.Top,
            Right = bounds.Left + (int)Math.Round(bounds.Width * ratio), Bottom = bounds.Top + (int)Math.Round(bounds.Height * ratio) };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WindowRect>());
        try
        {
            Marshal.StructureToPtr(rectangle, pointer, false);
            SendMessage(form.Handle, 0x02E0, (IntPtr)(dpi | dpi << 16), pointer);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
}
