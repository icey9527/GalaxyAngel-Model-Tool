using System;
using System.Text;
using System.Windows.Forms;

namespace ScnViewer;

static class Program
{
    // Usage (scheme B):
    //   - Viewer: run without args (or with a single .scn path)
    //   - Converter: SCNViewer.exe <inputDir> <outputDir>
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            // Enable legacy codepages (needed for Shift-JIS / CP932 strings found in some SCN assets).
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            if (args.Length == 2)
            {
                Converter.ConvertFolder(args[0], args[1]);
                return 0;
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new ViewerForm(args.Length == 1 ? args[0] : null));
            return 0;
        }
        catch (Exception ex)
        {
            try { MessageBox.Show(ex.ToString(), "SCN Viewer"); } catch { }
            return 1;
        }
    }
}
