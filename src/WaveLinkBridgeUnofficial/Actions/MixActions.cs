using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;
using WaveLinkBridgeUnofficial.WaveLink;

namespace WaveLinkBridgeUnofficial.Actions;

internal sealed class SetMixVolumeAction(WaveLinkClient client, ILogger logger)
	: WaveLinkActionBase(client, logger), IActionDefinition, IDynamicOptionsActionDefinition
{
	public string Id => "set-mix-volume";

	public LocalizedText Name => Strings.Actions.SetMixVolume.Name();

	public LocalizedText Description => Strings.Actions.SetMixVolume.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		Dynamic(MixParameter, Strings.Fields.Mix.Label(), Strings.Fields.Mix.Description()),
		ActionParameter.Slider(LevelParameter, 0, 100,
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
			Logger.Debug(ex, "Mix options could not be loaded.");
			return OptionsError(Strings.Errors.OptionsFailed());
		}

		return context.ParameterName switch
		{
			MixParameter => Options(MixOptions(snapshot, channel: null, includeSpecials: false)),
			_ => OptionsError(Strings.Errors.UnknownParameter(context.ParameterName)),
		};
	}

	private sealed class Executor(WaveLinkClient client, ILogger logger) : WaveLinkActionBase(client, logger), IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			if (context.Parameters.GetValueOrDefault(MixParameter) is not string { Length: > 0 } mix
				|| string.Equals(mix, WaveLinkClient.OverallMix, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(mix, WaveLinkClient.BothMixes, StringComparison.OrdinalIgnoreCase))
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Fields.Mix.Label()));
			}

			if (!TryGetDouble(context.Parameters.GetValueOrDefault(LevelParameter), out double level))
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Fields.Volume.Label()));
			}

			try
			{
				await Client.SetMixVolumeAsync(mix, level / 100, context.CancellationToken).ConfigureAwait(false);
				return ActionResult.Success();
			}
			catch (Exception ex)
			{
				return FailSend(ex);
			}
		}
	}
}

internal sealed class ToggleMixMuteAction(WaveLinkClient client, ILogger logger)
	: WaveLinkActionBase(client, logger), IActionDefinition, IDynamicOptionsActionDefinition, IStateProviderActionDefinition
{
	public string Id => "toggle-mix-mute";

	public LocalizedText Name => Strings.Actions.ToggleMixMute.Name();

	public LocalizedText Description => Strings.Actions.ToggleMixMute.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
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

	/// <summary>Answers from cache only: never connects, never throws. A configured target whose
	/// mix was seen before always gets the full set (active <c>unavailable</c> when there is
	/// nothing live to report); only missing params or a never-seen mix answer null.</summary>
	public Task<ActionStateSnapshot?> GetActionStateAsync(IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
	{
		try
		{
			if (parameters.GetValueOrDefault(MixParameter) is not string { Length: > 0 } mix)
			{
				return Task.FromResult<ActionStateSnapshot?>(null);
			}

			if (!Client.IsMixKnown(mix))
			{
				return Task.FromResult<ActionStateSnapshot?>(null);
			}

			string active = Client.GetMuteStateId(channelRef: null, mix) ?? WaveLinkClient.UnavailableStateId;
			return Task.FromResult<ActionStateSnapshot?>(new ActionStateSnapshot(MuteStates(), active));
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
			Logger.Debug(ex, "Mix options could not be loaded.");
			return OptionsError(Strings.Errors.OptionsFailed());
		}

		return context.ParameterName switch
		{
			MixParameter => Options(MixOptions(snapshot, channel: null, includeSpecials: false)),
			_ => OptionsError(Strings.Errors.UnknownParameter(context.ParameterName)),
		};
	}

	private sealed class Executor(WaveLinkClient client, ILogger logger) : WaveLinkActionBase(client, logger), IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			if (context.Parameters.GetValueOrDefault(MixParameter) is not string { Length: > 0 } mix
				|| string.Equals(mix, WaveLinkClient.OverallMix, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(mix, WaveLinkClient.BothMixes, StringComparison.OrdinalIgnoreCase))
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Fields.Mix.Label()));
			}

			try
			{
				var mode = ParseMuteMode(context.Parameters.GetValueOrDefault(MuteModeParameter));
				await Client.SetMixMuteAsync(mix, mode, context.CancellationToken).ConfigureAwait(false);
				return ActionResult.Success();
			}
			catch (Exception ex)
			{
				return FailSend(ex);
			}
		}
	}
}
