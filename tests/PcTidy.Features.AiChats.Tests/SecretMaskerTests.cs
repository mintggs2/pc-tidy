using PcTidy.Catalog.AiChats;
using PcTidy.Features.AiChats.Library;
using PcTidy.Features.AiChats.Model;
using PcTidy.Features.AiChats.Privacy;
using Xunit;

namespace PcTidy.Features.AiChats.Tests;

/// <summary>Every masking rule on a made-up key, plus ordinary text that must come through untouched.</summary>
public class SecretMaskerTests
{
    private static readonly SecretMasker Masker = new(clock: new FixedClock(AiSandbox.Now));

    public static IEnumerable<object[]> Keys() => new[]
    {
        new object[] { "anthropic-api-key", FakeSecrets.Anthropic, "sk-ant-…" },
        new object[] { "openai-project-key", FakeSecrets.OpenAiProject, "sk-proj-…" },
        new object[] { "openai-legacy-key", FakeSecrets.OpenAiLegacy, "sk-…" },
        new object[] { "github-fine-grained-pat", FakeSecrets.GitHubFineGrained, "github_pat_…" },
        new object[] { "github-token", FakeSecrets.GitHubClassic, "ghp_…" },
        new object[] { "aws-access-key-id", FakeSecrets.Aws, "AKIA…" },
        new object[] { "google-api-key", FakeSecrets.Google, "AIza…" },
        new object[] { "slack-token", FakeSecrets.Slack, "xoxb-…" },
        new object[] { "slack-webhook", FakeSecrets.SlackWebhook, "https://hooks.slack.com…" },
        new object[] { "stripe-key", FakeSecrets.Stripe, "sk_live_…" },
        new object[] { "huggingface-token", FakeSecrets.HuggingFace, "hf_…" },
        new object[] { "npm-token", FakeSecrets.Npm, "npm_…" },
        new object[] { "azure-storage-key", FakeSecrets.AzureStorage, "AccountKey=…" },
        new object[] { "azure-ad-client-secret", FakeSecrets.AzureAdSecret, "…" },
        new object[] { "discord-bot-token", FakeSecrets.Discord, "…" },
        new object[] { "jwt", FakeSecrets.Jwt(AiSandbox.Now.AddDays(30)), "eyJ…" },
        new object[] { "pem-private-key", FakeSecrets.Pem, "[private key hidden by PC Tidy]" },
        new object[] { "generic-credential", FakeSecrets.Password, "password=…" },
    };

    [Theory]
    [MemberData(nameof(Keys))]
    public void Each_rule_masks_its_key_keeping_only_the_prefix_and_last_four(string ruleId, string secret, string expectedStart)
    {
        var text = $"Here you go: {secret} (keep it safe)";
        var r = Masker.Mask(text);
        var finding = Assert.Single(r.Findings);
        Assert.Equal(ruleId, finding.RuleId);
        Assert.True(finding.LooksLive, "a random-looking key should be reported as live");
        Assert.StartsWith("Here you go: ", r.Text);
        Assert.EndsWith(" (keep it safe)", r.Text);
        Assert.Contains(expectedStart, r.Text);
        Assert.Contains(finding.Masked, r.Text);
        // The secret's random body never survives, in the text or in the finding.
        var body = secret.Length > 16 ? secret[^16..^4] : secret;
        Assert.DoesNotContain(body, r.Text);
        Assert.DoesNotContain(body, finding.Masked);
        Assert.Equal(12, finding.Fingerprint.Length);
        Assert.False(string.IsNullOrWhiteSpace(finding.Advice));
    }

    [Fact]
    public void Masked_form_looks_like_sk_ant_ellipsis_last_four()
    {
        var key = FakeSecrets.Anthropic;
        Assert.Equal("sk-ant-…" + key[^4..], Masker.Mask(key).Text);
    }

    [Fact]
    public void Masking_is_idempotent()
    {
        var once = Masker.Mask("a " + FakeSecrets.Anthropic + " b " + FakeSecrets.Password + " c " + FakeSecrets.Aws).Text;
        var twice = Masker.Mask(once);
        Assert.Equal(once, twice.Text);
        Assert.Empty(twice.Findings);
    }

