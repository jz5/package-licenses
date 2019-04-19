using ClosedXML.Excel;
using NuGet.Common;
using NuGet.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PackageLicenses.Sample
{
    class Logger : ILogger
    {
        public void Log(LogLevel level, string data) => $"{level.ToString().ToUpper()}: {data}".Dump();
        public void Log(ILogMessage message) => Task.FromResult(0);
        public Task LogAsync(LogLevel level, string data) => Task.FromResult(0);
        public Task LogAsync(ILogMessage message) => throw new NotImplementedException();
        public void LogDebug(string data) => $"DEBUG: {data}".Dump();
        public void LogError(string data) => $"ERROR: {data}".Dump();
        public void LogInformation(string data) => $"INFORMATION: {data}".Dump();
        public void LogInformationSummary(string data) => $"SUMMARY: {data}".Dump();
        public void LogMinimal(string data) => $"MINIMAL: {data}".Dump();
        public void LogVerbose(string data) => $"VERBOSE: {data}".Dump();
        public void LogWarning(string data) => $"WARNING: {data}".Dump();
    }

    static class LogExtension
    {
        public static void Dump(this string value) => Console.WriteLine(value);
    }

    class Program
    {
        static void Main(string[] args)
        {
            Console.Write("NuGet packages path or project path:");
            var path = Console.ReadLine();

            var isProjectPath = false;
            if (File.Exists(path))
            {
                isProjectPath = true;
            }
            else if (Directory.Exists(path))
            {
                isProjectPath = false;
            }
            else
            {
                Console.Write("Not Found.");
                return;
            }

            var log = new Logger();

            //LicenseUtility.ClientId = "xxx";
            //LicenseUtility.ClientSecret = "xxx";

            var packages = isProjectPath ?
                PackageLicensesUtility.GetPackagesFromProject(path, log) :
                PackageLicensesUtility.GetPackages(path, log);

            var list = new List<(LocalPackageInfo, License)>();
            var t = Task.Run(async () =>
            {
                foreach (var p in packages)
                {
                    Console.WriteLine($"{p.Nuspec.GetId()}.{p.Nuspec.GetVersion()}");
                    var license = await p.GetLicenseAsync(log);
                    list.Add((p, license));
                }
            });
            t.Wait();

            if (!list.Any())
            {
                Console.WriteLine("No Packages.");
                return;
            }

            CreateWorkbook(list);
            Console.WriteLine("Completed.");
        }

        private static void CreateWorkbook(List<(LocalPackageInfo, License)> list)
        {
            var book = new XLWorkbook();
            var sheet = book.Worksheets.Add("Packages");

            // header
            var headers = new[] { "Id", "Version", "Authors", "Title", "ProjectUrl", "LicenseUrl", "RequireLicenseAcceptance", "Copyright", "Inferred License ID", "Inferred License Name" };
            for (var i = 0; i < headers.Length; i++)
            {
                sheet.Cell(1, 1 + i).SetValue(headers[i]).Style.Font.SetBold();
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

                ++row;
            }

            book.SaveAs("Licenses.xlsx");
        }
    }
}
