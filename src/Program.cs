using System;
using System.Text;
using System.Windows.Forms;

namespace ScnViewer;

static class Program
{
    private const string AppTitle = "GalaxyAngel Model Tool";

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            // Enable legacy codepages (needed for Shift-JIS / CP932 strings found in some SCN assets).
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            ApplicationConfiguration.Initialize();
            Application.Run(new ViewerForm(args.Length == 1 ? args[0] : null));
            return 0;
        }
        catch (Exception ex)
        {
            try { MessageBox.Show(ex.ToString(), AppTitle); } catch { }
            return 1;
        }
    }
}
