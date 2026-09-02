using ClipsToDiscord;

internal static class RoutingLocalOnlyShellTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 2, 17, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await AssertSuccessfulDurableShellChangesAsync(Path.Combine(root, "success"));
        await AssertRapidTrayThenHotkeyPreservesFinalIntentAsync(
            Path.Combine(root, "serialized-toggle"));
        await AssertNativeConflictPreservesBothStatesAsync(Path.Combine(root, "conflict"));
        await AssertCorruptStateFailsSafeAsync(Path.Combine(root, "corrupt"));
        AssertExactModeNotifications();
    }

    private static async Task AssertSuccessfulDurableShellChangesAsync(string root)
    {
        Directory.CreateDirectory(root);
        var timestamp = Now;
        var path = Path.Combine(root, RoutingLocalOnlyOverrideStore.FileName);
        var state = new RoutingLocalOnlyOverrideState(
            new RoutingLocalOnlyOverrideStore(path),
            () => timestamp);
        _ = await state.EnsureMigratedAsync(
            legacyUploadToDiscord: true,
            GlobalHotkeyBinding.DefaultDisplayText);
        var registrar = new FakeGlobalHotkeyRegistrar();
        using var hotkeys = new GlobalHotkeyManager(registrar);
        Assert(hotkeys.TrySetBinding(GlobalHotkeyBinding.Default, out var initialError) &&
               initialError == 0,
            "The shell fixture must begin with the migrated native shortcut registered.");
        var changes = new List<(RoutingLocalOnlyOverrideInspection Inspection, bool Notify)>();
        var source = new RoutingLocalOnlyModeViewSource(
            state,
            hotkeys,
            (inspection, notify) => changes.Add((inspection, notify)));

        timestamp = timestamp.AddMinutes(1);
        var enabled = await source.SetEnabledAsync(true);
        timestamp = timestamp.AddMinutes(1);
        var rebound = await source.SetHotkeyAsync("Control + Shift + F8");
        timestamp = timestamp.AddMinutes(1);
        var dismissed = await source.DismissFirstRunNoticeAsync();
        timestamp = timestamp.AddMinutes(1);
        var disabled = await source.SetEnabledAsync(false);
        var noOp = await source.SetEnabledAsync(false);

        Assert(enabled is
               {
                   Succeeded: true,
                   Snapshot: { IsAvailable: true, EffectiveEnabled: true }
               } &&
               rebound is
               {
                   Succeeded: true,
                   Snapshot.HotkeyDisplayText: "Ctrl + Shift + F8"
               } &&
               dismissed is
               {
                   Succeeded: true,
                   Snapshot.FirstRunNoticeDismissed: true
               } &&
               disabled is
               {
                   Succeeded: true,
                   Snapshot: { IsAvailable: true, EffectiveEnabled: false }
               } &&
               noOp.Succeeded &&
               hotkeys.RegisteredBinding is { DisplayText: "Ctrl + Shift + F8" },
            "Successful shell changes must update the durable view and native shortcut together.");
        Assert(changes.Select(change => change.Notify)
                   .SequenceEqual([true, false, false, true, false]) &&
               changes[0].Inspection.Document is { Revision: 2, Enabled: true } &&
               changes[1].Inspection.Document is
                   { Revision: 3, HotkeyBinding: "Ctrl + Shift + F8" } &&
               changes[2].Inspection.Document is
                   { Revision: 4, FirstRunNoticeDismissed: true } &&
               changes[3].Inspection.Document is { Revision: 5, Enabled: false } &&
               changes[4].Inspection.Document is { Revision: 5 },
            "Only real ON/OFF transitions may request a tray notification; hotkey, notice, and no-op updates must stay quiet.");
        var restarted = new RoutingLocalOnlyOverrideState(
            new RoutingLocalOnlyOverrideStore(path)).Inspect();
        Assert(restarted.Document == changes[3].Inspection.Document &&
               restarted is
               {
                   EffectiveEnabled: false,
                   EffectiveHotkeyBinding: "Ctrl + Shift + F8"
               },
            "Shell changes must survive a new Routing state instance exactly.");
    }

    private static async Task AssertRapidTrayThenHotkeyPreservesFinalIntentAsync(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, RoutingLocalOnlyOverrideStore.FileName);
        var state = new RoutingLocalOnlyOverrideState(
            new RoutingLocalOnlyOverrideStore(path),
            () => Now);
        _ = await state.EnsureMigratedAsync(
            legacyUploadToDiscord: false,
            GlobalHotkeyBinding.DefaultDisplayText);
        var firstMutationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstMutation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationCalls = 0;
        var transitions = new List<(bool Enabled, bool Notify)>();
        using var hotkeys = new GlobalHotkeyManager(new FakeGlobalHotkeyRegistrar());
        var source = new RoutingLocalOnlyModeViewSource(
            state,
            hotkeys,
            (inspection, notify) => transitions.Add((inspection.EffectiveEnabled, notify)),
            beforeEnabledMutation: async (enabled, cancellationToken) =>
            {
                var call = Interlocked.Increment(ref mutationCalls);
                if (call != 1) return;
                Assert(!enabled,
                    "The deterministic race fixture must hold the tray's OFF mutation first.");
                firstMutationEntered.TrySetResult();
                await releaseFirstMutation.Task.WaitAsync(cancellationToken);
            });

        var trayOff = source.SetEnabledAsync(false);
        await firstMutationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var hotkeyToggle = source.ToggleEnabledAsync();
        Assert(!hotkeyToggle.IsCompleted && Volatile.Read(ref mutationCalls) == 1,
            "A hotkey toggle arriving during a tray save must wait before reading the durable mode.");
        releaseFirstMutation.TrySetResult();
        var results = await Task.WhenAll(trayOff, hotkeyToggle);

        Assert(results.All(result => result.Succeeded) &&
               transitions.SequenceEqual([(false, true), (true, true)]) &&
               state.Inspect().EffectiveEnabled,
            "Serialized tray OFF then hotkey toggle must durably end ON and notify both transitions in user-intent order.");
    }

    private static async Task AssertNativeConflictPreservesBothStatesAsync(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, RoutingLocalOnlyOverrideStore.FileName);
        var state = new RoutingLocalOnlyOverrideState(
            new RoutingLocalOnlyOverrideStore(path),
            () => Now);
        var migrated = await state.EnsureMigratedAsync(
            legacyUploadToDiscord: true,
            GlobalHotkeyBinding.DefaultDisplayText);
        var registrar = new FakeGlobalHotkeyRegistrar();
        using var hotkeys = new GlobalHotkeyManager(registrar);
        Assert(hotkeys.TrySetBinding(GlobalHotkeyBinding.Default, out _),
            "The hotkey-conflict fixture must register the previous shortcut.");
        var changeCalls = 0;
        var source = new RoutingLocalOnlyModeViewSource(
            state,
            hotkeys,
            (_, _) => changeCalls++);
        registrar.RegisterResults.Enqueue(false);
        registrar.RegisterResults.Enqueue(true);

        var result = await source.SetHotkeyAsync("Ctrl + Shift + U");

        Assert(result is
               {
                   Succeeded: false,
                   HotkeyConflict: true,
                   Error: "Ctrl + Shift + U is already in use by another app. ClipCord kept Ctrl + Alt + L."
               } &&
               result.Snapshot.HotkeyDisplayText == GlobalHotkeyBinding.DefaultDisplayText &&
               hotkeys.RegisteredBinding == GlobalHotkeyBinding.Default &&
               state.Inspect().Document == migrated.Document &&
               registrar.UnregisterCount == 1 &&
               registrar.RegisterCalls.Select(binding => binding.DisplayText)
                   .SequenceEqual([
                       GlobalHotkeyBinding.DefaultDisplayText,
                       "Ctrl + Shift + U",
                       GlobalHotkeyBinding.DefaultDisplayText
                   ]) &&
               changeCalls == 0,
            "A native shortcut conflict must restore the old registration, preserve the old durable revision, and emit no state-change notification.");
    }

    private static async Task AssertCorruptStateFailsSafeAsync(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, RoutingLocalOnlyOverrideStore.FileName);
        await File.WriteAllTextAsync(path, "{ invalid local-only state");
        var originalBytes = await File.ReadAllBytesAsync(path);
        var state = new RoutingLocalOnlyOverrideState(
            new RoutingLocalOnlyOverrideStore(path),
            () => Now);
        var registrar = new FakeGlobalHotkeyRegistrar();
        using var hotkeys = new GlobalHotkeyManager(registrar);
        Assert(hotkeys.TrySetBinding(GlobalHotkeyBinding.Default, out _),
            "The corrupt-state fixture must begin with its old native shortcut registered.");
        var changeCalls = 0;
        var source = new RoutingLocalOnlyModeViewSource(
            state,
            hotkeys,
            (_, _) => changeCalls++);

        var view = source.Inspect();
        var toggle = await source.SetEnabledAsync(false);
        var hotkey = await source.SetHotkeyAsync("Ctrl + Shift + F7");

        Assert(view is
               {
                   IsAvailable: false,
                   EffectiveEnabled: true,
                   HotkeyDisplayText: GlobalHotkeyBinding.DefaultDisplayText,
                   FirstRunNoticeDismissed: false,
                   StatusDetail: "Saved Local-only mode needs attention. External delivery remains paused."
               } &&
               toggle is
               {
                   Succeeded: false,
                   Snapshot: { IsAvailable: false, EffectiveEnabled: true },
                   Error: "ClipCord could not save Local-only mode. Existing routing is unchanged."
               } &&
               hotkey is
               {
                   Succeeded: false,
                   Snapshot: { IsAvailable: false, EffectiveEnabled: true },
                   Error: "ClipCord kept the previous Local-only mode shortcut."
               } &&
               hotkeys.RegisteredBinding == GlobalHotkeyBinding.Default &&
               changeCalls == 0 &&
               (await File.ReadAllBytesAsync(path)).SequenceEqual(originalBytes),
            "Corrupt Routing state must remain fail-safe Local-only, preserve its evidence, restore the previous native shortcut, and emit no success notification.");
    }

    private static void AssertExactModeNotifications()
    {
        Assert(ModeFeedbackPresentation.ForRoutingLocalOnlyMode(enabled: true) ==
               new ModeFeedbackPresentation(
                   "Local-only mode on",
                   "Future clips will stay on this PC. Existing deliveries are unchanged.",
                   ModeFeedbackTone.LocalOnlyEnabled) &&
               ModeFeedbackPresentation.ForRoutingLocalOnlyMode(enabled: false) ==
               new ModeFeedbackPresentation(
                   "Normal routing restored",
                   "Future clips will follow your active Routes.",
                   ModeFeedbackTone.UploadsEnabled),
            "The ON/OFF overlay copy and tone must exactly describe future-only routing behavior.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
