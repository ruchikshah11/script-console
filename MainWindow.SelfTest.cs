using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScriptConsole;

/// <summary>
/// Hidden --self-test mode: drives the window like a user (folder page, tree, parameter form, run,
/// search), saves screenshots and a results file, then closes. Used to verify builds.
/// </summary>
public partial class MainWindow
{
    async Task SelfTestAsync(string outDir, string library)
    {
        Directory.CreateDirectory(outDir);
        var results = new Dictionary<string, object?>();
        try
        {
            await Task.Delay(1500);
            results["suggestions"] = (Suggestions.ItemsSource as IEnumerable<SuggestionRow>)?.Count();
            Shot(outDir, "1-folder-page");

            OpenFolder(library);
            var demo = files.First(f => f.Name == "Demo-Streams");
            RevealEntry(demo);
            await Task.Delay(400);
            results["treeTop"] = string.Join(", ", Tree.Items.OfType<TreeViewItem>().Select(t => t.Tag is TreeNode n ? "folder:" + n.Name : "script:" + ((ScriptEntry)t.Tag).Name));
            results["selected"] = ScriptTitle.Text;

            foreach (var (p, control) in inputs)
            {
                switch (p.Name)
                {
                    case "Name": ((TextBox)control).Text = "Ruchik"; break;
                    case "Mode": ((ComboBox)control).SelectedItem = "Thorough"; break;
                    case "Sites": ((TextBox)control).Text = "hr, finance"; break;
                    case "ReportOnly": ((CheckBox)control).IsChecked = true; break;
                    case "ClientSecret": ((PasswordBox)control).Password = "s3cret"; break;
                }
            }
            results["preview"] = CommandPreview.Text;
            await Task.Delay(300);
            Shot(outDir, "2-main");

            RunSelected();
            var session = runs[0];
            for (int i = 0; i < 100 && !session.Done; i++) await Task.Delay(200);
            results["status"] = session.Status;
            results["exit"] = session.Exit;
            results["output"] = session.Lines;
            await Task.Delay(300);
            Shot(outDir, "3-run");

            fullHelp = true;
            FillHelp();
            await Task.Delay(200);
            Shot(outDir, "4-full-help");

            var watch = Stopwatch.StartNew();
            OpenFolder(@"C:\Ruchik\powershell-scripts");
            results["repoLoadMs"] = watch.ElapsedMilliseconds;
            if (Tree.Items[0] is TreeViewItem first) first.IsExpanded = true;
            await Task.Delay(400);
            Shot(outDir, "5-repo-tree");
            SearchBox.Text = "taxcatch";
            await Task.Delay(300);
            results["search"] = CountText.Text;
            Shot(outDir, "6-search");
        }
        catch (Exception ex) { results["error"] = ex.ToString(); }
        File.WriteAllText(Path.Combine(outDir, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Close();
    }

    void Shot(string dir, string name)
    {
        UpdateLayout();
        var bmp = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(dir, name + ".png"));
        encoder.Save(fs);
    }
}
