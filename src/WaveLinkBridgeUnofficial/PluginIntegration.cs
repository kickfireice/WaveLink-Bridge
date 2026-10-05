using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Variables;
using Serilog;
using WaveLinkBridgeUnofficial.Actions;
using WaveLinkBridgeUnofficial.WaveLink;

namespace WaveLinkBridgeUnofficial;

/// <summary>
/// The plugin's one integration. It owns the <see cref="WaveLinkClient"/> (cheap to construct: no
/// socket opens until <see cref="InitializeAsync"/>) and declares every action and variable.
/// Eager variables are polled by the host, so there is no push surface in v1.
/// </summary>
public sealed class PluginIntegration : IPluginIntegration, IVariableProvider, IAsyncDisposable
{
	private readonly WaveLinkClient _client;
	private readonly ILogger _logger;

	public PluginIntegration(ILogger logger)
	{
		_logger = logger.ForContext<PluginIntegration>();
		_client = new WaveLinkClient(logger);
		Actions =
		[
			new SetChannelVolumeAction(_client, logger),
			new AdjustChannelVolumeAction(_client, logger),
			new ToggleChannelMuteAction(_client, logger),
			new SetMixVolumeAction(_client, logger),
			new ToggleMixMuteAction(_client, logger),
			new ToggleChannelFxAction(_client, logger),
			new SetInputGainAction(_client, logger),
			new SetOutputAction(_client, logger),
			new ShowStatusAction(_client, logger),
		];
	}

	public IReadOnlyList<IActionDefinition> Actions { get; }

	/// <summary>
	/// Runs once the session is established, and again after a non-resume reconnect or a configuration
	/// change, so it has to be safe to run repeatedly against an already-initialized process.
	/// </summary>
	public Task InitializeAsync(IIntegrationContext context)
	{
		_logger.Information("Initialized.");
		return _client.StartAsync();
	}

	public Task ShutdownAsync() => _client.StopAsync();

	public async ValueTask DisposeAsync() => await _client.DisposeAsync().ConfigureAwait(false);

	public IReadOnlyList<VariableDefinition> Variables { get; } =
	[
		VariableDefinition.Eager("wavelink_connected", VariableType.Boolean) with
		{
			Id = "connected",
			DisplayName = Strings.Variables.Connected.DisplayName(),
			Description = Strings.Variables.Connected.Description(),
		},
		VariableDefinition.Eager("wavelink_version", VariableType.Text) with
		{
			Id = "version",
			DisplayName = Strings.Variables.Version.DisplayName(),
			Description = Strings.Variables.Version.Description(),
		},
		VariableDefinition.Eager("wavelink_channel_count", VariableType.Numeric) with
		{
			Id = "channel-count",
			DisplayName = Strings.Variables.ChannelCount.DisplayName(),
			Description = Strings.Variables.ChannelCount.Description(),
		},
	];

	/// <summary>Accepts every id form defensively: short, name, and <c>vars.</c>-prefixed.</summary>
	public ValueTask<VariableReading> ReadAsync(string localId, CancellationToken cancellationToken = default)
	{
		string id = NormalizeVariableId(localId);
		var (connected, version, channels) = _client.ReadStatus();

		return ValueTask.FromResult(id switch
		{
			"connected" => VariableReading.Of(connected),
			"version" => version is not null ? VariableReading.Of(version) : VariableReading.Unavailable,
			"channel-count" => channels.HasValue ? VariableReading.Of(channels.Value) : VariableReading.Unavailable,
			_ => VariableReading.Unavailable,
		});
	}

	public ValueTask<VariableWriteResult> SetValueAsync(string localId, object? value, CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(VariableWriteResult.NotWritable(Strings.Variables.ReadOnly()));

	private static string NormalizeVariableId(string localId)
	{
		string id = localId.Trim();
		if (id.StartsWith("vars.", StringComparison.OrdinalIgnoreCase))
		{
			id = id["vars.".Length..];
		}

		if (id.StartsWith("wavelink_", StringComparison.OrdinalIgnoreCase))
		{
			id = id["wavelink_".Length..];
		}

		return id.Replace('_', '-').ToLowerInvariant();
	}
}
