// -----------------------------------------------------------------------------------------------------------------
//  Secret-detection rules for the AI chats section (masking + "Exposed secrets" report).
//
//  The regular expressions below are adapted from gitleaks' default rule set (config/gitleaks.toml,
//  https://github.com/gitleaks/gitleaks). gitleaks is distributed under the MIT License:
//
//    MIT License
//    Copyright (c) 2019 Zachary Rice
//
//    Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
//    documentation files (the "Software"), to deal in the Software without restriction, including without limitation
//    the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
//    to permit persons to whom the Software is furnished to do so, subject to the following conditions:
//
//    The above copyright notice and this permission notice shall be included in all copies or substantial portions
//    of the Software.
//
//    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
//    THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
//    CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
//    DEALINGS IN THE SOFTWARE.
//
//  Changes from gitleaks: rules simplified for chat text (no file-path or keyword pre-filters), lengths loosened where
//  providers changed formats, and each rule carries the prefix PC Tidy keeps visible plus plain-English rotation advice.
//  TruffleHog (AGPL) was NOT used.
// -----------------------------------------------------------------------------------------------------------------

namespace PcTidy.Catalog.AiChats;

/// <summary>
/// One kind of secret. <see cref="Pattern"/> must capture the secret itself in the group named "secret"; text around it
/// (like "password=") stays visible. <see cref="KeepPrefix"/> is how much of the secret's start stays visible in the
/// masked form ("sk-ant-…9f2a"): a fixed provider prefix, never part of the random body.
/// </summary>
public sealed record SecretRule(
    string Id,
    string Title,
    string Pattern,
    int KeepPrefix,
    string Advice,
    bool NeedsEntropyCheck = false,
    bool KeepTail = true);

/// <summary>The rule list, most specific first (the first rule that matches a span wins).</summary>
public static class SecretRules
{
    private const string Rotate = "Rotate it: create a new one and revoke this one";

    public static readonly IReadOnlyList<SecretRule> All = new SecretRule[]
    {
        new("pem-private-key", "Private key (PEM)",
            @"(?<secret>-----BEGIN[ A-Z0-9_-]{0,100}PRIVATE KEY(?: BLOCK)?-----[\s\S]{32,}?-----END[ A-Z0-9_-]{0,100}PRIVATE KEY(?: BLOCK)?-----)",
            0, "Anyone with this private key can sign in as you or decrypt your data. Replace the key pair and remove the old public key from every server or service that trusts it.",
            KeepTail: false),
        new("anthropic-api-key", "Anthropic API key",
            @"(?<secret>\bsk-ant-(?:api|admin|sid|oat)\d{2}-[A-Za-z0-9_\-]{20,120})",
            7, Rotate + " in the Anthropic Console (Settings → API keys)."),
        new("openai-project-key", "OpenAI API key",
            @"(?<secret>\bsk-(?:proj|svcacct|admin)-[A-Za-z0-9_\-]{20,200})",
            8, Rotate + " on the OpenAI platform (API keys page)."),
        new("openai-legacy-key", "OpenAI API key",
            @"(?<secret>\bsk-[A-Za-z0-9]{20}T3BlbkFJ[A-Za-z0-9]{20}\b)",
            3, Rotate + " on the OpenAI platform (API keys page)."),
        new("github-fine-grained-pat", "GitHub token",
            @"(?<secret>\bgithub_pat_[A-Za-z0-9_]{22,255})",
            11, Rotate + " in GitHub (Settings → Developer settings → Personal access tokens)."),
        new("github-token", "GitHub token",
            @"(?<secret>\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36}\b)",
            4, Rotate + " in GitHub (Settings → Developer settings → Personal access tokens)."),
        new("aws-access-key-id", "AWS access key",
            @"(?<secret>\b(?:A3T[A-Z0-9]|AKIA|ASIA|ABIA|ACCA)[A-Z2-7]{16}\b)",
            4, Rotate + " in the AWS console (IAM → Users → Security credentials), and check CloudTrail for use you don't recognise."),
        new("google-api-key", "Google API key",
            @"(?<secret>\bAIza[A-Za-z0-9_\-]{35}(?![A-Za-z0-9_\-]))",
            4, Rotate + " in Google Cloud Console (APIs & Services → Credentials)."),
        new("slack-token", "Slack token",
            @"(?<secret>\bxox[abposr]-(?:\d{1,20}-)?[A-Za-z0-9\-]{10,250})",
            5, Rotate + " in your Slack app's settings (OAuth & Permissions)."),
        new("slack-webhook", "Slack webhook",
            @"(?<secret>https://hooks\.slack\.com/(?:services|workflows|triggers)/[A-Za-z0-9+/]{20,100})",
            23, "Anyone with this link can post into your Slack. Delete the webhook in the Slack app settings and create a new one."),
        new("stripe-key", "Stripe key",
            @"(?<secret>\b(?:sk|rk)_(?:live|test|prod)_[A-Za-z0-9]{10,99}\b)",
            8, Rotate + " in the Stripe Dashboard (Developers → API keys)."),
        new("huggingface-token", "Hugging Face token",
            @"(?<secret>\bhf_[A-Za-z0-9]{34,40}\b)",
            3, Rotate + " in Hugging Face (Settings → Access Tokens)."),
        new("npm-token", "npm token",
            @"(?<secret>\bnpm_[A-Za-z0-9]{36}\b)",
            4, Rotate + " on npmjs.com (Access Tokens)."),
        new("azure-storage-key", "Azure storage key",
            @"AccountKey=(?<secret>[A-Za-z0-9+/]{86}==)",
            0, "Rotate this storage account key in the Azure portal (Storage account → Access keys)."),
        new("azure-ad-client-secret", "Azure app secret",
            @"(?<secret>(?<![A-Za-z0-9_~.\-])[A-Za-z0-9_~.]{3}\dQ~[A-Za-z0-9_~.\-]{31,34}(?![A-Za-z0-9_~.\-]))",
            0, Rotate + " in the Azure portal (App registrations → Certificates & secrets)."),
        new("discord-bot-token", "Discord bot token",
            @"(?<secret>\b[MNO][A-Za-z0-9_\-]{23,25}\.[A-Za-z0-9_\-]{6}\.[A-Za-z0-9_\-]{27,38}\b)",
            0, "Reset this bot's token in the Discord Developer Portal (Applications → Bot → Reset Token)."),
        new("jwt", "Login token (JWT)",
            // Capped so the longest match stays below the masker's chunk overlap (AIC-3); real tokens are far shorter.
            @"(?<secret>\beyJ[A-Za-z0-9_\-]{17,4096}\.eyJ[A-Za-z0-9_\-]{17,12288}\.[A-Za-z0-9_\-]{10,1024}={0,2})",
            3, "This looks like a login/session token. If it's still valid, sign out of that service everywhere (or revoke the session) so it stops working."),
        new("generic-credential", "Password or secret",
            @"(?i)\b(?:password|passwd|pwd|secret|client[_\-]?secret|token|api[_\-]?key|apikey|access[_\-]?key|auth[_\-]?token)\b[""']?\s{0,3}(?:=|:|=>)\s{0,3}[""']?(?<secret>[^\s""'<>,;`]{8,200})",
            0, "Change this password or secret wherever it's used, then delete it from the chat if you can.",
            NeedsEntropyCheck: true, KeepTail: false),
    };
}
