using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace WaveLinkBridgeUnofficial.WaveLink;

/// <summary>Raised when Wave Link cannot be reached (not running, restarting, wrong machine).</summary>
public sealed class WaveLinkNotConnectedException(string message) : Exception(message);

/// <summary>Raised when Wave Link answers a call with a JSON-RPC error.</summary>
public sealed class WaveLinkRpcException(int code, string message) : Exception(message)
{
	public int Code { get; } = code;
}

/// <summary>Raised when a channel, mix, effect, input or output reference matches nothing.
/// Carries a stable kind so actions can localize the message.</summary>
public sealed class WaveLinkNotFoundException(string kind, string value)
	: Exception($"{kind} '{value}' was not found.")
{
	public string Kind { get; } = kind;

	public string Value { get; } = value;
};

/// <summary>
/// Out-of-process JSON-RPC 2.0 client for Elgato Wave Link 3 over a local WebSocket.
/// Owns the connection, the state cache and the diagnostics surface. All sends go through one gate;
/// the receive loop is the only reader. Invocations run concurrently, so every member is synchronized.
/// </summary>
public sealed class WaveLinkClient : IAsyncDisposable
{
	public const string OverallMix = "overall";
	public const string BothMixes = "both";

	private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan FreshnessLimit = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

	private readonly ILogger _logger;
	private readonly SemaphoreSlim _sendGate = new(1, 1);
	private readonly object _cacheLock = new();
	private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
	private readonly Queue<string> _recentEvents = new();
	private CancellationTokenSource _shutdown = new();

	private ClientWebSocket? _socket;
	private Task? _loop;
	private Snapshot? _snapshot;
	private int _rpcId;
	private int _backoffSeconds = 1;
	private bool _started;

	private long _calls;
	private long _ok;
	private long _failed;
	private long _connects;
	private long _drops;
	private string? _lastError;
	private DateTimeOffset? _connectedSince;

	/// <summary>How long the send path waits for a reconnect before failing (notes: reconnect window).</summary>
	public TimeSpan ReconnectGrace { get; set; } = TimeSpan.FromSeconds(8);

	public WaveLinkClient(ILogger logger) => _logger = logger.ForContext<WaveLinkClient>();

	public bool IsConnected
	{
		get
		{
			lock (_cacheLock)
			{
				return _connected;
			}
		}
	}

	/// <summary>Non-throwing status read for variable polling.</summary>
	public (bool Connected, string? Version, int? ChannelCount) ReadStatus()
	{
		lock (_cacheLock)
		{
			return (_connected, _snapshot?.AppVersion, _snapshot?.Channels.Count);
		}
	}

	private bool _connected;

	public Task StartAsync()
	{
		lock (_cacheLock)
		{
			if (_started)
			{
				return Task.CompletedTask;
			}

			_remember("client starting");
			_started = true;
			if (_shutdown.IsCancellationRequested)
			{
				_shutdown.Dispose();
				_shutdown = new CancellationTokenSource();
			}

			_loop = Task.Run(() => LoopAsync(_shutdown.Token));
			return Task.CompletedTask;
		}
	}

	public async Task StopAsync()
	{
		Task? loop;
		bool alreadyStopped;
		lock (_cacheLock)
		{
			loop = _loop;
			_loop = null;
			alreadyStopped = !_started;
			_started = false;
		}

		if (alreadyStopped && loop is null)
		{
			return;
		}

		try
		{
			_shutdown.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}

		if (loop is not null)
		{
			try
			{
				await loop.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				_logger.Debug(ex, "Wave Link loop stopped with an error during shutdown.");
			}
		}

		await CloseSocketAsync().ConfigureAwait(false);
	}

	private bool _disposed;

	public async ValueTask DisposeAsync()
	{
		bool take;
		lock (_cacheLock)
		{
			take = !_disposed;
			_disposed = true;
		}

		if (!take)
		{
			return;
		}

		try
		{
			await StopAsync().ConfigureAwait(false);
		}
		finally
		{
			_shutdown.Dispose();
			_sendGate.Dispose();
		}
	}

	/// <summary>Waits boundedly for a live connection, then throws rather than training double-clicks.</summary>
	public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
	{
		if (IsConnected)
		{
			return;
		}

		var deadline = DateTimeOffset.UtcNow + ReconnectGrace;
		while (DateTimeOffset.UtcNow < deadline)
		{
			cancellationToken.ThrowIfCancellationRequested();
			await Task.Delay(250, cancellationToken).ConfigureAwait(false);
			if (IsConnected)
			{
				return;
			}
		}

		throw new WaveLinkNotConnectedException(LastErrorOrDefault());
	}

	public async Task<Snapshot> GetSnapshotAsync(CancellationToken cancellationToken)
	{
		await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

		bool stale;
		lock (_cacheLock)
		{
			stale = _snapshot is null || DateTimeOffset.UtcNow - _snapshot.TakenAt > FreshnessLimit;
		}

		if (stale)
		{
			await RefreshAsync(cancellationToken).ConfigureAwait(false);
		}

		lock (_cacheLock)
		{
			return _snapshot!;
		}
	}

