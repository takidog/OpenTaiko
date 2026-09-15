using System.Reflection;
using System.Text;

namespace OpenTaiko.Tests;

public sealed class ConfigIniTests {
	[Fact]
	public void DefaultsPreserveUpstreamNetworkBehavior() {
		CConfigIni config = new();

		Assert.True(config.EnableNetworkConnectivityCheck);
		Assert.True(config.EnableDiscordRpc);
		Assert.False(config.SkipTitleScreen);
		Assert.False(config.RemoteControlEnabled);
		Assert.Equal("127.0.0.1", config.RemoteControlHost);
		Assert.Equal(100, config.PlayHistoryMaxEntries);
	}

	[Fact]
	public void NewSectionsLoadAndValidateValues() {
		CConfigIni config = new();
		Load(config, """
			[Startup]
			SkipTitleScreen=1
			DefaultSaveSlot=4
			DefaultPlayerSide=right
			[Online]
			EnableNetworkConnectivityCheck=0
			EnableDiscordRpc=0
			[RemoteControl]
			Enabled=1
			Host=localhost
			Port=4567
			ServeWebUI=0
			OpenWebUIOnStartup=1
			[PlayHistory]
			Enabled=0
			MaxEntries=42
			""");

		Assert.True(config.SkipTitleScreen);
		Assert.Equal(4, config.DefaultSaveSlot);
		Assert.Equal("Right", config.DefaultPlayerSide);
		Assert.False(config.EnableNetworkConnectivityCheck);
		Assert.False(config.EnableDiscordRpc);
		Assert.True(config.RemoteControlEnabled);
		Assert.Equal("127.0.0.1", config.RemoteControlHost);
		Assert.Equal(4567, config.RemoteControlPort);
		Assert.False(config.ServeWebUI);
		Assert.True(config.OpenWebUIOnStartup);
		Assert.False(config.PlayHistoryEnabled);
		Assert.Equal(42, config.PlayHistoryMaxEntries);
	}

	[Fact]
	public void InvalidValuesKeepSafeDefaults() {
		CConfigIni config = new();
		Load(config, """
			[Startup]
			DefaultSaveSlot=99
			DefaultPlayerSide=middle
			[RemoteControl]
			Host=0.0.0.0
			Port=0
			[PlayHistory]
			MaxEntries=101
			""");

		Assert.Equal(1, config.DefaultSaveSlot);
		Assert.Equal("Left", config.DefaultPlayerSide);
		Assert.Equal("127.0.0.1", config.RemoteControlHost);
		Assert.Equal(2354, config.RemoteControlPort);
		Assert.Equal(100, config.PlayHistoryMaxEntries);
	}

	[Fact]
	public void NewSettingsRoundTripThroughConfigFile() {
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		string path = Path.Combine(Path.GetTempPath(), $"opentaiko-config-{Guid.NewGuid():N}.ini");
		try {
			CConfigIni original = new() {
				SkipTitleScreen = true,
				DefaultSaveSlot = 3,
				DefaultPlayerSide = "Right",
				EnableNetworkConnectivityCheck = false,
				EnableDiscordRpc = false,
				RemoteControlEnabled = true,
				RemoteControlPort = 4321,
				ServeWebUI = false,
				OpenWebUIOnStartup = true,
				PlayHistoryEnabled = false,
				PlayHistoryMaxEntries = 25,
			};
			original.t書き出し(path);

			CConfigIni loaded = new(path);

			Assert.True(loaded.SkipTitleScreen);
			Assert.Equal(3, loaded.DefaultSaveSlot);
			Assert.Equal("Right", loaded.DefaultPlayerSide);
			Assert.False(loaded.EnableNetworkConnectivityCheck);
			Assert.False(loaded.EnableDiscordRpc);
			Assert.True(loaded.RemoteControlEnabled);
			Assert.Equal(4321, loaded.RemoteControlPort);
			Assert.False(loaded.ServeWebUI);
			Assert.True(loaded.OpenWebUIOnStartup);
			Assert.False(loaded.PlayHistoryEnabled);
			Assert.Equal(25, loaded.PlayHistoryMaxEntries);
		} finally {
			File.Delete(path);
		}
	}

	private static void Load(CConfigIni config, string ini) {
		MethodInfo method = typeof(CConfigIni).GetMethod("LoadFromString", BindingFlags.Instance | BindingFlags.NonPublic)!;
		method.Invoke(config, new object[] { ini });
	}
}
