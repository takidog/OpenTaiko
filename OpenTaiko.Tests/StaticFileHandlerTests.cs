using OpenTaiko.RemoteControl;

namespace OpenTaiko.Tests;

public sealed class StaticFileHandlerTests : IDisposable {
	private readonly string directory = Path.Combine(Path.GetTempPath(), $"opentaiko-static-{Guid.NewGuid():N}");

	public StaticFileHandlerTests() {
		Directory.CreateDirectory(this.directory);
		File.WriteAllText(Path.Combine(this.directory, "index.html"), "<h1>OpenTaiko</h1>");
		File.WriteAllText(Path.Combine(this.directory, "app.js"), "console.log('ok')");
	}

	[Fact]
	public void ServesRootAndKnownAssetsWithCorrectTypes() {
		StaticFileHandler handler = new(this.directory);

		ApiResponse root = handler.Route("GET", "/");
		ApiResponse script = handler.Route("GET", "/app.js");

		Assert.Equal(200, root.StatusCode);
		Assert.Equal("text/html; charset=utf-8", root.ContentType);
		Assert.Contains("OpenTaiko", root.BodyText);
		Assert.Equal("text/javascript; charset=utf-8", script.ContentType);
	}

	[Theory]
	[InlineData("/../secret.txt")]
	[InlineData("/%2e%2e/secret.txt")]
	[InlineData("/..%5csecret.txt")]
	[InlineData("/missing.txt")]
	public void RejectsTraversalAndUnknownFiles(string path) {
		Assert.Equal(404, new StaticFileHandler(this.directory).Route("GET", path).StatusCode);
	}

	[Fact]
	public void RejectsMutationMethodsAndSupportsHead() {
		StaticFileHandler handler = new(this.directory);
		Assert.Equal(405, handler.Route("POST", "/index.html").StatusCode);
		ApiResponse head = handler.Route("HEAD", "/index.html");
		Assert.Equal(200, head.StatusCode);
		Assert.Empty(head.Body);
	}

	public void Dispose() => Directory.Delete(this.directory, true);
}
