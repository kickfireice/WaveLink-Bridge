using MacroDeck.Plugin.Protocol.Capabilities.Actions;
using MacroDeck.Plugin.Protocol.Capabilities.Variables;
using MacroDeck.Plugin.Testing;
using MacroDeck.Sdk.Variables;
using NUnit.Framework;
using Serilog;

namespace WaveLinkBridgeUnofficial.Tests;

/// <summary>
/// Behaviour tests through <see cref="PluginTestHarness"/>: the plugin's own capability handlers run,
/// but nothing crosses a socket. Wave Link itself is never touched here; the live probe is manual.
/// Tests that need the client to fail wait out its reconnect grace, so they are slow by design.
/// </summary>
[TestFixture]
public sealed class PluginIntegrationTests
{
	private static readonly string[] _actionsNeedingParameters =
	[
		"set-channel-volume",
		"adjust-channel-volume",
		"toggle-channel-mute",
		"set-mix-volume",
		"toggle-mix-mute",
		"toggle-channel-fx",
		"set-input-gain",
		"set-output",
	];

	private static PluginTestHarness CreateHarness() =>
		PluginTestHarness.Create(builder => builder
			.UseLocalization(Strings.LocalizationCatalog)
			.RegisterIntegration<PluginIntegration>());

	[Test]
	public async Task The_plugin_builds_and_initializes()
	{
		await using var harness = CreateHarness();

		Assert.DoesNotThrowAsync(harness.InitializeIntegrationsAsync);
	}

	[Test]
	public async Task Every_action_is_reachable_and_validates_its_parameters([ValueSource(nameof(_actionsNeedingParameters))] string actionId)
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		var outcome = await harness.Actions.ExecuteAsync(actionId, new Dictionary<string, object?>());

