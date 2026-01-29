namespace DevProjex.Kernel.Models;

/// <summary>
/// Describes how the current project was loaded.
/// </summary>
public enum ProjectSourceType
{
    /// <summary>
    /// Project opened from local folder via File → Open.
    /// Git features (Branch, Get updates) are disabled.
    /// </summary>
    LocalFolder,

    /// <summary>
    /// Project cloned via Git CLI into the repo cache.
    /// Git features (Branch, Get updates) are enabled.
    /// </summary>
    GitClone,

    /// <summary>
    /// Project downloaded as ZIP archive (fallback when Git is not available).
    /// Git features (Branch, Get updates) are disabled.
    /// </summary>
    ZipDownload
}
