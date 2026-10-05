namespace WaveLinkBridgeUnofficial.WaveLink;

/// <summary>How a mute-style action resolves the target state.</summary>
public enum MuteMode
{
	Toggle,
	Mute,
	Unmute,
	Leave,
}

/// <summary>How an effect-style action resolves the target state.</summary>
public enum FxMode
{
	Toggle,
	Enable,
	Disable,
}

/// <summary>One per-mix send of a channel. Wave Link builds disagree on the key (<c>id</c> vs
/// <c>mixId</c>); both are accepted on read and both are sent on write.</summary>
public sealed record ChannelMixState(string MixId, double? Level, bool? IsMuted);

public sealed record EffectState(string Id, string Name, bool IsEnabled);

public sealed record ChannelState(
	string Id,
	string Name,
	string Type,
	double? Level,
	bool? IsMuted,
	IReadOnlyList<ChannelMixState> Mixes,
	IReadOnlyList<EffectState> Effects);

public sealed record MixState(string Id, string Name, double? Level, bool? IsMuted);

public sealed record InputEntry(
	string DeviceId,
	string DeviceName,
	string InputId,
	string InputName,
	bool? IsMuted,
	double? Gain,
	double? MicPcMix,
	IReadOnlyList<EffectState> Effects);

public sealed record OutputEntry(
	string DeviceId,
	string DeviceName,
	string OutputId,
	string OutputName,
	double? Level,
	bool? IsMuted,
	string? MixId);

public sealed record Snapshot(
	IReadOnlyList<ChannelState> Channels,
	IReadOnlyList<MixState> Mixes,
	IReadOnlyList<InputEntry> Inputs,
	IReadOnlyList<OutputEntry> Outputs,
	string? MainOutputDeviceId,
	string? MainOutputId,
	string? AppVersion,
	DateTimeOffset TakenAt);
