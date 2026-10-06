using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;
using WaveLinkBridgeUnofficial.WaveLink;

namespace WaveLinkBridgeUnofficial.Actions;

internal sealed class SetInputGainAction(WaveLinkClient client, ILogger logger)
	: WaveLinkActionBase(client, logger), IActionDefinition, IDynamicOptionsActionDefinition
{
	public string Id => "set-input-gain";

	public LocalizedText Name => Strings.Actions.SetInputGain.Name();

	public LocalizedText Description => Strings.Actions.SetInputGain.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		Dynamic(InputParameter, Strings.Fields.Input.Label(), Strings.Fields.Input.Description()),
		ActionParameter.Slider(GainParameter, 0, 100,
			label: Strings.Fields.Gain.Label(),
			description: Strings.Fields.Gain.Description(),
			step: 1),
		ActionParameter.Choice(MuteModeParameter, MuteModeOptions(includeLeave: true),
			label: Strings.Fields.MuteMode.Label(),
			description: Strings.Fields.MuteMode.Description(),
			defaultValue: "leave",
			required: true),
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
			Logger.Debug(ex, "Input options could not be loaded.");
			return OptionsError(Strings.Errors.OptionsFailed());
		}

		return context.ParameterName switch
		{
			InputParameter => Options(InputOptions(snapshot)),
			_ => OptionsError(Strings.Errors.UnknownParameter(context.ParameterName)),
		};
	}

	private sealed class Executor(WaveLinkClient client, ILogger logger) : WaveLinkActionBase(client, logger), IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			if (context.Parameters.GetValueOrDefault(InputParameter) is not string { Length: > 0 } inputRef)
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Fields.Input.Label()));
			}

			var separator = inputRef.IndexOf('/');
			if (separator < 0)
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.InvalidValue(Strings.Fields.Input.Label()));
			}

			string deviceId = inputRef[..separator];
			string inputId = inputRef[(separator + 1)..];

			double? gain = TryGetDouble(context.Parameters.GetValueOrDefault(GainParameter), out double parsed)
				? parsed / 100
				: null;
			var mute = ParseMuteMode(context.Parameters.GetValueOrDefault(MuteModeParameter));

			if (gain is null && mute == MuteMode.Leave)
			{
				return ActionResult.Success();
			}

			try
			{
				await Client.SetInputGainAsync(deviceId, inputId, gain, mute, context.CancellationToken).ConfigureAwait(false);
				return ActionResult.Success();
			}
			catch (Exception ex)
			{
				return FailSend(ex);
			}
		}
	}
}

internal sealed class SetOutputAction(WaveLinkClient client, ILogger logger)
	: WaveLinkActionBase(client, logger), IActionDefinition, IDynamicOptionsActionDefinition
{
	public string Id => "set-output";

	public LocalizedText Name => Strings.Actions.SetOutput.Name();

	public LocalizedText Description => Strings.Actions.SetOutput.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		Dynamic(OutputParameter, Strings.Fields.Output.Label(), Strings.Fields.Output.Description()),
		ActionParameter.Slider(LevelParameter, 0, 100,
			label: Strings.Fields.Volume.Label(),
			description: Strings.Fields.Volume.Description(),
			step: 1),
		ActionParameter.Choice(MuteModeParameter, MuteModeOptions(includeLeave: true),
			label: Strings.Fields.MuteMode.Label(),
			description: Strings.Fields.MuteMode.Description(),
			defaultValue: "leave",
			required: true),
		ActionParameter.Toggle(SetAsMainParameter,
			label: Strings.Fields.SetAsMain.Label(),
			description: Strings.Fields.SetAsMain.Description(),
			defaultValue: false),
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
			Logger.Debug(ex, "Output options could not be loaded.");
			return OptionsError(Strings.Errors.OptionsFailed());
		}

		return context.ParameterName switch
		{
			OutputParameter => Options(OutputOptions(snapshot)),
			_ => OptionsError(Strings.Errors.UnknownParameter(context.ParameterName)),
		};
	}

	private sealed class Executor(WaveLinkClient client, ILogger logger) : WaveLinkActionBase(client, logger), IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			if (context.Parameters.GetValueOrDefault(OutputParameter) is not string { Length: > 0 } outputRef)
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Fields.Output.Label()));
			}

			var separator = outputRef.IndexOf('/');
			if (separator < 0)
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.InvalidValue(Strings.Fields.Output.Label()));
			}

			string deviceId = outputRef[..separator];
			string outputId = outputRef[(separator + 1)..];

			double? level = TryGetDouble(context.Parameters.GetValueOrDefault(LevelParameter), out double parsed)
				? parsed / 100
				: null;
			var mute = ParseMuteMode(context.Parameters.GetValueOrDefault(MuteModeParameter));
			bool setAsMain = context.Parameters.GetValueOrDefault(SetAsMainParameter) is true;

			if (level is null && mute == MuteMode.Leave && !setAsMain)
			{
				return ActionResult.Success();
			}

			try
			{
				await Client.SetOutputAsync(deviceId, outputId, level, mute, setAsMain, context.CancellationToken).ConfigureAwait(false);
				return ActionResult.Success();
			}
			catch (Exception ex)
			{
				return FailSend(ex);
			}
		}
	}
}

internal sealed class ShowStatusAction(WaveLinkClient client, ILogger logger)
	: WaveLinkActionBase(client, logger), IActionDefinition
{
	public string Id => "show-status";

	public LocalizedText Name => Strings.Actions.ShowStatus.Name();

	public LocalizedText Description => Strings.Actions.ShowStatus.Description();

	public IReadOnlyList<ActionParameter> Parameters => [];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.All;

	public IActionExecutor CreateExecutor() => new Executor(Client, Logger);

	private sealed class Executor(WaveLinkClient client, ILogger logger) : WaveLinkActionBase(client, logger), IActionExecutor
	{
		public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			string status = Client.GetStatusText();
			Logger.Information("{Status}", status);
			return Task.FromResult(ActionResult.Accepted(status));
		}
	}
}
