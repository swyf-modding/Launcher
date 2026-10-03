using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace ScamWYF.Launcher.Views
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
        public string Name { get { return Release.Source.DisplayName; } }
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

        private void OnCheck(object sender, RoutedEventArgs e) { CheckForUpdates(); }
        private void OnInstall(object sender, RoutedEventArgs e) { InstallSelected(); }
        private void OnFromUrl(object sender, RoutedEventArgs e) { InstallFromUrl(); }

        private void CheckForUpdates()
        {
            if (!Host.Install.CanManageMods)
            {
                Summary.Text = "No BepInEx install found, so there is nowhere to put a mod. Run the Setup tab first.";
                return;
            }

            SetBusy(true, "Checking GitHub for the latest releases...");

            Run(delegate
            {
                var found = new List<ReleaseInfo>();
                var problems = new List<string>();

                foreach (var source in ModCatalogue.Mods)
                {
                    try
                    {
                        found.Add(ModCatalogue.FetchLatest(source));
                    }
                    catch (Exception ex)
                    {
                        // One unreachable repository must not hide the other two.
                        problems.Add(source.DisplayName + ": " + ex.Message);
                    }
                }

                OnUi(delegate
                {
                    _rows.Clear();

                    foreach (var release in found)
                    {
                        _rows.Add(new ReleaseRow(release, DescribeInstalled(release.Source)));
                    }

                    var report = problems.Count == 0
                        ? _rows.Count + " mod(s) checked against GitHub."
                        : _rows.Count + " checked, " + problems.Count + " could not be read. " +
                          string.Join("  ", problems.ToArray());

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
                Dialogs.Inform("Close the game first",
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

                    OnUi(delegate
                    {
                        SetBusy(false, null);
                        ConfirmAndApply(plan, release.Source.DisplayName, folder);
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
        private void ConfirmAndApply(InstallPlan plan, string title, string folder)
        {
            var text = ModInstaller.Describe(plan);

            foreach (var warning in plan.Warnings)
            {
                text += Environment.NewLine + warning + Environment.NewLine;
            }

            if (!Dialogs.Confirm("Install " + title + "?", text, "Install"))
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

                var second = Dialogs.Confirm("Replace existing files?",
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
                Failed(ex.Message);
            }
            finally
            {
                ModInstaller.Discard(folder);
            }

            Host.RefreshModViews();
        }

        private void InstallFromUrl()
        {
            var url = (UrlBox.Text ?? "").Trim();

            if (!Http.IsAcceptable(url))
            {
                Failed(Http.ExplainRefusal(url));
                return;
            }

            if (!Host.Install.CanManageMods)
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

                    OnUi(delegate
                    {
                        SetBusy(false, null);
                        ConfirmUrl(plan, folder, HostOf(url), steps[0].Guid == null);
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
        /// be copied into core\, which is harmless, but it will also do nothing, and a user who does not
        /// know that will conclude the mod is broken.
        /// </remarks>
        private void ConfirmUrl(InstallPlan plan, string folder, string host, bool looksInert)
        {
            var text = new StringBuilder();

            text.AppendLine("Install from " + host + "?");
            text.AppendLine();
            text.AppendLine("There is no list behind this URL. It is whatever that address serves, and it");
            text.AppendLine("will be loaded and run by the game on the next launch.");
            text.AppendLine();

            foreach (var step in plan.Steps)
            {
                var folderName = step.File.Folder == ModFolder.Core ? "BepInEx\\core" : "BepInEx\\plugins";
                var identity = string.IsNullOrEmpty(step.PluginName)
                    ? step.File.FileName
                    : step.PluginName + "  " + step.Version;

                text.AppendLine("  " + identity);
                text.AppendLine("    -> " + folderName + "\\" + step.File.FileName);

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

            if (!Dialogs.Confirm("Install from " + host + "?", text.ToString(), "Install", DialogSeverity.Warning))
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
            if (Dispatcher.CheckAccess())
            {
                action();
                return;
            }

            try
            {
                Dispatcher.BeginInvoke(action);
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
                Dialogs.Error("Could not install", message);
            });
        }
    }
}