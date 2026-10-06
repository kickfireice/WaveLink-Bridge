using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using Serilog;
using WaveLinkBridgeUnofficial.WaveLink;

namespace WaveLinkBridgeUnofficial.Actions;

/// <summary>Shared parameter shapes, option building and error mapping for every Wave Link action.</summary>
internal abstract class WaveLinkActionBase(WaveLinkClient client, ILogger logger)
{
	protected const string ChannelParameter = "channel";
	protected const string MixParameter = "mix";
	protected const string VolumeParameter = "volume";
	protected const string DeltaParameter = "delta";
	protected const string MuteModeParameter = "muteMode";
	protected const string EffectParameter = "effect";
	protected const string FxModeParameter = "fxMode";
	protected const string InputParameter = "input";
	protected const string GainParameter = "gain";
	protected const string OutputParameter = "output";
	protected const string LevelParameter = "level";
	protected const string SetAsMainParameter = "setAsMain";

	protected WaveLinkClient Client { get; } = client;

	protected ILogger Logger { get; } = logger;

	/// <summary>Dropdowns omit the source id and are routed by parameter name (host fills them via
	/// <see cref="IDynamicOptionsActionDefinition"/>). Custom values stay allowed so buttons remain
	/// hand-configurable with raw ids.</summary>
	protected static ActionParameter Dynamic(string name, LocalizedText label, LocalizedText description, bool required = true)
	{
		var seed = ActionParameter.DynamicChoice(name, label, description, required: required);
		return new ActionParameter
		{
			Name = seed.Name,
			Type = seed.Type,
			Label = seed.Label,
			Description = seed.Description,
			Placeholder = seed.Placeholder,
			DefaultValue = seed.DefaultValue,
			Required = seed.Required,
			DynamicOptions = true,
		};
	}

	protected static DynamicOptionsResult Options(IReadOnlyList<ActionParameterOption> options) => new()
	{
		Options = options,
		AllowsCustomValue = true,
		CacheSeconds = 5,
	};

	protected static DynamicOptionsResult OptionsError(LocalizedText reason) => new()
	{
		Options = [],
		Error = reason,
	};

	protected static IReadOnlyList<ActionParameterOption> ChannelOptions(Snapshot snapshot) =>
		[.. snapshot.Channels
			.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
			.Select(c => new ActionParameterOption
			{
				Value = c.Id,
				Label = ChannelLabel(c),
			})];

	protected static IReadOnlyList<ActionParameterOption> MixOptions(Snapshot snapshot, ChannelState? channel, bool includeSpecials = true)
	{
		var options = new List<ActionParameterOption>();
		if (includeSpecials)
		{
			options.Add(new ActionParameterOption { Value = WaveLinkClient.OverallMix, Label = Strings.Fields.Mix.Overall() });
			options.Add(new ActionParameterOption { Value = WaveLinkClient.BothMixes, Label = Strings.Fields.Mix.Both() });
		}

		IEnumerable<ChannelMixState> mixes = channel?.Mixes ?? [];
		if (channel is null)
		{
			mixes = snapshot.Mixes.Select(m => new ChannelMixState(m.Id, m.Level, m.IsMuted));
		}

		options.AddRange(mixes.Select(m => new ActionParameterOption
		{
			Value = m.MixId,
			Label = MixName(snapshot, m.MixId),
		}));
		return options;
	}

	protected static IReadOnlyList<ActionParameterOption> EffectOptions(ChannelState? channel) =>
		channel is null
			? []
			: [.. channel.Effects
				.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
				.Select(e => new ActionParameterOption
				{
					Value = e.Id,
					Label = e.Name + (e.IsEnabled ? "" : Strings.Fields.Effect.DisabledSuffix()),
				})];

	protected static IReadOnlyList<ActionParameterOption> InputOptions(Snapshot snapshot) =>
		[.. snapshot.Inputs
			.OrderBy(i => i.DeviceName, StringComparer.OrdinalIgnoreCase)
			.Select(i => new ActionParameterOption
			{
				Value = i.DeviceId + "/" + i.InputId,
				Label = i.DeviceName + " / " + i.InputName,
			})];

	protected static IReadOnlyList<ActionParameterOption> OutputOptions(Snapshot snapshot) =>
		[.. snapshot.Outputs
			.OrderBy(o => o.DeviceName, StringComparer.OrdinalIgnoreCase)
			.Select(o => new ActionParameterOption
			{
				Value = o.DeviceId + "/" + o.OutputId,
				Label = o.DeviceName + " / " + o.OutputName,
			})];

