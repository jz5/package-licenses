//------------------------------------------------------------------------------
// <copyright file="ListCommand.cs" company="Company">
//     Copyright (c) Company.  All rights reserved.
// </copyright>
//------------------------------------------------------------------------------

using System;
using System.ComponentModel.Design;
using System.Globalization;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using EnvDTE;
using System.IO;
using Microsoft.VisualStudio;
using NuGet.Common;
using System.Collections.Generic;
using NuGet.Protocol;
using ClosedXML.Excel;
using System.Linq;
using System.Text.RegularExpressions;
using EnvDTE80;

namespace PackageLicenses.VisualStudio
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class ListCommand
    {
        /// <summary>
        /// Command ID.
        /// </summary>
        public const int CommandId = 0x0100;

        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CommandSet = new Guid("ac075e0a-fd7a-4bf9-8a24-62f9cc5fa86e");

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly Package _package;

        private readonly MenuCommand _menuCommand;

        /// <summary>
        /// Initializes a new instance of the <see cref="ListCommand"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        private ListCommand(Package package)
        {
            _package = package ?? throw new ArgumentNullException("package");

            var commandService = ServiceProvider.GetService(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
            {
                var menuCommandID = new CommandID(CommandSet, CommandId);
                _menuCommand = new MenuCommand(MenuItemCallback, menuCommandID);
                commandService.AddCommand(_menuCommand);
            }

            Logger.Instance.Write = text => ServiceProvider.WriteOnOutputWindow(text);
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static ListCommand Instance { get; private set; }

        /// <summary>
        /// Gets the service provider from the owner package.
        /// </summary>
        private IServiceProvider ServiceProvider => _package;

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static void Initialize(Package package)
        {
            Instance = new ListCommand(package);
        }

        /// <summary>
        /// This function is the callback used to execute the command when the menu item is clicked.
        /// See the constructor to see how the menu item is associated with this function using
        /// OleMenuCommandService service and MenuCommand class.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private async void MenuItemCallback(object sender, EventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = (DTE)ServiceProvider.GetService(typeof(DTE));
            if (dte == null)
                return;

            try
            {
                var solutionDir = Path.GetDirectoryName(dte.Solution.FullName);
                var projectDirs = new List<string>();

                var item = dte.Solution.Projects.GetEnumerator();
                while (item.MoveNext())
                {
                    if (!(item.Current is Project project) || project.Kind == ProjectKinds.vsProjectKindSolutionFolder)
                        continue;

                    if (Directory.Exists(project.FullName))
                        projectDirs.Add(project.FullName);
                }

                _menuCommand.Enabled = false;

                // Create temporary folder
                var tempPath = Path.Combine(Path.GetTempPath(), $"{Path.GetFileName(solutionDir)}-{DateTime.Now.ToFileTimeUtc()}");
                Directory.CreateDirectory(tempPath);

                // List
                var result = await TryListAsync(solutionDir, projectDirs, tempPath);

                // Open folder
                if (result)
                    System.Diagnostics.Process.Start(tempPath);
                else
                    Directory.Delete(tempPath, true);
            }
            catch (Exception ex)
            {
                ServiceProvider.WriteOnOutputWindow($"ERROR: {ex.Message}\n");
            }
            finally
            {
                _menuCommand.Enabled = true;
            }
        }

        private readonly List<string> _headers = new List<string> { "Id", "Version", "Authors", "Title", "ProjectUrl", "LicenseUrl", "RequireLicenseAcceptance", "Copyright", "Inferred License ID", "Inferred License Name", "Downloaded license text file" };

        private async System.Threading.Tasks.Task<bool> TryListAsync(string solutionPath, List<string> projectPaths, string saveFolderPath)
        {
            var root = Path.Combine(solutionPath, "packages");
            var hasPackagesFolder = true;
            if (!Directory.Exists(root))
            {
                ServiceProvider.WriteOnOutputWindow($"Not Found: '{root}'\n");
                hasPackagesFolder = false;
            }
            if (hasPackagesFolder)
            {
                ServiceProvider.WriteOnOutputWindow($"Packages Path: '{root}'\n");
            }

            // Get GitHub Client ID and Client Secret
            var query = Environment.GetEnvironmentVariable("PACKAGE-LICENSES-GITHUB-QUERY", EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(query))
            {
                var m = Regex.Match(query, "client_id=(?<id>.*?)&client_secret=(?<secret>.*)");
                if (m.Success)
                {
                    LicenseUtility.ClientId = m.Groups["id"].Value;
                    LicenseUtility.ClientSecret = m.Groups["secret"].Value;
                }
            }

            // Get packages
            var packages = hasPackagesFolder ? 
                PackageLicensesUtility.GetPackages(root, Logger.Instance).ToList() : 
                new List<LocalPackageInfo>();

            // Get packages from projects
            var projectPackages = projectPaths.SelectMany(i => PackageLicensesUtility.GetPackagesFromProject(i, Logger.Instance)).ToList();
            packages.AddRange(projectPackages);

            if (!packages.Any())
            {
                ServiceProvider.WriteOnOutputWindow("No Packages\n");
                return false;
            }

            // Output metadata and get licenses
            var headers = string.Join("\t", _headers.Take(_headers.Count - 1));
            var dividers = string.Join("\t", _headers.Take(_headers.Count - 1).Select(i => new string('-', i.Length)));
            ServiceProvider.WriteOnOutputWindow($"\n{headers}\n{dividers}\n");

            var list = new List<(LocalPackageInfo, License)>();

            foreach (var p in LocalFolderUtility.GetDistinctPackages(packages))
            {
                var nuspec = p.Nuspec;
                var license = await p.GetLicenseAsync(Logger.Instance);
                ServiceProvider.WriteOnOutputWindow($"{nuspec.GetId()}\t{nuspec.GetVersion()}\t{nuspec.GetAuthors()}\t{nuspec.GetTitle()}\t{nuspec.GetProjectUrl()}\t{nuspec.GetLicenseUrl()}\t{nuspec.GetRequireLicenseAcceptance()}\t{nuspec.GetCopyright()}\t{license?.Id}\t{license?.Name}\n");

                list.Add((p, license));
            }
            ServiceProvider.WriteOnOutputWindow("\n");

            // Save to files
            CreateFiles(list, saveFolderPath);
            ServiceProvider.WriteOnOutputWindow($"Saved to '{saveFolderPath}'\n");

            return true;
        }

        private void CreateFiles(List<(LocalPackageInfo, License)> list, string saveFolderPath)
        {
            var book = new XLWorkbook();
            var sheet = book.Worksheets.Add("Packages");

            // header
            for (var i = 0; i < _headers.Count; i++)
            {
                sheet.Cell(1, 1 + i).SetValue(_headers[i]).Style.Font.SetBold();
            }

            // values
            var row = 2;
            foreach (var (p, l) in list)
            {
                var nuspec = p.Nuspec;

                sheet.Cell(row, 1).SetValue(nuspec.GetId() ?? "");
                sheet.Cell(row, 2).SetValue($"{nuspec.GetVersion()}");
                sheet.Cell(row, 3).SetValue(nuspec.GetAuthors() ?? "");
                sheet.Cell(row, 4).SetValue(nuspec.GetTitle() ?? "");
                sheet.Cell(row, 5).SetValue(nuspec.GetProjectUrl() ?? "");
                sheet.Cell(row, 6).SetValue(nuspec.GetLicenseUrl() ?? "");
                sheet.Cell(row, 7).SetValue($"{nuspec.GetRequireLicenseAcceptance()}");
                sheet.Cell(row, 8).SetValue(nuspec.GetCopyright() ?? "");
                sheet.Cell(row, 9).SetValue(l?.Id ?? "");
                sheet.Cell(row, 10).SetValue(l?.Name ?? "");

                // save license text file
                if (!string.IsNullOrEmpty(l?.Text))
                {
                    string filename;
                    if (l.IsMaster)
                    {
                        filename = $"{l.Id}.txt";
                    }
                    else
                    {
                        if (l.DownloadUri != null)
                            filename = l.DownloadUri.PathAndQuery.Substring(1).Replace("/", "-").Replace("?", "-") + ".txt";
                        else
                            filename = $"{nuspec.GetId()}.{nuspec.GetVersion()}.txt";
                    }

                    var path = Path.Combine(saveFolderPath, filename);
                    if (!File.Exists(path))
                        File.WriteAllText(path, l.Text, System.Text.Encoding.UTF8);

                    // set filename to cell 
                    sheet.Cell(row, 11).SetValue(filename);
                }

                ++row;
            }

            book.SaveAs(Path.Combine(saveFolderPath, "Licenses.xlsx"));
        }
    }

    internal class Logger : ILogger
    {
        private static Logger _instance;

        public static Logger Instance => _instance ?? (_instance = new Logger());

        public Action<string> Write { get; set; }

        private Logger()
        {
        }

        public void Log(LogLevel level, string data) { }
        public void Log(ILogMessage message) { }
        public System.Threading.Tasks.Task LogAsync(LogLevel level, string data) => System.Threading.Tasks.Task.FromResult(0);
        public System.Threading.Tasks.Task LogAsync(ILogMessage message) => System.Threading.Tasks.Task.FromResult(0);
        public void LogDebug(string data) => Write?.Invoke($"DEBUG: {data}\n");
        public void LogError(string data) => Write?.Invoke($"ERROR: {data}\n");
        public void LogInformation(string data) => Write?.Invoke($"INFORMATION: {data}\n");
        public void LogInformationSummary(string data) => Write?.Invoke($"SUMMARY: {data}\n");
        public void LogMinimal(string data) => Write?.Invoke($"MINIMAL: {data}\n");
        public void LogVerbose(string data) => Write?.Invoke($"VERBOSE: {data}\n");
        public void LogWarning(string data) => Write?.Invoke($"WARNING: {data}\n");
    }
}
