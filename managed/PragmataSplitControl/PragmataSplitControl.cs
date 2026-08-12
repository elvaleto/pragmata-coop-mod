using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

public static class PragmataSplitControl
{
    private enum PlayerOneInputMode
    {
        KeyboardMouse,
        NativeDualSense,
        XInput
    }

    private const string LogPrefix = "[PragmataSplitControl]";
    private const uint ErrorSuccess = 0;
    private const byte TriggerThreshold = 30;

    private const ushort DPadUp = 0x0001;
    private const ushort DPadDown = 0x0002;
    private const ushort DPadLeft = 0x0004;
    private const ushort DPadRight = 0x0008;
    private const ushort StartButton = 0x0010;
    private const ushort BackButton = 0x0020;
    private const ushort LeftThumb = 0x0040;
    private const ushort RightThumb = 0x0080;
    private const ushort LeftShoulder = 0x0100;
    private const ushort RightShoulder = 0x0200;
    private const ushort ButtonA = 0x1000;
    private const ushort ButtonB = 0x2000;
    private const ushort ButtonX = 0x4000;
    private const ushort ButtonY = 0x8000;

    private static PlayerOneInputMode playerOneInputMode = PlayerOneInputMode.XInput;
    private static int configuredPlayerOneSlot;
    private static int configuredPlayerTwoSlot = 1;
    private static string configurationPath = string.Empty;
    private static uint connectedMask = uint.MaxValue;
    private static int playerTwoSlot = -1;
    private static ulong playerOneGamePadDeviceAddress;
    private static XInputState playerTwoState;
    private static XInputState previousPlayerTwoState;
    private static XInputState playerOneState;
    private static XInputState previousPlayerOneState;
    private static bool playerOneConnected;
    private static bool loggedMergedDeviceSanitizer;
    private static uint puzzleRightHash;
    private static uint puzzleLeftHash;
    private static uint puzzleUpHash;
    private static uint puzzleDownHash;
    private static bool nativeInputFilterActive;
    private static uint nativeInputFilterFrame;
    private static long nativeBlockedCalls;
    private static ulong injectedPuzzleCommands;
    private static ulong suppressedHughPuzzleCommands;
    private static bool debugOverlayEnabled;

    [ThreadStatic] private static uint pendingTriggerCommand;
    [ThreadStatic] private static uint pendingDownCommand;
    [ThreadStatic] private static uint pendingReleaseCommand;

    [PluginEntryPoint]
    public static void Main()
    {
        LoadConfiguration();

        puzzleRightHash = app.hid.PlayerInputCommand.PuzzleRightHash;
        puzzleLeftHash = app.hid.PlayerInputCommand.PuzzleLeftHash;
        puzzleUpHash = app.hid.PlayerInputCommand.PuzzleUpHash;
        puzzleDownHash = app.hid.PlayerInputCommand.PuzzleDownHash;
        InitializeNativeInputFilter();

        API.LogInfo($"{LogPrefix} Loaded. P2 uses a direct physical XInput slot; no virtual controller is involved.");
        API.LogInfo($"{LogPrefix} Config: P1={playerOneInputMode}{(playerOneInputMode == PlayerOneInputMode.XInput ? $" slot {configuredPlayerOneSlot}" : string.Empty)}, P2=XInput slot {configuredPlayerTwoSlot}.");
        API.LogInfo($"{LogPrefix} Compatibility mode: the mod never starts, targets, refreshes, or finishes a puzzle.");
        API.LogInfo($"{LogPrefix} When the game opens any hacking window, P2 Y/X/A/B inject its native up/left/down/right commands.");
        API.LogInfo($"{LogPrefix} Debug overlay: {(debugOverlayEnabled ? "enabled" : "disabled")}.");
        API.LogInfo($"{LogPrefix} Puzzle command hashes: up=0x{puzzleUpHash:X8}, left=0x{puzzleLeftHash:X8}, down=0x{puzzleDownHash:X8}, right=0x{puzzleRightHash:X8}.");
    }

    [PluginExitPoint]
    public static void OnUnload()
    {
        ShutdownNativeInputFilter();
        connectedMask = uint.MaxValue;
        playerTwoSlot = -1;
        playerOneGamePadDeviceAddress = 0;
        playerTwoState = default;
        previousPlayerTwoState = default;
        playerOneState = default;
        previousPlayerOneState = default;
        playerOneConnected = false;
        loggedMergedDeviceSanitizer = false;
        nativeInputFilterActive = false;
        nativeInputFilterFrame = 0;
        nativeBlockedCalls = 0;
        injectedPuzzleCommands = 0;
        suppressedHughPuzzleCommands = 0;
        API.LogInfo($"{LogPrefix} Unloaded.");
    }

