using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public static class DwmDark
{
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    public static void Apply(Form form)
    {
        if (form == null || !form.IsHandleCreated) return;
        try
        {
            int useDark = 1;
            if (DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDark, sizeof(int));
        }
        catch { }
    }

    public static void Hook(Form form)
    {
        if (form == null) return;
        if (form.IsHandleCreated) Apply(form);
        else form.HandleCreated += (_, _) => Apply(form);
    }
}
