using System.Net;
using System.Net.Sockets;
using OpenTaiko.RemoteControl;

namespace OpenTaiko.Tests;

public sealed class HttpEventReporterApiTests {
	[Fact]
	public void LanWildcardUsesHttpListenerAllInterfacePrefix() {
		Assert.Equal("http://+:2354/", HttpEventReporter.GetListenerPrefix("0.0.0.0", 2354));
		Assert.Equal("http://127.0.0.1:2354/", HttpEventReporter.GetListenerPrefix("127.0.0.1", 2354));
	}

	[Theory]
	[InlineData("127.0.0.1")]
	[InlineData("0.0.0.0")]
	public async Task ListenerServesApiResponsesOverLoopback(string host) {
		string historyPath = Path.Combine(Path.GetTempPath(), $"opentaiko-http-{Guid.NewGuid():N}.json");
		int port = GetAvailablePort();
		GameRemoteControlApi api = new("transport-test", new RemoteCommandQueue(), new PlayHistoryService(historyPath));
		HttpEventReporter reporter = new(host, port, new ApiRouter(api));
		try {
			reporter.StartListening();
			Assert.True(reporter.started);
			using HttpClient client = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

			HttpResponseMessage response = await client.GetAsync("/api/v1/health");
			string json = await response.Content.ReadAsStringAsync();

			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
			Assert.Contains("transport-test", json);
		} finally {
			reporter.StopListening();
			File.Delete(historyPath);
		}
	}

	private static int GetAvailablePort() {
		TcpListener listener = new(IPAddress.Loopback, 0);
		listener.Start();
		try {
			return ((IPEndPoint)listener.LocalEndpoint).Port;
		} finally {
			listener.Stop();
		}
	}
}