	public async Task SetChannelVolumeAsync(string channelRef, string mixRef, double level01, CancellationToken cancellationToken)
	{
		var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
		var channel = FindChannel(snapshot, channelRef);
		level01 = Clamp01(level01);

		var payload = new JsonObject { ["id"] = channel.Id };
		if (IsOverall(mixRef))
		{
			payload["level"] = level01;
		}
		else
		{
			payload["mixes"] = MixEntries(ResolveMixes(snapshot, channel, mixRef), m => new JsonObject
			{
				["id"] = m.MixId,
				["mixId"] = m.MixId,
				["level"] = level01,
			});
		}

		await RpcAsync("setChannel", payload, cancellationToken).ConfigureAwait(false);
		await BestEffortRefreshAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task AdjustChannelVolumeAsync(string channelRef, string mixRef, double delta01, CancellationToken cancellationToken)
	{
		var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
		var channel = FindChannel(snapshot, channelRef);

		var payload = new JsonObject { ["id"] = channel.Id };
		if (IsOverall(mixRef))
		{
			payload["level"] = Clamp01((channel.Level ?? 0) + delta01);
		}
		else
		{
			payload["mixes"] = MixEntries(ResolveMixes(snapshot, channel, mixRef),
				m => new JsonObject
				{
					["id"] = m.MixId,
					["mixId"] = m.MixId,
					["level"] = Clamp01((m.Level ?? 0) + delta01),
				});
		}

		await RpcAsync("setChannel", payload, cancellationToken).ConfigureAwait(false);
		await BestEffortRefreshAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task SetChannelMuteAsync(string channelRef, string mixRef, MuteMode mode, CancellationToken cancellationToken)
	{
		var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
		var channel = FindChannel(snapshot, channelRef);

		var payload = new JsonObject { ["id"] = channel.Id };
		if (IsOverall(mixRef))
		{
			payload["isMuted"] = ResolveMute(mode, channel.IsMuted ?? false);
		}
		else
		{
			var targets = ResolveMixes(snapshot, channel, mixRef);
			bool anyUnmuted = targets.Any(m => !(m.IsMuted ?? false));
			payload["mixes"] = MixEntries(targets, m => new JsonObject
			{
				["id"] = m.MixId,
				["mixId"] = m.MixId,
				["isMuted"] = mode == MuteMode.Toggle ? anyUnmuted : mode == MuteMode.Mute,
			});
		}

		await RpcAsync("setChannel", payload, cancellationToken).ConfigureAwait(false);
		await BestEffortRefreshAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task SetMixVolumeAsync(string mixRef, double level01, CancellationToken cancellationToken)
	{
		var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
		var mix = FindMix(snapshot, mixRef);

		await RpcAsync("setMix", new JsonObject
		{
			["id"] = mix.Id,
			["mixId"] = mix.Id,
			["level"] = Clamp01(level01),
		}, cancellationToken).ConfigureAwait(false);
		await BestEffortRefreshAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task SetMixMuteAsync(string mixRef, MuteMode mode, CancellationToken cancellationToken)
	{
		if (mode == MuteMode.Leave)
		{
			return;
		}

		var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
		var mix = FindMix(snapshot, mixRef);

		await RpcAsync("setMix", new JsonObject
		{
			["id"] = mix.Id,
			["mixId"] = mix.Id,
			["isMuted"] = ResolveMute(mode, mix.IsMuted ?? false),
		}, cancellationToken).ConfigureAwait(false);
		await BestEffortRefreshAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task SetChannelFxAsync(string channelRef, string effectRef, FxMode mode, CancellationToken cancellationToken)
	{
		var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
		var channel = FindChannel(snapshot, channelRef);
		var effect = channel.Effects.FirstOrDefault(e =>
				string.Equals(e.Id, effectRef, StringComparison.OrdinalIgnoreCase))
			?? channel.Effects.FirstOrDefault(e =>
				string.Equals(e.Name, effectRef, StringComparison.OrdinalIgnoreCase))
			?? throw new WaveLinkNotFoundException("effect", $"'{effectRef}' on channel '{channel.Name}'");

		await RpcAsync("setChannel", new JsonObject
		{
			["id"] = channel.Id,
			["effects"] = new JsonArray
			{
				new JsonObject
				{
					["id"] = effect.Id,
					["isEnabled"] = mode == FxMode.Enable || (mode == FxMode.Toggle && !effect.IsEnabled),
				},
			},
		}, cancellationToken).ConfigureAwait(false);
		await BestEffortRefreshAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task SetInputGainAsync(string deviceId, string inputId, double? gain01, MuteMode mute, CancellationToken cancellationToken)
	{
		await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

		var input = new JsonObject { ["id"] = inputId };
		if (gain01.HasValue)
		{
			input["gain"] = new JsonObject { ["value"] = Clamp01(gain01.Value) };
		}

		if (mute != MuteMode.Leave)
		{
			var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
			var entry = snapshot.Inputs.FirstOrDefault(i =>
					string.Equals(i.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
					string.Equals(i.InputId, inputId, StringComparison.OrdinalIgnoreCase))
				?? throw new WaveLinkNotFoundException("input", $"'{inputId}' on device '{deviceId}'");
			input["isMuted"] = ResolveMute(mute, entry.IsMuted ?? false);
		}

		await RpcAsync("setInputDevice", new JsonObject
		{
			["id"] = deviceId,
			["inputs"] = new JsonArray { input },
		}, cancellationToken).ConfigureAwait(false);
		await BestEffortRefreshAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task SetOutputAsync(string deviceId, string outputId, double? level01, MuteMode mute, bool setAsMain, CancellationToken cancellationToken)
	{
		await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

		if (level01.HasValue || mute != MuteMode.Leave)
		{
			var output = new JsonObject { ["id"] = outputId };
			if (level01.HasValue)
			{
				output["level"] = Clamp01(level01.Value);
			}

			if (mute != MuteMode.Leave)
			{
				var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
				var entry = snapshot.Outputs.FirstOrDefault(o =>
						string.Equals(o.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
						string.Equals(o.OutputId, outputId, StringComparison.OrdinalIgnoreCase))
					?? throw new WaveLinkNotFoundException("output", $"'{outputId}' on device '{deviceId}'");
				output["isMuted"] = ResolveMute(mute, entry.IsMuted ?? false);
			}

			await RpcAsync("setOutputDevice", new JsonObject
			{
				["outputDevice"] = new JsonObject
				{
					["id"] = deviceId,
					["outputs"] = new JsonArray { output },
				},
			}, cancellationToken).ConfigureAwait(false);
		}

		if (setAsMain)
		{
			await RpcAsync("setOutputDevice", new JsonObject
			{
				["mainOutput"] = new JsonObject
				{
					["outputDeviceId"] = deviceId,
					["outputId"] = outputId,
				},
			}, cancellationToken).ConfigureAwait(false);
		}

		await BestEffortRefreshAsync(cancellationToken).ConfigureAwait(false);
	}

	public string GetStatusText()
	{
		// Diagnostics, not UI: English literals, like log text. The action chrome around this
		// (name, description, errors) is localized; the dump itself is troubleshooting output.
		var builder = new StringBuilder();
		bool connected;
		Snapshot? snapshot;
		long calls, ok, failed, connects, drops;
		string? lastError, version;
		DateTimeOffset? since;
		string[] recent;
		lock (_cacheLock)
		{
			connected = _connected;
			snapshot = _snapshot;
			calls = _calls;
			ok = _ok;
			failed = _failed;
			connects = _connects;
			drops = _drops;
			lastError = _lastError;
			version = _snapshot?.AppVersion;
			since = _connectedSince;
			recent = _recentEvents.ToArray();
		}

		builder.AppendLine(CultureInfo.InvariantCulture, $"WaveLink Bridge status (build {typeof(WaveLinkClient).Assembly.GetName().Version})");
		string connectionLine = connected
			? "Connected (Wave Link " + (version ?? "?") + ")"
			: "Not connected (" + (lastError ?? "not started") + ")";
		builder.AppendLine(connectionLine);
		if (snapshot is not null)
		{
			builder.AppendLine(CultureInfo.InvariantCulture, $"{snapshot.Channels.Count} channels, {snapshot.Mixes.Count} mixes, {snapshot.Inputs.Count} inputs, {snapshot.Outputs.Count} outputs");
		}

		builder.AppendLine(CultureInfo.InvariantCulture, $"RPC calls: {calls} ({ok} ok, {failed} failed)");
		builder.AppendLine(CultureInfo.InvariantCulture, $"Connections: {connects}, drops: {drops}");
		if (since.HasValue)
		{
			builder.AppendLine($"Session age: {FormatAge(DateTimeOffset.UtcNow - since.Value)}");
		}

		if (lastError is not null)
		{
			builder.AppendLine(CultureInfo.InvariantCulture, $"Last error: {lastError}");
		}

		if (recent.Length > 0)
		{
			builder.AppendLine("Recent events:");
			foreach (var line in recent.TakeLast(8))
			{
				builder.AppendLine(CultureInfo.InvariantCulture, $"  {line}");
			}
		}

		return builder.ToString().TrimEnd();
	}

	private static bool IsOverall(string mixRef) =>
		string.Equals(mixRef, OverallMix, StringComparison.OrdinalIgnoreCase);

	private static bool ResolveMute(MuteMode mode, bool current) => mode switch
	{
		MuteMode.Mute => true,
		MuteMode.Unmute => false,
		_ => !current,
	};

	private static double Clamp01(double value) => Math.Min(1, Math.Max(0, value));

	private static JsonArray MixEntries(IEnumerable<ChannelMixState> mixes, Func<ChannelMixState, JsonObject> build)
	{
		var array = new JsonArray();
		foreach (var mix in mixes)
		{
			array.Add(build(mix));
		}

		return array;
	}

	private static ChannelState FindChannel(Snapshot snapshot, string channelRef) =>
		snapshot.Channels.FirstOrDefault(c =>
			string.Equals(c.Id, channelRef, StringComparison.OrdinalIgnoreCase))
		?? snapshot.Channels.FirstOrDefault(c =>
			string.Equals(c.Name, channelRef, StringComparison.OrdinalIgnoreCase))
		?? throw new WaveLinkNotFoundException("channel", channelRef);

	private static MixState FindMix(Snapshot snapshot, string mixRef) =>
		snapshot.Mixes.FirstOrDefault(m =>
			string.Equals(m.Id, mixRef, StringComparison.OrdinalIgnoreCase))
		?? snapshot.Mixes.FirstOrDefault(m =>
			string.Equals(m.Name, mixRef, StringComparison.OrdinalIgnoreCase))
		?? throw new WaveLinkNotFoundException("mix", mixRef);

	private static IReadOnlyList<ChannelMixState> ResolveMixes(Snapshot snapshot, ChannelState channel, string mixRef)
	{
		if (string.Equals(mixRef, BothMixes, StringComparison.OrdinalIgnoreCase))
		{
			return channel.Mixes;
		}

		var direct = channel.Mixes.FirstOrDefault(m =>
			string.Equals(m.MixId, mixRef, StringComparison.OrdinalIgnoreCase));
		if (direct is null)
		{
			var named = snapshot.Mixes.FirstOrDefault(m =>
				string.Equals(m.Name, mixRef, StringComparison.OrdinalIgnoreCase));
			if (named is not null)
			{
				direct = channel.Mixes.FirstOrDefault(m =>
					string.Equals(m.MixId, named.Id, StringComparison.OrdinalIgnoreCase));
			}
		}

		return direct is not null
			? [direct]
			: throw new WaveLinkNotFoundException("mix", $"'{mixRef}' on channel '{channel.Name}'");
	}

	private static string LastErrorDefault() => "Wave Link is not reachable.";

	private string LastErrorOrDefault()
	{
		lock (_cacheLock)
		{
			return _lastError ?? LastErrorDefault();
		}
	}

	private void _remember(string line)
	{
		lock (_cacheLock)
		{
			_recentEvents.Enqueue($"{DateTimeOffset.UtcNow:HH:mm:ss} {line}");
			while (_recentEvents.Count > 20)
			{
				_recentEvents.Dequeue();
			}
		}
	}

	private static string FormatAge(TimeSpan age) =>
		age.TotalHours >= 1 ? $"{(int)age.TotalHours}h {age.Minutes}m" :
		age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes}m {age.Seconds}s" :
		$"{(int)age.TotalSeconds}s";

	private async Task BestEffortRefreshAsync(CancellationToken cancellationToken)
	{
		try
		{
			await RefreshAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.Debug(ex, "Wave Link post-write refresh failed; the cached state stays as-is.");
		}
	}

	private async Task RefreshAsync(CancellationToken cancellationToken)
	{
		var channelsTask = RpcAsync("getChannels", null, cancellationToken);
		var mixesTask = RpcAsync("getMixes", null, cancellationToken);
		var inputsTask = RpcAsync("getInputDevices", null, cancellationToken);
		var outputsTask = RpcAsync("getOutputDevices", null, cancellationToken);

		await Task.WhenAll(channelsTask, inputsTask, mixesTask, outputsTask).ConfigureAwait(false);

		var snapshot = new Snapshot(
			ParseChannels(await channelsTask.ConfigureAwait(false)),
			ParseMixes(await mixesTask.ConfigureAwait(false)),
			ParseInputs(await inputsTask.ConfigureAwait(false)),
			ParseOutputs(await outputsTask.ConfigureAwait(false), out var mainDevice, out var mainOutput),
			mainDevice,
			mainOutput,
			AppVersionOf(await RpcAsync("getApplicationInfo", null, cancellationToken).ConfigureAwait(false)),
			DateTimeOffset.UtcNow);

		lock (_cacheLock)
		{
			_snapshot = snapshot;
		}
	}

	private async Task<JsonElement> RpcAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
	{
		ClientWebSocket? socket;
		lock (_cacheLock)
		{
			socket = _socket;
		}

		if (socket is null || socket.State != WebSocketState.Open)
		{
			throw new WaveLinkNotConnectedException(LastErrorOrDefault());
		}

		int id = Interlocked.Increment(ref _rpcId);
		var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending[id] = completion;

		var request = new JsonObject
		{
			["jsonrpc"] = "2.0",
			["id"] = id,
			["method"] = method,
		};
		if (parameters is not null)
		{
			request["params"] = parameters;
		}

		Interlocked.Increment(ref _calls);
		try
		{
			var bytes = Encoding.UTF8.GetBytes(request.ToJsonString());
			await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				_sendGate.Release();
			}

			using var timeout = new CancellationTokenSource(RpcTimeout);
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
			try
			{
				var result = await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
				Interlocked.Increment(ref _ok);
				return result;
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				throw new TimeoutException($"Wave Link did not answer '{method}' within {RpcTimeout.TotalSeconds} seconds.");
			}
		}
		catch (Exception)
		{
			Interlocked.Increment(ref _failed);
			_pending.TryRemove(id, out _);
			throw;
		}
	}

	private async Task LoopAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				await ConnectAndServeAsync(cancellationToken).ConfigureAwait(false);
				_backoffSeconds = 1;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				NoteDrop(ex.Message);
				_logger.Debug(ex, "Wave Link connection failed; retrying in {Backoff}s.", _backoffSeconds);
			}

			try
			{
				await Task.Delay(TimeSpan.FromSeconds(_backoffSeconds), cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}

			_backoffSeconds = Math.Min(30, _backoffSeconds * 2);
		}
	}

	private async Task ConnectAndServeAsync(CancellationToken cancellationToken)
	{
		int port = await DiscoverPortAsync(cancellationToken).ConfigureAwait(false);
		var socket = new ClientWebSocket();
		socket.Options.SetRequestHeader("Origin", "streamdeck://");

		using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectTimeout.Token);
		try
		{
			await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), linked.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			socket.Dispose();
			throw new WaveLinkNotConnectedException($"Wave Link did not accept a connection on port {port}.");
		}

		lock (_cacheLock)
		{
			_socket = socket;
		}

		try
		{
			var info = await RpcAsync("getApplicationInfo", null, cancellationToken).ConfigureAwait(false);
			string? appId = info.TryGetProperty("appID", out var appIdEl) ? appIdEl.GetString() : null;
			if (!string.Equals(appId, "EWL", StringComparison.Ordinal))
			{
				_logger.Warning("Unexpected Wave Link app id '{AppId}'; continuing anyway.", appId ?? "?");
			}

			try
			{
				await RpcAsync("setPluginInfo", new JsonObject
				{
					["connectedDevices"] = new JsonArray { "SD" },
				}, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.Debug(ex, "Wave Link setPluginInfo was refused; continuing without it.");
			}

			await RefreshAsync(cancellationToken).ConfigureAwait(false);
			NoteConnect();
			await ReceiveLoopAsync(socket, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			lock (_cacheLock)
			{
				if (ReferenceEquals(_socket, socket))
				{
					_socket = null;
				}
			}

			NoteDrop("connection closed");
			await CloseSocketAsync(socket).ConfigureAwait(false);
		}
	}

	private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
	{
		var buffer = new byte[65536];
		var message = new List<byte>();
		var pollAt = DateTimeOffset.UtcNow + PollInterval;

		while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
		{
			using var receiveTimeout = new CancellationTokenSource(PollInterval);
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, receiveTimeout.Token);
			WebSocketReceiveResult frame;
			try
			{
				frame = await socket.ReceiveAsync(buffer, linked.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				await BestEffortRefreshAsync(cancellationToken).ConfigureAwait(false);
				pollAt = DateTimeOffset.UtcNow + PollInterval;
				continue;
			}

			if (frame.MessageType == WebSocketMessageType.Close)
			{
				break;
			}

			message.AddRange(new ArraySegment<byte>(buffer, 0, frame.Count));
			if (!frame.EndOfMessage)
			{
				continue;
			}

			var text = Encoding.UTF8.GetString(message.ToArray());
			message.Clear();
			HandleMessage(text);

			if (DateTimeOffset.UtcNow >= pollAt)
			{
				await BestEffortRefreshAsync(cancellationToken).ConfigureAwait(false);
				pollAt = DateTimeOffset.UtcNow + PollInterval;
			}
		}
	}

	private void HandleMessage(string text)
	{
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(text);
		}
		catch (JsonException ex)
		{
			_logger.Debug(ex, "Wave Link sent a frame that is not JSON.");
			return;
		}

		using (document)
		{
			var root = document.RootElement;
			if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out int id))
			{
				if (_pending.TryRemove(id, out var completion))
				{
					if (root.TryGetProperty("error", out var error))
					{
						int code = error.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt32(out int c) ? c : -1;
						string message = error.TryGetProperty("message", out var messageEl) ? messageEl.GetString() ?? "unknown error" : "unknown error";
						completion.TrySetException(new WaveLinkRpcException(code, message));
					}
					else if (root.TryGetProperty("result", out var result))
					{
						completion.TrySetResult(result.Clone());
					}
					else
					{
						completion.TrySetException(new WaveLinkRpcException(-1, "Wave Link answered without a result."));
					}
				}

				return;
			}

			if (root.TryGetProperty("method", out var methodEl) && methodEl.GetString() is { } method)
			{
				try
				{
					HandleNotification(method, root.TryGetProperty("params", out var paramsEl) ? paramsEl : null);
				}
				catch (Exception ex)
				{
					_logger.Debug(ex, "Wave Link notification '{Method}' could not be applied.", method);
				}
			}
		}
	}

	private void HandleNotification(string method, JsonElement? @params)
	{
		if (@params is null || @params.Value.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		var element = @params.Value;
		lock (_cacheLock)
		{
			if (_snapshot is null)
			{
				return;
			}

			_snapshot = method switch
			{
				"channelChanged" => _snapshot with { Channels = MergeChannel(_snapshot.Channels, element), TakenAt = DateTimeOffset.UtcNow },
				"channelsChanged" => _snapshot with { Channels = ReplaceChannels(element), TakenAt = DateTimeOffset.UtcNow },
				"mixChanged" => _snapshot with { Mixes = MergeMix(_snapshot.Mixes, element), TakenAt = DateTimeOffset.UtcNow },
				"mixesChanged" => _snapshot with { Mixes = ReplaceMixes(element), TakenAt = DateTimeOffset.UtcNow },
				"inputDeviceChanged" => _snapshot with { Inputs = MergeInput(_snapshot.Inputs, element), TakenAt = DateTimeOffset.UtcNow },
				"inputDevicesChanged" => _snapshot with { Inputs = ReplaceInputs(element), TakenAt = DateTimeOffset.UtcNow },
				"outputDeviceChanged" => _snapshot with { Outputs = MergeOutput(_snapshot.Outputs, element), TakenAt = DateTimeOffset.UtcNow },
				"outputDevicesChanged" => ApplyOutputDevices(_snapshot, element),
				_ => _snapshot,
			};

			_remember(method);
		}
	}

	private static Snapshot ApplyOutputDevices(Snapshot snapshot, JsonElement @params)
	{
		var outputs = ParseOutputDevices(@params, out string? dev, out string? main);
		return snapshot with
		{
			Outputs = outputs,
			MainOutputDeviceId = dev ?? snapshot.MainOutputDeviceId,
			MainOutputId = main ?? snapshot.MainOutputId,
			TakenAt = DateTimeOffset.UtcNow,
		};
	}

	private void NoteConnect()
	{
		lock (_cacheLock)
		{
			_connected = true;
			_connectedSince = DateTimeOffset.UtcNow;
			_connects++;
			_lastError = null;
		}

		_remember("connected");
		_logger.Information("Connected to Wave Link.");
	}

	private void NoteDrop(string reason)
	{
		lock (_cacheLock)
		{
			if (_connected)
			{
				_drops++;
			}

			_connected = false;
			_connectedSince = null;
			_lastError = reason;
		}

		_remember("dropped: " + reason);
	}

	private async Task CloseSocketAsync(ClientWebSocket? socket = null)
	{
		ClientWebSocket? owned;
		lock (_cacheLock)
		{
			owned = socket ?? _socket;
			_socket = null;
		}

		if (owned is null)
		{
			return;
		}

		try
		{
			if (owned.State == WebSocketState.Open)
			{
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
				await owned.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", timeout.Token).ConfigureAwait(false);
			}
		}
		catch (Exception ex)
		{
			_logger.Debug(ex, "Wave Link socket close reported an error.");
		}
		finally
		{
			owned.Dispose();
		}
	}

	private static async Task<int> DiscoverPortAsync(CancellationToken cancellationToken)
	{
		string? fromFile = ReadPortFile();
		if (fromFile is not null && int.TryParse(fromFile, out int filePort))
		{
			return filePort;
		}

		for (int port = 1884; port <= 1893; port++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (await ProbePortAsync(port, cancellationToken).ConfigureAwait(false))
			{
				return port;
			}
		}

		throw new WaveLinkNotConnectedException("Wave Link was not found (ws-info.json is missing and no fallback port answered). Is Wave Link running?");
	}

	private static string? ReadPortFile()
	{
		try
		{
			string path = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Packages", "Elgato.WaveLink_g54w8ztgkx496",
				"LocalState", "ws-info.json");
			if (!File.Exists(path))
			{
				return null;
			}

			using var document = JsonDocument.Parse(File.ReadAllText(path));
			if (document.RootElement.TryGetProperty("port", out var portEl))
			{
				return portEl.ValueKind == JsonValueKind.Number ? portEl.GetInt32().ToString(CultureInfo.InvariantCulture) : portEl.GetString();
			}
		}
		catch (Exception)
		{
			return null;
		}

		return null;
	}

	private static async Task<bool> ProbePortAsync(int port, CancellationToken cancellationToken)
	{
		var socket = new ClientWebSocket();
		socket.Options.SetRequestHeader("Origin", "streamdeck://");
		try
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
			await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), linked.Token).ConfigureAwait(false);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
		finally
		{
			socket.Dispose();
		}
	}

