using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Switchboard.Views
{
    /// <summary>
    /// One catalogue row. <see cref="Release"/> is what the install path uses; the rest is for display.
    /// </summary>
    /// <remarks>
    /// Observable because the installed column is re-read from disk after every install and after any
    /// change on the Mods tab. The old version mutated the sub-item text in place, which worked but left
    /// no way to tell the list that it had changed.
    /// </remarks>
    internal sealed class ReleaseRow : INotifyPropertyChanged
    {
        private string _installed;

        public ReleaseRow(ReleaseInfo release, string installed)
        {
            Release = release;
            _installed = installed;
        }

        public ReleaseInfo Release { get; private set; }

        /// <summary>
        /// The name with its owner when the mod comes from outside the project: a third-party entry
        /// has no shared review behind it, so the list says whose repository it points at.
        /// </summary>
        public string Name
        {
            get
            {
                return Release.Source.IsThirdParty
                    ? Release.Source.DisplayName + " (" + Release.Source.Owner + ")"
                    : Release.Source.DisplayName;
            }
        }
        public string Tag { get { return Release.TagName; } }
        public string Size { get { return Http.Describe(Release.Package.Size); } }
        public string What { get { return Release.Source.Summary; } }

        public string Installed
        {
            get { return _installed; }
            set
            {
                if (_installed == value) return;
                _installed = value;
                var handler = PropertyChanged;
                if (handler != null) handler(this, new PropertyChangedEventArgs("Installed"));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// The Get mods tab: fetch a release from GitHub, or install a dll from a URL.
    /// </summary>
    /// <remarks>
    /// Nothing is installed without a confirmation that names the files, the version read out of each
    /// file, the folder each is going into, and anything it would replace. The version in the URL is not
    /// shown as the version, because the version inside the file is the one that will actually load.
    ///
    /// The dialogs are asynchronous here, which is the one shape that changed from the .NET Framework
    /// build: Avalonia's ShowDialog is a task, so every step that asks a question is awaited rather than
    /// blocking. The network work still runs on a background thread, and the confirmation that follows it
    /// runs back on the UI thread.
    /// </remarks>
    public partial class GetModsView : UserControl
    {
        public MainWindow Host { get; set; }

        private readonly ObservableCollection<ReleaseRow> _rows = new ObservableCollection<ReleaseRow>();
        private volatile bool _busy;

        public GetModsView()
        {
            InitializeComponent();
            List.ItemsSource = _rows;
        }

        /// <summary>
        /// Re-read what is on disk and update the installed column, without going near the network.
        /// </summary>
        /// <remarks>
        /// Called after an install, and after the Mods tab changes something. The column is derived from
        /// the filesystem, so re-reading it is a handful of File.Exists calls - and doing that instead of
        /// re-running the whole lookup is the difference between the row updating immediately and the
        /// user being told a mod they just installed is "not installed" until they switch tabs and back.
        ///
        /// Deliberately does not touch the status line or re-fetch releases: an install has just put a
        /// message there worth reading, and the tag numbers have not changed.
        /// </remarks>
        public void RefreshInstalled()
        {
            foreach (var row in _rows)
            {
                row.Installed = DescribeInstalled(row.Release.Source);
            }
        }

        public void Reload()
        {
            Summary.Text = "Not checked yet.";
            _rows.Clear();
            UpdateButtons();
        }

        public void OnTabShown()
        {
            if (_rows.Count == 0 && !_busy) CheckForUpdates();
        }

        private void UpdateButtons()
        {
            CheckButton.IsEnabled = !_busy;
            InstallButton.IsEnabled = !_busy && List.SelectedItem is ReleaseRow;
            FromUrlButton.IsEnabled = !_busy;
            UrlBox.IsEnabled = !_busy;
        }

        private ReleaseRow Selected
        {
            get { return List.SelectedItem as ReleaseRow; }
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateButtons();
        }

        private void OnCheck(object sender, Avalonia.Interactivity.RoutedEventArgs e) { CheckForUpdates(); }
        private void OnInstall(object sender, Avalonia.Interactivity.RoutedEventArgs e) { InstallSelected(); }
        private void OnFromUrl(object sender, Avalonia.Interactivity.RoutedEventArgs e) { InstallFromUrl(); }

        private void CheckForUpdates()
        {
            if (!Host.Install.HasBepInEx)
            {
                Summary.Text = "No BepInEx install found, so there is nowhere to put a mod. Run the Setup tab first.";
                return;
            }

            SetBusy(true, "Checking GitHub for the latest releases...");

            Run(delegate
            {
                var found = new List<ReleaseInfo>();
                var problems = new List<string>();

                // The live list first; the built-in one when it cannot be reached, with the reason kept
                // so the tab says which list it is showing rather than silently serving a stale one.
                string fallback;
                var sources = ModCatalogue.FetchSources(out fallback);
                var listNote = fallback == null
                    ? "live list"
                    : "built-in list (the live one could not be read: " + fallback + ")";

                foreach (var source in sources)
                {
                    try
                    {
                        found.Add(ModCatalogue.FetchLatest(source));
                    }
                    catch (Exception ex)
                    {
                        // One unreachable repository must not hide the others. Named by owner and repo
                        // rather than display name, so two entries with the same name stay tellable apart.
                        problems.Add(source.QualifiedName + ": " + ex.Message);
                    }
                }

                OnUi(delegate
                {
                    _rows.Clear();

                    foreach (var release in found)
                    {
                        _rows.Add(new ReleaseRow(release, DescribeInstalled(release.Source)));
                    }

                    var report = _rows.Count + " mod(s) checked against GitHub, from the " + listNote + ".";

                    if (problems.Count > 0)
                    {
                        report += " " + problems.Count + " could not be read. " +
                                  string.Join("  ", problems.ToArray());
                    }

                    // Passed to SetBusy rather than assigned separately, so the status line is told what
                    // actually happened. It used to keep saying "Checking GitHub..." after the answer had
                    // arrived, which is the sort of thing that makes a person press the button again.
                    SetBusy(false, report);

                    if (_rows.Count > 0) List.SelectedIndex = 0;

                    UpdateButtons();
                });
            });
        }

        /// <summary>Whether a mod's own file is present, which is not the same as it being current.</summary>
        private string DescribeInstalled(ModSource source)
        {
            var present = 0;
            foreach (var file in source.Files)
            {
                if (File.Exists(Path.Combine(ModCatalogue.FolderFor(Host.Install, file.Folder), file.FileName)))
                {
                    present++;
                }
            }

            if (present == 0) return "not installed";
            return present == source.Files.Length ? "installed" : "partly (" + present + "/" + source.Files.Length + ")";
        }

        private void InstallSelected()
        {
            var row = Selected;
            if (row == null) return;

            var release = row.Release;

            string running;
            if (ModManager.GameRunning(out running))
            {
                ShowInform("Close the game first",
                    "The game is running (" + running + ").\n\nBepInEx keeps plugin files locked until it " +
                    "exits, so close the game first.");
                return;
            }

            var token = NewToken();
            SetBusy(true, "Preparing " + release.Source.DisplayName + "...");

            Run(delegate
            {
                string folder = null;
                try
                {
                    var fetched = ModInstaller.Fetch(release.Package, token, note => Status(note));
                    folder = fetched.WorkingFolder;

                    var plan = ModInstaller.Plan(release, fetched.UnpackedFolder, Host.Install);
                    var work = folder;

                    OnUi(delegate
                    {
                        SetBusy(false, null);
                        ConfirmAndApply(plan, release.Source.DisplayName, work);
                    });
                }
                catch (Exception ex)
                {
                    var message = ex.Message;
                    var work = folder;
                    OnUi(delegate
                    {
                        SetBusy(false, null);
                        if (work != null) ModInstaller.Discard(work);
                        Failed(message);
                    });
                }
            });
        }

        /// <summary>
        /// Show the plan, and install only on an explicit yes.
        /// </summary>
        /// <remarks>
        /// Default is Cancel, and the wording is about what will happen to the disk rather than about
        /// whether the user is sure. Replacing an existing file gets its own confirmation, because that is
        /// the only irreversible part - an install into an empty folder can be undone by deleting a file.
        /// </remarks>
        private async void ConfirmAndApply(InstallPlan plan, string title, string folder)
        {
            try
            {
                var text = ModInstaller.Describe(plan);

                foreach (var warning in plan.Warnings)
                {
                    text += Environment.NewLine + warning + Environment.NewLine;
                }

                if (!await Dialogs.ConfirmAsync("Install " + title + "?", text, "Install"))
                {
                    ModInstaller.Discard(folder);
                    Status("cancelled - nothing was installed");
                    return;
                }

                if (ModInstaller.ReplacesAnything(plan))
                {
                    var replaced = new List<string>();
                    foreach (var step in plan.Steps)
                    {
                        if (step.IsReplacement) replaced.Add(step.File.FileName + " (" + step.ReplacingVersion + ")");
                    }

                    var second = await Dialogs.ConfirmAsync("Replace existing files?",
                        "This replaces " + string.Join(", ", replaced.ToArray()) + "." + Environment.NewLine +
                        Environment.NewLine +
                        "There is no undo and no backup. A replaced file cannot be recovered from here.",
                        "Replace", DialogSeverity.Warning);

                    if (!second)
                    {
                        ModInstaller.Discard(folder);
                        Status("cancelled - nothing was installed");
                        return;
                    }
                }

                try
                {
                    var written = ModInstaller.Apply(plan);
                    Status("installed " + string.Join(", ", written));
                }
                catch (Exception ex)
                {
                    ModInstaller.Discard(folder);
                    Failed(ex.Message);
                    Host.RefreshModViews();
                    return;
                }

                await OfferPostInstall(plan, folder);
            }
            catch (Exception ex)
            {
                ModInstaller.Discard(folder);
                Failed(ex.Message);
            }
        }

        /// <summary>
        /// Discard the download's working folder. It survives until every optional step is answered,
        /// because the scripts run out of it - discarding it with the install would strand them.
        /// </summary>
        private void FinishInstall(string folder)
        {
            if (folder != null) ModInstaller.Discard(folder);
            Host.RefreshModViews();
        }

        /// <summary>
        /// Offer each optional setup step in turn, after the files are installed.
        /// </summary>
        /// <remarks>
        /// One prompt per step, each deniable on its own: a stream mod's ChatBridge setup can be skipped
        /// while another step still runs, and denying everything leaves the installed files exactly as
        /// they were. Nothing here runs without an explicit yes on its own prompt, and the prompt shows
        /// the script before asking.
        ///
        /// A loop rather than the old recursion, because each step is awaited: the next prompt only
        /// appears once the previous answer, and any script it ran, is finished.
        /// </remarks>
        private async Task OfferPostInstall(InstallPlan plan, string folder)
        {
            foreach (var step in plan.Scripts)
            {
                await OfferStep(step);
            }

            FinishInstall(folder);
        }

        private async Task OfferStep(PostInstallStep step)
        {
            if (!await Dialogs.ConfirmSkippableAsync("Run optional setup?", PromptFor(step), "Run",
                                                     DialogSeverity.Warning))
            {
                Status("skipped " + step.ScriptName + " - the installed files are unchanged");
                return;
            }

            string running;
            if (ModManager.GameRunning(out running))
            {
                // Re-checked per step rather than once per install: the game may have been started in
                // between, and a setup script writing at a running game is refused the same way a file
                // move is.
                Host.Status("The game is running (" + running + "), so " + step.ScriptName +
                            " was not run.", false);
                return;
            }

            SetBusy(true, "Running " + step.ScriptName + "...");

            var run = await CollectScript(step);

            SetBusy(false, null);
            await ReportScript(step, run.Lines, run.ExitCode);
        }

        /// <summary>What one script run produced, once it has finished.</summary>
        private sealed class ScriptRun
        {
            public int ExitCode;
            public List<string> Lines;
        }

        /// <summary>
        /// Run a mod's setup script on a background thread and wait for it.
        /// </summary>
        /// <remarks>
        /// A TaskCompletionSource rather than a plain thread and a callback: the caller is an async flow
        /// now, and it has to wait for the exit code before it can offer the next step. The lines are
        /// collected under a lock because the process's two reader threads fan into one callback.
        /// </remarks>
        private async Task<ScriptRun> CollectScript(PostInstallStep step)
        {
            var collected = new List<string>();
            var gate = new object();
            var done = new TaskCompletionSource<int>();

            new Thread(delegate ()
            {
                try
                {
                    ScriptRunner.RunScript(step.ScriptPath, Host.Install.Path,
                        line => { lock (gate) collected.Add(line); },
                        exitCode => done.TrySetResult(exitCode));
                }
                catch (Exception ex)
                {
                    lock (gate) collected.Add("Could not run " + step.ScriptName + ": " + ex.Message);
                    done.TrySetResult(-1);
                }
            })
            {
                IsBackground = true
            }.Start();

            var exitCode = await done.Task;

            List<string> snapshot;
            lock (gate) snapshot = new List<string>(collected);

            return new ScriptRun { ExitCode = exitCode, Lines = snapshot };
        }

        /// <summary>What the prompt shows: what the step does, then the script itself.</summary>
        private static string PromptFor(PostInstallStep step)
        {
            var text = new StringBuilder();

            text.AppendLine(step.Description);
            text.AppendLine();

            var full = step.ScriptText ?? "";
            var lineCount = full.Length == 0 ? 0 : full.Split('\n').Length;

            const int maxChars = 2000;
            var shown = full.Length > maxChars
                ? full.Substring(0, maxChars) + Environment.NewLine + "... (truncated; " +
                  full.Length + " characters in full)"
                : full;

            text.AppendLine("The script (" + step.ScriptName + ", " + lineCount + " lines) reads:");
            text.AppendLine();
            text.AppendLine(shown);
            text.AppendLine();
            text.AppendLine("It runs now, as you, with the game folder passed as -GameDir. " +
                            "Skipping changes nothing already installed.");

            return text.ToString();
        }

        /// <summary>What happened, as a dialog plus a status line.</summary>
        private async Task ReportScript(PostInstallStep step, List<string> lines, int exitCode)
        {
            var tail = lines.Count <= 20 ? lines : lines.GetRange(lines.Count - 20, 20);
            var output = tail.Count == 0
                ? "It printed nothing."
                : string.Join(Environment.NewLine, tail.ToArray());

            if (exitCode == 0)
            {
                Status(step.ScriptName + " finished.");
                await Dialogs.InformAsync(step.ScriptName + " finished", output);
                return;
            }

            Host.Status(step.ScriptName + " reported problems (exit " + exitCode + ").", false);
            await Dialogs.ErrorAsync(step.ScriptName + " reported problems",
                                     "Exit code " + exitCode + "." + Environment.NewLine +
                                     Environment.NewLine + output);
        }

        private void InstallFromUrl()
        {
            var url = (UrlBox.Text ?? "").Trim();

            if (!Http.IsAcceptable(url))
            {
                Failed(Http.ExplainRefusal(url));
                return;
            }

            if (!Host.Install.HasBepInEx)
            {
                Failed("No BepInEx install found, so there is nowhere to put a mod. Run the Setup tab first.");
                return;
            }

            string running;
            if (ModManager.GameRunning(out running))
            {
                Failed("The game is running (" + running + "). Close it first - BepInEx keeps plugin files " +
                       "locked until it exits.");
                return;
            }

            // A guessed file name is the one thing that can be got wrong here, and it decides which folder
            // the file lands in. Guessing from the URL is better than making the user type a path, but it
            // is a guess, so it is shown in the confirmation rather than assumed.
            var fileName = GuessFileName(url);
            if (string.IsNullOrEmpty(fileName))
            {
                Failed("The URL does not end in a file name, so there is nothing to call the download. " +
                       "Use a link to a .dll or a .zip.");
                return;
            }

            var token = NewToken();
            SetBusy(true, "Downloading...");

            Run(delegate
            {
                string folder = null;
                try
                {
                    var fetched = ModInstaller.Fetch(
                        new ReleaseAsset(fileName, 0, url, null),
                        token,
                        note => Status(note));

                    folder = fetched.WorkingFolder;

                    var isZip = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

                    var steps = isZip ? FromZip(fetched.UnpackedFolder) : Single(fetched.UnpackedFolder, fileName);
                    if (steps.Count == 0) throw new IOException("nothing installable was found in the download");

                    var plan = new InstallPlan { Steps = steps };
                    var source = new ModSource("(url)", Path.GetFileNameWithoutExtension(fileName),
                        "Downloaded from " + HostOf(url), new ModFile[0]);
                    plan.Release = new ReleaseInfo(source, "(no version)", null,
                        new ReleaseAsset(fileName, 0, url, fetched.Sha256));

                    var work = folder;
                    OnUi(delegate
                    {
                        SetBusy(false, null);
                        ConfirmUrl(plan, work, HostOf(url), steps[0].Guid == null);
                    });
                }
                catch (Exception ex)
                {
                    var message = ex.Message;
                    var work = folder;
                    OnUi(delegate
                    {
                        SetBusy(false, null);
                        if (work != null) ModInstaller.Discard(work);
                        Failed(message);
                    });
                }
            });
        }

        private List<InstallStep> Single(string folder, string fileName)
        {
            var steps = new List<InstallStep>();
            var path = Path.Combine(folder, fileName);

            if (!File.Exists(path)) throw new IOException("the download did not arrive as " + fileName);

            var entry = PluginScanner.Scan(folder, null).FirstOrDefaultNamed(fileName);
            if (entry == null) throw new IOException(fileName + " could not be read as a .NET assembly");

            steps.Add(new InstallStep
            {
                File = new ModFile(fileName, ModFolder.Plugins, true),
                SourcePath = path,
                DestinationPath = Path.Combine(Host.Install.PluginsFolder, fileName),
                Version = entry.DisplayVersion,
                PluginName = entry.DisplayName,
                Guid = entry.Guid,
                ReplacingVersion = ExistingVersion(fileName)
            });

            return steps;
        }

        private List<InstallStep> FromZip(string folder)
        {
            var steps = new List<InstallStep>();

            foreach (var file in Directory.GetFiles(folder, "*.dll"))
            {
                var name = Path.GetFileName(file);
                var entry = PluginScanner.Scan(folder, null).FirstOrDefaultNamed(name);
                if (entry == null) continue;

                var wantsPlugin = !string.IsNullOrEmpty(entry.Guid);

                steps.Add(new InstallStep
                {
                    File = new ModFile(name, wantsPlugin ? ModFolder.Plugins : ModFolder.Core, wantsPlugin),
                    SourcePath = file,
                    DestinationPath = Path.Combine(
                        ModCatalogue.FolderFor(Host.Install, wantsPlugin ? ModFolder.Plugins : ModFolder.Core), name),
                    Version = entry.DisplayVersion,
                    PluginName = entry.DisplayName,
                    Guid = entry.Guid,
                    ReplacingVersion = ExistingVersion(name)
                });
            }

            return steps;
        }

        private string ExistingVersion(string fileName)
        {
            var folders = new[] { Host.Install.PluginsFolder, Host.Install.BepInExCore };

            foreach (var folder in folders)
            {
                var path = Path.Combine(folder, fileName);
                if (!File.Exists(path)) continue;

                var entry = PluginScanner.Scan(folder, null).FirstOrDefaultNamed(fileName);
                return entry != null && !string.IsNullOrEmpty(entry.DisplayVersion)
                    ? entry.DisplayVersion
                    : "a copy with no version";
            }

            return null;
        }

        /// <summary>
        /// The URL install's confirmation.
        /// </summary>
        /// <remarks>
        /// Separate from the catalogue's because there is no list behind this one. The host is named, the
        /// file's own name and version are shown, and a file with no [BepInPlugin] is called out - it would
        /// be copied into core, which is harmless, but it will also do nothing, and a user who does not
        /// know that will conclude the mod is broken.
        /// </remarks>
        private async void ConfirmUrl(InstallPlan plan, string folder, string host, bool looksInert)
        {
            try
            {
                var text = new StringBuilder();

                text.AppendLine("Install from " + host + "?");
                text.AppendLine();
                text.AppendLine("There is no list behind this URL. It is whatever that address serves, and it");
                text.AppendLine("will be loaded and run by the game on the next launch.");
                text.AppendLine();

                foreach (var step in plan.Steps)
                {
                    // Relative and built with Path.Combine, so the separator matches this platform.
                    var folderName = Path.Combine("BepInEx",
                        step.File.Folder == ModFolder.Core ? "core" : "plugins");
                    var identity = string.IsNullOrEmpty(step.PluginName)
                        ? step.File.FileName
                        : step.PluginName + "  " + step.Version;

                    text.AppendLine("  " + identity);
                    text.AppendLine("    -> " + Path.Combine(folderName, step.File.FileName));

                    if (step.IsReplacement) text.AppendLine("    replaces " + step.ReplacingVersion);
                }

                text.AppendLine();
                text.AppendLine("SHA-256 " + plan.Release.Package.Sha256);

                if (looksInert)
                {
                    text.AppendLine();
                    text.AppendLine("This file has no [BepInPlugin] attribute, so BepInEx will not load it as a");
                    text.AppendLine("mod. It is a library - harmless, and it will do nothing on its own.");
                }

                if (!await Dialogs.ConfirmAsync("Install from " + host + "?", text.ToString(), "Install",
                                                DialogSeverity.Warning))
                {
                    ModInstaller.Discard(folder);
                    Status("cancelled - nothing was installed");
                    return;
                }

                try
                {
                    var written = ModInstaller.Apply(plan);
                    Status("installed " + string.Join(", ", written));
                }
                catch (Exception ex)
                {
                    Failed(ex.Message);
                }
                finally
                {
                    ModInstaller.Discard(folder);
                }

                Host.RefreshModViews();
            }
            catch (Exception ex)
            {
                ModInstaller.Discard(folder);
                Failed(ex.Message);
            }
        }

        private static string GuessFileName(string url)
        {
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)) return null;

            var name = Path.GetFileName(parsed.AbsolutePath);
            if (string.IsNullOrEmpty(name)) return null;

            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return name;
        }

        private static string HostOf(string url)
        {
            Uri parsed;
            return Uri.TryCreate(url, UriKind.Absolute, out parsed) ? parsed.Host : url;
        }

        private static string NewToken()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        /// <summary>
        /// Run work off the UI thread.
        /// </summary>
        /// <remarks>
        /// A download on the UI thread would freeze the window for its duration and look like a hang.
        /// The thread is background so a download in flight cannot keep the process alive on exit.
        /// </remarks>
        private void Run(Action work)
        {
            new Thread(delegate ()
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    OnUi(delegate
                    {
                        SetBusy(false, null);
                        Failed(ex.Message);
                    });
                }
            })
            {
                IsBackground = true
            }.Start();
        }

        private void OnUi(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
                return;
            }

            try
            {
                Dispatcher.UIThread.Post(action);
            }
            catch (Exception)
            {
                // The window closed while a download was in flight. There is nobody left to tell.
            }
        }

        private void SetBusy(bool busy, string message)
        {
            // Marshalled even though every current caller is already on the UI thread. This writes to
            // three controls and the window's enabled state, and it is the first thing a new caller
            // reaches for after a worker completes - so it guards itself rather than relying on each
            // call site remembering.
            OnUi(delegate
            {
                _busy = busy;

                if (message != null) Summary.Text = message;
                if (message != null) Host.Status(message, true);

                UpdateButtons();
                Host.SetBusy(busy);
                Host.Pump();
            });
        }

        private void Status(string message)
        {
            OnUi(delegate
            {
                Summary.Text = message;
                Host.Status(message, true);
            });
        }

        private void Failed(string message)
        {
            // Marshalled for the same reason as SetBusy: a failure is reported from whichever thread
            // the work was started on, and this one ends in a modal dialog.
            OnUi(delegate
            {
                Summary.Text = message;
                Host.Status(message, false);
                ShowError(message);
            });
        }

        /// <summary>
        /// Report a failure without letting the reporting itself become the failure.
        /// </summary>
        /// <remarks>
        /// async void because there is nothing to await here and no caller that could: a dialog is the
        /// end of the failure path. The try/catch is what keeps an exception in the dialog from becoming
        /// an unhandled one on the UI thread, which would take the window down.
        /// </remarks>
        private async void ShowError(string message)
        {
            try
            {
                await Dialogs.ErrorAsync("Could not install", message);
            }
            catch (Exception)
            {
                // Nothing left to show it with.
            }
        }

        private async void ShowInform(string title, string message)
        {
            try
            {
                await Dialogs.InformAsync(title, message);
            }
            catch (Exception)
            {
            }
        }
    }
}
