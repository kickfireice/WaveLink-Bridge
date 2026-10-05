using MacroDeck.Plugin.Protocol.Capabilities.Actions;
using MacroDeck.Plugin.Protocol.Capabilities.Variables;
using MacroDeck.Plugin.Testing;
using NUnit.Framework;

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
	public async Task The_channel_dropdown_offers_no_channels_without_wave_link()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		var options = (await harness.Actions.GetOptionsAsync("set-channel-volume", "channel")).DataAs<DynamicOptionsResultDto>();

		Assert.That(options!.Options, Is.Empty);
	}

	[Test]
	public async Task Setting_a_volume_without_wave_link_fails_as_not_connected()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		var outcome = await harness.Actions.ExecuteAsync(
			"set-channel-volume",
			new Dictionary<string, object?> { ["channel"] = "Discord", ["mix"] = "overall", ["volume"] = 50.0 });

		Assert.That(outcome.Succeeded, Is.False);
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
	public async Task The_connected_variable_reads_false_without_wave_link()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		var reading = (await harness.Variables.GetAsync("connected")).DataAs<VariableReadingDto>();

		Assert.That(reading!.Value.Boolean, Is.False);
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