	private static string? AppVersionOf(JsonElement result) =>
		result.ValueKind == JsonValueKind.Object && result.TryGetProperty("version", out var version)
			? version.GetString()
			: null;

	private static IReadOnlyList<ChannelState> ParseChannels(JsonElement result) =>
		result.ValueKind == JsonValueKind.Object && result.TryGetProperty("channels", out var channels) && channels.ValueKind == JsonValueKind.Array
			? [.. channels.EnumerateArray().Select(ParseChannel).Where(c => c is not null)!]
			: [];

	private static ChannelState? ParseChannel(JsonElement element)
	{
		if (!element.TryGetProperty("id", out var idEl) || idEl.GetString() is not { } id)
		{
			return null;
		}

		return new ChannelState(
			id,
			element.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? id : id,
			element.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "?" : "?",
			element.TryGetProperty("level", out var levelEl) && levelEl.ValueKind == JsonValueKind.Number ? levelEl.GetDouble() : null,
			element.TryGetProperty("isMuted", out var mutedEl) && mutedEl.ValueKind is JsonValueKind.True or JsonValueKind.False ? mutedEl.GetBoolean() : null,
			element.TryGetProperty("mixes", out var mixesEl) && mixesEl.ValueKind == JsonValueKind.Array
				? [.. mixesEl.EnumerateArray().Select(ParseChannelMix).Where(m => m is not null)!]
				: [],
			element.TryGetProperty("effects", out var fxEl) && fxEl.ValueKind == JsonValueKind.Array
				? [.. fxEl.EnumerateArray().Select(ParseEffect).Where(e => e is not null)!]
				: []);
	}