    [Theory]
    [InlineData("My password is in the vault, please don't ask me for it.")]
    [InlineData("Set the token = variable before calling the function.")]
    [InlineData("Use sk-learn (scikit-learn) for this classification task.")]
    [InlineData("var apiKey = getApiKey(); // read it from the environment")]
    [InlineData("password: hunter2")]
    [InlineData("The build id is 2026-01-01.4 and the commit is a3f9c2e1d0b4.")]
    [InlineData("Visit https://example.com/path?token=abc for the docs.")]
    [InlineData("secret = mySecretValue")]
    [InlineData("Install with npm install left-pad, then run npm_config_cache=foo.")]
    [InlineData("Discord invite: discord.gg/gardening. Slack channel #general.")]
    [InlineData("The Mona Lisa is in the Louvre. Ghp_ is not a prefix of anything here.")]
    [InlineData("-----BEGIN PUBLIC KEY-----\nMIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA\n-----END PUBLIC KEY-----")]
    public void Ordinary_text_is_left_alone(string text)
    {
        var r = Masker.Mask(text);
        Assert.Empty(r.Findings);
        Assert.Equal(text, r.Text);
    }

    [Fact]
    public void Documentation_examples_are_masked_but_not_reported_as_live()
    {
        var awsDocs = "AK" + "IAIOSFODNN7" + "EXAMPLE";
        var r = Masker.Mask("The docs use " + awsDocs + " as a sample.");
        var f = Assert.Single(r.Findings);
        Assert.False(f.LooksLive);
        Assert.DoesNotContain(awsDocs, r.Text);
    }

    [Fact]
    public void An_expired_login_token_is_not_live()
    {
        var f = Assert.Single(Masker.Mask("token " + FakeSecrets.Jwt(AiSandbox.Now.AddDays(-1))).Findings);
        Assert.Equal("jwt", f.RuleId);
        Assert.False(f.LooksLive);
    }

    [Fact]
    public void Same_key_twice_has_the_same_fingerprint_and_different_keys_differ()
    {
        var a = Masker.Mask(FakeSecrets.Stripe + " and again " + FakeSecrets.Stripe).Findings;
        Assert.Equal(2, a.Count);
        Assert.Equal(a[0].Fingerprint, a[1].Fingerprint);
        Assert.NotEqual(a[0].Fingerprint, Masker.Mask(FakeSecrets.Npm).Findings[0].Fingerprint);
    }

    /// <summary>A rule that backtracks catastrophically on a run of "a"s with no "!" after it.</summary>
    private static readonly SecretRule Pathological = new("slow-rule", "Slow rule", "(?<secret>(a|aa)+aa!)", 0, "Rotate it.");

    private static SecretMasker SlowMasker() => new(
        SecretRules.All.Where(r => r.Id == "aws-access-key-id").Append(Pathological), new FixedClock(AiSandbox.Now), TimeSpan.FromMilliseconds(100));

    [Fact]
    public void A_regex_timeout_never_lets_a_key_through_the_whole_text_is_hidden_and_recorded()
    {
        var text = $"my key is {FakeSecrets.Aws} and then a pasted blob: {new string('a', 4000)}";
        var r = SlowMasker().Mask(text);

        Assert.DoesNotContain(FakeSecrets.Aws, r.Text);
        Assert.Equal(SecretMasker.UncheckedText, r.Text);
        var finding = Assert.Single(r.Findings);
        Assert.Equal(SecretMasker.UncheckedRuleId, finding.RuleId);
        Assert.True(finding.LooksLive); // shown in "Exposed secrets", so the user knows a text wasn't checked
        Assert.DoesNotContain(FakeSecrets.Aws, finding.Masked + finding.Advice);
    }

    [Fact]
    public void A_text_the_slow_rule_finishes_on_is_masked_normally()
    {
        var r = SlowMasker().Mask($"key {FakeSecrets.Aws} here");
        Assert.Equal("aws-access-key-id", Assert.Single(r.Findings).RuleId);
        Assert.DoesNotContain(FakeSecrets.Aws, r.Text);
    }

    [Fact]
    public void A_chat_with_an_uncheckable_message_goes_into_the_library_hidden_never_in_clear()
    {
        var chat = new ImportedChat
        {
            Source = ChatSource.ChatGpt, SourceId = "slow", Title = "Pasted log", OriginKind = OriginKind.ExportZip, Origin = "export.zip",
            Messages = new[]
            {
                new ChatMessage(ChatRole.User, $"{FakeSecrets.Aws} {new string('a', 4000)}"),
                new ChatMessage(ChatRole.Assistant, "That looks like a log."),
            },
        };
        var masked = ChatIngest.Prepare(chat, SlowMasker());
        Assert.Equal(SecretMasker.UncheckedText, masked.Messages[0].Text);
        Assert.Equal("That looks like a log.", masked.Messages[1].Text);
        Assert.Contains(masked.Secrets, s => s.RuleId == SecretMasker.UncheckedRuleId);
    }