    [Callback(typeof(UpdateBehavior), CallbackType.Pre)]
    public static void OnPreUpdate()
    {
        if (nativeInputFilterActive && (++nativeInputFilterFrame % 120u) == 0)
        {
            try
            {
                nativeInputFilterActive = PragmataInputFilterEnsureInstalled() > 0;
                if (debugOverlayEnabled)
                    nativeBlockedCalls = PragmataInputFilterGetBlockedCalls();
            }
            catch (Exception exception)
            {
                nativeInputFilterActive = false;
                API.LogError($"{LogPrefix} Native input filter stopped responding: {exception.Message}");
            }
        }

        PollPhysicalControllers();
        SanitizeMergedGamePadForPlayerOne();
    }

    [Callback(typeof(ImGuiRender), CallbackType.Post)]
    public static void OnImGuiRender()
    {
        if (!debugOverlayEnabled)
            return;

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoFocusOnAppearing;

        ImGui.SetNextWindowPos(new Vector2(24.0f, 96.0f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.62f);

        if (!ImGui.Begin("PRAGMATA Split Control##PragmataSplitControlHud", flags))
        {
            ImGui.End();
            return;
        }

        if (playerTwoSlot < 0)
        {
            ImGui.Text("P2 PUZZLE INPUT: connect the configured XInput controller");
        }
        else
        {
            ImGui.Text($"P2 PUZZLE INPUT READY [slot {playerTwoSlot}]");
            ImGui.Text("Y/X/A/B: up/left/down/right in any game-opened puzzle");
            ImGui.Text("Hugh puzzle buttons blocked; jump and other controls preserved");
        }

        if (playerOneInputMode == PlayerOneInputMode.NativeDualSense)
            ImGui.Text("HUGH INPUT: native DualSense (adaptive triggers preserved)");

        if (nativeInputFilterActive)
            ImGui.Text($"INPUT SPLIT: native filter active (blocked {nativeBlockedCalls})");
        else
            ImGui.Text("INPUT SPLIT: fallback only");

        ImGui.Text($"P2 INPUT: Y/X/A/B {FormatFaceButtons()}  injected {injectedPuzzleCommands}");
        ImGui.Text($"HUGH PUZZLE INPUT SUPPRESSED: {suppressedHughPuzzleCommands}");

        ImGui.End();
    }

    [MethodHook(typeof(app.PlayerInputDriver), nameof(app.PlayerInputDriver.isTrigger), MethodHookType.Pre)]
    public static PreHookResult OnIsTriggerPre(Span<ulong> args)
    {
        pendingTriggerCommand = args.Length > 2 ? (uint)args[2] : 0;
        return PreHookResult.Continue;
    }

    [MethodHook(typeof(app.PlayerInputDriver), nameof(app.PlayerInputDriver.isTrigger), MethodHookType.Post)]
    public static void OnIsTriggerPost(ref ulong retval)
    {
        OverridePuzzleCommandResult(
            pendingTriggerCommand,
            ShouldInjectTrigger(pendingTriggerCommand),
            ref retval);

        pendingTriggerCommand = 0;
    }

    [MethodHook(typeof(app.PlayerInputDriver), nameof(app.PlayerInputDriver.isDown), MethodHookType.Pre)]
    public static PreHookResult OnIsDownPre(Span<ulong> args)
    {
        pendingDownCommand = args.Length > 2 ? (uint)args[2] : 0;
        return PreHookResult.Continue;
    }

    [MethodHook(typeof(app.PlayerInputDriver), nameof(app.PlayerInputDriver.isDown), MethodHookType.Post)]
    public static void OnIsDownPost(ref ulong retval)
    {
        OverridePuzzleCommandResult(
            pendingDownCommand,
            ShouldInjectDown(pendingDownCommand),
            ref retval);

        pendingDownCommand = 0;
    }

    [MethodHook(typeof(app.PlayerInputDriver), nameof(app.PlayerInputDriver.isRelease), MethodHookType.Pre)]
    public static PreHookResult OnIsReleasePre(Span<ulong> args)
    {
        pendingReleaseCommand = args.Length > 2 ? (uint)args[2] : 0;
        return PreHookResult.Continue;
    }

    [MethodHook(typeof(app.PlayerInputDriver), nameof(app.PlayerInputDriver.isRelease), MethodHookType.Post)]
    public static void OnIsReleasePost(ref ulong retval)
    {
        OverridePuzzleCommandResult(
            pendingReleaseCommand,
            ShouldInjectRelease(pendingReleaseCommand),
            ref retval);

        pendingReleaseCommand = 0;
    }

    [MethodHook(typeof(app.PlayerInputDriver), nameof(app.PlayerInputDriver.getActiveGamePadDevice), MethodHookType.Post)]
    public static void OnGetActiveGamePadDevicePost(ref ulong retval)
    {
        // When an additional controller is connected, keep normal movement,
        // aiming, menus and weapons bound to the first RE Engine gamepad.
        // P2 is read separately through XInput and is injected only below.
        if (!nativeInputFilterActive && playerTwoSlot >= 0 && playerOneGamePadDeviceAddress != 0)
            retval = playerOneGamePadDeviceAddress;
    }

    [MethodHook(typeof(app.PlayerInputDriver), nameof(app.PlayerInputDriver.updateCommand), MethodHookType.Pre)]
    public static PreHookResult OnUpdateCommandPre(Span<ulong> args)
    {
        SanitizeMergedGamePadForPlayerOne();
        return PreHookResult.Continue;
    }

    [MethodHook(typeof(app.PlayerCommandUpdater), nameof(app.PlayerCommandUpdater.update), MethodHookType.Pre)]
    public static PreHookResult OnPlayerCommandUpdaterPre(Span<ulong> args)
    {
        SanitizeMergedGamePadForPlayerOne();
        return PreHookResult.Continue;
    }

    private static void PollPhysicalControllers()
    {
        uint newMask = 0;
        int newPlayerTwoSlot = -1;
        XInputState newPlayerTwoState = default;
        XInputState newPlayerOneState = default;
        bool newPlayerOneConnected = playerOneInputMode != PlayerOneInputMode.XInput;

        for (uint slot = 0; slot < 4; slot++)
        {
            if (XInputGetState(slot, out var state) != ErrorSuccess)
                continue;

            newMask |= 1u << (int)slot;

            if (playerOneInputMode == PlayerOneInputMode.XInput && (int)slot == configuredPlayerOneSlot)
            {
                newPlayerOneConnected = true;
                newPlayerOneState = state;
            }

            if ((int)slot == configuredPlayerTwoSlot)
            {
                newPlayerTwoSlot = (int)slot;
                newPlayerTwoState = state;
            }
        }

        if (newPlayerOneConnected)
        {
            if (playerOneConnected)
                previousPlayerOneState = playerOneState;
            else
                previousPlayerOneState = newPlayerOneState;

            playerOneState = newPlayerOneState;
            playerOneConnected = true;
        }
        else
        {
            playerOneState = default;
            previousPlayerOneState = default;
            playerOneConnected = false;
        }

        if (newMask != connectedMask)
        {
            connectedMask = newMask;
            API.LogInfo($"{LogPrefix} Physical XInput connected mask: 0b{Convert.ToString(connectedMask, 2).PadLeft(4, '0')}");
            RefreshPlayerOneGamePadDevice();
        }

        if (newPlayerTwoSlot != playerTwoSlot)
        {
            playerTwoSlot = newPlayerTwoSlot;
            previousPlayerTwoState = default;
            playerTwoState = newPlayerTwoState;
            injectedPuzzleCommands = 0;

            if (playerTwoSlot >= 0)
                API.LogInfo($"{LogPrefix} Player 2 assigned to physical XInput slot {playerTwoSlot}.");
            else
                API.LogInfo($"{LogPrefix} Configured Player 2 XInput slot {configuredPlayerTwoSlot} is not connected.");

            return;
        }

        previousPlayerTwoState = playerTwoState;
        playerTwoState = newPlayerTwoState;
    }

    private static void RefreshPlayerOneGamePadDevice()
    {
        try
        {
            playerOneGamePadDeviceAddress = 0;
            uint deviceCount = via.hid.GamePad.getConnectingDevicesCount();
            var replacementDevice = playerOneInputMode == PlayerOneInputMode.KeyboardMouse
                ? via.hid.GamePad.NullDevice
                : via.hid.GamePad.Device;

            playerOneGamePadDeviceAddress = GetAddress(replacementDevice);
            string source = playerOneInputMode switch
            {
                PlayerOneInputMode.KeyboardMouse => "keyboard/mouse (null gamepad)",
                PlayerOneInputMode.NativeDualSense => "the game's native DualSense device",
                _ => $"physical XInput slot {configuredPlayerOneSlot}"
            };
            API.LogInfo($"{LogPrefix} P1 input locked to {source}; RE Engine device=0x{playerOneGamePadDeviceAddress:X} ({deviceCount} engine devices connected).");
        }
        catch (Exception exception)
        {
            playerOneGamePadDeviceAddress = 0;
            API.LogWarning($"{LogPrefix} Could not lock the primary RE Engine gamepad: {exception.Message}");
        }
    }

    private static void SanitizeMergedGamePadForPlayerOne()
    {
        if (nativeInputFilterActive)
            return;

        // The fallback sanitizer can reconstruct an XInput pad, but it must not
        // overwrite the game's native DualSense device or its adaptive-trigger
        // output. NativeDualSense therefore requires the native XInput filter.
        if (playerOneInputMode == PlayerOneInputMode.NativeDualSense)
            return;

        if (playerTwoSlot < 0)
        {
            return;
        }

        if (!playerOneConnected)
            return;

        try
        {
            ApplyXInputState(via.hid.GamePad.Device, playerOneState, previousPlayerOneState);
            ApplyXInputState(via.hid.GamePad.MergedDevice, playerOneState, previousPlayerOneState);

            if (playerOneGamePadDeviceAddress != 0)
            {
                var connectedDevice = ManagedObject.ToManagedObject(playerOneGamePadDeviceAddress)?.As<via.hid.GamePadDevice>();
                ApplyXInputState(connectedDevice, playerOneState, previousPlayerOneState);
            }

            if (!loggedMergedDeviceSanitizer)
            {
                loggedMergedDeviceSanitizer = true;
                string source = playerOneInputMode == PlayerOneInputMode.KeyboardMouse
                    ? "no gamepad input (P1 keyboard/mouse)"
                    : $"physical XInput slot {configuredPlayerOneSlot} only";
                API.LogInfo($"{LogPrefix} RE Engine merged gamepad is now sanitized to {source}.");
            }
        }
        catch (Exception exception)
        {
            API.LogWarning($"{LogPrefix} Could not sanitize the merged gamepad: {exception.Message}");
        }
    }

    private static void ApplyXInputState(via.hid.GamePadDevice? device, XInputState current, XInputState previous)
    {
        if (device == null)
            return;

        via.hid.GamePadButton currentButtons = ConvertButtons(current.Gamepad);
        via.hid.GamePadButton previousButtons = ConvertButtons(previous.Gamepad);

        device.Button = currentButtons;
        device.ButtonDown = currentButtons & ~previousButtons;
        device.ButtonUp = previousButtons & ~currentButtons;
        device.ButtonRepeat = via.hid.GamePadButton.None;
        device.AnalogL = current.Gamepad.LeftTrigger / 255.0f;
        device.AnalogR = current.Gamepad.RightTrigger / 255.0f;

        SetAxis(device, left: true, current.Gamepad.ThumbLX, current.Gamepad.ThumbLY, 7849.0f / 32767.0f);
        SetAxis(device, left: false, current.Gamepad.ThumbRX, current.Gamepad.ThumbRY, 8689.0f / 32767.0f);
    }

    private static via.hid.GamePadButton ConvertButtons(XInputGamepad gamepad)
    {
        via.hid.GamePadButton result = via.hid.GamePadButton.None;

        if ((gamepad.Buttons & DPadUp) != 0) result |= via.hid.GamePadButton.LUp;
        if ((gamepad.Buttons & DPadDown) != 0) result |= via.hid.GamePadButton.LDown;
        if ((gamepad.Buttons & DPadLeft) != 0) result |= via.hid.GamePadButton.LLeft;
        if ((gamepad.Buttons & DPadRight) != 0) result |= via.hid.GamePadButton.LRight;
        if ((gamepad.Buttons & ButtonY) != 0) result |= via.hid.GamePadButton.RUp;
        if ((gamepad.Buttons & ButtonA) != 0) result |= via.hid.GamePadButton.RDown;
        if ((gamepad.Buttons & ButtonX) != 0) result |= via.hid.GamePadButton.RLeft;
        if ((gamepad.Buttons & ButtonB) != 0) result |= via.hid.GamePadButton.RRight;
        if ((gamepad.Buttons & LeftShoulder) != 0) result |= via.hid.GamePadButton.LTrigTop;
        if ((gamepad.Buttons & RightShoulder) != 0) result |= via.hid.GamePadButton.RTrigTop;
        if (gamepad.LeftTrigger >= TriggerThreshold) result |= via.hid.GamePadButton.LTrigBottom;
        if (gamepad.RightTrigger >= TriggerThreshold) result |= via.hid.GamePadButton.RTrigBottom;
        if ((gamepad.Buttons & LeftThumb) != 0) result |= via.hid.GamePadButton.LStickPush;
        if ((gamepad.Buttons & RightThumb) != 0) result |= via.hid.GamePadButton.RStickPush;
        if ((gamepad.Buttons & BackButton) != 0) result |= via.hid.GamePadButton.CLeft;
        if ((gamepad.Buttons & StartButton) != 0) result |= via.hid.GamePadButton.CRight;

        return result;
    }

    private static void SetAxis(via.hid.GamePadDevice device, bool left, short rawX, short rawY, float deadZone)
    {
        float x = NormalizeStickAxis(rawX);
        float y = NormalizeStickAxis(rawY);
        var rawAxis = left ? device.RawAxisL : device.RawAxisR;
        rawAxis.x = x;
        rawAxis.y = y;

        float magnitude = MathF.Sqrt((x * x) + (y * y));
        float processedX = 0.0f;
        float processedY = 0.0f;
        if (magnitude > deadZone)
        {
            float scaledMagnitude = MathF.Min(1.0f, (magnitude - deadZone) / (1.0f - deadZone));
            processedX = (x / magnitude) * scaledMagnitude;
            processedY = (y / magnitude) * scaledMagnitude;
        }

        var processedAxis = left ? device.AxisL : device.AxisR;
        processedAxis.x = processedX;
        processedAxis.y = processedY;

        if (left)
        {
            device.RawAxisL = rawAxis;
            device.AxisL = processedAxis;
        }
        else
        {
            device.RawAxisR = rawAxis;
            device.AxisR = processedAxis;
        }
    }

    private static float NormalizeStickAxis(short value)
    {
        return value >= 0 ? value / 32767.0f : value / 32768.0f;
    }

    private static void InitializeNativeInputFilter()
    {
        try
        {
            // Mode 0 hides XInput from the game but leaves keyboard, mouse and the
            // native HID DualSense backend untouched. Mode 1 exposes one XInput slot.
            int mode = playerOneInputMode == PlayerOneInputMode.XInput ? 1 : 0;
            int patchedImports = PragmataInputFilterInitialize(mode, configuredPlayerOneSlot, configuredPlayerTwoSlot);
            nativeInputFilterActive = patchedImports > 0;
            nativeInputFilterFrame = 0;
            nativeBlockedCalls = 0;

            if (nativeInputFilterActive)
            {
                string source = playerOneInputMode switch
                {
                    PlayerOneInputMode.KeyboardMouse => "keyboard/mouse only (all XInput hidden from Hugh)",
                    PlayerOneInputMode.NativeDualSense => "native DualSense/HID (all XInput hidden from Hugh)",
                    _ => $"XInput slot {configuredPlayerOneSlot} only"
                };
                API.LogInfo($"{LogPrefix} Native XInput filter active: {patchedImports} PRAGMATA imports patched; Hugh={source}.");
            }
            else
            {
                API.LogError($"{LogPrefix} Native XInput filter found no compatible PRAGMATA imports; using the RE Engine fallback sanitizer.");
            }
        }
        catch (Exception exception)
        {
            nativeInputFilterActive = false;
            API.LogError($"{LogPrefix} Native XInput filter could not be loaded: {exception.Message}");
        }
    }

    private static void ShutdownNativeInputFilter()
    {
        if (!nativeInputFilterActive)
            return;

        try
        {
            PragmataInputFilterShutdown();
        }
        catch (Exception exception)
        {
            API.LogWarning($"{LogPrefix} Native input filter shutdown failed: {exception.Message}");
        }
    }

#if false
    // Retired experimental independent-target implementation. It is excluded from
    // the build so compatibility mode cannot touch HackingManager or PuzzleUnit.
    private static void HandleTargetSelection()
    {
        if (playerTwoSlot < 0 || hackingManager == null)
            return;

        bool cyclePrevious = ButtonPressed(LeftShoulder) || ButtonPressed(DPadLeft);
        bool cycleNext = ButtonPressed(RightShoulder) || ButtonPressed(DPadRight);
        bool toggleHacking = LeftTriggerPressed();

        if (!cyclePrevious && !cycleNext && !toggleHacking)
            return;

        playerTwoStatus = toggleHacking ? "LT received" : "target-cycle input received";

        if (toggleHacking && playerTwoHackingActive)
        {
            StopPlayerTwoHacking();
            return;
        }

        if (playerTwoHackingActive)
            return;

        if (!TryRefreshPlayerHandle())
        {
            playerTwoStatus = "Hugh is not ready";
            return;
        }

        RebuildCandidateTargets();

        if (CandidateTargets.Count == 0)
        {
            selectedTargetAddress = 0;
            selectedTargetIndex = -1;
            playerHandle = null;
            playerTwoStatus = "no valid enemy in range/view";
            if (!warnedNoTargets)
            {
                warnedNoTargets = true;
                API.LogInfo($"{LogPrefix} P2 found no enemy inside the game's current hacking distance and camera view.");
            }
            return;
        }

        warnedNoTargets = false;

        int existingIndex = CandidateTargets.IndexOf(selectedTargetAddress);
        if (existingIndex < 0)
        {
            ulong gameTarget = GetAddress(hackingManager.DefaultHackingTarget);
            existingIndex = CandidateTargets.IndexOf(gameTarget);
        }

        if (cyclePrevious)
            selectedTargetIndex = existingIndex >= 0 ? WrapIndex(existingIndex - 1, CandidateTargets.Count) : CandidateTargets.Count - 1;
        else if (cycleNext)
            selectedTargetIndex = existingIndex >= 0 ? WrapIndex(existingIndex + 1, CandidateTargets.Count) : 0;
        else
            selectedTargetIndex = existingIndex >= 0 ? existingIndex : 0;

        selectedTargetAddress = CandidateTargets[selectedTargetIndex];
        ApplySelectedTarget(requestRefresh: false);
        playerTwoStatus = $"selected target {selectedTargetIndex + 1}/{CandidateTargets.Count}";
        API.LogInfo($"{LogPrefix} P2 selected valid target {selectedTargetIndex + 1}/{CandidateTargets.Count}: 0x{selectedTargetAddress:X} (range {currentHackingDistance:F1}, FOV {currentHackingFov:F1}).");

        if (toggleHacking)
            StartPlayerTwoHacking();
    }

    private static bool TryRefreshPlayerHandle()
    {
        try
        {
            characterManager ??= API.GetManagedSingletonT<app.CharacterManager>();
            playerHandle = characterManager?.getPlayerHandle();
            if (playerHandle != null)
                return true;

            API.LogInfo($"{LogPrefix} P2 input ignored because the playable Hugh instance is not ready yet.");
        }
        catch (Exception exception)
        {
            playerHandle = null;
            API.LogWarning($"{LogPrefix} Could not acquire the playable Hugh instance: {exception.Message}");
        }

        return false;
    }

    private static void RebuildCandidateTargets()
    {
        CandidateTargets.Clear();
        currentHackingDistance = 0.0f;
        currentHackingFov = 0.0f;

        if (hackingManager == null)
            return;

        string scanPhase = "get puzzle drivers";
        try
        {
            var drivers = hackingManager.getSortedPuzzleDrivers();
            if (drivers == null || drivers.Count == 0)
                return;

            var potentialTargets = new List<app.PuzzleUnit>();
            for (int i = 0; i < drivers.Count; i++)
            {
                var driver = drivers[i];
                if (driver == null || !driver.Valid || !driver.IsEnemy)
                    continue;

                var unit = driver.getUnit();
                if (unit == null || !unit.Valid || !unit.Enabled || !unit.IsPlayablePuzzle || !unit.IsEnemy || !unit.EnableLockOn)
                    continue;

                potentialTargets.Add(unit);
            }

            // Avoid querying the player-dependent hacking parameters outside combat.
            if (potentialTargets.Count == 0)
                return;

            scanPhase = "read live hacking range";
            var targetType = app.PuzzleUnit.Type.Enemy;
            var checkParam = app.HackingManager.getHackingCheckParam(targetType, true);
            currentHackingDistance = checkParam.Distance;
            currentHackingFov = checkParam.Fov;

            scanPhase = "read hacking origin";
            var ownerPosition = hackingManager.getHackingStartPosition();

            for (int i = 0; i < potentialTargets.Count; i++)
            {
                var unit = potentialTargets[i];
                scanPhase = $"read target {i + 1} position";
                var targetPosition = unit.Position;

                // Keep the game's live, upgrade-aware range. Reading the optional
                // per-unit override through this REFramework build returns an
                // unboxed ValueType instead of a float, so it is deliberately not
                // touched here.
                float targetDistance = currentHackingDistance;

                scanPhase = $"measure target {i + 1} distance";
                float dx = targetPosition.x - ownerPosition.x;
                float dy = targetPosition.y - ownerPosition.y;
                float dz = targetPosition.z - ownerPosition.z;
                float distanceSquared = dx * dx + dy * dy + dz * dz;
                if (targetDistance <= 0.0f || distanceSquared > targetDistance * targetDistance)
                    continue;

                // CameraSystem performs the engine's normal on-screen test without
                // the unsafe Frustum/ref-parameter ABI used by canPuzzle.
                scanPhase = $"check target {i + 1} camera view";
                if (cameraSystem == null || !cameraSystem.isInsideView(targetPosition))
                    continue;

                ulong address = GetAddress(unit);
                if (address != 0 && !CandidateTargets.Contains(address))
                    CandidateTargets.Add(address);
            }
        }
        catch (Exception exception)
        {
            CandidateTargets.Clear();
            playerTwoStatus = $"target scan failed at {scanPhase}";
            API.LogWarning($"{LogPrefix} Target scan failed safely at '{scanPhase}': {exception.Message}");
        }
    }

    private static void EnforcePlayerTwoTarget()
    {
        if (!playerTwoHackingActive || selectedTargetAddress == 0)
            return;

        ApplySelectedTarget(requestRefresh: false);
    }

    private static void StartPlayerTwoHacking()
    {
        if (playerHandle == null || selectedTargetAddress == 0)
            return;

        try
        {
            var target = ManagedObject.ToManagedObject(selectedTargetAddress)?.As<app.PuzzleUnit>();
            if (target == null || !target.Valid || !target.Enabled || !target.IsPlayablePuzzle)
                return;

            ApplySelectedTarget(requestRefresh: false);
            playerHandle.requestOverrideStartPuzzle(target);
            playerTwoHackingActive = true;
            playerTwoHackingGraceFrames = 120;
            playerTwoActiveTargetAddress = selectedTargetAddress;
            playerTwoStatus = "puzzle start requested";
            API.LogInfo($"{LogPrefix} P2 directly requested puzzle start for 0x{selectedTargetAddress:X}; Hugh aim is not required.");
        }
        catch (Exception exception)
        {
            playerTwoHackingActive = false;
            playerTwoHackingGraceFrames = 0;
            playerTwoActiveTargetAddress = 0;
            playerTwoStatus = "puzzle start failed";
            API.LogWarning($"{LogPrefix} Could not start the P2 puzzle directly: {exception.Message}");
        }
    }

    private static void StopPlayerTwoHacking()
    {
        try
        {
            var target = ManagedObject.ToManagedObject(playerTwoActiveTargetAddress)?.As<app.PuzzleUnit>();
            if (playerHandle != null && target != null && target.Valid)
                playerHandle.requestOverrideFinishPuzzle(target);
        }
        catch (Exception exception)
        {
            API.LogWarning($"{LogPrefix} Could not cancel the P2 puzzle cleanly: {exception.Message}");
        }
        finally
        {
            playerTwoHackingActive = false;
            playerTwoHackingGraceFrames = 0;
            playerTwoActiveTargetAddress = 0;
            API.LogInfo($"{LogPrefix} P2 puzzle mode cancelled.");
        }
    }

    private static void UpdatePlayerTwoHackingState()
    {
        if (!playerTwoHackingActive)
            return;

        if (playerTwoHackingGraceFrames > 0)
        {
            playerTwoHackingGraceFrames--;
            return;
        }

        var target = ManagedObject.ToManagedObject(playerTwoActiveTargetAddress)?.As<app.PuzzleUnit>();
        if (target == null || !target.Valid || !target.Enabled || !target.IsPlayablePuzzle)
        {
            playerTwoHackingActive = false;
            playerTwoActiveTargetAddress = 0;
            API.LogInfo($"{LogPrefix} P2 puzzle target is no longer valid.");
        }
    }

    private static void ApplySelectedTarget(bool requestRefresh)
    {
        if (hackingManager == null || selectedTargetAddress == 0)
            return;

        try
        {
            var target = ManagedObject.ToManagedObject(selectedTargetAddress)?.As<app.PuzzleUnit>();
            if (target == null || !target.Valid || !target.Enabled || !target.IsPlayablePuzzle)
            {
                selectedTargetAddress = 0;
                selectedTargetIndex = -1;
                return;
            }

            hackingManager._DefaultHackingTarget = target;
            hackingManager.IsTargetedEnemy = target.IsEnemy;

            if (requestRefresh)
                hackingManager.requestRefreshTarget();
        }
        catch (Exception exception)
        {
            API.LogWarning($"{LogPrefix} Selected target became invalid: {exception.Message}");
            selectedTargetAddress = 0;
            selectedTargetIndex = -1;
        }
    }

    private static void PollHackingTargets()
    {
        if (hackingManager == null)
            return;

        var defaultTarget = hackingManager.DefaultHackingTarget;
        var lastTarget = hackingManager.LastHackingTarget;
        var defaultAddress = GetAddress(defaultTarget);
        var lastAddress = GetAddress(lastTarget);

        if (defaultAddress == lastDefaultTargetAddress && lastAddress == lastHackingTargetAddress)
            return;

        lastDefaultTargetAddress = defaultAddress;
        lastHackingTargetAddress = lastAddress;
        API.LogInfo($"{LogPrefix} Targets changed: default=0x{defaultAddress:X}, last=0x{lastAddress:X}");
    }

#endif

    private static void OverridePuzzleCommandResult(uint command, bool playerTwoResult, ref ulong retval)
    {
        // These hashes are queried for puzzle navigation. Hugh's jump and all
        // ordinary gameplay commands use different hashes and are untouched.
        // If P2 disconnects, retain the game's original result to avoid a softlock.
        if (playerTwoSlot < 0 || !TryGetPuzzleButton(command, out ushort ignoredButton))
            return;

        if (debugOverlayEnabled && retval != 0)
            suppressedHughPuzzleCommands++;

        retval = playerTwoResult ? 1ul : 0ul;
        if (debugOverlayEnabled && playerTwoResult)
            injectedPuzzleCommands++;
    }

    private static bool ShouldInjectTrigger(uint command)
    {
        return playerTwoSlot >= 0 &&
               TryGetPuzzleButton(command, out ushort button) &&
               ButtonPressed(button);
    }

    private static bool ShouldInjectDown(uint command)
    {
        return playerTwoSlot >= 0 &&
               TryGetPuzzleButton(command, out ushort button) &&
               ButtonDown(button);
    }

    private static bool ShouldInjectRelease(uint command)
    {
        return playerTwoSlot >= 0 &&
               TryGetPuzzleButton(command, out ushort button) &&
               ButtonReleased(button);
    }

    private static bool TryGetPuzzleButton(uint command, out ushort button)
    {
        if (command == puzzleUpHash)
            button = ButtonY;
        else if (command == puzzleLeftHash)
            button = ButtonX;
        else if (command == puzzleDownHash)
            button = ButtonA;
        else if (command == puzzleRightHash)
            button = ButtonB;
        else
        {
            button = 0;
            return false;
        }

        return true;
    }

    private static bool ButtonPressed(ushort mask)
    {
        return (playerTwoState.Gamepad.Buttons & mask) != 0 &&
               (previousPlayerTwoState.Gamepad.Buttons & mask) == 0;
    }

    private static bool ButtonDown(ushort mask)
    {
        return (playerTwoState.Gamepad.Buttons & mask) != 0;
    }

    private static bool ButtonReleased(ushort mask)
    {
        return (playerTwoState.Gamepad.Buttons & mask) == 0 &&
               (previousPlayerTwoState.Gamepad.Buttons & mask) != 0;
    }

    private static string FormatFaceButtons()
    {
        string y = ButtonDown(ButtonY) ? "Y" : "-";
        string x = ButtonDown(ButtonX) ? "X" : "-";
        string a = ButtonDown(ButtonA) ? "A" : "-";
        string b = ButtonDown(ButtonB) ? "B" : "-";
        return $"{y}{x}{a}{b}";
    }

    private static void LoadConfiguration()
    {
        configurationPath = FindConfigurationPath();
        if (string.IsNullOrEmpty(configurationPath) || !File.Exists(configurationPath))
        {
            API.LogWarning($"{LogPrefix} Configuration file was not found; defaults are P1=XInput slot 0 and P2=XInput slot 1.");
            return;
        }

        try
        {
            foreach (string rawLine in File.ReadAllLines(configurationPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("["))
                    continue;

                int separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                string key = line[..separator].Trim();
                string value = line[(separator + 1)..].Trim();

                if (key.Equals("Player1Mode", StringComparison.OrdinalIgnoreCase))
                {
                    playerOneInputMode = value.Equals("KeyboardMouse", StringComparison.OrdinalIgnoreCase)
                        ? PlayerOneInputMode.KeyboardMouse
                        : value.Equals("NativeDualSense", StringComparison.OrdinalIgnoreCase)
                            ? PlayerOneInputMode.NativeDualSense
                            : PlayerOneInputMode.XInput;
                }
                else if (key.Equals("Player1XInputSlot", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int playerOneSlot))
                {
                    configuredPlayerOneSlot = Math.Clamp(playerOneSlot, 0, 3);
                }
                else if (key.Equals("Player2XInputSlot", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int playerTwoSlotFromConfig))
                {
                    configuredPlayerTwoSlot = Math.Clamp(playerTwoSlotFromConfig, 0, 3);
                }
                else if (key.Equals("DebugOverlay", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool parsedDebugOverlay))
                {
                    debugOverlayEnabled = parsedDebugOverlay;
                }
            }

            if (playerOneInputMode == PlayerOneInputMode.XInput && configuredPlayerOneSlot == configuredPlayerTwoSlot)
            {
                API.LogError($"{LogPrefix} Invalid configuration: P1 and P2 both use XInput slot {configuredPlayerOneSlot}. P2 input is disabled until the config is fixed.");
                configuredPlayerTwoSlot = -1;
            }

            API.LogInfo($"{LogPrefix} Loaded configuration: {configurationPath}");
        }
        catch (Exception exception)
        {
            API.LogError($"{LogPrefix} Failed to read configuration '{configurationPath}': {exception.Message}");
        }
    }

    private static string FindConfigurationPath()
    {
        string workingDirectoryCandidate = Path.Combine(Environment.CurrentDirectory, "reframework", "data", "PragmataSplitControl.ini");
        if (File.Exists(workingDirectoryCandidate))
            return workingDirectoryCandidate;

        try
        {
            string pluginDirectory = API.GetPluginDirectory(Assembly.GetExecutingAssembly());
            var directory = new DirectoryInfo(pluginDirectory);

            for (int depth = 0; directory != null && depth < 5; depth++, directory = directory.Parent)
            {
                if (directory.Name.Equals("reframework", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(directory.FullName, "data", "PragmataSplitControl.ini");
            }
        }
        catch
        {
            // Fall through to the game-root layout used by normal installs.
        }

        return Path.Combine(AppContext.BaseDirectory, "reframework", "data", "PragmataSplitControl.ini");
    }

    private static int WrapIndex(int value, int count)
    {
        int result = value % count;
        return result < 0 ? result + count : result;
    }

    private static ulong GetAddress(object? value)
    {
        return value is IProxyable proxy ? proxy.GetAddress() : 0;
    }

    [DllImport("XINPUT9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);

    [DllImport("PragmataSplitControl_InputFilter.dll", EntryPoint = "PragmataInputFilter_Initialize", CallingConvention = CallingConvention.Cdecl)]
    private static extern int PragmataInputFilterInitialize(int playerOneMode, int playerOneSlot, int playerTwoSlot);

    [DllImport("PragmataSplitControl_InputFilter.dll", EntryPoint = "PragmataInputFilter_EnsureInstalled", CallingConvention = CallingConvention.Cdecl)]
    private static extern int PragmataInputFilterEnsureInstalled();

    [DllImport("PragmataSplitControl_InputFilter.dll", EntryPoint = "PragmataInputFilter_Shutdown", CallingConvention = CallingConvention.Cdecl)]
    private static extern void PragmataInputFilterShutdown();

    [DllImport("PragmataSplitControl_InputFilter.dll", EntryPoint = "PragmataInputFilter_GetBlockedCalls", CallingConvention = CallingConvention.Cdecl)]
    private static extern long PragmataInputFilterGetBlockedCalls();

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }
}