	private static ChannelMixState? ParseChannelMix(JsonElement element)
	{
		string? mixId = element.TryGetProperty("mixId", out var named) && named.GetString() is { } n ? n
			: element.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
		if (mixId is null)
		{
			return null;
		}

		return new ChannelMixState(
			mixId,
			element.TryGetProperty("level", out var levelEl) && levelEl.ValueKind == JsonValueKind.Number ? levelEl.GetDouble() : null,
			element.TryGetProperty("isMuted", out var mutedEl) && mutedEl.ValueKind is JsonValueKind.True or JsonValueKind.False ? mutedEl.GetBoolean() : null);
	}

	private static EffectState? ParseEffect(JsonElement element)
	{
		if (!element.TryGetProperty("id", out var idEl) || idEl.GetString() is not { } id)
		{
			return null;
		}

		return new EffectState(
			id,
			element.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? id : id,
			element.TryGetProperty("isEnabled", out var enabledEl) && enabledEl.ValueKind == JsonValueKind.True);
	}

	private static IReadOnlyList<MixState> ParseMixes(JsonElement result) =>
		result.ValueKind == JsonValueKind.Object && result.TryGetProperty("mixes", out var mixes) && mixes.ValueKind == JsonValueKind.Array
			? [.. mixes.EnumerateArray()
				.Where(m => m.TryGetProperty("id", out var idEl) && idEl.GetString() is not null)
				.Select(m => new MixState(
					m.GetProperty("id").GetString()!,
					m.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? m.GetProperty("id").GetString()! : m.GetProperty("id").GetString()!,
					m.TryGetProperty("level", out var levelEl) && levelEl.ValueKind == JsonValueKind.Number ? levelEl.GetDouble() : null,
					m.TryGetProperty("isMuted", out var mutedEl) && mutedEl.ValueKind is JsonValueKind.True or JsonValueKind.False ? mutedEl.GetBoolean() : null))]
			: [];

