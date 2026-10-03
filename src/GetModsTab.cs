using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ScamWYF.Launcher
{
    /// <summary>
    /// The Get mods tab: fetch a release from GitHub, or install a dll from a URL.
    /// </summary>
    /// <remarks>
    /// Separate from the Mods tab on purpose. That one is bookkeeping over files already on disk and
    /// never touches the network; this one downloads code that will be executed by the game. Keeping them
    /// apart means the Mods tab's promise - it moves and deletes files, nothing more - stays true, and
    /// the consequences of installing are somewhere you only arrive deliberately.
    ///
    /// Nothing is installed without a confirmation that names the files, the version read out of each
    /// file, the folder each is going into, and anything it would replace. The version in the URL is not
    /// shown as the version, because the version inside the file is the one that will actually load.
    /// </remarks>
    internal sealed class GetModsTab : UserControl
    {
        private readonly App _app;

        private readonly ListView _list = new ListView();
        private readonly Label _summary = new Label();
        private readonly Button _check = new Button();
        private readonly Button _install = new Button();
        private readonly TextBox _url = new TextBox();
        private readonly Button _fromUrl = new Button();

        private readonly List<ReleaseInfo> _releases = new List<ReleaseInfo>();

        private Thread _worker;
        private volatile bool _busy;

        public GetModsTab(App app)
        {
            _app = app;
            Build();
        }

        private void Build()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(12);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _summary.AutoSize = true;
            _summary.Margin = new Padding(0, 0, 0, 8);
            root.Controls.Add(_summary, 0, 0);

            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.GridLines = true;
            _list.Columns.Add("mod", 150);
            _list.Columns.Add("release", 90);
            _list.Columns.Add("size", 80);
            _list.Columns.Add("installed", 110);
            _list.Columns.Add("what it does", 480);
            _list.SelectedIndexChanged += delegate { UpdateButtons(); };
            root.Controls.Add(_list, 0, 1);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };

            _check.Text = "Check for updates";
            _check.AutoSize = true;
            _check.Padding = new Padding(10, 4, 10, 4);
            _check.Click += delegate { CheckForUpdates(); };
            buttons.Controls.Add(_check);

            _install.Text = "Install...";
            _install.AutoSize = true;
            _install.Padding = new Padding(10, 4, 10, 4);
            _install.Margin = new Padding(8, 0, 0, 0);
            _install.Click += delegate { InstallSelected(); };
            buttons.Controls.Add(_install);

            root.Controls.Add(buttons, 0, 2);

            var fromUrl = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };

            var urlLabel = new Label
            {
                Text = "Install a dll from a URL:",
                AutoSize = true,
                Margin = new Padding(0, 6, 8, 0)
            };
            fromUrl.Controls.Add(urlLabel);

            _url.Width = 380;
            _url.Margin = new Padding(0, 3, 8, 0);
            fromUrl.Controls.Add(_url);

            _fromUrl.Text = "Download and install...";
            _fromUrl.AutoSize = true;
            _fromUrl.Padding = new Padding(10, 4, 10, 4);
            _fromUrl.Margin = new Padding(0, 0, 0, 0);
            _fromUrl.Click += delegate { InstallFromUrl(); };
            fromUrl.Controls.Add(_fromUrl);

            root.Controls.Add(fromUrl, 0, 3);

            var warning = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(900, 0),
                ForeColor = Color.FromArgb(176, 96, 0),
                Margin = new Padding(0, 10, 0, 0),
                Text =
                    "Installing a mod puts code in your game folder that BepInEx will load and run on the " +
                    "next launch. Only install what you trust.\n\n" +
                    "Mods are downloaded over https and checked against the SHA-256 GitHub publishes for " +
                    "them. That catches a corrupted download; it is not a signature, and it cannot tell you " +
                    "whether a mod is safe to run.\n\n" +
                    "Changes take effect on the next launch."
            };
            root.Controls.Add(warning, 0, 4);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Controls.Add(root);
            UpdateButtons();
        }

        /// <summary>Ask GitHub what the latest release of each known mod is.</summary>
        public void Reload()
        {
            _summary.Text = "Not checked yet.";
            _releases.Clear();
            _list.Items.Clear();
            UpdateButtons();
        }

        public void OnTabShown()
        {
            if (_releases.Count == 0 && !_busy) CheckForUpdates();
        }

        private void UpdateButtons()
        {
            var ready = !_busy && Selected != null;

            _check.Enabled = !_busy;
            _install.Enabled = ready;
            _fromUrl.Enabled = !_busy;
            _url.Enabled = !_busy;
        }

        private ReleaseInfo Selected
        {
            get
            {
                if (_list.SelectedItems.Count == 0) return null;
                return _list.SelectedItems[0].Tag as ReleaseInfo;
            }
        }

        private void CheckForUpdates()
        {
            if (!_app.Install.CanManageMods)
            {
                _summary.Text = "No BepInEx install found, so there is nowhere to put a mod. Run the Setup tab first.";
                return;
            }

            SetBusy(true, "Checking GitHub for the latest releases...");

            Run(() =>
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

                OnUi(() =>
                {
                    _releases.Clear();
                    _releases.AddRange(found);
                    _list.BeginUpdate();
                    _list.Items.Clear();

                    foreach (var release in _releases)
                    {
                        var item = new ListViewItem(release.Source.DisplayName);
                        item.SubItems.Add(release.TagName);
                        item.SubItems.Add(Http.Describe(release.Package.Size));
                        item.SubItems.Add(DescribeInstalled(release.Source));
                        item.SubItems.Add(release.Source.Summary);
                        item.Tag = release;
                        _list.Items.Add(item);
                    }

                    _list.EndUpdate();

                    _summary.Text = problems.Count == 0
                        ? _releases.Count + " mod(s) checked against GitHub."
                        : _releases.Count + " checked, " + problems.Count + " could not be read. " +
                          string.Join("  ", problems.ToArray());

                    if (_list.Items.Count > 0) _list.Items[0].Selected = true;

                    SetBusy(false, null);
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
                if (File.Exists(Path.Combine(ModCatalogue.FolderFor(_app.Install, file.Folder), file.FileName)))
                {
                    present++;
                }
            }

            if (present == 0) return "not installed";
            return present == source.Files.Length ? "installed" : "partly (" + present + "/" + source.Files.Length + ")";
        }

        private void InstallSelected()
        {
            var release = Selected;
            if (release == null) return;

            if (ModManager.GameRunning(out var running))
            {
                MessageBox.Show(this,
                    "The game is running (" + running + ").\n\nBepInEx keeps plugin files locked until it " +
                    "exits, so close the game first.",
                    "Close the game first", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var token = NewToken();
            SetBusy(true, "Preparing " + release.Source.DisplayName + "...");

            Run(() =>
            {
                string folder = null;
                try
                {
                    var fetched = ModInstaller.Fetch(release.Package, token, note => Status(note));
                    folder = fetched.Key;

                    var plan = ModInstaller.Plan(release, folder, _app.Install);

                    OnUi(() =>
                    {
                        SetBusy(false, null);
                        ConfirmAndApply(plan, release.Source.DisplayName, folder);
                    });
                }
                catch (Exception ex)
                {
                    var message = ex.Message;
                    var work = folder;
                    OnUi(() =>
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

            var answer = MessageBox.Show(this, text,
                "Install " + title + "?",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.OK)
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

                var second = MessageBox.Show(this,
                    "This replaces " + string.Join(", ", replaced.ToArray()) + "." + Environment.NewLine +
                    Environment.NewLine +
                    "There is no undo and no backup. A replaced file cannot be recovered from here.",
                    "Replace existing files?",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

                if (second != DialogResult.OK)
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

            _app.Mods.Rescan();
        }

        private void InstallFromUrl()
        {
            var url = (_url.Text ?? "").Trim();

            if (!Http.IsAcceptable(url))
            {
                Failed(Http.ExplainRefusal(url));
                return;
            }

            if (!_app.Install.CanManageMods)
            {
                Failed("No BepInEx install found, so there is nowhere to put a mod. Run the Setup tab first.");
                return;
            }

            if (ModManager.GameRunning(out var running))
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

            Run(() =>
            {
                string folder = null;
                try
                {
                    var fetched = ModInstaller.Fetch(
                        new ReleaseAsset(fileName, 0, url, null),
                        token,
                        note => Status(note));

                    folder = fetched.Key;

                    var isZip = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

                    var files = isZip ? new[] { new ModFile(fileName, ModFolder.Plugins, true) } : null;

                    var steps = isZip ? FromZip(folder) : Single(folder, fileName);
                    if (steps.Count == 0) throw new IOException("nothing installable was found in the download");

                    var plan = new InstallPlan { Steps = steps };
                    var source = new ModSource("(url)", Path.GetFileNameWithoutExtension(fileName),
                        "Downloaded from " + HostOf(url), new ModFile[0]);
                    plan.Release = new ReleaseInfo(source, "(no version)", null,
                        new ReleaseAsset(fileName, 0, url, fetched.Value));

                    OnUi(() =>
                    {
                        SetBusy(false, null);
                        ConfirmUrl(plan, folder, HostOf(url), steps[0].Guid == null);
                    });
                }
                catch (Exception ex)
                {
                    var message = ex.Message;
                    var work = folder;
                    OnUi(() =>
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
                DestinationPath = Path.Combine(_app.Install.PluginsFolder, fileName),
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
                        ModCatalogue.FolderFor(_app.Install, wantsPlugin ? ModFolder.Plugins : ModFolder.Core), name),
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
            foreach (var folder in new[] { _app.Install.PluginsFolder, _app.Install.BepInExCore })
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

            var answer = MessageBox.Show(this, text.ToString(),
                "Install from " + host + "?",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.OK)
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

            _app.Mods.Rescan();
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
            _worker = new Thread(delegate ()
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    OnUi(() =>
                    {
                        SetBusy(false, null);
                        Failed(ex.Message);
                    });
                }
            })
            {
                IsBackground = true
            };

            _worker.Start();
        }

        private void OnUi(Action action)
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(action);
                }
                catch (Exception)
                {
                    // The window closed while a download was in flight. There is nobody left to tell.
                }
                return;
            }

            action();
        }

        private void SetBusy(bool busy, string message)
        {
            _busy = busy;

            if (message != null) _summary.Text = message;
            if (message != null) _app.Status(message, true);

            UpdateButtons();
            _app.SetBusy(busy);
            _app.Pump();
        }

        private void Status(string message)
        {
            OnUi(() =>
            {
                _summary.Text = message;
                _app.Status(message, true);
            });
        }

        private void Failed(string message)
        {
            _summary.Text = message;
            _app.Status(message, false);
            MessageBox.Show(this, message, "Could not install", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}