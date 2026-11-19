using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// The picture behind the panel's widgets (Settings › Display). The settings page scales it down in the browser and
/// uploads it; it is kept in a "backgrounds" folder beside the settings file under an id taken from its hash, which is
/// what <see cref="DisplayConfig.BackgroundImage"/> holds. Pictures are only ever replaced by a new id, so the phone
/// may cache one for good, and an upload the user never saves does not change what the phone shows.
/// </summary>
public static partial class BackgroundImages
{
    /// <summary>The settings page sends at most 1920 px on the long edge; this leaves room for a PNG.</summary>
    internal const int MaxBytes = 8 * 1024 * 1024;

    [GeneratedRegex("^[0-9a-f]{16}$")]
    private static partial Regex IdPattern();

    public static bool IsId(string? id) => id is not null && IdPattern().IsMatch(id);

    public static string Folder(ConfigStore cfg) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cfg.Path)) ?? AppPaths.ConfigDir, "backgrounds");

    /// <summary>Stores a JPEG, PNG or WebP and returns its id, or an error for anything else.</summary>
    public static (string? Id, string? Error) Save(ConfigStore cfg, byte[] bytes)
    {
        if (bytes.Length == 0) return (null, "the picture is empty");
        if (bytes.Length > MaxBytes) return (null, "the picture is larger than 8 MB");
        // GIF is refused: an animated one would keep the phone decoding frames behind every page
        if (PhotoFrame.MimeOf(bytes) is not ("image/jpeg" or "image/png" or "image/webp"))
            return (null, "only JPEG, PNG and WebP pictures can be used");
        var id = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
        var dir = Folder(cfg);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, id);
        if (!File.Exists(path))
        {
            // write beside it and move, so the phone never reads half a picture
            var tmp = path + ".tmp";
            using (var f = AppPaths.CreatePrivate(tmp)) f.Write(bytes);
            File.Move(tmp, path, overwrite: true);
        }
        Prune(cfg, keep: id);
        return (id, null);
    }

    /// <summary>The picture and its type, or null when the id is unknown.</summary>
    public static (byte[] Bytes, string Mime)? Get(ConfigStore cfg, string id)
    {
        if (!IsId(id)) return null;
        var path = Path.Combine(Folder(cfg), id);
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        return PhotoFrame.MimeOf(bytes) is { } mime ? (bytes, mime) : null;
    }

    /// <summary>
    /// Removes every picture except the saved one and <paramref name="keep"/> (an upload waiting for Save), so trying
    /// a few pictures does not pile them up.
    /// </summary>
    public static void Prune(ConfigStore cfg, string? keep = null)
    {
        var dir = Folder(cfg);
        if (!Directory.Exists(dir)) return;
        var saved = cfg.Get().Display.BackgroundImage;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (name == saved || name == keep) continue;
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