	protected static IReadOnlyList<ActionParameterOption> MuteModeOptions(bool includeLeave)
	{
		var options = new List<ActionParameterOption>
		{
			new() { Value = "toggle", Label = Strings.Fields.MuteMode.Toggle() },
			new() { Value = "mute", Label = Strings.Fields.MuteMode.Mute() },
			new() { Value = "unmute", Label = Strings.Fields.MuteMode.Unmute() },
		};

		if (includeLeave)
		{
			options.Add(new ActionParameterOption { Value = "leave", Label = Strings.Fields.MuteMode.Leave() });
		}

		return options;
	}

	protected static IReadOnlyList<ActionParameterOption> FxModeOptions() =>
	[
		new() { Value = "toggle", Label = Strings.Fields.FxMode.Toggle() },
		new() { Value = "enable", Label = Strings.Fields.FxMode.Enable() },
		new() { Value = "disable", Label = Strings.Fields.FxMode.Disable() },
	];

	protected static ChannelState? SelectedChannel(IReadOnlyDictionary<string, object?> parameters, Snapshot snapshot)
	{
		if (parameters.GetValueOrDefault(ChannelParameter) is not string { Length: > 0 } channelRef)
		{
			return null;
		}

		return snapshot.Channels.FirstOrDefault(c =>
				string.Equals(c.Id, channelRef, StringComparison.OrdinalIgnoreCase))
			?? snapshot.Channels.FirstOrDefault(c =>
				string.Equals(c.Name, channelRef, StringComparison.OrdinalIgnoreCase));
	}

	protected static string MixName(Snapshot snapshot, string mixId) =>
		snapshot.Mixes.FirstOrDefault(m => string.Equals(m.Id, mixId, StringComparison.OrdinalIgnoreCase))?.Name ?? mixId;

	private static string ChannelLabel(ChannelState channel) =>
		channel.Name == channel.Id ? channel.Name : $"{channel.Name} ({channel.Type})";

	protected static bool TryGetDouble(object? value, out double number)
	{
		switch (value)
		{
			case double d:
				number = d;
				return true;
			case float f:
				number = f;
				return true;
			case int i:
				number = i;
				return true;
			case long l:
				number = l;
				return true;
			case decimal m:
				number = (double)m;
				return true;
			case string s when TryParseNumber(s, out double parsed):
				number = parsed;
				return true;
			default:
				number = 0;
				return false;
		}
	}

	private static bool TryParseNumber(string text, out double number)
	{
		string cleaned = text.Trim();
		if (cleaned.EndsWith('%'))
		{
			cleaned = cleaned[..^1].Trim();
		}

		return double.TryParse(cleaned, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out number);
	}

	protected static MuteMode ParseMuteMode(object? value) => (value as string) switch
	{
		"mute" => MuteMode.Mute,
		"unmute" => MuteMode.Unmute,
		"leave" => MuteMode.Leave,
		_ => MuteMode.Toggle,
	};

	protected static FxMode ParseFxMode(object? value) => (value as string) switch
	{
		"enable" => FxMode.Enable,
		"disable" => FxMode.Disable,
		_ => FxMode.Toggle,
	};

	protected static ActionResult FailNotFound(WaveLinkNotFoundException ex) => ex.Kind switch
	{
		"channel" => ActionResult.Failed(ActionErrorCodes.NotFound, Strings.Errors.ChannelNotFound(ex.Value)),
		"mix" => ActionResult.Failed(ActionErrorCodes.NotFound, Strings.Errors.MixNotFound(ex.Value)),
		"effect" => ActionResult.Failed(ActionErrorCodes.NotFound, Strings.Errors.EffectNotFound(ex.Value)),
		"input" => ActionResult.Failed(ActionErrorCodes.NotFound, Strings.Errors.InputNotFound(ex.Value)),
		"output" => ActionResult.Failed(ActionErrorCodes.NotFound, Strings.Errors.OutputNotFound(ex.Value)),
		_ => ActionResult.Failed(ActionErrorCodes.NotFound, Strings.Errors.NotFound(ex.Value)),
	};

	protected ActionResult FailSend(Exception ex) => ex switch
	{
		OperationCanceledException => throw ex,
		WaveLinkNotConnectedException => ActionResult.Failed(ActionErrorCodes.NotConnected, Strings.Errors.NotConnected()),
		WaveLinkNotFoundException notFound => FailNotFound(notFound),
		TimeoutException => ActionResult.Failed(ActionErrorCodes.Timeout, Strings.Errors.TimedOut()),
		_ => FailUnexpected(ex),
	};

	private ActionResult FailUnexpected(Exception ex)
	{
		Logger.Warning(ex, "A Wave Link command failed unexpectedly.");
		return ActionResult.Failed(ActionErrorCodes.ProviderError, Strings.Errors.CommandFailed());
	}
}
