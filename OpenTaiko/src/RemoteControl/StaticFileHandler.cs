namespace OpenTaiko.RemoteControl;

internal sealed class StaticFileHandler {
	private readonly string rootDirectory;
	private readonly string rootPrefix;

	public StaticFileHandler(string rootDirectory) {
		this.rootDirectory = Path.GetFullPath(rootDirectory);
		this.rootPrefix = this.rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
			+ Path.DirectorySeparatorChar;
	}

	public ApiResponse Route(string method, string path) {
		bool head = method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
		if (!head && !method.Equals("GET", StringComparison.OrdinalIgnoreCase)) {
			return new ApiResponse(405, "text/plain; charset=utf-8", "Method Not Allowed"u8.ToArray());
		}

		string decoded;
		try {
			decoded = Uri.UnescapeDataString(path.Split('?', 2)[0]).Replace('\\', '/');
		} catch (UriFormatException) {
			return NotFound();
		}
		string[] segments = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Any(segment => segment is "." or "..")) return NotFound();

		string relative = segments.Length == 0 ? "index.html" : Path.Combine(segments);
		string fullPath = Path.GetFullPath(Path.Combine(this.rootDirectory, relative));
		if (!fullPath.StartsWith(this.rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) {
			return NotFound();
		}

		byte[] body = head ? Array.Empty<byte>() : File.ReadAllBytes(fullPath);
		return new ApiResponse(200, ContentTypeFor(fullPath), body);
	}

	private static ApiResponse NotFound()
		=> new(404, "text/plain; charset=utf-8", "Not Found"u8.ToArray());

	private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
		".html" => "text/html; charset=utf-8",
		".css" => "text/css; charset=utf-8",
		".js" => "text/javascript; charset=utf-8",
		".json" => "application/json; charset=utf-8",
		".svg" => "image/svg+xml",
		".png" => "image/png",
		".ico" => "image/x-icon",
		_ => "application/octet-stream",
	};
}
