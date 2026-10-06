using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;
using WaveLinkBridgeUnofficial.WaveLink;

namespace WaveLinkBridgeUnofficial.Actions;

internal sealed class ToggleChannelFxAction(WaveLinkClient client, ILogger logger)
	: WaveLinkActionBase(client, logger), IActionDefinition, IDynamicOptionsActionDefinition
{
	public string Id => "toggle-channel-fx";

	public LocalizedText Name => Strings.Actions.ToggleChannelFx.Name();

	public LocalizedText Description => Strings.Actions.ToggleChannelFx.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		Dynamic(ChannelParameter, Strings.Fields.Channel.Label(), Strings.Fields.Channel.Description()),
		Dynamic(EffectParameter, Strings.Fields.Effect.Label(), Strings.Fields.Effect.Description()),
		ActionParameter.Choice(FxModeParameter, FxModeOptions(),
			label: Strings.Fields.FxMode.Label(),
			description: Strings.Fields.FxMode.Description(),
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
			snapshot = await Client.GetProviderSnapshotAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (WaveLinkNotConnectedException)
		{
			return OptionsError(Strings.Errors.WaveLinkNotRunning());
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Effect options could not be loaded.");
			return OptionsError(Strings.Errors.OptionsFailed());
		}

		return context.ParameterName switch
		{
			ChannelParameter => Options(ChannelOptions(snapshot)),
			EffectParameter => Options(EffectOptions(SelectedChannel(context.CurrentParameters, snapshot))),
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

			if (context.Parameters.GetValueOrDefault(EffectParameter) is not string { Length: > 0 } effect)
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Fields.Effect.Label()));
			}

			try
			{
				await Client.SetChannelFxAsync(channel, effect, ParseFxMode(context.Parameters.GetValueOrDefault(FxModeParameter)), context.CancellationToken).ConfigureAwait(false);
				return ActionResult.Success();
			}
			catch (Exception ex)
			{
				return FailSend(ex);
			}
		}
	}
}
