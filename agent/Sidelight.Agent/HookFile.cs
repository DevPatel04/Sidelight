using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sidelight.Agent;

/// <summary>Reads and rewrites another tool's JSON hook file without losing the user's own entries.</summary>
internal static class HookFile
{
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static JsonObject? Read(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path), documentOptions: Lenient) as JsonObject; }
        catch { return null; }
    }

    /// <summary>
    /// Applies <paramref name="change"/> and writes the file atomically, keeping a backup of the old one. A missing
    /// file is only created when <paramref name="create"/> is set (removing a hook from nothing changes nothing).
    /// Returns an error message, or null.
    /// </summary>
    public static string? Update(string path, bool create, Action<JsonObject> change)
    {
        try
        {
            JsonObject root;
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                root = JsonNode.Parse(text, documentOptions: Lenient) as JsonObject
                       ?? throw new InvalidDataException(Path.GetFileName(path) + " is not a JSON object");
                File.WriteAllText(path + ".sidelight.bak", text);
            }
            else if (!create) return null;
            else root = new JsonObject();

            change(root);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            File.Move(tmp, path, overwrite: true);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
