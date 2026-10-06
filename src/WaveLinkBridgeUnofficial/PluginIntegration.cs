using System.Globalization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Variables;
using Serilog;
using WaveLinkBridgeUnofficial.Actions;
using WaveLinkBridgeUnofficial.WaveLink;

namespace WaveLinkBridgeUnofficial;

/// <summary>
/// The plugin's one integration. It owns the <see cref="WaveLinkClient"/> (cheap to construct: no
/// socket opens until <see cref="InitializeAsync"/>) and declares every action, the eager variables,
/// and a per-channel volume catalog so Slider widgets bind two-way.
/// Eager variables are polled by the host; catalog values are pushed while bound.
/// </summary>
public sealed class PluginIntegration : IPluginIntegration, IVariableProvider, IAsyncDisposable
{
	private readonly WaveLinkClient _client;
	private readonly ILogger _logger;
	private readonly object _sync = new();
	private IVariableSink? _sink;
	private HashSet<string> _pushedChannelSet = new(StringComparer.OrdinalIgnoreCase);
	private long _pushBatches;
	private long _pushValues;
	private DateTimeOffset? _lastPushAt;

	private string GetPushDiagnostics()
	{
		long batches, values;
		DateTimeOffset? at;
		lock (_sync)
		{
			batches = _pushBatches;
			values = _pushValues;
			at = _lastPushAt;
		}

		string line = "Pushes: " + batches.ToString(CultureInfo.InvariantCulture)
			+ " (" + values.ToString(CultureInfo.InvariantCulture) + " values)";
		if (at.HasValue)
		{
			var age = DateTimeOffset.UtcNow - at.Value;
			line += ", last " + (age.TotalSeconds < 60
				? ((int)age.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s ago"
				: ((int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m ago");
		}
		else
		{
			line += ", none yet";
		}

		return line;
	}

	public PluginIntegration(ILogger logger)
	{
		_logger = logger.ForContext<PluginIntegration>();
		_client = new WaveLinkClient(logger);
		_client.SnapshotChanged += OnWaveLinkChanged;
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
			new ShowStatusAction(_client, logger, GetPushDiagnostics),
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

	public async ValueTask DisposeAsync()
	{
		_client.SnapshotChanged -= OnWaveLinkChanged;
		await _client.DisposeAsync().ConfigureAwait(false);
		_pushGate.Dispose();
	}

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

	public bool SupportsCatalog => true;

	public bool SupportsPush => true;

	public bool SupportsSearch => true;

	public string CatalogName => "WaveLink";

	/// <summary>Accepts every id form defensively: short, name, and <c>vars.</c>-prefixed. Channel
	/// catalog ids are Wave Link channel ids and read the overall volume in percent.</summary>
	public ValueTask<VariableReading> ReadAsync(string localId, CancellationToken cancellationToken = default)
	{
		string id = NormalizeVariableId(localId);
		var (connected, version, channels) = _client.ReadStatus();

		VariableReading reading = id switch
		{
			"connected" => VariableReading.Of(connected),
			"version" => version is not null ? VariableReading.Of(version) : VariableReading.Unavailable,
			"channel-count" => channels.HasValue ? VariableReading.Of(channels.Value) : VariableReading.Unavailable,
			_ => ChannelVolumeReading(localId),
		};
		return ValueTask.FromResult(reading);
	}

	public async ValueTask<VariableWriteResult> SetValueAsync(string localId, object? value, CancellationToken cancellationToken = default)
	{
		// Only catalog volumes declare Write; the host refuses anything else before it reaches here.
		// A bare channel id addresses the overall volume; channel|mix addresses one mix.
		await EnsureChannelsKnownAsync(cancellationToken).ConfigureAwait(false);
		var (channelId, mixId) = SplitCatalogId(localId);
		if (!_client.IsChannelKnown(channelId))
		{
			return VariableWriteResult.NotFound(Strings.Variables.ChannelVolume.NotFound(localId));
		}

		if (!TryGetPercent(value, out double percent))
		{
			return VariableWriteResult.InvalidValue(Strings.Variables.ChannelVolume.InvalidValue());
		}

		try
		{
			await _client.SetChannelVolumeAsync(channelId, mixId ?? WaveLinkClient.OverallMix, percent / 100, cancellationToken).ConfigureAwait(false);
			return VariableWriteResult.Applied();
		}
		catch (WaveLinkNotConnectedException)
		{
			return VariableWriteResult.Unavailable(Strings.Errors.NotConnected());
		}
		catch (WaveLinkNotFoundException)
		{
			return VariableWriteResult.NotFound(Strings.Variables.ChannelVolume.NotFound(localId));
		}
		catch (Exception ex) when (ex is TimeoutException or WaveLinkRpcException)
		{
			_logger.Debug(ex, "A channel volume write failed transiently.");
			return VariableWriteResult.Unavailable(Strings.Errors.TimedOut());
		}
	}

	public async ValueTask<VariableCatalogPage> DiscoverAsync(VariableCatalogQuery query, CancellationToken cancellationToken = default)
	{
		if (query.ParentId is not null)
		{
			return VariableCatalogPage.Empty;
		}

		await EnsureChannelsKnownAsync(cancellationToken).ConfigureAwait(false);
		var names = ChannelVariableNames();
		var items = names
			.Where(entry => MatchesSearch(entry.Value.VariableName, query.Search) || MatchesSearch(entry.Value.Display, query.Search))
			.Select(entry => ChannelVolumeDefinition(entry.Key, entry.Value))
			.ToList();

		const int defaultPageSize = 50;
		int pageSize = query.PageSize > 0 ? query.PageSize : defaultPageSize;
		int offset = 0;
		if (query.ContinuationToken is not null)
		{
			int.TryParse(query.ContinuationToken, NumberStyles.None, CultureInfo.InvariantCulture, out offset);
		}

		var page = items.Skip(offset).Take(pageSize).ToList();
		string? continuation = offset + page.Count < items.Count
			? (offset + page.Count).ToString(CultureInfo.InvariantCulture)
			: null;
		return new VariableCatalogPage { Items = page, ContinuationToken = continuation };
	}

	/// <summary>Null only for an id that never named a channel. A channel that is merely gone right
	/// now still resolves, so the binding reads unavailable and resumes on its own.</summary>
	public async ValueTask<VariableDefinition?> ResolveAsync(string localId, CancellationToken cancellationToken = default)
	{
		await EnsureChannelsKnownAsync(cancellationToken).ConfigureAwait(false);
		var names = ChannelVariableNames();
		return names.TryGetValue(localId, out CatalogEntry? entry) ? ChannelVolumeDefinition(localId, entry) : null;
	}

	/// <summary>Cold-start guard: the channel map fills on the first refresh, which races catalog
	/// browsing. One live fetch when we know nothing yet, on a short grace so probes and browsers
	/// get an answer instead of a timeout; a genuinely unknown id still answers fast.</summary>
	private async Task EnsureChannelsKnownAsync(CancellationToken cancellationToken)
	{
		if (_client.KnownChannels().Count > 0)
		{
			return;
		}

		try
		{
			await _client.GetSnapshotAsync(cancellationToken, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_logger.Debug(ex, "Channel discovery ran before Wave Link was reachable; answering from nothing known.");
		}
	}

	/// <summary>Called with the complete set of bound ids every time it changes. Answers with the
	/// values already at hand. Pushes go to every known id regardless (ids nobody bound are
	/// dropped by the host), so a missing or late subscribe can never silence live updates.</summary>
	public ValueTask<IReadOnlyList<VariableValue>> SubscribeAsync(IReadOnlyCollection<string> localIds, CancellationToken cancellationToken = default)
	{
		var values = localIds
			.Select(id => new VariableValue { Id = id, Reading = ChannelVolumeReading(id) })
			.ToList();
		return ValueTask.FromResult<IReadOnlyList<VariableValue>>(values);
	}

	public Task OnAttachedAsync(IVariableSink sink, CancellationToken cancellationToken = default)
	{
		lock (_sync)
		{
			_sink = sink;
		}

		return Task.CompletedTask;
	}

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

	private VariableReading ChannelVolumeReading(string catalogId)
	{
		var (channelId, mixId) = SplitCatalogId(catalogId);
		double? percent = mixId is null
			? _client.TryGetChannelVolumePercent(channelId)
			: _client.TryGetChannelMixVolumePercent(channelId, mixId);
		return percent.HasValue
			? VariableReading.Of(percent.Value, 0, 100, 1)
			: VariableReading.Unavailable;
	}

	private static (string ChannelId, string? MixId) SplitCatalogId(string localId)
	{
		int separator = localId.LastIndexOf('|');
		return separator < 0 ? (localId, null) : (localId[..separator], localId[(separator + 1)..]);
	}

	private static bool TryGetPercent(object? value, out double percent)
	{
		switch (value)
		{
			case double d:
				percent = d;
				return true;
			case float f:
				percent = f;
				return true;
			case int i:
				percent = i;
				return true;
			case long l:
				percent = l;
				return true;
			default:
				percent = 0;
				return false;
		}
	}

	/// <summary>Deterministic variable names over the known channels, so Discover and Resolve agree.
	/// Each channel contributes its overall volume plus one entry per mix, so a slider can bind the
	/// exact cell shown in the Wave Link UI. Display names use channel and mix names, never ids.</summary>
	private Dictionary<string, CatalogEntry> ChannelVariableNames()
	{
		var candidates = new List<(string Id, string Base, string Display)>();
		foreach (var channel in _client.KnownChannels().OrderBy(c => c.Id, StringComparer.Ordinal))
		{
			string channelPart = SanitizeName(channel.Name);
			candidates.Add((channel.Id, "wavelink_vol_" + channelPart, channel.Name + " volume"));
			foreach (var mixId in channel.MixIds.OrderBy(m => m, StringComparer.Ordinal))
			{
				string mixName = _client.KnownMixName(mixId);
				candidates.Add((channel.Id + "|" + mixId,
					"wavelink_vol_" + channelPart + "_" + SanitizeName(mixName),
					channel.Name + " volume on " + mixName));
			}
		}

		var names = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
		var taken = new HashSet<string>(StringComparer.Ordinal);
		foreach (var (id, candidate, display) in candidates)
		{
			string name = candidate;
			int suffix = 2;
			while (!taken.Add(name))
			{
				name = candidate + "_" + suffix.ToString(CultureInfo.InvariantCulture);
				suffix++;
			}

			names[id] = new CatalogEntry(name, display);
		}

		return names;
	}

	private sealed record CatalogEntry(string VariableName, string Display);

	private static string SanitizeName(string channelName)
	{
		var builder = new System.Text.StringBuilder(channelName.Length);
		foreach (char c in channelName.ToLowerInvariant())
		{
			builder.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_');
		}

		string sanitized = builder.ToString().Trim('_');
		return sanitized.Length > 0 ? sanitized : "channel";
	}

	private static bool MatchesSearch(string variableName, string? search) =>
		string.IsNullOrWhiteSpace(search) ||
		variableName.Contains(search, StringComparison.OrdinalIgnoreCase);

	private static VariableDefinition ChannelVolumeDefinition(string channelId, CatalogEntry entry) =>
		VariableDefinition.OnDemand(channelId, VariableType.Numeric) with
		{
			Name = entry.VariableName,
			DisplayName = entry.Display,
			Description = Strings.Variables.ChannelVolume.Blurb(),
			Unit = "%",
			SemanticKind = VariableSemanticKinds.Percentage,
			Write = new VariableWriteCapability(),
		};

	private void OnWaveLinkChanged()
	{
		// Fire-and-forget into a serialized queue: overlapping publishes race, and a stale
		// one finishing last is exactly how a fast drag ends up stuck on an old value.
		_ = PushSerialAsync();
	}

	private readonly SemaphoreSlim _pushGate = new(1, 1);

	private async Task PushSerialAsync()
	{
		bool entered;
		try
		{
			entered = await _pushGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		}
		catch (ObjectDisposedException)
		{
			return;
		}

		if (!entered)
		{
			_logger.Debug("A channel volume push was dropped under backlog; the next change or poll corrects it.");
			return;
		}

		try
		{
			IVariableSink? sink;
			lock (_sync)
			{
				sink = _sink;
			}

			if (sink is null)
			{
				return;
			}

			// Every known catalog id, not just the subscribed set: values for ids nobody bound
			// are dropped by the host, while gating on a subscribe call that may never come (or
			// come in another id form) means no push ever fires and sliders only follow on poll.
			string[] ids = ChannelVariableNames().Keys.ToArray();
			await PushChannelValuesAsync(sink, ids).ConfigureAwait(false);
		}
		finally
		{
			try
			{
				_pushGate.Release();
			}
			catch (ObjectDisposedException)
			{
			}
		}
	}

	private async Task PushChannelValuesAsync(IVariableSink sink, string[] ids)
	{
		try
		{
			var values = ids
				.Select(id => new VariableValue { Id = id, Reading = ChannelVolumeReading(id) })
				.ToList();
			await sink.PublishAsync(values, CancellationToken.None).ConfigureAwait(false);

			lock (_sync)
			{
				_pushBatches++;
				_pushValues += values.Count;
				_lastPushAt = DateTimeOffset.UtcNow;
			}

			var current = new HashSet<string>(_client.KnownChannels().Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
			bool membershipChanged;
			lock (_sync)
			{
				membershipChanged = !current.SetEquals(_pushedChannelSet);
				_pushedChannelSet = current;
			}

			if (membershipChanged)
			{
				await sink.InvalidateCatalogAsync().ConfigureAwait(false);
			}
		}
		catch (Exception ex)
		{
			_logger.Debug(ex, "A channel volume push failed and was dropped.");
		}
	}
}