    [Fact]
    public void Every_catalog_rule_compiles_has_advice_and_a_unique_id()
    {
        Assert.Equal(SecretRules.All.Count, SecretRules.All.Select(r => r.Id).Distinct().Count());
        foreach (var rule in SecretRules.All)
        {
            Assert.Contains("(?<secret>", rule.Pattern);
            Assert.False(string.IsNullOrWhiteSpace(rule.Advice));
            _ = new System.Text.RegularExpressions.Regex(rule.Pattern);
        }
    }

    // ------------------------------------------------------------------ AIC-3: private keys in long texts

    private static string LongKey(int lines, int seed)
    {
        var body = string.Join("\n", Enumerable.Range(0, lines).Select(i => FakeSecrets.Body(64, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/", seed + i)));
        return "-----BEGIN RSA " + "PRIVATE KEY-----\n" + body + "\n-----END RSA " + "PRIVATE KEY-----";
    }

    /// <summary>
    /// The review's case: a long pasted log with many BEGIN lines and no END, and a real 3 KB key straddling the 64 KB
    /// piece boundary. Private keys are found by a linear scan, so the key is masked whatever the regex rules do.
    /// </summary>
    [Fact]
    public void A_private_key_across_a_64_KB_boundary_in_a_long_log_is_masked()
    {
        var key = LongKey(47, 100);
        var sb = new System.Text.StringBuilder();
        while (sb.Length < SecretMasker.ChunkLength - 1500) sb.Append("log line: -----BEGIN RSA " + "PRIVATE KEY----- seen in config\n");
        var keyStart = sb.Length;
        sb.Append(key).Append('\n');
        while (sb.Length < 300 * 1024) sb.Append("more log -----BEGIN OPENSSH " + "PRIVATE KEY-----\n");
        Assert.True(keyStart < SecretMasker.ChunkLength && keyStart + key.Length > SecretMasker.ChunkLength); // it really straddles

        var r = Masker.Mask(sb.ToString());

        var body = key.Split('\n')[20];
        Assert.DoesNotContain(body, r.Text);
        Assert.Single(r.Findings, f => f.RuleId == SecretMasker.PemRuleId);
        Assert.Contains("[private key hidden by PC Tidy]", r.Text);
        Assert.Contains("seen in config", r.Text); // lone BEGIN lines hold no key and stay readable
    }

    [Fact]
    public void A_key_cut_off_before_its_END_line_is_still_hidden()
    {
        var key = LongKey(10, 300);
        var cut = key[..key.IndexOf("-----END", StringComparison.Ordinal)] + "\nThat's all I could paste.";
        var r = Masker.Mask("Here: " + cut);
        Assert.DoesNotContain(key.Split('\n')[5], r.Text);
        Assert.Equal(SecretMasker.PemRuleId, Assert.Single(r.Findings).RuleId);
        Assert.EndsWith("That's all I could paste.", r.Text);

        // A certificate or public key is not a secret.
        var cert = LongKey(4, 400).Replace("RSA " + "PRIVATE KEY", "CERTIFICATE");
        Assert.Empty(Masker.Mask(cert).Findings);
    }

    [Fact]
    public void The_chunk_overlap_is_longer_than_any_regex_secret()
    {
        Assert.True(SecretMasker.ChunkOverlap >= 8 * 1024);
        Assert.True(SecretMasker.ChunkOverlap > SecretMasker.LongestRegexSecret);
        // Every regex rule except the private-key one (scanned separately) has bounded repeats only.
        foreach (var rule in SecretRules.All.Where(r => r.Id != SecretMasker.PemRuleId))
        {
            Assert.DoesNotMatch(@"\{\d+,\}", rule.Pattern);
            Assert.False(HasUnboundedRepeat(rule.Pattern), rule.Id);
        }
    }

    /// <summary>A "+" or "*" outside a character class that isn't escaped: a repeat with no upper limit.</summary>
    private static bool HasUnboundedRepeat(string pattern)
    {
        bool inClass = false;
        for (int i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\') { i++; continue; }
            if (inClass) { if (c == ']') inClass = false; continue; }
            if (c == '[') { inClass = true; continue; }
            if (c is '+' or '*') return true;
        }
        return false;
    }
}
