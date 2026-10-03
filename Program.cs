using System;
using System.Windows.Forms;

namespace KoreanInputFontTool;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        if (!AdministratorBootstrap.EnsureAdministrator())
            return;

        Application.Run(new LegacyMainForm());
    }
}