	private static List<InputEntry> ParseInputs(JsonElement result)
	{
		var entries = new List<InputEntry>();
		if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("inputDevices", out var devices) || devices.ValueKind != JsonValueKind.Array)
		{
			return entries;
		}

		foreach (var device in devices.EnumerateArray())
		{
			if (!device.TryGetProperty("id", out var deviceIdEl) || deviceIdEl.GetString() is not { } deviceId)
			{
				continue;
			}

			string deviceName = device.TryGetProperty("name", out var deviceNameEl) ? deviceNameEl.GetString() ?? deviceId : deviceId;
			if (!device.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array)
			{
				continue;
			}

			foreach (var input in inputs.EnumerateArray())
			{
				if (!input.TryGetProperty("id", out var inputIdEl) || inputIdEl.GetString() is not { } inputId)
				{
					continue;
				}

				double? gain = input.TryGetProperty("gain", out var gainEl) && gainEl.ValueKind == JsonValueKind.Object
					&& gainEl.TryGetProperty("value", out var gainValueEl) && gainValueEl.ValueKind == JsonValueKind.Number
					? gainValueEl.GetDouble() : null;
				double? micPcMix = input.TryGetProperty("micPcMix", out var mixEl) && mixEl.ValueKind == JsonValueKind.Object
					&& mixEl.TryGetProperty("value", out var mixValueEl) && mixValueEl.ValueKind == JsonValueKind.Number
					? mixValueEl.GetDouble() : null;
				var effects = new List<EffectState>();
				foreach (var key in new[] { "effects", "dspEffects" })
				{
					if (input.TryGetProperty(key, out var fxEl) && fxEl.ValueKind == JsonValueKind.Array)
					{
						effects.AddRange(fxEl.EnumerateArray().Select(ParseEffect).Where(e => e is not null)!);
					}
				}

				entries.Add(new InputEntry(
					deviceId,
					deviceName,
					inputId,
					input.TryGetProperty("name", out var inputNameEl) ? inputNameEl.GetString() ?? inputId : inputId,
					input.TryGetProperty("isMuted", out var mutedEl) && mutedEl.ValueKind is JsonValueKind.True or JsonValueKind.False ? mutedEl.GetBoolean() : null,
					gain,
					micPcMix,
					effects));
			}
		}

