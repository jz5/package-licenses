using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace PackageLicenses
{
    public static class PackageLicensesUtility
    {
        public static IEnumerable<LocalPackageInfo> GetPackages(string packagesPath, ILogger log = null)
        {
            var emptyList = new List<LocalPackageInfo>();
            var logger = log ?? NullLogger.Instance;
            var type = LocalFolderUtility.GetLocalFeedType(packagesPath, logger);
            switch (type)
            {
                case FeedType.FileSystemV2:
                    return LocalFolderUtility.GetPackagesV2(packagesPath, logger) ?? emptyList;
                case FeedType.FileSystemV3:
                    return LocalFolderUtility.GetPackagesV3(packagesPath, logger) ?? emptyList;
                default:
                    break;
            }
            return emptyList;
        }

        public static async Task<License> GetLicenseAsync(this LocalPackageInfo info, ILogger log = null)
        {
            var licenseUrl = info.Nuspec.GetLicenseUrl();
            if (!string.IsNullOrWhiteSpace(licenseUrl) && Uri.IsWellFormedUriString(licenseUrl, UriKind.Absolute))
            {
                var license = await new Uri(licenseUrl).GetLicenseAsync(log);
                if (license != null) return license;
            }

            var projectUrl = info.Nuspec.GetProjectUrl();
            if (!string.IsNullOrWhiteSpace(projectUrl) && Uri.IsWellFormedUriString(projectUrl, UriKind.Absolute))
            {
                var license = await new Uri(projectUrl).GetLicenseAsync(log);
                if (license != null) return license;
            }
            return null;
        }

        public static IEnumerable<LocalPackageInfo> GetPackagesFromProject(string projectPath, ILogger log = null)
        {
            var globalPackagesFolder = SettingsUtility.GetGlobalPackagesFolder(NullSettings.Instance);
            var list = new List<LocalPackageInfo>();

            var d = XDocument.Load(projectPath);
            var elements = d.Descendants("ItemGroup").Descendants("PackageReference");

            foreach (var element in elements)
            {
                var include = element.Attribute("Include")?.Value;
                var version = element.Attribute("Version")?.Value;

                if (include == null || version == null) continue;

                var path = System.IO.Path.Combine(globalPackagesFolder, include, version, $"{include}.{version}.nupkg");
                if (System.IO.File.Exists(path))
                    list.Add(LocalFolderUtility.GetPackage(new Uri(path), log));
            }

            return list;
        }
    }
}
