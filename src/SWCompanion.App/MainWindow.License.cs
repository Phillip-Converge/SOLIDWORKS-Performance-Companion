using System.Windows;
using SWCompanion.Core.Network;

namespace SWCompanion.App;

/// <summary>
/// The licence server panel (Details page): which SolidNetWork License server(s) this PC is set to use, and an opt-in
/// test of their ports. Reading the setting is read only; the test only connects and disconnects, and is logged.
/// </summary>
public partial class MainWindow
{
    private IReadOnlyList<LicenseServer> _licenseServers = [];

    private void SetupLicense()
    {
        Loaded += async (_, _) =>
        {
            _licenseServers = await Task.Run(LicenseServers.Read);
            if (_licenseServers.Count == 0)
            {
                LicenseFound.Text = "This PC isn't set to use a SolidNetWork License server, so there's nothing to test. " +
                                    "(SOLIDWORKS is probably licensed on this PC itself, or online.)";
                LicenseTestKey.Visibility = Visibility.Collapsed;
                return;
            }
            LicenseFound.Text = "This PC is set to look for a SolidNetWork License server at " +
                string.Join(" or ", _licenseServers.Select(s => $"{s.Host} (port {s.Port})")) +
                ". If SOLIDWORKS says it can't get a licence, or takes a long time to start, test the connection here. " +
                "(If SOLIDWORKS is activated on this PC itself rather than through a licence server, this setting may be left over and unused.)";
        };
    }

    private async void LicenseTestKey_Click(object sender, RoutedEventArgs e)
    {
        if (_licenseServers.Count == 0) return;
        LicenseTestKey.IsEnabled = false;
        LicenseTestNote.Text = "testing...";
        try
        {
            var results = await Task.Run(() => LicenseServers.Test(_licenseServers));
            foreach (var r in results)
                _log.Write($"licence server test (requested by the user): {r.Role} {r.Server} port {r.Port}: " +
                           $"{r.Result}{(r.Milliseconds is { } ms ? $" in {ms:0} ms" : "")}.");
            LicenseResults.ItemsSource = LicenseServers.Explain(results);
            LicenseTestNote.Text = $"Tested at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            LicenseTestNote.Text = "The test couldn't run: " + ex.Message;
            _log.Write("licence server test: couldn't run (" + ex.Message + ").");
        }
        finally
        {
            LicenseTestKey.IsEnabled = true;
        }
    }
}