		return entries;
	}

	private static List<OutputEntry> ParseOutputs(JsonElement result, out string? mainDeviceId, out string? mainOutputId)
	{
		mainDeviceId = null;
		mainOutputId = null;
		var entries = new List<OutputEntry>();
		if (result.ValueKind != JsonValueKind.Object)
		{
			return entries;
		}

		if (result.TryGetProperty("mainOutput", out var main) && main.ValueKind == JsonValueKind.Object)
		{
			mainDeviceId = main.TryGetProperty("outputDeviceId", out var devEl) ? devEl.GetString() : null;
			mainOutputId = main.TryGetProperty("outputId", out var outEl) ? outEl.GetString() : null;
		}

		if (!result.TryGetProperty("outputDevices", out var devices) || devices.ValueKind != JsonValueKind.Array)
		{
			return entries;
		}

		foreach (var device in devices.EnumerateArray())
		{
			if (!device.TryGetProperty("id", out var deviceIdEl) || deviceIdEl.GetString() is not { } deviceId)
			{
				continue;
			}

			string deviceName = device.TryGetProperty("name", out var deviceNameEl) ? deviceNameEl.GetString() ?? deviceId : deviceId;
			if (!device.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Array)
			{
				continue;
			}

			foreach (var output in outputs.EnumerateArray())
			{
				if (!output.TryGetProperty("id", out var outputIdEl) || outputIdEl.GetString() is not { } outputId)
				{
					continue;
				}

				entries.Add(new OutputEntry(
					deviceId,
					deviceName,
					outputId,
					output.TryGetProperty("name", out var outputNameEl) ? outputNameEl.GetString() ?? outputId : outputId,
					output.TryGetProperty("level", out var levelEl) && levelEl.ValueKind == JsonValueKind.Number ? levelEl.GetDouble() : null,
					output.TryGetProperty("isMuted", out var mutedEl) && mutedEl.ValueKind is JsonValueKind.True or JsonValueKind.False ? mutedEl.GetBoolean() : null,
					output.TryGetProperty("mixId", out var mixEl) ? mixEl.GetString() : null));
			}
		}

		return entries;
	}

	private static IReadOnlyList<ChannelState> ReplaceChannels(JsonElement @params) =>
		@params.TryGetProperty("channels", out var channels) && channels.ValueKind == JsonValueKind.Array
			? [.. channels.EnumerateArray().Select(ParseChannel).Where(c => c is not null)!]
			: [];

	private static IReadOnlyList<ChannelState> MergeChannel(IReadOnlyList<ChannelState> current, JsonElement @params)
	{
		var updated = ParseChannel(@params);
		if (updated is null)
		{
			return current;
		}

		var list = current.ToList();
		int index = list.FindIndex(c => string.Equals(c.Id, updated.Id, StringComparison.OrdinalIgnoreCase));
		if (index < 0)
		{
			list.Add(updated);
			return list;
		}

		var existing = list[index];
		list[index] = existing with
		{
			Name = @params.TryGetProperty("name", out _) ? updated.Name : existing.Name,
			Level = updated.Level ?? existing.Level,
			IsMuted = updated.IsMuted ?? existing.IsMuted,
			Mixes = updated.Mixes.Count > 0 ? MergeMixList(existing.Mixes, updated.Mixes) : existing.Mixes,
			Effects = updated.Effects.Count > 0 ? updated.Effects : existing.Effects,
		};
		return list;
	}

	private static List<ChannelMixState> MergeMixList(IReadOnlyList<ChannelMixState> current, IReadOnlyList<ChannelMixState> updated)
	{
		var list = current.ToList();
		foreach (var mix in updated)
		{
			int index = list.FindIndex(m => string.Equals(m.MixId, mix.MixId, StringComparison.OrdinalIgnoreCase));
			if (index < 0)
			{
				list.Add(mix);
			}
			else
			{
				list[index] = list[index] with
				{
					Level = mix.Level ?? list[index].Level,
					IsMuted = mix.IsMuted ?? list[index].IsMuted,
				};
			}
		}

		return list;
	}

	private static IReadOnlyList<MixState> ReplaceMixes(JsonElement @params) =>
		@params.TryGetProperty("mixes", out var mixes) && mixes.ValueKind == JsonValueKind.Array
			? [.. mixes.EnumerateArray()
				.Where(m => m.TryGetProperty("id", out var idEl) && idEl.GetString() is not null)
				.Select(m => new MixState(
					m.GetProperty("id").GetString()!,
					m.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? m.GetProperty("id").GetString()! : m.GetProperty("id").GetString()!,
					m.TryGetProperty("level", out var levelEl) && levelEl.ValueKind == JsonValueKind.Number ? levelEl.GetDouble() : null,
					m.TryGetProperty("isMuted", out var mutedEl) && mutedEl.ValueKind is JsonValueKind.True or JsonValueKind.False ? mutedEl.GetBoolean() : null))]
			: [];

	private static IReadOnlyList<MixState> MergeMix(IReadOnlyList<MixState> current, JsonElement @params)
	{
		if (!@params.TryGetProperty("id", out var idEl) || idEl.GetString() is not { } id)
		{
			return current;
		}

		var list = current.ToList();
		int index = list.FindIndex(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
		if (index < 0)
		{
			return current;
		}

		list[index] = list[index] with
		{
			Level = @params.TryGetProperty("level", out var levelEl) && levelEl.ValueKind == JsonValueKind.Number ? levelEl.GetDouble() : list[index].Level,
			IsMuted = @params.TryGetProperty("isMuted", out var mutedEl) && mutedEl.ValueKind is JsonValueKind.True or JsonValueKind.False ? mutedEl.GetBoolean() : list[index].IsMuted,
		};
		return list;
	}

	private static List<InputEntry> ReplaceInputs(JsonElement @params) =>
		ParseInputs(@params);

	private static IReadOnlyList<InputEntry> MergeInput(IReadOnlyList<InputEntry> current, JsonElement @params) =>
		current;

	private static List<OutputEntry> ParseOutputDevices(JsonElement @params, out string? mainDeviceId, out string? mainOutputId)
	{
		mainDeviceId = @params.TryGetProperty("mainOutput", out var main) && main.ValueKind == JsonValueKind.Object
			&& main.TryGetProperty("outputDeviceId", out var devEl) ? devEl.GetString() : null;
		mainOutputId = main.ValueKind == JsonValueKind.Object && main.TryGetProperty("outputId", out var outEl) ? outEl.GetString() : null;
		return @params.TryGetProperty("outputDevices", out _)
			? ParseOutputs(@params, out _, out _)
			: [];
	}

	private static IReadOnlyList<OutputEntry> MergeOutput(IReadOnlyList<OutputEntry> current, JsonElement @params) =>
		current;
}
