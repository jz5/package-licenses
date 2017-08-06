using NuGet.Common;
using NuGet.Protocol;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PackageLicenses
{
    public static class PackageLicensesUtility
    {
        public static IEnumerable<LocalPackageInfo> GetPackages(string packagesPath, ILogger log = null)
        {
            var logger = log ?? NullLogger.Instance;
            var type = LocalFolderUtility.GetLocalFeedType(packagesPath, logger);
            switch (type)
            {
                case FeedType.FileSystemV2:
                    return LocalFolderUtility.GetPackagesV2(packagesPath, logger);
                case FeedType.FileSystemV3:
                    return LocalFolderUtility.GetPackagesV3(packagesPath, logger);
                default:
                    break;
            }
            return new List<LocalPackageInfo>();
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
    }
}
