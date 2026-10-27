using System;
using System.Collections.Generic;
using System.Text;

namespace Sitim.Core.Options
{
    /// <summary>
    /// Local disk storage settings (MVP).
    /// </summary>
    public sealed class StorageOptions
    {
        /// <summary>
        /// Root folder where SITIM stores generated files (archives, AI results, reports).
        /// Relative paths are resolved from the application working directory.
        /// </summary>
        public string BasePath { get; set; } = "./storage";
    }
}
