using System.Text;
using System.Windows;
using StorageIt.Services;

namespace StorageIt.Views;

public partial class OutputWindow : Window
{
    public OutputWindow(IEnumerable<CommandRunResult> results)
    {
        InitializeComponent();
        var sb = new StringBuilder();
        foreach (var r in results)
        {
            sb.AppendLine(r.Title + (r.ExitCode is { } code ? $"   (exit code {code})" : ""));
            sb.AppendLine(new string('─', 60));
            sb.AppendLine(string.IsNullOrWhiteSpace(r.Output) ? "(no output)" : r.Output);
            sb.AppendLine();
        }
        Box.Text = sb.ToString().TrimEnd();
        Title = "Command output";
    }
}