		Assert.That(outcome.Succeeded, Is.False, "an action with no parameters must fail validation, not vanish");
	}

	[Test]
	public async Task The_channel_dropdown_answers_with_well_formed_options()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		// Whether Wave Link happens to be running on this machine or not, the provider
		// must answer: real options when connected, an error with no options when not.
		var options = (await harness.Actions.GetOptionsAsync("set-channel-volume", "channel")).DataAs<DynamicOptionsResultDto>();

		Assert.That(options, Is.Not.Null);
		Assert.That(options!.Options.All(option => !string.IsNullOrEmpty(option.Value)), Is.True);
	}

	[Test]
	public async Task The_status_action_answers_even_without_wave_link()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		var outcome = await harness.Actions.ExecuteAsync("show-status", new Dictionary<string, object?>());

		Assert.That(outcome.Succeeded, Is.True);
		Assert.That(outcome.DataAs<ActionExecuteResult>()!.Accepted, Is.True);
	}

	[Test]
	public async Task Mute_state_is_null_without_configuration()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		var channelMute = (await harness.Actions.GetActionStateAsync(
			"toggle-channel-mute", new Dictionary<string, object?>())).DataAs<ActionStateResult>();
		var mixMute = (await harness.Actions.GetActionStateAsync(
			"toggle-mix-mute", new Dictionary<string, object?>())).DataAs<ActionStateResult>();

		Assert.That(channelMute!.HasValue, Is.False);
		Assert.That(mixMute!.HasValue, Is.False);
	}

	[Test]
	public async Task Mute_state_is_null_for_targets_wave_link_does_not_have()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		var channelMute = (await harness.Actions.GetActionStateAsync(
			"toggle-channel-mute",
			new Dictionary<string, object?> { ["channel"] = "definitely-not-a-channel", ["mix"] = "overall" })).DataAs<ActionStateResult>();
		var mixMute = (await harness.Actions.GetActionStateAsync(
			"toggle-mix-mute",
			new Dictionary<string, object?> { ["mix"] = "definitely-not-a-mix" })).DataAs<ActionStateResult>();

		Assert.That(channelMute!.HasValue, Is.False);
		Assert.That(mixMute!.HasValue, Is.False);
	}

	[Test]
	public async Task ReadAsync_answers_every_id_form_the_same_way()
	{
		// Straight at the provider, not through host resolution: whatever Wave Link is doing
		// on this machine right now, the short id, the full name and the vars.-prefixed form
		// must resolve to the same reading.
		await using var integration = new PluginIntegration(new LoggerConfiguration().CreateLogger());

		var shortForm = await integration.ReadAsync("connected");
		var fullName = await integration.ReadAsync("wavelink_connected");
		var prefixed = await integration.ReadAsync("vars.wavelink_connected");

		Assert.That(fullName.Value, Is.EqualTo(shortForm.Value));
		Assert.That(prefixed.Value, Is.EqualTo(shortForm.Value));
	}

	[Test]
	public async Task Discover_answers_with_well_formed_items_whether_wave_link_is_up_or_not()
	{
		await using var integration = new PluginIntegration(new LoggerConfiguration().CreateLogger());

		var page = await integration.DiscoverAsync(
			new VariableCatalogQuery { PageSize = 50 }, TestContext.CurrentContext.CancellationToken);

		Assert.That(page, Is.Not.Null);
		Assert.That(page.Items.All(item => !string.IsNullOrEmpty(item.Id) && !string.IsNullOrEmpty(item.Name)), Is.True);
	}

	[Test]
	public async Task Resolve_answers_null_only_for_ids_that_never_named_a_channel()
	{
		await using var integration = new PluginIntegration(new LoggerConfiguration().CreateLogger());

		var resolved = await integration.ResolveAsync(
			"definitely-not-a-channel", TestContext.CurrentContext.CancellationToken);

		Assert.That(resolved, Is.Null);
	}

	[Test]
	public async Task Writing_an_unknown_channel_variable_reports_not_found()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		var written = (await harness.Variables.SetAsync("definitely-not-a-channel",
			new VariableValueDto { Kind = "number", Number = 50 })).DataAs<VariableSetResult>();

		Assert.That(written!.Status, Is.EqualTo("NotFound"));
	}

	[Test]
	public async Task Writing_text_to_a_channel_volume_reports_invalid_value()
	{
		await using var integration = new PluginIntegration(new LoggerConfiguration().CreateLogger());
		var page = await integration.DiscoverAsync(
			new VariableCatalogQuery { PageSize = 50 }, TestContext.CurrentContext.CancellationToken);
		if (page.Items.Count == 0)
		{
			Assert.Pass("Wave Link has no channels right now, so there is nothing to write to.");
			return;
		}

		var written = await integration.SetValueAsync(
			page.Items[0]!.Id!, "loud", TestContext.CurrentContext.CancellationToken);

		Assert.That(written.Status.ToString(), Is.EqualTo("InvalidValue"));
	}
}

/// <summary>
/// The localization set is generated from <c>Localization/*.resx</c>, so these guard the wiring rather
/// than any wording: a missing catalog registration leaves every label showing its raw key.
/// </summary>
[TestFixture]
public sealed class LocalizationTests
{
	[Test]
	public void The_catalog_is_scoped_to_the_plugin_id()
	{
		Assert.That(Strings.LocalizationCatalog.Scope, Is.EqualTo("plugin:com.kickfireice.wavelink-bridge"));
	}

	[Test]
	public void English_is_the_default_culture()
	{
		Assert.That(Strings.LocalizationCatalog.DefaultCulture, Is.EqualTo("en"));
		Assert.That(Strings.LocalizationCatalog.Cultures, Does.Contain("en"));
	}

	[Test]
	public void The_action_strings_come_from_the_catalog()
	{
		Assert.That(Strings.LocalizationCatalog.KeysOf("en"), Does.Contain("Actions.SetChannelVolume.Name"));
	}

	[Test]
	public void Every_key_the_default_culture_declares_resolves_to_text()
	{
		foreach (var key in Strings.LocalizationCatalog.KeysOf("en"))
		{
			Assert.That(Strings.LocalizationCatalog.TryGetTemplate("en", key, out var text), Is.True);
			Assert.That(text, Is.Not.Empty);
		}
	}
}
