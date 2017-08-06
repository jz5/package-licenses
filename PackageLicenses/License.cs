using System;

namespace PackageLicenses
{
    public class License
    {
        /// <summary>
        /// SPDX identifier
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// License full name
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// License text
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// Whether or not Text is from SPDX master file
        /// </summary>
        public bool IsMaster { get; set; }
        public Uri DownloadUri { get; set; }

        public License()
        {
        }

        public License(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public License Clone() => (License)MemberwiseClone();
    }
}
