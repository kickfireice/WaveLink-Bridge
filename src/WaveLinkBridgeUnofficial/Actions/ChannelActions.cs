using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;
using WaveLinkBridgeUnofficial.WaveLink;

namespace WaveLinkBridgeUnofficial.Actions;

internal sealed class SetChannelVolumeAction(WaveLinkClient client, ILogger logger)
	: WaveLinkActionBase(client, logger), IActionDefinition, IDynamicOptionsActionDefinition
{
	public string Id => "set-channel-volume";

	public LocalizedText Name => Strings.Actions.SetChannelVolume.Name();

	public LocalizedText Description => Strings.Actions.SetChannelVolume.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		Dynamic(ChannelParameter, Strings.Fields.Channel.Label(), Strings.Fields.Channel.Description()),
		Dynamic(MixParameter, Strings.Fields.Mix.Label(), Strings.Fields.Mix.Description()),
		ActionParameter.Slider(VolumeParameter, 0, 100,
			label: Strings.Fields.Volume.Label(),
			description: Strings.Fields.Volume.Description(),
			step: 1),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.All;

	public IActionExecutor CreateExecutor() => new Executor(Client, Logger);

	public async Task<DynamicOptionsResult> GetDynamicOptionsAsync(DynamicOptionsContext context, CancellationToken cancellationToken)
	{
		Snapshot snapshot;
		try
		{
			snapshot = await Client.GetProviderSnapshotAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (WaveLinkNotConnectedException)
		{
			return OptionsError(Strings.Errors.WaveLinkNotRunning());
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Channel options could not be loaded.");
			return OptionsError(Strings.Errors.OptionsFailed());
		}

		return context.ParameterName switch
		{
			ChannelParameter => Options(ChannelOptions(snapshot)),
			MixParameter => Options(MixOptions(snapshot, SelectedChannel(context.CurrentParameters, snapshot))),
			_ => OptionsError(Strings.Errors.UnknownParameter(context.ParameterName)),
		};
	}

	private sealed class Executor(WaveLinkClient client, ILogger logger) : WaveLinkActionBase(client, logger), IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			if (context.Parameters.GetValueOrDefault(ChannelParameter) is not string { Length: > 0 } channel)
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Fields.Channel.Label()));
			}

			if (context.Parameters.GetValueOrDefault(MixParameter) is not string { Length: > 0 } mix)
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Fields.Mix.Label()));
			}

			if (!TryGetDouble(context.Parameters.GetValueOrDefault(VolumeParameter), out double volume))
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Fields.Volume.Label()));
			}

			try
			{
				await Client.SetChannelVolumeAsync(channel, mix, volume / 100, context.CancellationToken).ConfigureAwait(false);
				return ActionResult.Success();
			}
			catch (Exception ex)
			{
				return FailSend(ex);
			}
		}
	}
}
internal sealed class AdjustChannelVolumeAction(WaveLinkClient client, ILogger logger)
	: WaveLinkActionBase(client, logger), IActionDefinition, IDynamicOptionsActionDefinition
{
	public string Id => "adjust-channel-volume";

	public LocalizedText Name => Strings.Actions.AdjustChannelVolume.Name();

	public LocalizedText Description => Strings.Actions.AdjustChannelVolume.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		Dynamic(ChannelParameter, Strings.Fields.Channel.Label(), Strings.Fields.Channel.Description()),
		Dynamic(MixParameter, Strings.Fields.Mix.Label(), Strings.Fields.Mix.Description()),
		ActionParameter.Slider(DeltaParameter, -100, 100,
			label: Strings.Fields.Delta.Label(),
			description: Strings.Fields.Delta.Description(),
			step: 1,
			defaultValue: 10),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.All;

	public IActionExecutor CreateExecutor() => new Executor(Client, Logger);

	public async Task<DynamicOptionsResult> GetDynamicOptionsAsync(DynamicOptionsContext context, CancellationToken cancellationToken)
	{
		Snapshot snapshot;
		try
		{
			snapshot = await Client.GetProviderSnapshotAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (WaveLinkNotConnectedException)
		{
			return OptionsError(Strings.Errors.WaveLinkNotRunning());
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Channel options could not be loaded.");
			return OptionsError(Strings.Errors.OptionsFailed());
		}

		return context.ParameterName switch
		{
			ChannelParameter => Options(ChannelOptions(snapshot)),
			MixParameter => Options(MixOptions(snapshot, SelectedChannel(context.CurrentParameters, snapshot))),
			_ => OptionsError(Strings.Errors.UnknownParameter(context.ParameterName)),
		};
	}

	private sealed class Executor(WaveLinkClient client, ILogger logger) : WaveLinkActionBase(client, logger), IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			if (context.Parameters.GetValueOrDefault(ChannelParameter) is not string { Length: > 0 } channel)
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Fields.Channel.Label()));
			}

			if (context.Parameters.GetValueOrDefault(MixParameter) is not string { Length: > 0 } mix)
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Fields.Mix.Label()));
			}

			if (!TryGetDouble(context.Parameters.GetValueOrDefault(DeltaParameter), out double delta) || delta == 0)
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Fields.Delta.Label()));
			}

			try
			{
				await Client.AdjustChannelVolumeAsync(channel, mix, delta / 100, context.CancellationToken).ConfigureAwait(false);
				return ActionResult.Success();
			}
			catch (Exception ex)
			{
				return FailSend(ex);
			}
		}
	}
}

