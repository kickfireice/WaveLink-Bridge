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
			snapshot = await Client.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
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
	: WaveLinkActionBase(client, logger), IActionDefinition, IDynamicOptionsActionDefinition
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

	public IActionExecutor CreateExecutor() => new Executor(Client, Logger);

	public async Task<DynamicOptionsResult> GetDynamicOptionsAsync(DynamicOptionsContext context, CancellationToken cancellationToken)
	{
		Snapshot snapshot;
		try
		{
			snapshot = await Client.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
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
				await Client.SetMixMuteAsync(mix, ParseMuteMode(context.Parameters.GetValueOrDefault(MuteModeParameter)), context.CancellationToken).ConfigureAwait(false);
				return ActionResult.Success();
			}
			catch (Exception ex)
			{
				return FailSend(ex);
			}
		}
	}
}
