using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Transport.Ssh;
using Tessera.Core;
using Tessera.Services;
using Xunit;
using F = Tessera.NativeTests.UiRegressionFixture;

namespace Tessera.NativeTests;

public sealed class SecurityWorkflowRegressionTests
{
    [AvaloniaTheory]
    [InlineData("Reject", HostKeyDecision.Reject)]
    [InlineData("Trust once", HostKeyDecision.TrustOnce)]
    [InlineData("Trust and save", HostKeyDecision.TrustAndSave)]
    public async Task NativeHostReviewReturnsOnlyTheExplicitDecisionAndPreservesTheEditor(string button, HostKeyDecision expected)
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        w.ShowProfiles(); await F.PumpAsync();
        var editor = w.FindControl<ContentControl>("OverlayContent")!.Content;
        var menu = NativeMenu.GetMenu(w);
        var challenge = new HostKeyChallenge("example.invalid", 2222, "operator", "ssh-ed25519", "SHA256:test-fingerprint", SshKnownHostTrustStatus.Unknown);
        var pending = w.Shell.Profiles.KnownHosts.Prompt!(challenge, F.Token);
        await F.UntilAsync(() => w.OwnedWindows.Count() == 1, "Host review must open."); await F.PumpAsync();
        var dialog = w.OwnedWindows.Single()!;
        var identity = F.Named<TextBox>(dialog, "Presented host key fingerprint");
        Assert.True(identity.IsReadOnly); Assert.Contains("example.invalid:2222", identity.Text);
        Assert.Contains(challenge.Fingerprint, identity.Text); Assert.False(pending.IsCompleted);
        F.Click(F.Button(dialog, button)); Assert.Equal(expected, await pending); await F.PumpAsync();
        Assert.Empty(w.OwnedWindows); Assert.Same(editor, w.FindControl<ContentControl>("OverlayContent")!.Content);
        Assert.Same(menu, NativeMenu.GetMenu(w));
    }

    [AvaloniaFact]
    public async Task KeyboardInteractiveSubmitHonorsEchoAndClearsAllResponseFields()
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        var pending = w.Shell.Profiles.ChallengePrompt!(new SshChallenge("example.invalid", "operator", "Complete authentication",
            [new SshPrompt("Account", true), new SshPrompt("One-time code", false)], 2), F.Token);
        await F.UntilAsync(() => w.OwnedWindows.Any(), "Challenge opens."); await F.PumpAsync();
        var dialog = w.OwnedWindows.Single()!;
        var account = F.Named<TextBox>(dialog, "Account"); var code = F.Named<TextBox>(dialog, "One-time code");
        Assert.Equal('\0', account.PasswordChar); Assert.NotEqual('\0', code.PasswordChar);
        account.Text = "operator"; code.Text = "123456";
        F.Click(F.Button(dialog, "Continue")); Assert.Equal(new[] { "operator", "123456" }, await pending);
        Assert.True(string.IsNullOrEmpty(account.Text)); Assert.True(string.IsNullOrEmpty(code.Text));
        Assert.False(File.Exists(Path.Combine(f.DirectoryPath, "profiles.json")));
    }

    [AvaloniaFact]
    public async Task EscapeCancelsMfaAndReleasesSecretControlText()
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        var pending = w.Shell.Profiles.ChallengePrompt!(new SshChallenge("example.invalid", "operator", "MFA",
            [new SshPrompt("One-time code", false)], 1), F.Token);
        await F.UntilAsync(() => w.OwnedWindows.Any(), "Challenge opens."); await F.PumpAsync();
        var dialog = w.OwnedWindows.Single()!; var secret = F.Named<TextBox>(dialog, "One-time code"); secret.Text = "654321";
        F.Press(dialog, Key.Escape); Assert.Null(await pending); Assert.True(string.IsNullOrEmpty(secret.Text));
        Assert.Empty(w.OwnedWindows);
    }

    [AvaloniaFact]
    public async Task SecurityPromptsAreSerializedAndOwnerCloseCancelsQueuedPrompts()
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        var first = w.Shell.Profiles.CredentialPrompt!("First secret", F.Token);
        await F.UntilAsync(() => w.OwnedWindows.Any(), "First prompt opens.");
        var second = w.Shell.Profiles.CredentialPrompt!("Second secret", F.Token);
        await F.PumpAsync(); Assert.Single(w.OwnedWindows); Assert.False(second.IsCompleted);
        var dialog = w.OwnedWindows.Single()!; var field = F.Named<TextBox>(dialog, "Credential"); field.Text = "not-to-retain";
        w.CloseForTests(); await F.PumpAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await second);
        try { Assert.Null(await first); } catch (OperationCanceledException) { }
        Assert.True(string.IsNullOrEmpty(field.Text));
    }

    [AvaloniaFact]
    public async Task RecoveryPassphraseFormRejectsShortAndMismatchedValuesBeforeAccepting()
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        var pending = w.Shell.Recovery.Keys.PassphrasePrompt!(true, F.Token);
        await F.UntilAsync(() => w.OwnedWindows.Any(), "Recovery passphrase form opens."); await F.PumpAsync();
        var dialog = w.OwnedWindows.Single()!;
        var password = F.Named<TextBox>(dialog, "Recovery passphrase"); var confirm = F.Named<TextBox>(dialog, "Confirm passphrase");
        password.Text = confirm.Text = "short"; F.Click(F.Button(dialog, "Protect recordings")); Assert.False(pending.IsCompleted);
        password.Text = "fixture-long-secret"; confirm.Text = "different-long-secret";
        F.Click(F.Button(dialog, "Protect recordings")); Assert.False(pending.IsCompleted);
        confirm.Text = password.Text; F.Click(F.Button(dialog, "Protect recordings")); Assert.Equal("fixture-long-secret", await pending);
        Assert.True(string.IsNullOrEmpty(password.Text)); Assert.True(string.IsNullOrEmpty(confirm.Text));
    }

    [AvaloniaFact]
    public async Task InputUnlockRequiresConfirmationAndBroadcastExcludesLockedSessions()
    {
        await using var f = await F.OpenAsync(); var w = f.Window; var session = w.Shell.ActiveSession!;
        session.Locked = true;
        var cancel = w.Commands["unlock"].ExecuteAsync(); await F.PumpAsync();
        F.Click(F.Button(f.Overlay, "Cancel")); await cancel; Assert.True(session.Locked);
        var accept = w.Commands["unlock"].ExecuteAsync(); await F.PumpAsync();
        F.Click(F.Button(f.Overlay, "Unlock input")); await accept; Assert.False(session.Locked);
        await w.Commands["unlock"].ExecuteAsync(); Assert.True(session.Locked);
        await w.Commands["broadcast"].ExecuteAsync(); await F.PumpAsync();
        // Explicit design sessions are not real running transports; all must be unavailable.
        Assert.All(f.Overlay.GetVisualDescendants().OfType<CheckBox>(), c => Assert.False(c.IsEnabled));
        Assert.False(w.Shell.BroadcastEnabled); await f.DismissAsync();
    }

    [AvaloniaFact]
    public async Task AdvancedProfileEditorValidatesImmutableIdentityAndWritesRealProfileData()
    {
        await using var f = await F.OpenAsync(); var w = f.Window; var id = w.Shell.ActiveDocument!.ProfileId;
        w.ShowProfiles(id); await F.PumpAsync(); F.Click(F.Button(f.Overlay, "Advanced profile document"));
        await F.UntilAsync(() => f.Overlay.GetVisualDescendants().OfType<TextBox>().Any(t => t.Text?.Contains("transport") == true), "Advanced editor opens."); await F.PumpAsync();
        var editor = F.Named<TextBox>(f.Overlay, "Native RoyalTerminal profile JSON");
        var original = w.Shell.Profiles.Get(id);
        editor.Text = JsonSerializer.Serialize(original with { Id = "changed-id" }, WorkspaceStore.Json);
        F.Click(F.Button(f.Overlay, "Validate and save document")); await F.PumpAsync();
        Assert.Contains(f.Overlay.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("immutable") == true);
        Assert.Equal(original.DisplayName, w.Shell.Profiles.Get(id).DisplayName);
        editor.Text = JsonSerializer.Serialize(original with { DisplayName = "Updated via native UI" }, WorkspaceStore.Json);
        F.Click(F.Button(f.Overlay, "Validate and save document"));
        await F.UntilAsync(() => w.Shell.Profiles.Get(id).DisplayName == "Updated via native UI", "Valid profile saves.");
        Assert.Contains("Updated via native UI", await File.ReadAllTextAsync(Path.Combine(f.DirectoryPath, "profiles.json"), F.Token));
    }

    [AvaloniaFact]
    public async Task SftpCommandsWithoutAConnectionReportErrorsAndWindowReusesItsInstance()
    {
        await using var f = await F.OpenAsync(); var w = f.Window;
        await w.Commands["sftp"].ExecuteAsync(); await F.PumpAsync(); var dialog = w.OwnedWindows.Single()!;
        await w.Commands["sftp"].ExecuteAsync(); Assert.Same(dialog, Assert.Single(w.OwnedWindows));
        var editor = F.Named<TextBox>(dialog, "Remote UTF-8 file editor"); Assert.True(editor.IsReadOnly);
        var readOnly = dialog.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "Read-only")); Assert.True(readOnly.IsChecked);
        F.Click(F.Button(dialog, "Upload")); await F.PumpAsync();
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Connect an SSH profile first") == true);
        Assert.Empty(f.Files.Requests); Assert.True(editor.IsReadOnly);
        dialog.Close(); await F.UntilAsync(() => !w.OwnedWindows.Any(), "Disconnected SFTP window closes cleanly.");
    }
}