internal sealed class ToggleChannelMuteAction(WaveLinkClient client, ILogger logger)
	: WaveLinkActionBase(client, logger), IActionDefinition, IDynamicOptionsActionDefinition, IStateProviderActionDefinition
{
	public string Id => "toggle-channel-mute";

	public LocalizedText Name => Strings.Actions.ToggleChannelMute.Name();

	public LocalizedText Description => Strings.Actions.ToggleChannelMute.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		Dynamic(ChannelParameter, Strings.Fields.Channel.Label(), Strings.Fields.Channel.Description()),
		Dynamic(MixParameter, Strings.Fields.Mix.Label(), Strings.Fields.Mix.Description()),
		ActionParameter.Choice(MuteModeParameter, MuteModeOptions(includeLeave: false),
			label: Strings.Fields.MuteMode.Label(),
			description: Strings.Fields.MuteMode.Description(),
			defaultValue: "toggle",
			required: true),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.All;

	public TimeSpan StatePollInterval => TimeSpan.FromSeconds(2);

	public IActionExecutor CreateExecutor() => new Executor(Client, Logger);

	/// <summary>Answers from cache only: never connects, never throws on partial configuration.
	/// A throw here would fail the widget holding the button, including on save.</summary>
	public Task<ActionStateSnapshot?> GetActionStateAsync(IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
	{
		try
		{
			if (parameters.GetValueOrDefault(ChannelParameter) is not string { Length: > 0 } channel)
			{
				return Task.FromResult<ActionStateSnapshot?>(null);
			}

			if (parameters.GetValueOrDefault(MixParameter) is not string { Length: > 0 } mix)
			{
				return Task.FromResult<ActionStateSnapshot?>(null);
			}

			string? active = Client.GetMuteStateId(channel, mix);
			if (active is null)
			{
				return Task.FromResult<ActionStateSnapshot?>(null);
			}

			return Task.FromResult<ActionStateSnapshot?>(new ActionStateSnapshot(
				MuteStates(Strings.Actions.ToggleChannelMute.State.Muted(), Strings.Actions.ToggleChannelMute.State.Unmuted()),
				active));
		}
		catch
		{
			return Task.FromResult<ActionStateSnapshot?>(null);
		}
	}

	public async Task<DynamicOptionsResult> GetDynamicOptionsAsync(DynamicOptionsContext context, CancellationToken cancellationToken)
	{
		Snapshot snapshot;
		try
		{
			snapshot = await Client.GetProviderSnapshotAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (WaveLinkNotConnectedException)
		{
			return OptionsError(Strings.Errors.WaveLinkNotRunning());
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Channel options could not be loaded.");
			return OptionsError(Strings.Errors.OptionsFailed());
		}

		return context.ParameterName switch
		{
			ChannelParameter => Options(ChannelOptions(snapshot)),
			MixParameter => Options(MixOptions(snapshot, SelectedChannel(context.CurrentParameters, snapshot))),
			_ => OptionsError(Strings.Errors.UnknownParameter(context.ParameterName)),
		};
	}

	private sealed class Executor(WaveLinkClient client, ILogger logger) : WaveLinkActionBase(client, logger), IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			if (context.Parameters.GetValueOrDefault(ChannelParameter) is not string { Length: > 0 } channel)
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Fields.Channel.Label()));
			}

			if (context.Parameters.GetValueOrDefault(MixParameter) is not string { Length: > 0 } mix)
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Fields.Mix.Label()));
			}

			try
			{
				var mode = ParseMuteMode(context.Parameters.GetValueOrDefault(MuteModeParameter));
				string? before = Client.GetMuteStateId(channel, mix);
				await Client.SetChannelMuteAsync(channel, mix, mode, context.CancellationToken).ConfigureAwait(false);
				return ActionResult.Success(ExpectedMuteStateId(mode, before));
			}
			catch (Exception ex)
			{
				return FailSend(ex);
			}
		}
	}
}
